using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Noise;
using NoiseProtocol = Noise.Protocol;

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
    private static readonly Lazy<bool> ChaChaPolyProbe =
        new Lazy<bool>(() => Probe(NoiseCipherSuite.ChaChaPoly));

    private static readonly Lazy<bool> AesGcmProbe =
        new Lazy<bool>(() => Probe(NoiseCipherSuite.AesGcm));

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
    /// Whether this platform can actually perform the suite, probed against the backend that
    /// really runs it.
    /// </summary>
    /// <remarks>
    /// This deliberately does not consult <see cref="System.Security.Cryptography.ChaCha20Poly1305"/>
    /// or <see cref="System.Security.Cryptography.AesGcm"/>. Those report on the BCL, and the
    /// handshake never uses the BCL AEADs — Noise.NET P/Invokes libsodium for every primitive. Asking the BCL
    /// gave the wrong answer in both directions: it passed on platforms with no libsodium binary
    /// for the RID (every ARM target before libsodium was floated to 1.0.22), so the guard let
    /// the handshake through and it died on a bare <see cref="DllNotFoundException"/> — exactly
    /// the opaque failure the guard exists to prevent (#144).
    /// </remarks>
    public static bool IsSupported(this NoiseCipherSuite suite) => suite switch
    {
        NoiseCipherSuite.ChaChaPoly => ChaChaPolyProbe.Value,
        NoiseCipherSuite.AesGcm => AesGcmProbe.Value,
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
                : "Neither Sendspin cipher suite is available on this platform. Both run through "
                  + "libsodium, so this usually means no native libsodium binary shipped for this "
                  + $"runtime identifier ({RuntimeInformation.RuntimeIdentifier})."));
    }

    /// <summary>
    /// Runs one real KKpsk2 message-1 write for <paramref name="suite"/> and reports whether it
    /// completed. Noise.NET exposes no availability query, so the only honest way to answer
    /// "can this platform do this suite" is to do it once. Message 1 covers both primitives that
    /// can be missing: Curve25519 for the <c>e</c>/<c>es</c>/<c>ss</c> tokens and the suite's
    /// AEAD for the payload — AES-GCM in particular is a CPU-level question libsodium answers at
    /// runtime, not a build-time one. Cached, so the cost is paid once per process.
    /// </summary>
    private static bool Probe(NoiseCipherSuite suite)
    {
        try
        {
            using var local = KeyPair.Generate();
            using var remote = KeyPair.Generate();
            var protocol = NoiseProtocol.Parse(suite.ToProtocolName().AsSpan());

            using var state = protocol.Create(
                initiator: true,
                prologue: Array.Empty<byte>(),
                s: (byte[])local.PrivateKey.Clone(),
                rs: remote.PublicKey.ToArray(),
                psks: new[] { new byte[NoiseConstants.PskSize] });

            state.WriteMessage(Array.Empty<byte>(), new byte[NoiseProtocol.MaxMessageLength]);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException
            or EntryPointNotFoundException
            or TypeInitializationException
            or BadImageFormatException
            or NotSupportedException
            or PlatformNotSupportedException)
        {
            // A missing or unusable libsodium for this RID, or a CPU that cannot do the suite.
            // Anything else is not an availability answer and must not be swallowed as one.
            return false;
        }
    }
}
