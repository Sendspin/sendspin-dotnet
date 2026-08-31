// <copyright file="SyncCorrectionCalculator.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sendspin.SDK.Audio;

/// <summary>
/// Computes the sync correction — a target playback rate — from an observed sync error, and
/// raises <see cref="CorrectionChanged"/> when it changes. Subscribers are responsible for
/// unsubscribing before discarding the reference.
/// </summary>
/// <remarks>
/// The decision itself lives in <see cref="SyncCorrectionPolicy"/>, shared with
/// <see cref="TimedAudioBuffer"/>'s internal corrector so the two cannot diverge.
/// When the policy selects <see cref="SyncCorrectionMode.HardSync"/> this reports
/// a neutral rate: the buffer applies that snap itself on both read paths,
/// and a caller adding its own on top would double-correct.
/// </remarks>
public sealed class SyncCorrectionCalculator : ISyncCorrectionProvider
{
    private readonly SyncCorrectionOptions _options;
    private readonly int _sampleRate;
    private readonly int _channels;

    // The same stand-down rule TimedAudioBuffer runs, on its own copy of the state. It has to be
    // here as well as there: on the ReadRaw path the host drives this object, so a buffer that
    // suppressed its snap alone would leave this one still reporting the hard-sync decision, the
    // host would keep standing down for a snap that is no longer coming, and nothing would
    // correct at all. See HardSyncStallDetector.
    private readonly HardSyncStallDetector _hardSyncStall;

    private readonly object _lock = new();

    private SyncCorrectionMode _currentMode = SyncCorrectionMode.None;
    private double _targetPlaybackRate = 1.0;

    private long _totalSamplesProcessed;
    private bool _inStartupGracePeriod = true;

    // Reconnect stabilization tracking (separate from startup grace to avoid interference)
    private bool _inReconnectStabilization;
    private long _reconnectSamplesProcessed;

    /// <inheritdoc/>
    public SyncCorrectionMode CurrentMode
    {
        get { lock (_lock) return _currentMode; }
    }

    /// <inheritdoc/>
    public double TargetPlaybackRate
    {
        get { lock (_lock) return _targetPlaybackRate; }
    }

    /// <inheritdoc/>
    public event Action<ISyncCorrectionProvider>? CorrectionChanged;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncCorrectionCalculator"/> class.
    /// </summary>
    /// <param name="options">Sync correction options. Uses <see cref="SyncCorrectionOptions.Default"/> if null.</param>
    /// <param name="sampleRate">Audio sample rate in Hz (e.g., 48000). Must be greater than zero.</param>
    /// <param name="channels">Number of audio channels (e.g., 2 for stereo). Must be greater than zero.</param>
    /// <param name="logger">
    /// Optional logger, used only to warn when <see cref="SyncCorrectionOptions.MaxSpeedCorrection"/>
    /// exceeds the spec's cap and is therefore being clamped. Without one the clamp is silent.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="sampleRate"/> or <paramref name="channels"/> is less than or equal to zero.
    /// </exception>
    public SyncCorrectionCalculator(
        SyncCorrectionOptions? options,
        int sampleRate,
        int channels,
        ILogger? logger = null)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate,
                "Sample rate must be greater than zero.");
        }

        if (channels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channels), channels,
                "Channel count must be greater than zero.");
        }

        _options = options?.Clone() ?? SyncCorrectionOptions.Default;
        _options.Validate();
        SyncCorrectionPolicy.WarnIfSpeedCapExceeded(_options, logger ?? NullLogger.Instance);
        _sampleRate = sampleRate;
        _channels = channels;
        _hardSyncStall = new HardSyncStallDetector(_options);
    }

    /// <inheritdoc/>
    public void UpdateFromSyncError(long rawMicroseconds, double smoothedMicroseconds)
    {
        bool changed;
        lock (_lock)
        {
            changed = UpdateCorrectionInternal(rawMicroseconds, smoothedMicroseconds);
        }

        // Fire event outside lock to prevent deadlocks
        if (changed)
        {
            CorrectionChanged?.Invoke(this);
        }
    }

    /// <inheritdoc/>
    public void Reset()
    {
        bool changed;
        lock (_lock)
        {
            changed = _currentMode != SyncCorrectionMode.None
                || Math.Abs(_targetPlaybackRate - 1.0) > 0.0001;

            _currentMode = SyncCorrectionMode.None;
            _targetPlaybackRate = 1.0;
            _totalSamplesProcessed = 0;
            _inStartupGracePeriod = true;
            _inReconnectStabilization = false;
            _reconnectSamplesProcessed = 0;
            _hardSyncStall.Reset();
        }

        if (changed)
        {
            CorrectionChanged?.Invoke(this);
        }
    }

    /// <summary>
    /// Notifies the provider that a WebSocket reconnect occurred.
    /// Suppresses corrections during the reconnect stabilization period.
    /// </summary>
    /// <remarks>
    /// <para>
    /// After reconnect, the Kalman clock synchronizer is reset and needs ~2 seconds
    /// to re-converge. During this window, sync error measurements are unreliable.
    /// This method sets a stabilization flag that causes <see cref="UpdateFromSyncError"/>
    /// to return neutral corrections until the period elapses.
    /// </para>
    /// <para>
    /// Multiple rapid reconnects restart the stabilization window each time.
    /// </para>
    /// </remarks>
    public void NotifyReconnect()
    {
        bool changed;
        lock (_lock)
        {
            changed = _currentMode != SyncCorrectionMode.None
                || Math.Abs(_targetPlaybackRate - 1.0) > 0.0001;

            _inReconnectStabilization = true;
            _reconnectSamplesProcessed = 0;
            _currentMode = SyncCorrectionMode.None;
            _targetPlaybackRate = 1.0;
            _hardSyncStall.Reset();
        }

        if (changed)
        {
            CorrectionChanged?.Invoke(this);
        }
    }

    /// <summary>
    /// Notifies the calculator that samples were processed.
    /// Call this after applying corrections to track startup grace period and reconnect stabilization.
    /// </summary>
    /// <param name="samplesProcessed">Number of samples processed.</param>
    public void NotifySamplesProcessed(int samplesProcessed)
    {
        lock (_lock)
        {
            _totalSamplesProcessed += samplesProcessed;

            // Check if we've exited the startup grace period
            if (_inStartupGracePeriod)
            {
                var microsecondsPerSample = 1_000_000.0 / (_sampleRate * _channels);
                var elapsedMicroseconds = (long)(_totalSamplesProcessed * microsecondsPerSample);
                if (elapsedMicroseconds >= _options.StartupGracePeriodMicroseconds)
                {
                    _inStartupGracePeriod = false;
                }
            }

            // Check if we've exited the reconnect stabilization period
            if (_inReconnectStabilization)
            {
                _reconnectSamplesProcessed += samplesProcessed;
                var microsecondsPerSample = 1_000_000.0 / (_sampleRate * _channels);
                var elapsedMicroseconds = (long)(_reconnectSamplesProcessed * microsecondsPerSample);
                if (elapsedMicroseconds >= _options.ReconnectStabilizationMicroseconds)
                {
                    _inReconnectStabilization = false;
                }
            }
        }
    }

    /// <summary>
    /// Updates correction parameters based on smoothed sync error.
    /// Must be called under lock.
    /// </summary>
    /// <returns>True if correction parameters changed.</returns>
    private bool UpdateCorrectionInternal(long rawMicroseconds, double smoothedMicroseconds)
    {
        var previousMode = _currentMode;
        var previousRate = _targetPlaybackRate;

        // During startup grace period, don't apply corrections
        if (_inStartupGracePeriod)
        {
            _currentMode = SyncCorrectionMode.None;
            _targetPlaybackRate = 1.0;
            return HasChanged(previousMode, previousRate);
        }

        // While the Kalman filter is re-converging after reconnect, sync error
        // measurements are unreliable; suppress corrections.
        if (_inReconnectStabilization)
        {
            _currentMode = SyncCorrectionMode.None;
            _targetPlaybackRate = 1.0;
            return HasChanged(previousMode, previousRate);
        }

        // One decision ladder, shared with TimedAudioBuffer's internal corrector, and one
        // currency. The mechanism is not decided here: this object cannot see whether its caller
        // has a resampler, and the SyncCorrectionOptions.Mechanism it used to read belongs to a
        // different object's copy of the options. A caller without a resampler converts the rate
        // to a stepping interval itself.
        // That ladder includes the stand-down, timed against playback rather than wall clock so
        // both copies of the detector score the same error stream the same way.
        var now = (long)(_totalSamplesProcessed * (1_000_000.0 / (_sampleRate * _channels)));
        var decision = SyncCorrectionPolicy.Decide(
            smoothedMicroseconds,
            _options,
            suppressHardSync: _hardSyncStall.ShouldStandDown(smoothedMicroseconds, now));

        if (decision.Mode == SyncCorrectionMode.HardSync)
        {
            // The buffer sizes its snap from the raw error, so the cooldown and the convergence
            // score are measured against the same figure.
            _hardSyncStall.RecordSnap(rawMicroseconds, now);
        }

        _currentMode = decision.Mode;
        _targetPlaybackRate = decision.TargetPlaybackRate;

        return HasChanged(previousMode, previousRate);
    }

    /// <summary>
    /// Checks if correction parameters changed from previous values.
    /// </summary>
    private bool HasChanged(SyncCorrectionMode previousMode, double previousRate)
    {
        return previousMode != _currentMode
            || Math.Abs(previousRate - _targetPlaybackRate) > 0.0001;
    }
}
