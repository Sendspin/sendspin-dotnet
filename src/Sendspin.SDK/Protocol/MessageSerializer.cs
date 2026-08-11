using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Protocol;

/// <summary>
/// Handles serialization and deserialization of Sendspin protocol messages.
/// Uses source-generated JsonSerializerContext for NativeAOT compatibility.
/// </summary>
public static class MessageSerializer
{
    private static readonly MessageSerializerContext s_context = MessageSerializerContext.Default;

    private static JsonTypeInfo<T> GetTypeInfo<T>() =>
        (JsonTypeInfo<T>)s_context.GetTypeInfo(typeof(T))!;

    /// <summary>
    /// Resolves source-generated metadata for a message by its <b>runtime</b> type.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>typeof(T)</c>. A caller that holds a message through
    /// <see cref="IMessage"/> — the source role's send delegate is
    /// <c>Func&lt;IMessage, Task&gt;</c> — would otherwise ask the context for the interface,
    /// which has no entry, and serialization would fail on null metadata. That made
    /// <c>client_stream/start</c> unsendable, so the source role never streamed at all, and
    /// the only symptom was a swallowed ArgumentNullException naming 'jsonTypeInfo'.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The message type has no context entry.</exception>
    private static JsonTypeInfo GetTypeInfo(IMessage message) =>
        s_context.GetTypeInfo(message.GetType())
        ?? throw new InvalidOperationException(
            $"No source-generated metadata for {message.GetType()}. Add a [JsonSerializable] "
            + "entry to MessageSerializerContext — this is the mandatory transport path, and "
            + "reflection-based serialization breaks under PublishAot.");

    /// <summary>
    /// Serializes a message to JSON string.
    /// </summary>
    public static string Serialize<T>(T message) where T : IMessage
    {
        return JsonSerializer.Serialize(message, GetTypeInfo(message));
    }

    /// <summary>
    /// Serializes a message to UTF-8 bytes.
    /// </summary>
    public static byte[] SerializeToBytes<T>(T message) where T : IMessage
    {
        return JsonSerializer.SerializeToUtf8Bytes(message, GetTypeInfo(message));
    }

    /// <summary>
    /// Deserializes a JSON message, returning the appropriate message type.
    /// </summary>
    /// <exception cref="JsonException">
    /// The document is not a JSON object, or has no string <c>type</c> member. Both are
    /// malformed input rather than an unrecognised message: an unknown-but-well-formed
    /// type returns <see langword="null"/> for the caller to ignore.
    /// </exception>
    public static IMessage? Deserialize(string json)
    {
        var messageType = GetMessageType(json);
        return messageType switch
        {
            MessageTypes.ServerHello => JsonSerializer.Deserialize(json, s_context.ServerHelloMessage),
            MessageTypes.ServerActivate => JsonSerializer.Deserialize(json, s_context.ServerActivateMessage),
            MessageTypes.ServerPairFinalize => JsonSerializer.Deserialize(json, s_context.ServerPairFinalizeMessage),
            MessageTypes.PairAbort => JsonSerializer.Deserialize(json, s_context.PairAbortMessage),
            MessageTypes.ServerPairInit => JsonSerializer.Deserialize(json, s_context.ServerPairInitMessage),
            MessageTypes.ServerPairAuth => JsonSerializer.Deserialize(json, s_context.ServerPairAuthMessage),
            MessageTypes.ServerPairConfirm => JsonSerializer.Deserialize(json, s_context.ServerPairConfirmMessage),
            MessageTypes.ServerTime => JsonSerializer.Deserialize(json, s_context.ServerTimeMessage),
            MessageTypes.StreamStart => JsonSerializer.Deserialize(json, s_context.StreamStartMessage),
            MessageTypes.StreamEnd => JsonSerializer.Deserialize(json, s_context.StreamEndMessage),
            MessageTypes.StreamClear => JsonSerializer.Deserialize(json, s_context.StreamClearMessage),
            MessageTypes.GroupUpdate => JsonSerializer.Deserialize(json, s_context.GroupUpdateMessage),
            MessageTypes.ServerCommand => JsonSerializer.Deserialize(json, s_context.ServerCommandMessage),
            _ => null // Unknown message type
        };
    }

    /// <summary>
    /// Deserializes a specific message type.
    /// </summary>
    public static T? Deserialize<T>(string json) where T : class, IMessage
    {
        return JsonSerializer.Deserialize(json, GetTypeInfo<T>());
    }

    /// <summary>
    /// Gets the message type from a JSON string without full deserialization.
    /// </summary>
    /// <exception cref="JsonException">
    /// The input is not valid JSON, is not a JSON object, has no <c>type</c> member, or
    /// <c>type</c> is not a string. All of these are malformed input rather than an
    /// unrecognised message: an unknown-but-well-formed type is returned as-is for the
    /// caller to ignore.
    /// </exception>
    public static string GetMessageType(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"Message root is {root.ValueKind}, not a JSON object.");
        }

        if (!root.TryGetProperty("type", out var typeProp))
        {
            throw new JsonException("Message has no \"type\" member.");
        }

        if (typeProp.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"Message \"type\" member is {typeProp.ValueKind}, not a string.");
        }

        // ValueKind == String guarantees a non-null result; JsonElement.GetString() is
        // annotated nullable only because it also covers JsonValueKind.Null.
        return typeProp.GetString()!;
    }

    /// <summary>
    /// Gets the message type from a UTF-8 byte span without full deserialization.
    /// </summary>
    /// <exception cref="JsonException">
    /// The input is not valid JSON, is not a JSON object, has no <c>type</c> member, or
    /// <c>type</c> is not a string. All of these are malformed input rather than an
    /// unrecognised message: an unknown-but-well-formed type is returned as-is for the
    /// caller to ignore.
    /// </exception>
    public static string GetMessageType(ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(utf8Json);

        // The !reader.Read() branch is defensive rather than reachable: with a
        // single-segment, final-block reader (this constructor), Read() throws on empty
        // or whitespace-only input instead of returning false -- there is no "need more
        // data" case for a reader that already has the whole, final buffer.
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Message root is {reader.TokenType}, not a JSON object.");
        }

        // Root-only, matching the string overload: members of the root object sit at depth
        // 1, so a "type" nested inside another member (such as a payload) must not classify
        // the document.
        //
        // Record the latest match rather than stopping at the first: duplicate root-level
        // "type" members are legal JSON (RFC 8259 leaves duplicate-key semantics to the
        // implementation), and JsonDocument.TryGetProperty -- what the string overload
        // uses, and what JsonSerializer.Deserialize's typed routing agrees with -- is
        // last-wins. Deferring the non-string throw until the scan finishes is what makes
        // that possible: a `{"type":42,"type":"a"}` document must resolve to "a", not throw
        // on the first, superseded value.
        //
        // Draining the reader to the end of the document (rather than returning as soon as
        // the loop's target is settled) is what surfaces trailing content after the closing
        // brace as a JsonException on a subsequent Read().
        bool found = false;
        JsonTokenType valueToken = default;
        string? messageType = null;
        while (reader.Read())
        {
            if (reader.CurrentDepth == 1 &&
                reader.TokenType == JsonTokenType.PropertyName &&
                reader.ValueTextEquals("type"u8))
            {
                reader.Read();
                found = true;
                valueToken = reader.TokenType;
                messageType = valueToken == JsonTokenType.String ? reader.GetString() : null;
            }
        }

        if (!found)
        {
            throw new JsonException("Message has no \"type\" member.");
        }

        if (valueToken != JsonTokenType.String)
        {
            throw new JsonException($"Message \"type\" member is {valueToken}, not a string.");
        }

        return messageType!;
    }
}
