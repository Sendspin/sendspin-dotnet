using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Noise;

namespace Sendspin.SDK.Tests.Connection;

/// <summary>
/// Verifies that connection drops which do NOT surface as a ReceiveAsync exception
/// still drive the client into the reconnect path (windowsSpin issue #1).
/// </summary>
[Collection("RealSockets")]
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

        // The subject here is a restart mid-session, so the framing must already be in
        // transport mode. A NoiseWireFraming against this loopback server never gets there,
        // which would make the server's normal-closure close the legacy-server signature
        // (a permanent, deliberately un-retried failure) instead of the mid-session drop
        // this test is about. StubFraming is transport-ready, so the close is a drop.
        await using var connection = new SendspinConnection(
            NullLogger<SendspinConnection>.Instance,
            new ConnectionOptions { ReconnectDelayMs = 100, AutoReconnect = true },
            new StubFraming());

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

        // Unlike the other reconnect tests, this one calls DisconnectAsync, which sends a
        // client/goodbye through the framing before closing. A NoiseWireFraming against this
        // loopback server never reaches transport mode, so EncodeText would throw and the
        // graceful WebSocket close (and the race it sets up against the receive loop) would
        // never run — masking the ConnectionState.Disconnecting guard this test exists to
        // cover. StubFraming stays transport-ready so the disconnect path runs as written.
        await using var connection = new SendspinConnection(
            NullLogger<SendspinConnection>.Instance,
            new ConnectionOptions { ReconnectDelayMs = 100, AutoReconnect = true },
            new StubFraming());

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

#if NET9_0_OR_GREATER
    [Fact]
    public async Task HalfOpenConnection_DrivesReconnect()
    {
        // A peer that completes the WebSocket handshake but then never answers a PING
        // (frozen container / network drop with no TCP FIN). On net9+ the keep-alive
        // timeout aborts ReceiveAsync; the client must treat that as a lost connection.
        //
        // net9+ only, and that is a real shipped difference rather than a test detail:
        // ClientWebSocketOptions.KeepAliveTimeout does not exist on net8.0, so a net8.0
        // consumer detects a half-open socket only when the OS TCP timeout fires (minutes).
        // SendspinConnection says so in its #else branch. Running the suite on both frameworks
        // is what makes that visible instead of implied (#155).
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
            },
            new NoiseWireFraming(SendspinIdentity.Generate()));

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
#endif

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

        // A crash/container kill takes down an established session, so — as in
        // CleanServerClose_DrivesReconnect — the framing must be in transport mode. With a
        // NoiseWireFraming the drop would instead be an ambiguous mid-handshake failure and
        // redial on HandshakeFailureBackoffMs (30s), well past this test's 10s window.
        await using var connection = new SendspinConnection(
            NullLogger<SendspinConnection>.Instance,
            new ConnectionOptions { ReconnectDelayMs = 100, AutoReconnect = true },
            new StubFraming());

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

    /// <summary>
    /// Disposal must end the reconnect loop, including a loop already parked in its delay.
    /// </summary>
    /// <remarks>
    /// The loop delayed on <c>CancellationToken.None</c> and only re-read <c>_disposed</c> at
    /// the top of the next iteration, so disposal could not interrupt it. The parked task
    /// outlived the connection and woke up one delay later — up to the full 30s handshake
    /// backoff — to dial a port nothing was listening on. Harmless in production, but in a test
    /// run it is cross-test background noise and a latent flake source (#98 item 5).
    /// </remarks>
    [Fact]
    public async Task Disposal_EndsAReconnectLoopParkedInItsDelay()
    {
        _server.Start(0);

        var firstConnection = new TaskCompletionSource<WebSocketClientConnection>();
        var dials = 0;
        _server.ClientConnected += (_, c) =>
        {
            if (Interlocked.Increment(ref dials) == 1)
                firstConnection.TrySetResult(c);
        };

        // Long enough that the loop is certainly still parked when disposal lands, short enough
        // that waiting it out below stays quick. Transport-ready framing, as in the tests above,
        // so the drop takes the ordinary socket schedule rather than the handshake backoff.
        var connection = new SendspinConnection(
            NullLogger<SendspinConnection>.Instance,
            new ConnectionOptions { ReconnectDelayMs = 1500, AutoReconnect = true },
            new StubFraming());

        var reconnecting = new TaskCompletionSource<bool>();
        connection.StateChanged += (_, e) =>
        {
            if (e.NewState == ConnectionState.Reconnecting)
                reconnecting.TrySetResult(true);
        };

        await connection.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}/sendspin"));
        var serverConn = await firstConnection.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await serverConn.DisposeAsync();

        Assert.True(await reconnecting.Task.WaitAsync(TimeSpan.FromSeconds(10)),
            "Client should enter Reconnecting after an abrupt server-side socket teardown");

        // Positive control on the setup: exactly the one original dial so far, which is what
        // makes the count below meaningful. Were the redial already spent, this test could pass
        // without disposal cancelling anything.
        Assert.Equal(1, Volatile.Read(ref dials));

        await connection.DisposeAsync();

        // Well past the 1500ms the parked delay had left to run.
        await Task.Delay(2500);

        Assert.Equal(1, Volatile.Read(ref dials));
        Assert.Equal(ConnectionState.Disconnected, connection.State);
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
