using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Discovery;
using Sendspin.SDK.Tests.Audio;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// What the static-delay argument of <see cref="SendspinHostService.SendPlayerStateAsync"/>
/// means, mirroring <see cref="PlayerStateDelayReportingTests"/> on the dial path: omitting it
/// leaves the current delay alone, supplying one applies and persists it.
/// </summary>
/// <remarks>
/// The facade took a non-nullable <c>double = 0.0</c>, so the natural call for a volume or mute
/// change forwarded a real zero — which the client writes to
/// <see cref="Synchronization.IClockSynchronizer.StaticDelayMs"/> and through
/// <see cref="IStaticDelayStore"/>, wiping a server-set delay and persisting the wipe.
/// A real loopback connection is needed because the facade only forwards to clients that have
/// completed a handshake.
/// </remarks>
[Collection("RealSockets")]
public class SendspinHostServiceStaticDelayTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static readonly byte[] TestPsk = Enumerable.Repeat((byte)0x42, 32).ToArray();

    [Fact]
    public async Task OmittedDelay_LeavesTheDelayUntouchedAndPersistsNothing()
    {
        var clock = new FakeClockSynchronizer { IsConverged = true, HasMinimalSync = true };
        var store = new RecordingDelayStore();
        await using var host = await StartHostAsync(clock, store);
        await using var server = await ConnectServerAsync(host);

        clock.StaticDelayMs = 250.0; // where a server/command set_static_delay would leave it

        await host.SendPlayerStateAsync(volume: 60, muted: false);

        Assert.Equal(250.0, clock.StaticDelayMs);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task SuppliedDelay_IsAppliedAndPersisted()
    {
        // Positive control: if a supplied delay were dropped, the test above would pass for the
        // wrong reason.
        var clock = new FakeClockSynchronizer { IsConverged = true, HasMinimalSync = true };
        var store = new RecordingDelayStore();
        await using var host = await StartHostAsync(clock, store);
        await using var server = await ConnectServerAsync(host);

        await host.SendPlayerStateAsync(volume: 60, muted: false, staticDelayMs: 400.0);

        Assert.Equal(400.0, clock.StaticDelayMs);
        Assert.Equal(new[] { 400.0 }, store.Saved);
    }

    private static async Task<SendspinHostService> StartHostAsync(
        FakeClockSynchronizer clock, RecordingDelayStore store)
    {
        // Unbound LongTerm record: each FakeServer generates a fresh identity, and the session
        // trust it grants makes the server/activate below admissible.
        var records = new InMemoryPairingRecordStore();
        records.Upsert(new PairingRecord(TestPsk, PskCategory.LongTerm));

        var host = new SendspinHostService(
            NullLoggerFactory.Instance,
            new SendspinClientOptions
            {
                Identity = SendspinIdentity.Generate(),
                PairingRecordStore = records,

                // Configured, so BuildClientOptions hands the same instance to the connection
                // rather than minting a per-connection Kalman synchronizer.
                ClockSynchronizer = clock,
                StaticDelayStore = store,
            },
            listenerOptions: new ListenerOptions { Port = 0 },
            advertiserOptions: new AdvertiserOptions { Enabled = false });

        await host.StartAsync();
        return host;
    }

    private static async Task<FakeServer> ConnectServerAsync(SendspinHostService host)
    {
        var server = new FakeServer(TestPsk, new[] { "playback" });
        await server.ConnectAsync(host.ListeningPort);
        await WaitForServerConnectedAsync(host, server.ServerId);
        return server;
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

    private sealed class RecordingDelayStore : IStaticDelayStore
    {
        public List<double> Saved { get; } = new List<double>();

        public double? Load() => null;

        public void Save(double staticDelayMs) => Saved.Add(staticDelayMs);
    }
}
