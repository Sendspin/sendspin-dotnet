using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Protocol;

/// <summary>
/// Wire-format coverage for the controller role's seek verbs: <c>position_ms</c> / <c>offset_ms</c>
/// on the client/command controller object, and <c>seek_max_ms</c> on the server/state one.
/// </summary>
public class ControllerSeekSerializationTests
{
    [Fact]
    public void ClientCommand_Seek_SerializesPositionMs()
    {
        var msg = ClientCommandMessage.Create(Commands.Seek, positionMs: 42_000);

        var json = MessageSerializer.Serialize(msg);

        Assert.Contains("\"command\":\"seek\"", json);
        Assert.Contains("\"position_ms\":42000", json);
        Assert.DoesNotContain("offset_ms", json);
    }

    [Fact]
    public void ClientCommand_SeekRelative_SerializesOffsetMs()
    {
        var msg = ClientCommandMessage.Create(Commands.SeekRelative, offsetMs: -15_000);

        var json = MessageSerializer.Serialize(msg);

        Assert.Contains("\"command\":\"seek_relative\"", json);
        Assert.Contains("\"offset_ms\":-15000", json);
        Assert.DoesNotContain("position_ms", json);
    }

    [Fact]
    public void ClientCommand_WithoutSeekParameters_OmitsBothFields()
    {
        var json = MessageSerializer.Serialize(ClientCommandMessage.Create(Commands.Play));

        Assert.DoesNotContain("position_ms", json);
        Assert.DoesNotContain("offset_ms", json);
    }

    [Fact]
    public void ServerState_SeekMaxMs_Present_IsParsed()
    {
        const string Json = """
            {
                "type": "server/state",
                "payload": {
                    "controller": {
                        "supported_commands": ["play", "seek", "seek_relative"],
                        "volume": 50,
                        "muted": false,
                        "seek_max_ms": 245000
                    }
                }
            }
            """;

        var msg = MessageSerializer.Deserialize<ServerStateMessage>(Json);

        Assert.NotNull(msg?.Payload.Controller);
        Assert.Equal(245_000, msg.Payload.Controller.SeekMaxMs);
        Assert.Equal(
            new[] { "play", "seek", "seek_relative" },
            msg.Payload.Controller.SupportedCommands);
    }

    [Fact]
    public void ServerState_SeekMaxMs_Absent_IsNull()
    {
        // The server omits seek_max_ms when the seekable range is unknown (e.g. live streams),
        // and drops 'seek' from supported_commands along with it.
        const string Json = """
            {
                "type": "server/state",
                "payload": {
                    "controller": { "supported_commands": ["play", "seek_relative"], "volume": 50 }
                }
            }
            """;

        var msg = MessageSerializer.Deserialize<ServerStateMessage>(Json);

        Assert.NotNull(msg?.Payload.Controller);
        Assert.Null(msg.Payload.Controller.SeekMaxMs);
    }
}
