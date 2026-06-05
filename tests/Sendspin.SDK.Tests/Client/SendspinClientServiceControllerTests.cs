using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Coverage for the controller role send path (client/command) and for surfacing the controller
/// object's supported_commands from server/state.
/// </summary>
public class SendspinClientServiceControllerTests
{
    private static SendspinClientService NewClient(FakeSendspinConnection connection) =>
        new(NullLogger<SendspinClientService>.Instance, connection);

    private static ControllerCommand LastControllerCommand(FakeSendspinConnection connection)
    {
        var msg = Assert.IsType<ClientCommandMessage>(connection.SentMessages.Last());
        Assert.NotNull(msg.Payload.Controller);
        return msg.Payload.Controller;
    }

    [Fact]
    public async Task SetVolumeAsync_SendsControllerVolumeCommand()
    {
        var connection = new FakeSendspinConnection();
        using var client = NewClient(connection);

        await client.SetVolumeAsync(150); // clamps to 100

        var cmd = LastControllerCommand(connection);
        Assert.Equal(Commands.Volume, cmd.Command);
        Assert.Equal(100, cmd.Volume);
        Assert.Null(cmd.Mute);
    }

    [Fact]
    public async Task SetMuteAsync_SendsControllerMuteCommand()
    {
        var connection = new FakeSendspinConnection();
        using var client = NewClient(connection);

        await client.SetMuteAsync(true);

        var cmd = LastControllerCommand(connection);
        Assert.Equal(Commands.Mute, cmd.Command);
        Assert.True(cmd.Mute);
        Assert.Null(cmd.Volume);
    }

    [Fact]
    public async Task SendCommandAsync_PlaintCommand_NestsUnderController()
    {
        var connection = new FakeSendspinConnection();
        using var client = NewClient(connection);

        await client.SendCommandAsync(Commands.Play);

        Assert.Equal(Commands.Play, LastControllerCommand(connection).Command);
    }

    [Theory]
    [InlineData("mute")]
    [InlineData("muted")]
    public async Task SendCommandAsync_AcceptsMuteOrMutedParamKey(string key)
    {
        var connection = new FakeSendspinConnection();
        using var client = NewClient(connection);

        await client.SendCommandAsync(Commands.Mute, new Dictionary<string, object> { [key] = true });

        Assert.True(LastControllerCommand(connection).Mute);
    }

    [Fact]
    public void ServerState_SupportedCommands_SurfacedOnGroup()
    {
        var connection = new FakeSendspinConnection();
        using var client = NewClient(connection);

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
}
