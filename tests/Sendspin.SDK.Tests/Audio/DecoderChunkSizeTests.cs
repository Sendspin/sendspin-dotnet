using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using Sendspin.SDK.Audio.Codecs;
using Sendspin.SDK.Models;

namespace Sendspin.SDK.Tests.Audio;

/// <summary>
/// Decode-buffer sizing against the spec's chunk ceiling (#231 for PCM, #234 for FLAC).
/// </summary>
/// <remarks>
/// <para>
/// roles/player/v1.md: "A server MUST NOT send an audio chunk longer than 150 ms" — so a chunk
/// of up to 150 ms is legal input every decoder has to hand back whole. Both decoders were
/// sized well under that (PCM for 50 ms, FLAC for a fixed 8192 frames) and both dropped the
/// overflow without saying so, which is indistinguishable from a stream that really was short.
/// </para>
/// <para>
/// The FLAC chunks here are built by <see cref="BuildFlacChunk"/> rather than read from a
/// fixture: the sizes that matter (150 ms at 96 kHz and 192 kHz) are derived from the sample
/// rate, so generating them keeps the arithmetic under test visible in the test.
/// </para>
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

    [Theory]
    [InlineData(96000, 14400)]
    [InlineData(192000, 28800)]
    public void Flac_MaxSamplesPerFrame_HoldsAFullLengthChunkAtHiRes(int sampleRate, int expectedPerChannel)
    {
        // The defect (#234): "const int maxBlockSize = 8192" regardless of rate, so 150 ms of
        // 96 kHz (14400 frames) or 192 kHz (28800 frames) could not fit whatever the server did.
        using var decoder = new FlacDecoder(FlacFormat(sampleRate, 2));

        Assert.Equal(expectedPerChannel * 2, decoder.MaxSamplesPerFrame);
    }

    [Fact]
    public void Flac_At48kHz_KeepsTheHistoricalBlockFloor()
    {
        // The positive control for the sizing change: 150 ms at 48 kHz is 7200 frames, less
        // than the 8192 the decoder has always advertised in its synthetic STREAMINFO. Shrinking
        // to 7200 would make SimpleFlac reject the 8192-sample blocks that decode today
        // ("Frame sample count exceeds maximum"), turning a working stream into silence, so the
        // new sizing must be a floor and never a reduction.
        using var decoder = new FlacDecoder(FlacFormat(48000, 2));

        Assert.Equal(8192 * 2, decoder.MaxSamplesPerFrame);
    }

    [Theory]
    [InlineData(96000, 14400)]
    [InlineData(192000, 28800)]
    public void Flac_DecodesAFullLengthChunkWithoutLoss(int sampleRate, int framesPerChannel)
    {
        // A 150 ms chunk carrying several complete FLAC frames (v1.md:43) at a hi-res rate.
        // Before the fix the loop stopped once 8192 frames were written and returned the
        // truncated count with no diagnostic.
        var blockSize = framesPerChannel / 8;
        using var decoder = new FlacDecoder(FlacFormat(sampleRate, 2));

        var (chunk, expected) = BuildFlacChunk(channels: 2, blockSize, frameCount: 8);
        var decoded = new float[decoder.MaxSamplesPerFrame];

        var written = decoder.Decode(chunk, decoded);

        Assert.Equal(framesPerChannel * 2, written);
        Assert.Equal(ToFloats(expected), decoded.AsSpan(0, written).ToArray());
    }

    [Fact]
    public void Flac_ChunkLongerThanTheSpecAllows_LogsAnError()
    {
        // 200 ms at 96 kHz: more than the buffer holds even after the fix. The frames that do
        // not fit are still dropped — FLAC frames are indivisible — but the drop is reported
        // rather than being an unexplained gap in the stream.
        var logger = new CapturingLogger<FlacDecoder>();
        using var decoder = new FlacDecoder(FlacFormat(96000, 2), logger);

        var (chunk, _) = BuildFlacChunk(channels: 2, blockSize: 3200, frameCount: 6);
        var decoded = new float[decoder.MaxSamplesPerFrame];

        var written = decoder.Decode(chunk, decoded);

        Assert.True(written < 6 * 3200 * 2, "the over-long chunk should not fit");
        Assert.Single(logger.MessagesAt(LogLevel.Error));
    }

    [Fact]
    public void Flac_ChunkWithinTheSpecLimit_DecodesWholeAndSaysNothing()
    {
        // The positive control: a chunk that fits must not report a drop.
        var logger = new CapturingLogger<FlacDecoder>();
        using var decoder = new FlacDecoder(FlacFormat(96000, 2), logger);

        var (chunk, expected) = BuildFlacChunk(channels: 2, blockSize: 4096, frameCount: 2);
        var decoded = new float[decoder.MaxSamplesPerFrame];

        var written = decoder.Decode(chunk, decoded);

        Assert.Equal(expected.Length, written);
        Assert.Empty(logger.MessagesAt(LogLevel.Error));
    }

    private static AudioFormat PcmFormat(int sampleRate, int channels) =>
        new AudioFormat { Codec = AudioCodecs.Pcm, SampleRate = sampleRate, Channels = channels, BitDepth = 16 };

    private static AudioFormat FlacFormat(int sampleRate, int channels) =>
        new AudioFormat { Codec = AudioCodecs.Flac, SampleRate = sampleRate, Channels = channels, BitDepth = 16 };

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

    /// <summary>
    /// Builds a FLAC chunk of <paramref name="frameCount"/> complete frames, each carrying
    /// <paramref name="blockSize"/> verbatim-coded 16-bit samples per channel, and the
    /// interleaved samples it encodes.
    /// </summary>
    /// <remarks>
    /// Verbatim subframes keep the writer to the frame framing itself — no prediction, no Rice
    /// coding — which is all these tests need. The two CRCs are written as zero because the
    /// vendored SimpleFlac decoder skips both without validating them.
    /// </remarks>
    private static (byte[] Chunk, short[] Interleaved) BuildFlacChunk(int channels, int blockSize, int frameCount)
    {
        var interleaved = BuildPcmSamples(blockSize * frameCount * channels);
        var writer = new BitWriter();

        for (int frame = 0; frame < frameCount; frame++)
        {
            var frameStart = frame * blockSize * channels;

            writer.Write(0x3FFE, 14);       // Frame sync code
            writer.Write(0, 1);             // Reserved
            writer.Write(0, 1);             // Blocking strategy: fixed block size
            writer.Write(7, 4);             // Block size: 16-bit value follows the header
            writer.Write(0, 4);             // Sample rate: take from STREAMINFO
            writer.Write(channels - 1, 4);  // Independent channels
            writer.Write(0, 3);             // Bit depth: take from STREAMINFO
            writer.Write(0, 1);             // Reserved
            writer.Write(frame, 8);         // Coded frame number (single byte below 128)
            writer.Write(blockSize - 1, 16);
            writer.Write(0, 8);             // Header CRC-8

            for (int ch = 0; ch < channels; ch++)
            {
                writer.Write(0, 1);         // Subframe padding
                writer.Write(1, 6);         // Subframe type: verbatim
                writer.Write(0, 1);         // No wasted bits

                for (int i = 0; i < blockSize; i++)
                {
                    writer.Write(interleaved[frameStart + (i * channels) + ch], 16);
                }
            }

            writer.Write(0, 16);            // Frame CRC-16
        }

        return (writer.ToArray(), interleaved);
    }

    /// <summary>Big-endian bit writer, the order a FLAC bitstream is written in.</summary>
    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = new List<byte>();
        private int _partial;
        private int _partialBits;

        public void Write(long value, int bitCount)
        {
            for (int i = bitCount - 1; i >= 0; i--)
            {
                _partial = (_partial << 1) | (int)((value >> i) & 1);
                _partialBits++;

                if (_partialBits == 8)
                {
                    _bytes.Add((byte)_partial);
                    _partial = 0;
                    _partialBits = 0;
                }
            }
        }

        public byte[] ToArray()
        {
            Assert.Equal(0, _partialBits); // FLAC frames are byte aligned by construction
            return _bytes.ToArray();
        }
    }
}
