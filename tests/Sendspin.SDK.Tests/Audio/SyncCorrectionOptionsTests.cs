using Sendspin.SDK.Audio;

namespace Sendspin.SDK.Tests.Audio;

public class SyncCorrectionOptionsTests
{
    [Fact]
    public void Default_ResamplingBandEqualsWhatTheContinuousTierCanClose()
    {
        var options = new SyncCorrectionOptions();

        // Issue #267: the band is derived from what the rate tier can actually close at the cap —
        // EffectiveMaxSpeedCorrection × CorrectionTargetSeconds = 0.005 × 3.0 s = 15 ms — rather
        // than set independently, so it can never describe a band the continuous tier cannot reach.
        Assert.Equal(15_000, options.ResamplingThresholdMicroseconds);
    }

    [Fact]
    public void OverPermissiveSpeedCap_DoesNotWidenTheDerivedResamplingBand()
    {
        // The band reads the clamped EffectiveMaxSpeedCorrection, not the configured cap, so a
        // configuration above the spec cap cannot re-describe a band the clamped correction still
        // cannot reach — the incoherence #267 removes must stay unrepresentable.
        var options = new SyncCorrectionOptions { MaxSpeedCorrection = 0.02 };

        Assert.Equal(15_000, options.ResamplingThresholdMicroseconds);
    }

    [Fact]
    public void Default_DeadbandMatchesTheReferenceSoftSyncThreshold()
    {
        // Issue #235: the dead band used to sit at 1 ms, exactly the spec's MUST floor, which
        // makes the ±0.5 ms SHOULD target unreachable by construction. 100 µs is the spec's
        // suggested band (roles/player/v1.md:172) and the reference's SOFT_SYNC_THRESHOLD_US.
        Assert.Equal(100, SyncCorrectionOptions.Default.DeadbandMicroseconds);
    }

    [Fact]
    public void Default_MaxSpeedCorrectionIsTheSpecCap()
    {
        // Issue #228: the default was 2% and the CLI preset 4%, both far past the spec's
        // ±0.5% MUST (roles/player/v1.md:134).
        Assert.Equal(0.005, SyncCorrectionOptions.SpecMaxSpeedCorrection);
        Assert.Equal(
            SyncCorrectionOptions.SpecMaxSpeedCorrection,
            SyncCorrectionOptions.Default.MaxSpeedCorrection);
    }

    [Fact]
    public void CliDefaults_AreWithinTheSpecCap()
    {
        var cli = SyncCorrectionOptions.CliDefaults;

        Assert.True(cli.MaxSpeedCorrection <= SyncCorrectionOptions.SpecMaxSpeedCorrection);
        Assert.Equal(100, cli.DeadbandMicroseconds);

        // Derived from the CLI's shorter 2 s target and the spec cap: 0.005 × 2.0 s = 10 ms.
        Assert.Equal(10_000, cli.ResamplingThresholdMicroseconds);
        cli.Validate();
    }

    [Fact]
    public void SpeedCorrectionAboveTheSpecCap_IsClampedNotRejected()
    {
        // Rejecting would stop a client whose configuration predates the cap from starting at
        // all — a worse outcome than playing it in conformance. The configured value is left
        // visible so the app can see what it asked for; only what is applied is clamped.
        var options = new SyncCorrectionOptions { MaxSpeedCorrection = 0.02 };

        options.Validate();

        Assert.Equal(0.02, options.MaxSpeedCorrection);
        Assert.True(options.ExceedsSpecSpeedCap);
        Assert.Equal(SyncCorrectionOptions.SpecMaxSpeedCorrection, options.EffectiveMaxSpeedCorrection);
        Assert.Equal(1.0 + SyncCorrectionOptions.SpecMaxSpeedCorrection, options.MaxRate);
        Assert.Equal(1.0 - SyncCorrectionOptions.SpecMaxSpeedCorrection, options.MinRate);
    }

    [Fact]
    public void Validate_StillRejectsNonsensicalSpeedCorrection()
    {
        Assert.Throws<ArgumentException>(
            new SyncCorrectionOptions { MaxSpeedCorrection = 0 }.Validate);
        Assert.Throws<ArgumentException>(
            new SyncCorrectionOptions { MaxSpeedCorrection = 1.5 }.Validate);
    }

    [Fact]
    public void Default_HardSyncTierSitsBetweenRateCorrectionAndReanchor()
    {
        var options = SyncCorrectionOptions.Default;

        // Issue #232: 5 ms, matching HARD_SYNC_THRESHOLD_US in the reference.
        Assert.Equal(5_000, options.HardSyncThresholdMicroseconds);
        Assert.True(options.HardSyncThresholdMicroseconds < options.ReanchorThresholdMicroseconds);
    }

    [Fact]
    public void Validate_RejectsHardSyncThresholdAtOrAboveReanchor()
    {
        var options = new SyncCorrectionOptions { HardSyncThresholdMicroseconds = 500_000 };

        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void Clone_CopiesHardSyncThreshold()
    {
        var options = new SyncCorrectionOptions { HardSyncThresholdMicroseconds = 7_000 };

        Assert.Equal(7_000, options.Clone().HardSyncThresholdMicroseconds);
    }

    [Fact]
    public void Default_StillValidates()
    {
        var options = new SyncCorrectionOptions();
        options.Validate(); // must not throw with the new threshold
    }

    [Fact]
    public void TrackClockDrift_DefaultsToTrue()
    {
        Assert.True(SyncCorrectionOptions.Default.TrackClockDrift);
    }

    [Fact]
    public void Clone_CopiesTrackClockDrift()
    {
        var options = new SyncCorrectionOptions { TrackClockDrift = false };
        Assert.False(options.Clone().TrackClockDrift);
    }
}
