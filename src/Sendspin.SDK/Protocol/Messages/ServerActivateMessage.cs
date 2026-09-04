using System.Text.Json.Serialization;

namespace Sendspin.SDK.Protocol.Messages;

/// <summary>
/// Declares the server's current purpose on this connection (encrypted protocol).
/// Sent after <c>client/hello</c>; may be re-sent any time to change the activity set.
/// No other client messages should flow before the initial <c>server/activate</c>.
/// </summary>
public sealed class ServerActivateMessage : IMessageWithPayload<ServerActivatePayload>
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.ServerActivate;

    [JsonPropertyName("payload")]
    public ServerActivatePayload Payload { get; set; } = new();
}

/// <summary>
/// Payload of <c>server/activate</c> per the Sendspin spec.
/// </summary>
public sealed class ServerActivatePayload
{
    /// <summary>
    /// The set of currently-active purposes on this connection. Members are drawn from
    /// <see cref="Activities"/>; may be empty.
    /// </summary>
    [JsonPropertyName("activities")]
    public List<string> ActivitiesList { get; set; } = new();

    /// <summary>
    /// Versioned roles active for this client. Required on the first
    /// <c>server/activate</c>; persists across subsequent messages that omit it.
    /// </summary>
    [JsonPropertyName("active_roles")]
    public List<string>? ActiveRoles { get; set; }

    /// <summary>
    /// Parameters of the pairing attempt this activation admits. Present exactly when
    /// 'pairing' is in activities; ignored otherwise.
    /// </summary>
    [JsonPropertyName("pairing")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PairingActivation? Pairing { get; set; }
}

/// <summary>
/// The <c>pairing</c> object on <c>server/activate</c>: which method the server picked and
/// the parameters that method needs.
/// </summary>
public sealed class PairingActivation
{
    /// <summary>
    /// The pairing method the server picked, drawn from the client's
    /// <c>supported_pair_methods</c>. One of <see cref="PairMethods"/>.
    /// </summary>
    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    /// <summary>
    /// The dynamic emission format for this attempt, drawn from the client's
    /// <c>dynamic_pairing_code</c> descriptor. Required when <see cref="Method"/> is
    /// <c>dynamic_pairing_code</c>; absent otherwise. See <see cref="PairingCodeFormats"/>.
    /// </summary>
    /// <remarks>
    /// Validated on receipt of the activation rather than at <c>server/pair-init</c>: a format
    /// the client does not offer is <c>pair/abort</c> <c>method_not_supported</c>, which the
    /// spec expects before <c>client/pair-init</c> is sent. This replaces the earlier
    /// <c>pin_length</c> field — the dynamic pairing code is now a fixed 6 digits.
    /// </remarks>
    [JsonPropertyName("format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Format { get; set; }
}

/// <summary>
/// The activity identifiers a <c>server/activate</c> may declare.
/// </summary>
public static class Activities
{
    /// <summary>Normal playback and control flows.</summary>
    public const string Playback = "playback";

    /// <summary>A pairing exchange is in progress.</summary>
    public const string Pairing = "pairing";
}
