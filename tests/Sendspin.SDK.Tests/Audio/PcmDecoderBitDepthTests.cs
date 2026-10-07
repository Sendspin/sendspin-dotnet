using Sendspin.SDK.Audio.Codecs;
using Sendspin.SDK.Models;

namespace Sendspin.SDK.Tests.Audio;

/// <summary>
/// A PCM bit depth the decoder cannot read is refused at construction (#347). It used to be
/// accepted and fail per chunk: a depth under 8 as a division by zero, 8/12/20 as
/// <see cref="NotSupportedException"/>.
/// </summary>
public class PcmDecoderBitDepthTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(20)]
    [InlineData(64)]
    public void Constructor_RejectsABitDepthItCannotDecode(int bitDepth)
    {
        var format = new AudioFormat { Codec = AudioCodecs.Pcm, SampleRate = 48000, Channels = 2, BitDepth = bitDepth };

        Assert.Throws<ArgumentException>(() => new PcmDecoder(format));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void Constructor_AcceptsTheDepthsTheConventionDefines(int? bitDepth)
    {
        var format = new AudioFormat { Codec = AudioCodecs.Pcm, SampleRate = 48000, Channels = 2, BitDepth = bitDepth };

        using var decoder = new PcmDecoder(format);

        Assert.Same(format, decoder.Format);
    }
}
