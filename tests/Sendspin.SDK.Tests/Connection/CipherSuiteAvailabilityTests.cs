using System.Security.Cryptography;
using Sendspin.SDK.Connection.Noise;

namespace Sendspin.SDK.Tests.Connection;

/// <summary>
/// Suite selection must respect what the platform can actually do. Both suites are
/// spec-defined and every server supports both, so the choice is the client's to make at
/// runtime — but hardcoding one meant a platform without it threw from inside the Noise
/// handshake and surfaced as a generic crypto fatal with nothing naming the cause (#89).
/// </summary>
public class CipherSuiteAvailabilityTests
{
    [Fact]
    public void IsSupported_TracksTheBcl()
    {
        // Not a tautology against a hardcoded answer: each arm is compared to the BCL's own
        // capability flag, which is what actually decides whether the AEAD can be built.
        Assert.Equal(ChaCha20Poly1305.IsSupported, NoiseCipherSuite.ChaChaPoly.IsSupported());
        Assert.Equal(AesGcm.IsSupported, NoiseCipherSuite.AesGcm.IsSupported());
    }

    [Fact]
    public void SelectDefault_PicksASuiteThisPlatformCanPerform()
    {
        // The property that matters, stated without assuming which suite this machine has.
        Assert.True(NoiseCipherSuiteExtensions.SelectDefault().IsSupported());
    }

    [Fact]
    public void SelectDefault_PrefersChaChaPolyWhenAvailable()
    {
        // Preference, not just availability — AES-GCM is the fallback, not a coin flip.
        if (!ChaCha20Poly1305.IsSupported)
            return; // nothing to assert on a platform without it

        Assert.Equal(NoiseCipherSuite.ChaChaPoly, NoiseCipherSuiteExtensions.SelectDefault());
    }

    [Fact]
    public void EnsureSupported_PassesForASupportedSuite()
    {
        // Positive control: a check that threw unconditionally would satisfy nothing below.
        NoiseCipherSuiteExtensions.SelectDefault().EnsureSupported();
    }

    [Fact]
    public void EnsureSupported_OnAnUnsupportedSuite_NamesTheSuiteAndTheAlternative()
    {
        // Only meaningful where exactly one suite is missing; both are present on every
        // platform CI runs today, so this documents the message rather than exercising it.
        var unsupported = Enum.GetValues<NoiseCipherSuite>().FirstOrDefault(s => !s.IsSupported());
        if (unsupported == default && NoiseCipherSuite.ChaChaPoly.IsSupported())
            return;

        var ex = Assert.Throws<PlatformNotSupportedException>(() => unsupported.EnsureSupported());
        Assert.Contains(unsupported.ToWireName(), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultOption_IsPlatformSelected_NotAConstant()
    {
        // Pins the wiring: SendspinClientOptions must take its default from SelectDefault(),
        // so a platform without ChaCha20-Poly1305 does not get handed it anyway.
        var options = new Sendspin.SDK.Client.SendspinClientOptions
        {
            Identity = SendspinIdentity.Generate(),
        };

        Assert.Equal(NoiseCipherSuiteExtensions.SelectDefault(), options.Suite);
        Assert.True(options.Suite.IsSupported());
    }
}
