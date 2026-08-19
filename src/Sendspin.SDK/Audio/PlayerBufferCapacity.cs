// <copyright file="PlayerBufferCapacity.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Sendspin.SDK.Models;

namespace Sendspin.SDK.Audio;

/// <summary>
/// Converts between the decoded-audio buffer a player actually holds and the compressed-byte
/// <c>buffer_capacity</c> it advertises in <c>client/hello</c>.
/// </summary>
/// <remarks>
/// <para>
/// The spec makes the advertised figure a hard per-player byte limit that servers fill toward
/// (roles/player/v1.md:34-35). It is therefore a promise, not a hint: whatever is advertised,
/// the server may legally send, and anything beyond what the buffer holds is discarded before
/// it is ever played. The C++ reference derives its advertisement from its real ring size
/// (player_role.cpp:199-206, advertising four fifths of it); this does the same across the
/// decode step, which is the only extra complication in a PCM-buffered client.
/// </para>
/// <para>
/// The conversion is per codec, and the binding one is whichever advertised codec packs the
/// most audio into a byte — a megabyte of Opus is minutes, a megabyte of PCM is seconds. The
/// advertisement therefore uses the <em>minimum</em> byte rate across the advertised formats,
/// so the promise holds whichever format the server picks.
/// </para>
/// </remarks>
public static class PlayerBufferCapacity
{
    /// <summary>
    /// Decoded-audio buffer duration, in milliseconds, that the SDK defaults to. Used both by
    /// <see cref="TimedAudioBuffer"/>'s constructor and by the default advertisement, so the
    /// two agree unless a caller deliberately changes one.
    /// </summary>
    /// <remarks>
    /// 30 s is long enough to absorb a server's opening burst on a buffered stream without
    /// committing a player to a very large allocation (about 11 MB of float PCM at 48 kHz
    /// stereo). Raise it — on both sides — for clients that want the server to run further
    /// ahead.
    /// </remarks>
    public const int DefaultDecodedBufferMilliseconds = 30_000;

    /// <summary>
    /// Fraction of the real capacity that is advertised, as a denominator: the advertisement is
    /// <c>(N-1)/N</c> of what the buffer holds. Matches the C++ reference's
    /// <c>AUDIO_BUFFER_ADVERTISE_DENOMINATOR</c>, and leaves headroom for the burst that is
    /// already in flight when the server reaches the limit.
    /// </summary>
    public const int AdvertisedFractionDenominator = 5;

    /// <summary>
    /// Assumed worst-case FLAC compression, as a fraction of the equivalent PCM byte rate.
    /// </summary>
    /// <remarks>
    /// FLAC is lossless, so its byte rate is bounded above by PCM but has no useful lower
    /// bound — near-silent passages compress far past this. Real music sits at 50-70%, and
    /// combined with <see cref="AdvertisedFractionDenominator"/> this keeps the advertisement
    /// conservative without collapsing it to something unusable. If a deployment streams
    /// material that compresses much harder than music, advertise explicitly.
    /// </remarks>
    private const double FlacCompressionFloor = 0.5;

    /// <summary>
    /// Compressed bytes per second the given format is expected to occupy on the wire.
    /// </summary>
    /// <param name="format">Advertised audio format.</param>
    /// <returns>Bytes per second.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="format"/> is null.</exception>
    public static int CompressedBytesPerSecond(AudioFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);

        var pcmBytesPerSecond =
            (long)format.SampleRate * format.Channels * (Math.Max(format.BitDepth ?? 16, 16) / 8);

        return format.Codec?.ToLowerInvariant() switch
        {
            // The bitrate we ask for is the rate the server encodes at.
            "opus" when format.Bitrate > 0 => format.Bitrate.Value * 1000 / 8,
            "flac" => (int)(pcmBytesPerSecond * FlacCompressionFloor),
            _ => (int)pcmBytesPerSecond,
        };
    }

    /// <summary>
    /// The <c>buffer_capacity</c> to advertise for a given decoded-buffer duration.
    /// </summary>
    /// <param name="decodedBufferMilliseconds">Decoded audio the player can hold.</param>
    /// <param name="formats">Formats being advertised in the same <c>client/hello</c>.</param>
    /// <returns>
    /// Compressed bytes the server may legally have queued, guaranteed to decode to no more
    /// than <paramref name="decodedBufferMilliseconds"/> for any of <paramref name="formats"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="formats"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="decodedBufferMilliseconds"/> is not positive.
    /// </exception>
    public static int AdvertisedBytes(int decodedBufferMilliseconds, IEnumerable<AudioFormat> formats)
    {
        ArgumentNullException.ThrowIfNull(formats);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(decodedBufferMilliseconds);

        var bytesPerSecond = int.MaxValue;
        foreach (var format in formats)
        {
            bytesPerSecond = Math.Min(bytesPerSecond, CompressedBytesPerSecond(format));
        }

        if (bytesPerSecond == int.MaxValue)
        {
            // No formats advertised: the player role is not usable, so nothing can be sent to it.
            return 0;
        }

        var holdable = (long)decodedBufferMilliseconds * bytesPerSecond / 1000;
        var advertised = holdable * (AdvertisedFractionDenominator - 1) / AdvertisedFractionDenominator;

        return (int)Math.Min(advertised, int.MaxValue);
    }

    /// <summary>
    /// Decoded audio, in milliseconds, that <paramref name="advertisedBytes"/> of
    /// <paramref name="format"/> decodes to — the inverse of <see cref="AdvertisedBytes"/>,
    /// for checking that an advertisement a client made is one it can honour.
    /// </summary>
    /// <param name="advertisedBytes">Advertised <c>buffer_capacity</c>.</param>
    /// <param name="format">The negotiated format.</param>
    /// <returns>Milliseconds of decoded audio.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="format"/> is null.</exception>
    public static double HoldableMilliseconds(long advertisedBytes, AudioFormat format)
    {
        var bytesPerSecond = CompressedBytesPerSecond(format);
        return bytesPerSecond > 0 ? advertisedBytes * 1000.0 / bytesPerSecond : 0;
    }
}
