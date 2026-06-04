using Sendspin.SDK.Audio;

namespace Sendspin.SDK.Tests.Audio;

/// <summary>
/// Verifies the perceptual volume curve: per the Sendspin spec, volume (0-100) is perceived
/// loudness, mapped to linear amplitude via <c>(volume/100)^1.5</c>, not a linear ratio.
/// </summary>
public class VolumeCurveTests
{
    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(100, 1.0)]
    [InlineData(50, 0.354)]     // 0.5^1.5 = 0.35355...
    [InlineData(25, 0.125)]     // 0.25^1.5 = exactly 0.125
    public void PerceivedVolume_MapsViaPerceptualCurve(int volume, double expected)
    {
        Assert.Equal(expected, AudioPipeline.PerceivedVolumeToAmplitude(volume), precision: 3);
    }

    [Fact]
    public void PerceivedVolume_IsNotLinear()
    {
        // The whole point: volume 50 must NOT be amplitude 0.5 (that would be the old linear bug).
        Assert.NotEqual(0.5f, AudioPipeline.PerceivedVolumeToAmplitude(50), tolerance: 0.01f);
    }

    [Theory]
    [InlineData(150, 1.0)]   // clamps above 100
    [InlineData(-10, 0.0)]   // clamps below 0
    public void PerceivedVolume_ClampsOutOfRange(int volume, double expected)
    {
        Assert.Equal(expected, AudioPipeline.PerceivedVolumeToAmplitude(volume), precision: 4);
    }
}
