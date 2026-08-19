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
                holdableMs <= capabilities.AudioBufferCapacityMs,
                $"{format.Codec}: advertised {capabilities.BufferCapacity} bytes decodes to " +
                $"{holdableMs:F0}ms but the buffer holds only {capabilities.AudioBufferCapacityMs}ms");
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
        var capabilities = new ClientCapabilities { AudioBufferCapacityMs = 60_000 };
        var doubled = capabilities.BufferCapacity;

        capabilities.AudioBufferCapacityMs = 30_000;

        Assert.Equal(doubled / 2, capabilities.BufferCapacity);
    }

    [Fact]
    public void DefaultAdvertisement_MatchesTheDefaultBuffer()
    {
        // The two defaults are the same constant, so a client that configures neither is
        // still telling the server the truth.
        using var buffer = new TimedAudioBuffer(Pcm, new FakeClockSynchronizer());

        Assert.Equal(new ClientCapabilities().AudioBufferCapacityMs, buffer.CapacityMilliseconds);
    }

    [Fact]
    public void ExplicitBufferCapacity_OverridesTheDerivation()
    {
        var capabilities = new ClientCapabilities { BufferCapacity = 12_345 };

        Assert.Equal(12_345, capabilities.BufferCapacity);

        // ...and stays overridden even if the buffer duration is changed afterwards.
        capabilities.AudioBufferCapacityMs = 90_000;
        Assert.Equal(12_345, capabilities.BufferCapacity);
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
}
