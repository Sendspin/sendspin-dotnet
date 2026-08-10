using Sendspin.SDK.Connection.Framing;
using Sendspin.SDK.Connection.Noise;

namespace Sendspin.SDK.Tests.Connection;

/// <summary>
/// Send-side fragmentation, asserted against sizes worked out by hand from the spec rather
/// than by running the algorithm under test.
/// </summary>
/// <remarks>
/// <para>
/// This matters because the obvious way to test a fragmenter — round-trip it through the test
/// server — cannot catch a systematic error. <c>TestNoiseServer.EncryptFragmented</c> and
/// <c>DecryptAndReassemble</c> implement the same chunking rules as
/// <c>NoiseWireFraming.EncryptOutbound</c>, so an off-by-one in the header length or the
/// ceiling would be applied identically on both sides and the round-trip would still pass
/// (#90). Every expected number below is derived from the constants, not observed from a run.
/// </para>
/// <para>
/// The arithmetic, from <c>NoiseConstants.MaxTransportPlaintext</c> = 65535 − 16 = 65519 (a
/// full frame is exactly the 65535-byte Noise ceiling once the 16-byte AEAD tag is added):
/// a message at or under 65519 goes out whole. Above that it is split, and the original type
/// byte moves out of the payload and into the first fragment's header — so the first fragment
/// carries a 2-byte header <c>[2][orig_type]</c> and every later one a 1-byte header, leaving
/// 65517 and 65518 payload bytes respectively. The final fragment's header byte is 3.
/// </para>
/// </remarks>
public class FragmentationBoundaryTests
{
    private const int MaxPlaintext = 65535 - 16;   // 65519
    private const int AeadTag = 16;
    private const int FirstFragmentPayload = MaxPlaintext - 2;   // 65517
    private const int LaterFragmentPayload = MaxPlaintext - 1;   // 65518

    [Theory]
    [InlineData(MaxPlaintext - 2)]  // 65517
    [InlineData(MaxPlaintext - 1)]  // 65518
    [InlineData(MaxPlaintext)]      // 65519 — exactly the ceiling, still one frame
    public void AtOrBelowTheCeiling_IsSentWhole(int length)
    {
        var frames = Encode(length);

        var frame = Assert.Single(frames);
        Assert.Equal(length + AeadTag, frame.Payload.Length);
    }

    [Fact]
    public void OneByteOverTheCeiling_SplitsIntoTwo_WithTheSecondCarryingTwoBytes()
    {
        // 65520 plaintext = 1 type byte + 65519 body. The first fragment spends 2 header
        // bytes and can therefore carry 65517 of that body; the remaining 2 bytes go in a
        // second fragment behind a 1-byte header. This is the case where an off-by-one in
        // the header accounting is most visible: get it wrong and the tail is 1 or 3 bytes.
        var frames = Encode(MaxPlaintext + 1);

        Assert.Equal(2, frames.Count);
        Assert.Equal(MaxPlaintext + AeadTag, frames[0].Payload.Length);   // 65535, a full frame
        Assert.Equal(3 + AeadTag, frames[1].Payload.Length);              // [3] + 2 bytes + tag
    }

    [Fact]
    public void AMultiFragmentMessage_MatchesTheHandComputedSplit()
    {
        // 200_000 plaintext = 1 type byte + 199_999 body.
        //   fragment 1: 2-byte header, 65_517 body  (running total 65_517)
        //   fragment 2: 1-byte header, 65_518 body  (131_035)
        //   fragment 3: 1-byte header, 65_518 body  (196_553)
        //   fragment 4: 1-byte header,  3_446 body  (199_999 — exactly the body)
        const int length = 200_000;
        Assert.Equal(
            length - 1,
            FirstFragmentPayload + LaterFragmentPayload + LaterFragmentPayload + 3_446);

        var frames = Encode(length);

        Assert.Equal(4, frames.Count);
        Assert.Equal(MaxPlaintext + AeadTag, frames[0].Payload.Length);
        Assert.Equal(MaxPlaintext + AeadTag, frames[1].Payload.Length);
        Assert.Equal(MaxPlaintext + AeadTag, frames[2].Payload.Length);
        Assert.Equal(1 + 3_446 + AeadTag, frames[3].Payload.Length);
    }

    [Fact]
    public void FragmentHeaders_CarryTheOriginalTypeOnce_AndEndOnTheLastFragment()
    {
        const byte origType = 12;   // a source audio chunk
        var identity = SendspinIdentity.Generate();
        var framing = new NoiseWireFraming(identity);
        var server = CompleteHandshake(framing, identity);

        var payload = new byte[MaxPlaintext + 1];
        payload[0] = origType;
        var frames = framing.EncodeBinary(payload).ToList();

        Assert.Equal(2, frames.Count);

        // First fragment: [MessageTypeFragmentMore][orig_type], then body. The original type
        // must appear exactly once, in the header — not repeated in the body, and not left
        // in the payload where a reassembler would double-count it.
        byte[] first = server.DecryptFrame(frames[0].Payload.ToArray());
        Assert.Equal(NoiseConstants.MessageTypeFragmentMore, first[0]);
        Assert.Equal(origType, first[1]);
        Assert.Equal(MaxPlaintext, first.Length);

        // Last fragment: [MessageTypeFragmentEnd], then body. No repeated orig_type.
        byte[] last = server.DecryptFrame(frames[1].Payload.ToArray());
        Assert.Equal(NoiseConstants.MessageTypeFragmentEnd, last[0]);
        Assert.Equal(3, last.Length);
    }

    [Fact]
    public void ASingleFrameMessage_KeepsItsTypeByteInThePayload()
    {
        // The positive control for the header assertions above: below the ceiling there is no
        // fragment header at all, and the type byte stays where it started. A fragmenter that
        // wrapped everything would pass the size theory but fail here.
        const byte origType = 12;
        var identity = SendspinIdentity.Generate();
        var framing = new NoiseWireFraming(identity);
        var server = CompleteHandshake(framing, identity);

        var frames = framing.EncodeBinary(new byte[] { origType, 0xAA, 0xBB }).ToList();

        byte[] only = server.DecryptFrame(Assert.Single(frames).Payload.ToArray());
        Assert.Equal(new byte[] { origType, 0xAA, 0xBB }, only);
    }

    private static List<WireFrame> Encode(int length)
    {
        var identity = SendspinIdentity.Generate();
        var framing = new NoiseWireFraming(identity);
        CompleteHandshake(framing, identity);

        var payload = new byte[length];
        payload[0] = 12;   // a plausible binary type byte; EncodeBinary requires one
        return framing.EncodeBinary(payload).ToList();
    }

    private static TestNoiseServer CompleteHandshake(NoiseWireFraming framing, SendspinIdentity identity)
    {
        var server = new TestNoiseServer(identity.PublicKey, NoiseConstants.SentinelPsk.ToArray());
        var clientInit = Assert.Single(framing.Start());
        var (serverInit, msg1) = server.Respond(clientInit.PayloadAsText());
        Assert.Null(framing.ProcessInbound(WireFrame.FromText(serverInit)).FatalReason);
        var result = framing.ProcessInbound(WireFrame.FromText(msg1));
        Assert.Null(result.FatalReason);
        server.CompleteHandshake(Assert.Single(result.Replies!).PayloadAsText());
        return server;
    }
}
