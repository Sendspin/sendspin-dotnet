using Sendspin.SDK.Audio;

namespace Sendspin.SDK.Tests.Audio;

/// <summary>
/// The two guards the one-shot snap tier was missing (issue #252), in isolation: a snap is not
/// eligible again within its own duration, and a run of snaps that leave the error where they
/// found it stands the tier down in favour of the capped continuous one.
/// </summary>
public class HardSyncStallDetectorTests
{
    private const long InBandError = -90_000;

    private static HardSyncStallDetector NewDetector() => new(SyncCorrectionOptions.Default);

    [Fact]
    public void Snap_IsNotEligibleAgainWithinItsOwnDuration()
    {
        var detector = NewDetector();

        Assert.False(detector.ShouldStandDown(InBandError, 0));
        detector.RecordSnap(InBandError, 0);

        // A 90 ms splice must not be followed by another 30 ms later, which is the rate at which
        // the tier was re-firing.
        Assert.True(detector.ShouldStandDown(InBandError, 30_000));
        Assert.True(detector.ShouldStandDown(InBandError, 89_999));
        Assert.False(detector.ShouldStandDown(InBandError, 90_000));

        // One snap that achieved nothing is not yet a stall — the tier is allowed to try again.
        Assert.False(detector.IsStalled);
    }

    [Fact]
    public void ThreeSnapsThatMoveNothing_StandTheTierDown()
    {
        var detector = NewDetector();
        var now = 0L;

        for (var i = 0; i < 3; i++)
        {
            Assert.False(detector.ShouldStandDown(InBandError, now));
            Assert.False(detector.IsStalled);
            detector.RecordSnap(InBandError, now);
            now += Math.Abs(InBandError);
        }

        Assert.True(detector.ShouldStandDown(InBandError, now));
        Assert.True(detector.IsStalled);
    }

    [Fact]
    public void SnapsThatShrinkTheError_NeverStallTheTier()
    {
        var detector = NewDetector();
        var error = InBandError;
        var now = 0L;

        // A tier that is closing the error must be allowed to keep going, however many steps it
        // takes. Each snap here leaves a quarter of what it found, still inside the band.
        for (var i = 0; i < 3; i++)
        {
            Assert.False(detector.ShouldStandDown(error, now));
            detector.RecordSnap(error, now);
            now += Math.Abs(error);
            error /= 4;
        }

        Assert.False(detector.IsStalled);
    }

    [Fact]
    public void ErrorLeavingTheBand_ClearsTheStall()
    {
        var detector = StalledDetector(out var now);

        // Below the snap threshold there is nothing for this tier to do, so it starts clean.
        Assert.False(detector.ShouldStandDown(1_000, now));
        Assert.False(detector.IsStalled);
        Assert.False(detector.ShouldStandDown(InBandError, now));
    }

    [Fact]
    public void ErrorPastTheReanchorCeiling_ClearsTheStall()
    {
        var detector = StalledDetector(out var now);

        // Past the ceiling the catastrophic tier owns the error; this one is no longer the one
        // failing, and must be ready again for whatever the buffer restarts into.
        Assert.False(detector.ShouldStandDown(-600_000, now));
        Assert.False(detector.IsStalled);
    }

    [Fact]
    public void Reset_ClearsTheStallAndTheCooldown()
    {
        var detector = StalledDetector(out var now);

        detector.Reset();

        Assert.False(detector.IsStalled);
        Assert.False(detector.ShouldStandDown(InBandError, now));
    }

    [Fact]
    public void DisabledSnapTier_NeverStandsDown()
    {
        // HardSyncThresholdMicroseconds = 0 turns the tier off, and the whole band collapses:
        // every error is above the threshold and the detector must not start suppressing a tier
        // that is not firing.
        var detector = new HardSyncStallDetector(
            new SyncCorrectionOptions { HardSyncThresholdMicroseconds = 0 });

        for (var i = 0; i < 10; i++)
        {
            Assert.False(detector.ShouldStandDown(InBandError, i * 90_000L));
        }

        Assert.False(detector.IsStalled);
    }

    /// <summary>A detector already stood down, with the playback clock it got there on.</summary>
    private static HardSyncStallDetector StalledDetector(out long now)
    {
        var detector = NewDetector();
        now = 0;

        for (var i = 0; i < 3; i++)
        {
            detector.ShouldStandDown(InBandError, now);
            detector.RecordSnap(InBandError, now);
            now += Math.Abs(InBandError);
        }

        detector.ShouldStandDown(InBandError, now);
        Assert.True(detector.IsStalled);
        return detector;
    }
}
