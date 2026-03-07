using Sendspin.SDK.Diagnostics;

namespace Sendspin.SDK.Tests.Diagnostics;

public class DiagnosticAudioRingBufferTests
{
    [Fact]
    public void Constructor_ValidParameters_CreatesBuffer()
    {
        var buffer = new DiagnosticAudioRingBuffer(48000, 2, 5);

        Assert.Equal(48000, buffer.SampleRate);
        Assert.Equal(2, buffer.Channels);
        Assert.True(buffer.Capacity >= 48000 * 2 * 5);
    }

    [Fact]
    public void Constructor_OverflowingParameters_Throws()
    {
        // 192000 * 2 * 6000 = 2,304,000,000 — exceeds int.MaxValue
        Assert.Throws<OverflowException>(
            () => new DiagnosticAudioRingBuffer(192000, 2, 6000));
    }

    [Theory]
    [InlineData(0, 2, 5)]
    [InlineData(48000, 0, 5)]
    [InlineData(48000, 2, 0)]
    [InlineData(-1, 2, 5)]
    public void Constructor_InvalidParameters_Throws(int sampleRate, int channels, int duration)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DiagnosticAudioRingBuffer(sampleRate, channels, duration));
    }
}
