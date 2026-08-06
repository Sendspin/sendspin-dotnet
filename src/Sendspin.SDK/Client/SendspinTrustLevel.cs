namespace Sendspin.SDK.Client;

/// <summary>
/// How far the current session's peer is trusted. Under the encrypted protocol every session
/// is authenticated and encrypted, so this describes <em>trust</em>, not confidentiality —
/// <see cref="Unpaired"/> does not mean the connection is in the clear.
/// </summary>
public enum SendspinTrustLevel
{
    /// <summary>No session, or the handshake has not completed.</summary>
    None = 0,

    /// <summary>
    /// Authenticated with the published Sentinel PSK. The peer proved nothing beyond knowing a
    /// constant anyone can read, so it is authenticated but untrusted.
    /// </summary>
    Unpaired = 1,

    /// <summary>Authenticated with the bootstrap Pairing PSK; pairing is in progress.</summary>
    Pairing = 2,

    /// <summary>Authenticated with a long-term PSK from a completed pairing.</summary>
    Paired = 3,
}
