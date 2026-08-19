using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using Sendspin.SDK.Audio.Codecs;
using Sendspin.SDK.Models;

namespace Sendspin.SDK.Tests.Audio;

/// <summary>
/// Decode-buffer sizing against the spec's chunk ceiling (#231 for PCM).
/// </summary>
/// <remarks>
/// roles/player/v1.md: "A server MUST NOT send an audio chunk longer than 150 ms" — so a chunk
/// of up to 150 ms is legal input every decoder has to hand back whole. The PCM decoder was
/// sized for 50 ms and dropped the overflow without saying so, which is indistinguishable from
/// a stream that really was short.
/// </remarks>
public class DecoderChunkSizeTests
{
    /// <summary>The spec ceiling the decoders must accommodate.</summary>
    private const int MaxChunkMs = 150;

    [Fact]
    public void Pcm_MaxSamplesPerFrame_HoldsAFullLengthChunk()
    {
        // The defect (#231): sizing was "(SampleRate / 20) * Channels", i.e. 50 ms, so at
        // 48 kHz stereo the pipeline allocated 4800 samples for a chunk that may legally carry
        // 14400. AudioPipeline sizes its decode buffer from this property alone, so the
        // property is where the loss starts.
        using var decoder = new PcmDecoder(PcmFormat(48000, 2));

        Assert.Equal(48000 * MaxChunkMs / 1000 * 2, decoder.MaxSamplesPerFrame);
    }

    [Fact]
    public void Pcm_DecodesAFullLengthChunkWithoutLoss()
    {
        // 150 ms at 48 kHz stereo: the largest chunk a conformant server may send.
        var format = PcmFormat(48000, 2);
        using var decoder = new PcmDecoder(format);

        var samples = BuildPcmSamples(48000 * MaxChunkMs / 1000 * 2);
        var chunk = ToPcm16(samples);
        var decoded = new float[decoder.MaxSamplesPerFrame];

        var written = decoder.Decode(chunk, decoded);

        Assert.Equal(samples.Length, written);
        Assert.Equal(ToFloats(samples), decoded.AsSpan(0, written).ToArray());
    }

    [Fact]
    public void Pcm_ChunkLongerThanTheSpecAllows_DecodesThePrefixAndLogsAnError()
    {
        // A non-conformant server (300 ms here) still must not lose samples silently: the
        // pipeline discards the whole chunk on an exception, so the legal prefix is decoded
        // and the loss is reported. Without this test a fix that only enlarged the buffer
        // would leave the silent clip in place one size further out.
        var logger = new CapturingLogger<PcmDecoder>();
        var format = PcmFormat(48000, 2);
        using var decoder = new PcmDecoder(format, logger);

        var samples = BuildPcmSamples(48000 * 300 / 1000 * 2);
        var decoded = new float[decoder.MaxSamplesPerFrame];

        var written = decoder.Decode(ToPcm16(samples), decoded);

        Assert.Equal(decoder.MaxSamplesPerFrame, written);
        var error = Assert.Single(logger.MessagesAt(LogLevel.Error));
        Assert.Contains("28800", error, StringComparison.Ordinal);  // samples the chunk carried
        Assert.Contains("14400", error, StringComparison.Ordinal);  // samples that fit
    }

    [Fact]
    public void Pcm_ChunkWithinTheSpecLimit_DecodesWholeAndSaysNothing()
    {
        // The positive control for the test above: an ordinary 20 ms chunk must decode intact
        // with no diagnostic at all. A "fix" that logged on every chunk, or that reported a
        // short chunk as truncated, would pass the loud-failure test and fail here.
        var logger = new CapturingLogger<PcmDecoder>();
        using var decoder = new PcmDecoder(PcmFormat(48000, 2), logger);

        var samples = BuildPcmSamples(48000 * 20 / 1000 * 2);
        var decoded = new float[decoder.MaxSamplesPerFrame];

        var written = decoder.Decode(ToPcm16(samples), decoded);

        Assert.Equal(samples.Length, written);
        Assert.Empty(logger.MessagesAt(LogLevel.Error));
        Assert.Empty(logger.MessagesAt(LogLevel.Warning));
    }

    private static AudioFormat PcmFormat(int sampleRate, int channels) =>
        new AudioFormat { Codec = AudioCodecs.Pcm, SampleRate = sampleRate, Channels = channels, BitDepth = 16 };

    /// <summary>A deterministic, non-repeating 16-bit pattern, so a lost tail is visible.</summary>
    private static short[] BuildPcmSamples(int count)
    {
        var samples = new short[count];
        for (int i = 0; i < count; i++)
        {
            samples[i] = (short)(((i * 7919) % 60000) - 30000);
        }

        return samples;
    }

    private static byte[] ToPcm16(short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), samples[i]);
        }

        return bytes;
    }

    private static float[] ToFloats(short[] samples)
    {
        var floats = new float[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            floats[i] = samples[i] / 32768f;
        }

        return floats;
    }
}
