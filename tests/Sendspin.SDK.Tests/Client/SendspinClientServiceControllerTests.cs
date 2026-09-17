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
        // Snapshot + filter by type: once a handshake is completed the client's time-sync loop
        // appends client/time frames from a background task, so reading the live SentMessages
        // (unlocked) would race with those. SnapshotSentMessages takes the copy under the lock.
        var msg = connection.SnapshotSentMessages().OfType<ClientCommandMessage>().Last();
        Assert.NotNull(msg.Payload.Controller);
        return msg.Payload.Controller;
    }

    /// <summary>
    /// Completes the handshake with <c>controller@v1</c> active and reports a broad
    /// <c>supported_commands</c> list, so the controller-command gate admits every command these
    /// tests exercise. The gate itself (an inactive role or an unlisted command drops the send)
    /// is covered by the three *_Drops / _Sends tests at the end of this file.
    /// </summary>
    private static void ActivateController(FakeSendspinConnection connection)
    {
        TestClient.CompleteHandshake(connection, ClientRoles.Controller);
        connection.RaiseTextMessageReceived("""
            {"type":"server/state","payload":{"controller":{"supported_commands":["play","pause","volume","mute","seek","seek_relative"]}}}
            """);
    }

    [Fact]
    public async Task SetVolumeAsync_SendsControllerVolumeCommand()
    {
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        ActivateController(connection);
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

        ActivateController(connection);
        await client.SetMuteAsync(true);

        var cmd = LastControllerCommand(connection);
        Assert.Equal(Commands.Mute, cmd.Command);
        Assert.True(cmd.Mute);
        Assert.Null(cmd.Volume);
    }

    [Fact]
    public async Task SetVolumeAsync_WhenControllerInactive_DropsBeforeReachingTransport()
    {
        // The controller-role gate precedes the send: with the role never activated,
        // SetVolumeAsync drops the command and never touches the transport. EnforceConnectionState
        // would make the fake throw "WebSocket is not connected" if the send reached it, so a
        // clean return (no throw, nothing sent) proves the guard fired first. Before the gate,
        // this same setup surfaced that transport throw.
        var (client, connection, _) = TestClient.Create(connected: false);
        connection.EnforceConnectionState = true;
        using var _c = client;

        await client.SetVolumeAsync(50); // controller@v1 never activated -> dropped, not thrown
        Assert.Empty(connection.SentMessages);
    }

    [Fact]
    public async Task SendCommandAsync_PlainCommand_NestsUnderController()
    {
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        ActivateController(connection);
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

        ActivateController(connection);
        await client.SendCommandAsync(Commands.Mute, new Dictionary<string, object> { [key] = true });

        Assert.True(LastControllerCommand(connection).Mute);
    }

    [Fact]
    public async Task SendCommandAsync_RoutesPositionMsParameter()
    {
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        ActivateController(connection);
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

        ActivateController(connection);
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

        ActivateController(connection);
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

        ActivateController(connection);
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
    public void ServerState_SeekMaxMsAbsent_IsUnset()
    {
        // The controller object is full state (spec #175): a later object carrying only volume
        // omits seek_max_ms, which unsets the bound rather than keeping it.
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        connection.RaiseTextMessageReceived("""
            {"type":"server/state","payload":{"controller":{"seek_max_ms":245000}}}
            """);
        connection.RaiseTextMessageReceived("""
            {"type":"server/state","payload":{"controller":{"volume":30}}}
            """);

        Assert.NotNull(client.CurrentGroup);
        Assert.Null(client.CurrentGroup.SeekMaxMs);
        Assert.Equal(30, client.CurrentGroup.Volume);
    }

    [Fact]
    public void ServerState_SeekMaxMsExplicitNull_ClearsPreviousValue()
    {
        // The explicit-null counterpart to the absent case above — both unset the bound under
        // spec #175. The server nulls it when the seekable range becomes unknown (a seekable track
        // giving way to a live stream); keeping the old bound would leave a seek bar pointing at
        // the length of a track that is no longer playing.
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

        ActivateController(connection);
        await client.SendCommandAsync(
            Commands.Seek, new Dictionary<string, object> { ["position_ms"] = positionMs });

        Assert.Equal(42_000, LastControllerCommand(connection).PositionMs);
    }

    [Fact]
    public async Task SendCommandAsync_SeekRelative_AcceptsWiderNumericOffsetMs()
    {
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        ActivateController(connection);
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

        ActivateController(connection); // role active + 'seek' listed, so the position check is what drops
        await client.SendCommandAsync(
            Commands.Seek,
            positionMs is null ? null : new Dictionary<string, object> { ["position_ms"] = positionMs });

        Assert.DoesNotContain(connection.SnapshotSentMessages(), m => m is ClientCommandMessage);
    }

    [Fact]
    public async Task SendCommandAsync_SeekRelativeWithoutUsableOffset_SendsNothing()
    {
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        ActivateController(connection); // role active + 'seek_relative' listed, so the offset check is what drops
        await client.SendCommandAsync(Commands.SeekRelative);

        Assert.DoesNotContain(connection.SnapshotSentMessages(), m => m is ClientCommandMessage);
    }

    [Fact]
    public async Task SendCommandAsync_WhenControllerActiveAndCommandListed_Sends()
    {
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        TestClient.CompleteHandshake(connection, ClientRoles.Controller);
        connection.RaiseTextMessageReceived("""
            {"type":"server/state","payload":{"controller":{"supported_commands":["play"]}}}
            """);

        await client.SendCommandAsync(Commands.Play);

        Assert.Equal(Commands.Play, LastControllerCommand(connection).Command);
    }

    [Fact]
    public async Task SendCommandAsync_WhenControllerInactive_Drops()
    {
        // The command is listed, but the controller role was never activated - nothing may be sent.
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        connection.RaiseTextMessageReceived("""
            {"type":"server/state","payload":{"controller":{"supported_commands":["play"]}}}
            """);

        await client.SendCommandAsync(Commands.Play);

        Assert.DoesNotContain(connection.SnapshotSentMessages(), m => m is ClientCommandMessage);
    }

    [Fact]
    public async Task SendCommandAsync_WhenCommandUnlisted_Drops()
    {
        // Role active, but 'pause' is not in the group's supported_commands - dropped.
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        TestClient.CompleteHandshake(connection, ClientRoles.Controller);
        connection.RaiseTextMessageReceived("""
            {"type":"server/state","payload":{"controller":{"supported_commands":["play"]}}}
            """);

        await client.SendCommandAsync(Commands.Pause);

        Assert.DoesNotContain(connection.SnapshotSentMessages(), m => m is ClientCommandMessage);
    }

    [Fact]
    public async Task SetVolumeAsync_WhenSupportedCommandsUnsetByLaterFullState_Drops()
    {
        // The controller object is full state (spec #175): a later object that omits
        // supported_commands unsets the list, so a command the earlier state advertised is no
        // longer authorised. Without the unconditional assign, the stale list would keep letting
        // it through.
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        TestClient.CompleteHandshake(connection, ClientRoles.Controller);
        connection.RaiseTextMessageReceived("""
            {"type":"server/state","payload":{"controller":{"supported_commands":["volume"]}}}
            """);

        await client.SetVolumeAsync(50);
        Assert.Equal(Commands.Volume, LastControllerCommand(connection).Command);
        int sentBefore = connection.SnapshotSentMessages().OfType<ClientCommandMessage>().Count();

        // A later controller object without supported_commands unsets the list.
        connection.RaiseTextMessageReceived("""
            {"type":"server/state","payload":{"controller":{"volume":30}}}
            """);

        await client.SetVolumeAsync(60);

        Assert.Equal(sentBefore, connection.SnapshotSentMessages().OfType<ClientCommandMessage>().Count());
    }
}
