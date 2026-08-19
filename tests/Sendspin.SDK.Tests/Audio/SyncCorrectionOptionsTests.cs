using Sendspin.SDK.Audio;

namespace Sendspin.SDK.Tests.Audio;

public class SyncCorrectionOptionsTests
{
    [Fact]
    public void Default_RoutesModerateErrorsThroughResampling()
    {
        var options = new SyncCorrectionOptions();

        // Errors up to 100ms are corrected inaudibly via playback-rate
        // adjustment; audible frame drop/insert is reserved for errors
        // rate correction can't close (9.0.3 item 3).
        Assert.Equal(100_000, options.ResamplingThresholdMicroseconds);
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
        cli.Validate();
    }

    [Fact]
    public void Validate_RejectsSpeedCorrectionAboveTheSpecCap()
    {
        var options = new SyncCorrectionOptions { MaxSpeedCorrection = 0.02 };

        var ex = Assert.Throws<ArgumentException>(options.Validate);
        Assert.Contains("MaxSpeedCorrection", ex.Message, StringComparison.Ordinal);
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
