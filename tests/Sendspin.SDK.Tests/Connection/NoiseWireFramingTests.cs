using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Noise;
using Sendspin.SDK.Connection.Framing;
using Sendspin.SDK.Connection.Noise;
using NoiseProtocol = Noise.Protocol;

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
            Convert.ToHexStringLower(NoiseConstants.SentinelPsk.ToArray()));
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
    public void Handshake_UnknownPskId_IsFatal()
    {
        var identity = SendspinIdentity.Generate();
        var framing = new NoiseWireFraming(identity);
        var server = new TestNoiseServer(identity, psk: RandomNumberGenerator.GetBytes(32));

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
        var server = new TestNoiseServer(identity, NoiseConstants.SentinelPsk.ToArray());

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

        // A 150 KB artwork-style message: [type 8][payload...]
        byte[] payload = new byte[150_000];
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

    // --- Harness ---

    private static (NoiseWireFraming Framing, TestNoiseServer Server) CompleteHandshake()
    {
        var identity = SendspinIdentity.Generate();
        var framing = new NoiseWireFraming(identity);
        var server = new TestNoiseServer(identity, NoiseConstants.SentinelPsk.ToArray());

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

    /// <summary>Server-side Noise initiator, mirroring aiosendspin's server role.</summary>
    private sealed class TestNoiseServer
    {
        private readonly KeyPair _keys = KeyPair.Generate();
        private readonly SendspinIdentity _clientIdentity;
        private readonly byte[] _psk;
        private HandshakeState? _state;
        private Transport? _transport;

        public string ServerId { get; }

        public TestNoiseServer(SendspinIdentity clientIdentity, byte[] psk)
        {
            _clientIdentity = clientIdentity;
            _psk = psk;
            ServerId = Base64Url.EncodeToString(_keys.PublicKey);
        }

        public (string ServerInitText, string Msg1Text) Respond(string clientInitText)
        {
            string serverInitText = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["type"] = "server/init",
                ["payload"] = new Dictionary<string, object> { ["server_id"] = ServerId, ["version"] = 1 },
            });

            byte[] prologue = Encoding.UTF8.GetBytes(clientInitText + serverInitText);
            var protocol = NoiseProtocol.Parse("Noise_KKpsk2_25519_ChaChaPoly_SHA256".AsSpan());
            _state = protocol.Create(
                initiator: true, prologue: prologue,
                s: (byte[])_keys.PrivateKey.Clone(),
                rs: _clientIdentity.PublicKey.ToArray(),
                psks: [_psk]);

            string msg1Payload = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["psk_id"] = NoiseConstants.DerivePskId(_psk),
            });
            var buf = new byte[NoiseProtocol.MaxMessageLength];
            var (len, _, _) = _state.WriteMessage(Encoding.UTF8.GetBytes(msg1Payload), buf);

            string msg1Text = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["type"] = "noise/handshake",
                ["payload"] = new Dictionary<string, object> { ["data"] = Base64Url.EncodeToString(buf.AsSpan(0, len)) },
            });
            return (serverInitText, msg1Text);
        }

        public void CompleteHandshake(string msg2Text)
        {
            using var doc = JsonDocument.Parse(msg2Text);
            byte[] msg2 = Base64Url.DecodeFromChars(
                doc.RootElement.GetProperty("payload").GetProperty("data").GetString()!);
            var buf = new byte[NoiseProtocol.MaxMessageLength];
            var (_, _, transport) = _state!.ReadMessage(msg2, buf);
            _transport = transport ?? throw new InvalidOperationException("handshake incomplete");
            _state.Dispose();
        }

        public byte[] EncryptFrame(byte[] plaintext)
        {
            var buf = new byte[plaintext.Length + 16];
            int written = _transport!.WriteMessage(plaintext, buf);
            return buf[..written];
        }

        public byte[] DecryptFrame(byte[] ciphertext)
        {
            var buf = new byte[ciphertext.Length];
            int len = _transport!.ReadMessage(ciphertext, buf);
            return buf[..len];
        }

        public IEnumerable<byte[]> EncryptFragmented(byte[] appMessage)
        {
            byte origType = appMessage[0];
            ReadOnlyMemory<byte> remaining = appMessage.AsMemory(1);
            bool first = true;
            while (true)
            {
                int headerLen = first ? 2 : 1;
                int chunkLen = Math.Min(remaining.Length, NoiseConstants.MaxTransportPlaintext - headerLen);
                bool isLast = chunkLen == remaining.Length;

                var fragment = new byte[headerLen + chunkLen];
                fragment[0] = isLast ? NoiseConstants.MessageTypeFragmentEnd : NoiseConstants.MessageTypeFragmentMore;
                if (first)
                    fragment[1] = origType;
                remaining[..chunkLen].CopyTo(fragment.AsMemory(headerLen));
                yield return EncryptFrame(fragment);

                if (isLast)
                    yield break;
                remaining = remaining[chunkLen..];
                first = false;
            }
        }

        public byte[] DecryptAndReassemble(IEnumerable<byte[]> frames)
        {
            using var assembled = new MemoryStream();
            byte? origType = null;
            foreach (var frame in frames)
            {
                byte[] plaintext = DecryptFrame(frame);
                if (origType is null && plaintext[0] is not (NoiseConstants.MessageTypeFragmentMore or NoiseConstants.MessageTypeFragmentEnd))
                {
                    return plaintext;
                }

                if (origType is null)
                {
                    origType = plaintext[1];
                    assembled.Write(plaintext.AsSpan(2));
                }
                else
                {
                    assembled.Write(plaintext.AsSpan(1));
                }
            }

            return [origType!.Value, .. assembled.ToArray()];
        }
    }
}
