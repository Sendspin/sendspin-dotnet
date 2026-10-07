using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Connection;

namespace Sendspin.SDK.Tests.Connection;

/// <summary>
/// Both receive loops accumulate a WebSocket message until its final frame, and the Noise
/// 65535-byte check only runs on the completed message. Without a bound of their own, a peer
/// that has done nothing but the unauthenticated upgrade decides how much each connection
/// buffers (#313). The loops must stop reading once a message outgrows anything a conformant
/// peer sends, and close with 1009.
/// </summary>
[Collection("RealSockets")]
public class InboundMessageSizeCapTests
{
    private const int Oversize = 1024 * 1024;

    [Fact]
    public async Task AcceptPath_OversizeMessage_IsRefusedWith1009_AndNeverDelivered()
    {
        await using var server = new SimpleWebSocketServer();
        server.Start(0);

        var delivered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errored = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ClientConnected += (_, c) =>
        {
            c.OnBinary = data => delivered.TrySetResult(data.Length);
            c.OnError = ex => errored.TrySetResult(ex);
        };

        using var peer = new ClientWebSocket();
        await peer.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/sendspin"), CancellationToken.None);

        // One message spread over continuation frames, so the bound has to hold across them
        // and not just per frame. Not awaited: once the listener stops reading, the tail of
        // this may never drain.
        using var sendCts = new CancellationTokenSource();
        var sending = SendFragmentedAsync(peer, Oversize, sendCts.Token);

        var closed = ReceiveCloseAsync(peer);
        var first = await Task.WhenAny(closed, delivered.Task).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(first == delivered.Task,
            $"A {Oversize}-byte message was buffered whole and handed to the application");
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, await closed);
        await errored.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await sendCts.CancelAsync();
        try { await sending; } catch { /* the peer's send is cut off by design */ }
    }

    [Theory]
    [InlineData(WebSocketMessageType.Binary, 65535)]  // a full Noise transport message
    [InlineData(WebSocketMessageType.Text, 87500)]    // noise/handshake carrying a 65535-byte Noise message
    public async Task AcceptPath_LargestLegitimateMessage_IsStillDelivered(WebSocketMessageType type, int size)
    {
        await using var server = new SimpleWebSocketServer();
        server.Start(0);

        var delivered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ClientConnected += (_, c) =>
        {
            c.OnBinary = data => delivered.TrySetResult(data.Length);
            c.OnText = data => delivered.TrySetResult(data.Length);
            c.OnError = ex => delivered.TrySetException(ex);
        };

        using var peer = new ClientWebSocket();
        await peer.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/sendspin"), CancellationToken.None);

        var payload = new byte[size];
        Array.Fill(payload, (byte)'a');
        await peer.SendAsync(payload, type, endOfMessage: true, CancellationToken.None);

        Assert.Equal(size, await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task DialPath_OversizeMessageBeforeTransportMode_IsRefusedWith1009_AndFailsTheHandshake()
    {
        await using var server = new SimpleWebSocketServer();
        server.Start(0);

        var accepted = new TaskCompletionSource<WebSocketClientConnection>();
        var serverSawClose = new TaskCompletionSource<WebSocketCloseStatus?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        server.ClientConnected += (_, c) =>
        {
            c.OnClose = status => serverSawClose.TrySetResult(status);
            accepted.TrySetResult(c);
        };

        await using var connection = new SendspinConnection(
            NullLogger<SendspinConnection>.Instance,
            new ConnectionOptions { AutoReconnect = true, ReconnectDelayMs = 10 },
            new StubFraming { IsTransportReady = false });

        var delivered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource<ConnectionStateChangedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connection.BinaryMessageReceived += (_, data) => delivered.TrySetResult(data.Length);
        connection.StateChanged += (_, e) =>
        {
            if (e.NewState == ConnectionState.Disconnected)
                disconnected.TrySetResult(e);
        };

        await connection.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/sendspin"));
        var serverConn = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Not awaited: the client stops reading partway through.
        var sending = serverConn.SendAsync(new byte[Oversize]);

        var first = await Task.WhenAny(disconnected.Task, delivered.Task).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(first == delivered.Task,
            $"A {Oversize}-byte message was buffered whole and handed to the application");

        // Before transport mode this is a peer that is not speaking the protocol, so it ends
        // the same way a framing fatal does there: permanently, with the classified exception.
        var ex = Assert.IsType<SendspinHandshakeException>((await disconnected.Task).Exception);
        Assert.Equal(HandshakeFailureKind.HandshakeRejected, ex.Kind);
        Assert.Equal(
            WebSocketCloseStatus.MessageTooBig,
            await serverSawClose.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        try { await sending.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* cut off by design */ }
    }

    [Fact]
    public async Task DialPath_OversizeMessageInTransportMode_Reconnects()
    {
        await using var server = new SimpleWebSocketServer();
        server.Start(0);

        var firstConnection = new TaskCompletionSource<WebSocketClientConnection>();
        var secondConnection = new TaskCompletionSource<bool>();
        var dials = 0;
        server.ClientConnected += (_, c) =>
        {
            if (Interlocked.Increment(ref dials) == 1)
                firstConnection.TrySetResult(c);
            else
                secondConnection.TrySetResult(true);
        };

        await using var connection = new SendspinConnection(
            NullLogger<SendspinConnection>.Instance,
            new ConnectionOptions { AutoReconnect = true, ReconnectDelayMs = 100 },
            new StubFraming { IsTransportReady = true });

        var delivered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.BinaryMessageReceived += (_, data) => delivered.TrySetResult(data.Length);

        await connection.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/sendspin"));
        var serverConn = await firstConnection.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var sending = serverConn.SendAsync(new byte[Oversize]);

        // An established session that desyncs recovers by redialling, as a transport-mode
        // framing fatal does.
        var first = await Task.WhenAny(secondConnection.Task, delivered.Task).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(first == delivered.Task,
            $"A {Oversize}-byte message was buffered whole and handed to the application");

        try { await sending.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* cut off by design */ }
    }

    private static async Task SendFragmentedAsync(ClientWebSocket peer, int total, CancellationToken cancellationToken)
    {
        var chunk = new byte[64 * 1024];
        for (int sent = 0; sent < total; sent += chunk.Length)
        {
            await peer.SendAsync(
                chunk,
                WebSocketMessageType.Binary,
                endOfMessage: sent + chunk.Length >= total,
                cancellationToken);
        }
    }

    private static async Task<WebSocketCloseStatus?> ReceiveCloseAsync(ClientWebSocket peer)
    {
        var buffer = new byte[1024];
        while (true)
        {
            var result = await peer.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
                return result.CloseStatus;
        }
    }
}
