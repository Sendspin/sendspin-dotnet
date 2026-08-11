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
    /// type is returned as-is (null) for the caller to ignore.
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
    public static string? GetMessageType(string json)
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

        return typeProp.GetString();
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
    public static string? GetMessageType(ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(utf8Json);

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Message root is not a JSON object.");
        }

        string? messageType = null;
        while (reader.Read())
        {
            // Root-only, matching the string overload: members of the root object sit at
            // depth 1, so a "type" nested inside another member (such as a payload) must
            // not classify the document. Once found, keep draining the reader instead of
            // returning immediately -- a subsequent Read() surfaces trailing content after
            // the closing brace (e.g. `{"type":"a"}x`) as a JsonException, which is what
            // catches malformed input the first match would otherwise let through.
            if (messageType is null &&
                reader.CurrentDepth == 1 &&
                reader.TokenType == JsonTokenType.PropertyName &&
                reader.ValueTextEquals("type"u8))
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.String)
                {
                    throw new JsonException(
                        $"Message \"type\" member is {reader.TokenType}, not a string.");
                }

                messageType = reader.GetString();
            }
        }

        return messageType ?? throw new JsonException("Message has no \"type\" member.");
    }
}
