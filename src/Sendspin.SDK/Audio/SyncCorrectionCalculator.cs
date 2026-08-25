// <copyright file="SyncCorrectionCalculator.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sendspin.SDK.Audio;

/// <summary>
/// Computes sync-correction parameters (playback rate, drop/insert intervals)
/// from an observed sync error and raises <see cref="CorrectionChanged"/>
/// when those parameters change. Subscribers are responsible for unsubscribing
/// before discarding the reference.
/// </summary>
/// <remarks>
/// The decision itself lives in <see cref="SyncCorrectionPolicy"/>, shared with
/// <see cref="TimedAudioBuffer"/>'s internal corrector so the two cannot diverge.
/// When the policy selects <see cref="SyncCorrectionMode.HardSync"/> this reports
/// a neutral correction: the buffer applies that snap itself on both read paths,
/// and a caller adding its own on top would double-correct.
/// </remarks>
public sealed class SyncCorrectionCalculator : ISyncCorrectionProvider
{
    private readonly SyncCorrectionOptions _options;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly object _lock = new();

    private SyncCorrectionMode _currentMode = SyncCorrectionMode.None;
    private int _dropEveryNFrames;
    private int _insertEveryNFrames;
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
    public int DropEveryNFrames
    {
        get { lock (_lock) return _dropEveryNFrames; }
    }

    /// <inheritdoc/>
    public int InsertEveryNFrames
    {
        get { lock (_lock) return _insertEveryNFrames; }
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
    }

    /// <inheritdoc/>
    public void UpdateFromSyncError(long rawMicroseconds, double smoothedMicroseconds)
    {
        bool changed;
        lock (_lock)
        {
            changed = UpdateCorrectionInternal(smoothedMicroseconds);
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
                || _dropEveryNFrames != 0
                || _insertEveryNFrames != 0
                || Math.Abs(_targetPlaybackRate - 1.0) > 0.0001;

            _currentMode = SyncCorrectionMode.None;
            _dropEveryNFrames = 0;
            _insertEveryNFrames = 0;
            _targetPlaybackRate = 1.0;
            _totalSamplesProcessed = 0;
            _inStartupGracePeriod = true;
            _inReconnectStabilization = false;
            _reconnectSamplesProcessed = 0;
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
                || _dropEveryNFrames != 0
                || _insertEveryNFrames != 0
                || Math.Abs(_targetPlaybackRate - 1.0) > 0.0001;

            _inReconnectStabilization = true;
            _reconnectSamplesProcessed = 0;
            _currentMode = SyncCorrectionMode.None;
            _dropEveryNFrames = 0;
            _insertEveryNFrames = 0;
            _targetPlaybackRate = 1.0;
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
    private bool UpdateCorrectionInternal(double smoothedMicroseconds)
    {
        var previousMode = _currentMode;
        var previousDrop = _dropEveryNFrames;
        var previousInsert = _insertEveryNFrames;
        var previousRate = _targetPlaybackRate;

        // During startup grace period, don't apply corrections
        if (_inStartupGracePeriod)
        {
            _currentMode = SyncCorrectionMode.None;
            _targetPlaybackRate = 1.0;
            _dropEveryNFrames = 0;
            _insertEveryNFrames = 0;
            return HasChanged(previousMode, previousDrop, previousInsert, previousRate);
        }

        // While the Kalman filter is re-converging after reconnect, sync error
        // measurements are unreliable; suppress corrections.
        if (_inReconnectStabilization)
        {
            _currentMode = SyncCorrectionMode.None;
            _targetPlaybackRate = 1.0;
            _dropEveryNFrames = 0;
            _insertEveryNFrames = 0;
            return HasChanged(previousMode, previousDrop, previousInsert, previousRate);
        }

        // One decision ladder, shared with TimedAudioBuffer's internal corrector. The mechanism
        // decides the currency, not the amount: a caller that has no resampler gets the same speed
        // change expressed as frame stepping rather than a rate it cannot apply to anything.
        var decision = SyncCorrectionPolicy.Decide(
            smoothedMicroseconds,
            _options,
            _sampleRate,
            _channels,
            selfApplied: _options.Mechanism == SyncCorrectionMechanism.FrameStepping);

        _currentMode = decision.Mode;
        _targetPlaybackRate = decision.TargetPlaybackRate;
        _dropEveryNFrames = decision.DropEveryNFrames;
        _insertEveryNFrames = decision.InsertEveryNFrames;

        return HasChanged(previousMode, previousDrop, previousInsert, previousRate);
    }

    /// <summary>
    /// Checks if correction parameters changed from previous values.
    /// </summary>
    private bool HasChanged(SyncCorrectionMode previousMode, int previousDrop, int previousInsert, double previousRate)
    {
        return previousMode != _currentMode
            || previousDrop != _dropEveryNFrames
            || previousInsert != _insertEveryNFrames
            || Math.Abs(previousRate - _targetPlaybackRate) > 0.0001;
    }
}
