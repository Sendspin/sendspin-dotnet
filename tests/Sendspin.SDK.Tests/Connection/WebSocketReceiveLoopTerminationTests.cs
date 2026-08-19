using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading.Channels;
using Sendspin.SDK.Connection;

namespace Sendspin.SDK.Tests.Connection;

/// <summary>
/// However the receive loop ends, it owes its subscriber exactly one terminal callback.
/// <see cref="IncomingConnection"/> only leaves <c>Connected</c> when <c>OnClose</c> or
/// <c>OnError</c> fires, so an exit that reports nothing leaves the host still holding the
/// connection and its arbitration slot — the symptom #208 set out to remove — and a second
/// report is what the state-based fallback it replaced was guarding against.
/// </summary>
public class WebSocketReceiveLoopTerminationTests
{
    [Fact]
    public async Task AbortWhileAMessageHandlerHasControl_StillReportsTheClose()
    {
        // The keep-alive timer aborts the socket while control is inside OnText rather than
        // inside ReceiveAsync — reachable on the accept path since it gained a keep-alive
        // timeout (#217), when a peer keeps sending data frames while its pongs are overdue.
        // Nothing throws, so the loop ends at its while condition, and the fallback that keyed
        // off the socket state read Aborted and stayed silent.
        var fake = new ScriptedWebSocket();
        await using var socket = new WebSocketClientConnection(
            new TcpClient(), fake, IPAddress.Loopback, 8928, "/sendspin");

        var ended = new TaskCompletionSource<WebSocketCloseStatus?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int closeCount = 0;
        socket.OnText = _ => fake.Abort();
        socket.OnClose = status =>
        {
            Interlocked.Increment(ref closeCount);
            ended.TrySetResult(status);
        };
        socket.OnError = ex => ended.TrySetException(ex);

        socket.StartReceiving();
        fake.Deliver(WebSocketMessageType.Text, "{}"u8.ToArray());

        // Statusless: an abort produces no Close frame, so there is nothing to report but the
        // fact of the end.
        Assert.Null(await ended.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        // Disposing awaits the loop, so the count below covers the whole of its lifetime rather
        // than whatever had happened by the time the callback woke this thread.
        await socket.DisposeAsync();
        Assert.Equal(1, closeCount);
    }

    [Fact]
    public async Task PeerCloseWhoseHandshakeCannotComplete_IsReportedOnce_WithThePeersStatus()
    {
        // The peer sends its Close frame and goes away before we can answer it, so
        // CloseOutputAsync fails and the socket is left in CloseReceived. That is neither
        // Closed nor Aborted, so the old state-based fallback fired a second, statusless
        // OnClose on top of the one the Close frame had already reported — and a subscriber
        // that classifies a close on its status (IncomingConnection does, #97) would have seen
        // the null one last.
        var fake = new ScriptedWebSocket { CloseOutputThrows = true };
        await using var socket = new WebSocketClientConnection(
            new TcpClient(), fake, IPAddress.Loopback, 8928, "/sendspin");

        var ended = new TaskCompletionSource<WebSocketCloseStatus?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var statuses = new List<WebSocketCloseStatus?>();
        socket.OnClose = status =>
        {
            lock (statuses)
            {
                statuses.Add(status);
            }

            ended.TrySetResult(status);
        };
        socket.OnError = ex => ended.TrySetException(ex);

        socket.StartReceiving();
        fake.DeliverClose(WebSocketCloseStatus.NormalClosure);

        await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // As above: dispose first so the loop, including its finally, has finished.
        await socket.DisposeAsync();

        lock (statuses)
        {
            Assert.Equal(WebSocketCloseStatus.NormalClosure, Assert.Single(statuses));
        }
    }

    /// <summary>
    /// Fake <see cref="WebSocket"/> that hands queued frames to a real receive loop, so the
    /// loop's own exit paths are what is under test. Sends go nowhere: nothing here asserts on
    /// them.
    /// </summary>
    private sealed class ScriptedWebSocket : WebSocket
    {
        private readonly Channel<(WebSocketMessageType Type, byte[] Data, WebSocketCloseStatus? Status)> _inbound =
            Channel.CreateUnbounded<(WebSocketMessageType, byte[], WebSocketCloseStatus?)>();

        private WebSocketState _state = WebSocketState.Open;

        /// <summary>Fails the closing handshake the way a peer that has already gone does.</summary>
        public bool CloseOutputThrows { get; init; }

        public override WebSocketState State => _state;

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override string? SubProtocol => null;

        /// <summary>Queues a data frame for the receive loop to pick up.</summary>
        public void Deliver(WebSocketMessageType type, byte[] data) => _inbound.Writer.TryWrite((type, data, null));

        /// <summary>Queues the peer's Close frame, carrying a status the loop can report.</summary>
        public void DeliverClose(WebSocketCloseStatus status) =>
            _inbound.Writer.TryWrite((WebSocketMessageType.Close, Array.Empty<byte>(), status));

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            var (type, data, status) = await _inbound.Reader.ReadAsync(cancellationToken);

            if (type == WebSocketMessageType.Close)
            {
                _state = WebSocketState.CloseReceived;
                return new WebSocketReceiveResult(0, type, true, status, null);
            }

            data.AsSpan().CopyTo(buffer.AsSpan());
            return new WebSocketReceiveResult(data.Length, type, true);
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken) => Task.CompletedTask;

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
            if (CloseOutputThrows)
            {
                throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
            }

            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override void Dispose() => _state = WebSocketState.Closed;
    }
}
