using System.Buffers.Binary;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Once the client has decided to close, nothing the peer sends may still take effect. The
/// connection keeps delivering frames until the socket is actually closed, and the text path
/// has always dropped them; these cover the binary path (#332).
/// </summary>
public class BinaryAfterCloseTests
{
    private static byte[] AudioFrame(long timestamp)
    {
        var frame = new byte[13 + 4];
        frame[0] = BinaryMessageTypes.PlayerAudio0;
        BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(1, 8), timestamp);
        return frame;
    }

    private static (SendspinClientService Client, FakeSendspinConnection Connection, FakeAudioPipeline Pipeline)
        StreamingClient()
    {
        var pipe = new FakeAudioPipeline();
        var (client, connection, _) = TestClient.Create(activated: true, configure: options => options with
        {
            AudioPipeline = pipe,
            ClockSynchronizer = new ConvergedClockSynchronizer(),
        });
        return (client, connection, pipe);
    }

    private static async Task StartStreamAsync(FakeSendspinConnection connection, FakeAudioPipeline pipe)
    {
        connection.RaiseTextMessageReceived("""
            {"type":"stream/start","payload":{"player":{"codec":"pcm","sample_rate":48000,"channels":2,"bit_depth":16}}}
            """);
        await pipe.CallsCompleted(1).WaitAsync(TimeSpan.FromSeconds(5));

        // Positive control: on a live connection the chunk reaches the pipeline.
        connection.RaiseBinaryMessageReceived(AudioFrame(5_000));
        Assert.Single(pipe.Chunks);
    }

    [Fact]
    public async Task Audio_WhileTheCloseIsInFlight_IsDropped()
    {
        var (client, connection, pipe) = StreamingClient();
        using var _c = client;
        await StartStreamAsync(connection, pipe);

        connection.SimulateClosing();
        connection.RaiseBinaryMessageReceived(AudioFrame(6_000));

        Assert.Single(pipe.Chunks);
    }

    [Fact]
    public void ArtworkSequenceError_WhileTheCloseIsInFlight_DoesNotStartASecondClose()
    {
        var (client, connection, _) = StreamingClient();
        using var _c = client;

        connection.SimulateClosing();

        // A part with no transfer in flight: on a live connection, a protocol error the client
        // closes over with 'unauthorized'.
        connection.RaiseBinaryMessageReceived(ArtworkWire.Part(BinaryMessageTypes.Artwork0, 1, 2));

        Assert.Null(connection.LastDisconnectReason);
        Assert.Equal(ConnectionState.Disconnecting, connection.State);
    }

    [Fact]
    public async Task Binary_AfterServerUnpair_IsDropped_AndTheGoodbyeReasonStands()
    {
        var (client, connection, pipe) = StreamingClient();
        using var _c = client;
        await StartStreamAsync(connection, pipe);

        connection.RaiseTextMessageReceived("""{"type":"server/unpair","payload":{}}""");
        Assert.Equal("unpaired", connection.LastDisconnectReason);

        connection.RaiseBinaryMessageReceived(AudioFrame(6_000));
        connection.RaiseBinaryMessageReceived(ArtworkWire.Part(BinaryMessageTypes.Artwork0, 1, 2));

        Assert.Single(pipe.Chunks);
        Assert.Equal("unpaired", connection.LastDisconnectReason);
    }
}
