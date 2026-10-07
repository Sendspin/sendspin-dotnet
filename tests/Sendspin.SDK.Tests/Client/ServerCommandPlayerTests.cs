using Sendspin.SDK.Client;
using Sendspin.SDK.Models;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// The player object of <c>server/command</c> is applied only under an active player role, only
/// as the one command its <c>command</c> member names, and only with an in-range volume (#328).
/// </summary>
public class ServerCommandPlayerTests
{
    private static string PlayerCommand(string body) =>
        "{\"type\":\"server/command\",\"payload\":{\"player\":{" + body + "}}}";

    private static List<PlayerState> RecordPlayerStates(SendspinClientService client)
    {
        var states = new List<PlayerState>();
        client.PlayerStateChanged += (_, state) => states.Add(new PlayerState { Volume = state.Volume, Muted = state.Muted });
        return states;
    }

    [Fact]
    public void PlayerCommand_WithoutActivePlayerRole_IsIgnored()
    {
        // messaging.md: "player?: object - only if the player role is active". The server may
        // leave player out of active_roles; such a connection must not control the output.
        var (client, connection, _) = TestClient.Create();
        using var _c = client;
        TestClient.CompleteHandshake(connection, "metadata@v1");
        var states = RecordPlayerStates(client);

        connection.RaiseTextMessageReceived(PlayerCommand("\"command\":\"mute\",\"mute\":true"));
        connection.RaiseTextMessageReceived(PlayerCommand("\"command\":\"volume\",\"volume\":10"));

        Assert.False(client.CurrentPlayerState.Muted);
        Assert.Equal(100, client.CurrentPlayerState.Volume);
        Assert.Empty(states);
    }

    [Theory]
    [InlineData(5000, 100)]
    [InlineData(-5, 0)]
    public void VolumeCommand_OutOfRange_IsClampedBeforeItReachesTheApp(int requested, int expected)
    {
        var (client, connection, _) = TestClient.Create(activated: true);
        using var _c = client;
        var states = RecordPlayerStates(client);

        connection.RaiseTextMessageReceived(PlayerCommand($"\"command\":\"volume\",\"volume\":{requested}"));

        Assert.Equal(expected, client.CurrentPlayerState.Volume);
        Assert.Equal(expected, Assert.Single(states).Volume);
    }

    [Fact]
    public void UnsupportedSetOutputDelay_DoesNotApplyAStrayVolume()
    {
        // roles/player/v1.md: "commands absent from the client's current supported_commands are
        // ignored by the client" — the whole command, not just its own parameter.
        var (client, connection, _) = TestClient.Create(activated: true, configure: options => options with
        {
            Capabilities = new ClientCapabilities { SupportsSetOutputDelay = false },
        });
        using var _c = client;
        var states = RecordPlayerStates(client);

        connection.RaiseTextMessageReceived(
            PlayerCommand("\"command\":\"set_output_delay\",\"output_delay_ms\":100,\"volume\":0"));

        Assert.Equal(100, client.CurrentPlayerState.Volume);
        Assert.Empty(states);
    }

    [Fact]
    public void MuteCommand_AppliesOnlyMute()
    {
        var (client, connection, _) = TestClient.Create(activated: true);
        using var _c = client;
        var states = RecordPlayerStates(client);

        connection.RaiseTextMessageReceived(PlayerCommand("\"command\":\"mute\",\"mute\":true,\"volume\":0"));

        Assert.True(client.CurrentPlayerState.Muted);
        Assert.Equal(100, client.CurrentPlayerState.Volume);
        Assert.Single(states);
    }

    [Fact]
    public void VolumeCommand_InRange_IsApplied()
    {
        var (client, connection, _) = TestClient.Create(activated: true);
        using var _c = client;
        var states = RecordPlayerStates(client);

        connection.RaiseTextMessageReceived(PlayerCommand("\"command\":\"volume\",\"volume\":40"));

        Assert.Equal(40, client.CurrentPlayerState.Volume);
        Assert.False(client.CurrentPlayerState.Muted);
        Assert.Equal(40, Assert.Single(states).Volume);
    }
}
