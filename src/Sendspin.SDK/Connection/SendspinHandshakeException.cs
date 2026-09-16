namespace Sendspin.SDK.Connection;

/// <summary>Why a Sendspin handshake failed permanently.</summary>
public enum HandshakeFailureKind
{
    /// <summary>
    /// The peer closed the connection during client/init without replying — the
    /// signature of a server predating the encrypted protocol (aiosendspin &lt; 7.0.0).
    /// </summary>
    LegacyServer,

    /// <summary>
    /// The peer spoke the encrypted protocol but the handshake was rejected: an
    /// unusable PSK, an unsupported suite, a version mismatch, or malformed input.
    /// </summary>
    HandshakeRejected,

    /// <summary>
    /// The server answered <c>client/init</c> with a cleartext <c>server/error</c> instead of
    /// <c>server/init</c>. The exception's <see cref="Exception.Message"/> carries the spec
    /// <c>reason</c> — normally one of <c>unsupported_version</c>, <c>unsupported_suite</c>, or
    /// <c>malformed</c>, but a malformed or missing <c>reason</c> yields the detail
    /// <c>unknown</c>, so do not assume it is one of those three values. That reason arrives
    /// before any key is established and so is unauthenticated: treat it as a hint for logging
    /// and operator display, not a trusted fact.
    /// </summary>
    ServerError,

    /// <summary>
    /// A stored PSK is bound to a different <c>server_id</c> than the server presenting it: the
    /// pairing record on one side is stale, so no retry can succeed — pair again. (A re-handshake
    /// <c>psk_id</c> miss is not this: it reconnects and self-heals via the Sentinel fallback.)
    /// </summary>
    PairingStateDiverged,
}

/// <summary>
/// A permanent handshake failure. Retrying cannot succeed, so the connection does not
/// re-enter the reconnect loop when this is raised.
/// </summary>
public sealed class SendspinHandshakeException : Exception
{
    public SendspinHandshakeException(HandshakeFailureKind kind, string? detail = null)
        : base(BuildMessage(kind, detail))
    {
        Kind = kind;
    }

    /// <summary>The classification of this failure.</summary>
    public HandshakeFailureKind Kind { get; }

    private static string BuildMessage(HandshakeFailureKind kind, string? detail) => kind switch
    {
        HandshakeFailureKind.LegacyServer =>
            "Server closed the connection during client/init and does not support Sendspin "
            + "encryption. Upgrade the server to aiosendspin >= 7.0.0, or pin Sendspin SDK 9.x.",
        HandshakeFailureKind.HandshakeRejected =>
            $"Sendspin handshake rejected: {detail ?? "no detail"}.",
        HandshakeFailureKind.ServerError =>
            $"Server rejected client/init with reason '{detail ?? "unknown"}'.",
        HandshakeFailureKind.PairingStateDiverged =>
            $"Sendspin pairing state has diverged; re-pair (retrying cannot help): {detail ?? "no detail"}.",
        _ => $"Sendspin handshake failed: {kind}.",
    };
}
