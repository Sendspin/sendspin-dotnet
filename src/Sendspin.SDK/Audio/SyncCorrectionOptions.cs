// <copyright file="SyncCorrectionOptions.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

namespace Sendspin.SDK.Audio;

/// <summary>
/// How an external corrector realizes the continuous correction tier. This is a choice of
/// <em>mechanism</em>, not of policy: the thresholds and the ±0.5% cap are spec constants under
/// either one.
/// </summary>
/// <remarks>
/// Read by <see cref="SyncCorrectedSampleSource"/> and by
/// <see cref="SyncCorrectionCalculator"/>, which reports its decision in whichever currency the
/// selected mechanism can spend. <see cref="TimedAudioBuffer.Read"/> ignores it — that path has no
/// resampler and always steps frames.
/// </remarks>
public enum SyncCorrectionMechanism
{
    /// <summary>
    /// Trim playback speed continuously through a resampler. The quality mode, and the default:
    /// a ±0.5% speed change is inaudible where stepping whole frames is faintly granular.
    /// </summary>
    SmoothResampling,

    /// <summary>
    /// Drop or duplicate whole frames instead, at an interval bounded by the same ±0.5% cap.
    /// The fallback, for hosts that must not carry a resampler in the output chain; it is the
    /// same mechanism <see cref="TimedAudioBuffer.Read"/> applies internally.
    /// </summary>
    FrameStepping,
}

/// <summary>
/// Configuration for sync correction in <see cref="TimedAudioBuffer"/>. Defaults are
/// tuned for Windows WASAPI; Linux/macOS callers may want <see cref="CliDefaults"/>.
/// </summary>
public sealed class SyncCorrectionOptions
{
    /// <summary>
    /// The spec's hard ceiling on continuous playback-speed deviation: the effective
    /// speed MUST stay within ±0.5% of normal, measured as a sliding average over
    /// 150 ms (spec roles/player/v1.md:134). A larger <see cref="MaxSpeedCorrection"/> is
    /// clamped to this rather than applied — see <see cref="EffectiveMaxSpeedCorrection"/>.
    /// </summary>
    /// <remarks>
    /// The cap bounds <em>steady-state</em> correction only. A one-shot
    /// resynchronization after a disturbance is explicitly exempt, which is what
    /// <see cref="HardSyncThresholdMicroseconds"/> implements.
    /// </remarks>
    public const double SpecMaxSpeedCorrection = 0.005;

    /// <summary>
    /// Sync errors below this magnitude are ignored. Default 100 µs, matching the
    /// spec's suggested dead band (roles/player/v1.md:172) and the C++ reference's
    /// <c>SOFT_SYNC_THRESHOLD_US</c>.
    /// </summary>
    /// <remarks>
    /// The spec requires steady-state error within ±1 ms (MUST) and asks for ±0.5 ms
    /// (SHOULD). A dead band at the MUST floor makes the SHOULD target unreachable by
    /// construction and leaves no margin, so the band sits an order of magnitude below
    /// it. Raise it only with a measured platform-jitter justification, and record that
    /// measurement where you set it.
    /// </remarks>
    public long DeadbandMicroseconds { get; set; } = 100;

    /// <summary>
    /// Maximum allowed playback-rate deviation from 1.0. Default 0.005 (0.5%), the
    /// spec's MUST cap — see <see cref="SpecMaxSpeedCorrection"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is not a comfort setting to be traded against correction speed. The cap is
    /// a fleet-homogeneity contract: every player in a group recovers from the same
    /// disturbance at the same bounded rate, so they stay audibly together while
    /// converging. Errors too large to close inside the cap are handled by the one-shot
    /// hard-sync tier (<see cref="HardSyncThresholdMicroseconds"/>), which the spec
    /// exempts from it — not by exceeding it.
    /// </para>
    /// <para>
    /// Setting this above <see cref="SpecMaxSpeedCorrection"/> does not raise the cap: the
    /// value is clamped where correction is applied, and the SDK logs a warning once when it
    /// sees one. Rejecting it outright would take a client that plays today and stop it from
    /// starting, which is a worse answer than playing it in conformance.
    /// </para>
    /// </remarks>
    public double MaxSpeedCorrection { get; set; } = SpecMaxSpeedCorrection;

    /// <summary>
    /// Gets the speed cap that is actually applied: <see cref="MaxSpeedCorrection"/> limited to
    /// <see cref="SpecMaxSpeedCorrection"/>. Every correction path uses this, so an
    /// over-permissive configured value can never produce an out-of-spec speed.
    /// </summary>
    public double EffectiveMaxSpeedCorrection =>
        Math.Min(MaxSpeedCorrection, SpecMaxSpeedCorrection);

    /// <summary>
    /// Gets whether <see cref="MaxSpeedCorrection"/> is set above the spec's cap and is
    /// therefore being clamped.
    /// </summary>
    public bool ExceedsSpecSpeedCap => MaxSpeedCorrection > SpecMaxSpeedCorrection;

    /// <summary>
    /// Target time, in seconds, over which sync error should be corrected.
    /// Smaller values correct faster but can overshoot on jittery platforms.
    /// Default 3.0; the Python CLI uses 2.0.
    /// </summary>
    public double CorrectionTargetSeconds { get; set; } = 3.0;

    /// <summary>
    /// Above this error magnitude the correction is a single discontinuity — skip or
    /// insert the exact excess in one step — instead of a continuous speed change.
    /// Default 5 ms, matching <c>HARD_SYNC_THRESHOLD_US</c> in the C++ reference
    /// (sync_task.cpp). Set to 0 to disable the tier.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The spec exempts a one-shot resynchronization from the ±0.5% speed cap and
    /// describes exactly this behaviour (roles/player/v1.md:178): when the error would
    /// otherwise exceed the ±1 ms floor, drop a leading prefix if late or insert
    /// silence if early. Grinding a 50 ms error out at 0.5% would take 10 s, during
    /// which this player audibly trails every reference player in the group; the snap
    /// closes it inside one callback.
    /// </para>
    /// <para>
    /// The tier sits between the rate/drop-insert band and
    /// <see cref="ReanchorThresholdMicroseconds"/>: errors above the re-anchor
    /// threshold are catastrophic and clear the buffer instead. Because the default
    /// (5 ms) is below <see cref="ResamplingThresholdMicroseconds"/>, the discrete
    /// drop/insert band is not reached with default settings — it is used only when a
    /// caller lowers the resampling threshold below this one.
    /// </para>
    /// <para>
    /// The snap is applied by <see cref="TimedAudioBuffer"/> itself on both the default
    /// (<c>Read</c>) and the external (<c>ReadRaw</c>) correction paths, because skipping
    /// buffered content or manufacturing silence is a buffer-timeline operation an
    /// external corrector cannot perform on the samples it has already been handed.
    /// </para>
    /// </remarks>
    public long HardSyncThresholdMicroseconds { get; set; } = 5_000;

    /// <summary>
    /// Below this error magnitude the correction is a smooth rate adjustment;
    /// above it the correction switches to frame drop/insert. Default 100 ms.
    /// </summary>
    /// <remarks>
    /// Rate adjustment is inaudible (bounded by <see cref="MaxSpeedCorrection"/>),
    /// while frame drop/insert is audible as stutter, so moderate errors route through
    /// resampling. Both are bounded by the same ±0.5% cap: the drop/insert interval is
    /// floored at <c>ceil(1 / MaxSpeedCorrection)</c> frames, which is the per-chunk
    /// bound <c>N ≤ floor(0.005 × samples_in_chunk)</c> from roles/player/v1.md:174
    /// expressed as a rate. <see cref="HardSyncThresholdMicroseconds"/> takes
    /// precedence above 5 ms by default, so this band is only reached when that tier
    /// is disabled or this threshold is lowered below it.
    /// </remarks>
    public long ResamplingThresholdMicroseconds { get; set; } = 100_000;

    /// <summary>
    /// Above this error magnitude the buffer is cleared and sync is restarted.
    /// Default 500 ms. This is the catastrophic tier above
    /// <see cref="HardSyncThresholdMicroseconds"/>.
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
    /// How an external corrector realizes the continuous tier — a resampler by default, whole-frame
    /// stepping as the fallback. See <see cref="SyncCorrectionMechanism"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This selects the mechanism, not the policy. It is <em>not</em> a way to correct harder or
    /// more gently: both mechanisms obey <see cref="DeadbandMicroseconds"/>,
    /// <see cref="CorrectionTargetSeconds"/> and the ±0.5% cap, and both hand the same errors to
    /// the same one-shot and re-anchor tiers.
    /// </para>
    /// <para>
    /// Distinct from <see cref="ResamplingThresholdMicroseconds"/>, which is a magnitude boundary
    /// <em>within</em> the resampling mechanism — how large an error is still worth trimming
    /// smoothly before switching to discrete corrections. This property decides whether a resampler
    /// is in the picture at all, and <see cref="TimedAudioBuffer.Read"/> ignores it either way.
    /// </para>
    /// </remarks>
    public SyncCorrectionMechanism Mechanism { get; set; } = SyncCorrectionMechanism.SmoothResampling;

    /// <summary>
    /// When true (default), the sync error tracks post-anchor movement of the Kalman
    /// clock offset, so absolute alignment to the server schedule holds over long
    /// gapless streams instead of drifting with relative crystal error. Output delay
    /// is excluded; delay changes keep their explicit re-anchor semantics.
    /// Set false to restore the pre-9.2 pace-only behavior.
    /// </summary>
    public bool TrackClockDrift { get; set; } = true;

    /// <summary>
    /// Tolerance window around the scheduled start time. Compensates for audio
    /// callback timing granularity. Default 10 ms.
    /// </summary>
    public long ScheduledStartGraceWindowMicroseconds { get; set; } = 10_000;

    /// <summary>
    /// Gets the minimum playback rate (1.0 - MaxSpeedCorrection).
    /// </summary>
    public double MinRate => 1.0 - EffectiveMaxSpeedCorrection;

    /// <summary>
    /// Gets the maximum playback rate (1.0 + MaxSpeedCorrection).
    /// </summary>
    public double MaxRate => 1.0 + EffectiveMaxSpeedCorrection;

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

        // Only nonsensical values are rejected. A value above the spec cap is a real
        // misconfiguration, but it is one the SDK can honour safely by clamping — see
        // EffectiveMaxSpeedCorrection — and throwing would stop a client that plays today
        // from starting at all.
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

        if (HardSyncThresholdMicroseconds < 0)
        {
            throw new ArgumentException(
                "HardSyncThresholdMicroseconds must be non-negative (0 disables the tier).",
                nameof(HardSyncThresholdMicroseconds));
        }

        if (HardSyncThresholdMicroseconds >= ReanchorThresholdMicroseconds)
        {
            throw new ArgumentException(
                "HardSyncThresholdMicroseconds must be less than ReanchorThresholdMicroseconds; " +
                "the one-shot snap tier sits below the catastrophic re-anchor tier.",
                nameof(HardSyncThresholdMicroseconds));
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
        HardSyncThresholdMicroseconds = HardSyncThresholdMicroseconds,
        ResamplingThresholdMicroseconds = ResamplingThresholdMicroseconds,
        ReanchorThresholdMicroseconds = ReanchorThresholdMicroseconds,
        ReanchorCooldownMicroseconds = ReanchorCooldownMicroseconds,
        StartupGracePeriodMicroseconds = StartupGracePeriodMicroseconds,
        ScheduledStartGraceWindowMicroseconds = ScheduledStartGraceWindowMicroseconds,
        ReconnectStabilizationMicroseconds = ReconnectStabilizationMicroseconds,
        TrackClockDrift = TrackClockDrift,
        Mechanism = Mechanism,
    };

    /// <summary>
    /// Gets the default options (matching current Windows behavior).
    /// </summary>
    public static SyncCorrectionOptions Default => new();

    /// <summary>
    /// Gets options matching the Python CLI defaults (more aggressive).
    /// </summary>
    /// <remarks>
    /// The CLI converges faster (shorter correction target, tighter resampling band),
    /// which works well on platforms with precise timing (hardware audio interfaces,
    /// etc.). It does <em>not</em> loosen the dead band or the speed cap: both are
    /// spec conformance points, not platform tuning.
    /// </remarks>
    public static SyncCorrectionOptions CliDefaults => new()
    {
        DeadbandMicroseconds = 100,
        MaxSpeedCorrection = SpecMaxSpeedCorrection,
        CorrectionTargetSeconds = 2.0,    // 2s vs Windows 3s
        HardSyncThresholdMicroseconds = 5_000,
        ResamplingThresholdMicroseconds = 15_000,
        ReanchorThresholdMicroseconds = 500_000,
        StartupGracePeriodMicroseconds = 500_000,
        ScheduledStartGraceWindowMicroseconds = 10_000,
    };
}
