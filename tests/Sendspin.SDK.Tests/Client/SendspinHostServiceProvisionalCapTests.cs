using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Discovery;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// #343: every upgraded connection got a whole client for the 30 s provisional window, with no
/// limit on how many were in flight. connection.md: "Clients MAY cap how many provisional
/// connections they hold at once, rejecting further incoming connections as if they were lower
/// priority."
/// </summary>
[Collection("RealSockets")]
public class SendspinHostServiceProvisionalCapTests
{
    [Fact]
    public async Task ConnectionsBeyondTheCap_AreClosedAtOnce_AndTheHeldOnesAreKept()
    {
        await using var host = new SendspinHostService(
            NullLoggerFactory.Instance,
            new SendspinClientOptions { Identity = SendspinIdentity.Generate() },
            listenerOptions: new ListenerOptions { Port = 0 },
            advertiserOptions: new AdvertiserOptions { Enabled = false });
        await host.StartAsync();

        var uri = new Uri($"ws://127.0.0.1:{host.ListeningPort}/sendspin");
        var held = new List<ClientWebSocket>();
        try
        {
            // Servers that upgrade and then say nothing: provisional until the window closes.
            // Each is read up to the client/init the host sends once it has taken the
            // connection on, so all of them are counted before the next one dials.
            for (var i = 0; i < SendspinHostService.MaxProvisionalConnections; i++)
            {
                var peer = new ClientWebSocket();
                held.Add(peer);
                await peer.ConnectAsync(uri, CancellationToken.None);
                Assert.Equal(WebSocketMessageType.Text, (await ReceiveAsync(peer)).MessageType);
            }

            using var excess = new ClientWebSocket();
            await excess.ConnectAsync(uri, CancellationToken.None);

            // Closed without a client/init, well inside the 30 s it used to be held for.
            await Assert.ThrowsAnyAsync<WebSocketException>(() => ReceiveAsync(excess));

            Assert.All(held, peer => Assert.Equal(WebSocketState.Open, peer.State));
        }
        finally
        {
            await host.StopAsync();
            held.ForEach(peer => peer.Dispose());
        }
    }

    private static async Task<WebSocketReceiveResult> ReceiveAsync(ClientWebSocket socket)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await socket.ReceiveAsync(new ArraySegment<byte>(new byte[4096]), cts.Token);
    }
}
