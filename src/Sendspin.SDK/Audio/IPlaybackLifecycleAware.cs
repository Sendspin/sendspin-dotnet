// <copyright file="IPlaybackLifecycleAware.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

namespace Sendspin.SDK.Audio;

/// <summary>
/// Implemented by an <see cref="IAudioSampleSource"/> that carries correction state of its own,
/// so <see cref="IAudioPipeline"/> can reach it with the two events that invalidate that state:
/// a stream discontinuity and a reconnect.
/// </summary>
/// <remarks>
/// <para>
/// Optional by design. <see cref="IAudioSampleSource"/> is a pull loop and nothing more — a source
/// that only reads the buffer has no state to invalidate, and must not be forced to implement
/// empty methods. The pipeline forwards to whichever source implements this and leaves the rest
/// alone.
/// </para>
/// <para>
/// A source that resamples or steps frames (<see cref="SyncCorrectedSampleSource"/> is the one the
/// SDK ships) needs both. Without <see cref="Reset"/>, a <c>stream/clear</c> leaves a primed
/// resampler holding audio from before the seek and splices it into the new position; without
/// <see cref="NotifyReconnect"/>, the source keeps correcting against an error the re-converging
/// clock has not finished re-measuring.
/// </para>
/// </remarks>
public interface IPlaybackLifecycleAware
{
    /// <summary>
    /// Clears correction state after a buffer clear or a playback restart, so a stale rate, a
    /// half-finished drop/insert interval, or buffered resampler input cannot leak into the audio
    /// that follows.
    /// </summary>
    void Reset();

    /// <summary>
    /// Notifies the source that a reconnect occurred, so it suppresses corrections while the clock
    /// synchronizer re-converges (see
    /// <see cref="SyncCorrectionOptions.ReconnectStabilizationMicroseconds"/>).
    /// </summary>
    void NotifyReconnect();
}
