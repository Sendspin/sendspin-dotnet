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

    // Per Sendspin protocol spec and all reference implementations
    // (sendspin-cpp, SendspinKit, sendspin-js, aiosendspin), positive
    // static_delay_ms is SUBTRACTED from server timestamps to compensate
    // for hardware delay beyond the audio port. The audio is scheduled
    // earlier from the digital pipeline so it emerges from external
    // hardware (speakers, amplifiers) on time relative to peers.
    //
    // The previous C# behavior (positive = play later, ADD to client time)
    // was opposite to spec; see .localNotes/static-delay-research/FINDINGS.md.

    [Fact]
    public void OutputDelay_PositiveValue_AdvancesPlaybackEarlier()
    {
        _sync.ProcessMeasurement(0, 5000, 5100, 10_000);
        _sync.ProcessMeasurement(100_000, 105_000, 105_100, 110_000);

        long serverTime = 200_000L;
        long withoutDelay = _sync.ServerToClientTime(serverTime);

        _sync.OutputDelayMs = 10.0; // 10 ms = 10000 µs of hardware compensation
        long withDelay = _sync.ServerToClientTime(serverTime);

        // Positive static_delay subtracts from the converted client time,
        // so the scheduled play time is 10 ms earlier.
        Assert.Equal(-10_000, withDelay - withoutDelay);
    }

    [Fact]
    public void OutputDelay_NegativeValue_DelaysPlaybackLater()
    {
        _sync.ProcessMeasurement(0, 5000, 5100, 10_000);
        _sync.ProcessMeasurement(100_000, 105_000, 105_100, 110_000);

        long serverTime = 200_000L;
        long withoutDelay = _sync.ServerToClientTime(serverTime);

        _sync.OutputDelayMs = -5.0; // negative compensation → schedule later
        long withDelay = _sync.ServerToClientTime(serverTime);

        Assert.Equal(5_000, withDelay - withoutDelay);
    }

    [Fact]
    public void OutputDelay_ZeroValue_NoOp()
    {
        _sync.ProcessMeasurement(0, 5000, 5100, 10_000);
        _sync.ProcessMeasurement(100_000, 105_000, 105_100, 110_000);

        long serverTime = 200_000L;
        long withoutDelay = _sync.ServerToClientTime(serverTime);

        _sync.OutputDelayMs = 0.0;
        long withDelay = _sync.ServerToClientTime(serverTime);

        Assert.Equal(0, withDelay - withoutDelay);
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
    public void ZeroRtt_IsRejected_AndLeavesTheFilterStable()
    {
        // T1=T2=T3=T4 makes the round trip exactly zero, which is not a fast exchange but an
        // impossible one — max_error would be zero, an infinitely confident measurement. The
        // reference drops non-positive max_error rather than flooring it (#224), so nothing
        // enters the filter and it stays at its initial, numerically sound state.
        for (int i = 0; i < 10; i++)
        {
            long t = i * 1_000_000L;
            _sync.ProcessMeasurement(t, t, t, t);
        }

        var status = _sync.GetStatus();

        Assert.Equal(0, status.MeasurementCount);
        Assert.False(double.IsNaN(status.OffsetMicroseconds), "OffsetMicroseconds is NaN");
        Assert.False(double.IsInfinity(status.OffsetMicroseconds), "OffsetMicroseconds is infinite");
        Assert.False(double.IsNaN(status.OffsetUncertaintyMicroseconds), "OffsetUncertaintyMicroseconds is NaN");
        Assert.False(double.IsNaN(status.DriftMicrosecondsPerSecond), "DriftMicrosecondsPerSecond is NaN");
        Assert.False(double.IsNaN(status.DriftUncertaintyMicrosecondsPerSecond), "DriftUncertaintyMicrosecondsPerSecond is NaN");
        Assert.Equal(0, status.OffsetMicroseconds, precision: 0);
        Assert.Equal(0, status.DriftMicrosecondsPerSecond, precision: 0);
    }

    [Fact]
    public void NegativeRtt_IsRejected_LeavingEarlierStateIntact()
    {
        // A server clock stepping between T2 and T3 (or two server stamps from different
        // sources) makes (T4−T1) − (T3−T2) negative. Because burst-best selection prefers the
        // lowest round trip, such a sample reaches the filter preferentially, and with a
        // near-zero variance it would snap offset and drift onto the corrupt value.
        _sync.ProcessMeasurement(0, 5000, 5100, 2000);
        _sync.ProcessMeasurement(1_000_000, 1_005_000, 1_005_100, 1_002_000);
        var before = _sync.GetStatus();

        // T3−T2 = 8000 µs against 2000 µs of elapsed client time: rtt = −6000 µs.
        _sync.ProcessMeasurement(2_000_000, 2_400_000, 2_408_000, 2_002_000);

        var after = _sync.GetStatus();
        Assert.Equal(before.MeasurementCount, after.MeasurementCount);
        Assert.Equal(before.OffsetMicroseconds, after.OffsetMicroseconds);
        Assert.Equal(before.DriftMicrosecondsPerSecond, after.DriftMicrosecondsPerSecond);
        Assert.Equal(before.OffsetUncertaintyMicroseconds, after.OffsetUncertaintyMicroseconds);
    }

    [Fact]
    public void SubTwoMicrosecondRtt_IsAccepted_WithTheMaxErrorFloorAsItsVarianceGuard()
    {
        // The 1 µs floor survives the non-positive drop, and only for what it was for: a
        // genuine but tiny round trip (loopback, embedded interconnect) whose half-delay would
        // otherwise be a sub-microsecond — and therefore near-zero-variance — measurement.
        // rtt = 2 µs → max_error = 1 µs → σ = max_error × 0.5.
        _sync.ProcessMeasurement(0, 5000, 5000, 2);

        var status = _sync.GetStatus();
        Assert.Equal(1, status.MeasurementCount);
        Assert.Equal(0.5, status.OffsetUncertaintyMicroseconds, precision: 3);
    }

    // =========================================================================
    // Process noise (#222) — sendspin-cpp time_filter.h:48-52.
    //
    // process_std_dev = 0 and drift_process_std_dev = 1e-11 per √µs, which under this
    // class's units (dt in seconds, drift in µs/s) are 0.0 µs²/s and 1e-4 (µs/s)²/s. See
    // the derivation at the constants. The pre-fix defaults were 100.0 and 1.0.
    // =========================================================================

    [Fact]
    public void Defaults_AreTheReferenceProcessNoise()
    {
        var reference = new KalmanClockSynchronizer(processNoiseOffset: 0.0, processNoiseDrift: 1e-4);
        var previous = new KalmanClockSynchronizer(processNoiseOffset: 100.0, processNoiseDrift: 1.0);

        for (int i = 0; i < 30; i++)
        {
            long t1 = i * 10_000_000L; // 10 s apart, the reference's steady-state cadence
            long offsetMicros = 5000 + (25L * (t1 / 1_000_000L)); // 25 µs/s of real drift
            long t2 = t1 + offsetMicros;
            _sync.ProcessMeasurement(t1, t2, t2 + 100, t1 + 2000);
            reference.ProcessMeasurement(t1, t2, t2 + 100, t1 + 2000);
            previous.ProcessMeasurement(t1, t2, t2 + 100, t1 + 2000);
        }

        // Bit-for-bit with the reference constants...
        Assert.Equal(reference.Offset, _sync.Offset);
        Assert.Equal(reference.Drift, _sync.Drift);
        Assert.Equal(reference.OffsetUncertainty, _sync.OffsetUncertainty);

        // ...and materially unlike the constants they replaced, so the assertion above has
        // something to fail against.
        Assert.NotEqual(previous.OffsetUncertainty, _sync.OffsetUncertainty, precision: 3);
    }

    [Fact]
    public void ReferenceProcessNoise_LetsTheDriftEstimateSettle()
    {
        // The concrete cost of the old constants: 1.0 (µs/s)² of drift variance added per
        // second means 10 (µs/s)² per 10 s interval, so the drift estimate forgot its history
        // as fast as it accumulated it and never settled. With the reference's 1e-4 the same
        // measurements pin drift to a fraction of a µs/s, which is what a reference client
        // fed the same data reports.
        var previous = new KalmanClockSynchronizer(processNoiseOffset: 100.0, processNoiseDrift: 1.0);

        for (int i = 0; i < 40; i++)
        {
            long t1 = i * 10_000_000L;
            long offsetMicros = 5000 + (25L * (t1 / 1_000_000L));
            long t2 = t1 + offsetMicros;
            _sync.ProcessMeasurement(t1, t2, t2 + 100, t1 + 2000);
            previous.ProcessMeasurement(t1, t2, t2 + 100, t1 + 2000);
        }

        var settled = _sync.GetStatus();
        var unsettled = previous.GetStatus();

        Assert.InRange(settled.DriftMicrosecondsPerSecond, 24.0, 26.0);
        Assert.True(settled.DriftUncertaintyMicrosecondsPerSecond < 1.0,
            $"Expected sub-µs/s drift uncertainty with the reference process noise; got " +
            $"{settled.DriftUncertaintyMicrosecondsPerSecond:F2} µs/s.");
        Assert.True(
            unsettled.DriftUncertaintyMicrosecondsPerSecond > settled.DriftUncertaintyMicrosecondsPerSecond * 2,
            $"The old defaults should be visibly worse here; got {unsettled.DriftUncertaintyMicrosecondsPerSecond:F2} " +
            $"vs {settled.DriftUncertaintyMicrosecondsPerSecond:F2} µs/s.");
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

    // =========================================================================
    // Drift gating in the conversions (#223) — sendspin-cpp time_filter.cpp:141-170.
    //
    // Both compute_server_time and compute_client_time extrapolate with
    // `effective_drift = use_drift_ ? drift_ : 0.0`, so a drift estimate that has not
    // cleared the 2σ SNR test contributes nothing. The mapping stays linear in offset
    // and drift either way (roles/source/v1.md:8) — the gate only decides which drift
    // the linear mapping is built from, and the inverse tracks the same choice.
    // =========================================================================

    [Fact]
    public void ClientToServerTime_ExtrapolatesFlat_WhileDriftIsInsignificant()
    {
        // Two measurements bootstrap an exact drift of 1000 µs/s (see
        // DriftBootstrap_AfterTwoMeasurements): offset = 5050 µs at
        // lastUpdate = 1_002_000, drift = 1000 µs/s — but that number is a finite
        // difference of two noisy offsets, not yet a measured rate, and it fails the
        // significance test. The reference zeroes the drift term in that regime; applying
        // it instead put ~1 ms of pure noise into a chunk timestamp extrapolated one second
        // out, at the spec's whole accuracy budget, while a C++ client in the same group
        // scheduled the same chunk flat.
        _sync.ProcessMeasurement(0, 5000, 5100, 2000);
        _sync.ProcessMeasurement(1_000_000, 1_006_000, 1_006_100, 1_002_000);
        Assert.False(_sync.IsDriftReliable);
        Assert.Equal(1000.0, _sync.Drift, precision: 0);

        // 10 s past the last update: offset alone, not offset + 1000·10.
        long clientTime = 1_002_000 + 10_000_000;
        long serverTime = _sync.ClientToServerTime(clientTime);

        long expected = clientTime + 5050;
        Assert.InRange(serverTime, expected - 2, expected + 2);
    }

    [Fact]
    public void ServerToClientTime_ExtrapolatesFlat_WhileDriftIsInsignificant()
    {
        // The same gate on the inverse direction: time_filter.cpp evaluates
        // effective_drift in compute_client_time too, so a filter carrying an
        // insignificant drift divides by (1 + 0), not by (1 + drift/1e6).
        _sync.ProcessMeasurement(0, 5000, 5100, 2000);
        _sync.ProcessMeasurement(1_000_000, 1_006_000, 1_006_100, 1_002_000);
        Assert.False(_sync.IsDriftReliable);

        const long lastUpdate = 1_002_000;
        long serverTime = lastUpdate + 5050 + 10_000_000;
        long clientTime = _sync.ServerToClientTime(serverTime);

        // Ungated, the divide by (1 + 1000/1e6) would pull this ~10 ms earlier.
        Assert.InRange(clientTime, lastUpdate + 10_000_000 - 2, lastUpdate + 10_000_000 + 2);
    }

    [Fact]
    public void Conversions_ApplyDrift_OnceItIsSignificant()
    {
        // Positive control: with a real 1000 µs/s rate measured over 50 samples the gate
        // opens and both directions carry the drift term again.
        for (int i = 0; i < 50; i++)
        {
            long t1 = i * 1_000_000L;
            long offsetMicros = 5000 + (1000L * i);
            _sync.ProcessMeasurement(t1, t1 + offsetMicros, t1 + offsetMicros + 100, t1 + 2000);
        }

        Assert.True(_sync.IsDriftReliable);
        Assert.InRange(_sync.Drift, 900, 1100);

        long lastUpdate = (49 * 1_000_000L) + 2000;
        long clientTime = lastUpdate + 10_000_000; // 10 s out
        long flat = clientTime + (long)Math.Round(_sync.Offset);
        long serverTime = _sync.ClientToServerTime(clientTime);

        // 10 s × ~1000 µs/s ≈ 10 ms of drift on top of the offset.
        Assert.InRange(serverTime - flat, 9_000, 11_000);
    }

    [Fact]
    public void TimeConversions_AreExactInverses_UnderSignificantDrift()
    {
        // Strong linear drift (~1000 µs/s) over 50 measurements: statistically
        // significant, so even the old gated code applied drift in both directions —
        // but it evaluated the server→client drift term at an approximated client
        // time instead of solving the linear mapping, leaving a residual of
        // drift²·elapsed (≈100 µs at 1000 µs/s over 100 s). The spec says the
        // mapping is linear and its inverse well-defined, so the round trip must
        // come back exact up to integer rounding.
        for (int i = 0; i < 50; i++)
        {
            long t1 = i * 1_000_000L;
            long offsetMicros = 5000 + (1000L * i);
            _sync.ProcessMeasurement(t1, t1 + offsetMicros, t1 + offsetMicros + 100, t1 + 2000);
        }

        Assert.True(_sync.IsDriftReliable);

        long lastUpdate = (49 * 1_000_000L) + 2000;
        long clientTime = lastUpdate + 100_000_000; // 100 s past the last update
        long serverTime = _sync.ClientToServerTime(clientTime);
        long roundTripped = _sync.ServerToClientTime(serverTime);

        Assert.InRange(Math.Abs(roundTripped - clientTime), 0, 2);
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
