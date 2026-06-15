using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Connection;

namespace Sendspin.SDK.Tests.Connection;

/// <summary>
/// Verifies that connection drops which do NOT surface as a ReceiveAsync exception
/// still drive the client into the reconnect path (windowsSpin issue #1).
/// </summary>
public class SendspinConnectionReconnectTests : IAsyncDisposable
{
    private readonly SimpleWebSocketServer _server = new();

    [Fact]
    public async Task CleanServerClose_DrivesReconnect()
    {
        _server.Start(0);

        var firstConnection = new TaskCompletionSource<WebSocketClientConnection>();
        var secondConnection = new TaskCompletionSource<bool>();
        var connectionCount = 0;
        _server.ClientConnected += (_, c) =>
        {
            if (Interlocked.Increment(ref connectionCount) == 1)
                firstConnection.TrySetResult(c);
            else
                secondConnection.TrySetResult(true);
        };

        await using var connection = new SendspinConnection(
            NullLogger<SendspinConnection>.Instance,
            new ConnectionOptions { ReconnectDelayMs = 100, AutoReconnect = true });

        var reconnecting = new TaskCompletionSource<bool>();
        connection.StateChanged += (_, e) =>
        {
            if (e.NewState == ConnectionState.Reconnecting)
                reconnecting.TrySetResult(true);
        };

        await connection.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}/sendspin"));
        var serverConn = await firstConnection.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Server sends a graceful WebSocket close frame (e.g. Music Assistant restart).
        // Pre-fix this hit a bare `return;` and the client went silent.
        await serverConn.CloseAsync();

        Assert.True(await reconnecting.Task.WaitAsync(TimeSpan.FromSeconds(10)),
            "Client should enter Reconnecting after a clean server close");
        Assert.True(await secondConnection.Task.WaitAsync(TimeSpan.FromSeconds(10)),
            "Client should reconnect to the still-running server");
    }

    [Fact]
    public async Task ExplicitDisconnect_DoesNotReconnect()
    {
        _server.Start(0);

        var connected = new TaskCompletionSource<WebSocketClientConnection>();
        _server.ClientConnected += (_, c) => connected.TrySetResult(c);

        await using var connection = new SendspinConnection(
            NullLogger<SendspinConnection>.Instance,
            new ConnectionOptions { ReconnectDelayMs = 100, AutoReconnect = true });

        var sawReconnecting = false;
        connection.StateChanged += (_, e) =>
        {
            if (e.NewState == ConnectionState.Reconnecting)
                sawReconnecting = true;
        };

        await connection.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}/sendspin"));
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await connection.DisconnectAsync("test");

        // Give any stray reconnect a chance to fire before asserting it didn't.
        await Task.Delay(500);

        Assert.Equal(ConnectionState.Disconnected, connection.State);
        Assert.False(sawReconnecting,
            "An explicit DisconnectAsync must not trigger the reconnect path");
    }

    [Fact]
    public async Task HalfOpenConnection_DrivesReconnect()
    {
        // A peer that completes the WebSocket handshake but then never answers a PING
        // (frozen container / network drop with no TCP FIN). On net9+ the keep-alive
        // timeout aborts ReceiveAsync; the client must treat that as a lost connection.
        using var silentServer = new SilentWebSocketServer();
        silentServer.Start();

        await using var connection = new SendspinConnection(
            NullLogger<SendspinConnection>.Instance,
            new ConnectionOptions
            {
                KeepAliveIntervalMs = 200,
                KeepAliveTimeoutMs = 200,
                ReconnectDelayMs = 100,
                AutoReconnect = true,
            });

        var sawHandshaking = false;
        var reconnecting = new TaskCompletionSource<bool>();
        connection.StateChanged += (_, e) =>
        {
            if (e.NewState == ConnectionState.Handshaking)
                sawHandshaking = true;
            if (e.NewState == ConnectionState.Reconnecting)
                reconnecting.TrySetResult(true);
        };

        await connection.ConnectAsync(new Uri($"ws://127.0.0.1:{silentServer.Port}/sendspin"));

        Assert.True(await reconnecting.Task.WaitAsync(TimeSpan.FromSeconds(5)),
            "Client should enter Reconnecting after the keep-alive timeout aborts a half-open socket");

        // The handshake must have completed first; otherwise the reconnect came from an
        // initial connect failure rather than the keep-alive abort this test exercises.
        Assert.True(sawHandshaking,
            "Client should reach Handshaking before Reconnecting (proves the abort, not a connect failure, drove it)");
    }

    [Fact]
    public async Task AbruptServerDrop_DrivesReconnect()
    {
        // Server-side socket torn down without a graceful WebSocket close (crash / container
        // kill). Surfaces as a WebSocketException out of ReceiveAsync — the pre-existing
        // reconnect path the keep-alive comment contrasts itself against. Guards it from regressing.
        _server.Start(0);

        var firstConnection = new TaskCompletionSource<WebSocketClientConnection>();
        var secondConnection = new TaskCompletionSource<bool>();
        var connectionCount = 0;
        _server.ClientConnected += (_, c) =>
        {
            if (Interlocked.Increment(ref connectionCount) == 1)
                firstConnection.TrySetResult(c);
            else
                secondConnection.TrySetResult(true);
        };

        await using var connection = new SendspinConnection(
            NullLogger<SendspinConnection>.Instance,
            new ConnectionOptions { ReconnectDelayMs = 100, AutoReconnect = true });

        var reconnecting = new TaskCompletionSource<bool>();
        connection.StateChanged += (_, e) =>
        {
            if (e.NewState == ConnectionState.Reconnecting)
                reconnecting.TrySetResult(true);
        };

        await connection.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}/sendspin"));
        var serverConn = await firstConnection.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Tear down the server side abruptly (no graceful WS close handshake).
        await serverConn.DisposeAsync();

        Assert.True(await reconnecting.Task.WaitAsync(TimeSpan.FromSeconds(10)),
            "Client should enter Reconnecting after an abrupt server-side socket teardown");
        Assert.True(await secondConnection.Task.WaitAsync(TimeSpan.FromSeconds(10)),
            "Client should reconnect to the still-running server");
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Accepts WebSocket connections, completes the opening handshake, then stays silent —
    /// never answering keep-alive PINGs — to simulate a half-open peer.
    /// </summary>
    private sealed class SilentWebSocketServer : IDisposable
    {
        private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly List<TcpClient> _clients = [];

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public void Start()
        {
            _listener.Start();
            _ = AcceptLoopAsync(_cts.Token);
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(ct);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return; // Listener stopped during teardown — the only expected exit.
                }

                lock (_clients)
                    _clients.Add(client);

                // Let any handshake fault surface as an unobserved-task exception rather than
                // be swallowed here: a silently-failed handshake would make the client reconnect
                // due to a connect failure instead of the keep-alive abort this test exercises.
                await CompleteHandshakeAsync(client.GetStream(), ct);
                // Intentionally go silent: keep the socket open, never PONG.
            }
        }

        private static async Task CompleteHandshakeAsync(NetworkStream stream, CancellationToken ct)
        {
            var request = new StringBuilder();
            var buffer = new byte[1024];
            while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer, ct);
                if (read == 0)
                {
                    if (ct.IsCancellationRequested)
                        return;
                    throw new IOException("Client closed before completing the WebSocket upgrade");
                }

                request.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }

            var key = request.ToString()
                .Split("\r\n")
                .First(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                .Split(':', 2)[1]
                .Trim();

            var accept = Convert.ToBase64String(
                SHA1.HashData(Encoding.UTF8.GetBytes(key + WebSocketGuid)));

            var response =
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(response), ct);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            lock (_clients)
            {
                foreach (var client in _clients)
                    client.Dispose();
            }

            _cts.Dispose();
        }
    }
}
