using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Discovery;
using Sendspin.SDK.Models;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// The host's source-role surface: the line-sense signal it reports on every connection, and
/// the source-role state it raises for the app. The host keeps each connection's client
/// private, so without these a server-initiated app could neither drive line-sense nor tell
/// whether its source role is active. Loopback end-to-end, like
/// <see cref="SendspinHostServiceLastPlaybackServerTests"/>.
/// </summary>
[Collection("RealSockets")]
public class SendspinHostServiceSourceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static readonly byte[] TestPsk = Enumerable.Repeat((byte)0x42, 32).ToArray();

    private static readonly string[] SourceRole = ["source@v1"];

    private static async Task<SendspinHostService> StartHostAsync()
    {
        var records = new InMemoryPairingRecordStore();

        // Unbound LongTerm record: the session is paired, so the source role may activate.
        records.Upsert(new PairingRecord(TestPsk, PskCategory.LongTerm));

        var host = new SendspinHostService(
            NullLoggerFactory.Instance,
            new SendspinClientOptions
            {
                Identity = SendspinIdentity.Generate(),
                PairingRecordStore = records,
                Capabilities = new ClientCapabilities
                {
                    Roles = { "source@v1" },
                    SourceRoleSupport = new SourceRoleSupport { LineSense = true },
                },
                CaptureDevice = new FakeCaptureDevice(),

                // Converged, so the initial client/state — which carries the signal — is sent.
                ClockSynchronizer = new ConvergedClockSynchronizer(),
            },
            listenerOptions: new ListenerOptions { Port = 0 },
            advertiserOptions: new AdvertiserOptions { Enabled = false });

        await host.StartAsync();
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

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {because}");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task ASignalSetBeforeAConnection_RidesItsInitialClientState()
    {
        await using var host = await StartHostAsync();
        await host.SetSourceSignalAsync(false);

        await using var server = new FakeServer(TestPsk, ["playback"], activeRoles: SourceRole);
        await server.ConnectAsync(host.ListeningPort);

        Assert.True(await server.WaitForSourceSignalAsync("absent", Timeout));
    }

    [Fact]
    public async Task ASignalSetAfterAdmission_ReachesTheAdmittedServer()
    {
        await using var host = await StartHostAsync();
        await using var server = new FakeServer(TestPsk, ["playback"], activeRoles: SourceRole);
        await server.ConnectAsync(host.ListeningPort);
        await WaitForServerConnectedAsync(host, server.ServerId);

        await host.SetSourceSignalAsync(true);

        Assert.True(await server.WaitForSourceSignalAsync("present", Timeout));
    }

    [Fact]
    public async Task TheSignal_IsRememberedForTheNextConnection()
    {
        await using var host = await StartHostAsync();
        await using (var first = new FakeServer(TestPsk, ["playback"], activeRoles: SourceRole))
        {
            await first.ConnectAsync(host.ListeningPort);
            await WaitForServerConnectedAsync(host, first.ServerId);
            await host.SetSourceSignalAsync(true);
        }

        await WaitUntilAsync(() => host.ConnectedServers.Count == 0, "the first server to leave");

        await using var second = new FakeServer(TestPsk, ["playback"], activeRoles: SourceRole);
        await second.ConnectAsync(host.ListeningPort);

        Assert.True(await second.WaitForSourceSignalAsync("present", Timeout));
    }
}
