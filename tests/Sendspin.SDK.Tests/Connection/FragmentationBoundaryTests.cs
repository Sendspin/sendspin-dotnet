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
/// carries a 3-byte header <c>[1][flags][orig_type]</c> and every later one a 2-byte header
/// <c>[1][flags]</c>, leaving 65516 and 65517 payload bytes respectively.
/// </para>
/// </remarks>
public class FragmentationBoundaryTests
{
    private const int MaxPlaintext = 65535 - 16;   // 65519
    private const int AeadTag = 16;
    private const int FirstFragmentPayload = MaxPlaintext - 3;   // 65516
    private const int LaterFragmentPayload = MaxPlaintext - 2;   // 65517

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
        // 65520 plaintext = 1 type byte + 65519 body. The first fragment spends 3 header
        // bytes and can therefore carry 65516 of that body; the remaining 3 bytes go in a
        // second fragment behind a 2-byte header. This is the case where an off-by-one in
        // the header accounting is most visible: get it wrong and the tail moves.
        var frames = Encode(MaxPlaintext + 1);

        Assert.Equal(2, frames.Count);
        Assert.Equal(MaxPlaintext + AeadTag, frames[0].Payload.Length);   // 65535, a full frame
        Assert.Equal(5 + AeadTag, frames[1].Payload.Length);              // [1][flags] + 3 bytes + tag
    }

    [Fact]
    public void AMultiFragmentMessage_MatchesTheHandComputedSplit()
    {
        // 200_000 plaintext = 1 type byte + 199_999 body.
        //   fragment 1: 3-byte header, 65_516 body  (running total 65_516)
        //   fragment 2: 2-byte header, 65_517 body  (131_033)
        //   fragment 3: 2-byte header, 65_517 body  (196_550)
        //   fragment 4: 2-byte header,  3_449 body  (199_999 — exactly the body)
        const int length = 200_000;
        Assert.Equal(
            length - 1,
            FirstFragmentPayload + LaterFragmentPayload + LaterFragmentPayload + 3_449);

        var frames = Encode(length);

        Assert.Equal(4, frames.Count);
        Assert.Equal(MaxPlaintext + AeadTag, frames[0].Payload.Length);
        Assert.Equal(MaxPlaintext + AeadTag, frames[1].Payload.Length);
        Assert.Equal(MaxPlaintext + AeadTag, frames[2].Payload.Length);
        Assert.Equal(2 + 3_449 + AeadTag, frames[3].Payload.Length);
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

        // First fragment: [1][flags][orig_type], then body. The original type must appear
        // exactly once, in the header — not repeated in the body, and not left in the payload
        // where a reassembler would double-count it. The flags byte has the first bit set.
        byte[] first = server.DecryptFrame(frames[0].Payload.ToArray());
        Assert.Equal(NoiseConstants.MessageTypeFragment, first[0]);
        Assert.Equal(NoiseConstants.FragmentFlagFirst, first[1]);
        Assert.Equal(origType, first[2]);
        Assert.Equal(MaxPlaintext, first.Length);

        // Last fragment: [1][flags], then body. Flags has the last bit set, no repeated orig_type.
        byte[] last = server.DecryptFrame(frames[1].Payload.ToArray());
        Assert.Equal(NoiseConstants.MessageTypeFragment, last[0]);
        Assert.Equal(NoiseConstants.FragmentFlagLast, last[1]);
        Assert.Equal(5, last.Length);
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
