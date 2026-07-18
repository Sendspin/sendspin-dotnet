namespace Sendspin.SDK.Connection.Noise;

/// <summary>
/// Read-only view of an established Noise session, consumed by the client service to
/// drive the encrypted protocol flow (server identity from <c>server/init</c>, and the
/// matched PSK category for <c>server/activate</c> admissibility checks).
/// </summary>
public interface INoiseSessionInfo
{
    /// <summary>The server's id (its static public key) from server/init, once received.</summary>
    string? ServerId { get; }

    /// <summary>The PSK that authenticated the current session, once the handshake completes.</summary>
    NoisePsk? MatchedPsk { get; }

    /// <summary>The Noise handshake hash h, once the handshake completes (PIN pairing binds to it).</summary>
    ReadOnlyMemory<byte>? HandshakeHash { get; }
}
