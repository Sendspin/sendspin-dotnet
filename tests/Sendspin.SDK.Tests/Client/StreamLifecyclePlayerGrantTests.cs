using Sendspin.SDK.Client;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// <c>stream/start</c>, <c>stream/end</c> and <c>stream/clear</c> reach the audio pipeline only
/// on a connection whose <c>active_roles</c> include the player role (#329).
/// </summary>
public class StreamLifecyclePlayerGrantTests
{
    private const string PlayerStreamStart =
        """{"type":"stream/start","payload":{"player":{"codec":"pcm","channels":2,"sample_rate":48000,"bit_depth":16}}}""";

    private static (SendspinClientService Client, FakeSendspinConnection Connection, FakeAudioPipeline Pipeline)
        ClientWithRoles(bool clockConverged, params string[] activeRoles)
    {
        var pipe = new FakeAudioPipeline();
        var (client, connection, _) = TestClient.Create(configure: options => options with
        {
            AudioPipeline = pipe,
            ClockSynchronizer = clockConverged ? new ConvergedClockSynchronizer() : options.ClockSynchronizer,
            Capabilities = new ClientCapabilities
            {
                Roles = new List<string> { "player@v1", "artwork@v1" },
            },
        });

        TestClient.CompleteHandshake(connection, activeRoles);
        return (client, connection, pipe);
    }

    [Fact]
    public void StreamStart_WithoutActivePlayerRole_DoesNotStartThePipelineOrReportPlaying()
    {
        // messaging.md: "player?: object - only if the player role is active".
        var (client, connection, pipe) = ClientWithRoles(clockConverged: true, "artwork@v1");
        using var _c = client;
        var groupChanges = 0;
        client.GroupStateChanged += (_, _) => groupChanges++;

        connection.RaiseTextMessageReceived(PlayerStreamStart);

        Assert.Empty(pipe.StartCalls);
        Assert.NotEqual(PlaybackState.Playing, client.CurrentGroup?.PlaybackState);
        Assert.Equal(0, groupChanges);
    }

    [Theory]
    [InlineData("""{"type":"stream/end","payload":{"server_transmitted":1}}""")]
    [InlineData("""{"type":"stream/end","payload":{"server_transmitted":1,"roles":["player"]}}""")]
    public void StreamEnd_WithoutActivePlayerRole_LeavesThePipelineAlone(string streamEnd)
    {
        // The pipeline can be another connection's: a host shares one across its servers.
        var (client, connection, pipe) = ClientWithRoles(clockConverged: true, "artwork@v1");
        using var _c = client;
        var groupChanges = 0;
        client.GroupStateChanged += (_, _) => groupChanges++;

        connection.RaiseTextMessageReceived(streamEnd);

        Assert.Equal(0, pipe.StopCount);
        Assert.Equal(0, groupChanges);
    }

    [Theory]
    [InlineData("""{"type":"stream/clear","payload":{"server_transmitted":1}}""")]
    [InlineData("""{"type":"stream/clear","payload":{"server_transmitted":1,"roles":["player"]}}""")]
    public void StreamClear_WithoutActivePlayerRole_LeavesThePipelineAlone(string streamClear)
    {
        var (client, connection, pipe) = ClientWithRoles(clockConverged: true, "artwork@v1");
        using var _c = client;

        connection.RaiseTextMessageReceived(streamClear);

        Assert.Equal(0, pipe.ClearCount);
    }

    /// <summary>
    /// The gate is the role grant, not the binary gate's "player state object has been sent".
    /// aiosendspin starts a held player without its state once its five-second wait lapses, and
    /// this client withholds its initial client/state until the clock converges; a start dropped
    /// in that window is never repeated, so the stream would stay closed for good.
    /// </summary>
    [Fact]
    public void StreamStart_ForAnActivePlayer_StartsThePipelineBeforeThePlayerStateHasGoneOut()
    {
        var (client, connection, pipe) = ClientWithRoles(clockConverged: false, "player@v1");
        using var _c = client;
        Assert.DoesNotContain(connection.SnapshotSentMessages(), m => m is ClientStateMessage);

        connection.RaiseTextMessageReceived(PlayerStreamStart);

        Assert.Single(pipe.StartCalls);
        Assert.Equal(PlaybackState.Playing, client.CurrentGroup?.PlaybackState);
    }

    /// <summary>
    /// The grant that counts is the one in effect when the message arrived. Handlers run behind
    /// whatever the pipeline is still doing, so a later <c>server/activate</c> must not
    /// authorize a start that was sent while the player was inactive.
    /// </summary>
    [Fact]
    public async Task StreamStart_ReceivedWhilePlayerIsInactive_IsNotAuthorizedByALaterActivate()
    {
        var (client, connection, pipe) = ClientWithRoles(clockConverged: true, "player@v1", "artwork@v1");
        using var _c = client;
        var held = pipe.HoldNextStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        connection.RaiseTextMessageReceived(PlayerStreamStart);
        await pipe.StartEntered.WaitAsync(TimeSpan.FromSeconds(30));

        // All delivered while the first start is still opening the device.
        connection.RaiseTextMessageReceived("""
            {"type":"server/activate","payload":{"activities":["playback"],"active_roles":["artwork@v1"]}}
            """);
        connection.RaiseTextMessageReceived(PlayerStreamStart);
        connection.RaiseTextMessageReceived("""
            {"type":"server/activate","payload":{"activities":["playback"],"active_roles":["player@v1","artwork@v1"]}}
            """);
        connection.RaiseTextMessageReceived("""{"type":"stream/clear","payload":{"server_transmitted":1}}""");

        held.SetResult();
        await pipe.CallsCompleted(3).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(new[] { "start", "stop", "clear" }, pipe.CallLog);
    }
}
