using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Discovery;
using Sendspin.SDK.Tests.Connection;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// What reaches the peer when a provisional connection never activates. The spec says such a
/// connection "is dropped" (connection.md:40) and names no goodbye reason for it, so the host
/// must close without one.
/// </summary>
/// <remarks>
/// The host used to send <c>client/goodbye</c> reason <c>handshake_timeout</c>, which is
/// outside the spec's closed set (messaging.md:426). A server cannot parse it, so per the
/// spec's fallback it reads as a silent drop from a crashed client and may auto-reconnect
/// immediately — defeating the rejection and looping.
/// </remarks>
[Collection("RealSockets")]
public class SendspinHostServiceProvisionalTimeoutTests : IAsyncDisposable
{
    private readonly SimpleWebSocketServer _server = new SimpleWebSocketServer();

    [Fact]
    public async Task Timeout_DropsTheConnection_WithoutSendingAGoodbye()
    {
        _server.Start(0);

        var accepted = new TaskCompletionSource<WebSocketClientConnection>();
        _server.ClientConnected += (s, c) => accepted.TrySetResult(c);

        // The dialing Sendspin server: it completes the WebSocket upgrade and then never sends
        // server/activate, which is exactly the provisional connection the 30 s window covers.
        using var peer = new ClientWebSocket();
        await peer.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}/sendspin"), CancellationToken.None);
        var socket = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // StubFraming is transport-ready, so a goodbye would actually encode and reach the peer
        // — a real NoiseWireFraming against a peer that never handshakes would throw on the way
        // out and hide the defect behind an unrelated failure.
        var connection = new IncomingConnection(
            NullLogger<IncomingConnection>.Instance, socket, new StubFraming());
        await connection.StartAsync();

        await using var host = NewHost();
        await using var client = NewClient(connection);

        Assert.False(await host.WaitForHandshakeAsync(
            client, connection, "test-conn", timeoutSeconds: 1));

        // A Close frame, not a Text frame carrying client/goodbye: the drop is the whole
        // message. Before the fix this was the goodbye, with an unparseable reason.
        var buffer = new byte[1024];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await peer.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);

        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(ConnectionState.Disconnected, connection.State);
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>Never started: this exercises the handshake wait, not the listener.</summary>
    private static SendspinHostService NewHost() =>
        new SendspinHostService(
            NullLoggerFactory.Instance,
            new SendspinClientOptions { Identity = SendspinIdentity.Generate() },
            advertiserOptions: new AdvertiserOptions { Enabled = false });

    /// <summary>
    /// The client the handshake wait watches for a state change. Nothing arrives on this
    /// connection, so it stays inert and only its ConnectionStateChanged event matters.
    /// </summary>
    private static SendspinClientService NewClient(IncomingConnection connection) =>
        new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection,
            new NoiseWireFraming(SendspinIdentity.Generate()),
            new SendspinClientOptions { Identity = SendspinIdentity.Generate() });
}
