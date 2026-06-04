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
        var msg = ClientStateMessage.CreateSynchronized(
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
        // Per spec these are "always required for players", so they must serialize even at zero
        // (unlike static_delay_ms, which is omitted at its default).
        var msg = ClientStateMessage.CreateSynchronized(requiredLeadTimeMs: 0, minBufferMs: 0);

        var json = MessageSerializer.Serialize(msg);

        Assert.Contains("\"required_lead_time_ms\":0", json);
        Assert.Contains("\"min_buffer_ms\":0", json);
    }

    [Fact]
    public void ClientState_SupportedCommandsOmittedWhenNull()
    {
        var msg = ClientStateMessage.CreateSynchronized(supportedCommands: null);

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
