// <copyright file="SyncCorrectionOptions.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

namespace Sendspin.SDK.Audio;

/// <summary>
/// Configuration for sync correction in <see cref="TimedAudioBuffer"/>. Defaults are
/// tuned for Windows WASAPI; Linux/macOS callers may want <see cref="CliDefaults"/>.
/// </summary>
public sealed class SyncCorrectionOptions
{
    /// <summary>
    /// Sync errors below this magnitude are ignored. Default 1 ms.
    /// </summary>
    public long DeadbandMicroseconds { get; set; } = 1_000;

    /// <summary>
    /// Maximum allowed playback-rate deviation from 1.0. Human pitch-perception
    /// threshold is roughly 3%, so values up to 0.04 are typically imperceptible.
    /// Default 0.02 (2%); the Python CLI uses 0.04.
    /// </summary>
    public double MaxSpeedCorrection { get; set; } = 0.02;

    /// <summary>
    /// Target time, in seconds, over which sync error should be corrected.
    /// Smaller values correct faster but can overshoot on jittery platforms.
    /// Default 3.0; the Python CLI uses 2.0.
    /// </summary>
    public double CorrectionTargetSeconds { get; set; } = 3.0;

    /// <summary>
    /// Below this error magnitude the correction is a smooth rate adjustment;
    /// above it the correction switches to frame drop/insert. Default 100 ms.
    /// </summary>
    /// <remarks>
    /// Rate adjustment is inaudible (bounded by <see cref="MaxSpeedCorrection"/>),
    /// while frame drop/insert is audible as stutter. Moderate errors should
    /// therefore route through resampling: at the default 2% max correction a
    /// 100 ms error closes in ~5 s without a perceptible pitch change. Reserve
    /// drop/insert for errors rate correction cannot close in reasonable time.
    /// </remarks>
    public long ResamplingThresholdMicroseconds { get; set; } = 100_000;

    /// <summary>
    /// Above this error magnitude the buffer is cleared and sync is restarted.
    /// Default 500 ms.
    /// </summary>
    public long ReanchorThresholdMicroseconds { get; set; } = 500_000;

    /// <summary>
    /// Minimum time between consecutive re-anchors. Prevents rapid repeated
    /// re-anchors during persistent clock error. Default 5 s.
    /// </summary>
    public long ReanchorCooldownMicroseconds { get; set; } = 5_000_000;

    /// <summary>
    /// Initial period after playback starts during which corrections are suppressed
    /// to let timing stabilize. Default 500 ms.
    /// </summary>
    public long StartupGracePeriodMicroseconds { get; set; } = 500_000;

    /// <summary>
    /// Period after a reconnect during which corrections are suppressed while the
    /// Kalman filter re-converges. Default 2 s.
    /// </summary>
    public long ReconnectStabilizationMicroseconds { get; set; } = 2_000_000;

    /// <summary>
    /// Tolerance window around the scheduled start time. Compensates for audio
    /// callback timing granularity. Default 10 ms.
    /// </summary>
    public long ScheduledStartGraceWindowMicroseconds { get; set; } = 10_000;

    /// <summary>
    /// Gets the minimum playback rate (1.0 - MaxSpeedCorrection).
    /// </summary>
    public double MinRate => 1.0 - MaxSpeedCorrection;

    /// <summary>
    /// Gets the maximum playback rate (1.0 + MaxSpeedCorrection).
    /// </summary>
    public double MaxRate => 1.0 + MaxSpeedCorrection;

    /// <summary>
    /// Validates the options and throws if invalid.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when options are invalid.</exception>
    public void Validate()
    {
        if (DeadbandMicroseconds < 0)
        {
            throw new ArgumentException(
                "DeadbandMicroseconds must be non-negative.",
                nameof(DeadbandMicroseconds));
        }

        if (MaxSpeedCorrection is <= 0 or > 1.0)
        {
            throw new ArgumentException(
                "MaxSpeedCorrection must be between 0 (exclusive) and 1.0 (inclusive).",
                nameof(MaxSpeedCorrection));
        }

        if (CorrectionTargetSeconds <= 0)
        {
            throw new ArgumentException(
                "CorrectionTargetSeconds must be positive.",
                nameof(CorrectionTargetSeconds));
        }

        if (ResamplingThresholdMicroseconds < 0)
        {
            throw new ArgumentException(
                "ResamplingThresholdMicroseconds must be non-negative.",
                nameof(ResamplingThresholdMicroseconds));
        }

        if (ReanchorThresholdMicroseconds <= ResamplingThresholdMicroseconds)
        {
            throw new ArgumentException(
                "ReanchorThresholdMicroseconds must be greater than ResamplingThresholdMicroseconds.",
                nameof(ReanchorThresholdMicroseconds));
        }

        if (ReanchorCooldownMicroseconds < 0)
        {
            throw new ArgumentException(
                "ReanchorCooldownMicroseconds must be non-negative.",
                nameof(ReanchorCooldownMicroseconds));
        }

        if (StartupGracePeriodMicroseconds < 0)
        {
            throw new ArgumentException(
                "StartupGracePeriodMicroseconds must be non-negative.",
                nameof(StartupGracePeriodMicroseconds));
        }

        if (ScheduledStartGraceWindowMicroseconds < 0)
        {
            throw new ArgumentException(
                "ScheduledStartGraceWindowMicroseconds must be non-negative.",
                nameof(ScheduledStartGraceWindowMicroseconds));
        }

        if (ReconnectStabilizationMicroseconds < 0)
        {
            throw new ArgumentException(
                "ReconnectStabilizationMicroseconds must be non-negative.",
                nameof(ReconnectStabilizationMicroseconds));
        }
    }

    /// <summary>
    /// Creates a copy of these options.
    /// </summary>
    /// <returns>A new instance with the same values.</returns>
    public SyncCorrectionOptions Clone() => new()
    {
        DeadbandMicroseconds = DeadbandMicroseconds,
        MaxSpeedCorrection = MaxSpeedCorrection,
        CorrectionTargetSeconds = CorrectionTargetSeconds,
        ResamplingThresholdMicroseconds = ResamplingThresholdMicroseconds,
        ReanchorThresholdMicroseconds = ReanchorThresholdMicroseconds,
        ReanchorCooldownMicroseconds = ReanchorCooldownMicroseconds,
        StartupGracePeriodMicroseconds = StartupGracePeriodMicroseconds,
        ScheduledStartGraceWindowMicroseconds = ScheduledStartGraceWindowMicroseconds,
        ReconnectStabilizationMicroseconds = ReconnectStabilizationMicroseconds,
    };

    /// <summary>
    /// Gets the default options (matching current Windows behavior).
    /// </summary>
    public static SyncCorrectionOptions Default => new();

    /// <summary>
    /// Gets options matching the Python CLI defaults (more aggressive).
    /// </summary>
    /// <remarks>
    /// The CLI uses tighter tolerances and faster correction, which works well
    /// on platforms with precise timing (hardware audio interfaces, etc.).
    /// </remarks>
    public static SyncCorrectionOptions CliDefaults => new()
    {
        DeadbandMicroseconds = 1_000,
        MaxSpeedCorrection = 0.04,        // 4% vs Windows 2%
        CorrectionTargetSeconds = 2.0,    // 2s vs Windows 3s
        ResamplingThresholdMicroseconds = 15_000,
        ReanchorThresholdMicroseconds = 500_000,
        StartupGracePeriodMicroseconds = 500_000,
        ScheduledStartGraceWindowMicroseconds = 10_000,
    };
}
