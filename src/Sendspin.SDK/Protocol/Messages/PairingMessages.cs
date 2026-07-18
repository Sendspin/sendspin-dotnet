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
}
