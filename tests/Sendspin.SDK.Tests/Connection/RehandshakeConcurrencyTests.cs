using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Framing;
using Sendspin.SDK.Connection.Noise;

namespace Sendspin.SDK.Tests.Connection;

/// <summary>
/// The #81 concurrency contract, driven through a real <see cref="IncomingConnection"/>
/// (the listen path Music Assistant dials) with a real <see cref="NoiseWireFraming"/>:
/// a server-initiated re-handshake concurrent with application sends must put no frame
/// on the wire under mixed or wrong keys, and the re-handshake reply (Noise message 2,
/// under the OLD keys) must precede every frame encrypted under the NEW keys.
///
/// Determinism comes from a gate, not timing: the socket is a fake
/// <see cref="WebSocket"/> whose next send can be parked on a
/// <see cref="TaskCompletionSource"/> (the <c>HoldNextSend</c> pattern), so a send is
/// provably mid-flight -- holding the connection's send lock -- when the re-handshake
/// arrives on the receive path. Every frame is then verified by decrypting it with the
/// server-side transport for its position: a frame under the wrong key epoch fails AEAD
/// authentication and the test throws.
/// </summary>
public class RehandshakeConcurrencyTests
{
    [Fact]
    public async Task Rehandshake_ConcurrentApplicationSends_NeverPutNewKeyFramesBeforeTheReply()
    {
        var (framing, server, newPsk, gatedSocket, socket, incoming) = await ConnectAndHandshakeAsync();
        await using var socketCleanup = socket;
        await using var incomingCleanup = incoming;

        // A send in flight, holding the connection's send lock: its frame is already
        // encrypted under the OLD keys and its socket write is parked on the gate.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gatedSocket.HoldNextSend = gate;
        var send1 = incoming.SendBinaryAsync(new byte[] { 4, 1 });
        Assert.False(send1.IsCompleted);

        // A second send queued behind the lock before the re-handshake arrives.
        var send2 = incoming.SendBinaryAsync(new byte[] { 4, 2 });
        Assert.False(send2.IsCompleted);

        // The server-initiated re-handshake lands on the receive path while both sends
        // are in flight. The socket callbacks are synchronous, so this returning proves
        // the receive path neither blocked on the send lock nor touched the wire.
        socket.OnBinary!(server.StartRehandshake(newPsk));
        Assert.Empty(gatedSocket.BinaryFrames());

        gate.SetResult();
        await send1.WaitAsync(TimeSpan.FromSeconds(5));
        await send2.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(
            () => gatedSocket.BinaryFrames().Length == 3,
            "both sends and the re-handshake reply to reach the wire");

        // Verify the wire in order against the server's transports: every frame before
        // the reply must decrypt under the OLD keys, and every frame after it under the
        // NEW ones. A frame under the wrong (or a mixed) key fails AEAD authentication
        // here and the decrypt throws.
        var frames = gatedSocket.BinaryFrames();
        var beforeReply = new List<byte[]>();
        int replyIndex = -1;
        for (int i = 0; i < frames.Length && replyIndex < 0; i++)
        {
            byte[] plain = server.DecryptFrame(frames[i]);
            if (plain[0] == NoiseConstants.MessageTypeJsonBody)
            {
                replyIndex = i;
                server.CompleteHandshake(Encoding.UTF8.GetString(plain.AsSpan(1)));
            }
            else
            {
                beforeReply.Add(plain);
            }
        }

        Assert.True(replyIndex >= 0, "the re-handshake reply never reached the wire");

        var afterReply = new List<byte[]>();
        for (int i = replyIndex + 1; i < frames.Length; i++)
        {
            afterReply.Add(server.DecryptFrame(frames[i]));
        }

        // Both racing application messages arrived intact, each entirely in one key epoch.
        var recovered = beforeReply.Concat(afterReply).ToList();
        Assert.Equal(2, recovered.Count);
        Assert.Contains(recovered, f => f.SequenceEqual(new byte[] { 4, 1 }));
        Assert.Contains(recovered, f => f.SequenceEqual(new byte[] { 4, 2 }));

        // A send issued after the reply is on the wire uses the new keys, and the
        // session now reports the new authentication.
        await incoming.SendBinaryAsync(new byte[] { 4, 3 });
        Assert.Equal(new byte[] { 4, 3 }, server.DecryptFrame(gatedSocket.BinaryFrames()[3]));
        Assert.Equal(PskCategory.LongTerm, framing.MatchedPsk!.Category);
    }

    /// <summary>
    /// The other half of the race: an application send issued while the reply itself is
    /// mid-transmission must land entirely after it -- never between the swap and the
    /// reply -- and under the new keys, because the swap is committed in the same
    /// send-lock acquisition that transmits the reply.
    /// </summary>
    [Fact]
    public async Task Rehandshake_ApplicationSendRacingTheReply_LandsEntirelyAfterIt_UnderTheNewKeys()
    {
        var (framing, server, newPsk, gatedSocket, socket, incoming) = await ConnectAndHandshakeAsync();
        await using var socketCleanup = socket;
        await using var incomingCleanup = incoming;

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gatedSocket.HoldNextSend = gate;
        gatedSocket.SendHeld = held;

        socket.OnBinary!(server.StartRehandshake(newPsk));

        // The reply's socket write is now parked on the gate, holding the send lock.
        await held.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // An application send racing the reply queues behind the send lock...
        var appSend = incoming.SendBinaryAsync(new byte[] { 4, 9 });
        Assert.False(appSend.IsCompleted);
        Assert.Empty(gatedSocket.BinaryFrames());

        gate.SetResult();
        await appSend.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(
            () => gatedSocket.BinaryFrames().Length == 2,
            "the reply and the racing send to reach the wire");

        // ...and lands entirely after the reply, under the new keys.
        var frames = gatedSocket.BinaryFrames();
        server.CompleteRehandshake(frames[0]);
        Assert.Equal(new byte[] { 4, 9 }, server.DecryptFrame(frames[1]));
        Assert.Equal(PskCategory.LongTerm, framing.MatchedPsk!.Category);
    }

    // --- Harness ---
    private static async Task<(
        NoiseWireFraming Framing,
        TestNoiseServer Server,
        byte[] NewPsk,
        GatedWebSocket GatedSocket,
        WebSocketClientConnection Socket,
        IncomingConnection Incoming)> ConnectAndHandshakeAsync()
    {
        var identity = SendspinIdentity.Generate();
        var store = new InMemoryPairingRecordStore();
        byte[] newPsk = RandomNumberGenerator.GetBytes(32);
        store.Upsert(new PairingRecord(newPsk, PskCategory.LongTerm));
        var framing = new NoiseWireFraming(identity, new RecordPskResolver(store));
        var server = new TestNoiseServer(identity.PublicKey, NoiseConstants.SentinelPsk.ToArray());

        var gatedSocket = new GatedWebSocket();
        var socket = new WebSocketClientConnection(
            new TcpClient(), gatedSocket, IPAddress.Loopback, 8928, "/sendspin");
        var incoming = new IncomingConnection(
            NullLogger<IncomingConnection>.Instance, socket, framing);

        await incoming.StartAsync();
        var (serverInit, msg1) = server.Respond(gatedSocket.TextFrames()[0]);
        socket.OnText!(Encoding.UTF8.GetBytes(serverInit));
        socket.OnText!(Encoding.UTF8.GetBytes(msg1));
        await WaitUntilAsync(
            () => gatedSocket.TextFrames().Length == 2,
            "the initial noise handshake reply");
        server.CompleteHandshake(gatedSocket.TextFrames()[1]);

        return (framing, server, newPsk, gatedSocket, socket, incoming);
    }

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
    /// Fake <see cref="WebSocket"/> for a <see cref="WebSocketClientConnection"/>: records
    /// completed sends in wire order and can park the next send on a
    /// <see cref="TaskCompletionSource"/> so a test can interleave work while a send is
    /// provably mid-flight (holding the connection's send lock). The receive side is
    /// never started; tests deliver inbound frames by invoking the connection's
    /// synchronous socket callbacks directly, exactly as the real receive loop does.
    /// </summary>
    private sealed class GatedWebSocket : WebSocket
    {
        private readonly List<(WebSocketMessageType Type, byte[] Data)> _sent =
            new List<(WebSocketMessageType Type, byte[] Data)>();

        private WebSocketState _state = WebSocketState.Open;

        /// <summary>Parks the next send until resolved; consumed by that one send.</summary>
        public TaskCompletionSource? HoldNextSend { get; set; }

        /// <summary>Resolved when a send begins waiting on <see cref="HoldNextSend"/>.</summary>
        public TaskCompletionSource? SendHeld { get; set; }

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
            CancellationToken cancellationToken) => SendCoreAsync(buffer.ToArray(), messageType);

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

        private async Task SendCoreAsync(byte[] data, WebSocketMessageType type)
        {
            if (HoldNextSend is { } hold)
            {
                HoldNextSend = null;
                SendHeld?.TrySetResult();
                await hold.Task;
            }

            // Recorded on completion, so list order is true wire order.
            lock (_sent)
            {
                _sent.Add((type, data));
            }
        }
    }
}
