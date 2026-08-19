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

        var controller = msg?.Payload.Controller.Value;
        Assert.NotNull(controller);
        Assert.True(controller.SeekMaxMs.IsPresent);
        Assert.Equal(245_000, controller.SeekMaxMs.Value);
        Assert.Equal(new[] { "play", "seek", "seek_relative" }, controller.SupportedCommands);
    }

    [Fact]
    public void ServerState_SeekMaxMs_Absent_IsAbsentNotNull()
    {
        // A partial update that says nothing about the seekable range: absent, which the merge
        // reads as "keep the bound you have". Distinguishing this from the explicit null below is
        // the whole reason the leaf is Optional rather than a plain nullable.
        const string Json = """
            {
                "type": "server/state",
                "payload": {
                    "controller": { "supported_commands": ["play", "seek"], "volume": 50 }
                }
            }
            """;

        var msg = MessageSerializer.Deserialize<ServerStateMessage>(Json);

        var controller = msg?.Payload.Controller.Value;
        Assert.NotNull(controller);
        Assert.True(controller.SeekMaxMs.IsAbsent);
    }

    [Fact]
    public void ServerState_SeekMaxMs_ExplicitNull_IsPresentWithNull()
    {
        // The server nulls the leaf out when the seekable range becomes unknown — e.g. a seekable
        // track giving way to a live stream — and drops 'seek' from supported_commands with it.
        const string Json = """
            {
                "type": "server/state",
                "payload": {
                    "controller": { "supported_commands": ["play", "seek_relative"], "seek_max_ms": null }
                }
            }
            """;

        var msg = MessageSerializer.Deserialize<ServerStateMessage>(Json);

        var controller = msg?.Payload.Controller.Value;
        Assert.NotNull(controller);
        Assert.True(controller.SeekMaxMs.IsPresent);
        Assert.Null(controller.SeekMaxMs.Value);
    }
}
