using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Connection.Noise.Pairing;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Connection;

/// <summary>
/// The #127 gated static-pairing code flow, end to end over a real Noise session: a real
/// <see cref="IncomingConnection"/> + <see cref="NoiseWireFraming"/> completes a genuine
/// handshake with <see cref="TestNoiseServer"/> -- the same harness shape as
/// <see cref="RehandshakeConcurrencyTests"/> -- then drives activation →
/// <c>client/pair-pending</c> → the pairing window opening → <c>client/pair-init</c> →
/// the CPace round → a persisted pairing record, with the window closed throughout except
/// for the deliberate operator gesture that opens it.
/// </summary>
/// <remarks>
/// The server side of the PAKE round is the real <see cref="CPace"/> type from the SDK, run
/// in the initiator role -- genuinely the opposite role from the client's own
/// <see cref="CPaceRole.Responder"/> use in <c>SendSpinClient.HandleServerPairAuth</c>, not a
/// hand-rolled peer that mirrors the client's own maths and would pass even if both sides
/// shared the same bug. This is the same construction the unit-level
/// <c>PairingGatingTests.PairingHarness</c> already uses over a fake connection; this test
/// reuses it unchanged, now over real encrypted wire frames.
/// </remarks>
public class PairingWindowEndToEndTests
{
    private const string StaticPairingCode = "12345678";

    [Fact]
    public async Task GatedStaticPairingCode_RunsOverARealNoiseSession_AndPersistsTheRecord()
    {
        var window = new PairingWindow();
        var store = new InMemoryPairingRecordStore();
        var (server, link, incoming, client) = await ConnectAndHandshakeAsync(window, store);
        await using var clientCleanup = client;
        await using var incomingCleanup = incoming;
        await using var linkCleanup = link;

        // Activation: static_pin is always gesture-gated, and the window starts closed, so
        // the attempt is deferred -- client/pair-pending goes out, client/pair-init must not.
        link.SendServerJson(
            """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"pairing":{"method":"static_pin"}}}""");

        var pending = await link.NextMessageAsync<ClientPairPendingMessage>();
        Assert.Equal(1, pending.Payload.PairingIndex);
        Assert.Empty(link.SentOfType<ClientPairInitMessage>());

        // The operator gesture: the window opens and the deferred attempt starts.
        window.Open();

        var init = await link.NextMessageAsync<ClientPairInitMessage>();
        Assert.Equal(1, init.Payload.PairingIndex);
        Assert.Null(init.Payload.CommitB); // static pairing code never commits to a nonce
        Assert.False(window.IsOpen, "the opening is consumed by the attempt it started");

        // The PAKE round. sid is built exactly as HandleServerPairAuth builds it (same
        // handshake hash, same pairing_index), and CPace.Start(CPaceRole.Initiator, ...) is
        // the real SDK type in the server's role -- not a reimplementation of its maths.
        byte[] sid = PairingCodes.BuildSid(server.HandshakeHash!, (uint)init.Payload.PairingIndex);
        var serverPake = CPace.Start(
            CPaceRole.Initiator, Encoding.ASCII.GetBytes(StaticPairingCode), sid, ad: PairingCodes.AdServer);

        link.SendServerMessage(new ServerPairAuthMessage
        {
            Payload = new ServerPairAuthPayload { PakeMsg1 = B64Url(serverPake.PublicShare) },
        });

        var auth = await link.NextMessageAsync<ClientPairAuthMessage>();
        serverPake.Derive(Base64UrlText.Decode(auth.Payload.PakeMsg2), PairingCodes.AdClient);

        link.SendServerMessage(new ServerPairConfirmMessage
        {
            Payload = new ServerPairConfirmPayload { ServerKc = B64Url(serverPake.Tag()) },
        });

        var confirm = await link.NextMessageAsync<ClientPairConfirmMessage>();
        Assert.True(
            serverPake.Verify(Base64UrlText.Decode(confirm.Payload.ClientKc)),
            "the client's confirmation tag must verify against an independently-run server CPace");

        await link.NextMessageAsync<ClientPairFinalizeMessage>();

        link.SendServerJson("""{"type":"server/pair-finalize","payload":{}}""");

        await WaitUntilAsync(() => store.List().Count == 1, "the pairing record to be persisted");
        var record = Assert.Single(store.List());
        Assert.Equal(PskCategory.LongTerm, record.Category);
        Assert.False(window.IsOpen, "the window must stay closed once the attempt has completed");
    }

    // --- Harness ---
    private static async Task<(
        TestNoiseServer Server,
        ServerLink Link,
        IncomingConnection Incoming,
        SendspinClientService Client)>
        ConnectAndHandshakeAsync(PairingWindow window, InMemoryPairingRecordStore store)
    {
        var identity = SendspinIdentity.Generate();
        var framing = new NoiseWireFraming(identity, new RecordPskResolver(store));
        var server = new TestNoiseServer(identity.PublicKey, NoiseConstants.SentinelPsk.ToArray());

        var fakeSocket = new RecordingWebSocket();
        var socket = new WebSocketClientConnection(
            new TcpClient(), fakeSocket, IPAddress.Loopback, 8928, "/sendspin");
        var incoming = new IncomingConnection(
            NullLogger<IncomingConnection>.Instance, socket, framing);

        var options = new SendspinClientOptions
        {
            Identity = identity,
            PairingRecordStore = store,
            Capabilities = new ClientCapabilities
            {
                PairingCodeMethods = new List<string> { "static_pin" },
                StaticPairingCode = StaticPairingCode,
            },
            PairingCodeLockoutStore = new InMemoryPairingCodeLockoutStore(),
            PairingWindow = window,
        };
        var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance, incoming, framing, options);

        await incoming.StartAsync();
        var (serverInit, msg1) = server.Respond(fakeSocket.TextFrames()[0]);
        socket.OnText!(Encoding.UTF8.GetBytes(serverInit));
        socket.OnText!(Encoding.UTF8.GetBytes(msg1));
        await WaitUntilAsync(
            () => fakeSocket.TextFrames().Length == 2, "the initial noise handshake reply");
        server.CompleteHandshake(fakeSocket.TextFrames()[1]);

        var link = new ServerLink(server, socket, fakeSocket);

        // Completes the handshake tail -- server/hello answered with client/hello -- which
        // production requires before any server/activate.
        link.SendServerJson("""{"type":"server/hello","payload":{"name":"srv"}}""");
        await link.NextMessageAsync<ClientHelloMessage>();

        return (server, link, incoming, client);
    }

    private static string B64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {because}");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Delivers server-side JSON to the client over the real encrypted transport and decodes
    /// the client's replies. Each wire frame is decrypted exactly once and cached: the Noise
    /// transport nonce is sequential, so a frame read twice would fail AEAD authentication on
    /// the second attempt.
    /// </summary>
    private sealed class ServerLink : IAsyncDisposable
    {
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

        private readonly TestNoiseServer _server;
        private readonly WebSocketClientConnection _socket;
        private readonly RecordingWebSocket _fakeSocket;
        private readonly List<IMessage> _decoded = new List<IMessage>();
        private readonly Dictionary<Type, int> _consumed = new Dictionary<Type, int>();
        private int _decryptedFrames;

        public ServerLink(TestNoiseServer server, WebSocketClientConnection socket, RecordingWebSocket fakeSocket)
        {
            _server = server;
            _socket = socket;
            _fakeSocket = fakeSocket;
        }

        /// <summary>Encrypts and delivers a raw JSON app message as if it arrived from the server.</summary>
        public void SendServerJson(string json)
        {
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            byte[] plain = new byte[jsonBytes.Length + 1];
            plain[0] = NoiseConstants.MessageTypeJsonBody;
            jsonBytes.CopyTo(plain, 1);
            _socket.OnBinary!(_server.EncryptFrame(plain));
        }

        public void SendServerMessage<T>(T message)
            where T : IMessage =>
            SendServerJson(MessageSerializer.Serialize(message));

        /// <summary>Waits for the next sent message of type T, failing the test on timeout.</summary>
        public async Task<T> NextMessageAsync<T>(TimeSpan? timeout = null)
            where T : class, IMessage
        {
            var effectiveTimeout = timeout ?? DefaultTimeout;
            var deadline = DateTime.UtcNow + effectiveTimeout;
            int skip = _consumed.GetValueOrDefault(typeof(T));
            while (true)
            {
                DrainNewFrames();
                var match = _decoded.OfType<T>().Skip(skip).FirstOrDefault();
                if (match is not null)
                {
                    _consumed[typeof(T)] = skip + 1;
                    return match;
                }

                if (DateTime.UtcNow >= deadline)
                {
                    string sentSoFar = string.Join(", ", _decoded.Select(m => m.GetType().Name));
                    Assert.Fail(
                        $"Timed out after {effectiveTimeout.TotalSeconds}s waiting for a {typeof(T).Name} " +
                        $"to be sent. Sent so far: [{sentSoFar}]");
                }

                await Task.Delay(10);
            }
        }

        /// <summary>Every message of type T sent so far.</summary>
        public IReadOnlyList<T> SentOfType<T>()
            where T : class, IMessage
        {
            DrainNewFrames();
            return _decoded.OfType<T>().ToList();
        }

        public ValueTask DisposeAsync() => _socket.DisposeAsync();

        // Dispatches on the wire "type" the same way SendSpinClient's own receive switch
        // does, rather than trusting Deserialize<T> to reject a mismatched shape -- it does
        // not, since IMessage.Type has no setter and unknown JSON members are ignored.
        private static IMessage? Decode(string json) => MessageSerializer.GetMessageType(json) switch
        {
            MessageTypes.ClientHello => MessageSerializer.Deserialize<ClientHelloMessage>(json),
            MessageTypes.ClientPairPending => MessageSerializer.Deserialize<ClientPairPendingMessage>(json),
            MessageTypes.ClientPairInit => MessageSerializer.Deserialize<ClientPairInitMessage>(json),
            MessageTypes.ClientPairAuth => MessageSerializer.Deserialize<ClientPairAuthMessage>(json),
            MessageTypes.ClientPairConfirm => MessageSerializer.Deserialize<ClientPairConfirmMessage>(json),
            MessageTypes.ClientPairFinalize => MessageSerializer.Deserialize<ClientPairFinalizeMessage>(json),
            _ => null,
        };

        private void DrainNewFrames()
        {
            var frames = _fakeSocket.BinaryFrames();
            for (; _decryptedFrames < frames.Length; _decryptedFrames++)
            {
                byte[] plain = _server.DecryptFrame(frames[_decryptedFrames]);
                if (plain[0] != NoiseConstants.MessageTypeJsonBody)
                {
                    continue; // no fragmented or raw-binary application frames in this flow
                }

                string json = Encoding.UTF8.GetString(plain.AsSpan(1));
                if (Decode(json) is { } decoded)
                {
                    _decoded.Add(decoded);
                }
            }
        }
    }

    /// <summary>
    /// Fake <see cref="WebSocket"/> that records completed sends in wire order and serves
    /// nothing on receive -- inbound frames are delivered directly through the connection's
    /// synchronous socket callbacks, exactly as <see cref="RehandshakeConcurrencyTests"/>'s
    /// GatedWebSocket does. This omits that type's concurrency gate, which this test does
    /// not need.
    /// </summary>
    private sealed class RecordingWebSocket : WebSocket
    {
        private readonly List<(WebSocketMessageType Type, byte[] Data)> _sent =
            new List<(WebSocketMessageType Type, byte[] Data)>();

        private WebSocketState _state = WebSocketState.Open;

        public override WebSocketState State => _state;

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override string? SubProtocol => null;

        public string[] TextFrames()
        {
            lock (_sent)
            {
                return _sent.Where(f => f.Type == WebSocketMessageType.Text)
                    .Select(f => Encoding.UTF8.GetString(f.Data)).ToArray();
            }
        }

        public byte[][] BinaryFrames()
        {
            lock (_sent)
            {
                return _sent.Where(f => f.Type == WebSocketMessageType.Binary)
                    .Select(f => f.Data).ToArray();
            }
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            lock (_sent)
            {
                _sent.Add((messageType, buffer.ToArray()));
            }

            return Task.CompletedTask;
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            throw new NotSupportedException("tests deliver inbound frames via the socket callbacks");

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
        }
    }
}
