using System.Text.Json.Serialization;

namespace Sendspin.SDK.Protocol.Messages;

/// <summary>
/// Delivers the long-term PSK for this (client, server) pair. In the Pairing PSK flow
/// it starts the pairing attempt, sent immediately after the pairing
/// <c>server/activate</c>, carrying the PSK directly.
/// </summary>
public sealed class ClientPairFinalizeMessage : IMessageWithPayload<ClientPairFinalizePayload>
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.ClientPairFinalize;

    [JsonPropertyName("payload")]
    public ClientPairFinalizePayload Payload { get; set; } = new();
}

/// <summary>Payload of <c>client/pair-finalize</c>.</summary>
public sealed class ClientPairFinalizePayload
{
    /// <summary>43-char base64url 32-byte Sendspin PSK (Pairing PSK flow only).</summary>
    [JsonPropertyName("long_term_psk")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LongTermPsk { get; set; }

    /// <summary>64-char base64url wrapped PSK (pairing code flows only; not yet implemented).</summary>
    [JsonPropertyName("wrapped_psk")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WrappedPsk { get; set; }
}

/// <summary>
/// Acknowledges that the server has persisted the pairing record. The client persists
/// its own record only after receiving this.
/// </summary>
public sealed class ServerPairFinalizeMessage : IMessageWithPayload<ServerPairFinalizePayload>
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.ServerPairFinalize;

    [JsonPropertyName("payload")]
    public ServerPairFinalizePayload Payload { get; set; } = new();
}

/// <summary>Payload of <c>server/pair-finalize</c> (empty).</summary>
public sealed class ServerPairFinalizePayload
{
}

/// <summary>Aborts a pairing attempt, started or not.</summary>
public sealed class PairAbortMessage : IMessageWithPayload<PairAbortPayload>
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.PairAbort;

    [JsonPropertyName("payload")]
    public PairAbortPayload Payload { get; set; } = new();
}

/// <summary>Payload of <c>pair/abort</c>.</summary>
public sealed class PairAbortPayload
{
    /// <summary>
    /// Abort reason: attempt_timeout, concurrent_attempt, method_not_supported,
    /// pin_length_unacceptable, pin_mismatch, or user_cancelled.
    /// </summary>
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Reports that the selected attempt is gesture-gated and no pairing window is open. Sent
/// immediately on receiving such a pairing <c>server/activate</c>; <c>client/pair-init</c>
/// follows once a window opens. Does not start the attempt or its timeout.
/// </summary>
public sealed class ClientPairPendingMessage : IMessageWithPayload<ClientPairPendingPayload>
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.ClientPairPending;

    [JsonPropertyName("payload")]
    public ClientPairPendingPayload Payload { get; set; } = new();
}

/// <summary>Payload of <c>client/pair-pending</c>.</summary>
public sealed class ClientPairPendingPayload
{
    /// <summary>Number of pairing server/activate messages received since the last Noise handshake.</summary>
    [JsonPropertyName("pairing_index")]
    public int PairingIndex { get; set; }
}

/// <summary>
/// A pair-method descriptor advertised in <c>client/hello</c>'s
/// <c>supported_pair_methods</c>.
/// </summary>
public sealed class PairMethodDescriptor
{
    /// <summary>The method identifier: pairing_psk, dynamic_pin, or static_pin.</summary>
    [JsonPropertyName("method")]
    public string Method { get; set; } = "pairing_psk";

    /// <summary>Out-channels conveying the per-session pairing code (dynamic_pin only).</summary>
    [JsonPropertyName("out_channels")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? OutChannels { get; set; }

    /// <summary>Shortest acceptable pairing code length in digits (dynamic_pin only, 4-12).</summary>
    [JsonPropertyName("min_pin_length")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MinPairingCodeLength { get; set; }

    /// <summary>
    /// Where the operator can find this method's configured secret — <c>"device"</c> (printed
    /// on it), <c>"leaflet"</c> (in the box), or <c>"operator"</c> (they set it themselves).
    /// Informational, and for <c>static_pin</c> and <c>pairing_psk</c> only.
    /// </summary>
    /// <remarks>
    /// Drives server UX copy such as "check the label on the device", so it has to follow the
    /// secret: the spec requires the client to update the hint when the secret is rotated,
    /// because a stale hint sends the operator to a label that no longer matches. See
    /// <see cref="Client.ClientCapabilities.StaticPairingCodeLocations"/>.
    /// </remarks>
    [JsonPropertyName("locations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Locations { get; set; }
}

/// <summary>
/// The values a pair-method descriptor's <c>locations</c> hint may carry.
/// </summary>
/// <remarks>
/// Constants rather than literals for the same reason as
/// <see cref="Activities"/>: this is wire vocabulary, and a literal at a use site does not
/// follow a change to it (#93). The hint is informational and never grounds for
/// <c>pair/abort</c>, so an out-of-vocabulary entry does not break pairing — the SDK passes
/// through whatever the app declares rather than policing it, as it does for
/// <see cref="Client.ClientCapabilities.PairingCodeOutChannels"/>.
/// </remarks>
public static class PairMethodLocations
{
    /// <summary>Printed on the device itself.</summary>
    public const string Device = "device";

    /// <summary>Printed on a leaflet in the box.</summary>
    public const string Leaflet = "leaflet";

    /// <summary>Set by the operator, who therefore already knows it.</summary>
    public const string Operator = "operator";
}

/// <summary>Starts a pairing code attempt (client → server).</summary>
public sealed class ClientPairInitMessage : IMessageWithPayload<ClientPairInitPayload>
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.ClientPairInit;

    [JsonPropertyName("payload")]
    public ClientPairInitPayload Payload { get; set; } = new();
}

/// <summary>Payload of <c>client/pair-init</c>.</summary>
public sealed class ClientPairInitPayload
{
    /// <summary>Number of pairing server/activate messages received since the last Noise handshake.</summary>
    [JsonPropertyName("pairing_index")]
    public int PairingIndex { get; set; }

    /// <summary>Dynamic-pairing code commitment over nonce_B (43-char base64url); absent in static pairing code.</summary>
    [JsonPropertyName("commit_B")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CommitB { get; set; }
}

/// <summary>Server's nonce contribution in dynamic-pairing code.</summary>
public sealed class ServerPairInitMessage : IMessageWithPayload<ServerPairInitPayload>
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.ServerPairInit;

    [JsonPropertyName("payload")]
    public ServerPairInitPayload Payload { get; set; } = new();
}

/// <summary>Payload of <c>server/pair-init</c>.</summary>
public sealed class ServerPairInitPayload
{
    /// <summary>32 bytes from a CSPRNG, base64url (43 chars).</summary>
    [JsonPropertyName("nonce_A")]
    public string NonceA { get; set; } = string.Empty;
}

/// <summary>Server's CPace public share.</summary>
public sealed class ServerPairAuthMessage : IMessageWithPayload<ServerPairAuthPayload>
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.ServerPairAuth;

    [JsonPropertyName("payload")]
    public ServerPairAuthPayload Payload { get; set; } = new();
}

/// <summary>Payload of <c>server/pair-auth</c>.</summary>
public sealed class ServerPairAuthPayload
{
    /// <summary>Server's CPace public share Ya (32 bytes base64url, 43 chars).</summary>
    [JsonPropertyName("pake_msg_1")]
    public string PakeMsg1 { get; set; } = string.Empty;
}

/// <summary>Client's CPace public share.</summary>
public sealed class ClientPairAuthMessage : IMessageWithPayload<ClientPairAuthPayload>
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.ClientPairAuth;

    [JsonPropertyName("payload")]
    public ClientPairAuthPayload Payload { get; set; } = new();
}

/// <summary>Payload of <c>client/pair-auth</c>.</summary>
public sealed class ClientPairAuthPayload
{
    /// <summary>Client's CPace public share Yb (32 bytes base64url, 43 chars).</summary>
    [JsonPropertyName("pake_msg_2")]
    public string PakeMsg2 { get; set; } = string.Empty;
}

/// <summary>Server's mutual-confirmation tag.</summary>
public sealed class ServerPairConfirmMessage : IMessageWithPayload<ServerPairConfirmPayload>
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.ServerPairConfirm;

    [JsonPropertyName("payload")]
    public ServerPairConfirmPayload Payload { get; set; } = new();
}

/// <summary>Payload of <c>server/pair-confirm</c>.</summary>
public sealed class ServerPairConfirmPayload
{
    /// <summary>Server's MCF tag Ta (64 bytes base64url, 86 chars).</summary>
    [JsonPropertyName("server_kc")]
    public string ServerKc { get; set; } = string.Empty;
}

/// <summary>Client's mutual-confirmation tag plus the dynamic-pairing code commitment opening.</summary>
public sealed class ClientPairConfirmMessage : IMessageWithPayload<ClientPairConfirmPayload>
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.ClientPairConfirm;

    [JsonPropertyName("payload")]
    public ClientPairConfirmPayload Payload { get; set; } = new();
}

/// <summary>Payload of <c>client/pair-confirm</c>.</summary>
public sealed class ClientPairConfirmPayload
{
    /// <summary>Client's MCF tag Tb (64 bytes base64url, 86 chars).</summary>
    [JsonPropertyName("client_kc")]
    public string ClientKc { get; set; } = string.Empty;

    /// <summary>The nonce_B preimage of commit_B (dynamic pairing code only).</summary>
    [JsonPropertyName("nonce_B")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NonceB { get; set; }
}
