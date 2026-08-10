using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Noise;

namespace Sendspin.SDK.Tests.Connection;

/// <summary>
/// The #124 contract: on the listen path -- the direction Music Assistant dials -- the
/// Noise prologue binds the bytes the socket received, not a re-encoding of them.
///
/// This drives the real <see cref="WebSocketClientConnection.StartReceiving"/> loop rather
/// than invoking the socket callbacks directly, because the receive loop is where the
/// decode used to happen. A test that called the callback would prove the callback carries
/// bytes and nothing about whether the socket still discards them.
/// </summary>
public class ListenerPrologueFidelityTests
{
    [Fact]
    public async Task ListenPath_ServerInitCarryingInvalidUtf8_BindsThePrologueToTheReceivedBytes()
    {
        var identity = SendspinIdentity.Generate();
        var framing = new NoiseWireFraming(identity);
        var server = new TestNoiseServer(identity.PublicKey, NoiseConstants.SentinelPsk.ToArray());

        var fake = new ReceivingWebSocket();
        await using var socket = new WebSocketClientConnection(
            new TcpClient(), fake, IPAddress.Loopback, 8928, "/sendspin");
        await using var incoming = new IncomingConnection(
            NullLogger<IncomingConnection>.Instance, socket, framing);

        socket.StartReceiving();
        await incoming.StartAsync();

        byte[] clientInitBytes = Assert.Single(fake.TextFrames());

        // A server/init whose payload carries 0xFF, which is not a legal lead byte in any
        // UTF-8 sequence. Decoding replaces it with U+FFFD, and re-encoding that emits three
        // bytes (EF BF BD) -- so a prologue built from a decoded string cannot equal one
        // built from the bytes the socket received. The byte sits in a member the framing
        // ignores, so the message still parses either way: the handshake is what diverges,
        // not the JSON.
        byte[] serverInitBytes =
        [
            .. Encoding.UTF8.GetBytes(
                "{\"type\":\"server/init\",\"payload\":{\"server_id\":\"" + server.ServerId +
                "\",\"version\":" + NoiseConstants.ProtocolVersion + ",\"note\":\""),
            0xFF,
            .. Encoding.UTF8.GetBytes("\"}}"),
        ];
        fake.Deliver(WebSocketMessageType.Text, serverInitBytes);

        // Byte concatenation of both init payloads exactly as they crossed the wire. Driving
        // the initiator with this prologue makes the assertion cryptographic rather than a
        // string comparison: if the framing computed anything else, the transcript hashes
        // differ and message 1 fails to authenticate.
        byte[] expectedPrologue =
        [
            .. clientInitBytes,
            .. serverInitBytes,
        ];
        string msg1Text = server.RespondWithPrologue(expectedPrologue);
        fake.Deliver(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(msg1Text));

        await WaitForReplyOrCloseAsync(fake, incoming);

        Assert.Equal(ConnectionState.Handshaking, incoming.State);
        server.CompleteHandshake(Encoding.UTF8.GetString(fake.TextFrames()[1]));
        Assert.True(framing.IsTransportReady);
    }

    /// <summary>
    /// Waits for the handshake reply, but gives up as soon as the connection closes: a
    /// prologue mismatch surfaces as a framing fatal, which disconnects rather than
    /// replying, and waiting out the full timeout for that would only slow the failure down.
    /// </summary>
    private static async Task WaitForReplyOrCloseAsync(ReceivingWebSocket fake, IncomingConnection incoming)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (fake.TextFrames().Length < 2 && incoming.State != ConnectionState.Disconnected)
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the noise handshake reply");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Fake <see cref="WebSocket"/> that serves queued inbound frames to a real receive
    /// loop and records sends in wire order. Unlike the callback-driven fakes elsewhere in
    /// this suite, this one exists to exercise
    /// <see cref="WebSocketClientConnection.StartReceiving"/> itself.
    /// </summary>
    private sealed class ReceivingWebSocket : WebSocket
    {
        private readonly Channel<(WebSocketMessageType Type, byte[] Data)> _inbound =
            Channel.CreateUnbounded<(WebSocketMessageType, byte[])>();

        private readonly List<(WebSocketMessageType Type, byte[] Data)> _sent =
            new List<(WebSocketMessageType, byte[])>();

        private (WebSocketMessageType Type, byte[] Data)? _current;
        private int _offset;
        private WebSocketState _state = WebSocketState.Open;

        public override WebSocketState State => _state;

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override string? SubProtocol => null;

        /// <summary>Queues a frame for the receive loop to pick up.</summary>
        public void Deliver(WebSocketMessageType type, byte[] data) => _inbound.Writer.TryWrite((type, data));

        /// <summary>Payload bytes of every text frame sent, in wire order.</summary>
        public byte[][] TextFrames()
        {
            lock (_sent)
            {
                return _sent.Where(f => f.Type == WebSocketMessageType.Text).Select(f => f.Data).ToArray();
            }
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            _current ??= await _inbound.Reader.ReadAsync(cancellationToken);

            var (type, data) = _current.Value;
            int count = Math.Min(buffer.Count, data.Length - _offset);
            data.AsSpan(_offset, count).CopyTo(buffer.AsSpan());
            _offset += count;

            bool endOfMessage = _offset == data.Length;
            if (endOfMessage)
            {
                _current = null;
                _offset = 0;
            }

            return new WebSocketReceiveResult(count, type, endOfMessage);
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

        public override void Dispose() => _state = WebSocketState.Closed;
    }
}
