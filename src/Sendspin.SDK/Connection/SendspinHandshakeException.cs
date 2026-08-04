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
        _ => $"Sendspin handshake failed: {kind}.",
    };
}
