using System.Text.Json.Serialization;

namespace Sendspin.SDK.Protocol.Messages;

/// <summary>
/// Server response to client hello, confirming role activations.
/// Uses the envelope format: { "type": "server/hello", "payload": { ... } }
/// </summary>
public sealed class ServerHelloMessage : IMessageWithPayload<ServerHelloPayload>
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.ServerHello;

    [JsonPropertyName("payload")]
    public ServerHelloPayload Payload { get; set; } = new();

    // Convenience accessors (excluded from serialization)
    [JsonIgnore]
    public string ServerId => Payload.ServerId;
    [JsonIgnore]
    public string? Name => Payload.Name;
    [JsonIgnore]
    public int Version => Payload.Version;
    [JsonIgnore]
    public List<string> ActiveRoles => Payload.ActiveRoles;
    [JsonIgnore]
    public string? ConnectionReason => Payload.ConnectionReason;
}

/// <summary>
/// Payload for the server/hello message.
/// </summary>
/// <remarks>
/// <para>
/// Under the encrypted protocol the spec defines exactly one field here — <c>name</c>
/// (messaging.md, "Server → Client: <c>server/hello</c>"). The four properties below it are
/// residue from the pre-encryption protocol, retained rather than removed because they are
/// public API and dropping them would break compilation for consumers reading them. They are
/// left populated-if-present so a server that still sends them does not fail the parse, but no
/// conformant encrypted server sends any of them, and application code should not read them:
/// </para>
/// <list type="bullet">
/// <item><see cref="ServerId"/> and <see cref="Version"/> travel in <c>server/init</c>, which
/// is part of the Noise handshake. Read the server id from the session
/// (<c>INoiseSessionInfo.ServerId</c>) — it is the server's static public key and is
/// authenticated, which a value out of a JSON payload is not.</item>
/// <item><see cref="ActiveRoles"/> is carried by <c>server/activate</c>. The SDK deliberately
/// reuses this property as the mirror for that grant, so it is the one member here that does
/// hold a meaningful value after a handshake — but it is written by the activate handler, not
/// parsed from this message.</item>
/// <item><see cref="ConnectionReason"/> has no equivalent in the encrypted protocol at all.</item>
/// </list>
/// <para>
/// Recorded here because #99 found the decision was nowhere in the codebase; whether the
/// properties are eventually removed belongs with the wider public-surface work (#77).
/// </para>
/// </remarks>
public sealed class ServerHelloPayload
{
    /// <summary>
    /// Unique server identifier. Not sent by an encrypted server — see the remarks on
    /// <see cref="ServerHelloPayload"/> and read the session's server id instead.
    /// </summary>
    [JsonPropertyName("server_id")]
    public string ServerId { get; set; } = string.Empty;

    /// <summary>
    /// Server name.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// BCP 47 language tags in descending operator preference (e.g. <c>["ca", "es", "en"]</c>)
    /// — a hint about the languages the operator understands, informing any operator-facing
    /// output. Optional; null when the server sends none.
    /// </summary>
    /// <remarks>
    /// The SDK forwards this to <see cref="Client.PairingCodePresentation.Languages"/>, which is
    /// where it was read from before the spec moved the hint off <c>server/activate</c>'s
    /// pairing object onto this message.
    /// </remarks>
    [JsonPropertyName("languages")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Languages { get; set; }

    /// <summary>
    /// Protocol version. Not sent by an encrypted server — the version is negotiated in
    /// <c>client/init</c>/<c>server/init</c>. See the remarks on <see cref="ServerHelloPayload"/>.
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    /// <summary>
    /// Roles the server has activated for this client, as versioned role strings (e.g.
    /// <c>["player@v1", "controller@v1"]</c>). Not parsed from <c>server/hello</c>: the SDK
    /// writes it from each <c>server/activate</c>, for which it is the single field the rest of
    /// the client reads the role grant from. Empty until the first activate of a session.
    /// </summary>
    [JsonPropertyName("active_roles")]
    public List<string> ActiveRoles { get; set; } = new();

    /// <summary>
    /// Reason for this connection (e.g. "discovery", "playback"). No equivalent exists in the
    /// encrypted protocol, so this is always null in practice. See the remarks on
    /// <see cref="ServerHelloPayload"/>.
    /// </summary>
    [JsonPropertyName("connection_reason")]
    public string? ConnectionReason { get; set; }
}
