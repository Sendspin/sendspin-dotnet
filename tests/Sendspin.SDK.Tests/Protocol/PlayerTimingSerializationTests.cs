using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Protocol;

/// <summary>
/// Wire-format coverage for the player timing capabilities added in spec PR #69:
/// <c>required_lead_time_ms</c> / <c>min_buffer_ms</c> / <c>supported_commands</c> on the
/// client/state player object, and <c>set_static_delay</c> on the server/command player object.
/// </summary>
public class PlayerTimingSerializationTests
{
    [Fact]
    public void ClientState_SerializesTimingFields()
    {
        var msg = ClientStateMessage.Create(
            available: true,
            player: new PlayerStatePayload
            {
                Volume = 80,
                Muted = false,
                RequiredLeadTimeMs = 200,
                MinBufferMs = 150,
                SupportedCommands = [Commands.SetStaticDelay],
            });

        var json = MessageSerializer.Serialize(msg);

        Assert.Contains("\"required_lead_time_ms\":200", json);
        Assert.Contains("\"min_buffer_ms\":150", json);
        Assert.Contains("\"supported_commands\":[\"set_static_delay\"]", json);
    }

    [Fact]
    public void ClientState_TimingFieldsAlwaysWrittenEvenWhenZero()
    {
        // All three are "REQUIRED for players", so all three must serialize even at zero.
        // static_delay_ms used to be omitted at its default — which is 0, the common case — so
        // essentially every player left a required field out of its initial state.
        var msg = ClientStateMessage.Create(
            available: true,
            player: new PlayerStatePayload { Volume = 100, Muted = false });

        var json = MessageSerializer.Serialize(msg);

        Assert.Contains("\"static_delay_ms\":0", json);
        Assert.Contains("\"required_lead_time_ms\":0", json);
        Assert.Contains("\"min_buffer_ms\":0", json);
    }

    [Fact]
    public void ClientState_InitialMessage_CarriesOutputDelayAtItsDefault()
    {
        // The initial full state is where the presence requirement actually bites: aiosendspin
        // reads an omitted static_delay_ms as "unchanged", which on the first message leaves it
        // with no value at all rather than the 0 we meant.
        var msg = ClientStateMessage.Create(
            available: true, player: new PlayerStatePayload());

        Assert.Contains("\"static_delay_ms\":0", MessageSerializer.Serialize(msg));
    }

    [Fact]
    public void ClientState_OutputDelayIsAnIntegerOnTheWire()
    {
        // The spec types static_delay_ms as an integer. The scheduler's own delay is a double,
        // so the factory takes the projected wire value rather than the raw one — this pins
        // that the wire field cannot carry a fraction.
        var msg = ClientStateMessage.Create(
            available: true,
            player: new PlayerStatePayload { Volume = 100, Muted = false, OutputDelayMs = 250 });

        string json = MessageSerializer.Serialize(msg);

        Assert.Contains("\"static_delay_ms\":250", json);
        Assert.DoesNotContain("\"static_delay_ms\":250.", json);
    }

    [Fact]
    public void ClientState_SupportedCommandsWrittenAsEmptyArray_NotOmitted()
    {
        // Spec PR #175 made the field required: a player that accepts no commands sends [].
        // Omitting it once merging was removed left the server unable to tell "accepts none"
        // from "unchanged".
        var msg = ClientStateMessage.Create(
            available: true,
            player: new PlayerStatePayload { Volume = 100, Muted = false });

        var json = MessageSerializer.Serialize(msg);

        Assert.Contains("\"supported_commands\":[]", json);
    }

    [Fact]
    public void ServerCommand_SetStaticDelay_Deserializes()
    {
        var json = """
        {
            "type": "server/command",
            "payload": {
                "player": { "command": "set_static_delay", "static_delay_ms": 250 }
            }
        }
        """;

        var msg = MessageSerializer.Deserialize<ServerCommandMessage>(json);

        Assert.NotNull(msg);
        Assert.NotNull(msg.Payload.Player);
        Assert.Equal(Commands.SetStaticDelay, msg.Payload.Player.Command);
        Assert.Equal(250, msg.Payload.Player.StaticDelayMs);
    }

    [Fact]
    public void ServerCommand_SetOutputDelay_Deserializes()
    {
        // Spec 168a677 (PR #164) renamed the command to set_output_delay and the field to
        // output_delay_ms, with no alias on either. Inbound tolerance only: what the SDK sends
        // stays on the pre-rename names until servers adopt the rename.
        var json = """
        {
            "type": "server/command",
            "payload": {
                "player": { "command": "set_output_delay", "output_delay_ms": 120 }
            }
        }
        """;

        var msg = MessageSerializer.Deserialize<ServerCommandMessage>(json);

        Assert.NotNull(msg);
        Assert.NotNull(msg.Payload.Player);
        Assert.Equal(Commands.SetOutputDelay, msg.Payload.Player.Command);
        Assert.Equal(120, msg.Payload.Player.OutputDelayMs);
        Assert.Null(msg.Payload.Player.StaticDelayMs);
    }
}
