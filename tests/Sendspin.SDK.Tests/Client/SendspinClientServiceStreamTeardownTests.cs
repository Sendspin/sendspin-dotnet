using Sendspin.SDK.Client;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// <c>stream/end</c> and <c>stream/clear</c> carry an optional <c>roles</c> array, and a
/// role-targeted teardown must reach only the roles it names (#193).
/// </summary>
/// <remarks>
/// This is not an exotic case: removing a stream role from <c>active_roles</c> is a normal
/// <c>server/activate</c>, and the spec has the server end that role's output first, so a
/// <c>stream/end</c> naming <c>artwork</c> arrives mid-playback. Handling it unconditionally
/// stopped the audio pipeline and flipped the group to Idle while every other compliant client
/// in the group kept playing.
/// </remarks>
public class SendspinClientServiceStreamTeardownTests
{
    private const string PlayerStreamStart =
        """{"type":"stream/start","payload":{"player":{"codec":"pcm","channels":2,"sample_rate":48000,"bit_depth":16}}}""";

    /// <summary>
    /// A client mid-playback. The converged clock keeps the <c>stream/start</c> off the
    /// sync-burst path, and the <c>stream/start</c> is what creates the group state and sets it
    /// Playing — so the teardown below has both a live pipeline and a non-Idle group to act on.
    /// </summary>
    private static (SendspinClientService Client, FakeSendspinConnection Connection, FakeAudioPipeline Pipeline)
        PlayingClient()
    {
        var pipe = new FakeAudioPipeline();
        var (client, connection, _) = TestClient.Create(configure: options => options with
        {
            AudioPipeline = pipe,
            ClockSynchronizer = new ConvergedClockSynchronizer(),
        });

        connection.RaiseTextMessageReceived(PlayerStreamStart);
        return (client, connection, pipe);
    }

    [Fact]
    public void StreamEnd_ForAnotherRole_LeavesAudioPlaying()
    {
        var (client, connection, pipe) = PlayingClient();
        using var _c = client;
        Assert.Equal(PlaybackState.Playing, client.CurrentGroup?.PlaybackState);

        connection.RaiseTextMessageReceived(
            """{"type":"stream/end","payload":{"server_transmitted":1000,"roles":["artwork"]}}""");

        Assert.Equal(0, pipe.StopCount);
        Assert.Equal(PlaybackState.Playing, client.CurrentGroup?.PlaybackState);
    }

    [Fact]
    public void StreamEnd_WithNoRoles_EndsEveryStreamAndStopsAudio()
    {
        var (client, connection, pipe) = PlayingClient();
        using var _c = client;

        connection.RaiseTextMessageReceived(
            """{"type":"stream/end","payload":{"server_transmitted":1000}}""");

        Assert.Equal(1, pipe.StopCount);
        Assert.Equal(PlaybackState.Idle, client.CurrentGroup?.PlaybackState);
    }

    [Fact]
    public void StreamEnd_NamingPlayerAmongOtherRoles_StopsAudio()
    {
        var (client, connection, pipe) = PlayingClient();
        using var _c = client;

        connection.RaiseTextMessageReceived(
            """{"type":"stream/end","payload":{"server_transmitted":1000,"roles":["artwork","player"]}}""");

        Assert.Equal(1, pipe.StopCount);
        Assert.Equal(PlaybackState.Idle, client.CurrentGroup?.PlaybackState);
    }

    [Fact]
    public void StreamClear_ForAnotherRole_LeavesTheAudioBuffersAlone()
    {
        var (client, connection, pipe) = PlayingClient();
        using var _c = client;

        connection.RaiseTextMessageReceived(
            """{"type":"stream/clear","payload":{"server_transmitted":2000,"roles":["visualizer"]}}""");

        Assert.Equal(0, pipe.ClearCount);
    }

    [Fact]
    public void StreamClear_WithNoRoles_ClearsTheAudioBuffers()
    {
        var (client, connection, pipe) = PlayingClient();
        using var _c = client;

        connection.RaiseTextMessageReceived(
            """{"type":"stream/clear","payload":{"server_transmitted":2000}}""");

        Assert.Equal(1, pipe.ClearCount);
    }

    [Fact]
    public void StreamClear_NamingPlayer_ClearsTheAudioBuffers()
    {
        var (client, connection, pipe) = PlayingClient();
        using var _c = client;

        connection.RaiseTextMessageReceived(
            """{"type":"stream/clear","payload":{"server_transmitted":2000,"roles":["player","visualizer"]}}""");

        Assert.Equal(1, pipe.ClearCount);
    }

    /// <summary>
    /// The other half of the fix: a role the SDK does not implement has to reach the consumer
    /// that does, or an artwork or visualizer surface can never obey the teardown it was sent.
    /// The application-specific role here (a name starting with <c>_</c>) is the case the SDK
    /// can never recognise, and it must pass through rather than be dropped or throw.
    /// </summary>
    [Fact]
    public void StreamEnd_RaisesTheEventWithEveryRoleItNamed()
    {
        var (client, connection, pipe) = PlayingClient();
        using var _c = client;

        StreamEndPayload? received = null;
        client.StreamEndReceived += (_, payload) => received = payload;

        connection.RaiseTextMessageReceived(
            """{"type":"stream/end","payload":{"server_transmitted":4242,"roles":["artwork","_vendor_leds"]}}""");

        Assert.NotNull(received);
        Assert.Equal(new[] { "artwork", "_vendor_leds" }, received.Roles);
        Assert.Equal(4242, received.ServerTransmitted);
        Assert.Equal(0, pipe.StopCount);
    }

    [Fact]
    public void StreamClear_RaisesTheEventWithEveryRoleItNamed()
    {
        var (client, connection, pipe) = PlayingClient();
        using var _c = client;

        StreamClearPayload? received = null;
        client.StreamClearReceived += (_, payload) => received = payload;

        connection.RaiseTextMessageReceived(
            """{"type":"stream/clear","payload":{"server_transmitted":99,"roles":["visualizer","_vendor_leds"]}}""");

        Assert.NotNull(received);
        Assert.Equal(new[] { "visualizer", "_vendor_leds" }, received.Roles);
        Assert.Equal(99, received.ServerTransmitted);
        Assert.Equal(0, pipe.ClearCount);
    }

    /// <summary>
    /// An omitted <c>roles</c> means "every active stream", and the event has to be able to say
    /// so: a null list is the signal, not an empty one, so a subscriber can tell "end everything"
    /// from a list that happens to name no role it owns.
    /// </summary>
    [Fact]
    public void StreamTeardown_WithNoRoles_RaisesTheEventsWithANullRoleList()
    {
        var (client, connection, _) = PlayingClient();
        using var _c = client;

        StreamEndPayload? end = null;
        StreamClearPayload? clear = null;
        client.StreamEndReceived += (_, payload) => end = payload;
        client.StreamClearReceived += (_, payload) => clear = payload;

        connection.RaiseTextMessageReceived(
            """{"type":"stream/clear","payload":{"server_transmitted":1}}""");
        connection.RaiseTextMessageReceived(
            """{"type":"stream/end","payload":{"server_transmitted":2}}""");

        Assert.NotNull(end);
        Assert.Null(end.Roles);
        Assert.NotNull(clear);
        Assert.Null(clear.Roles);
    }
}
