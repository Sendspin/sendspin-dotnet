using System.Text.Json.Serialization;

namespace Sendspin.SDK.Protocol.Messages;

/// <summary>
/// A paired server dropping its own pairing record from the client. Valid at any time regardless
/// of the connection's current activities; carries no payload fields, so the client's handler
/// reads nothing off it. Modelled so <see cref="MessageSerializer.Deserialize(string)"/> can name
/// the type rather than returning null for a message every client must act on (#207).
/// </summary>
/// <remarks>
/// Lives beside the pairing messages rather than in a management file: the spec removed the
/// <c>management/*</c> family, and <c>server/unpair</c> outlived it as the one unpairing message
/// on the wire.
/// </remarks>
public sealed class ServerUnpairMessage : IMessageWithPayload<ServerUnpairPayload>
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.ServerUnpair;

    [JsonPropertyName("payload")]
    public ServerUnpairPayload Payload { get; set; } = new();
}

/// <summary>Payload of <c>server/unpair</c> (empty).</summary>
public sealed class ServerUnpairPayload
{
}
