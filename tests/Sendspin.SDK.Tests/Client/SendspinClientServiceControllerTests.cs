using Sendspin.SDK.Client;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Coverage for the controller role send path (client/command) and for surfacing the controller
/// object's supported_commands from server/state.
/// </summary>
public class SendspinClientServiceControllerTests
{
    private static ControllerCommand LastControllerCommand(FakeSendspinConnection connection)
    {
        var msg = Assert.IsType<ClientCommandMessage>(connection.SentMessages.Last());
        Assert.NotNull(msg.Payload.Controller);
        return msg.Payload.Controller;
    }

    [Fact]
    public async Task SetVolumeAsync_SendsControllerVolumeCommand()
    {
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        await client.SetVolumeAsync(150); // clamps to 100

        var cmd = LastControllerCommand(connection);
        Assert.Equal(Commands.Volume, cmd.Command);
        Assert.Equal(100, cmd.Volume);
        Assert.Null(cmd.Mute);
    }

    [Fact]
    public async Task SetMuteAsync_SendsControllerMuteCommand()
    {
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        await client.SetMuteAsync(true);

        var cmd = LastControllerCommand(connection);
        Assert.Equal(Commands.Mute, cmd.Command);
        Assert.True(cmd.Mute);
        Assert.Null(cmd.Volume);
    }

    [Fact]
    public async Task SetVolumeAsync_WhenDisconnected_ThrowsLikeRealConnection()
    {
        // SetVolumeAsync sends directly with no connection-state guard, relying on the transport
        // to reject the send when there's no live socket. EnforceConnectionState makes the fake
        // throw "WebSocket is not connected" like SendspinConnection does while disconnected.
        var (client, connection, _) = TestClient.Create(connected: false);
        connection.EnforceConnectionState = true;
        using var _c = client;

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SetVolumeAsync(50));
        Assert.Empty(connection.SentMessages);
    }

    [Fact]
    public async Task SendCommandAsync_PlaintCommand_NestsUnderController()
    {
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        await client.SendCommandAsync(Commands.Play);

        Assert.Equal(Commands.Play, LastControllerCommand(connection).Command);
    }

    [Theory]
    [InlineData("mute")]
    [InlineData("muted")]
    public async Task SendCommandAsync_AcceptsMuteOrMutedParamKey(string key)
    {
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        await client.SendCommandAsync(Commands.Mute, new Dictionary<string, object> { [key] = true });

        Assert.True(LastControllerCommand(connection).Mute);
    }

    [Fact]
    public async Task SendCommandAsync_RoutesPositionMsParameter()
    {
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        await client.SendCommandAsync(
            Commands.Seek, new Dictionary<string, object> { ["position_ms"] = 42_000 });

        var cmd = LastControllerCommand(connection);
        Assert.Equal(Commands.Seek, cmd.Command);
        Assert.Equal(42_000, cmd.PositionMs);
        Assert.Null(cmd.OffsetMs);
    }

    [Fact]
    public async Task SendCommandAsync_RoutesOffsetMsParameter()
    {
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        await client.SendCommandAsync(
            Commands.SeekRelative, new Dictionary<string, object> { ["offset_ms"] = -15_000 });

        var cmd = LastControllerCommand(connection);
        Assert.Equal(Commands.SeekRelative, cmd.Command);
        Assert.Equal(-15_000, cmd.OffsetMs);
        Assert.Null(cmd.PositionMs);
    }

    [Fact]
    public async Task SeekAsync_SendsControllerSeekCommand()
    {
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        await client.SeekAsync(90_000);

        var cmd = LastControllerCommand(connection);
        Assert.Equal(Commands.Seek, cmd.Command);
        Assert.Equal(90_000, cmd.PositionMs);
        Assert.Null(cmd.OffsetMs);
    }

    [Fact]
    public async Task SeekRelativeAsync_SendsControllerSeekRelativeCommand()
    {
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        await client.SeekRelativeAsync(30_000);

        var cmd = LastControllerCommand(connection);
        Assert.Equal(Commands.SeekRelative, cmd.Command);
        Assert.Equal(30_000, cmd.OffsetMs);
        Assert.Null(cmd.PositionMs);
    }

    [Fact]
    public void ServerState_SupportedCommands_SurfacedOnGroup()
    {
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        connection.RaiseTextMessageReceived("""
            {
                "type": "server/state",
                "payload": {
                    "controller": { "supported_commands": ["play", "pause", "next"], "volume": 50, "muted": false }
                }
            }
            """);

        Assert.NotNull(client.CurrentGroup);
        Assert.Equal(new[] { "play", "pause", "next" }, client.CurrentGroup.SupportedCommands);
    }

    [Fact]
    public void ServerState_SeekSupport_SurfacedOnGroup()
    {
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        connection.RaiseTextMessageReceived("""
            {
                "type": "server/state",
                "payload": {
                    "controller": {
                        "supported_commands": ["play", "seek", "seek_relative"],
                        "seek_max_ms": 245000
                    }
                }
            }
            """);

        Assert.NotNull(client.CurrentGroup);
        Assert.Equal(new[] { "play", "seek", "seek_relative" }, client.CurrentGroup.SupportedCommands);
        Assert.Equal(245_000, client.CurrentGroup.SeekMaxMs);
    }

    [Fact]
    public void ServerState_SeekMaxMsAbsent_KeepsPreviousValue()
    {
        // The controller object is a partial update, so an update carrying only volume must not
        // wipe seek_max_ms — same keep-on-absent rule its siblings (volume/muted/repeat) follow.
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        connection.RaiseTextMessageReceived("""
            {"type":"server/state","payload":{"controller":{"seek_max_ms":245000}}}
            """);
        connection.RaiseTextMessageReceived("""
            {"type":"server/state","payload":{"controller":{"volume":30}}}
            """);

        Assert.NotNull(client.CurrentGroup);
        Assert.Equal(245_000, client.CurrentGroup.SeekMaxMs);
        Assert.Equal(30, client.CurrentGroup.Volume);
    }

    [Fact]
    public void ServerState_SeekMaxMsExplicitNull_ClearsPreviousValue()
    {
        // The counterpart to the absent case above, and the reason this leaf is Optional: the
        // server nulls it when the seekable range becomes unknown (a seekable track giving way to
        // a live stream), and a leaf set to null is a clear. Keeping the old bound would leave a
        // seek bar pointing at the length of a track that is no longer playing.
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        connection.RaiseTextMessageReceived("""
            {"type":"server/state","payload":{"controller":{"supported_commands":["play","seek"],"seek_max_ms":245000}}}
            """);
        connection.RaiseTextMessageReceived("""
            {"type":"server/state","payload":{"controller":{"supported_commands":["play"],"seek_max_ms":null}}}
            """);

        Assert.NotNull(client.CurrentGroup);
        Assert.Null(client.CurrentGroup.SeekMaxMs);
        Assert.Equal(new[] { "play" }, client.CurrentGroup.SupportedCommands);
    }

    [Theory]
    [InlineData(42_000L)]
    [InlineData(42_000d)]
    public async Task SendCommandAsync_AcceptsWiderNumericPositionMs(object positionMs)
    {
        // A caller reaching for the untyped overload has whatever its arithmetic produced —
        // TimeSpan.TotalMilliseconds is a double, a JSON round-trip lands on long.
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        await client.SendCommandAsync(
            Commands.Seek, new Dictionary<string, object> { ["position_ms"] = positionMs });

        Assert.Equal(42_000, LastControllerCommand(connection).PositionMs);
    }

    [Fact]
    public async Task SendCommandAsync_SeekRelative_AcceptsWiderNumericOffsetMs()
    {
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        await client.SendCommandAsync(
            Commands.SeekRelative, new Dictionary<string, object> { ["offset_ms"] = -15_000d });

        Assert.Equal(-15_000, LastControllerCommand(connection).OffsetMs);
    }

    [Theory]
    [InlineData(null)] // no parameters at all
    [InlineData("42000")] // a string, not a number
    [InlineData(long.MaxValue)] // numeric, but nowhere near an int
    public async Task SendCommandAsync_SeekWithoutUsablePosition_SendsNothing(object? positionMs)
    {
        // position_ms is mandatory on 'seek', so a bare {"controller":{"command":"seek"}} is a
        // shape the spec forbids. Dropping it keeps the malformed command off the wire.
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        await client.SendCommandAsync(
            Commands.Seek,
            positionMs is null ? null : new Dictionary<string, object> { ["position_ms"] = positionMs });

        Assert.Empty(connection.SentMessages);
    }

    [Fact]
    public async Task SendCommandAsync_SeekRelativeWithoutUsableOffset_SendsNothing()
    {
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        await client.SendCommandAsync(Commands.SeekRelative);

        Assert.Empty(connection.SentMessages);
    }
}
