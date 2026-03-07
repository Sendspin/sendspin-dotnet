// <copyright file="SyncCorrectionOptions.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

namespace Sendspin.SDK.Audio;

/// <summary>
/// Configuration options for audio sync correction in <see cref="TimedAudioBuffer"/>.
/// </summary>
/// <remarks>
/// <para>
/// These options control how the buffer corrects timing drift between the local clock
/// and the server's audio stream. The defaults are tuned for Windows WASAPI but may
/// need adjustment for other platforms (Linux ALSA/PulseAudio, macOS CoreAudio, etc.).
/// </para>
/// <para>
/// The sync correction uses a tiered approach with proportional rate adjustment:
/// <list type="bullet">
/// <item>Tier 1 (Deadband): Errors below 1ms are ignored</item>
/// <item>Tier 2 (Proportional): Errors 1-15ms use proportional rate adjustment
/// calculated as: rate = 1.0 + (error / <see cref="CorrectionTargetSeconds"/> / 1,000,000),
/// clamped to <see cref="MaxSpeedCorrection"/>. This matches the Python CLI approach.</item>
/// <item>Tier 3 (Drop/Insert): Errors 15ms+ use frame manipulation for faster correction</item>
/// <item>Tier 4 (Re-anchor): Errors exceeding <see cref="ReanchorThresholdMicroseconds"/> trigger buffer clear</item>
/// </list>
/// </para>
/// <para>
/// Sync error measurements are smoothed using an exponential moving average (EMA) to
/// filter jitter and prevent oscillation. Proportional correction prevents overshoot
/// by adjusting rate based on error magnitude rather than using fixed rate steps.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Typical usage - most options use defaults
/// var options = new SyncCorrectionOptions
/// {
///     CorrectionTargetSeconds = 2.0,    // Faster convergence
///     MaxSpeedCorrection = 0.04,        // Allow up to 4% rate adjustment
/// };
/// </code>
/// </example>
public sealed class SyncCorrectionOptions
{
    /// <summary>
    /// Gets or sets the deadband threshold (microseconds).
    /// Sync errors below this are ignored — no correction is applied.
    /// </summary>
    /// <remarks>
    /// Matches the JS/CLI spec: 1ms deadband. Errors within this range are
    /// considered "in sync" and do not trigger rate adjustment or frame manipulation.
    /// Default: 1000 (1ms).
    /// </remarks>
    public long DeadbandMicroseconds { get; set; } = 1_000;

    /// <summary>
    /// Gets or sets the maximum playback rate adjustment (0.0 to 1.0).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Controls how aggressively playback speed is adjusted. A value of 0.02 means
    /// rates between 0.98x and 1.02x (2% adjustment). Human pitch perception threshold
    /// is approximately 3%, so values up to 0.04 are generally imperceptible.
    /// </para>
    /// <para>
    /// Lower values reduce oscillation on platforms with timing jitter but correct more slowly.
    /// The Python CLI uses 0.04 (4%), while Windows defaults to 0.02 (2%) for stability.
    /// </para>
    /// <para>
    /// Default: 0.02 (2%).
    /// </para>
    /// </remarks>
    public double MaxSpeedCorrection { get; set; } = 0.02;

    /// <summary>
    /// Gets or sets the target time to eliminate drift (seconds).
    /// </summary>
    /// <remarks>
    /// Controls how quickly the sync error should be corrected. Lower values are more
    /// aggressive and correct faster, but may overshoot on platforms with timing jitter.
    /// The Python CLI uses 2.0 seconds.
    /// Default: 3.0 seconds.
    /// </remarks>
    public double CorrectionTargetSeconds { get; set; } = 3.0;

    /// <summary>
    /// Gets or sets the threshold between resampling and drop/insert correction (microseconds).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Errors below this threshold use smooth playback rate adjustment (resampling),
    /// which is imperceptible. Errors above use frame drop/insert for faster correction,
    /// which may cause minor audio artifacts.
    /// </para>
    /// <para>
    /// Default: 15000 (15ms).
    /// </para>
    /// </remarks>
    public long ResamplingThresholdMicroseconds { get; set; } = 15_000;

    /// <summary>
    /// Gets or sets the threshold for re-anchoring (microseconds).
    /// </summary>
    /// <remarks>
    /// When sync error exceeds this threshold, the buffer is cleared and sync is restarted.
    /// This handles catastrophic drift that cannot be corrected incrementally.
    /// Default: 500000 (500ms).
    /// </remarks>
    public long ReanchorThresholdMicroseconds { get; set; } = 500_000;

    /// <summary>
    /// Gets or sets the minimum time between re-anchors (microseconds).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Prevents rapid repeated re-anchors when the clock synchronizer has persistent error
    /// (e.g., during reconnect re-convergence or network instability). Without a cooldown,
    /// re-anchors can trigger every ~750ms (500ms grace + 250ms rebuffer), causing audio stuttering.
    /// </para>
    /// <para>
    /// This value matches the Android client's <c>REANCHOR_COOLDOWN_US</c> and the Python CLI's
    /// <c>_REANCHOR_COOLDOWN_US</c>.
    /// Default: 5000000 (5 seconds).
    /// </para>
    /// </remarks>
    public long ReanchorCooldownMicroseconds { get; set; } = 5_000_000;

    /// <summary>
    /// Gets or sets the startup grace period (microseconds).
    /// </summary>
    /// <remarks>
    /// No sync correction is applied during this initial period after playback starts.
    /// This allows playback to stabilize before measuring drift, preventing false
    /// corrections due to initial timing jitter.
    /// Default: 500000 (500ms).
    /// </remarks>
    public long StartupGracePeriodMicroseconds { get; set; } = 500_000;

    /// <summary>
    /// Gets or sets the reconnect stabilization period (microseconds).
    /// </summary>
    /// <remarks>
    /// <para>
    /// After a WebSocket reconnect, the clock synchronizer is reset and needs time to
    /// re-converge. During this window, sync error measurements are unreliable because
    /// they are based on a freshly-reset Kalman filter with high uncertainty.
    /// </para>
    /// <para>
    /// Without suppression, the sync correction system reacts to these inaccurate
    /// measurements, causing erratic drop/insert or resampling corrections that produce
    /// audible artifacts.
    /// </para>
    /// <para>
    /// This value matches the Android client's <c>RECONNECT_STABILIZATION_US</c>.
    /// Default: 2000000 (2 seconds).
    /// </para>
    /// </remarks>
    public long ReconnectStabilizationMicroseconds { get; set; } = 2_000_000;

    /// <summary>
    /// Gets or sets the grace window for scheduled start (microseconds).
    /// </summary>
    /// <remarks>
    /// Playback starts when current time is within this window of the scheduled start time.
    /// This compensates for audio callback timing granularity that might cause starting
    /// slightly late.
    /// Default: 10000 (10ms).
    /// </remarks>
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
