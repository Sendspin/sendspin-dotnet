using System.Buffers.Binary;
using Sendspin.SDK.Client;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// A <c>stream/start</c> per role, as aiosendspin 10.0.0 sends them: the artwork role's when the
/// client declares its channels, the visualizer's when the stream starts, and the player's with
/// the first audio chunk — so the player's always lands last. Each configures its own role and
/// leaves the others as they were: "Starts a stream for one or more roles."
/// </summary>
public class PerRoleStreamStartTests
{
    /// <summary>Frozen local clock; everything here is stamped just before it, so due on arrival.</summary>
    private const long Now = 10_000_000;

    private const string ArtworkStart =
        """{"type":"stream/start","payload":{"artwork":{"channels":[{"source":"album","format":"jpeg","width":512,"height":512},{"source":"artist","format":"jpeg","width":256,"height":256}]}}}""";

    private const string VisualizerStart =
        """{"type":"stream/start","payload":{"visualizer":{"types":["spectrum"],"rate_max":30,"spectrum":{"n_disp_bins":4,"scale":"lin","f_min":20,"f_max":16000}}}}""";

    private const string PlayerStart =
        """{"type":"stream/start","payload":{"player":{"codec":"pcm","channels":2,"sample_rate":48000,"bit_depth":16}}}""";

    private static (SendspinClientService Client, FakeSendspinConnection Connection) Client()
    {
        var (client, connection, _) = TestClient.Create(activated: true, configure: options => options with
        {
            PrecisionTimer = new FakePrecisionTimer { CurrentTime = Now },
            ClockSynchronizer = new ConvergedClockSynchronizer(),
            AudioPipeline = new FakeAudioPipeline(),
            Capabilities = new ClientCapabilities
            {
                Roles = new List<string> { "player@v1", "artwork@v1", "visualizer@v1" },
                VisualizerRoleSupport = new VisualizerRoleSupport
                {
                    BufferCapacity = 65_536,
                    RateMax = 30,
                    Types = new List<string> { VisualizerTypes.Spectrum },
                    Spectrum = new VisualizerSpectrum { NDispBins = 4, Scale = "lin", FMin = 20, FMax = 16000 },
                },
            },
        });
        return (client, connection);
    }

    private static byte[] SpectrumFrame(long timestamp, int bins)
    {
        var buf = new byte[9 + (2 * bins)];
        buf[0] = BinaryMessageTypes.VisualizerSpectrum;
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(1, 8), timestamp);
        for (int bin = 0; bin < bins; bin++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(9 + (2 * bin), 2), (ushort)(bin + 1));
        }

        return buf;
    }

    [Fact]
    public void SpectrumFrames_AreStillParsed_AfterThePlayersOwnStreamStart()
    {
        var (client, connection) = Client();
        using var _c = client;

        var frames = new List<VisualizerFrame>();
        client.VisualizationReceived += (_, f) => frames.Add(f);

        connection.RaiseTextMessageReceived(ArtworkStart);
        connection.RaiseTextMessageReceived(VisualizerStart);
        connection.RaiseTextMessageReceived(PlayerStart);

        connection.RaiseBinaryMessageReceived(SpectrumFrame(Now - 1, bins: 4));

        var only = Assert.Single(frames);
        Assert.Equal(new[] { 1, 2, 3, 4 }, only.Spectrum!);
    }

    [Fact]
    public void SpectrumFrames_AreDropped_OnceTheVisualizerStreamHasEnded()
    {
        var (client, connection) = Client();
        using var _c = client;

        var frames = new List<VisualizerFrame>();
        client.VisualizationReceived += (_, f) => frames.Add(f);

        connection.RaiseTextMessageReceived(VisualizerStart);
        connection.RaiseTextMessageReceived(
            """{"type":"stream/end","payload":{"server_transmitted":1,"roles":["visualizer"]}}""");

        // The bin count went with the stream; the next one brings its own.
        connection.RaiseBinaryMessageReceived(SpectrumFrame(Now - 1, bins: 4));

        Assert.Empty(frames);
    }

    [Fact]
    public void ArtworkReconfiguredAfterAPlayerStart_KeepsTheTransferOnAnUnchangedChannel()
    {
        var (client, connection) = Client();
        using var _c = client;

        var received = new List<ArtworkReceivedEventArgs>();
        client.ArtworkReceived += (_, e) => received.Add(e);

        connection.RaiseTextMessageReceived(ArtworkStart);
        connection.RaiseTextMessageReceived(VisualizerStart);
        connection.RaiseTextMessageReceived(PlayerStart);

        connection.RaiseBinaryMessageReceived(ArtworkWire.Announce(BinaryMessageTypes.Artwork0, Now - 1, 2));
        connection.RaiseBinaryMessageReceived(ArtworkWire.Part(BinaryMessageTypes.Artwork0, 1));

        // Channel 1 changes; channel 0, whose image is half received, does not. With the artwork
        // configuration forgotten at the player's start, every channel read as changed and the
        // transfer was cancelled — making its next part a protocol error.
        connection.RaiseTextMessageReceived(
            """{"type":"stream/start","payload":{"artwork":{"channels":[{"source":"album","format":"jpeg","width":512,"height":512},{"source":"artist","format":"jpeg","width":64,"height":64}]}}}""");
        connection.RaiseBinaryMessageReceived(ArtworkWire.Part(BinaryMessageTypes.Artwork0, 2));

        var only = Assert.Single(received);
        Assert.Equal(new byte[] { 1, 2 }, only.ImageData);
        Assert.Null(connection.LastDisconnectReason);
    }

    [Fact]
    public void LastStreamStart_IsStillTheLastMessage()
    {
        var (client, connection) = Client();
        using var _c = client;

        connection.RaiseTextMessageReceived(VisualizerStart);
        connection.RaiseTextMessageReceived(PlayerStart);

        Assert.NotNull(client.LastStreamStart!.Format);
        Assert.Null(client.LastStreamStart.Visualizer);
    }
}
