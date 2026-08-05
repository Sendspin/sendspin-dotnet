using System.Security.Cryptography;
using System.Text;
using Sendspin.SDK.Connection.Framing;
using Sendspin.SDK.Connection.Noise;

namespace Sendspin.SDK.Tests.Connection;

/// <summary>
/// The spec's malformed fragment sequences (messaging.md: a fragment-end frame with no
/// fragmented message in flight, a non-fragment frame while a fragmented message is in
/// flight, and an <c>orig_type</c> of 2 or 3 — the receiver MUST close on each), plus
/// the local pre-first-message reassembly cap.
/// </summary>
public class FragmentationConformanceTests
{
    private const string HelloJson = """{"type":"server/hello","payload":{"name":"srv"}}""";

    [Fact]
    public void NonFragmentFrame_MidReassembly_IsFatal_AndReassemblyDoesNotSurvive()
    {
        var identity = SendspinIdentity.Generate();
        var framing = new NoiseWireFraming(identity);
        var server = CompleteHandshake(framing, identity);

        // Open a reassembly.
        Assert.Null(Feed(framing, server, [NoiseConstants.MessageTypeFragmentMore, 8, 0xDE, 0xAD]).FatalReason);

        // Spec: a non-fragment frame received while a fragmented message is in flight
        // is a malformed sequence; the receiver MUST close.
        var result = Feed(framing, server, [NoiseConstants.MessageTypeJsonBody, .. Encoding.UTF8.GetBytes(HelloJson)]);
        Assert.NotNull(result.FatalReason);
        Assert.Null(result.Text);

        // The abandoned reassembly must not survive: on the next connection a
        // fragment-more opens a NEW message, and the reassembled bytes contain
        // nothing from the abandoned buffer.
        framing.Reset();
        server = CompleteHandshake(framing, identity);
        Assert.Null(Feed(framing, server, [NoiseConstants.MessageTypeFragmentMore, 8, 0x01, 0x02]).FatalReason);
        var reassembled = Feed(framing, server, [NoiseConstants.MessageTypeFragmentEnd, 0x03]);
        Assert.Null(reassembled.FatalReason);
        Assert.Equal(new byte[] { 8, 0x01, 0x02, 0x03 }, reassembled.Binary!.Value.ToArray());
    }

    [Theory]
    [InlineData(NoiseConstants.MessageTypeFragmentMore)]
    [InlineData(NoiseConstants.MessageTypeFragmentEnd)]
    public void OpeningFragment_WithFragmentOrigType_IsFatal_AndSurfacesNothing(byte origType)
    {
        var identity = SendspinIdentity.Generate();
        var framing = new NoiseWireFraming(identity);
        var server = CompleteHandshake(framing, identity);

        // Spec: an orig_type of 2 or 3 is a malformed sequence.
        var opening = Feed(framing, server, [NoiseConstants.MessageTypeFragmentMore, origType, 0xAA]);
        Assert.NotNull(opening.FatalReason);
        Assert.Null(opening.Text);
        Assert.Null(opening.Binary);

        // Even if the peer pushes the rest of the sequence, nothing may surface to
        // BinaryMessageParser (the pre-fix defect dispatched [origType][payload] as
        // an application binary message at fragment-end).
        var end = Feed(framing, server, [NoiseConstants.MessageTypeFragmentEnd, 0xBB]);
        Assert.Null(end.Text);
        Assert.Null(end.Binary);
    }

    [Fact]
    public void FragmentEnd_WithNothingInFlight_IsFatal()
    {
        var identity = SendspinIdentity.Generate();
        var framing = new NoiseWireFraming(identity);
        var server = CompleteHandshake(framing, identity);

        var result = Feed(framing, server, [NoiseConstants.MessageTypeFragmentEnd, 1, 2, 3]);

        Assert.NotNull(result.FatalReason);
    }

    [Fact]
    public void Reassembly_PastPreFirstMessageCap_BeforeFirstApplicationMessage_IsFatal()
    {
        var identity = SendspinIdentity.Generate();
        var framing = new NoiseWireFraming(identity);
        var server = CompleteHandshake(framing, identity);

        // 3 x 50 KB crosses the 128 KiB pre-first-message cap on the third frame
        // while staying far below the 64 MiB post-first-message ceiling.
        byte[] chunk = new byte[50_000];
        Assert.Null(Feed(framing, server, [NoiseConstants.MessageTypeFragmentMore, 8, .. chunk]).FatalReason);
        Assert.Null(Feed(framing, server, [NoiseConstants.MessageTypeFragmentMore, .. chunk]).FatalReason);

        Assert.NotNull(Feed(framing, server, [NoiseConstants.MessageTypeFragmentMore, .. chunk]).FatalReason);
    }

    [Fact]
    public void Reassembly_LargerThanPreFirstMessageCap_AfterFirstApplicationMessage_Succeeds()
    {
        var identity = SendspinIdentity.Generate();
        var framing = new NoiseWireFraming(identity);
        var server = CompleteHandshake(framing, identity);
        SurfaceFirstApplicationMessage(framing, server);

        byte[] payload = new byte[NoiseConstants.MaxReassembledMessageBytesBeforeFirstMessage + 1024];
        RandomNumberGenerator.Fill(payload);
        byte[] appMessage = [8, .. payload];

        InboundFrameResult last = default;
        foreach (var frame in server.EncryptFragmented(appMessage))
        {
            last = framing.ProcessInbound(new WireFrame(WireFrameKind.Binary, frame));
            Assert.Null(last.FatalReason);
        }

        Assert.Equal(appMessage, last.Binary!.Value.ToArray());
    }

    [Fact]
    public void Reset_RestoresThePreFirstMessageCap()
    {
        var identity = SendspinIdentity.Generate();
        var framing = new NoiseWireFraming(identity);
        var server = CompleteHandshake(framing, identity);
        SurfaceFirstApplicationMessage(framing, server); // loose cap now in effect

        framing.Reset();
        server = CompleteHandshake(framing, identity);

        // Nothing has surfaced on THIS connection, so the tight cap applies again.
        byte[] chunk = new byte[50_000];
        Assert.Null(Feed(framing, server, [NoiseConstants.MessageTypeFragmentMore, 8, .. chunk]).FatalReason);
        Assert.Null(Feed(framing, server, [NoiseConstants.MessageTypeFragmentMore, .. chunk]).FatalReason);

        Assert.NotNull(Feed(framing, server, [NoiseConstants.MessageTypeFragmentMore, .. chunk]).FatalReason);
    }

    [Fact]
    public void Rehandshake_DoesNotLiftThePreFirstMessageCap()
    {
        var identity = SendspinIdentity.Generate();
        var store = new InMemoryPairingRecordStore();
        byte[] newPsk = RandomNumberGenerator.GetBytes(32);
        store.Upsert(new PairingRecord(newPsk, PskCategory.LongTerm));
        var framing = new NoiseWireFraming(identity, new RecordPskResolver(store));
        var server = new TestNoiseServer(identity.PublicKey, NoiseConstants.SentinelPsk.ToArray());

        var clientInit = Assert.Single(framing.Start());
        var (serverInit, msg1) = server.Respond(clientInit.PayloadAsText());
        Assert.Null(framing.ProcessInbound(WireFrame.FromText(serverInit)).FatalReason);
        var hs = framing.ProcessInbound(WireFrame.FromText(msg1));
        server.CompleteHandshake(Assert.Single(hs.Replies!).PayloadAsText());

        // A server-initiated re-handshake completes, is consumed by the framing, and
        // must NOT count as a surfaced application message.
        var rehs = framing.ProcessInbound(new WireFrame(WireFrameKind.Binary, server.StartRehandshake(newPsk)));
        Assert.Null(rehs.FatalReason);
        Assert.Null(rehs.Text);
        server.CompleteRehandshake(Assert.Single(rehs.Replies!).Payload.ToArray());
        Assert.Equal(PskCategory.LongTerm, framing.MatchedPsk!.Category);

        // Still before the first application message: the tight cap must apply, or a
        // hostile peer could lift it just by triggering a re-handshake.
        byte[] chunk = new byte[50_000];
        Assert.Null(Feed(framing, server, [NoiseConstants.MessageTypeFragmentMore, 8, .. chunk]).FatalReason);
        Assert.Null(Feed(framing, server, [NoiseConstants.MessageTypeFragmentMore, .. chunk]).FatalReason);

        Assert.NotNull(Feed(framing, server, [NoiseConstants.MessageTypeFragmentMore, .. chunk]).FatalReason);
    }

    [Fact]
    public void Reassembly_PastPostFirstMessageCeiling_IsFatal()
    {
        var identity = SendspinIdentity.Generate();
        var framing = new NoiseWireFraming(identity);
        var server = CompleteHandshake(framing, identity);
        SurfaceFirstApplicationMessage(framing, server);

        // Stream maximum-size continuation fragments; the fatal must arrive exactly
        // when the reassembled size would first exceed the 64 MiB ceiling.
        int dataPerFrame = NoiseConstants.MaxTransportPlaintext - 1; // continuation: [2][data]
        byte[] chunk = new byte[dataPerFrame];
        var result = Feed(framing, server, [NoiseConstants.MessageTypeFragmentMore, 8, .. new byte[dataPerFrame - 1]]);
        long buffered = dataPerFrame - 1;
        while (result.FatalReason is null)
        {
            Assert.True(buffered <= NoiseConstants.MaxReassembledMessageBytes,
                "reassembly accepted more than MaxReassembledMessageBytes without a fatal");
            result = Feed(framing, server, [NoiseConstants.MessageTypeFragmentMore, .. chunk]);
            buffered += dataPerFrame;
        }

        Assert.True(buffered > NoiseConstants.MaxReassembledMessageBytes);
    }

    // --- Harness ---

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

    private static InboundFrameResult Feed(NoiseWireFraming framing, TestNoiseServer server, byte[] plaintext) =>
        framing.ProcessInbound(new WireFrame(WireFrameKind.Binary, server.EncryptFrame(plaintext)));

    private static void SurfaceFirstApplicationMessage(NoiseWireFraming framing, TestNoiseServer server)
    {
        var result = Feed(framing, server, [NoiseConstants.MessageTypeJsonBody, .. Encoding.UTF8.GetBytes(HelloJson)]);
        Assert.Equal(HelloJson, result.Text); // the cap widens only on a genuine surface
    }
}
