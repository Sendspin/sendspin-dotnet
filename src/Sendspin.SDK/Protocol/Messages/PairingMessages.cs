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
    /// <summary>The abort reason; one of <see cref="PairAbortReasons"/>.</summary>
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// The closed set of <c>pair/abort</c> reasons the spec defines
/// (pairing.md, "Client ↔ Server: <c>pair/abort</c>").
/// </summary>
/// <remarks>
/// Constants rather than literals for the same reason as <see cref="GoodbyeReasons"/>: the
/// reason is wire vocabulary a peer branches on, and a literal at a call site does not follow
/// a change to it. The spec's earlier <c>pin_mismatch</c> and <c>pin_length_unacceptable</c>
/// are gone: key-confirmation failure is now <see cref="PairingCodeMismatch"/>, and an
/// emission format the client does not offer is <see cref="MethodNotSupported"/>.
/// </remarks>
public static class PairAbortReasons
{
    /// <summary>The attempt did not complete within the attempt timeout (client).</summary>
    public const string AttemptTimeout = "attempt_timeout";

    /// <summary>Another pairing attempt is already in progress with this client (client).</summary>
    public const string ConcurrentAttempt = "concurrent_attempt";

    /// <summary>
    /// The activation's method or emission format is not one this client currently offers, or
    /// the activity set and method are not a permitted combination for the matched PSK (client).
    /// </summary>
    public const string MethodNotSupported = "method_not_supported";

    /// <summary>PAKE key confirmation failed (client or server).</summary>
    public const string PairingCodeMismatch = "pairing_code_mismatch";

    /// <summary>The operator aborted the pairing through a local UI (client or server).</summary>
    public const string UserCancelled = "user_cancelled";

    /// <summary>Every reason the spec defines, for validating a received value.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        AttemptTimeout, ConcurrentAttempt, MethodNotSupported, PairingCodeMismatch, UserCancelled,
    ];

    /// <summary>Whether the spec defines a reason.</summary>
    /// <param name="reason">The reason to check.</param>
    /// <returns><see langword="true"/> if <paramref name="reason"/> is in <see cref="All"/>.</returns>
    public static bool IsDefined(string reason) => All.Contains(reason, StringComparer.Ordinal);
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
/// A pair-method descriptor: one value of <c>client/hello</c>'s
/// <c>supported_pair_methods</c> object, whose key is the method identifier
/// (<see cref="PairMethods"/>).
/// </summary>
/// <remarks>
/// <para>
/// The descriptor no longer names its own method: spec #179 re-keyed
/// <c>supported_pair_methods</c> from a list of self-describing descriptors into an object
/// keyed by method identifier, so the method lives in the key and the value carries only the
/// operator-interaction hints for it.
/// </para>
/// <para>
/// Which members apply depends on the key: <c>pairing_psk</c> and <c>static_pairing_code</c>
/// carry <see cref="Locations"/> only; <c>dynamic_pairing_code</c> carries
/// <see cref="OutChannels"/> and <see cref="Formats"/>. The spec's <c>digit_audio</c> object
/// is not modelled — it is required only for a client that emits the code over a speaker from
/// a server-supplied digit audio pack, which this SDK does not implement, so it never
/// advertises the <c>'speaker'</c> out-channel.
/// </para>
/// </remarks>
public sealed class PairMethodDescriptor
{
    /// <summary>
    /// Out-channels conveying the per-session pairing code to the operator
    /// (<c>dynamic_pairing_code</c> only).
    /// </summary>
    [JsonPropertyName("out_channels")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? OutChannels { get; set; }

    /// <summary>
    /// The emission formats the client offers for the per-session pairing code
    /// (<c>dynamic_pairing_code</c> only). Non-empty; see <see cref="PairingCodeFormats"/>.
    /// </summary>
    [JsonPropertyName("formats")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Formats { get; set; }

    /// <summary>
    /// Where the operator can find this method's configured secret — <c>"device"</c> (printed
    /// on it), <c>"leaflet"</c> (in the box), or <c>"operator"</c> (they set it themselves).
    /// Informational, and for <c>static_pairing_code</c> and <c>pairing_psk</c> only.
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
/// The pairing method identifiers: the keys of <c>supported_pair_methods</c> and the values
/// <c>server/activate</c>'s <c>pairing.method</c> may name.
/// </summary>
/// <remarks>
/// Every client implements <see cref="PairingPsk"/>. A client may additionally offer <em>at
/// most one</em> pairing-code method — <see cref="StaticPairingCode"/> or
/// <see cref="DynamicPairingCode"/>, never both (spec #189).
/// </remarks>
public static class PairMethods
{
    /// <summary>Pairing authenticated by the client's pairing PSK; no PAKE round, no code.</summary>
    public const string PairingPsk = "pairing_psk";

    /// <summary>Pairing with a per-session pairing code emitted through an out-channel.</summary>
    public const string DynamicPairingCode = "dynamic_pairing_code";

    /// <summary>Pairing with the device's fixed 8-digit pairing code.</summary>
    public const string StaticPairingCode = "static_pairing_code";

    /// <summary>The two pairing-code methods, of which a client may offer at most one.</summary>
    public static IReadOnlyList<string> PairingCodeMethods { get; } =
    [
        DynamicPairingCode, StaticPairingCode,
    ];

    /// <summary>Whether the identifier is one of the two pairing-code methods.</summary>
    /// <param name="method">The method identifier to check.</param>
    /// <returns><see langword="true"/> for the static and dynamic pairing-code methods.</returns>
    public static bool IsPairingCodeMethod(string method) =>
        PairingCodeMethods.Contains(method, StringComparer.Ordinal);
}

/// <summary>
/// The emission formats a <c>dynamic_pairing_code</c> descriptor may advertise, and that
/// <c>server/activate</c>'s <c>pairing.format</c> may name.
/// </summary>
public static class PairingCodeFormats
{
    /// <summary>The 6-digit decimal code, shown or spoken to the operator.</summary>
    public const string Digits = "digits";

    /// <summary>A QR code of the per-session pairing token. Not implemented by this SDK.</summary>
    public const string QrCode = "qr_code";
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
