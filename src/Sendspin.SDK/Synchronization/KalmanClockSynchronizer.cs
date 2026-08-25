using Microsoft.Extensions.Logging;

namespace Sendspin.SDK.Synchronization;

/// <summary>
/// High-precision clock synchronizer that tracks server-client offset and drift
/// to convert between time domains for synchronized audio playback.
/// </summary>
public sealed class KalmanClockSynchronizer : IClockSynchronizer
{
    private readonly ILogger<KalmanClockSynchronizer>? _logger;
    private readonly object _lock = new();

    private double _offset;
    private double _drift;
    private double _offsetVariance;
    private double _driftVariance;
    private double _covariance;

    private long _lastUpdateTime;
    private int _measurementCount;

    private readonly double _processNoiseOffset;
    private readonly double _processNoiseDrift;
    private readonly double _measurementNoiseFloor;
    private readonly double _maxErrorScale;
    private long _outputDelayMicroseconds;

    private readonly double _forgetVarianceFactor;
    private readonly double _adaptiveCutoff;
    private readonly int _minSamplesForForgetting;
    private int _adaptiveForgettingTriggerCount;

    // Squared so the SNR check avoids sqrt and divide-by-zero. See upstream PR #5.
    private readonly double _driftSignificanceThresholdSquared;

    private const int MinMeasurementsForConvergence = 5;
    private const int MinMeasurementsForPlayback = 2;
    private const double MaxOffsetUncertaintyForConvergence = 1000.0;

    // Process noise, carried over from the reference filter's Config defaults
    // (sendspin-cpp time_filter.h: process_std_dev = 0.0, drift_process_std_dev = 1e-11)
    // and converted into this class's units. Both codebases run the same predict step; only
    // the units of dt and of drift differ, so the conversion is a pure change of variables.
    //
    //   reference        dt in µs; drift dimensionless (µs of offset per µs of elapsed time)
    //                    offset variance += process_std_dev²       · dt_µs
    //                    drift  variance += drift_process_std_dev² · dt_µs
    //   this class       dt in seconds; drift in µs/s
    //                    offset variance += _processNoiseOffset · dt_s
    //                    drift  variance += _processNoiseDrift  · dt_s
    //
    // dt_µs = 1e6 · dt_s, and drift_here = 1e6 · drift_reference, so a drift variance here is
    // 1e12 × the reference's. Matching the two growth rates term by term:
    //
    //   _processNoiseOffset = 1e6  · process_std_dev²       = 1e6  · 0     = 0.0    µs²/s
    //   _processNoiseDrift  = 1e18 · drift_process_std_dev² = 1e18 · 1e-22 = 1e-4   (µs/s)²/s
    //                         (1e12 for the drift unit change × 1e6 for dt)
    //
    // The prior defaults (100.0 and 1.0) inflated both by orders of magnitude, so an SDK
    // client's filter tracked per-burst measurement noise where a reference client smoothed
    // it, and its drift estimate forgot history fast enough never to settle (#222).
    private const double DefaultProcessNoiseOffset = 0.0;
    private const double DefaultProcessNoiseDrift = 1e-4;

    private bool _driftReliableLogged;

    // RTT tracking for clock-sync diagnostics
    private double _lastRttMicroseconds;
    private double _avgRttMicroseconds;
    private double _rttJitterMicroseconds;
    private const double RttEwmaAlpha = 0.2;

    /// <summary>
    /// Current estimated clock offset in microseconds.
    /// server_time = client_time + Offset
    /// </summary>
    public double Offset
    {
        get { lock (_lock) return _offset; }
    }

    /// <summary>
    /// Current estimated clock drift in microseconds per second.
    /// Positive means server clock is running faster than client.
    /// </summary>
    public double Drift
    {
        get { lock (_lock) return _drift; }
    }

    /// <summary>
    /// Uncertainty (standard deviation) of the offset estimate in microseconds.
    /// </summary>
    public double OffsetUncertainty
    {
        get { lock (_lock) return Math.Sqrt(_offsetVariance); }
    }

    /// <summary>
    /// Number of measurements processed.
    /// </summary>
    public int MeasurementCount
    {
        get { lock (_lock) return _measurementCount; }
    }

    /// <summary>
    /// Whether the synchronizer has converged to a stable estimate.
    /// Requires 5+ measurements and low offset uncertainty.
    /// </summary>
    public bool IsConverged
    {
        get
        {
            lock (_lock)
            {
                return _measurementCount >= MinMeasurementsForConvergence
                       && Math.Sqrt(_offsetVariance) < MaxOffsetUncertaintyForConvergence;
            }
        }
    }

    /// <summary>
    /// True after at least two measurements. Allows playback to start before full
    /// statistical convergence (matches the JS/CLI player). Compare with
    /// <see cref="IsConverged"/> which also requires low offset uncertainty.
    /// </summary>
    public bool HasMinimalSync
    {
        get
        {
            lock (_lock)
            {
                return _measurementCount >= MinMeasurementsForPlayback;
            }
        }
    }

    /// <summary>
    /// True when the drift estimate is statistically significant (z-score above the
    /// configured threshold, default 2σ ≈ 95% confidence). This is the gate the time
    /// conversions apply drift through: below it they extrapolate on offset alone, exactly
    /// as the reference filter's <c>use_drift_</c> flag does. See upstream time-filter PR #5.
    /// </summary>
    public bool IsDriftReliable
    {
        get
        {
            lock (_lock)
            {
                return IsDriftStatisticallySignificantUnsafe();
            }
        }
    }

    // Caller must hold _lock. Squared form avoids sqrt and divide-by-zero on
    // the equivalent |drift|/σ_drift > k test.
    //
    // The reference filter (time_filter.cpp) assigns use_drift_ only at the end of its full
    // update step, so its own floor is three measurements — the two initialization branches
    // leave the flag false. This class keeps the stricter five-measurement floor it already
    // had: the extra window is the one where the bootstrap drift (z1−z0)/dt is pure
    // measurement noise, and a false positive there is the very error the gate exists to
    // prevent. The two agree from the fifth measurement on.
    private bool IsDriftStatisticallySignificantUnsafe()
    {
        return _measurementCount >= MinMeasurementsForConvergence
               && _drift * _drift > _driftSignificanceThresholdSquared * _driftVariance;
    }

    // Caller must hold _lock. The drift the conversions actually apply: the estimate once it
    // clears the significance gate, zero before that. Mirrors time_filter.cpp's
    // `const double effective_drift = this->use_drift_ ? this->drift_ : 0.0;`, which both
    // compute_server_time and compute_client_time evaluate.
    private double EffectiveDriftUnsafe()
        => IsDriftStatisticallySignificantUnsafe() ? _drift : 0.0;

    /// <summary>
    /// Creates a new Kalman clock synchronizer.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <param name="processNoiseOffset">Rate at which offset variance grows between updates
    /// (μs² per second). Default 0.0 is the reference filter's <c>process_std_dev = 0</c>: the
    /// model carries no offset random walk. See the derivation at
    /// <c>DefaultProcessNoiseOffset</c>.</param>
    /// <param name="processNoiseDrift">Rate at which drift variance grows between updates
    /// ((μs/s)² per second). Default 1e-4 is the reference filter's
    /// <c>drift_process_std_dev = 1e-11</c> per √μs expressed in these units. See the
    /// derivation at <c>DefaultProcessNoiseDrift</c>.</param>
    /// <param name="measurementNoiseFloor">Optional additive floor on measurement variance (μs²).
    /// Default 0 matches the upstream time-filter reference; set above 0 to add a fixed
    /// noise floor on top of the RTT-derived variance.</param>
    /// <param name="forgetFactor">Adaptive-forgetting covariance inflation factor (must be &gt; 1 to enable).
    /// Default 2.0 matches upstream; the prior 1.0 silently disabled adaptive forgetting.</param>
    /// <param name="adaptiveCutoff">Multiple of <c>max_error</c> at which a residual triggers adaptive forgetting.
    /// Default 3.0 matches upstream (RTT-aware threshold).</param>
    /// <param name="minSamplesForForgetting">Minimum measurements before adaptive forgetting may fire.</param>
    /// <param name="driftSignificanceThreshold">SNR threshold (in σ) for applying drift compensation
    /// (default 2.0, ≈95% confidence). Mirrors <c>drift_significance_threshold</c> upstream.</param>
    /// <param name="maxErrorScale">Scale applied to <c>max_error</c> before it is used as a 1σ
    /// measurement-noise estimate. Default 0.5: <c>max_error</c> is a worst-case bound, not a 1σ value.</param>
    /// <remarks>
    /// Every default here is the reference time filter's, restated in this class's units — the
    /// two implementations run the same predict/update algebra, so identical measurements now
    /// produce near-identical estimates on both. Deviating from a default deviates from the
    /// reference clients this one shares a playback group with.
    /// </remarks>
    public KalmanClockSynchronizer(
        ILogger<KalmanClockSynchronizer>? logger = null,
        double processNoiseOffset = DefaultProcessNoiseOffset,
        double processNoiseDrift = DefaultProcessNoiseDrift,
        double measurementNoiseFloor = 0.0,
        double forgetFactor = 2.0,
        double adaptiveCutoff = 3.0,
        int minSamplesForForgetting = 100,
        double driftSignificanceThreshold = 2.0,
        double maxErrorScale = 0.5)
    {
        _logger = logger;
        _processNoiseOffset = processNoiseOffset;
        _processNoiseDrift = processNoiseDrift;
        _measurementNoiseFloor = measurementNoiseFloor;
        _maxErrorScale = maxErrorScale;
        _forgetVarianceFactor = forgetFactor * forgetFactor;
        _adaptiveCutoff = adaptiveCutoff;
        _minSamplesForForgetting = minSamplesForForgetting;
        _driftSignificanceThresholdSquared = driftSignificanceThreshold * driftSignificanceThreshold;

        Reset();
    }

    /// <summary>
    /// Resets the synchronizer to initial state.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            // Variance placeholders shown to callers via OffsetUncertainty/GetStatus
            // before the first measurement arrives; both init branches in
            // ProcessMeasurement overwrite these once measurements start flowing.
            _offset = 0;
            _drift = 0;
            _offsetVariance = 1e12;
            _driftVariance = 1e6;
            _covariance = 0;
            _lastUpdateTime = 0;
            _measurementCount = 0;
            _driftReliableLogged = false;
            _adaptiveForgettingTriggerCount = 0;
            _lastRttMicroseconds = 0;
            _avgRttMicroseconds = 0;
            _rttJitterMicroseconds = 0;
        }

        _logger?.LogDebug("Clock synchronizer reset");
    }

    /// <summary>
    /// Processes a complete time exchange measurement.
    /// </summary>
    /// <param name="t1">Client transmit time (T1) in microseconds.</param>
    /// <param name="t2">Server receive time (T2) in microseconds.</param>
    /// <param name="t3">Server transmit time (T3) in microseconds.</param>
    /// <param name="t4">Client receive time (T4) in microseconds.</param>
    public void ProcessMeasurement(long t1, long t2, long t3, long t4)
    {
        // NTP four-timestamp formulas
        double measuredOffset = ((t2 - t1) + (t3 - t4)) / 2.0;
        double rtt = (t4 - t1) - (t3 - t2);

        // A non-positive round trip is not a fast exchange, it is a corrupt one: the server
        // clock stepped between T2 and T3, the two server stamps came from different sources,
        // or a VM's counter jumped. Its max_error would be zero or negative, which is an
        // "infinitely confident" measurement that drives the Kalman gain to 1 and snaps the
        // filter onto the corrupt value. The reference discards these before they can be
        // selected as a burst best (time_burst.cpp: "Dropping time response with non-positive
        // max_error"); this is the same rule at the filter's own boundary, so a caller that
        // feeds measurements directly cannot poison the state either (#224).
        if (rtt <= 0)
        {
            _logger?.LogWarning(
                "Dropping time measurement with non-positive round trip: {Rtt}μs", rtt);
            return;
        }

        // max_error is half the network round-trip delay, floored to 1µs so a genuine
        // sub-2µs round trip (loopback, embedded interconnect) still yields a usable
        // measurement variance rather than a near-zero one.
        double maxError = Math.Max(rtt / 2.0, 1.0);

        // Measurement variance derived from max_error (used in init branches and update step).
        double measurementStdDev = maxError * _maxErrorScale;
        double measurementVariance = _measurementNoiseFloor + measurementStdDev * measurementStdDev;

        lock (_lock)
        {
            // Update RTT stats for every measurement regardless of Kalman state
            if (_measurementCount == 0)
            {
                _avgRttMicroseconds = rtt;
                _rttJitterMicroseconds = 0;
            }
            else
            {
                double rttDelta = Math.Abs(rtt - _avgRttMicroseconds);
                _rttJitterMicroseconds = RttEwmaAlpha * rttDelta + (1 - RttEwmaAlpha) * _rttJitterMicroseconds;
                _avgRttMicroseconds = RttEwmaAlpha * rtt + (1 - RttEwmaAlpha) * _avgRttMicroseconds;
            }
            _lastRttMicroseconds = rtt;

            // First measurement: seed offset directly from the measurement; defer drift
            // estimation until the next measurement provides a finite-difference baseline.
            if (_measurementCount == 0)
            {
                _offset = measuredOffset;
                _offsetVariance = measurementVariance;
                _drift = 0;
                _driftVariance = 0;
                _covariance = 0;
                _lastUpdateTime = t4;
                _measurementCount = 1;

                _logger?.LogDebug(
                    "Initial time sync: offset={Offset:F0}μs, RTT={RTT:F0}μs",
                    measuredOffset, rtt);
                return;
            }

            double dt = (t4 - _lastUpdateTime) / 1_000_000.0;
            if (dt <= 0)
            {
                _logger?.LogWarning("Non-positive time delta: {Dt}s, skipping measurement", dt);
                return;
            }

            // Second measurement: bootstrap drift from finite differences.
            // Propagating the two offset variances through (z1-z0)/dt gives
            // drift_variance = (R0 + R1) / dt². Matches upstream cpp:64-75.
            if (_measurementCount == 1)
            {
                _drift = (measuredOffset - _offset) / dt;
                _driftVariance = (_offsetVariance + measurementVariance) / (dt * dt);
                _offset = measuredOffset;
                _offsetVariance = measurementVariance;
                _covariance = 0;
                _lastUpdateTime = t4;
                _measurementCount = 2;
                return;
            }

            // Predict step: P = F·P·F^T + Q with F = [1, dt; 0, 1].
            double predictedOffset = _offset + _drift * dt;
            double predictedDrift = _drift;
            double p00 = _offsetVariance + 2 * _covariance * dt + _driftVariance * dt * dt
                        + _processNoiseOffset * dt;
            double p01 = _covariance + _driftVariance * dt;
            double p11 = _driftVariance + _processNoiseDrift * dt;

            // Adaptive forgetting: a residual exceeding adaptiveCutoff × max_error
            // signals network disruption or a clock step; inflate covariance so the
            // next update weights the measurement more heavily.
            if (_measurementCount >= _minSamplesForForgetting && _forgetVarianceFactor > 1.0)
            {
                double predictionError = Math.Abs(measuredOffset - predictedOffset);
                double threshold = _adaptiveCutoff * maxError;

                if (predictionError > threshold)
                {
                    p00 *= _forgetVarianceFactor;
                    p01 *= _forgetVarianceFactor;
                    p11 *= _forgetVarianceFactor;
                    _adaptiveForgettingTriggerCount++;

                    _logger?.LogWarning(
                        "⚡ Adaptive forgetting triggered (#{Count}): prediction error {Error:F0}μs > " +
                        "threshold {Threshold:F0}μs. Scaling covariance by {Factor:F6} for faster recovery.",
                        _adaptiveForgettingTriggerCount,
                        predictionError,
                        threshold,
                        _forgetVarianceFactor);
                }
            }

            // Update step (H = [1, 0], so we only observe offset directly).
            double innovation = measuredOffset - predictedOffset;
            double innovationVariance = p00 + measurementVariance;
            double k0 = p00 / innovationVariance;
            double k1 = p01 / innovationVariance;

            _offset = predictedOffset + k0 * innovation;
            _drift = predictedDrift + k1 * innovation;

            _offsetVariance = (1 - k0) * p00;
            _covariance = (1 - k0) * p01;
            _driftVariance = p11 - k1 * p01;

            // Floor to a tiny positive value if FP error pushes the simplified covariance
            // update below zero. Invisible against measurement noise; prevents NaN cascades.
            const double VarianceFloor = 1e-6;
            if (_offsetVariance < VarianceFloor) _offsetVariance = VarianceFloor;
            if (_driftVariance < VarianceFloor) _driftVariance = VarianceFloor;

            _lastUpdateTime = t4;
            _measurementCount++;

            if (_measurementCount <= 10 || _measurementCount % 10 == 0)
            {
                _logger?.LogDebug(
                    "Time sync #{Count}: offset={Offset:F0}μs (±{Uncertainty:F0}), " +
                    "drift={Drift:F2}μs/s (±{DriftUncertainty:F1}), RTT={RTT:F0}μs",
                    _measurementCount,
                    _offset,
                    Math.Sqrt(_offsetVariance),
                    _drift,
                    Math.Sqrt(_driftVariance),
                    rtt);
            }

            bool driftNowReliable = IsDriftStatisticallySignificantUnsafe();
            if (driftNowReliable && !_driftReliableLogged)
            {
                _driftReliableLogged = true;
                _logger?.LogInformation(
                    "[ClockSync] Drift estimate now statistically significant: " +
                    "drift={Drift:F2}μs/s (±{Uncertainty:F1}μs/s), " +
                    "offset={Offset:F0}μs, measurements={Count}.",
                    _drift,
                    Math.Sqrt(_driftVariance),
                    _offset,
                    _measurementCount);
            }
        }
    }

    /// <summary>
    /// Converts a client timestamp to server time.
    /// </summary>
    /// <param name="clientTime">Client time in microseconds.</param>
    /// <returns>Estimated server time in microseconds.</returns>
    /// <remarks>
    /// The filter's mapping is <c>t_server = t_client + offset + drift·elapsed</c> with
    /// <c>elapsed</c> measured from the last update in client time. The drift term is applied
    /// through the significance gate (<see cref="IsDriftReliable"/>): an estimate that has not
    /// cleared 2σ is noise, so the extrapolation stays flat until it has, exactly as the
    /// reference filter's <c>effective_drift</c> does. Exact inverse of
    /// <see cref="ServerToClientTime"/> up to integer rounding, ignoring
    /// <see cref="OutputDelayMs"/> which only the server→client direction applies.
    /// </remarks>
    public long ClientToServerTime(long clientTime)
    {
        lock (_lock)
        {
            if (_lastUpdateTime > 0)
            {
                double elapsedSeconds = (clientTime - _lastUpdateTime) / 1_000_000.0;
                return clientTime + (long)Math.Round(_offset + EffectiveDriftUnsafe() * elapsedSeconds);
            }

            return clientTime + (long)_offset;
        }
    }

    /// <summary>
    /// Converts a server timestamp to client time.
    /// </summary>
    /// <param name="serverTime">Server time in microseconds.</param>
    /// <returns>Estimated client time in microseconds, with <see cref="OutputDelayMs"/> applied.</returns>
    /// <remarks>
    /// <para>
    /// Solves the filter's linear mapping <c>t_server = t_client + offset +
    /// drift·(t_client − t_lastUpdate)/1e6</c> for <c>t_client</c> exactly, rather than
    /// evaluating the drift term at an approximated client time: the mapping is linear, so
    /// its inverse is well-defined (spec), and the approximation left a residual of
    /// drift²·elapsed that broke round-tripping with <see cref="ClientToServerTime"/>. The
    /// drift it inverts is the gated one (<see cref="IsDriftReliable"/>), so both directions
    /// stay exact inverses of each other in either regime.
    /// </para>
    /// <para>
    /// Subtracts <see cref="OutputDelayMs"/> from the converted client time per the Sendspin
    /// protocol spec (positive value compensates for hardware delay; audio is scheduled earlier
    /// from the digital pipeline so it emerges from external speakers/amplifiers on time).
    /// Timestamps that schedule something other than sound leaving the speakers want
    /// <see cref="ServerToClientTimeUncompensated"/> instead.
    /// </para>
    /// </remarks>
    public long ServerToClientTime(long serverTime)
    {
        lock (_lock)
        {
            return ConvertToClientTimeUnsafe(serverTime) - _outputDelayMicroseconds;
        }
    }

    /// <summary>
    /// Converts a server timestamp to client time using the synchronized clock alone, without
    /// <see cref="OutputDelayMs"/>.
    /// </summary>
    /// <param name="serverTime">Server time in microseconds.</param>
    /// <returns>Estimated client time in microseconds.</returns>
    /// <remarks>
    /// The conversion the spec asks for wherever a server timestamp schedules something that is
    /// not audio leaving the speakers: the visualizer and artwork roles translate their display
    /// timestamps with "the offset computed from clock synchronization", and only the player
    /// role goes on to subtract <c>static_delay_ms</c>. A visual scheduled by
    /// <see cref="ServerToClientTime"/> would be shown early by exactly the hardware delay the
    /// audio is compensating for, so it would run ahead of the sound it belongs to.
    /// </remarks>
    public long ServerToClientTimeUncompensated(long serverTime)
    {
        lock (_lock)
        {
            return ConvertToClientTimeUnsafe(serverTime);
        }
    }

    /// <summary>
    /// The server→client conversion both public methods share, before any output delay.
    /// Caller must hold <see cref="_lock"/>.
    /// </summary>
    private long ConvertToClientTimeUnsafe(long serverTime)
    {
        if (_lastUpdateTime > 0)
        {
            // t_client = t_last + (t_server − t_last − offset) / (1 + drift/1e6).
            double clientRelative = (serverTime - _lastUpdateTime - _offset)
                                    / (1.0 + EffectiveDriftUnsafe() / 1_000_000.0);
            return _lastUpdateTime + (long)Math.Round(clientRelative);
        }

        return serverTime - (long)_offset;
    }

    /// <summary>
    /// Gets or sets the output delay in milliseconds. Compensates for hardware delay beyond
    /// the device's audio port (external speakers, amplifiers). Per the Sendspin protocol spec,
    /// this value is subtracted from server timestamps when scheduling playback: positive values
    /// schedule audio earlier from the digital pipeline; negative values schedule it later.
    /// </summary>
    public double OutputDelayMs
    {
        get { lock (_lock) return _outputDelayMicroseconds / 1000.0; }
        set { lock (_lock) _outputDelayMicroseconds = (long)(value * 1000); }
    }

    /// <summary>
    /// Gets the current synchronization status for diagnostics.
    /// </summary>
    public ClockSyncStatus GetStatus()
    {
        lock (_lock)
        {
            var offsetUncertainty = Math.Sqrt(_offsetVariance);
            var driftUncertainty = Math.Sqrt(_driftVariance);

            return new ClockSyncStatus
            {
                OffsetMicroseconds = _offset,
                DriftMicrosecondsPerSecond = _drift,
                OffsetUncertaintyMicroseconds = offsetUncertainty,
                DriftUncertaintyMicrosecondsPerSecond = driftUncertainty,
                MeasurementCount = _measurementCount,
                IsConverged = _measurementCount >= MinMeasurementsForConvergence
                              && offsetUncertainty < MaxOffsetUncertaintyForConvergence,
                IsDriftReliable = IsDriftStatisticallySignificantUnsafe(),
                AdaptiveForgettingTriggerCount = _adaptiveForgettingTriggerCount,
                LastRttMicroseconds = _lastRttMicroseconds,
                AvgRttMicroseconds = _avgRttMicroseconds,
                RttJitterMicroseconds = _rttJitterMicroseconds,
            };
        }
    }
}

/// <summary>
/// Interface for clock synchronization implementations.
/// </summary>
public interface IClockSynchronizer
{
    /// <summary>
    /// Processes a time sync measurement using the NTP 4-timestamp method.
    /// </summary>
    void ProcessMeasurement(long t1, long t2, long t3, long t4);

    /// <summary>
    /// Converts client time to server time.
    /// </summary>
    long ClientToServerTime(long clientTime);

    /// <summary>
    /// Converts server time to client time, with <see cref="OutputDelayMs"/> subtracted. Use
    /// this for anything scheduling audio out of the speakers.
    /// </summary>
    long ServerToClientTime(long serverTime);

    /// <summary>
    /// Converts server time to client time using the synchronized clock alone, without
    /// <see cref="OutputDelayMs"/>. Use this for server timestamps the spec translates with the
    /// clock offset only — the visualizer and artwork roles' display times, which must not move
    /// when the user changes a hardware delay that applies to sound.
    /// </summary>
    long ServerToClientTimeUncompensated(long serverTime);

    /// <summary>
    /// Whether the synchronizer has converged to a stable estimate.
    /// Requires 5+ measurements and low offset uncertainty.
    /// </summary>
    bool IsConverged { get; }

    /// <summary>
    /// Whether the synchronizer has enough measurements for playback (at least 2).
    /// Unlike <see cref="IsConverged"/>, this doesn't require statistical convergence.
    /// </summary>
    bool HasMinimalSync { get; }

    /// <summary>
    /// Resets the synchronizer state.
    /// </summary>
    void Reset();

    /// <summary>
    /// Gets the current sync status.
    /// </summary>
    ClockSyncStatus GetStatus();

    /// <summary>
    /// Gets or sets the output delay in milliseconds. Compensates for hardware delay beyond
    /// the audio port (external speakers, amplifiers). Per the Sendspin protocol spec, the
    /// value is subtracted from server timestamps when scheduling playback: positive values
    /// schedule audio earlier; negative values schedule it later.
    /// </summary>
    double OutputDelayMs { get; set; }
}

/// <summary>
/// Status information about clock synchronization.
/// </summary>
public record ClockSyncStatus
{
    /// <summary>
    /// Estimated offset: server_time = client_time + offset.
    /// </summary>
    public double OffsetMicroseconds { get; init; }

    /// <summary>
    /// Estimated drift rate in microseconds per second.
    /// </summary>
    public double DriftMicrosecondsPerSecond { get; init; }

    /// <summary>
    /// Uncertainty (standard deviation) of offset in microseconds.
    /// </summary>
    public double OffsetUncertaintyMicroseconds { get; init; }

    /// <summary>
    /// Uncertainty (standard deviation) of drift in microseconds per second.
    /// </summary>
    public double DriftUncertaintyMicrosecondsPerSecond { get; init; }

    /// <summary>
    /// Number of measurements processed.
    /// </summary>
    public int MeasurementCount { get; init; }

    /// <summary>
    /// Whether synchronization has converged.
    /// </summary>
    public bool IsConverged { get; init; }

    /// <summary>
    /// Whether drift estimate is reliable enough for compensation.
    /// </summary>
    public bool IsDriftReliable { get; init; }

    /// <summary>
    /// Number of times adaptive forgetting was triggered due to large prediction errors.
    /// This indicates recovery from network disruptions or clock adjustments.
    /// </summary>
    public int AdaptiveForgettingTriggerCount { get; init; }

    /// <summary>
    /// Offset in milliseconds for display.
    /// </summary>
    public double OffsetMilliseconds => OffsetMicroseconds / 1000.0;

    // ── RTT diagnostics ─────────────────────────────────────────────────────

    /// <summary>
    /// RTT of the most recent time exchange measurement in microseconds.
    /// </summary>
    public double LastRttMicroseconds { get; init; }

    /// <summary>
    /// EWMA of recent RTT measurements in microseconds.
    /// </summary>
    public double AvgRttMicroseconds { get; init; }

    /// <summary>
    /// EWMA of |ΔRTT| between consecutive measurements in microseconds.
    /// High values indicate WiFi jitter causing unstable clock offset estimation;
    /// pairs with <see cref="AdaptiveForgettingTriggerCount"/> to distinguish
    /// one-shot disruption from sustained instability.
    /// </summary>
    public double RttJitterMicroseconds { get; init; }
}
