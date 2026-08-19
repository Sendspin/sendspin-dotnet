// <copyright file="SyncCorrectionPolicy.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

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
    /// Chooses the correction for a smoothed sync error.
    /// </summary>
    /// <param name="smoothedMicroseconds">
    /// Smoothed sync error. Positive = playing behind (speed up / drop), negative = playing
    /// ahead (slow down / insert).
    /// </param>
    /// <param name="options">Correction options (thresholds and the speed cap).</param>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="channels">Channel count.</param>
    /// <returns>The correction to apply.</returns>
    internal static SyncCorrectionDecision Decide(
        double smoothedMicroseconds,
        SyncCorrectionOptions options,
        int sampleRate,
        int channels)
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

        if (absError < options.ResamplingThresholdMicroseconds)
        {
            // Rate = 1 + error / (targetSeconds × 1e6), clamped to the spec's speed cap.
            var correctionFactor = Math.Clamp(
                smoothedMicroseconds / options.CorrectionTargetSeconds / 1_000_000.0,
                -options.MaxSpeedCorrection,
                options.MaxSpeedCorrection);

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
        var maxCorrectionsPerSec = framesPerSecond * options.MaxSpeedCorrection;
        var actualCorrectionsPerSec = Math.Min(desiredCorrectionsPerSec, maxCorrectionsPerSec);

        var interval = actualCorrectionsPerSec > 0
            ? (int)Math.Ceiling(framesPerSecond / actualCorrectionsPerSec)
            : 0;

        // The speed cap, as a hard floor on the interval.
        interval = Math.Max(interval, (int)Math.Ceiling(1.0 / options.MaxSpeedCorrection));

        // Floor to channels × 10 frames so corrections don't run faster than ~440Hz at 48kHz stereo.
        return Math.Max(interval, channels * 10);
    }
}

/// <summary>
/// The correction <see cref="SyncCorrectionPolicy"/> selected for one sync-error reading.
/// </summary>
/// <param name="Mode">Which tier applies.</param>
/// <param name="TargetPlaybackRate">Resampling rate; 1.0 in every other mode.</param>
/// <param name="DropEveryNFrames">Drop one frame every N; 0 when not dropping.</param>
/// <param name="InsertEveryNFrames">Insert one frame every N; 0 when not inserting.</param>
internal readonly record struct SyncCorrectionDecision(
    SyncCorrectionMode Mode,
    double TargetPlaybackRate,
    int DropEveryNFrames,
    int InsertEveryNFrames)
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
        new(SyncCorrectionMode.HardSync, 1.0, 0, 0);
}
