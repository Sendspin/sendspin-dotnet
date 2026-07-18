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
}
