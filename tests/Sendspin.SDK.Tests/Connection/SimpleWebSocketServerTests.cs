using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Connection;

namespace Sendspin.SDK.Tests.Connection;

[Collection("RealSockets")]
public class SimpleWebSocketServerTests : IAsyncDisposable
{
    private readonly SimpleWebSocketServer _server = new();

    [Fact]
    public async Task Server_AcceptsWebSocketConnection()
    {
        _server.Start(0); // port 0 = OS assigns a random available port

        var connected = new TaskCompletionSource<WebSocketClientConnection>();
        _server.ClientConnected += (s, c) => connected.TrySetResult(c);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}/sendspin"), CancellationToken.None);

        var serverConn = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(serverConn);
        Assert.Equal("/sendspin", serverConn.Path);
        Assert.Equal(WebSocketState.Open, client.State);

        await serverConn.DisposeAsync();
    }

    [Fact]
    public async Task Server_SendsAndReceivesTextMessages()
    {
        _server.Start(0);

        var connected = new TaskCompletionSource<WebSocketClientConnection>();
        _server.ClientConnected += (s, c) => connected.TrySetResult(c);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}/test"), CancellationToken.None);

        var serverConn = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Client sends, server receives
        var received = new TaskCompletionSource<string>();
        serverConn.OnText = data => received.TrySetResult(System.Text.Encoding.UTF8.GetString(data));

        var msgBytes = System.Text.Encoding.UTF8.GetBytes("hello from client");
        await client.SendAsync(msgBytes, WebSocketMessageType.Text, true, CancellationToken.None);

        var text = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("hello from client", text);

        // Server sends, client receives
        await serverConn.SendAsync("hello from server");

        var buffer = new byte[1024];
        var result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        var response = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
        Assert.Equal("hello from server", response);

        await serverConn.DisposeAsync();
    }

    [Fact]
    public async Task Server_SendsAndReceivesBinaryMessages()
    {
        _server.Start(0);

        var connected = new TaskCompletionSource<WebSocketClientConnection>();
        _server.ClientConnected += (s, c) => connected.TrySetResult(c);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}/test"), CancellationToken.None);

        var serverConn = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var received = new TaskCompletionSource<byte[]>();
        serverConn.OnBinary = data => received.TrySetResult(data);

        var payload = new byte[] { 0x04, 0x00, 0x01, 0x02, 0x03 };
        await client.SendAsync(payload, WebSocketMessageType.Binary, true, CancellationToken.None);

        var data = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(payload, data);

        await serverConn.DisposeAsync();
    }

    [Fact]
    public async Task Server_RaisesOnClose_WhenClientDisconnects()
    {
        _server.Start(0);

        var connected = new TaskCompletionSource<WebSocketClientConnection>();
        _server.ClientConnected += (s, c) => connected.TrySetResult(c);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}/test"), CancellationToken.None);

        var serverConn = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Captures the status rather than just the fact of a close: the receive loop reports
        // the peer's status only from the Close-frame site and null everywhere else, and
        // IncomingConnection's legacy-server classification is keyed off that distinction
        // (#97). Nothing else would notice this site regressing to null.
        var closed = new TaskCompletionSource<WebSocketCloseStatus?>();
        serverConn.OnClose = status => closed.TrySetResult(status);

        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);

        var reported = await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WebSocketCloseStatus.NormalClosure, reported);

        await serverConn.DisposeAsync();
    }

    [Fact]
    public async Task Connection_Dispose_ClosesUnderlyingSocket()
    {
        // After disposing the server-side connection, the client should
        // detect that the peer closed — proving the TcpClient is disposed.
        _server.Start(0);

        var connected = new TaskCompletionSource<WebSocketClientConnection>();
        _server.ClientConnected += (s, c) => connected.TrySetResult(c);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}/test"), CancellationToken.None);

        var serverConn = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WebSocketState.Open, client.State);

        // Dispose the server-side connection (WebSocket + TcpClient)
        await serverConn.DisposeAsync();

        // The client should detect the socket was torn down. This manifests as
        // either a close message or a WebSocketException (abrupt TCP teardown).
        var buffer = new byte[128];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        try
        {
            var result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        }
        catch (WebSocketException)
        {
            // Expected — server tore down the TCP connection without a graceful
            // WebSocket close handshake, proving the TcpClient was disposed.
        }

        Assert.NotEqual(WebSocketState.Open, client.State);
    }

    [Fact]
    public async Task IncomingConnection_Dispose_ReleasesUnderlyingSocket()
    {
        // Regression test for #143's review: IncomingConnection.DisconnectAsync sends a Close
        // frame, but that alone doesn't release the WebSocketClientConnection it wraps — a peer
        // could already detect that close frame without the underlying socket ever being
        // disposed. Never calling StartAsync keeps IncomingConnection in its initial
        // Disconnected/!_isOpen state, so DisposeAsync's internal DisconnectAsync short-circuits
        // without touching the wire, isolating the one line under test: that DisposeAsync also
        // disposes the wrapped WebSocketClientConnection.
        _server.Start(0);

        var connected = new TaskCompletionSource<WebSocketClientConnection>();
        _server.ClientConnected += (s, c) => connected.TrySetResult(c);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}/test"), CancellationToken.None);

        var serverConn = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WebSocketState.Open, client.State);

        var incoming = new IncomingConnection(
            NullLogger<IncomingConnection>.Instance,
            serverConn,
            new StubFraming());

        await incoming.DisposeAsync();

        // Same assertion as Connection_Dispose_ClosesUnderlyingSocket: the client should detect
        // the socket was torn down, as either a close message or a WebSocketException (abrupt
        // TCP teardown). No Close frame is sent on this path, so an abrupt teardown is expected.
        var buffer = new byte[128];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        try
        {
            var result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        }
        catch (WebSocketException)
        {
            // Expected — no graceful WebSocket close handshake occurred on this path.
        }

        Assert.NotEqual(WebSocketState.Open, client.State);
    }

    [Fact]
    public async Task IncomingConnection_DisposeAfterDisconnect_StillReleasesUnderlyingSocket()
    {
        // Regression test for #143's eviction-path review: SendSpinHostService.DisconnectExistingAsync
        // now calls DisconnectAsync(reason) — to send the arbitration-specific goodbye — and then
        // DisposeAsync(), to actually release the socket. This reproduces that exact sequence
        // directly on IncomingConnection.
        //
        // A real DisconnectAsync send would leave the peer having already received a Close frame,
        // and a WebSocket client's SendAsync/ReceiveAsync after that point is governed by its own
        // local state machine rather than genuinely probing the connection (verified empirically:
        // a write from the peer after receiving a Close throws WebSocketException regardless of
        // whether the host disposed its side), so that can't distinguish "released" from "merely
        // stopped reading". StubFraming.ThrowOnEncodeText makes the goodbye send fail and get
        // swallowed by DisconnectAsync's own catch, so nothing reaches the wire — DisconnectAsync
        // still flips the connection to disconnected via its finally block. That isolates exactly
        // what's under test: does DisposeAsync's unconditional socket-dispose call still run after
        // a prior DisconnectAsync, the same way IncomingConnection_Dispose_ReleasesUnderlyingSocket
        // proves it runs from a fresh connection.
        _server.Start(0);

        var connected = new TaskCompletionSource<WebSocketClientConnection>();
        _server.ClientConnected += (s, c) => connected.TrySetResult(c);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}/test"), CancellationToken.None);

        var serverConn = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WebSocketState.Open, client.State);

        var incoming = new IncomingConnection(
            NullLogger<IncomingConnection>.Instance,
            serverConn,
            new StubFraming { ThrowOnEncodeText = true });

        await incoming.StartAsync();
        await incoming.DisconnectAsync("evicted"); // send fails and is swallowed; nothing sent
        await incoming.DisposeAsync();

        // Same assertion as IncomingConnection_Dispose_ReleasesUnderlyingSocket: since nothing
        // was sent, an abrupt teardown (or, less likely, a synthesized Close) proves the socket
        // was actually released rather than merely left open with nobody reading it.
        var buffer = new byte[128];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        try
        {
            var result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        }
        catch (WebSocketException)
        {
            // Expected — no graceful WebSocket close handshake occurred on this path.
        }

        Assert.NotEqual(WebSocketState.Open, client.State);
    }

    [Fact]
    public async Task Connection_Dispose_AfterClientClose_CleansUpSocket()
    {
        // Verify the full lifecycle: client closes gracefully, then server
        // disposes — both WebSocket and TcpClient should be cleaned up.
        _server.Start(0);

        var connected = new TaskCompletionSource<WebSocketClientConnection>();
        _server.ClientConnected += (s, c) => connected.TrySetResult(c);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}/test"), CancellationToken.None);

        var serverConn = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var closed = new TaskCompletionSource<bool>();
        serverConn.OnClose = _ => closed.TrySetResult(true);

        // Client initiates graceful close
        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Server disposes — this should not throw (double-close safe)
        var ex = await Record.ExceptionAsync(() => serverConn.DisposeAsync().AsTask());
        Assert.Null(ex);
    }

    [Fact]
    public async Task Server_MultipleConnections_AllDisposedCleanly()
    {
        // Connect several clients, dispose them all, verify no exceptions.
        // Catches handle leaks that accumulate across connections.
        _server.Start(0);

        const int connectionCount = 5;
        var serverConns = new List<WebSocketClientConnection>();
        var clients = new List<ClientWebSocket>();

        var connectedCount = 0;
        var allConnected = new TaskCompletionSource<bool>();
        _server.ClientConnected += (s, c) =>
        {
            lock (serverConns)
            {
                serverConns.Add(c);
                if (++connectedCount == connectionCount)
                    allConnected.TrySetResult(true);
            }
        };

        for (var i = 0; i < connectionCount; i++)
        {
            var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}/test"), CancellationToken.None);
            clients.Add(ws);
        }

        await allConnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(connectionCount, serverConns.Count);

        // Dispose all server-side connections
        foreach (var conn in serverConns)
            await conn.DisposeAsync();

        // All clients should detect the socket was torn down
        var buffer = new byte[128];
        foreach (var ws in clients)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                Assert.Equal(WebSocketMessageType.Close, result.MessageType);
            }
            catch (WebSocketException)
            {
                // Expected — abrupt TCP teardown
            }

            Assert.NotEqual(WebSocketState.Open, ws.State);
            ws.Dispose();
        }
    }

    [Fact]
    public async Task Server_HandlesPartialHttpUpgradeReads()
    {
        // Simulate a client that sends the HTTP upgrade request in multiple
        // small TCP segments — the server must accumulate them.
        _server.Start(0);

        var connected = new TaskCompletionSource<WebSocketClientConnection>();
        _server.ClientConnected += (s, c) => connected.TrySetResult(c);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", _server.Port);
        var stream = tcp.GetStream();

        // Build a valid WebSocket upgrade request
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var request = $"GET /sendspin HTTP/1.1\r\n" +
                      $"Host: 127.0.0.1:{_server.Port}\r\n" +
                      $"Upgrade: websocket\r\n" +
                      $"Connection: Upgrade\r\n" +
                      $"Sec-WebSocket-Key: {key}\r\n" +
                      $"Sec-WebSocket-Version: 13\r\n" +
                      $"\r\n";

        var bytes = Encoding.UTF8.GetBytes(request);

        // Send in small chunks to simulate partial TCP segments
        const int chunkSize = 20;
        for (var i = 0; i < bytes.Length; i += chunkSize)
        {
            var len = Math.Min(chunkSize, bytes.Length - i);
            await stream.WriteAsync(bytes.AsMemory(i, len));
            await Task.Delay(10); // Give the OS time to deliver each segment separately
        }

        // Server should still accept the connection
        var serverConn = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(serverConn);
        Assert.Equal("/sendspin", serverConn.Path);

        await serverConn.DisposeAsync();
    }

    [Fact]
    public async Task Server_SendsKeepAliveFrames_ToASilentPeer()
    {
        // WebSocketCreationOptions.KeepAliveInterval defaults to TimeSpan.Zero — keep-alive
        // off — unlike ClientWebSocket, so an accepted connection used to send nothing at all
        // and a peer that died without a FIN/RST was only noticed by the OS TCP timeout.
        await using var server = new SimpleWebSocketServer(
            logger: null,
            connectionOptions: new ConnectionOptions { KeepAliveIntervalMs = 100 });
        server.Start(0);

        var connected = new TaskCompletionSource<WebSocketClientConnection>();
        server.ClientConnected += (s, c) => connected.TrySetResult(c);

        using var peer = await ConnectSilentPeerAsync(server.Port);
        var serverConn = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Ping (0x9) when a keep-alive timeout is configured, unsolicited Pong (0xA) otherwise —
        // a runtime detail. That a frame arrives at all is the behaviour under test.
        var opcode = await ReadNextFrameOpcodeAsync(peer.GetStream(), TimeSpan.FromSeconds(5));
        Assert.True(
            opcode is 0x9 or 0xA,
            $"Expected a keep-alive Ping or Pong frame; got {(opcode is null ? "nothing" : $"opcode 0x{opcode:X}")}");

        await serverConn.DisposeAsync();
    }

#if NET9_0_OR_GREATER
    [Fact]
    public async Task Server_AbortsTheConnection_WhenASilentPeerMissesTheKeepAliveTimeout()
    {
        // net9+ only, and that is a shipped difference rather than a test detail:
        // WebSocketCreationOptions.KeepAliveTimeout does not exist on net8.0, where a half-open
        // peer is still detected only by the OS TCP timeout. SimpleWebSocketServer says so in
        // its #else branch, mirroring SendspinConnection on the dial path.
        await using var server = new SimpleWebSocketServer(
            logger: null,
            connectionOptions: new ConnectionOptions { KeepAliveIntervalMs = 250, KeepAliveTimeoutMs = 250 });
        server.Start(0);

        var connected = new TaskCompletionSource<WebSocketClientConnection>();
        server.ClientConnected += (s, c) => connected.TrySetResult(c);

        using var peer = await ConnectSilentPeerAsync(server.Port);
        var serverConn = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The abort surfaces as an OperationCanceledException("Aborted") out of ReceiveAsync, so
        // it reaches OnError rather than OnClose. Either ends the connection —
        // IncomingConnection maps both to Disconnected, which is what makes the host raise
        // ServerDisconnected — and which one it is is a runtime detail, so accept both. With
        // keep-alive off, the peer's silence is invisible and this never completes.
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        serverConn.OnClose = _ => ended.TrySetResult();
        serverConn.OnError = _ => ended.TrySetResult();

        await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await serverConn.DisposeAsync();
    }
#endif

    [Fact]
    public async Task Server_RejectsOversizedHttpHeaders()
    {
        // A client sending more than MaxHttpHeaderSize bytes without a \r\n\r\n
        // terminator should be rejected, not cause unbounded reads.
        _server.Start(0);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", _server.Port);
        var stream = tcp.GetStream();

        // Send 9KB of junk (exceeds 8KB limit) with no header terminator
        var junk = new byte[9000];
        Array.Fill(junk, (byte)'X');
        await stream.WriteAsync(junk);

        // The server should reject the connection — either by closing the socket
        // gracefully (0 bytes), sending a 400, or resetting the connection.
        var buffer = new byte[128];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        try
        {
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(), cts.Token);
            Assert.True(bytesRead == 0 || Encoding.UTF8.GetString(buffer, 0, bytesRead).Contains("400"));
        }
        catch (IOException)
        {
            // Connection reset by server — expected
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
    }

    /// <summary>
    /// Completes a raw WebSocket upgrade and then stays silent, never answering a keep-alive —
    /// the half-open peer. The returned client's stream carries the server's frames only: the
    /// 101 response is consumed exactly, byte by byte, so nothing following it is swallowed.
    /// </summary>
    private static async Task<TcpClient> ConnectSilentPeerAsync(int port)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port);
        var stream = tcp.GetStream();

        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var request = $"GET /sendspin HTTP/1.1\r\n" +
                      $"Host: 127.0.0.1:{port}\r\n" +
                      $"Upgrade: websocket\r\n" +
                      $"Connection: Upgrade\r\n" +
                      $"Sec-WebSocket-Key: {key}\r\n" +
                      $"Sec-WebSocket-Version: 13\r\n" +
                      $"\r\n";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(request));

        var terminator = "\r\n\r\n"u8.ToArray();
        var response = new List<byte>();
        var one = new byte[1];
        while (!response.TakeLast(terminator.Length).SequenceEqual(terminator))
        {
            var read = await stream.ReadAsync(one.AsMemory());
            Assert.Equal(1, read);
            response.Add(one[0]);
        }

        return tcp;
    }

    /// <summary>
    /// The opcode of the next frame the server sends, or null if nothing arrives within the
    /// bound — which is what keep-alive being disabled looks like from the wire.
    /// </summary>
    private static async Task<int?> ReadNextFrameOpcodeAsync(NetworkStream stream, TimeSpan within)
    {
        var firstByte = new byte[1];
        using var cts = new CancellationTokenSource(within);

        try
        {
            var read = await stream.ReadAsync(firstByte.AsMemory(), cts.Token);
            return read == 0 ? null : firstByte[0] & 0x0F;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
