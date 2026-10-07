using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Discovery;
using Sendspin.SDK.Models;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// What makes a server the last-playback server (#341). connection.md: "Clients MUST
/// persistently store the server_id of the server that most recently held the admitted
/// connection while 'playback' was among its activities". The declared activities decide it,
/// not the group's playback state. Loopback end-to-end, like
/// <see cref="SendspinHostServiceArbitrationTests"/>.
/// </summary>
[Collection("RealSockets")]
public class SendspinHostServiceLastPlaybackServerTests
{
    // Same generous ceiling and rationale as SendspinHostServiceArbitrationTests.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static readonly byte[] TestPsk = Enumerable.Repeat((byte)0x42, 32).ToArray();

    private const string PlaybackActivate =
        """{"type":"server/activate","payload":{"activities":["playback"]}}""";

    private const string GroupPlaying =
        """{"type":"group/update","payload":{"group_id":"g1","playback_state":"playing"}}""";

    private static async Task<SendspinHostService> StartHostAsync()
    {
        var records = new InMemoryPairingRecordStore();

        // Unbound LongTerm record, so a playback activate is admissible — see
        // SendspinHostServiceArbitrationTests for the full reasoning.
        records.Upsert(new PairingRecord(TestPsk, PskCategory.LongTerm));

        var host = new SendspinHostService(
            NullLoggerFactory.Instance,
            new SendspinClientOptions
            {
                Identity = SendspinIdentity.Generate(),
                PairingRecordStore = records,
            },
            listenerOptions: new ListenerOptions { Port = 0 },
            advertiserOptions: new AdvertiserOptions { Enabled = false });

        await host.StartAsync();
        return host;
    }

    private static async Task WaitForLastPlayedAsync(SendspinHostService host, string serverId)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? s, string id)
        {
            if (id == serverId)
            {
                tcs.TrySetResult();
            }
        }

        host.LastPlayedServerIdChanged += Handler;
        try
        {
            // Cover the race where it was recorded before we subscribed.
            if (host.LastPlayedServerId == serverId)
            {
                tcs.TrySetResult();
            }

            await tcs.Task.WaitAsync(Timeout);
        }
        finally
        {
            host.LastPlayedServerIdChanged -= Handler;
        }
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

    [Fact]
    public async Task AdmittedWithPlayback_IsRecorded_ThoughTheGroupNeverPlays()
    {
        await using var host = await StartHostAsync();
        await using var server = new FakeServer(TestPsk, ["playback"]);

        await server.ConnectAsync(host.ListeningPort);

        await WaitForLastPlayedAsync(host, server.ServerId);
    }

    [Fact]
    public async Task AdmittedWithNoActivities_IsRecorded_WhenALaterActivateDeclaresPlayback()
    {
        await using var host = await StartHostAsync();
        await using var server = new FakeServer(TestPsk, []);
        await server.ConnectAsync(host.ListeningPort);
        await WaitForServerConnectedAsync(host, server.ServerId);
        Assert.Null(host.LastPlayedServerId);

        await server.SendJsonAsync(PlaybackActivate);

        await WaitForLastPlayedAsync(host, server.ServerId);
    }

    [Fact]
    public async Task AdmittedWithNoActivities_IsNotRecorded_WhenItsGroupPlays()
    {
        await using var host = await StartHostAsync();
        var groupPlaying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.GroupStateChanged += (s, g) =>
        {
            if (g.PlaybackState == PlaybackState.Playing)
            {
                groupPlaying.TrySetResult();
            }
        };

        await using var server = new FakeServer(TestPsk, []);
        await server.ConnectAsync(host.ListeningPort);
        await WaitForServerConnectedAsync(host, server.ServerId);

        await server.SendJsonAsync(GroupPlaying);
        await groupPlaying.Task.WaitAsync(Timeout);

        Assert.Null(host.LastPlayedServerId);
    }
}
