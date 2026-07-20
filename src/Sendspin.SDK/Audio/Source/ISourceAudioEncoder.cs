using Sendspin.SDK.Models;

namespace Sendspin.SDK.Audio.Source;

/// <summary>
/// Encodes captured PCM into a source codec for streaming to the server (the reverse of
/// <see cref="IAudioDecoder"/>). Each <see cref="Encode"/> call returns whole codec
/// units — a unit never spans chunks, per the spec's source codec-framing rule.
/// </summary>
public interface ISourceAudioEncoder : IDisposable
{
    /// <summary>The wire codec name ('pcm', 'opus', 'flac').</summary>
    string Codec { get; }

    /// <summary>
    /// Optional base64 codec header for <c>client_stream/start</c> (e.g. FLAC). Null when
    /// the codec needs none.
    /// </summary>
    string? CodecHeader { get; }

    /// <summary>Encodes one buffer of interleaved 16-bit PCM into codec bytes.</summary>
    byte[] Encode(ReadOnlySpan<byte> pcm);
}

/// <summary>Creates a <see cref="ISourceAudioEncoder"/> for a capture format.</summary>
public interface ISourceAudioEncoderFactory
{
    /// <summary>Creates an encoder for the given codec and capture format.</summary>
    ISourceAudioEncoder Create(string codec, AudioFormat format);
}
