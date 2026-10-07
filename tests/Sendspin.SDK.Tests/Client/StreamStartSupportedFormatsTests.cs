using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Models;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// A <c>stream/start</c> player object opens the pipeline only for a format this client listed
/// in <c>supported_formats</c> (#347).
/// </summary>
public class StreamStartSupportedFormatsTests
{
    private static (SendspinClientService Client, FakeSendspinConnection Connection, FakeAudioPipeline Pipeline)
        PlayerListing(params AudioFormat[] formats)
    {
        var pipe = new FakeAudioPipeline();
        var (client, connection, _) = TestClient.Create(configure: options => options with
        {
            AudioPipeline = pipe,
            ClockSynchronizer = new ConvergedClockSynchronizer(),
            Capabilities = new ClientCapabilities
            {
                Roles = new List<string> { "player@v1" },
                AudioFormats = formats.ToList(),
            },
        });

        TestClient.CompleteHandshake(connection, "player@v1");
        return (client, connection, pipe);
    }

    private static string StreamStart(string player) =>
        """{"type":"stream/start","payload":{"player":""" + player + "}}";

    [Theory]

    // The issue's allocation: 30 s at this rate and channel count is a 2.9 GB ring.
    [InlineData("""{"codec":"pcm","channels":64,"sample_rate":384000,"bit_depth":16}""")]

    // A divisor of zero in the PCM decoder, per chunk.
    [InlineData("""{"codec":"pcm","channels":2,"sample_rate":48000,"bit_depth":4}""")]
    [InlineData("""{"codec":"pcm","channels":2,"sample_rate":48000,"bit_depth":24}""")]
    [InlineData("""{"codec":"pcm","channels":2,"sample_rate":44100,"bit_depth":16}""")]
    [InlineData("""{"codec":"pcm","channels":1,"sample_rate":48000,"bit_depth":16}""")]
    [InlineData("""{"codec":"flac","channels":2,"sample_rate":48000,"bit_depth":16}""")]
    [InlineData("""{"codec":"opus","channels":2,"sample_rate":24000,"bit_depth":16}""")]
    public void StreamStart_WithAFormatTheClientDidNotList_DoesNotStartThePipeline(string player)
    {
        var (client, connection, pipe) = PlayerListing(
            new AudioFormat { Codec = "pcm", SampleRate = 48000, Channels = 2, BitDepth = 16 },
            new AudioFormat { Codec = "opus", SampleRate = 48000, Channels = 2 });
        using var _c = client;

        connection.RaiseTextMessageReceived(StreamStart(player));

        Assert.Empty(pipe.StartCalls);
        Assert.NotEqual(PlaybackState.Playing, client.CurrentGroup?.PlaybackState);
        Assert.Equal(ConnectionState.Connected, client.ConnectionState);
    }

    [Theory]
    [InlineData("""{"codec":"pcm","channels":2,"sample_rate":48000,"bit_depth":16}""")]
    [InlineData("""{"codec":"pcm","channels":2,"sample_rate":96000,"bit_depth":24}""")]
    [InlineData("""{"codec":"flac","channels":2,"sample_rate":44100,"bit_depth":24,"codec_header":"ZkxhQw=="}""")]

    // An entry listed without a bit depth goes out in client/hello as 16.
    [InlineData("""{"codec":"flac","channels":2,"sample_rate":48000,"bit_depth":16}""")]

    // "bit_depth: integer - bit depth to be used; ignored for opus".
    [InlineData("""{"codec":"opus","channels":2,"sample_rate":48000,"bit_depth":16}""")]
    [InlineData("""{"codec":"opus","channels":2,"sample_rate":48000,"bit_depth":24}""")]
    [InlineData("""{"codec":"opus","channels":2,"sample_rate":48000}""")]
    public void StreamStart_WithAListedFormat_StartsThePipeline(string player)
    {
        var (client, connection, pipe) = PlayerListing(
            new AudioFormat { Codec = "opus", SampleRate = 48000, Channels = 2, Bitrate = 256 },
            new AudioFormat { Codec = "pcm", SampleRate = 48000, Channels = 2, BitDepth = 16 },
            new AudioFormat { Codec = "pcm", SampleRate = 96000, Channels = 2, BitDepth = 24 },
            new AudioFormat { Codec = "flac", SampleRate = 44100, Channels = 2, BitDepth = 24 },
            new AudioFormat { Codec = "flac", SampleRate = 48000, Channels = 2 });
        using var _c = client;

        connection.RaiseTextMessageReceived(StreamStart(player));

        Assert.Single(pipe.StartCalls);
        Assert.Equal(PlaybackState.Playing, client.CurrentGroup?.PlaybackState);
    }

    [Fact]
    public async Task StreamStart_ForTheFormatTheClientPrefers_StartsThePipeline()
    {
        var preferred = new AudioFormat { Codec = "flac", SampleRate = 96000, Channels = 2, BitDepth = 24 };
        var (client, connection, pipe) = PlayerListing(
            new AudioFormat { Codec = "pcm", SampleRate = 48000, Channels = 2, BitDepth = 16 },
            preferred);
        using var _c = client;

        await client.SetPlayerFormatPreferenceAsync(preferred);
        connection.RaiseTextMessageReceived(StreamStart(
            """{"codec":"flac","channels":2,"sample_rate":96000,"bit_depth":24,"codec_header":"ZkxhQw=="}"""));

        Assert.Single(pipe.StartCalls);
    }
}
