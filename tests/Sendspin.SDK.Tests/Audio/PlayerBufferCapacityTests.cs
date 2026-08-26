using Sendspin.SDK.Audio;
using Sendspin.SDK.Client;
using Sendspin.SDK.Models;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Tests.Audio;

/// <summary>
/// Issue #230: <c>buffer_capacity</c> is a hard byte limit the server may fill toward, so it
/// has to be a figure the audio buffer can actually hold. It used to be a flat 32 MB with no
/// relationship to the buffer at all.
/// </summary>
public class PlayerBufferCapacityTests
{
    private static readonly AudioFormat Opus = new()
    {
        Codec = "opus", SampleRate = 48_000, Channels = 2, Bitrate = 256,
    };

    private static readonly AudioFormat Pcm = new()
    {
        Codec = "pcm", SampleRate = 48_000, Channels = 2, BitDepth = 16,
    };

    private static readonly AudioFormat Flac = new()
    {
        Codec = "flac", SampleRate = 48_000, Channels = 2,
    };

    [Fact]
    public void AdvertisedCapacity_IsHoldable_ForEveryAdvertisedFormat()
    {
        var capabilities = new ClientCapabilities();

        foreach (var format in capabilities.AudioFormats)
        {
            var holdableMs = PlayerBufferCapacity.HoldableMilliseconds(capabilities.BufferCapacity, format);

            Assert.True(
                holdableMs <= PlayerBufferCapacity.DefaultDecodedBufferMilliseconds,
                $"{format.Codec}: advertised {capabilities.BufferCapacity} bytes decodes to " +
                $"{holdableMs:F0}ms but the buffer holds only {PlayerBufferCapacity.DefaultDecodedBufferMilliseconds}ms");
        }
    }

    [Fact]
    public void AdvertisedCapacity_IsBoundedByTheMostCompressedCodec()
    {
        // A megabyte of Opus is minutes of audio; a megabyte of PCM is seconds. Sizing the
        // advertisement off PCM — the intuitive "worst case" — is what lets an Opus stream
        // overrun the buffer by a factor of six.
        var opusOnly = PlayerBufferCapacity.AdvertisedBytes(10_000, new[] { Opus });
        var mixed = PlayerBufferCapacity.AdvertisedBytes(10_000, new[] { Opus, Pcm, Flac });

        Assert.Equal(opusOnly, mixed);
        Assert.True(mixed < PlayerBufferCapacity.AdvertisedBytes(10_000, new[] { Pcm }));
    }

    [Fact]
    public void AdvertisedCapacity_LeavesHeadroomLikeTheReference()
    {
        // Four fifths of the real capacity, as the C++ reference advertises.
        var advertised = PlayerBufferCapacity.AdvertisedBytes(10_000, new[] { Opus });
        var full = 10L * PlayerBufferCapacity.CompressedBytesPerSecond(Opus);

        Assert.Equal(full * 4 / 5, advertised);
    }

    [Fact]
    public void AdvertisedCapacity_TracksTheBufferDuration()
    {
        // 9.x has no public knob for the decoded-buffer duration, so the derivation is
        // exercised directly: twice the buffer is twice the advertisement.
        var doubled = PlayerBufferCapacity.AdvertisedBytes(60_000, new[] { Opus });
        var single = PlayerBufferCapacity.AdvertisedBytes(30_000, new[] { Opus });

        Assert.Equal(doubled / 2, single);
    }

    [Fact]
    public void DefaultAdvertisement_MatchesTheDefaultBuffer()
    {
        // The two defaults are the same constant, so a client that configures neither is
        // still telling the server the truth.
        using var buffer = new TimedAudioBuffer(Pcm, new FakeClockSynchronizer());

        Assert.Equal(
            (double)PlayerBufferCapacity.DefaultDecodedBufferMilliseconds,
            buffer.CapacityMilliseconds);
    }

    [Fact]
    public void ExplicitBufferCapacity_BelowTheCeiling_IsHonoured()
    {
        // Advertising less than the buffer holds is always safe, so a caller asking for a
        // smaller figure gets exactly it.
        var capabilities = new ClientCapabilities { BufferCapacity = 12_345 };

        Assert.Equal(12_345, capabilities.BufferCapacity);
        Assert.False(capabilities.BufferCapacityWasClamped);
    }

    [Fact]
    public void ExplicitBufferCapacity_AboveTheCeiling_IsClamped()
    {
        // The 9.x surface is frozen, so there is no property for a caller to declare a larger
        // real buffer with - which means an over-large advertisement can only ever be a
        // promise this client cannot keep. Clamped rather than honoured: the spec lets the
        // server fill toward whatever is advertised, and everything past the buffer is audio
        // discarded before it plays. 32 MB was the pre-9.3 default.
        var capabilities = new ClientCapabilities { BufferCapacity = 32_000_000 };

        Assert.True(capabilities.BufferCapacityWasClamped);
        Assert.Equal(32_000_000, capabilities.ConfiguredBufferCapacity);
        Assert.Equal(capabilities.TruthfulBufferCapacityBytes, capabilities.BufferCapacity);

        foreach (var format in capabilities.AudioFormats)
        {
            Assert.True(
                PlayerBufferCapacity.HoldableMilliseconds(capabilities.BufferCapacity, format)
                    <= PlayerBufferCapacity.DefaultDecodedBufferMilliseconds,
                $"{format.Codec}: clamped advertisement still overruns the buffer");
        }
    }

    [Fact]
    public void NoAdvertisedFormats_AdvertisesNothing()
    {
        Assert.Equal(0, PlayerBufferCapacity.AdvertisedBytes(30_000, Array.Empty<AudioFormat>()));
    }

    [Theory]
    [InlineData("opus", 32_000)]     // 256 kbps
    [InlineData("pcm", 192_000)]     // 48 kHz x 2ch x 16-bit
    public void CompressedBytesPerSecond_MatchesTheCodec(string codec, int expected)
    {
        var format = codec == "opus" ? Opus : Pcm;

        Assert.Equal(expected, PlayerBufferCapacity.CompressedBytesPerSecond(format));
    }

    [Fact]
    public void FlacIsAssumedToCompress_SoItsAdvertisementIsLowerThanPcm()
    {
        Assert.True(
            PlayerBufferCapacity.CompressedBytesPerSecond(Flac)
                < PlayerBufferCapacity.CompressedBytesPerSecond(Pcm));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void OpusWithoutADeclaredBitrate_IsNotTreatedAsUncompressed(int? bitrate)
    {
        // Falling through to the PCM byte rate overstated this roughly sixfold, silently
        // breaking the holdability invariant for anyone advertising opus without a bitrate.
        var opus = new AudioFormat
        {
            Codec = "opus", SampleRate = 48_000, Channels = 2, Bitrate = bitrate,
        };

        Assert.True(
            PlayerBufferCapacity.CompressedBytesPerSecond(opus)
                < PlayerBufferCapacity.CompressedBytesPerSecond(Pcm));

        var capabilities = new ClientCapabilities
        {
            AudioFormats = new List<AudioFormat> { opus, Pcm },
        };
        var holdableMs = PlayerBufferCapacity.HoldableMilliseconds(capabilities.BufferCapacity, opus);

        Assert.True(
            holdableMs <= PlayerBufferCapacity.DefaultDecodedBufferMilliseconds,
            $"advertised {capabilities.BufferCapacity} bytes decodes to {holdableMs:F0}ms of opus, " +
            $"over the {PlayerBufferCapacity.DefaultDecodedBufferMilliseconds}ms the buffer holds");
    }
}
