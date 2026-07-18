using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
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
        serverConn.OnMessage = msg => received.TrySetResult(msg);

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

        var closed = new TaskCompletionSource<bool>();
        serverConn.OnClose = () => closed.TrySetResult(true);

        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);

        var wasClosed = await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(wasClosed);

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
        serverConn.OnClose = () => closed.TrySetResult(true);

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
}
