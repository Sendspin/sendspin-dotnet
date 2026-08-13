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

    /// <summary>
    /// The framing's fatal for a reassembly crossing either cap. Asserted by text rather than
    /// for non-null because an unrelated fatal on the crossing frame satisfied the old
    /// assertion just as well (#110).
    /// </summary>
    private const string CapExceededFatal = "reassembled message exceeds size bound";

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

        // Fixed expectation, deliberately NOT MaxReassembledMessageBytesBeforeFirstMessage:
        // reading the cap from the constant under test is what let a silent edit of it move
        // the boundary and still pass. The old 3 x 50 KB shape only bracketed the cap to
        // [100_000, 150_000) (#110); feeding exactly the cap and then one byte past it pins
        // it from both sides -- a smaller cap fatals on the fourth frame, a larger one fails
        // to fatal on the fifth.
        const int cap = 128 * 1024;
        const int perFrame = cap / 4; // 4 x 32 KiB, each well under MaxTransportPlaintext

        // The opening fragment spends a byte on orig_type, which is not counted toward the
        // reassembled size, so every frame here contributes exactly perFrame bytes.
        byte[] chunk = new byte[perFrame];
        Assert.Null(Feed(framing, server, [NoiseConstants.MessageTypeFragmentMore, 8, .. chunk]).FatalReason);
        for (int i = 0; i < 3; i++)
        {
            Assert.Null(Feed(framing, server, [NoiseConstants.MessageTypeFragmentMore, .. chunk]).FatalReason);
        }

        // Landing exactly on the cap is still legal; the very next byte is not.
        var result = Feed(framing, server, [NoiseConstants.MessageTypeFragmentMore, 0x00]);
        Assert.Equal(CapExceededFatal, result.FatalReason);
    }

    [Fact]
    public void ReassemblyCaps_HoldTheirPublishedValues()
    {
        // Both caps are local hardening choices rather than spec numbers, so nothing outside
        // this suite would notice them drifting. The behavioural tests bracket enforcement;
        // these pin the declared values, so an edit inside a bracket cannot pass silently
        // (#110). Changing either is a deliberate act that should update this test.
        Assert.Equal(128 * 1024, NoiseConstants.MaxReassembledMessageBytesBeforeFirstMessage);
        Assert.Equal(64 * 1024 * 1024, NoiseConstants.MaxReassembledMessageBytes);
    }

    [Fact]
    public void Reassembly_LargerThanPreFirstMessageCap_AfterFirstApplicationMessage_Succeeds()
    {
        var identity = SendspinIdentity.Generate();
        var framing = new NoiseWireFraming(identity);
        var server = CompleteHandshake(framing, identity);
        SurfaceFirstApplicationMessage(framing, server);

        // Sized from a literal, not from MaxReassembledMessageBytesBeforeFirstMessage: taking
        // it from the constant meant lowering the tight cap shrank this payload with it, so
        // the test kept passing without ever exceeding the cap it is meant to clear (#110).
        byte[] payload = new byte[(128 * 1024) + 1024];
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

        // Nothing has surfaced on THIS connection, so the tight cap applies again. The exact
        // value is pinned by Reassembly_PastPreFirstMessageCap_...; the subject here is only
        // which cap is in force, so 3 x 50 KB is enough to cross the tight one.
        byte[] chunk = new byte[50_000];
        Assert.Null(Feed(framing, server, [NoiseConstants.MessageTypeFragmentMore, 8, .. chunk]).FatalReason);
        Assert.Null(Feed(framing, server, [NoiseConstants.MessageTypeFragmentMore, .. chunk]).FatalReason);

        Assert.Equal(
            CapExceededFatal,
            Feed(framing, server, [NoiseConstants.MessageTypeFragmentMore, .. chunk]).FatalReason);
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
        Assert.True(rehs.HasDeferredReply);
        server.CompleteRehandshake(Assert.Single(framing.EncodeDeferredReply()).Payload.ToArray());
        Assert.Equal(PskCategory.LongTerm, framing.MatchedPsk!.Category);

        // Still before the first application message: the tight cap must apply, or a
        // hostile peer could lift it just by triggering a re-handshake.
        byte[] chunk = new byte[50_000];
        Assert.Null(Feed(framing, server, [NoiseConstants.MessageTypeFragmentMore, 8, .. chunk]).FatalReason);
        Assert.Null(Feed(framing, server, [NoiseConstants.MessageTypeFragmentMore, .. chunk]).FatalReason);

        Assert.Equal(
            CapExceededFatal,
            Feed(framing, server, [NoiseConstants.MessageTypeFragmentMore, .. chunk]).FatalReason);
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
        //
        // Fixed expectation rather than MaxReassembledMessageBytes: bounding the loop by the
        // constant it exists to pin made any edit of the ceiling self-consistent -- the loop
        // just ran a different number of times and both assertions still held (#110).
        const long ceiling = 64L * 1024 * 1024;

        int dataPerFrame = NoiseConstants.MaxTransportPlaintext - 1; // continuation: [2][data]
        byte[] chunk = new byte[dataPerFrame];
        var result = Feed(framing, server, [NoiseConstants.MessageTypeFragmentMore, 8, .. new byte[dataPerFrame - 1]]);
        long buffered = dataPerFrame - 1;
        while (result.FatalReason is null)
        {
            Assert.True(buffered <= ceiling,
                "reassembly accepted more than the 64 MiB ceiling without a fatal");
            result = Feed(framing, server, [NoiseConstants.MessageTypeFragmentMore, .. chunk]);
            buffered += dataPerFrame;
        }

        Assert.Equal(CapExceededFatal, result.FatalReason);
        Assert.True(buffered > ceiling);
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
