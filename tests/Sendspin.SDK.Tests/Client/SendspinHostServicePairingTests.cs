using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Discovery;
using Sendspin.SDK.Connection.Noise;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// The pairing surface in host (server-dials-client) mode: the host-level
/// <see cref="SendspinHostService.PairingConfigChanged"/> forward, and
/// <see cref="SendspinHostService.EnsurePairingPsk"/> /
/// <see cref="SendspinHostService.RotatePairingPsk"/> working before any server has
/// connected — which is when the QR code has to be shown in this mode.
/// </summary>
[Collection("RealSockets")]
public class SendspinHostServicePairingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static readonly byte[] TestPsk = Enumerable.Repeat((byte)0x42, 32).ToArray();

    [Fact]
    public async Task PairingConfigChanged_OnAConnection_ReachesAHostLevelSubscriber()
    {
        var records = new InMemoryPairingRecordStore();
        records.Upsert(new PairingRecord(TestPsk, PskCategory.LongTerm));

        await using var host = new SendspinHostService(
            NullLoggerFactory.Instance,
            new SendspinClientOptions
            {
                Identity = SendspinIdentity.Generate(),
                PairingRecordStore = records,
            },
            listenerOptions: new ListenerOptions { Port = 0 },
            advertiserOptions: new AdvertiserOptions { Enabled = false });
        await host.StartAsync();

        var changed = new TaskCompletionSource<PairingConfigChangedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        host.PairingConfigChanged += (_, e) => changed.TrySetResult(e);

        await using var server = new FakeServer(TestPsk, ["playback", "management"]);
        await server.ConnectAsync(host.ListeningPort);
        await WaitForServerConnectedAsync(host, server.ServerId);

        await server.SendJsonAsync(
            """{"type":"management/set-pairing-config","payload":{"unpaired_access":{"enabled":true}}}""");

        var change = await changed.Task.WaitAsync(Timeout);
        Assert.True(change.UnpairedAccessEnabled);
        Assert.False(change.PairingPskReplaced);
    }

    [Fact]
    public async Task EnsurePairingPsk_WorksBeforeAnyConnection_AndMatchesAClientOverTheSameStore()
    {
        var store = new InMemoryPairingRecordStore();
        var identity = SendspinIdentity.Generate();

        // Never started: no listener, no advertiser, no connection exists.
        await using var host = new SendspinHostService(
            NullLoggerFactory.Instance,
            new SendspinClientOptions { Identity = identity, PairingRecordStore = store },
            advertiserOptions: new AdvertiserOptions { Enabled = false });

        string token = host.EnsurePairingPsk();

        Assert.Equal(token, host.EnsurePairingPsk());

        // A dial-mode client over the same store and identity hands out the same token.
        var (client, _, _) = TestClient.Create(configure: options =>
        {
            options.Identity = identity;
            options.PairingRecordStore = store;
        });
        using var _c = client;
        Assert.Equal(token, client.EnsurePairingPsk());

        // Rotation invalidates it, exactly as on the client.
        string rotated = host.RotatePairingPsk();
        Assert.NotEqual(token, rotated);
        Assert.Equal(rotated, host.EnsurePairingPsk());
    }

    [Fact]
    public async Task EnsurePairingPsk_WithoutAStore_Throws()
    {
        await using var host = new SendspinHostService(
            NullLoggerFactory.Instance,
            new SendspinClientOptions { Identity = SendspinIdentity.Generate() },
            advertiserOptions: new AdvertiserOptions { Enabled = false });

        Assert.Throws<InvalidOperationException>(() => host.EnsurePairingPsk());
        Assert.Throws<InvalidOperationException>(() => host.RotatePairingPsk());
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
}
