using System.Buffers.Binary;
using Sendspin.SDK.Client;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// The artwork and visualizer roles against a lifecycle chain that is busy opening or closing the
/// output device. Their binary messages are handled on the receive loop, so the part of a
/// <c>stream/start</c> or <c>stream/end</c> that concerns them has to be too: waiting behind the
/// player's device, it took effect after the data the server sent next. "Clients MUST process
/// <c>stream/start</c>, <c>stream/clear</c>, and <c>stream/end</c> in delivery order".
/// </summary>
public class DisplayRoleDeliveryOrderTests
{
    private const long Now = 10_000_000;

    private const string PlayerStart =
        """{"type":"stream/start","payload":{"player":{"codec":"pcm","channels":2,"sample_rate":48000,"bit_depth":16}}}""";

    private const string PlayerEnd =
        """{"type":"stream/end","payload":{"server_transmitted":1,"roles":["player"]}}""";

    private const string VisualizerEnd =
        """{"type":"stream/end","payload":{"server_transmitted":1,"roles":["visualizer"]}}""";

    private const string PlayerClear =
        """{"type":"stream/clear","payload":{"server_transmitted":1,"roles":["player"]}}""";

    private static string VisualizerStart(int bins) =>
        """{"type":"stream/start","payload":{"visualizer":{"types":["spectrum"],"rate_max":30,"spectrum":{"n_disp_bins":"""
        + bins
        + ""","scale":"lin","f_min":20,"f_max":16000}}}}""";

    private static string ArtworkStart(int width) =>
        """{"type":"stream/start","payload":{"artwork":{"channels":[{"source":"album","format":"jpeg","width":"""
        + width
        + ""","height":512}]}}}""";

    private static (SendspinClientService Client, FakeSendspinConnection Connection, FakePrecisionTimer Timer)
        Client(FakeAudioPipeline pipe)
    {
        var timer = new FakePrecisionTimer { CurrentTime = Now };
        var (client, connection, _) = TestClient.Create(activated: true, configure: options => options with
        {
            PrecisionTimer = timer,
            ClockSynchronizer = new ConvergedClockSynchronizer(),
            AudioPipeline = pipe,
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
        return (client, connection, timer);
    }

    private static byte[] SpectrumFrame(long timestamp, int bins)
    {
        var buf = new byte[9 + (2 * bins)];
        buf[0] = BinaryMessageTypes.VisualizerSpectrum;
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(1, 8), timestamp);
        return buf;
    }

    private static TaskCompletionSource Hold() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task WithTimeout(Task task) => task.WaitAsync(TimeSpan.FromSeconds(30));

    /// <summary>
    /// Waits until every lifecycle message delivered so far has run: a player stream/clear is
    /// sent last, and its turn on the chain is the proof.
    /// </summary>
    private static async Task Settled(FakeSendspinConnection connection, FakeAudioPipeline pipe, int calls)
    {
        connection.RaiseTextMessageReceived(PlayerClear);
        await WithTimeout(pipe.CallsCompleted(calls + 1));
    }

    [Fact]
    public async Task ATrackChange_KeepsTheNewVisualizerStreamsFrames()
    {
        // What the reference server sends at a track change, within one millisecond: the player's
        // stream/end, the visualizer's, the visualizer's stream/start, the player's, then the new
        // stream's frames - stamped ahead, for display as the audio they describe plays. The
        // visualizer's end waited for the output device to close and then discarded them.
        var pipe = new FakeAudioPipeline { HoldNextStop = Hold() };
        var (client, connection, timer) = Client(pipe);
        using var _c = client;

        var frames = new List<VisualizerFrame>();
        client.VisualizationReceived += (_, f) =>
        {
            lock (frames)
            {
                frames.Add(f);
            }
        };

        connection.RaiseTextMessageReceived(VisualizerStart(bins: 4));
        connection.RaiseTextMessageReceived(PlayerStart);
        await WithTimeout(pipe.CallsCompleted(1));

        var held = pipe.HoldNextStop!;
        connection.RaiseTextMessageReceived(PlayerEnd);
        await WithTimeout(pipe.StopEntered);

        connection.RaiseTextMessageReceived(VisualizerEnd);
        connection.RaiseTextMessageReceived(VisualizerStart(bins: 8));
        connection.RaiseTextMessageReceived(PlayerStart);
        connection.RaiseBinaryMessageReceived(SpectrumFrame(Now + 1_000, bins: 8));

        held.SetResult();
        await Settled(connection, pipe, calls: 3);

        timer.CurrentTime = Now + 1_000;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (true)
        {
            lock (frames)
            {
                if (frames.Count == 1)
                {
                    break;
                }
            }

            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the new stream's frame");
            await Task.Delay(10);
        }

        Assert.Equal(8, frames[0].Spectrum!.Count);
    }

    [Fact]
    public async Task AnArtworkReconfigurationBehindADeviceOpen_DoesNotCancelTheTransferThatFollowsIt()
    {
        // The application changes a channel while the player's stream is starting. The server
        // answers with an artwork stream/start and then the image encoded for it. The start
        // waited for the device to open, found the channel changed, and cancelled the transfer
        // the server had begun for that very configuration - whose next part closed the
        // connection as a protocol error.
        var pipe = new FakeAudioPipeline { HoldNextStart = Hold() };
        var (client, connection, _) = Client(pipe);
        using var _c = client;

        var received = new List<ArtworkReceivedEventArgs>();
        client.ArtworkReceived += (_, e) => received.Add(e);

        connection.RaiseTextMessageReceived(ArtworkStart(width: 512));

        var held = pipe.HoldNextStart!;
        connection.RaiseTextMessageReceived(PlayerStart);
        await WithTimeout(pipe.StartEntered);

        connection.RaiseTextMessageReceived(ArtworkStart(width: 128));
        connection.RaiseBinaryMessageReceived(ArtworkWire.Announce(BinaryMessageTypes.Artwork0, Now - 1, 2));
        connection.RaiseBinaryMessageReceived(ArtworkWire.Part(BinaryMessageTypes.Artwork0, 1));

        held.SetResult();
        await Settled(connection, pipe, calls: 1);

        connection.RaiseBinaryMessageReceived(ArtworkWire.Part(BinaryMessageTypes.Artwork0, 2));

        var only = Assert.Single(received);
        Assert.Equal(new byte[] { 1, 2 }, only.ImageData);
        Assert.Null(connection.LastDisconnectReason);
    }

    [Fact]
    public async Task LifecycleEvents_AreRaisedInDeliveryOrder_WhileTheDeviceCloses()
    {
        var pipe = new FakeAudioPipeline { HoldNextStop = Hold() };
        var (client, connection, _) = Client(pipe);
        using var _c = client;

        var events = new List<string>();
        client.StreamStartReceived += (_, _) => events.Add("start");
        client.StreamEndReceived += (_, _) => events.Add("end");
        client.StreamClearReceived += (_, _) => events.Add("clear");

        connection.RaiseTextMessageReceived(PlayerStart);
        await WithTimeout(pipe.CallsCompleted(1));
        events.Clear();

        var held = pipe.HoldNextStop!;
        connection.RaiseTextMessageReceived(PlayerEnd);
        await WithTimeout(pipe.StopEntered);

        connection.RaiseTextMessageReceived(VisualizerEnd);
        connection.RaiseTextMessageReceived(VisualizerStart(bins: 4));
        connection.RaiseTextMessageReceived(
            """{"type":"stream/clear","payload":{"server_transmitted":1,"roles":["visualizer"]}}""");

        // Raised as each message is received; none of them waits for the device.
        Assert.Equal(new[] { "end", "end", "start", "clear" }, events);
        Assert.NotNull(client.LastStreamStart!.Visualizer);

        held.SetResult();
        await WithTimeout(pipe.CallsCompleted(2));
    }
}
