using System.Text.Json;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Protocol;

public class MessageSerializerTests
{
    [Fact]
    public void Serialize_ClientTimeMessage_RoundTrips()
    {
        var original = new ClientTimeMessage
        {
            Payload = new ClientTimePayload { ClientTransmitted = 123456789 }
        };

        var json = MessageSerializer.Serialize(original);
        var deserialized = MessageSerializer.Deserialize<ClientTimeMessage>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("client/time", deserialized.Type);
        Assert.Equal(123456789, deserialized.Payload.ClientTransmitted);
    }

    [Fact]
    public void Serialize_UsesSnakeCaseNaming()
    {
        var msg = new ClientTimeMessage
        {
            Payload = new ClientTimePayload { ClientTransmitted = 100 }
        };

        var json = MessageSerializer.Serialize(msg);

        Assert.Contains("\"client_transmitted\"", json);
        Assert.DoesNotContain("\"ClientTransmitted\"", json);
    }

    [Fact]
    public void Deserialize_ServerHelloMessage_ParsesCorrectly()
    {
        var json = """
        {
            "type": "server/hello",
            "payload": {
                "server_id": "test-server",
                "name": "Test Server",
                "version": 1,
                "active_roles": ["player@v1"],
                "connection_reason": "discovery"
            }
        }
        """;

        var msg = MessageSerializer.Deserialize(json) as ServerHelloMessage;

        Assert.NotNull(msg);
        Assert.Equal("test-server", msg.ServerId);
        Assert.Equal("Test Server", msg.Name);
        Assert.Equal(1, msg.Version);
        Assert.Single(msg.ActiveRoles);
        Assert.Equal("discovery", msg.ConnectionReason);
    }

    [Fact]
    public void Deserialize_UnknownType_ReturnsNull()
    {
        var json = """{"type": "unknown/type", "payload": {}}""";
        var result = MessageSerializer.Deserialize(json);
        Assert.Null(result);
    }

    [Fact]
    public void Deserialize_AllServerMessageTypes_Succeeds()
    {
        var testCases = new Dictionary<string, Type>
        {
            ["server/hello"] = typeof(ServerHelloMessage),
            ["server/time"] = typeof(ServerTimeMessage),
            ["stream/start"] = typeof(StreamStartMessage),
            ["stream/end"] = typeof(StreamEndMessage),
            ["stream/clear"] = typeof(StreamClearMessage),
            ["group/update"] = typeof(GroupUpdateMessage),
            ["server/command"] = typeof(ServerCommandMessage),
        };

        foreach (var (type, expectedType) in testCases)
        {
            var json = $$"""{ "type": "{{type}}", "payload": {} }""";
            var msg = MessageSerializer.Deserialize(json);
            Assert.NotNull(msg);
            Assert.IsType(expectedType, msg);
        }
    }

    [Fact]
    public void Deserialize_StreamStartMessage_ParsesArtworkChannels()
    {
        var json = """
        {
            "type": "stream/start",
            "payload": {
                "artwork": {
                    "channels": [
                        { "source": "album", "format": "jpeg", "width": 512, "height": 512 }
                    ]
                }
            }
        }
        """;

        var msg = MessageSerializer.Deserialize(json) as StreamStartMessage;

        Assert.NotNull(msg);
        Assert.NotNull(msg.Payload.Artwork);
        var channel = Assert.Single(msg.Payload.Artwork.Channels);
        Assert.Equal("album", channel.Source);
        Assert.Equal("jpeg", channel.Format);
        Assert.Equal(512, channel.Width);
        Assert.Equal(512, channel.Height);
    }

    [Fact]
    public void Deserialize_StreamStartMessage_ArtworkAbsent_YieldsNull()
    {
        var json = """
        {
            "type": "stream/start",
            "payload": {
                "player": { "codec": "pcm", "sample_rate": 48000, "channels": 2, "bit_depth": 16 }
            }
        }
        """;

        var msg = MessageSerializer.Deserialize(json) as StreamStartMessage;

        Assert.NotNull(msg);
        Assert.Null(msg.Payload.Artwork);
        Assert.NotNull(msg.Payload.Format);
    }

    [Fact]
    public void GetMessageType_Utf8_ReadsOnlyTheRootObject()
    {
        // The span overload must classify exactly like the string overload: "type" is a
        // member of the root object, and a "type" nested inside another member (such as
        // a payload) does not make the document a message of that type — a document with
        // no root "type" is malformed however deep a "type" appears.
        Assert.Equal("a/b", MessageSerializer.GetMessageType(
            """{"payload":{"x":1},"type":"a/b"}"""u8.ToArray()));
        Assert.Throws<JsonException>(
            () => MessageSerializer.GetMessageType("""{"payload":{"type":"x"}}"""u8.ToArray()));
    }

    [Fact]
    public void ClientHello_NeverSerializesClientIdOrVersion()
    {
        var hello = ClientHelloMessage.Create(
            name: "test-client",
            supportedRoles: ["player@v1"]);

        string json = MessageSerializer.Serialize(hello);

        // Under encryption both travel in client/init instead.
        Assert.DoesNotContain("client_id", json);
        Assert.DoesNotContain("\"version\"", json);
    }

    [Fact]
    public void ServerActivate_DeserializesNestedPairingObject()
    {
        // Spec 5b0e6469 replaced the flat selected_pair_method with a pairing object
        // carrying method, pin_length and (spec #131) languages.
        const string json = """
            {"type":"server/activate","payload":{"activities":["pairing"],
            "active_roles":[],"pairing":{"method":"dynamic_pin","pin_length":8,
            "languages":["ca","es"]}}}
            """;

        var msg = MessageSerializer.Deserialize<ServerActivateMessage>(json);

        Assert.NotNull(msg);
        Assert.Equal("dynamic_pin", msg!.Payload.Pairing!.Method);
        Assert.Equal(8, msg.Payload.Pairing.PinLength);
        Assert.Equal(new[] { "ca", "es" }, msg.Payload.Pairing.Languages);
    }

    [Fact]
    public void ClientPairPending_SerializesWithPairingIndex()
    {
        var json = MessageSerializer.Serialize(new ClientPairPendingMessage
        {
            Payload = new ClientPairPendingPayload { PairingIndex = 3 },
        });

        Assert.Contains("\"type\":\"client/pair-pending\"", json, StringComparison.Ordinal);
        Assert.Contains("\"pairing_index\":3", json, StringComparison.Ordinal);
    }
}
