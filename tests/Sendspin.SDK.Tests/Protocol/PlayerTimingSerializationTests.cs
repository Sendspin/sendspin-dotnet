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
        var msg = ClientStateMessage.CreatePlayerState(
            volume: 80,
            muted: false,
            staticDelayMs: 0,
            requiredLeadTimeMs: 200,
            minBufferMs: 150,
            supportedCommands: new List<string> { Commands.SetStaticDelay });

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
        var msg = ClientStateMessage.CreatePlayerState(
            volume: 100, muted: false, staticDelayMs: 0, requiredLeadTimeMs: 0, minBufferMs: 0);

        var json = MessageSerializer.Serialize(msg);

        Assert.Contains("\"static_delay_ms\":0", json);
        Assert.Contains("\"required_lead_time_ms\":0", json);
        Assert.Contains("\"min_buffer_ms\":0", json);
    }

    [Fact]
    public void ClientState_InitialMessage_CarriesStaticDelayAtItsDefault()
    {
        // The initial full state is where the presence requirement actually bites: aiosendspin
        // reads an omitted static_delay_ms as "unchanged", which on the first message leaves it
        // with no value at all rather than the 0 we meant.
        var msg = ClientStateMessage.CreateInitial(
            available: true, player: new PlayerStatePayload());

        Assert.Contains("\"static_delay_ms\":0", MessageSerializer.Serialize(msg));
    }

    [Fact]
    public void ClientState_StaticDelayIsAnIntegerOnTheWire()
    {
        // The spec types static_delay_ms as an integer. The scheduler's own delay is a double,
        // so the factory takes the projected wire value rather than the raw one — this pins
        // that the wire field cannot carry a fraction.
        var msg = ClientStateMessage.CreatePlayerState(
            volume: 100, muted: false, staticDelayMs: 250, requiredLeadTimeMs: 0, minBufferMs: 0);

        string json = MessageSerializer.Serialize(msg);

        Assert.Contains("\"static_delay_ms\":250", json);
        Assert.DoesNotContain("\"static_delay_ms\":250.", json);
    }

    [Fact]
    public void ClientState_SupportedCommandsOmittedWhenNull()
    {
        var msg = ClientStateMessage.CreatePlayerState(
            volume: 100, muted: false, staticDelayMs: 0, requiredLeadTimeMs: 0, minBufferMs: 0,
            supportedCommands: null);

        var json = MessageSerializer.Serialize(msg);

        Assert.DoesNotContain("supported_commands", json);
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
}
