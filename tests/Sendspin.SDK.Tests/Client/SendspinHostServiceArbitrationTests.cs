using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Discovery;
using Sendspin.SDK.Connection;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Loopback end-to-end coverage for multi-server arbitration: two FakeServers connect to the host's
/// real listener, and each scenario asserts which connection receives a client/goodbye and with what
/// reason — verifying the bytes on the wire, not just the decision logic.
/// </summary>
public class SendspinHostServiceArbitrationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static async Task<SendspinHostService> StartHostAsync(string? seed = null)
    {
        var host = new SendspinHostService(
            NullLoggerFactory.Instance,
            listenerOptions: new ListenerOptions { Port = 0 },
            advertiserOptions: new AdvertiserOptions { Enabled = false },
            lastPlayedServerId: seed);

        await host.StartAsync(); // prevent real network servers from racing into arbitration
        return host;
    }

    private static async Task WaitForServerConnectedAsync(SendspinHostService host, string serverId)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? s, ConnectedServerInfo info)
        {
            if (info.ServerId == serverId)
            {
                tcs.TrySetResult();
            }
        }

        host.ServerConnected += Handler;
        try
        {
            // Cover the race where it connected before we subscribed.
            if (host.ConnectedServers.Any(c => c.ServerId == serverId))
            {
                tcs.TrySetResult();
            }

            await tcs.Task.WaitAsync(Timeout);
        }
        finally
        {
            host.ServerConnected -= Handler;
        }
    }

    [Fact]
    public async Task SingleDiscoveryServer_IsAcceptedWithoutGoodbye()
    {
        await using var host = await StartHostAsync();
        await using var server = new FakeServer("srv-only", "discovery");
        await server.ConnectAsync(host.ListeningPort);
        await WaitForServerConnectedAsync(host, "srv-only");

        var reason = await server.WaitForGoodbyeAsync(TimeSpan.FromMilliseconds(750));

        Assert.Null(reason); // accepted, stayed connected
        Assert.Contains(host.ConnectedServers, c => c.ServerId == "srv-only");
    }

    [Fact]
    public async Task NewPlaybackServer_SwitchesAndSendsAnotherServerToExisting()
    {
        await using var host = await StartHostAsync();
        await using var existing = new FakeServer("srv-existing", "discovery");
        await existing.ConnectAsync(host.ListeningPort);
        await WaitForServerConnectedAsync(host, "srv-existing");

        await using var incoming = new FakeServer("srv-new", "playback");
        await incoming.ConnectAsync(host.ListeningPort);

        Assert.Equal("another_server", await existing.WaitForGoodbyeAsync(Timeout));
    }

    [Fact]
    public async Task NewDiscoveryServer_AgainstPlaybackExisting_IsRejectedWithConcurrentAttempt()
    {
        await using var host = await StartHostAsync();
        await using var existing = new FakeServer("srv-existing", "playback");
        await existing.ConnectAsync(host.ListeningPort);
        await WaitForServerConnectedAsync(host, "srv-existing");

        await using var incoming = new FakeServer("srv-new", "discovery");
        await incoming.ConnectAsync(host.ListeningPort);

        Assert.Equal("concurrent_attempt", await incoming.WaitForGoodbyeAsync(Timeout));
    }

    [Fact]
    public async Task BothDiscovery_LastPlayedNewServer_WinsTieAndExistingGetsAnotherServer()
    {
        await using var host = await StartHostAsync(seed: "srv-new");
        await using var existing = new FakeServer("srv-existing", "discovery");
        await existing.ConnectAsync(host.ListeningPort);
        await WaitForServerConnectedAsync(host, "srv-existing");

        await using var incoming = new FakeServer("srv-new", "discovery");
        await incoming.ConnectAsync(host.ListeningPort);

        Assert.Equal("another_server", await existing.WaitForGoodbyeAsync(Timeout));
    }

    [Fact]
    public async Task SameServerReconnect_SendsUserRequestToStaleConnection()
    {
        await using var host = await StartHostAsync();
        await using var first = new FakeServer("srv-1", "discovery");
        await first.ConnectAsync(host.ListeningPort);
        await WaitForServerConnectedAsync(host, "srv-1");

        await using var second = new FakeServer("srv-1", "discovery"); // same server_id reconnecting
        await second.ConnectAsync(host.ListeningPort);

        Assert.Equal("user_request", await first.WaitForGoodbyeAsync(Timeout));
    }
}
