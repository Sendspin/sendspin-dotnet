using Sendspin.SDK.Client;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// A host configuration that would put a value on the wire the spec does not allow is rejected
/// when the client is constructed, where the app can still fix it, rather than reaching a server
/// that fails the <c>client/hello</c> parse or disconnects on <c>client/state</c> (spec PRs #238
/// and #257). Each rule has a rejected configuration and the accepted one beside it.
/// </summary>
public class CapabilitiesWireValueValidationTests
{
    private static SendspinClientService Create(ClientCapabilities capabilities)
    {
        var (client, _, _) = TestClient.Create(configure: options => options with { Capabilities = capabilities });
        return client;
    }

    [Fact]
    public void PlayerWithoutFlacOrPcm_ThrowsAtConstruction()
    {
        var ex = Assert.Throws<ArgumentException>(() => Create(new ClientCapabilities
        {
            AudioFormats = new List<AudioFormat>
            {
                new() { Codec = "opus", SampleRate = 48000, Channels = 2, Bitrate = 256 },
            },
        }));

        Assert.Contains("AudioFormats", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("flac")]
    [InlineData("pcm")]
    public void PlayerWithFlacOrPcm_IsAccepted(string codec)
    {
        using var client = Create(new ClientCapabilities
        {
            AudioFormats = new List<AudioFormat>
            {
                new() { Codec = "opus", SampleRate = 48000, Channels = 2, Bitrate = 256 },
                new() { Codec = codec, SampleRate = 48000, Channels = 2, BitDepth = 16 },
            },
        });
    }

    [Fact]
    public void NonPlayerWithoutFlacOrPcm_IsAccepted()
    {
        // supported_formats only travels with the player role, so a client that does not
        // advertise it has no codec list to get wrong.
        using var client = Create(new ClientCapabilities
        {
            Roles = new List<string> { "controller@v1" },
            AudioFormats = new List<AudioFormat>(),
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void VisualizerWithNonPositiveBufferCapacity_ThrowsAtConstruction(int bufferCapacity)
    {
        var ex = Assert.Throws<ArgumentException>(() => Create(VisualizerCapabilities(bufferCapacity)));

        Assert.Contains("BufferCapacity", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VisualizerWithPositiveBufferCapacity_IsAccepted()
    {
        using var client = Create(VisualizerCapabilities(bufferCapacity: 65536));
    }

    [Theory]
    [InlineData("cover", "jpeg", 512, 512, "Source")]
    [InlineData("album", "gif", 512, 512, "Format")]
    [InlineData("album", null, 512, 512, "Format")]
    [InlineData("album", "jpeg", 0, 512, "Width")]
    [InlineData("album", "jpeg", null, 512, "Width")]
    [InlineData("artist", "png", 512, -1, "Height")]
    [InlineData("artist", "png", 512, null, "Height")]
    public void InvalidArtworkChannel_ThrowsAtConstruction(
        string source, string? format, int? width, int? height, string property)
    {
        var ex = Assert.Throws<ArgumentException>(() => Create(new ClientCapabilities
        {
            ArtworkChannels = new List<ArtworkChannelState>
            {
                new() { Source = source, Format = format, Width = width, Height = height },
            },
        }));

        Assert.Contains($"ArtworkChannelState.{property}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidArtworkChannels_AreAccepted()
    {
        // A disabled channel is checked no further than its source: its format and size never
        // travel, so the nulls an app leaves on it are not a wire value.
        using var client = Create(new ClientCapabilities
        {
            ArtworkChannels = new List<ArtworkChannelState>
            {
                new() { Source = ArtworkSources.Album, Format = "jpeg", Width = 512, Height = 512 },
                new() { Source = ArtworkSources.Artist, Format = "png", Width = 64, Height = 96 },
                new() { Source = ArtworkSources.None, Format = null, Width = null, Height = null },
            },
        });
    }

    [Fact]
    public void InvalidArtworkChannel_WithoutArtworkRole_IsAccepted()
    {
        // The artwork state object is only reported for the artwork role.
        using var client = Create(new ClientCapabilities
        {
            Roles = new List<string> { "player@v1" },
            ArtworkChannels = new List<ArtworkChannelState> { new() { Format = "gif" } },
        });
    }

    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF")]
    [InlineData("aa-bb-cc-dd-ee-ff")]
    [InlineData("aabbccddeeff")]
    [InlineData("aa:bb:cc:dd:ee")]
    [InlineData("")]
    public void MacAddressNotLowercaseColonSeparated_ThrowsAtConstruction(string macAddress)
    {
        var ex = Assert.Throws<ArgumentException>(() => Create(new ClientCapabilities { MacAddress = macAddress }));

        Assert.Contains("MacAddress", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("aa:bb:cc:dd:ee:ff")]
    [InlineData("00:1a:2b:3c:4d:5e")]
    [InlineData(null)]
    public void MacAddressLowercaseColonSeparatedOrUnset_IsAccepted(string? macAddress)
    {
        using var client = Create(new ClientCapabilities { MacAddress = macAddress });
    }

    private static ClientCapabilities VisualizerCapabilities(int bufferCapacity) => new()
    {
        Roles = new List<string> { "visualizer@v1" },
        VisualizerRoleSupport = new VisualizerRoleSupport
        {
            BufferCapacity = bufferCapacity,
            RateMax = 30,
            Types = new List<string> { VisualizerTypes.Loudness },
        },
    };
}
