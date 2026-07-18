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
    /// Pairing method the server picked. Present exactly when 'pairing' is in
    /// activities.
    /// </summary>
    [JsonPropertyName("selected_pair_method")]
    public string? SelectedPairMethod { get; set; }
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

    /// <summary>Management operations (paired servers only).</summary>
    public const string Management = "management";
}
