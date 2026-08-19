// <copyright file="AudioChunkLimits.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

namespace Sendspin.SDK.Audio;

/// <summary>
/// The protocol's ceiling on how much audio one binary chunk may carry, and the decode buffer
/// size that follows from it.
/// </summary>
internal static class AudioChunkLimits
{
    /// <summary>
    /// Most audio one binary chunk may carry, per the spec's MUST in roles/player/v1.md:
    /// "A server MUST NOT send an audio chunk longer than 150 ms". A decoder therefore has to
    /// be able to hand back a whole chunk of this length; anything shorter is the common case,
    /// not the limit to size for.
    /// </summary>
    internal const int MaxChunkMilliseconds = 150;

    /// <summary>
    /// Samples per channel in a maximum-length chunk at <paramref name="sampleRate"/>.
    /// </summary>
    /// <param name="sampleRate">Negotiated sample rate in Hz.</param>
    /// <returns>
    /// The per-channel sample count, rounded up so a rate that does not divide evenly
    /// (22050 Hz gives 3307.5) still leaves room for a full 150 ms.
    /// </returns>
    internal static int MaxSamplesPerChannel(int sampleRate) =>
        (int)((((long)sampleRate * MaxChunkMilliseconds) + 999) / 1000);
}
