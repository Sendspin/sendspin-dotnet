using System.Security.Cryptography;

namespace Sendspin.SDK.Connection.Noise;

/// <summary>
/// The Noise KKpsk2 cipher suites defined by the Sendspin spec. Servers must support
/// both; the client picks one and announces it in <c>client/init</c>.
/// </summary>
public enum NoiseCipherSuite
{
    /// <summary>25519_ChaChaPoly_SHA256 - software-friendly suite.</summary>
    ChaChaPoly,

    /// <summary>25519_AESGCM_SHA256 - hardware-accelerated suite.</summary>
    AesGcm,
}

/// <summary>Wire-name helpers for <see cref="NoiseCipherSuite"/>.</summary>
public static class NoiseCipherSuiteExtensions
{
    /// <summary>The suite name as carried in <c>client/init</c>.</summary>
    public static string ToWireName(this NoiseCipherSuite suite) => suite switch
    {
        NoiseCipherSuite.ChaChaPoly => "25519_ChaChaPoly_SHA256",
        NoiseCipherSuite.AesGcm => "25519_AESGCM_SHA256",
        _ => throw new ArgumentOutOfRangeException(nameof(suite)),
    };

    /// <summary>The full Noise protocol name for the suite.</summary>
    public static string ToProtocolName(this NoiseCipherSuite suite) =>
        $"Noise_KKpsk2_{suite.ToWireName()}";

    /// <summary>
    /// Whether this platform's BCL can actually perform the suite's AEAD. Availability of
    /// <see cref="ChaCha20Poly1305"/> and <see cref="AesGcm"/> varies by platform and,
    /// for AES-GCM, by CPU.
    /// </summary>
    public static bool IsSupported(this NoiseCipherSuite suite) => suite switch
    {
        NoiseCipherSuite.ChaChaPoly => ChaCha20Poly1305.IsSupported,
        NoiseCipherSuite.AesGcm => AesGcm.IsSupported,
        _ => false,
    };

    /// <summary>
    /// The suite to use when the caller expresses no preference: ChaCha20-Poly1305 where the
    /// platform provides it, otherwise AES-GCM.
    /// </summary>
    /// <remarks>
    /// Both are spec-defined and every server supports both, so the choice is ours to make at
    /// runtime. Hardcoding ChaChaPoly meant a platform without it threw from inside the Noise
    /// handshake and surfaced as a generic crypto fatal, with nothing pointing at the cause.
    /// </remarks>
    public static NoiseCipherSuite SelectDefault() =>
        NoiseCipherSuite.ChaChaPoly.IsSupported()
            ? NoiseCipherSuite.ChaChaPoly
            : NoiseCipherSuite.AesGcm;

    /// <summary>
    /// Throws with an actionable message if the platform cannot perform <paramref name="suite"/>.
    /// Called before the handshake so the failure names the suite and the alternative, rather
    /// than arriving later as an opaque exception from inside the Noise state machine.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">The suite's AEAD is unavailable here.</exception>
    public static void EnsureSupported(this NoiseCipherSuite suite)
    {
        if (suite.IsSupported())
            return;

        NoiseCipherSuite other = suite == NoiseCipherSuite.ChaChaPoly
            ? NoiseCipherSuite.AesGcm
            : NoiseCipherSuite.ChaChaPoly;

        throw new PlatformNotSupportedException(
            $"This platform cannot perform {suite.ToWireName()}. "
            + (other.IsSupported()
                ? $"Set SendspinClientOptions.Suite to NoiseCipherSuite.{other} — servers support both."
                : "Neither Sendspin cipher suite is available on this platform."));
    }
}
