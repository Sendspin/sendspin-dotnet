using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Tests.Synchronization;

public class KalmanClockSynchronizerTests
{
    private readonly KalmanClockSynchronizer _sync = new();

    [Fact]
    public void GetStatus_Initial_ReturnsNotConverged()
    {
        var status = _sync.GetStatus();

        Assert.Equal(0, status.MeasurementCount);
        Assert.False(status.IsConverged);
        Assert.False(status.IsDriftReliable);
    }

    [Fact]
    public void GetStatus_AfterOneMeasurement_NotYetMinimalSync()
    {
        _sync.ProcessMeasurement(0, 1000, 1100, 2000);

        Assert.False(_sync.HasMinimalSync);
        var status = _sync.GetStatus();
        Assert.Equal(1, status.MeasurementCount);
        Assert.False(status.IsConverged);
    }

    [Fact]
    public void GetStatus_AfterTwoMeasurements_HasMinimalSync()
    {
        _sync.ProcessMeasurement(0, 1000, 1100, 2000);
        _sync.ProcessMeasurement(100_000, 101_000, 101_100, 102_000);

        Assert.True(_sync.HasMinimalSync);
        var status = _sync.GetStatus();
        Assert.Equal(2, status.MeasurementCount);
    }

    [Fact]
    public void GetStatus_MatchesPropertyAccessors()
    {
        // Feed enough measurements with realistic 1-second intervals
        for (int i = 0; i < 20; i++)
        {
            long t1 = i * 1_000_000L;
            long t2 = t1 + 5000;
            long t3 = t2 + 100;
            long t4 = t1 + 10_000;
            _sync.ProcessMeasurement(t1, t2, t3, t4);
        }

        var status = _sync.GetStatus();

        Assert.Equal(_sync.IsConverged, status.IsConverged);
        Assert.Equal(_sync.IsDriftReliable, status.IsDriftReliable);
        Assert.Equal(_sync.MeasurementCount, status.MeasurementCount);
        Assert.Equal(_sync.Offset, status.OffsetMicroseconds);
        Assert.Equal(_sync.Drift, status.DriftMicrosecondsPerSecond);
        Assert.Equal(_sync.OffsetUncertainty, status.OffsetUncertaintyMicroseconds);
    }

    [Fact]
    public void GetStatus_AfterManyMeasurements_Converges()
    {
        // Use realistic LAN timing: 1-second intervals, consistent 5ms offset, ~2ms RTT
        for (int i = 0; i < 50; i++)
        {
            long t1 = i * 1_000_000L;
            long t2 = t1 + 5000;  // 5ms offset
            long t3 = t2 + 100;   // 100μs server processing
            long t4 = t1 + 2000;  // ~2ms RTT
            _sync.ProcessMeasurement(t1, t2, t3, t4);
        }

        var status = _sync.GetStatus();

        Assert.True(status.IsConverged, $"Expected converged but uncertainty was {status.OffsetUncertaintyMicroseconds:F0}μs");
        Assert.True(status.MeasurementCount >= 5);
        Assert.True(status.OffsetUncertaintyMicroseconds < 1000.0);
    }

    [Fact]
    public void Reset_ClearsState()
    {
        _sync.ProcessMeasurement(0, 5000, 5100, 10_000);
        _sync.ProcessMeasurement(100_000, 105_000, 105_100, 110_000);

        _sync.Reset();

        var status = _sync.GetStatus();
        Assert.Equal(0, status.MeasurementCount);
        Assert.False(status.IsConverged);
        Assert.Equal(0, status.OffsetMicroseconds);
    }

    [Fact]
    public void ClientToServerTime_AndBack_RoundTrips()
    {
        // Feed consistent measurements with ~5000μs offset, 1-second intervals
        for (int i = 0; i < 10; i++)
        {
            long t1 = i * 1_000_000L;
            long t2 = t1 + 5000;
            long t3 = t2 + 100;
            long t4 = t1 + 10_000;
            _sync.ProcessMeasurement(t1, t2, t3, t4);
        }

        long clientTime = 500_000L;
        long serverTime = _sync.ClientToServerTime(clientTime);
        long roundTripped = _sync.ServerToClientTime(serverTime);

        // Should round-trip within a few microseconds (rounding from double→long)
        Assert.InRange(Math.Abs(roundTripped - clientTime), 0, 5);
    }

    [Fact]
    public void StaticDelay_AffectsServerToClientTime()
    {
        _sync.ProcessMeasurement(0, 5000, 5100, 10_000);
        _sync.ProcessMeasurement(100_000, 105_000, 105_100, 110_000);

        long serverTime = 200_000L;
        long withoutDelay = _sync.ServerToClientTime(serverTime);

        _sync.StaticDelayMs = 10.0; // 10ms = 10000μs
        long withDelay = _sync.ServerToClientTime(serverTime);

        Assert.Equal(10_000, withDelay - withoutDelay);
    }

    // =========================================================================
    // Drift significance gate (SNR / z-score) — see upstream time-filter PR #5
    // https://github.com/Sendspin/time-filter/pull/5
    //
    // Drift compensation must only apply when |drift| > k × σ_drift (default k=2).
    // The previous absolute-threshold gate (σ_drift < 50 µs/s regardless of drift
    // magnitude) erroneously applied noise-dominated drift estimates.
    // =========================================================================

    [Fact]
    public void IsDriftReliable_RejectsDriftWhenSignalIsBelowNoise()
    {
        // Constant offset, no real drift in the input.
        // The filter's drift estimate will be ≈ 0 with some uncertainty σ_drift.
        // SNR = |0| / σ_drift = 0, which fails the z >= 2 test for any σ.
        //
        // Under the previous absolute-threshold gate, this would erroneously
        // return true once σ_drift fell below 50 µs/s (which it does after enough
        // converging measurements). The SNR gate correctly rejects it.
        for (int i = 0; i < 30; i++)
        {
            long t1 = i * 1_000_000L;
            _sync.ProcessMeasurement(t1, t1 + 5000, t1 + 5100, t1 + 2000);
        }

        var status = _sync.GetStatus();

        // Sanity: drift estimate is near zero with no real drift in input
        Assert.True(Math.Abs(status.DriftMicrosecondsPerSecond) < 10.0,
            $"Expected drift ≈ 0 with constant-offset input, got {status.DriftMicrosecondsPerSecond:F2} µs/s");

        var z = Math.Abs(status.DriftMicrosecondsPerSecond) /
                Math.Max(1e-9, status.DriftUncertaintyMicrosecondsPerSecond);

        Assert.False(status.IsDriftReliable,
            $"Drift {status.DriftMicrosecondsPerSecond:F2} µs/s ± {status.DriftUncertaintyMicrosecondsPerSecond:F1} (z={z:F2}) " +
            "should not be 'reliable' — z-score is below the 2σ significance threshold.");
    }

    [Fact]
    public void IsDriftReliable_AcceptsDriftWhenSignalIsStrong()
    {
        // Linear drift in the apparent offset: server clock 100 µs/s ahead each step.
        // The filter should converge on drift ≈ 100 µs/s with σ_drift << 100,
        // yielding z >> 2 and IsDriftReliable = true.
        for (int i = 0; i < 50; i++)
        {
            long t1 = i * 1_000_000L;
            long offsetMicros = 5000 + 100L * i; // +100 µs per second
            _sync.ProcessMeasurement(t1, t1 + offsetMicros, t1 + offsetMicros + 100, t1 + 2000);
        }

        var status = _sync.GetStatus();

        // Sanity: drift estimate has converged near 100
        Assert.InRange(status.DriftMicrosecondsPerSecond, 80, 120);

        var z = Math.Abs(status.DriftMicrosecondsPerSecond) /
                Math.Max(1e-9, status.DriftUncertaintyMicrosecondsPerSecond);

        Assert.True(z >= 2.0,
            $"Expected SNR >= 2 with strong drift signal, got z = {z:F2}");
        Assert.True(status.IsDriftReliable,
            $"Drift {status.DriftMicrosecondsPerSecond:F2} µs/s ± {status.DriftUncertaintyMicrosecondsPerSecond:F1} (z={z:F2}) " +
            "should be 'reliable'.");
    }

    [Fact]
    public void IsDriftReliable_FalseAfterReset()
    {
        // Drive to a state where the old absolute-threshold gate would say "reliable",
        // then reset, confirm gate returns false.
        for (int i = 0; i < 50; i++)
        {
            long t1 = i * 1_000_000L;
            long offsetMicros = 5000 + 100L * i;
            _sync.ProcessMeasurement(t1, t1 + offsetMicros, t1 + offsetMicros + 100, t1 + 2000);
        }

        _sync.Reset();

        Assert.False(_sync.IsDriftReliable);
        Assert.False(_sync.GetStatus().IsDriftReliable);
    }

    // =========================================================================
    // Measurement noise + adaptive forgetting + localhost — see upstream PR #6
    // https://github.com/Sendspin/time-filter/pull/6
    //
    // - Measurement variance: (max_error × maxErrorScale)² (no fixed floor)
    // - Adaptive forgetting threshold: adaptiveCutoff × max_error (RTT-based)
    // - max_error floored to 1µs to prevent zero-variance NaN on localhost
    // =========================================================================

    [Fact]
    public void Localhost_ZeroRtt_DoesNotProduceNaNOrInfinity()
    {
        // Loopback / localhost can produce identical T1=T2=T3=T4 timestamps,
        // making max_error = 0. The filter must remain numerically stable AND produce
        // correct values (offset = 0, no drift) for this idealized input.
        for (int i = 0; i < 10; i++)
        {
            long t = i * 1_000_000L;
            _sync.ProcessMeasurement(t, t, t, t);
        }

        var status = _sync.GetStatus();

        Assert.False(double.IsNaN(status.OffsetMicroseconds), "OffsetMicroseconds is NaN");
        Assert.False(double.IsInfinity(status.OffsetMicroseconds), "OffsetMicroseconds is infinite");
        Assert.False(double.IsNaN(status.OffsetUncertaintyMicroseconds), "OffsetUncertaintyMicroseconds is NaN");
        Assert.False(double.IsNaN(status.DriftMicrosecondsPerSecond), "DriftMicrosecondsPerSecond is NaN");
        Assert.False(double.IsNaN(status.DriftUncertaintyMicrosecondsPerSecond), "DriftUncertaintyMicrosecondsPerSecond is NaN");

        // Correctness: with T1=T2=T3=T4, the NTP offset is 0; drift should remain ~0.
        Assert.Equal(0, status.OffsetMicroseconds, precision: 0);
        Assert.True(Math.Abs(status.DriftMicrosecondsPerSecond) < 1.0,
            $"Drift should remain near zero on noise-free localhost input; got {status.DriftMicrosecondsPerSecond:F2} µs/s");
    }

    [Fact]
    public void AdaptiveForgetting_FiresOnLargeShockWithDefaultForgetFactor()
    {
        // Validates the A1 fix: the *default* forgetFactor (now 2.0) must produce
        // a forget_variance_factor > 1.0 so the adaptive forgetting gate is live.
        // minSamplesForForgetting is overridden purely to keep the test fast — the
        // 100-sample default would dominate runtime without affecting the assertion.
        var sync = new KalmanClockSynchronizer(minSamplesForForgetting: 5);

        // Phase 1: 20 stable measurements to build up filter state (~5ms offset, ~2ms RTT)
        for (int i = 0; i < 20; i++)
        {
            long t1 = i * 1_000_000L;
            sync.ProcessMeasurement(t1, t1 + 5000, t1 + 5100, t1 + 2000);
        }

        var triggersBefore = sync.GetStatus().AdaptiveForgettingTriggerCount;

        // Phase 2: introduce a 100ms clock step (way beyond 3 × max_error ≈ 6ms)
        long shockBase = 25_000_000L;
        const long shockOffset = 105_000; // 105ms apparent offset (was 5ms)
        sync.ProcessMeasurement(shockBase, shockBase + shockOffset, shockBase + shockOffset + 100, shockBase + 2000);

        var triggersAfter = sync.GetStatus().AdaptiveForgettingTriggerCount;
        Assert.True(triggersAfter > triggersBefore,
            $"Adaptive forgetting should fire on a 100ms shock (residual >> 3 × max_error). " +
            $"Before: {triggersBefore}, after: {triggersAfter}.");
    }

    [Fact]
    public void Convergence_IsTighterThanOldNoiseFloor()
    {
        // With a very-low-RTT path (e.g., loopback, USB, embedded interconnect),
        // the upstream measurement variance (max_error × 0.5)² is tiny, so the filter
        // can trust measurements heavily. The old code's hard 10000 µs² noise floor
        // would cap uncertainty around 30+ µs even on a perfect path.
        // 50 µs RTT → max_error = 25 µs → upstream R = 156 µs² → expected σ ≈ 12 µs
        // Old (with floor): R ≥ 10000 µs² → σ floor around 32 µs.
        for (int i = 0; i < 100; i++)
        {
            long t1 = i * 1_000_000L;
            _sync.ProcessMeasurement(t1, t1 + 5000, t1 + 5025, t1 + 50);
        }

        var status = _sync.GetStatus();

        // Threshold sits between the expected post-fix value (around 12) and the
        // pre-fix floor-bound value (around 32). Failure indicates the old noise
        // floor has crept back in.
        Assert.True(status.OffsetUncertaintyMicroseconds < 25.0,
            $"Expected uncertainty < 25µs on low-RTT path after 100 measurements; got {status.OffsetUncertaintyMicroseconds:F1}µs. " +
            "If this regressed, check the measurement-variance formula (upstream PR #6).");
    }

    // =========================================================================
    // Two-stage initialization — see upstream sendspin_time_filter.cpp:53-75
    //
    // First measurement seeds offset and offset_variance from the measurement.
    // Second measurement bootstraps drift via finite differences and propagates
    // measurement uncertainties into drift_variance.
    // =========================================================================

    [Fact]
    public void Init_AfterFirstMeasurement_OffsetUncertaintyEqualsMeasurementStdDev()
    {
        // Upstream initializes offset_variance to (max_error × maxErrorScale)² on the
        // first measurement. Old C# code left it at the Reset default (~1e12), so a
        // single measurement claimed ≈1 second of uncertainty regardless of RTT.
        // RTT = 1900 µs → max_error = 950 → uncertainty (default scale 0.5) = 475.
        _sync.ProcessMeasurement(0, 5000, 5100, 2000);

        var status = _sync.GetStatus();

        Assert.Equal(1, status.MeasurementCount);
        Assert.Equal(475.0, status.OffsetUncertaintyMicroseconds, precision: 0);
    }

    [Fact]
    public void DriftBootstrap_AfterTwoMeasurements_EstimatesDriftFromFiniteDifference()
    {
        // Two measurements 1 second apart with apparent offset shifting by 1000 µs.
        // Upstream bootstrap: drift = (z1 - z0) / dt = 1000 µs/s.
        // Old C# code (no bootstrap) ran the standard Kalman update with a very
        // high drift prior, producing drift ≈ 0 after these inputs.
        _sync.ProcessMeasurement(0, 5000, 5100, 2000);                          // z0 = 4050
        _sync.ProcessMeasurement(1_000_000, 1_005_000 + 1000, 1_005_100 + 1000, 1_002_000); // z1 = 5050

        var status = _sync.GetStatus();

        Assert.Equal(2, status.MeasurementCount);
        Assert.Equal(1000.0, status.DriftMicrosecondsPerSecond, precision: 0);
    }

    [Fact]
    public void DriftBootstrap_NoDriftSignal_EstimatesNearZero()
    {
        // Two identical measurements 1 second apart → drift ≈ 0.
        _sync.ProcessMeasurement(0, 5000, 5100, 2000);
        _sync.ProcessMeasurement(1_000_000, 1_005_000, 1_005_100, 1_002_000);

        var status = _sync.GetStatus();
        Assert.Equal(0.0, status.DriftMicrosecondsPerSecond, precision: 0);
    }

    [Fact]
    public void DriftSignificanceThreshold_LowerThresholdAcceptsWeakerSignals()
    {
        // Two filters fed identical measurements; only the SNR threshold differs.
        // A permissive threshold (k=0.5) should accept signals that a strict threshold (k=2.0) rejects.
        var permissive = new KalmanClockSynchronizer(driftSignificanceThreshold: 0.5);
        var strict = new KalmanClockSynchronizer(); // default 2.0

        // Modest drift signal (~5 µs/s) — designed to land in the ambiguous zone
        // between the two thresholds.
        for (int i = 0; i < 20; i++)
        {
            long t1 = i * 1_000_000L;
            long offsetMicros = 5000 + (5L * i);
            permissive.ProcessMeasurement(t1, t1 + offsetMicros, t1 + offsetMicros + 100, t1 + 2000);
            strict.ProcessMeasurement(t1, t1 + offsetMicros, t1 + offsetMicros + 100, t1 + 2000);
        }

        var permissiveStatus = permissive.GetStatus();
        var strictStatus = strict.GetStatus();

        // Both filters reach the same numerical state since only the gate threshold differs
        Assert.Equal(permissiveStatus.DriftMicrosecondsPerSecond, strictStatus.DriftMicrosecondsPerSecond, precision: 1);

        var z = Math.Abs(permissiveStatus.DriftMicrosecondsPerSecond) /
                Math.Max(1e-9, permissiveStatus.DriftUncertaintyMicrosecondsPerSecond);

        // The SNR check should agree with manual computation for both thresholds.
        Assert.Equal(z >= 0.5, permissiveStatus.IsDriftReliable);
        Assert.Equal(z >= 2.0, strictStatus.IsDriftReliable);
    }
}
