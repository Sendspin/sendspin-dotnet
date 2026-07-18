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

    /// <summary>64-char base64url wrapped PSK (PIN flows only; not yet implemented).</summary>
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
    /// Abort reason: attempt_timeout, concurrent_attempt, locked_out,
    /// method_not_supported, pin_length_unacceptable, pin_mismatch, or user_cancelled.
    /// </summary>
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
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

    /// <summary>Out-channels conveying the per-session PIN (dynamic_pin only).</summary>
    [JsonPropertyName("out_channels")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? OutChannels { get; set; }

    /// <summary>Shortest acceptable PIN length in digits (dynamic_pin only, 4-12).</summary>
    [JsonPropertyName("min_pin_length")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MinPinLength { get; set; }

    /// <summary>Whether the method is in terminal lockout (PIN methods only).</summary>
    [JsonPropertyName("locked_out")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LockedOut { get; set; }
}

/// <summary>Starts a PIN-pairing attempt (client → server).</summary>
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

    /// <summary>Dynamic-PIN commitment over nonce_B (43-char base64url); absent in static PIN.</summary>
    [JsonPropertyName("commit_B")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CommitB { get; set; }
}

/// <summary>Server's nonce contribution in dynamic-PIN pairing.</summary>
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

    /// <summary>The PIN length in digits: max(client_min, server_min) clamped to 4-12.</summary>
    [JsonPropertyName("pin_length")]
    public int PinLength { get; set; }
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

/// <summary>Client's mutual-confirmation tag plus the dynamic-PIN commitment opening.</summary>
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

    /// <summary>The nonce_B preimage of commit_B (dynamic PIN only).</summary>
    [JsonPropertyName("nonce_B")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NonceB { get; set; }
}
