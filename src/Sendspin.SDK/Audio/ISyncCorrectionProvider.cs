// <copyright file="ISyncCorrectionProvider.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

namespace Sendspin.SDK.Audio;

/// <summary>
/// Provides sync correction decisions based on sync error from <see cref="ITimedAudioBuffer"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the advanced seam, not the default.</b> It belongs to players that drive
/// <see cref="ITimedAudioBuffer.ReadRaw"/> because the platform owns a smooth-correction
/// mechanism the buffer cannot drive from the inside — hardware rate adjust, device-clock
/// steering, or a resampler already in the output chain. A player without one should call
/// <see cref="ITimedAudioBuffer.Read"/>, which applies the same spec-fixed ladder itself.
/// </para>
/// <para>
/// The interface abstracts the correction <em>policy</em>, not the mechanism: the thresholds and
/// the ±0.5% cap are spec constants, and how the correction is realized belongs to the caller,
/// which is the only side that knows what it can apply. The SDK provides
/// <see cref="SyncCorrectionCalculator"/> as a default implementation that mirrors the CLI's
/// tiered correction approach.
/// </para>
/// <para>
/// <b>The decision is always a playback rate.</b> That is the single currency: a speed change is
/// the whole correction, and a caller with no resampler realizes it as whole-frame stepping of the
/// same magnitude — one frame in N is a speed change of 1/N — which is what
/// <see cref="SyncCorrectedSampleSource"/> does under
/// <see cref="SyncCorrectionMechanism.FrameStepping"/>. A provider does not choose between the
/// two; it cannot see which one the caller has.
/// </para>
/// <para>
/// Usage pattern:
/// 1. Call <see cref="UpdateFromSyncError"/> with error values from <see cref="ITimedAudioBuffer"/>
/// 2. Read <see cref="TargetPlaybackRate"/> (and <see cref="CurrentMode"/> for diagnostics)
/// 3. Apply that speed externally, by whatever mechanism you have
/// 4. If you realized it by stepping frames, call
///    <see cref="ITimedAudioBuffer.NotifyExternalCorrection"/> so the counts appear in the stats
/// </para>
/// </remarks>
public interface ISyncCorrectionProvider
{
    /// <summary>
    /// Gets the current sync correction tier.
    /// </summary>
    /// <remarks>
    /// Which band the error falls in, not which mechanism to use — the caller owns that. Callers
    /// should treat <see cref="SyncCorrectionMode.Dropping"/> and
    /// <see cref="SyncCorrectionMode.Inserting"/> as "too far out to be worth trimming smoothly",
    /// and read the magnitude from <see cref="TargetPlaybackRate"/> like any other tier.
    /// </remarks>
    SyncCorrectionMode CurrentMode { get; }

    /// <summary>
    /// Gets the target playback rate: the correction, as a speed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Values: 1.0 = normal speed, &gt;1.0 = speed up (behind), &lt;1.0 = slow down (ahead),
    /// always within <see cref="SyncCorrectionOptions.MinRate"/>..<see cref="SyncCorrectionOptions.MaxRate"/>.
    /// </para>
    /// <para>
    /// Meaningful in every continuous tier, not only <see cref="SyncCorrectionMode.Resampling"/>.
    /// Apply it to a resampler, to a hardware rate control, or — with no such mechanism — as one
    /// dropped or inserted frame every <c>1 / |rate - 1|</c> frames.
    /// </para>
    /// </remarks>
    double TargetPlaybackRate { get; }

    /// <summary>
    /// Event raised when correction parameters change.
    /// </summary>
    /// <remarks>
    /// Subscribers can use this to update resamplers or other correction components
    /// without polling. The event provides the provider instance for accessing updated values.
    /// </remarks>
    event Action<ISyncCorrectionProvider>? CorrectionChanged;

    /// <summary>
    /// Updates correction decisions based on current sync error values.
    /// </summary>
    /// <param name="rawMicroseconds">Raw sync error in microseconds from <see cref="ITimedAudioBuffer.SyncErrorMicroseconds"/>.</param>
    /// <param name="smoothedMicroseconds">Smoothed sync error from <see cref="ITimedAudioBuffer.SmoothedSyncErrorMicroseconds"/>.</param>
    /// <remarks>
    /// <para>
    /// Call this method after each read from <see cref="ITimedAudioBuffer.ReadRaw"/>.
    /// The provider uses the smoothed error for correction decisions to avoid jittery behavior.
    /// </para>
    /// <para>
    /// Sign convention (same as <see cref="ITimedAudioBuffer"/>):
    /// - Positive = playing behind (need to speed up/drop frames)
    /// - Negative = playing ahead (need to slow down/insert frames)
    /// </para>
    /// </remarks>
    void UpdateFromSyncError(long rawMicroseconds, double smoothedMicroseconds);

    /// <summary>
    /// Resets the provider to initial state (no correction).
    /// </summary>
    /// <remarks>
    /// Call this when the buffer is cleared or playback restarts to prevent
    /// stale correction decisions from affecting new playback.
    /// </remarks>
    void Reset();

    /// <summary>
    /// Notifies the provider that a WebSocket reconnect occurred.
    /// </summary>
    /// <remarks>
    /// <para>
    /// After a reconnect, the clock synchronizer is reset and needs time to re-converge.
    /// During this stabilization period, sync error measurements are unreliable.
    /// Implementations should suppress corrections until the stabilization period elapses.
    /// </para>
    /// <para>
    /// The stabilization duration is configured via
    /// <see cref="SyncCorrectionOptions.ReconnectStabilizationMicroseconds"/>.
    /// </para>
    /// </remarks>
    void NotifyReconnect() { }
}
