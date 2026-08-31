// <copyright file="HardSyncStallDetector.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

namespace Sendspin.SDK.Audio;

/// <summary>
/// Watches the one-shot snap tier and stands it down when snapping is not closing the error.
/// </summary>
/// <remarks>
/// <para>
/// The snap tier had no cooldown and no convergence check, while the re-anchor tier directly
/// above it has a five-second one — and the snap tier can splice up to half a second in a single
/// step. An error that sits inside the snap band and does not respond to being spliced therefore
/// re-fires the tier as fast as snaps drain: issue #252 reports ~1,700 snaps in one session, each
/// splicing ~90 ms of silence, with buffer depth walking from 9 s to 29.6 s.
/// </para>
/// <para>
/// Two rules close that gap. A snap is not eligible again until its own duration has elapsed, so
/// a 90 ms splice cannot be followed by another 30 ms later; and a snap that leaves the error
/// where it found it counts against the tier, which stands down after
/// <see cref="StallAfterNonConvergingSnaps"/> of those in a row. Standing down is a fall-through
/// rather than a stop: <see cref="SyncCorrectionPolicy.Decide"/> skips the snap and returns the
/// continuous tier instead, so correction carries on at the spec's ±0.5% — inaudible, capped, and
/// still able to close genuine drift layered on top — while an error nothing can close simply
/// stays visible rather than being papered over with silence.
/// </para>
/// <para>
/// The rules read nothing but the smoothed error and a playback clock, which is what lets
/// <see cref="TimedAudioBuffer"/> and <see cref="SyncCorrectionCalculator"/> each own an instance
/// and still reach the same verdict from the same error stream. They have to: on the
/// <see cref="TimedAudioBuffer.ReadRaw"/> path the host drives the calculator through
/// <see cref="ISyncCorrectionProvider"/>, so a buffer that suppressed its snap alone would leave
/// the calculator still reporting <see cref="SyncCorrectionDecision.HardSync"/> — which tells the
/// host to stand down for a snap that is no longer coming, and then nothing corrects at all.
/// </para>
/// <para>
/// Not thread-safe: each owner already holds its own lock across the decision this participates
/// in, and a second lock here would only be a second thing to get wrong.
/// </para>
/// </remarks>
internal sealed class HardSyncStallDetector
{
    /// <summary>
    /// A snap that leaves the error at this fraction of where it found it has not moved it.
    /// Half is deliberately generous: a tier that halves the error each time converges in a
    /// handful of steps, so only a snap achieving materially nothing counts against it.
    /// </summary>
    private const double ResidualFraction = 0.5;

    /// <summary>
    /// Consecutive non-converging snaps before the tier stands down. Three keeps the cost of a
    /// genuinely unclosable error to three splices while leaving room for one snap to be
    /// mismeasured by a disturbance that happens to land across it.
    /// </summary>
    private const int StallAfterNonConvergingSnaps = 3;

    private readonly SyncCorrectionOptions _options;

    private long _eligibleAtMicroseconds;
    private double _snapErrorMicroseconds;
    private bool _awaitingOutcome;
    private int _nonConvergingSnaps;

    /// <summary>
    /// Initializes a new instance of the <see cref="HardSyncStallDetector"/> class.
    /// </summary>
    /// <param name="options">
    /// The owner's correction options, read for the band the snap tier occupies.
    /// </param>
    internal HardSyncStallDetector(SyncCorrectionOptions options) => _options = options;

    /// <summary>
    /// Gets a value indicating whether the snap tier has stood down because snapping stopped
    /// closing the error.
    /// </summary>
    internal bool IsStalled { get; private set; }

    /// <summary>
    /// Reports whether the snap tier must stand down for this reading, and scores the outcome of
    /// the previous snap once enough playback has passed to judge it. Call once per correction
    /// decision, immediately before <see cref="SyncCorrectionPolicy.Decide"/>.
    /// </summary>
    /// <param name="smoothedMicroseconds">The smoothed error the tier gates on.</param>
    /// <param name="nowMicroseconds">
    /// Playback time, i.e. output samples converted to microseconds. Wall clock would do just as
    /// well; what matters is that it is the same monotonic quantity the previous snap was timed
    /// against, and that it restarts when its owner does.
    /// </param>
    /// <returns>True when the snap must be suppressed in favour of the continuous tier.</returns>
    internal bool ShouldStandDown(double smoothedMicroseconds, long nowMicroseconds)
    {
        var absError = Math.Abs(smoothedMicroseconds);

        // Out of the snap tier's band in either direction: below the threshold there is nothing
        // to snap, and past the re-anchor ceiling the catastrophic tier owns the error. Either
        // way this tier is no longer the one failing, so it starts clean the next time it
        // applies. This is also the recovery clause — a stall lifts as soon as the error leaves
        // the band, whether the continuous tier closed it or it grew into the tier above.
        if (absError <= _options.HardSyncThresholdMicroseconds
            || absError > _options.ReanchorThresholdMicroseconds)
        {
            Reset();
            return false;
        }

        if (_awaitingOutcome && nowMicroseconds >= _eligibleAtMicroseconds)
        {
            _awaitingOutcome = false;

            if (absError >= Math.Abs(_snapErrorMicroseconds) * ResidualFraction)
            {
                if (++_nonConvergingSnaps >= StallAfterNonConvergingSnaps)
                {
                    IsStalled = true;
                }
            }
            else
            {
                _nonConvergingSnaps = 0;
            }
        }

        return IsStalled || nowMicroseconds < _eligibleAtMicroseconds;
    }

    /// <summary>
    /// Records a snap the tier just asked for. Call only when a snap is actually being performed,
    /// so the cooldown and the convergence score both describe real splices.
    /// </summary>
    /// <param name="snapErrorMicroseconds">The error the snap was sized against.</param>
    /// <param name="nowMicroseconds">Playback time, on the same clock as
    /// <see cref="ShouldStandDown"/>.</param>
    internal void RecordSnap(double snapErrorMicroseconds, long nowMicroseconds)
    {
        _snapErrorMicroseconds = snapErrorMicroseconds;
        _awaitingOutcome = true;

        // The snap's own duration, which is both the rate limit the tier was missing and the
        // earliest honest moment to judge it. An insert spends exactly this long draining, so the
        // cooldown expires on the callback that finishes it — the first reading that reflects
        // what it achieved, because the buffer re-seeds the EMA from the post-snap raw error. A
        // skip lands in one callback and is simply held off for as long as it was large.
        _eligibleAtMicroseconds = nowMicroseconds + (long)Math.Abs(snapErrorMicroseconds);
    }

    /// <summary>
    /// Returns the tier to its unstalled, uncooled state. Belongs wherever the owner abandons its
    /// timing state — a clear, a sync-tracking reset, a reconnect — because the clock
    /// <see cref="ShouldStandDown"/> is timed against restarts there too.
    /// </summary>
    internal void Reset()
    {
        IsStalled = false;
        _awaitingOutcome = false;
        _nonConvergingSnaps = 0;
        _eligibleAtMicroseconds = 0;
        _snapErrorMicroseconds = 0;
    }
}
