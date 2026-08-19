using System.Text.Json.Serialization;

namespace Sendspin.SDK.Models;

/// <summary>
/// Represents an audio format specification.
/// </summary>
public sealed class AudioFormat
{
    /// <summary>
    /// Audio codec (e.g., "opus", "flac", "pcm").
    /// </summary>
    [JsonPropertyName("codec")]
    public string Codec { get; set; } = "opus";

    /// <summary>
    /// Sample rate in Hz (e.g., 44100, 48000).
    /// </summary>
    [JsonPropertyName("sample_rate")]
    public int SampleRate { get; set; } = 48000;

    /// <summary>
    /// Number of audio channels (1 = mono, 2 = stereo).
    /// </summary>
    [JsonPropertyName("channels")]
    public int Channels { get; set; } = 2;

    /// <summary>
    /// Bits per sample (for PCM: 16, 24, 32).
    /// </summary>
    [JsonPropertyName("bit_depth")]
    public int? BitDepth { get; set; }

    /// <summary>
    /// Bitrate in kbps (for lossy codecs like Opus).
    /// </summary>
    [JsonPropertyName("bitrate")]
    public int? Bitrate { get; set; }

    /// <summary>
    /// Codec-specific header data (base64 encoded).
    /// For FLAC, this contains the STREAMINFO block.
    /// </summary>
    [JsonPropertyName("codec_header")]
    public string? CodecHeader { get; set; }

    /// <summary>
    /// Determines whether <paramref name="other"/> announces the same stream configuration as this
    /// format — whether audio described by it can be decoded and played by the components already
    /// built for this one.
    /// </summary>
    /// <param name="other">The newly announced format.</param>
    /// <returns>True when nothing a decoder or an audio output is configured from has changed.</returns>
    /// <remarks>
    /// <para>
    /// Compares every field the decoders read: <see cref="Codec"/> (case-insensitively, matching
    /// how the decoder factory selects on it), <see cref="SampleRate"/>, <see cref="Channels"/>,
    /// <see cref="BitDepth"/> and <see cref="CodecHeader"/>. The header is compared as the Base64
    /// text the server sent: the FLAC decoder prepends its decoded bytes to every frame and
    /// calibrates its sample scaling from the bit depth inside it, so a changed header is a
    /// changed decoder. Two headers that differ as text but decode to the same bytes therefore
    /// compare as different, which errs toward rebuilding rather than toward decoding with a
    /// stale header.
    /// </para>
    /// <para>
    /// <see cref="Bitrate"/> is deliberately excluded: it describes the server's encoder, is not
    /// part of the spec's <c>stream/start</c> player object, and no decoder or output reads it.
    /// </para>
    /// </remarks>
    internal bool IsSameStreamConfiguration(AudioFormat other) =>
        string.Equals(Codec, other.Codec, StringComparison.OrdinalIgnoreCase)
        && SampleRate == other.SampleRate
        && Channels == other.Channels
        && BitDepth == other.BitDepth
        && string.Equals(CodecHeader, other.CodecHeader, StringComparison.Ordinal);

    public override string ToString()
    {
        var bitInfo = Bitrate.HasValue ? $" @ {Bitrate}kbps" : BitDepth.HasValue ? $" {BitDepth}bit" : "";
        return $"{Codec.ToUpperInvariant()} {SampleRate}Hz {Channels}ch{bitInfo}";
    }
}

/// <summary>
/// Common audio codec identifiers used in the Sendspin protocol.
/// </summary>
public static class AudioCodecs
{
    public const string Opus = "opus";
    public const string Flac = "flac";
    public const string Pcm = "pcm";
}
