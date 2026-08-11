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
    public void GetMessageType_String_ReadsOnlyTheRootObject()
    {
        // Same root-only rule as the span overload, verified on the string overload too —
        // the two must classify identically.
        Assert.Equal("a/b", MessageSerializer.GetMessageType(
            """{"payload":{"x":1},"type":"a/b"}"""));
        Assert.Throws<JsonException>(
            () => MessageSerializer.GetMessageType("""{"payload":{"type":"x"}}"""));
    }

    // #107: one rule for both overloads — input that is not a JSON object carrying a
    // string "type" member throws JsonException. Each row below used to be classified
    // differently (JsonException, InvalidOperationException, or a silently-returned
    // null/wrong value) depending on which overload and which way the input was wrong.
    [Theory]
    [InlineData("""{"type":null}""")]        // failed open on the string overload
    [InlineData("""{"type":42}""")]
    [InlineData("""{"type":{}}""")]
    [InlineData("[1,2]")]                     // non-object root
    [InlineData("42")]
    [InlineData("\"str\"")]
    [InlineData("{}")]                        // missing "type"
    public void GetMessageType_String_MalformedInput_Throws(string json)
    {
        Assert.Throws<JsonException>(() => MessageSerializer.GetMessageType(json));
    }

    [Theory]
    [InlineData("""{"type":null}""")]
    [InlineData("""{"type":42}""")]
    [InlineData("""{"type":{}}""")]
    [InlineData("[1,2]")]
    [InlineData("42")]
    [InlineData("\"str\"")]
    [InlineData("{}")]
    public void GetMessageType_Utf8_MalformedInput_Throws(string json)
    {
        Assert.Throws<JsonException>(() => MessageSerializer.GetMessageType(Utf8(json)));
    }

    [Fact]
    public void GetMessageType_String_TrailingGarbage_Throws()
    {
        // JsonDocument.Parse rejects trailing content by throwing JsonReaderException, a
        // JsonException subtype -- ThrowsAny (rather than Throws, which requires an exact
        // type match) is what confirms the thrown type still satisfies callers that catch
        // JsonException, matching SendSpinClient's dispatch filter.
        Assert.ThrowsAny<JsonException>(() => MessageSerializer.GetMessageType("""{"type":"a"}x"""));
    }

    [Fact]
    public void GetMessageType_Utf8_TrailingGarbage_Throws()
    {
        // Confirms the brief's claim: draining the reader past the matched "type" value
        // surfaces trailing content as a JsonReaderException (a JsonException subtype) on
        // a subsequent Read() — the span overload used to stop at the first match and
        // silently accept this.
        Assert.ThrowsAny<JsonException>(
            () => MessageSerializer.GetMessageType(Utf8("""{"type":"a"}x""")));
    }

    [Fact]
    public void GetMessageType_String_NormalMessage_ReturnsType()
    {
        Assert.Equal("server/hello",
            MessageSerializer.GetMessageType("""{"type":"server/hello","payload":{}}"""));
    }

    [Fact]
    public void GetMessageType_Utf8_NormalMessage_ReturnsType()
    {
        Assert.Equal("server/hello",
            MessageSerializer.GetMessageType(Utf8("""{"type":"server/hello","payload":{}}""")));
    }

    [Fact]
    public void GetMessageType_String_UnknownButWellFormedType_ReturnsAsIs()
    {
        // Positive control: without this, a change that throws on everything would still
        // pass every malformed-input test above.
        Assert.Equal("unknown/type", MessageSerializer.GetMessageType("""{"type":"unknown/type"}"""));
    }

    [Fact]
    public void GetMessageType_Utf8_UnknownButWellFormedType_ReturnsAsIs()
    {
        Assert.Equal("unknown/type",
            MessageSerializer.GetMessageType(Utf8("""{"type":"unknown/type"}""")));
    }

    [Fact]
    public void Deserialize_MissingTypeMember_Throws()
    {
        // #107 item 4: Deserialize(string) used to return null for a missing "type",
        // making "malformed" indistinguishable from "unknown type" (which still returns
        // null via the switch's default arm — see Deserialize_UnknownType_ReturnsNull).
        // No test pinned the old null-for-missing-type behaviour.
        Assert.Throws<JsonException>(() => MessageSerializer.Deserialize("{}"));
    }

    [Fact]
    public void Deserialize_NonStringTypeMember_Throws()
    {
        Assert.Throws<JsonException>(() => MessageSerializer.Deserialize("""{"type":null}"""));
    }

    // #107 review: a duplicate root-level "type" member is legal JSON (RFC 8259 leaves
    // duplicate-key semantics to the implementation), and the two overloads must resolve
    // it identically. JsonDocument.TryGetProperty (the string overload) is last-wins, and
    // so is JsonSerializer.Deserialize's typed routing, so the span overload must agree
    // rather than keep its first match.
    [Fact]
    public void GetMessageType_DuplicateTypeMember_LastString_BothOverloadsAgree()
    {
        const string json = """{"type":"a","type":"b"}""";
        Assert.Equal("b", MessageSerializer.GetMessageType(json));
        Assert.Equal("b", MessageSerializer.GetMessageType(Utf8(json)));
    }

    [Fact]
    public void GetMessageType_DuplicateTypeMember_LastNonString_BothOverloadsThrow()
    {
        const string json = """{"type":"a","type":42}""";
        Assert.Throws<JsonException>(() => MessageSerializer.GetMessageType(json));
        Assert.Throws<JsonException>(() => MessageSerializer.GetMessageType(Utf8(json)));
    }

    [Fact]
    public void GetMessageType_DuplicateTypeMember_FirstNonStringLastString_BothOverloadsAgree()
    {
        const string json = """{"type":42,"type":"a"}""";
        Assert.Equal("a", MessageSerializer.GetMessageType(json));
        Assert.Equal("a", MessageSerializer.GetMessageType(Utf8(json)));
    }

    [Fact]
    public void GetMessageType_Utf8_EmptyInput_Throws()
    {
        var ex = Assert.ThrowsAny<JsonException>(() => MessageSerializer.GetMessageType(ReadOnlySpan<byte>.Empty));
        Assert.IsType<JsonReaderException>(ex);
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
    public void Serialize_ThroughTheIMessageInterface_ResolvesTheRuntimeType()
    {
        // The source role's send delegate is Func<IMessage, Task>, so T binds to the
        // interface. Resolving metadata from typeof(T) asked the source-generated context
        // for IMessage, which has no entry, and serialization died on null metadata — so
        // client_stream/start could never be sent and the source role never streamed.
        IMessage message = new ClientStreamStartMessage();

        string json = MessageSerializer.Serialize(message);

        Assert.Contains("\"type\":\"client_stream/start\"", json, StringComparison.Ordinal);
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

    private static byte[] Utf8(string json) => System.Text.Encoding.UTF8.GetBytes(json);
}
