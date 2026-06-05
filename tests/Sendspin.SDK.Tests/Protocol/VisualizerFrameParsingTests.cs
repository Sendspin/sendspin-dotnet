using System.Buffers.Binary;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Protocol;

/// <summary>
/// Binary decode coverage for the six visualizer feature frames (types 16-21), mirroring the
/// aiosendspin v1 wire. Each test builds [type][BE int64 ts][data] and asserts the parsed frame;
/// malformed lengths must return null (dropped, not thrown).
/// </summary>
public class VisualizerFrameParsingTests
{
    private static byte[] Frame(byte type, long ts, byte[] data)
    {
        var buf = new byte[9 + data.Length];
        buf[0] = type;
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(1, 8), ts);
        data.CopyTo(buf, 9);
        return buf;
    }

    private static byte[] U16(ushort v)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, v);
        return b;
    }

    [Fact]
    public void Loudness_Parses()
    {
        var frame = BinaryMessageParser.ParseVisualizerFrame(Frame(BinaryMessageTypes.VisualizerLoudness, 1_234_000, U16(12345)), null);
        Assert.NotNull(frame);
        Assert.Equal(1_234_000, frame.Timestamp);
        Assert.Equal(12345, frame.Loudness);
    }

    [Fact]
    public void FPeak_Parses()
    {
        var data = U16(1024).Concat(U16(0x4000)).ToArray();
        var frame = BinaryMessageParser.ParseVisualizerFrame(Frame(BinaryMessageTypes.VisualizerFPeak, 100, data), null);
        Assert.NotNull(frame);
        Assert.Equal(1024, frame.FPeakFrequency);
        Assert.Equal(0x4000, frame.FPeakAmplitude);
    }

    [Fact]
    public void Spectrum_Parses_WhenBinCountMatches()
    {
        var data = Enumerable.Range(0, 8).SelectMany(i => U16((ushort)i)).ToArray();
        var frame = BinaryMessageParser.ParseVisualizerFrame(Frame(BinaryMessageTypes.VisualizerSpectrum, 42, data), spectrumBinCount: 8);
        Assert.NotNull(frame);
        Assert.Equal(Enumerable.Range(0, 8), frame.Spectrum!);
    }

    [Fact]
    public void Spectrum_RejectsWrongBinCount()
    {
        var data = Enumerable.Range(0, 4).SelectMany(i => U16((ushort)i)).ToArray();
        var frame = BinaryMessageParser.ParseVisualizerFrame(Frame(BinaryMessageTypes.VisualizerSpectrum, 0, data), spectrumBinCount: 8);
        Assert.Null(frame);
    }

    [Fact]
    public void Peak_Parses()
    {
        var frame = BinaryMessageParser.ParseVisualizerFrame(Frame(BinaryMessageTypes.VisualizerPeak, 99, new byte[] { 0xC8 }), null);
        Assert.NotNull(frame);
        Assert.Equal(0xC8, frame.PeakStrength);
    }

    [Fact]
    public void Pitch_ParsesQ88AndConfidence()
    {
        // A4 = MIDI 69 -> 0x4500 in Q8.8. Confidence 200.
        var data = U16(0x4500).Concat(new byte[] { 200 }).ToArray();
        var frame = BinaryMessageParser.ParseVisualizerFrame(Frame(BinaryMessageTypes.VisualizerPitch, 1, data), null);
        Assert.NotNull(frame);
        Assert.Equal(0x4500, frame.PitchMidiQ88);
        Assert.Equal(200, frame.PitchConfidence);
        Assert.Equal(69.0, frame.PitchMidi);
    }

    [Theory]
    [InlineData(true, 0b0000_0001)]
    [InlineData(false, 0b0000_0000)]
    public void Beat_ParsesDownbeatFlag(bool expectedDownbeat, byte flags)
    {
        var frame = BinaryMessageParser.ParseVisualizerFrame(Frame(BinaryMessageTypes.VisualizerBeat, 100, new[] { flags }), null);
        Assert.NotNull(frame);
        Assert.Equal(expectedDownbeat, frame.IsDownbeat);
    }

    [Theory]
    [InlineData(BinaryMessageTypes.VisualizerLoudness, 1)]  // needs 2
    [InlineData(BinaryMessageTypes.VisualizerFPeak, 2)]     // needs 4
    [InlineData(BinaryMessageTypes.VisualizerPitch, 2)]     // needs 3
    [InlineData(BinaryMessageTypes.VisualizerBeat, 0)]      // needs 1
    public void Malformed_WrongLength_ReturnsNull(byte type, int dataLen)
    {
        var frame = BinaryMessageParser.ParseVisualizerFrame(Frame(type, 1, new byte[dataLen]), spectrumBinCount: 8);
        Assert.Null(frame);
    }

    [Fact]
    public void TruncatedHeader_ReturnsNull()
    {
        Assert.Null(BinaryMessageParser.ParseVisualizerFrame(new byte[] { 16, 0, 1 }, null));
    }
}
