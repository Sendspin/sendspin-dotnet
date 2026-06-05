using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
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
            lastPlayedServerId: seed);

        await host.StartAsync();
        await host.StopAdvertisingAsync(); // prevent real network servers from racing into arbitration
        return host;
    }

    private static Task WaitForServerConnectedAsync(SendspinHostService host, string serverId)
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

        // Cover the race where it connected before we subscribed.
        if (host.ConnectedServers.Any(c => c.ServerId == serverId))
        {
            tcs.TrySetResult();
        }

        return tcs.Task.WaitAsync(Timeout);
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
    public async Task NewDiscoveryServer_AgainstPlaybackExisting_IsRejectedWithAnotherServer()
    {
        await using var host = await StartHostAsync();
        await using var existing = new FakeServer("srv-existing", "playback");
        await existing.ConnectAsync(host.ListeningPort);
        await WaitForServerConnectedAsync(host, "srv-existing");

        await using var incoming = new FakeServer("srv-new", "discovery");
        await incoming.ConnectAsync(host.ListeningPort);

        Assert.Equal("another_server", await incoming.WaitForGoodbyeAsync(Timeout));
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
}
