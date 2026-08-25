// <copyright file="SpliceBlend.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

namespace Sendspin.SDK.Audio;

/// <summary>
/// The one interpolated splice, shared by <see cref="TimedAudioBuffer"/>'s internal corrector and
/// <see cref="SyncCorrectedSampleSource"/>'s frame stepping.
/// </summary>
/// <remarks>
/// <para>
/// A dropped frame is not simply discarded and an inserted one is not simply duplicated: both emit
/// a 3-point weighted blend, which keeps the waveform's slope continuous across the splice. A raw
/// cut or repeat puts a step in the signal, and a step is a click.
/// </para>
/// <para>
/// The weights (0.25 / 0.5 / 0.25) and the degradations as the input runs out are the whole
/// kernel, and they lived in two hand-written copies that had no way of staying in step. The two
/// callers still differ in <em>which</em> frames they hand over — dropping consumes them, inserting
/// peeks at them — but what happens to the samples is decided here.
/// </para>
/// </remarks>
internal static class SpliceBlend
{
    /// <summary>
    /// Writes one spliced frame, degrading gracefully as neighbouring content runs out.
    /// </summary>
    /// <param name="previous">
    /// The frame last emitted, as the continuity term. Must be at least as long as
    /// <paramref name="destination"/>.
    /// </param>
    /// <param name="primary">
    /// The frame at the splice point, or empty when none is available. Empty degrades to holding
    /// <paramref name="previous"/>, which is the only thing left to do.
    /// </param>
    /// <param name="neighbour">
    /// The frame after it, or empty when none is available. Empty degrades to a 2-point blend.
    /// </param>
    /// <param name="destination">One frame of output; its length sets the channel count.</param>
    internal static void Blend(
        ReadOnlySpan<float> previous,
        ReadOnlySpan<float> primary,
        ReadOnlySpan<float> neighbour,
        Span<float> destination)
    {
        if (!primary.IsEmpty && !neighbour.IsEmpty)
        {
            // 3-point weighted interpolation: 0.25 continuity from the previous frame, 0.5 the
            // frame at the splice, 0.25 the one after. Smoother than a 2-point average, which
            // still leaves a slope discontinuity.
            for (var i = 0; i < destination.Length; i++)
            {
                destination[i] = (0.25f * previous[i])
                    + (0.5f * primary[i])
                    + (0.25f * neighbour[i]);
            }

            return;
        }

        if (!primary.IsEmpty)
        {
            for (var i = 0; i < destination.Length; i++)
            {
                destination[i] = (previous[i] + primary[i]) * 0.5f;
            }

            return;
        }

        previous[..destination.Length].CopyTo(destination);
    }
}
