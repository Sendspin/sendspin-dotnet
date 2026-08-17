using Sendspin.SDK.Connection.Framing;
using Sendspin.SDK.Connection.Noise;

namespace Sendspin.SDK.Tests.Connection;

/// <summary>
/// Suite selection must respect what the platform can actually do. Both suites are
/// spec-defined and every server supports both, so the choice is the client's to make at
/// runtime — but hardcoding one meant a platform without it threw from inside the Noise
/// handshake and surfaced as a generic crypto fatal with nothing naming the cause (#89).
///
/// "What the platform can actually do" means libsodium, not the BCL: Noise.NET P/Invokes
/// libsodium for every primitive and never touches <c>System.Security.Cryptography</c>'s
/// AEADs. These tests therefore compare the probe against a real handshake rather than
/// against the BCL's capability flags, which is what they used to do (#144).
/// </summary>
public class CipherSuiteAvailabilityTests
{
    [Fact]
    public void IsSupported_TracksTheRealBackend_NotTheBcl()
    {
        // Previously this asserted IsSupported() == ChaCha20Poly1305.IsSupported /
        // AesGcm.IsSupported. That pinned the wrong contract: the BCL flags describe a
        // library the handshake never calls, so they answered "supported" on every ARM
        // target, where libsodium had no native binary and the handshake died on
        // DllNotFoundException. The real question is whether the handshake runs.
        foreach (var suite in Enum.GetValues<NoiseCipherSuite>())
        {
            Assert.Equal(suite.IsSupported(), CompletesRealHandshake(suite));
        }
    }

    [Fact]
    public void SelectDefault_PicksASuiteThisPlatformCanPerform()
    {
        // The property that matters, stated without assuming which suite this machine has.
        Assert.True(NoiseCipherSuiteExtensions.SelectDefault().IsSupported());
    }

    [Fact]
    public void SelectDefault_ReturnsASuiteWhoseHandshakeActuallyCompletes()
    {
        // SelectDefault's promise, checked against the thing it is a promise about. This is
        // the end-to-end version of the test above: a probe that reported on the wrong
        // backend would satisfy IsSupported() and fail here.
        Assert.True(CompletesRealHandshake(NoiseCipherSuiteExtensions.SelectDefault()));
    }

    [Fact]
    public void SelectDefault_PrefersChaChaPolyWhenAvailable()
    {
        // Preference, not just availability — AES-GCM is the fallback, not a coin flip.
        if (!NoiseCipherSuite.ChaChaPoly.IsSupported())
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
        // PairingCodes the wiring: SendspinClientOptions must take its default from SelectDefault(),
        // so a platform without ChaCha20-Poly1305 does not get handed it anyway.
        var options = new Sendspin.SDK.Client.SendspinClientOptions
        {
            Identity = SendspinIdentity.Generate(),
        };

        Assert.Equal(NoiseCipherSuiteExtensions.SelectDefault(), options.Suite);
        Assert.True(options.Suite.IsSupported());
    }

    /// <summary>
    /// Drives a genuine KKpsk2 handshake for <paramref name="suite"/> through the production
    /// framing against <see cref="TestNoiseServer"/>, returning whether it reached transport
    /// mode. Goes through the real path on purpose — the point is to check the probe against
    /// reality, not against a second copy of the probe, which would pass on both sides of a
    /// systematic error.
    /// </summary>
    private static bool CompletesRealHandshake(NoiseCipherSuite suite)
    {
        var identity = SendspinIdentity.Generate();
        var framing = new NoiseWireFraming(identity, pskResolver: null, suite);
        var server = new TestNoiseServer(
            identity.PublicKey, NoiseConstants.SentinelPsk.ToArray(), suite: suite);

        var clientInit = Assert.Single(framing.Start());
        var (serverInit, msg1) = server.Respond(clientInit.PayloadAsText());

        Assert.Null(framing.ProcessInbound(WireFrame.FromText(serverInit)).FatalReason);
        var result = framing.ProcessInbound(WireFrame.FromText(msg1));
        Assert.Null(result.FatalReason);

        server.CompleteHandshake(Assert.Single(result.Replies!).PayloadAsText());
        return framing.IsTransportReady;
    }
}
