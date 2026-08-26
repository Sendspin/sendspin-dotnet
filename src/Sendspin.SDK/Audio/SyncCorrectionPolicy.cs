// <copyright file="SyncCorrectionPolicy.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Microsoft.Extensions.Logging;

namespace Sendspin.SDK.Audio;

/// <summary>
/// The single implementation of the tiered sync-correction decision, shared by
/// <see cref="TimedAudioBuffer"/>'s internal corrector and <see cref="SyncCorrectionCalculator"/>.
/// </summary>
/// <remarks>
/// <para>
/// The two used to carry independent copies of this ladder, which is how they drifted apart.
/// Keeping the decision here means an SDK player and an app-side corrector cannot disagree
/// about what a given error should do.
/// </para>
/// <para>
/// Tiers, from smallest error up (spec roles/player/v1.md:134, 142-143, 172-178; C++
/// reference sync_task.cpp):
/// </para>
/// <list type="number">
/// <item>below <see cref="SyncCorrectionOptions.DeadbandMicroseconds"/> (~100 µs): nothing;</item>
/// <item>above <see cref="SyncCorrectionOptions.HardSyncThresholdMicroseconds"/> (~5 ms):
/// <see cref="SyncCorrectionMode.HardSync"/> — one discontinuity, exempt from the speed cap;</item>
/// <item>below <see cref="SyncCorrectionOptions.ResamplingThresholdMicroseconds"/>: a rate
/// adjustment clamped to <see cref="SyncCorrectionOptions.MaxSpeedCorrection"/>;</item>
/// <item>otherwise: the same rate, tagged <see cref="SyncCorrectionMode.Dropping"/> or
/// <see cref="SyncCorrectionMode.Inserting"/> — an error too large to be worth trimming
/// smoothly.</item>
/// </list>
/// <para>
/// Errors above <see cref="SyncCorrectionOptions.ReanchorThresholdMicroseconds"/> are not a
/// tier here: the buffer clears and re-anchors, and until its cooldown allows that it keeps
/// correcting through the drop/insert band.
/// </para>
/// <para>
/// <b>The decision is always a rate.</b> A speed change is the whole correction; how it is
/// realized belongs to whoever applies it, because only that object knows whether it has a
/// resampler. A caller without one converts the rate to a drop/insert interval through
/// <see cref="SteppingIntervalFrames"/>, which is the exact same correction expressed the other
/// way round — one frame in N is a speed change of 1/N.
/// </para>
/// </remarks>
internal static class SyncCorrectionPolicy
{
    /// <summary>
    /// Logs once, at construction, when the configured speed cap exceeds the spec's and is
    /// therefore being clamped.
    /// </summary>
    /// <param name="options">The options a corrector was constructed with.</param>
    /// <param name="logger">Logger to warn through.</param>
    /// <remarks>
    /// Loud but not fatal. A value like 2% is a real misconfiguration — most often a
    /// configuration default written before the cap was enforced — and the client should be
    /// told; but refusing to construct would stop playback that works today, which is the worse
    /// of the two failures. Correction is applied at
    /// <see cref="SyncCorrectionOptions.EffectiveMaxSpeedCorrection"/> either way.
    /// </remarks>
    internal static void WarnIfSpeedCapExceeded(SyncCorrectionOptions options, ILogger logger)
    {
        if (!options.ExceedsSpecSpeedCap)
        {
            return;
        }

        logger.LogWarning(
            "[Correction] MaxSpeedCorrection is {Configured:P2}, above the spec's MUST cap " +
            "(roles/player/v1.md:134); correction will be applied at {Cap:P2} instead. Lower " +
            "the configured value — errors too large for the cap are handled by the one-shot " +
            "hard-sync tier, which the spec exempts.",
            options.MaxSpeedCorrection,
            SyncCorrectionOptions.SpecMaxSpeedCorrection);
    }

    /// <summary>
    /// Chooses the correction for a smoothed sync error.
    /// </summary>
    /// <param name="smoothedMicroseconds">
    /// Smoothed sync error. Positive = playing behind (speed up / drop), negative = playing
    /// ahead (slow down / insert).
    /// </param>
    /// <param name="options">Correction options (thresholds and the speed cap).</param>
    /// <returns>The correction to apply, always expressed as a playback rate.</returns>
    internal static SyncCorrectionDecision Decide(
        double smoothedMicroseconds,
        SyncCorrectionOptions options)
    {
        var absError = Math.Abs(smoothedMicroseconds);

        if (absError < options.DeadbandMicroseconds)
        {
            return SyncCorrectionDecision.None;
        }

        // One-shot tier. Bounded above by the re-anchor threshold: past that the error is
        // catastrophic and the buffer restarts rather than splicing half a second of audio.
        if (options.HardSyncThresholdMicroseconds > 0
            && absError > options.HardSyncThresholdMicroseconds
            && absError <= options.ReanchorThresholdMicroseconds)
        {
            return SyncCorrectionDecision.HardSync;
        }

        // Rate = 1 + error / (targetSeconds × 1e6), clamped to the spec's speed cap. This is the
        // whole continuous decision, in every band: the tier below only says whether the error is
        // small enough to be worth trimming smoothly, and the caller that applies it decides how.
        var correctionFactor = Math.Clamp(
            smoothedMicroseconds / options.CorrectionTargetSeconds / 1_000_000.0,
            -options.EffectiveMaxSpeedCorrection,
            options.EffectiveMaxSpeedCorrection);

        var rate = 1.0 + correctionFactor;

        if (absError < options.ResamplingThresholdMicroseconds)
        {
            return new SyncCorrectionDecision(SyncCorrectionMode.Resampling, rate);
        }

        return smoothedMicroseconds > 0
            ? new SyncCorrectionDecision(SyncCorrectionMode.Dropping, rate)
            : new SyncCorrectionDecision(SyncCorrectionMode.Inserting, rate);
    }

    /// <summary>
    /// Expresses a playback rate as a discrete frame drop/insert interval — correct one frame
    /// every N — for a corrector that has no resampler to apply the rate to.
    /// </summary>
    /// <param name="targetPlaybackRate">
    /// The rate to realize. Above 1.0 speeds up, so frames are dropped; below 1.0 slows down, so
    /// frames are inserted.
    /// </param>
    /// <param name="options">Correction options, for the speed cap.</param>
    /// <param name="channels">Channel count.</param>
    /// <returns>
    /// The drop interval or the insert interval; never both, because dropping and inserting at
    /// once is two corrections cancelling rather than one correction.
    /// </returns>
    /// <remarks>
    /// One frame in N is a speed change of 1/N, so the interval is simply the reciprocal of the
    /// rate's deviation from unity — the spec's own suggested strategy (roles/player/v1.md:169-176)
    /// and what the C++ reference does per chunk. The cap is enforced by clamping the deviation
    /// first, which floors N at <c>ceil(1 / MaxSpeedCorrection)</c> — 200 frames at the spec's
    /// ±0.5%. That is the per-chunk bound <c>N ≤ floor(0.005 × samples_in_chunk)</c>
    /// (roles/player/v1.md:174) restated as a rate, and it holds for any chunk length. Rounding is
    /// deliberately upward: truncating N downward is what let the old code sit just over the cap.
    /// </remarks>
    internal static (int DropEveryNFrames, int InsertEveryNFrames) SteppingIntervalFrames(
        double targetPlaybackRate,
        SyncCorrectionOptions options,
        int channels)
    {
        var deviation = targetPlaybackRate - 1.0;
        if (!double.IsFinite(deviation) || deviation == 0.0)
        {
            return (0, 0);
        }

        var magnitude = Math.Min(Math.Abs(deviation), options.EffectiveMaxSpeedCorrection);

        // The ceiling is relaxed by a relative whisker first. A rate is built as 1 + deviation and
        // recovered by subtracting 1, which can lose an ulp, so the exact reciprocal of an integer
        // interval comes back a hair above it — and a bare ceiling would then answer N+1 for a rate
        // that means N, quietly correcting at 1/201 where the ladder asked for 1/200. The tolerance
        // is nine orders of magnitude below the interval, so it cannot round a genuine fraction
        // away, and the speed it can add is ~5e-12 against a cap of 5e-3.
        const double ReciprocalTolerance = 1e-9;

        var frames = Math.Ceiling(1.0 / magnitude * (1.0 - ReciprocalTolerance));
        if (frames > int.MaxValue)
        {
            // Slower than one correction per 12 hours at 48 kHz: not a correction.
            return (0, 0);
        }

        // Floor to channels × 10 frames so corrections don't run faster than ~440Hz at 48kHz stereo.
        var interval = Math.Max((int)frames, channels * 10);

        return deviation > 0 ? (interval, 0) : (0, interval);
    }
}

/// <summary>
/// The correction <see cref="SyncCorrectionPolicy"/> selected for one sync-error reading.
/// </summary>
/// <param name="Mode">Which tier applies.</param>
/// <param name="TargetPlaybackRate">
/// The correction, as a playback speed; 1.0 when there is nothing to correct continuously.
/// A caller with no resampler spends it through
/// <see cref="SyncCorrectionPolicy.SteppingIntervalFrames"/>.
/// </param>
internal readonly record struct SyncCorrectionDecision(
    SyncCorrectionMode Mode,
    double TargetPlaybackRate)
{
    /// <summary>Gets the neutral decision: no correction of any kind.</summary>
    internal static SyncCorrectionDecision None { get; } =
        new(SyncCorrectionMode.None, 1.0);

    /// <summary>
    /// Gets the one-shot decision. The magnitude is not carried here: the buffer derives it
    /// from the same error, and an external corrector must stand down entirely because only
    /// the buffer can skip or manufacture the samples involved.
    /// </summary>
    internal static SyncCorrectionDecision HardSync { get; } =
        new(SyncCorrectionMode.HardSync, 1.0);
}
