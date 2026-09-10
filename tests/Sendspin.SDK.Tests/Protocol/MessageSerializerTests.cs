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

    /// <summary>
    /// The shape a conformant encrypted server actually sends: <c>name</c> and nothing else
    /// (messaging.md, "Server → Client: server/hello").
    /// </summary>
    /// <remarks>
    /// The test below deserializes a payload carrying four more fields, and was the only
    /// server/hello coverage there was — so nothing pinned the shape every real server sends,
    /// and a change that made any of those fields required would have left the suite green
    /// (#99).
    /// </remarks>
    [Fact]
    public void Deserialize_ServerHello_SpecMinimalShape_ParsesCorrectly()
    {
        const string json = """{"type":"server/hello","payload":{"name":"Test Server"}}""";

        var msg = MessageSerializer.Deserialize(json) as ServerHelloMessage;

        Assert.NotNull(msg);
        Assert.Equal("Test Server", msg.Name);

        // The absent fields land on their defaults rather than failing the parse. ServerId in
        // particular is empty here because the encrypted protocol carries it in server/init,
        // not server/hello — see ServerHelloPayload's remarks.
        Assert.Equal(string.Empty, msg.ServerId);
        Assert.Empty(msg.ActiveRoles);
        Assert.Null(msg.ConnectionReason);
    }

    /// <summary>
    /// Tolerance for the pre-encryption payload shape. Every field here except <c>name</c> is
    /// residue: the encrypted protocol carries <c>server_id</c> and <c>version</c> in
    /// <c>server/init</c>, <c>active_roles</c> in <c>server/activate</c>, and has no
    /// <c>connection_reason</c> at all. Kept because the properties are still public API and a
    /// server that sends them must not break the parse — not because any server does (#99).
    /// </summary>
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

            // #207: routed through the internal client by GetMessageType + typed Deserialize<T>,
            // so nothing in-repo noticed that this public entry point dropped them.
            ["server/state"] = typeof(ServerStateMessage),
            ["server/unpair"] = typeof(ServerUnpairMessage),
            ["server/activate"] = typeof(ServerActivateMessage),
            ["server/pair-finalize"] = typeof(ServerPairFinalizeMessage),
            ["pair/abort"] = typeof(PairAbortMessage),
            ["server/pair-init"] = typeof(ServerPairInitMessage),
            ["server/pair-auth"] = typeof(ServerPairAuthMessage),
            ["server/pair-confirm"] = typeof(ServerPairConfirmMessage),
        };

        foreach (var (type, expectedType) in testCases)
        {
            var json = $$"""{ "type": "{{type}}", "payload": {} }""";
            var msg = MessageSerializer.Deserialize(json);
            Assert.NotNull(msg);
            Assert.IsType(expectedType, msg);
        }
    }

    /// <summary>
    /// The client-authored half of the switch (#207). These are the messages this SDK sends, so
    /// nothing in-repo deserializes them — but a null return here was indistinguishable from a
    /// type the SDK has never heard of, which is what the entry point's contract now promises
    /// null means.
    /// </summary>
    [Fact]
    public void Deserialize_ClientAuthoredMessageTypes_Succeeds()
    {
        var testCases = new Dictionary<string, Type>
        {
            ["""{"type":"client/hello","payload":{"name":"c","supported_roles":[]}}"""] =
                typeof(ClientHelloMessage),
            ["""{"type":"client/goodbye","payload":{"reason":"user_request"}}"""] =
                typeof(ClientGoodbyeMessage),
            ["""{"type":"client/time","payload":{"client_transmitted":1}}"""] = typeof(ClientTimeMessage),
            ["""{"type":"client/state","payload":{"available":true}}"""] = typeof(ClientStateMessage),
            ["""{"type":"client/command","payload":{"controller":{"command":"play"}}}"""] =
                typeof(ClientCommandMessage),
            ["""{"type":"client/pair-finalize","payload":{}}"""] = typeof(ClientPairFinalizeMessage),
            ["""{"type":"client/pair-pending","payload":{"pairing_index":1}}"""] =
                typeof(ClientPairPendingMessage),
            ["""{"type":"client/pair-init","payload":{}}"""] = typeof(ClientPairInitMessage),
            ["""{"type":"client/pair-auth","payload":{}}"""] = typeof(ClientPairAuthMessage),
            ["""{"type":"client/pair-confirm","payload":{}}"""] = typeof(ClientPairConfirmMessage),
            ["""{"type":"stream/request-format","payload":{}}"""] = typeof(StreamRequestFormatMessage),
            ["""{"type":"client_stream/start","payload":{}}"""] = typeof(ClientStreamStartMessage),
            ["""{"type":"client_stream/end","payload":{}}"""] = typeof(ClientStreamEndMessage),
        };

        foreach (var (json, expectedType) in testCases)
        {
            Assert.IsType(expectedType, MessageSerializer.Deserialize(json));
        }
    }

    [Fact]
    public void Deserialize_ServerState_CarriesTheRoleObjectsThrough()
    {
        // server/state is the message #207 costs the most: a consumer dispatching on this entry
        // point lost every metadata, controller and color update with no error to notice.
        var msg = MessageSerializer.Deserialize("""
            {"type":"server/state","payload":{"metadata":{"title":"T"},"controller":{"volume":7},
             "color":null}}
            """);

        var state = Assert.IsType<ServerStateMessage>(msg);
        Assert.Equal("T", state.Payload.Metadata.Value!.Title.Value);
        Assert.Equal(7, state.Payload.Controller.Value!.Volume);
        Assert.True(state.Payload.Color.IsPresent);
        Assert.Null(state.Payload.Color.Value);
    }

    [Fact]
    public void Serialize_ServerUnpair_RoundTrips()
    {
        var json = MessageSerializer.Serialize(new ServerUnpairMessage());

        Assert.Contains("\"type\":\"server/unpair\"", json, StringComparison.Ordinal);
        Assert.Contains("\"payload\":{}", json, StringComparison.Ordinal);
        Assert.IsType<ServerUnpairMessage>(MessageSerializer.Deserialize(json));
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
        Assert.ThrowsAny<JsonException>(() => MessageSerializer.GetMessageType(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void ClientHello_NeverSerializesClientIdOrVersion()
    {
        var hello = ClientHelloMessage.Create(
            name: "test-client",
            supportedRoles: ["player@v1"]);

        string json = MessageSerializer.Serialize(hello);

        // Positive control first: without it every assertion below is satisfied by an empty or
        // malformed document, and this test would pass against a serializer that produced
        // nothing at all (#99).
        Assert.Contains("\"type\":\"client/hello\"", json);
        Assert.Contains("\"name\":\"test-client\"", json);
        Assert.Contains("\"supported_roles\":[\"player@v1\"]", json);

        // Under encryption both travel in client/init instead.
        Assert.DoesNotContain("client_id", json);
        Assert.DoesNotContain("\"version\"", json);
    }

    [Fact]
    public void ServerActivate_DeserializesNestedPairingObject()
    {
        // The pairing activation is {method, format?}: spec #178 dropped the negotiable
        // pin_length (a digits code is 6 digits) and moved the operator languages hint to
        // server/hello, and named the emission format the server picked instead.
        const string json = """
            {"type":"server/activate","payload":{"activities":["pairing"],
            "active_roles":[],"pairing":{"method":"dynamic_pairing_code","format":"digits"}}}
            """;

        var msg = MessageSerializer.Deserialize<ServerActivateMessage>(json);

        Assert.NotNull(msg);
        Assert.Equal("dynamic_pairing_code", msg!.Payload.Pairing!.Method);
        Assert.Equal("digits", msg.Payload.Pairing.Format);
    }

    [Fact]
    public void ClientHello_SerializesSupportedPairMethods_KeyedByMethod()
    {
        // Spec #179: supported_pair_methods is an object keyed by method identifier, not a
        // list of descriptors each repeating its own method name.
        var hello = ClientHelloMessage.Create(
            name: "Test",
            supportedRoles: ["player@v1"],
            supportedPairMethods: new Dictionary<string, PairMethodDescriptor>
            {
                ["pairing_psk"] = new() { Locations = ["device"] },
                ["dynamic_pairing_code"] = new() { OutChannels = ["display"], Formats = ["digits"] },
            });

        string json = MessageSerializer.Serialize(hello);

        using var doc = JsonDocument.Parse(json);
        var methods = doc.RootElement.GetProperty("payload").GetProperty("supported_pair_methods");
        Assert.Equal(JsonValueKind.Object, methods.ValueKind);

        var psk = methods.GetProperty("pairing_psk");
        Assert.Equal("device", psk.GetProperty("locations")[0].GetString());
        Assert.False(psk.TryGetProperty("method", out _), "the key is the method; it is not repeated");
        Assert.False(psk.TryGetProperty("out_channels", out _));

        var code = methods.GetProperty("dynamic_pairing_code");
        Assert.Equal("display", code.GetProperty("out_channels")[0].GetString());
        Assert.Equal("digits", code.GetProperty("formats")[0].GetString());
        Assert.False(code.TryGetProperty("min_pin_length", out _), "min_pin_length is retired");
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
    public void ClientPairPending_SerializesWithPairingIndexOnly()
    {
        // The payload is {pairing_index} and nothing else. Spec #178 reworked the pairing
        // vocabulary around it but added no free-text 'message' field: the reason the client
        // is pending is already implied by the message's existence, and anything a client
        // invented here would be untranslatable server-side.
        var json = MessageSerializer.Serialize(new ClientPairPendingMessage
        {
            Payload = new ClientPairPendingPayload { PairingIndex = 3 },
        });

        Assert.Contains("\"type\":\"client/pair-pending\"", json, StringComparison.Ordinal);
        Assert.Contains("\"pairing_index\":3", json, StringComparison.Ordinal);

        using var doc = JsonDocument.Parse(json);
        var payload = doc.RootElement.GetProperty("payload");
        Assert.Equal("pairing_index", Assert.Single(payload.EnumerateObject()).Name);
    }

    [Theory]
    [InlineData("attempt_timeout")]
    [InlineData("concurrent_attempt")]
    [InlineData("method_not_supported")]
    [InlineData("pairing_code_mismatch")]
    [InlineData("user_cancelled")]
    public void PairAbortReasons_CoverExactlyTheSpecVocabulary(string reason)
    {
        // Spec #178 renamed pin_mismatch to pairing_code_mismatch and retired
        // pin_length_unacceptable outright. Anything the SDK sends must be in this set.
        Assert.True(PairAbortReasons.IsDefined(reason));
        Assert.Contains(reason, PairAbortReasons.All);
    }

    [Theory]
    [InlineData("pin_mismatch")]
    [InlineData("pin_length_unacceptable")]
    public void RetiredPairAbortReasons_AreNotRecognized(string reason) =>
        Assert.False(PairAbortReasons.IsDefined(reason));

    private static byte[] Utf8(string json) => System.Text.Encoding.UTF8.GetBytes(json);
}
