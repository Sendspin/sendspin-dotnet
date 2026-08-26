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
/// a one-shot snap (<see cref="SyncCorrectionDecision.IsHardSync"/>) — one discontinuity,
/// exempt from the speed cap;</item>
/// <item>below <see cref="SyncCorrectionOptions.ResamplingThresholdMicroseconds"/>: a rate
/// adjustment clamped to <see cref="SyncCorrectionOptions.MaxSpeedCorrection"/>;</item>
/// <item>otherwise: discrete frame drop/insert, whose interval is floored so the implied
/// speed change also respects the cap.</item>
/// </list>
/// <para>
/// Errors above <see cref="SyncCorrectionOptions.ReanchorThresholdMicroseconds"/> are not a
/// tier here: the buffer clears and re-anchors, and until its cooldown allows that it keeps
/// correcting through the drop/insert band.
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
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="channels">Channel count.</param>
    /// <param name="selfApplied">
    /// True for a corrector that has no resampler and must realize the continuous tier itself,
    /// which is the case for <see cref="TimedAudioBuffer.Read"/>. Such a caller gets the same
    /// speed change expressed as frame stepping — the spec's own suggested strategy
    /// (roles/player/v1.md:169-176) and what the C++ reference does per chunk — instead of a
    /// rate it has nothing to apply to. False for a caller that drives a resampler.
    /// </param>
    /// <returns>The correction to apply.</returns>
    internal static SyncCorrectionDecision Decide(
        double smoothedMicroseconds,
        SyncCorrectionOptions options,
        int sampleRate,
        int channels,
        bool selfApplied = false)
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

        // A rate is only a correction to someone who can resample. The buffer's own read path
        // cannot, so for it the continuous tier is expressed as frame stepping of the same
        // magnitude; handing it an advisory rate instead left the error to walk up to the
        // hard-sync threshold and splice, which is not what "rare" means.
        if (!selfApplied && absError < options.ResamplingThresholdMicroseconds)
        {
            // Rate = 1 + error / (targetSeconds × 1e6), clamped to the spec's speed cap.
            var correctionFactor = Math.Clamp(
                smoothedMicroseconds / options.CorrectionTargetSeconds / 1_000_000.0,
                -options.EffectiveMaxSpeedCorrection,
                options.EffectiveMaxSpeedCorrection);

            return new SyncCorrectionDecision(
                SyncCorrectionMode.Resampling,
                1.0 + correctionFactor,
                DropEveryNFrames: 0,
                InsertEveryNFrames: 0);
        }

        var interval = CorrectionInterval(absError, options, sampleRate, channels);

        return smoothedMicroseconds > 0
            ? new SyncCorrectionDecision(SyncCorrectionMode.Dropping, 1.0, interval, 0)
            : new SyncCorrectionDecision(SyncCorrectionMode.Inserting, 1.0, 0, interval);
    }

    /// <summary>
    /// Frames between consecutive discrete corrections, i.e. correct one frame every N.
    /// </summary>
    /// <remarks>
    /// One frame in N is a speed change of 1/N, so the cap is enforced by flooring N at
    /// <c>ceil(1 / MaxSpeedCorrection)</c> — 200 frames at the spec's ±0.5%. This is the
    /// per-chunk bound <c>N ≤ floor(0.005 × samples_in_chunk)</c> (roles/player/v1.md:174)
    /// restated as a rate, and it holds for any chunk length. Rounding is deliberately
    /// upward: truncating N downward is what let the old code sit just over the cap.
    /// </remarks>
    private static int CorrectionInterval(
        double absError,
        SyncCorrectionOptions options,
        int sampleRate,
        int channels)
    {
        var framesError = absError * sampleRate / 1_000_000.0;
        var desiredCorrectionsPerSec = framesError / options.CorrectionTargetSeconds;
        var framesPerSecond = (double)sampleRate;
        var maxCorrectionsPerSec = framesPerSecond * options.EffectiveMaxSpeedCorrection;
        var actualCorrectionsPerSec = Math.Min(desiredCorrectionsPerSec, maxCorrectionsPerSec);

        var interval = actualCorrectionsPerSec > 0
            ? (int)Math.Ceiling(framesPerSecond / actualCorrectionsPerSec)
            : 0;

        // The speed cap, as a hard floor on the interval.
        interval = Math.Max(interval, (int)Math.Ceiling(1.0 / options.EffectiveMaxSpeedCorrection));

        // Floor to channels × 10 frames so corrections don't run faster than ~440Hz at 48kHz stereo.
        return Math.Max(interval, channels * 10);
    }
}

/// <summary>
/// The correction <see cref="SyncCorrectionPolicy"/> selected for one sync-error reading.
/// </summary>
/// <param name="Mode">Which tier applies, in the vocabulary an external corrector sees.</param>
/// <param name="TargetPlaybackRate">Resampling rate; 1.0 in every other mode.</param>
/// <param name="DropEveryNFrames">Drop one frame every N; 0 when not dropping.</param>
/// <param name="InsertEveryNFrames">Insert one frame every N; 0 when not inserting.</param>
/// <param name="IsHardSync">
/// True for the one-shot tier. Carried as a flag rather than as a
/// <see cref="SyncCorrectionMode"/> member because 9.x's enum is frozen; to an external
/// corrector the decision is indistinguishable from
/// <see cref="SyncCorrectionMode.None"/>, which is exactly right — it must stand down while
/// <see cref="TimedAudioBuffer"/> performs the snap itself.
/// </param>
internal readonly record struct SyncCorrectionDecision(
    SyncCorrectionMode Mode,
    double TargetPlaybackRate,
    int DropEveryNFrames,
    int InsertEveryNFrames,
    bool IsHardSync = false)
{
    /// <summary>Gets the neutral decision: no correction of any kind.</summary>
    internal static SyncCorrectionDecision None { get; } =
        new(SyncCorrectionMode.None, 1.0, 0, 0);

    /// <summary>
    /// Gets the one-shot decision. The magnitude is not carried here: the buffer derives it
    /// from the same error, and an external corrector must stand down entirely (rate 1.0, no
    /// stepping) because only the buffer can skip or manufacture the samples involved.
    /// </summary>
    internal static SyncCorrectionDecision HardSync { get; } =
        new(SyncCorrectionMode.None, 1.0, 0, 0, IsHardSync: true);
}
