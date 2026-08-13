using System.Security.Cryptography;
using System.Text;
using Sendspin.SDK.Connection.Framing;
using Sendspin.SDK.Connection.Noise;

namespace Sendspin.SDK.Tests.Connection;

/// <summary>
/// Exercises <see cref="NoiseWireFraming"/> against an in-process server-side Noise
/// initiator (the same role aiosendspin's server plays): handshake, transport
/// encryption in both directions, fragmentation, and the spec's failure rules.
/// </summary>
public class NoiseWireFramingTests
{
    // --- Spec constants ---

    [Fact]
    public void SentinelPsk_MatchesPublishedSpecConstants()
    {
        Assert.Equal(
            "1b5e24dbc1aed95fc2a5a338a90c05df44bd10f5ec1f4cd66cbf86272767b9d3",
            Convert.ToHexString(NoiseConstants.SentinelPsk.ToArray()).ToLowerInvariant());
        Assert.Equal("GFsV9tLaSQm9HcFWpKsgYQOr7wFTvNUtkmFwuVz3zoo", NoiseConstants.SentinelPskId);
    }

    [Fact]
    public void Identity_PeerId_Is43CharBase64UrlPublicKey()
    {
        var identity = SendspinIdentity.Generate();
        Assert.Equal(43, identity.PeerId.Length);
        Assert.Equal(identity.PublicKey.ToArray(), SendspinIdentity.DecodePeerId(identity.PeerId));
    }

    // --- Handshake ---

    [Fact]
    public void Handshake_CompletesAgainstServerInitiator()
    {
        var (framing, server) = CompleteHandshake();

        Assert.True(framing.IsTransportReady);
        Assert.Equal(server.ServerId, framing.ServerId);
        Assert.Equal(PskCategory.Sentinel, framing.MatchedPsk!.Category);
        Assert.NotNull(framing.HandshakeHash);
    }

    [Fact]
    public void Handshake_PrologueIsByteConcatenationOfWireCapturedInitPayloads()
    {
        var identity = SendspinIdentity.Generate();
        var framing = new NoiseWireFraming(identity);
        var server = new TestNoiseServer(identity.PublicKey, NoiseConstants.SentinelPsk.ToArray());

        var clientInit = Assert.Single(framing.Start());
        byte[] clientInitBytes = clientInit.Payload.ToArray();

        // server/init carrying a multi-byte UTF-8 sequence ('é' is two bytes in UTF-8).
        // A lossy prologue construction (decode then re-encode) would still round-trip
        // this correctly, which is exactly why the comparison below must not recompute
        // the expected prologue from strings either -- it uses the same wire bytes the
        // framing received.
        string serverInitText =
            "{\"type\":\"server/init\",\"payload\":{\"server_id\":\"" + server.ServerId +
            "\",\"version\":" + NoiseConstants.ProtocolVersion + ",\"note\":\"café\"}}";
        var serverInitFrame = WireFrame.FromText(serverInitText);
        byte[] serverInitBytes = serverInitFrame.Payload.ToArray();
        Assert.Null(framing.ProcessInbound(serverInitFrame).FatalReason);

        // Expected prologue: byte concatenation of the two payloads exactly as they
        // crossed the wire, not re-derived from the JSON strings.
        byte[] expectedPrologue = [.. clientInitBytes, .. serverInitBytes];

        // Drive the responder handshake with that exact prologue. This only completes
        // if the framing computed the identical prologue internally -- a mismatched
        // prologue diverges the Noise transcript hash and message 1 fails to
        // authenticate, so this is a cryptographic equality check, not a string one.
        string msg1Text = server.RespondWithPrologue(expectedPrologue);
        var result = framing.ProcessInbound(WireFrame.FromText(msg1Text));

        Assert.Null(result.FatalReason);
        var reply = Assert.Single(result.Replies!);
        server.CompleteHandshake(reply.PayloadAsText());
        Assert.True(framing.IsTransportReady);
    }

    [Fact]
    public void Handshake_UnknownPskId_IsFatal()
    {
        var identity = SendspinIdentity.Generate();
        var framing = new NoiseWireFraming(identity);
        var server = new TestNoiseServer(identity.PublicKey, psk: RandomNumberGenerator.GetBytes(32));

        var clientInit = Assert.Single(framing.Start());
        var (serverInit, msg1) = server.Respond(clientInit.PayloadAsText());

        Assert.Null(framing.ProcessInbound(WireFrame.FromText(serverInit)).FatalReason);
        var result = framing.ProcessInbound(WireFrame.FromText(msg1));

        Assert.NotNull(result.FatalReason);
        Assert.False(framing.IsTransportReady);
    }

    [Fact]
    public void Handshake_PskBoundToOtherServer_IsFatal()
    {
        var identity = SendspinIdentity.Generate();
        var resolver = new BoundResolver("SomeOtherServerIdAAAAAAAAAAAAAAAAAAAAAAAAAA");
        var framing = new NoiseWireFraming(identity, resolver);
        var server = new TestNoiseServer(identity.PublicKey, NoiseConstants.SentinelPsk.ToArray());

        var clientInit = Assert.Single(framing.Start());
        var (serverInit, msg1) = server.Respond(clientInit.PayloadAsText());
        framing.ProcessInbound(WireFrame.FromText(serverInit));

        var result = framing.ProcessInbound(WireFrame.FromText(msg1));
        Assert.NotNull(result.FatalReason);
    }

    [Fact]
    public void Handshake_UnsupportedServerVersion_IsFatal()
    {
        var identity = SendspinIdentity.Generate();
        var framing = new NoiseWireFraming(identity);
        framing.Start();

        var serverInit = """{"type":"server/init","payload":{"server_id":"GFsV9tLaSQm9HcFWpKsgYQOr7wFTvNUtkmFwuVz3zoo","version":2}}""";
        var result = framing.ProcessInbound(WireFrame.FromText(serverInit));

        Assert.NotNull(result.FatalReason);
    }

    [Fact]
    public void SendBeforeHandshakeComplete_Throws()
    {
        var framing = new NoiseWireFraming(SendspinIdentity.Generate());
        framing.Start();
        Assert.Throws<InvalidOperationException>(() => framing.EncodeText("{}").ToList());
    }

    // --- Transport mode ---

    [Fact]
    public void Inbound_EncryptedJson_SurfacesAsText()
    {
        var (framing, server) = CompleteHandshake();
        const string json = """{"type":"server/hello","payload":{"name":"srv"}}""";

        var frame = server.EncryptFrame([0, .. Encoding.UTF8.GetBytes(json)]);
        var result = framing.ProcessInbound(new WireFrame(WireFrameKind.Binary, frame));

        Assert.Equal(json, result.Text);
    }

    [Fact]
    public void Inbound_EncryptedBinary_SurfacesWithTypeByte()
    {
        var (framing, server) = CompleteHandshake();
        byte[] appMessage = [4, 0, 0, 0, 0, 0, 0, 0, 42, 0xAA, 0xBB];

        var frame = server.EncryptFrame(appMessage);
        var result = framing.ProcessInbound(new WireFrame(WireFrameKind.Binary, frame));

        Assert.Equal(appMessage, result.Binary!.Value.ToArray());
    }

    [Fact]
    public void Outbound_Json_DecryptsOnServerSide()
    {
        var (framing, server) = CompleteHandshake();
        const string json = """{"type":"client/time","payload":{"client_transmitted":1}}""";

        var frames = framing.EncodeText(json).ToList();

        var frame = Assert.Single(frames);
        byte[] plaintext = server.DecryptFrame(frame.Payload.ToArray());
        Assert.Equal(0, plaintext[0]);
        Assert.Equal(json, Encoding.UTF8.GetString(plaintext[1..]));
    }

    [Fact]
    public void Inbound_FragmentedMessage_Reassembles()
    {
        var (framing, server) = CompleteHandshake();

        // A 100 KB artwork-style message ([type 8][payload...]): spans multiple frames
        // while staying under the pre-first-message reassembly cap, since nothing has
        // surfaced yet on this connection.
        byte[] payload = new byte[100_000];
        RandomNumberGenerator.Fill(payload);
        byte[] appMessage = [8, .. payload];

        InboundFrameResult last = default;
        foreach (var wireFrame in server.EncryptFragmented(appMessage))
        {
            last = framing.ProcessInbound(new WireFrame(WireFrameKind.Binary, wireFrame));
            Assert.Null(last.FatalReason);
        }

        Assert.Equal(appMessage, last.Binary!.Value.ToArray());
    }

    [Fact]
    public void Outbound_OversizeMessage_FragmentsAndReassemblesOnServerSide()
    {
        var (framing, server) = CompleteHandshake();
        byte[] payload = new byte[200_000];
        RandomNumberGenerator.Fill(payload);
        byte[] appMessage = [12, .. payload];

        var frames = framing.EncodeBinary(appMessage).ToList();

        Assert.True(frames.Count > 1);
        byte[] reassembled = server.DecryptAndReassemble(frames.Select(f => f.Payload.ToArray()));
        Assert.Equal(appMessage, reassembled);
    }

    [Fact]
    public void Inbound_FragmentEndWithoutStart_IsFatal()
    {
        var (framing, server) = CompleteHandshake();

        var frame = server.EncryptFrame([NoiseConstants.MessageTypeFragmentEnd, 1, 2, 3]);
        var result = framing.ProcessInbound(new WireFrame(WireFrameKind.Binary, frame));

        Assert.NotNull(result.FatalReason);
    }

    [Fact]
    public void Inbound_GarbageCiphertext_IsFatal()
    {
        var (framing, _) = CompleteHandshake();

        var result = framing.ProcessInbound(new WireFrame(WireFrameKind.Binary, new byte[64]));

        Assert.NotNull(result.FatalReason);
    }

    [Fact]
    public void Inbound_ApplicationJsonContainingHandshakeLiteral_WithNoRootType_Surfaces()
    {
        var (framing, server) = CompleteHandshake();

        // Application content whose value happens to equal the literal "noise/handshake"
        // (e.g. a management record field) and which has no root "type" member at all.
        // Routing by substring sniff sends this into HandleRehandshakeMessage, which does
        // doc.RootElement.GetProperty("type") and throws KeyNotFoundException -- fatal to
        // the connection under the current (pre-fix) code.
        const string json = """{"track_title":"noise/handshake"}""";
        var frame = server.EncryptFrame([0, .. Encoding.UTF8.GetBytes(json)]);

        var result = framing.ProcessInbound(new WireFrame(WireFrameKind.Binary, frame));

        Assert.Null(result.FatalReason);
        Assert.Equal(json, result.Text);
        Assert.True(framing.IsTransportReady);
    }

    [Fact]
    public void Inbound_ApplicationJsonWithOtherRootType_ContainingHandshakeLiteral_Surfaces()
    {
        var (framing, server) = CompleteHandshake();

        // Root type is an ordinary application type; the literal only appears inside the
        // body. Must surface, not route to the re-handshake handler.
        const string json = """{"type":"server/hello","payload":{"note":"noise/handshake"}}""";
        var frame = server.EncryptFrame([0, .. Encoding.UTF8.GetBytes(json)]);

        var result = framing.ProcessInbound(new WireFrame(WireFrameKind.Binary, frame));

        Assert.Null(result.FatalReason);
        Assert.Equal(json, result.Text);
        Assert.True(framing.IsTransportReady);
    }

    [Fact]
    public void Rehandshake_SwapsKeys_AndTrafficContinues()
    {
        var identity = SendspinIdentity.Generate();
        var store = new InMemoryPairingRecordStore();
        byte[] newPsk = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        store.Upsert(new PairingRecord(newPsk, PskCategory.LongTerm));
        var framing = new NoiseWireFraming(identity, new RecordPskResolver(store));
        var server = new TestNoiseServer(identity.PublicKey, NoiseConstants.SentinelPsk.ToArray());

        var clientInit = Assert.Single(framing.Start());
        var (serverInit, msg1) = server.Respond(clientInit.PayloadAsText());
        framing.ProcessInbound(WireFrame.FromText(serverInit));
        var hs = framing.ProcessInbound(WireFrame.FromText(msg1));
        server.CompleteHandshake(Assert.Single(hs.Replies!).PayloadAsText());
        Assert.Equal(PskCategory.Sentinel, framing.MatchedPsk!.Category);

        // Server initiates re-handshake to the long-term PSK inside the channel.
        byte[] rehsMsg1 = server.StartRehandshake(newPsk);
        var result = framing.ProcessInbound(new WireFrame(WireFrameKind.Binary, rehsMsg1));

        Assert.Null(result.FatalReason);
        Assert.Null(result.Text); // consumed by the framing, never surfaced
        Assert.True(result.HasDeferredReply);
        Assert.Null(result.Replies); // the reply must NOT be produced on the receive path

        // The connection encodes the reply on its send path; the same call commits the swap.
        var reply = Assert.Single(framing.EncodeDeferredReply());
        server.CompleteRehandshake(reply.Payload.ToArray());
        Assert.Equal(PskCategory.LongTerm, framing.MatchedPsk!.Category);

        // Traffic continues under the NEW keys in both directions.
        const string json = """{"type":"server/hello","payload":{"name":"again"}}""";
        var inbound = framing.ProcessInbound(new WireFrame(WireFrameKind.Binary,
            server.EncryptFrame([0, .. Encoding.UTF8.GetBytes(json)])));
        Assert.Equal(json, inbound.Text);
        var outFrame = Assert.Single(framing.EncodeText(json).ToList());
        Assert.Equal(json, Encoding.UTF8.GetString(server.DecryptFrame(outFrame.Payload.ToArray())[1..]));
    }

    /// <summary>
    /// Spec [#122]: message 2 travels under the pre-re-handshake keys and the new keys
    /// take effect only afterwards. Until the connection's send path encodes the deferred
    /// reply, the swap must not be observable anywhere -- outbound frames still encrypt
    /// under the old keys and the session still reports the old authentication. This is
    /// what lets a send racing the re-handshake land entirely before the reply (#81).
    /// </summary>
    [Fact]
    public void Rehandshake_SwapIsNotObservableBeforeTheDeferredReplyIsEncoded()
    {
        var identity = SendspinIdentity.Generate();
        var store = new InMemoryPairingRecordStore();
        byte[] newPsk = RandomNumberGenerator.GetBytes(32);
        store.Upsert(new PairingRecord(newPsk, PskCategory.LongTerm));
        var framing = new NoiseWireFraming(identity, new RecordPskResolver(store));
        var server = new TestNoiseServer(identity.PublicKey, NoiseConstants.SentinelPsk.ToArray());

        var clientInit = Assert.Single(framing.Start());
        var (serverInit, msg1) = server.Respond(clientInit.PayloadAsText());
        framing.ProcessInbound(WireFrame.FromText(serverInit));
        var hs = framing.ProcessInbound(WireFrame.FromText(msg1));
        server.CompleteHandshake(Assert.Single(hs.Replies!).PayloadAsText());

        var result = framing.ProcessInbound(new WireFrame(WireFrameKind.Binary, server.StartRehandshake(newPsk)));
        Assert.Null(result.FatalReason);
        Assert.True(result.HasDeferredReply);

        // Before the reply is encoded: outbound frames must still decrypt under the
        // server's OLD transport (a frame under the new keys would fail AEAD here)...
        const string before = """{"type":"client/time","payload":{"seq":1}}""";
        var beforeFrame = Assert.Single(framing.EncodeText(before).ToList());
        Assert.Equal(before, Encoding.UTF8.GetString(server.DecryptFrame(beforeFrame.Payload.ToArray())[1..]));

        // ...and the session still reports the OLD authentication.
        Assert.Equal(PskCategory.Sentinel, framing.MatchedPsk!.Category);

        // EncodeDeferredReply encodes under the old keys and commits the swap, in one call.
        var reply = Assert.Single(framing.EncodeDeferredReply());
        server.CompleteRehandshake(reply.Payload.ToArray());
        Assert.Equal(PskCategory.LongTerm, framing.MatchedPsk!.Category);

        // After the commit, outbound traffic is under the NEW keys.
        const string after = """{"type":"client/time","payload":{"seq":2}}""";
        var afterFrame = Assert.Single(framing.EncodeText(after).ToList());
        Assert.Equal(after, Encoding.UTF8.GetString(server.DecryptFrame(afterFrame.Payload.ToArray())[1..]));
    }

    /// <summary>
    /// Regression guard for the branch the deferred swap does not touch: the initial
    /// handshake reply is an immediate cleartext text frame -- there are no prior
    /// session keys for it to travel under, and no swap to defer.
    /// </summary>
    [Fact]
    public void InitialHandshake_ReplyIsCleartext_AndNothingIsDeferred()
    {
        var identity = SendspinIdentity.Generate();
        var framing = new NoiseWireFraming(identity);
        var server = new TestNoiseServer(identity.PublicKey, NoiseConstants.SentinelPsk.ToArray());

        var clientInit = Assert.Single(framing.Start());
        var (serverInit, msg1) = server.Respond(clientInit.PayloadAsText());
        Assert.Null(framing.ProcessInbound(WireFrame.FromText(serverInit)).FatalReason);
        var result = framing.ProcessInbound(WireFrame.FromText(msg1));

        Assert.False(result.HasDeferredReply);
        var reply = Assert.Single(result.Replies!);
        Assert.Equal(WireFrameKind.Text, reply.Kind);
        Assert.Throws<InvalidOperationException>(() => framing.EncodeDeferredReply());

        server.CompleteHandshake(reply.PayloadAsText());
        Assert.True(framing.IsTransportReady);
    }

    /// <summary>
    /// A second re-handshake before the pending swap is committed is a protocol
    /// violation -- the server cannot have received message 2 yet -- and must fail
    /// loudly rather than silently overwriting the pending keys.
    /// </summary>
    [Fact]
    public void Rehandshake_SecondMessage1BeforeTheReplyIsCommitted_IsFatal()
    {
        var identity = SendspinIdentity.Generate();
        var store = new InMemoryPairingRecordStore();
        byte[] newPsk = RandomNumberGenerator.GetBytes(32);
        store.Upsert(new PairingRecord(newPsk, PskCategory.LongTerm));
        var framing = new NoiseWireFraming(identity, new RecordPskResolver(store));
        var server = new TestNoiseServer(identity.PublicKey, NoiseConstants.SentinelPsk.ToArray());

        var clientInit = Assert.Single(framing.Start());
        var (serverInit, msg1) = server.Respond(clientInit.PayloadAsText());
        framing.ProcessInbound(WireFrame.FromText(serverInit));
        var hs = framing.ProcessInbound(WireFrame.FromText(msg1));
        server.CompleteHandshake(Assert.Single(hs.Replies!).PayloadAsText());

        var first = framing.ProcessInbound(new WireFrame(WireFrameKind.Binary, server.StartRehandshake(newPsk)));
        Assert.True(first.HasDeferredReply);

        var second = framing.ProcessInbound(new WireFrame(WireFrameKind.Binary, server.StartRehandshake(newPsk)));
        Assert.NotNull(second.FatalReason);
        Assert.Contains("uncommitted", second.FatalReason);
    }

    // --- Harness ---

    private static (NoiseWireFraming Framing, TestNoiseServer Server) CompleteHandshake()
    {
        var identity = SendspinIdentity.Generate();
        var framing = new NoiseWireFraming(identity);
        var server = new TestNoiseServer(identity.PublicKey, NoiseConstants.SentinelPsk.ToArray());

        var clientInit = Assert.Single(framing.Start());
        var (serverInit, msg1) = server.Respond(clientInit.PayloadAsText());

        Assert.Null(framing.ProcessInbound(WireFrame.FromText(serverInit)).FatalReason);
        var result = framing.ProcessInbound(WireFrame.FromText(msg1));
        Assert.Null(result.FatalReason);

        var reply = Assert.Single(result.Replies!);
        server.CompleteHandshake(reply.PayloadAsText());
        return (framing, server);
    }

    private sealed class BoundResolver(string serverId) : INoisePskResolver
    {
        public NoisePsk? Resolve(string pskId) =>
            new(NoiseConstants.SentinelPsk.ToArray(), PskCategory.LongTerm, serverId);
    }
}
