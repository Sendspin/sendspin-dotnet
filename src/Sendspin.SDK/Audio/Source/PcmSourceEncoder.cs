using Sendspin.SDK.Models;

namespace Sendspin.SDK.Audio.Source;

/// <summary>
/// Passthrough PCM "encoder": capture PCM is already the wire format (little-endian
/// signed integers per the spec's PCM convention), so each buffer streams as-is. Always
/// available — the spec guarantees servers accept PCM from sources.
/// </summary>
public sealed class PcmSourceEncoder : ISourceAudioEncoder
{
    /// <inheritdoc/>
    public string Codec => "pcm";

    /// <inheritdoc/>
    public string? CodecHeader => null;

    /// <inheritdoc/>
    public byte[] Encode(ReadOnlySpan<byte> pcm) => pcm.ToArray();

    /// <inheritdoc/>
    public void Dispose()
    {
    }
}

/// <summary>Default source-encoder factory. Supports PCM; extend for Opus/FLAC.</summary>
public sealed class DefaultSourceAudioEncoderFactory : ISourceAudioEncoderFactory
{
    /// <inheritdoc/>
    public ISourceAudioEncoder Create(string codec, AudioFormat format) => codec switch
    {
        "pcm" => new PcmSourceEncoder(),
        _ => throw new NotSupportedException(
            $"Source codec '{codec}' is not supported by the default factory; supply a custom ISourceAudioEncoderFactory."),
    };
}
