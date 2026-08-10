using System.Buffers.Binary;
using Sendspin.SDK.Client;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Client-level coverage for the visualizer role: hello advertisement, stream/start config caching,
/// binary frame dispatch through VisualizationReceived (incl. spectrum bin-count validation), and
/// the request-format send path.
/// </summary>
public class SendspinClientServiceVisualizerTests
{
    private static byte[] Frame(byte type, long ts, byte[] data)
    {
        var buf = new byte[9 + data.Length];
        buf[0] = type;
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(1, 8), ts);
        data.CopyTo(buf, 9);
        return buf;
    }

    private static byte[] U16(ushort v)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, v);
        return b;
    }

    private static (SendspinClientService Client, FakeSendspinConnection Connection) VisualizerClient()
    {
        var (client, connection, _) = TestClient.Create(configure: options =>
            options with
            {
                Capabilities = new ClientCapabilities
                {
                    Roles = new List<string> { "visualizer@v1" },
                    VisualizerSupport = new VisualizerSupport
                    {
                        BufferCapacity = 65536,
                        RateMax = 30,
                        Types = new List<string> { VisualizerTypes.Loudness, VisualizerTypes.Spectrum },
                        Spectrum = new VisualizerSpectrum { NDispBins = 4, Scale = "log", FMin = 20, FMax = 16000 },
                    },
                },
            });
        return (client, connection);
    }

    [Fact]
    public void ClientHello_AdvertisesVisualizerSupport()
    {
        var (client, connection) = VisualizerClient();
        using var _c = client;

        TestClient.CompleteHandshake(connection, "visualizer@v1");

        var support = connection.SentMessages.OfType<ClientHelloMessage>().Single().Payload.VisualizerV1Support;
        Assert.NotNull(support);
        Assert.Equal(30, support.RateMax);
        Assert.Contains(VisualizerTypes.Spectrum, support.Types);
        Assert.Equal(4, support.Spectrum!.NDispBins);
        Assert.Equal("log", support.Spectrum.Scale);
    }

    [Fact]
    public void StreamStart_ParsesVisualizerConfig()
    {
        var (client, connection) = VisualizerClient();
        using var _c = client;

        connection.RaiseTextMessageReceived("""
            {
                "type": "stream/start",
                "payload": { "visualizer": {
                    "types": ["spectrum", "beat"], "rate_max": 30, "tracks_downbeats": true,
                    "spectrum": { "n_disp_bins": 8, "scale": "mel", "f_min": 40, "f_max": 18000 }
                } }
            }
            """);

        var vis = client.LastStreamStart?.Visualizer;
        Assert.NotNull(vis);
        Assert.Equal(new[] { "spectrum", "beat" }, vis.Types);
        Assert.True(vis.TracksDownbeats);
        Assert.Equal(8, vis.Spectrum!.NDispBins);
    }

    [Fact]
    public void BinaryFrame_RaisesVisualizationReceived()
    {
        var (client, connection) = VisualizerClient();
        using var _c = client;

        VisualizerFrame? frame = null;
        client.VisualizationReceived += (_, f) => frame = f;

        connection.RaiseBinaryMessageReceived(Frame(BinaryMessageTypes.VisualizerLoudness, 500, U16(40000)));

        Assert.NotNull(frame);
        Assert.Equal(500, frame.Timestamp);
        Assert.Equal(40000, frame.Loudness);
    }

    [Fact]
    public void SpectrumFrame_ValidatedAgainstNegotiatedBinCount()
    {
        var (client, connection) = VisualizerClient();
        using var _c = client;

        var frames = new List<VisualizerFrame>();
        client.VisualizationReceived += (_, f) => frames.Add(f);

        // Negotiate 4 bins via stream/start.
        connection.RaiseTextMessageReceived("""
            { "type": "stream/start", "payload": { "visualizer": {
                "types": ["spectrum"], "rate_max": 30,
                "spectrum": { "n_disp_bins": 4, "scale": "lin", "f_min": 20, "f_max": 16000 } } } }
            """);

        // 4-bin spectrum: accepted.
        var fourBins = Enumerable.Range(1, 4).SelectMany(i => U16((ushort)i)).ToArray();
        connection.RaiseBinaryMessageReceived(Frame(BinaryMessageTypes.VisualizerSpectrum, 1, fourBins));

        // 8-bin spectrum: rejected (mismatch with negotiated 4).
        var eightBins = Enumerable.Range(1, 8).SelectMany(i => U16((ushort)i)).ToArray();
        connection.RaiseBinaryMessageReceived(Frame(BinaryMessageTypes.VisualizerSpectrum, 2, eightBins));

        var only = Assert.Single(frames);
        Assert.Equal(new[] { 1, 2, 3, 4 }, only.Spectrum!);
    }

    [Fact]
    public void MalformedFrame_RaisesNoEvent()
    {
        var (client, connection) = VisualizerClient();
        using var _c = client;

        var fired = false;
        client.VisualizationReceived += (_, _) => fired = true;

        // Loudness needs 2 data bytes; give 1.
        connection.RaiseBinaryMessageReceived(Frame(BinaryMessageTypes.VisualizerLoudness, 1, new byte[] { 0x01 }));

        Assert.False(fired);
    }

    [Fact]
    public void DefaultCapabilities_DoNotAdvertiseVisualizer()
    {
        // Default capabilities: no VisualizerSupport, no visualizer@v1 role.
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        TestClient.CompleteHandshake(connection);

        var hello = connection.SentMessages.OfType<ClientHelloMessage>().Single();
        Assert.Null(hello.Payload.VisualizerV1Support);
        Assert.DoesNotContain("visualizer@v1", hello.Payload.SupportedRoles);

        var json = Sendspin.SDK.Protocol.MessageSerializer.Serialize(hello);
        Assert.DoesNotContain("visualizer@v1_support", json);
    }

    [Fact]
    public void SpectrumFrame_BeforeStreamStart_IsDropped()
    {
        var (client, connection) = VisualizerClient();
        using var _c = client;

        var fired = false;
        client.VisualizationReceived += (_, _) => fired = true;

        // No stream/start yet, so no negotiated bin count: the spectrum frame cannot be decoded.
        var bins = Enumerable.Range(0, 4).SelectMany(i => U16((ushort)i)).ToArray();
        connection.RaiseBinaryMessageReceived(Frame(BinaryMessageTypes.VisualizerSpectrum, 1, bins));

        Assert.False(fired);
    }

    [Fact]
    public void BeatFrame_DispatchedEndToEnd()
    {
        var (client, connection) = VisualizerClient();
        using var _c = client;

        VisualizerFrame? frame = null;
        client.VisualizationReceived += (_, f) => frame = f;

        connection.RaiseBinaryMessageReceived(Frame(BinaryMessageTypes.VisualizerBeat, 700, new byte[] { 0b0000_0001 }));

        Assert.NotNull(frame);
        Assert.Equal(700, frame.Timestamp);
        Assert.True(frame.IsDownbeat);
    }

    [Fact]
    public void ThrowingVisualizationHandler_PropagatesToTheReceiveLoop()
    {
        var (client, connection) = VisualizerClient();
        using var _c = client;

        var calls = 0;
        client.VisualizationReceived += (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("subscriber boom");
        };

        // A throwing subscriber is a bug in the app's own handling, and the inbound path
        // no longer swallows it (#88 item 2): it escapes the receive callback, and in
        // production the receive loop surfaces it as a lost connection an operator can
        // see. Swallowing it here kept the visualizer alive but hid the bug.
        Assert.Throws<InvalidOperationException>(() =>
            connection.RaiseBinaryMessageReceived(Frame(BinaryMessageTypes.VisualizerLoudness, 1, U16(100))));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RequestVisualizerFormatAsync_SendsRequestFormat()
    {
        var (client, connection) = VisualizerClient();
        using var _c = client;

        await client.RequestVisualizerFormatAsync(
            types: new List<string> { VisualizerTypes.Loudness },
            rateMax: 15,
            spectrum: new VisualizerSpectrum { NDispBins = 16, Scale = "mel", FMin = 30, FMax = 20000 });

        var msg = Assert.IsType<StreamRequestFormatMessage>(connection.SentMessages.Last());
        var vis = msg.Payload.Visualizer;
        Assert.NotNull(vis);
        Assert.Equal(new[] { VisualizerTypes.Loudness }, vis.Types);
        Assert.Equal(15, vis.RateMax);
        Assert.Equal(16, vis.Spectrum!.NDispBins);
    }
}
