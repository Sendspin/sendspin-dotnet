using System.Buffers.Binary;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol.Messages;

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

    // Announced as 32 for 24-bit content by servers encoding through PyAV's s32 container; the
    // decoder scales from STREAMINFO, so the announced depth decides nothing.
    [InlineData("""{"codec":"flac","channels":2,"sample_rate":44100,"bit_depth":32,"codec_header":"ZkxhQw=="}""")]

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

    /// <summary>
    /// The server goes on to send the format it announced, so a stream left running would put
    /// those chunks through the previous format's decoder — for PCM, as full-scale noise.
    /// </summary>
    [Fact]
    public void StreamStart_WithAnUnlistedFormatMidStream_EndsThePlayerStreamAndDropsItsChunks()
    {
        const string Listed = """{"codec":"pcm","channels":2,"sample_rate":48000,"bit_depth":16}""";
        var (client, connection, pipe) = PlayerListing(
            new AudioFormat { Codec = "pcm", SampleRate = 48000, Channels = 2, BitDepth = 16 });
        using var _c = client;

        connection.RaiseTextMessageReceived(StreamStart(Listed));
        connection.RaiseBinaryMessageReceived(AudioFrame(1_000));

        connection.RaiseTextMessageReceived(StreamStart(
            """{"codec":"flac","channels":2,"sample_rate":48000,"bit_depth":16,"codec_header":"ZkxhQw=="}"""));

        Assert.Equal(1, pipe.StopCount);
        Assert.Equal(PlaybackState.Idle, client.CurrentGroup?.PlaybackState);
        Assert.Equal(ConnectionState.Connected, client.ConnectionState);

        // A stopped pipeline has no decoder, which is what the real one reports here.
        pipe.IsReady = false;
        connection.RaiseBinaryMessageReceived(AudioFrame(2_000));
        connection.RaiseBinaryMessageReceived(AudioFrame(3_000));

        Assert.Equal(1_000, Assert.Single(pipe.Chunks).ServerTimestamp);

        connection.RaiseTextMessageReceived(StreamStart(Listed));
        pipe.IsReady = true;
        connection.RaiseBinaryMessageReceived(AudioFrame(4_000));

        Assert.Equal(2, pipe.StartCalls.Count);
        Assert.Equal(new long[] { 1_000, 4_000 }, pipe.Chunks.Select(c => c.ServerTimestamp));
        Assert.Equal(PlaybackState.Playing, client.CurrentGroup?.PlaybackState);
    }

    private static byte[] AudioFrame(long timestamp)
    {
        // Player audio chunk header: type + timestamp + send_ahead (13 bytes), audio from byte 13.
        var frame = new byte[13 + 4];
        frame[0] = BinaryMessageTypes.PlayerAudio0;
        BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(1, 8), timestamp);
        return frame;
    }
}
