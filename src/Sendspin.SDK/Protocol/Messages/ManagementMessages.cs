using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sendspin.SDK.Protocol.Messages;

/// <summary>
/// Response to a <c>management/*</c> request. At most one management request is in
/// flight per connection, so ordering alone matches replies to requests.
/// </summary>
public sealed class ManagementResultMessage : IMessageWithPayload<ManagementResultPayload>
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.ManagementResult;

    [JsonPropertyName("payload")]
    public ManagementResultPayload Payload { get; set; } = new();
}

/// <summary>
/// A paired server dropping its own pairing record from the client. Valid at any time regardless
/// of the connection's current activities; carries no payload fields, so the client's handler
/// reads nothing off it. Modelled so <see cref="MessageSerializer.Deserialize(string)"/> can name
/// the type rather than returning null for a message every client must act on (#207).
/// </summary>
public sealed class ServerUnpairMessage : IMessage
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.ServerUnpair;
}

/// <summary>Payload of <c>management/result</c>.</summary>
public sealed class ManagementResultPayload
{
    /// <summary>
    /// Result code: ok, permission_denied, already_exists, invalid, not_found, or
    /// storage_exhausted.
    /// </summary>
    [JsonPropertyName("result")]
    public string Result { get; set; } = "ok";

    /// <summary>Operation-specific response payload; present only on ok when defined.</summary>
    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Data { get; set; }
}
