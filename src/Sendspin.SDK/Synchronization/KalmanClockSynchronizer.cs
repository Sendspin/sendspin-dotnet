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
    private long _staticDelayMicroseconds;

    private readonly double _forgetVarianceFactor;
    private readonly double _adaptiveCutoff;
    private readonly int _minSamplesForForgetting;
    private int _adaptiveForgettingTriggerCount;

    // Squared so the SNR check avoids sqrt and divide-by-zero. See upstream PR #5.
    private readonly double _driftSignificanceThresholdSquared;

    private const int MinMeasurementsForConvergence = 5;
    private const int MinMeasurementsForPlayback = 2;
    private const double MaxOffsetUncertaintyForConvergence = 1000.0;

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
    /// configured threshold, default 2σ ≈ 95% confidence). When false, time
    /// conversions skip drift compensation. See upstream time-filter PR #5.
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
    private bool IsDriftStatisticallySignificantUnsafe()
    {
        return _measurementCount >= MinMeasurementsForConvergence
               && _drift * _drift > _driftSignificanceThresholdSquared * _driftVariance;
    }

    /// <summary>
    /// Creates a new Kalman clock synchronizer.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <param name="processNoiseOffset">Process noise variance for offset (μs²/s).</param>
    /// <param name="processNoiseDrift">Process noise variance for drift (μs²/s²).</param>
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
    public KalmanClockSynchronizer(
        ILogger<KalmanClockSynchronizer>? logger = null,
        double processNoiseOffset = 100.0,
        double processNoiseDrift = 1.0,
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

        // max_error is half the network round-trip delay; floored to 1µs so
        // localhost (RTT=0) and pathological clock-skew (RTT<0) don't yield
        // zero or negative measurement variance / forgetting thresholds.
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
                    "[ClockSync] Drift reliable: drift={Drift:F2}μs/s (±{Uncertainty:F1}μs/s), " +
                    "offset={Offset:F0}μs, measurements={Count}. " +
                    "Future timestamps will include drift compensation.",
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
    public long ClientToServerTime(long clientTime)
    {
        lock (_lock)
        {
            // Account for drift since last update, but only if drift estimate is reliable
            // Early drift estimates are essentially noise and can make timing worse
            if (_lastUpdateTime > 0)
            {
                double elapsedSeconds = (clientTime - _lastUpdateTime) / 1_000_000.0;

                double currentOffset = IsDriftStatisticallySignificantUnsafe()
                    ? _offset + _drift * elapsedSeconds
                    : _offset;

                return clientTime + (long)currentOffset;
            }
            return clientTime + (long)_offset;
        }
    }

    /// <summary>
    /// Converts a server timestamp to client time.
    /// </summary>
    /// <param name="serverTime">Server time in microseconds.</param>
    /// <returns>Estimated client time in microseconds, with <see cref="StaticDelayMs"/> applied.</returns>
    /// <remarks>
    /// Subtracts <see cref="StaticDelayMs"/> from the converted client time per the Sendspin
    /// protocol spec (positive value compensates for hardware delay; audio is scheduled earlier
    /// from the digital pipeline so it emerges from external speakers/amplifiers on time).
    /// </remarks>
    public long ServerToClientTime(long serverTime)
    {
        lock (_lock)
        {
            if (_lastUpdateTime > 0)
            {
                // Approximate client time used only to compute elapsed seconds for drift extrapolation.
                long approxClientTime = serverTime - (long)_offset;
                double elapsedSeconds = (approxClientTime - _lastUpdateTime) / 1_000_000.0;

                double currentOffset = IsDriftStatisticallySignificantUnsafe()
                    ? _offset + _drift * elapsedSeconds
                    : _offset;

                return serverTime - (long)currentOffset - _staticDelayMicroseconds;
            }

            return serverTime - (long)_offset - _staticDelayMicroseconds;
        }
    }

    /// <summary>
    /// Gets or sets the static delay in milliseconds. Compensates for hardware delay beyond
    /// the device's audio port (external speakers, amplifiers). Per the Sendspin protocol spec,
    /// this value is subtracted from server timestamps when scheduling playback: positive values
    /// schedule audio earlier from the digital pipeline; negative values schedule it later.
    /// </summary>
    public double StaticDelayMs
    {
        get { lock (_lock) return _staticDelayMicroseconds / 1000.0; }
        set { lock (_lock) _staticDelayMicroseconds = (long)(value * 1000); }
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
    /// Converts server time to client time.
    /// </summary>
    long ServerToClientTime(long serverTime);

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
    /// Gets or sets the static delay in milliseconds. Compensates for hardware delay beyond
    /// the audio port (external speakers, amplifiers). Per the Sendspin protocol spec, the
    /// value is subtracted from server timestamps when scheduling playback: positive values
    /// schedule audio earlier; negative values schedule it later.
    /// </summary>
    double StaticDelayMs { get; set; }
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
