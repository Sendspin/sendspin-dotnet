using Sendspin.SDK.Models;

namespace Sendspin.SDK.Tests.Models;

/// <summary>
/// <see cref="AudioFormat.IsSameStreamConfiguration"/> decides whether a re-announced
/// <c>stream/start</c> can keep the running decode chain (#201), so it must track exactly the
/// fields a decoder is built from — and only those.
/// </summary>
public class AudioFormatTests
{
    private static AudioFormat Format() => new AudioFormat
    {
        Codec = "flac",
        SampleRate = 48_000,
        Channels = 2,
        BitDepth = 24,
        CodecHeader = "Zkxh",
    };

    [Fact]
    public void IsSameStreamConfiguration_IdenticalFields_IsTrue() =>
        Assert.True(Format().IsSameStreamConfiguration(Format()));

    [Fact]
    public void IsSameStreamConfiguration_CodecCaseDiffers_IsTrue()
    {
        var other = Format();
        other.Codec = "FLAC";

        // The decoder factory selects on the lower-cased codec, so casing alone is not a change.
        Assert.True(Format().IsSameStreamConfiguration(other));
    }

    [Fact]
    public void IsSameStreamConfiguration_BitrateDiffers_IsTrue()
    {
        var other = Format();
        other.Bitrate = 320;

        // Describes the server's encoder; nothing on the decode or output side reads it.
        Assert.True(Format().IsSameStreamConfiguration(other));
    }

    [Fact]
    public void IsSameStreamConfiguration_CodecDiffers_IsFalse()
    {
        var other = Format();
        other.Codec = "opus";

        Assert.False(Format().IsSameStreamConfiguration(other));
    }

    [Fact]
    public void IsSameStreamConfiguration_SampleRateDiffers_IsFalse()
    {
        var other = Format();
        other.SampleRate = 44_100;

        Assert.False(Format().IsSameStreamConfiguration(other));
    }

    [Fact]
    public void IsSameStreamConfiguration_ChannelsDiffer_IsFalse()
    {
        var other = Format();
        other.Channels = 1;

        Assert.False(Format().IsSameStreamConfiguration(other));
    }

    [Fact]
    public void IsSameStreamConfiguration_BitDepthDiffers_IsFalse()
    {
        var other = Format();
        other.BitDepth = 16;

        Assert.False(Format().IsSameStreamConfiguration(other));
    }

    [Fact]
    public void IsSameStreamConfiguration_CodecHeaderDiffers_IsFalse()
    {
        var other = Format();
        other.CodecHeader = "Zkxi";

        // FLAC prepends the decoded header to every frame and calibrates from the bit depth in it.
        Assert.False(Format().IsSameStreamConfiguration(other));
    }

    [Fact]
    public void IsSameStreamConfiguration_CodecHeaderAddedOrRemoved_IsFalse()
    {
        var without = Format();
        without.CodecHeader = null;

        Assert.False(Format().IsSameStreamConfiguration(without));
        Assert.False(without.IsSameStreamConfiguration(Format()));
    }
}
