using System.Buffers.Binary;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Client;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Tests.Audio;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Audio chunks against the lifecycle messages around them. A chunk is handled on the receive
/// loop, while a <c>stream/start</c>, <c>stream/end</c> or <c>stream/clear</c> that arrives with
/// an earlier one still opening or closing the output device waits its turn — so the chunks
/// behind such a message reached the pipeline ahead of it, under the configuration it replaces.
/// </summary>
/// <remarks>
/// Each test holds a pipeline start or stop open, as a device does, and reads the one sequence
/// the pipeline saw. The player role: "Clients MUST keep buffered chunks and decode each chunk in
/// the format that was in effect when it was received", and for <c>stream/clear</c>, "clients MUST
/// clear all buffered audio chunks and continue with chunks received after this message".
/// </remarks>
public class StreamLifecycleAudioChunkTests
{
    private const string PlayerStreamStart =
        """{"type":"stream/start","payload":{"player":{"codec":"pcm","channels":2,"sample_rate":48000,"bit_depth":16}}}""";

    private const string DisplayOnlyStreamStart =
        """{"type":"stream/start","payload":{}}""";

    private const string StreamEnd =
        """{"type":"stream/end","payload":{"server_transmitted":1000}}""";

    private const string StreamClear =
        """{"type":"stream/clear","payload":{"server_transmitted":2000}}""";

    private static (SendspinClientService Client, FakeSendspinConnection Connection) PlayerClient(FakeAudioPipeline pipe)
    {
        var (client, connection, _) = TestClient.Create(activated: true, configure: options => options with
        {
            AudioPipeline = pipe,
            ClockSynchronizer = new ConvergedClockSynchronizer(),
        });
        return (client, connection);
    }

    private static byte[] AudioFrame(long timestamp)
    {
        // Player audio chunk header: type + timestamp + send_ahead (13 bytes), audio from byte 13.
        var buf = new byte[14];
        buf[0] = BinaryMessageTypes.PlayerAudio0;
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(1, 8), timestamp);
        return buf;
    }

    private static TaskCompletionSource Hold() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task WithTimeout(Task task) => task.WaitAsync(TimeSpan.FromSeconds(30));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChunksBehindAnEndAndAStart_GoToTheStreamThatStartStarted(bool readyWhileStopping)
    {
        // stop -> play, and any server that ends the stream between tracks: stream/end,
        // stream/start, then the new stream's opening burst, all inside the time the output
        // device takes to close. The burst went to the pipeline being torn down — the head of
        // the track, decoded by the outgoing decoder into a ring about to be cleared.
        var pipe = new FakeAudioPipeline { HoldNextStop = Hold() };
        var (client, connection) = PlayerClient(pipe);
        using var _c = client;

        connection.RaiseTextMessageReceived(PlayerStreamStart);
        await WithTimeout(pipe.CallsCompleted(1));

        var held = pipe.HoldNextStop!;
        connection.RaiseTextMessageReceived(StreamEnd);
        await WithTimeout(pipe.StopEntered);

        // A pipeline that stops reporting ready as soon as it starts closing queues these
        // instead; they are still the new stream's, and the start must not drop them as left
        // over from the old one.
        pipe.IsReady = readyWhileStopping;

        connection.RaiseTextMessageReceived(PlayerStreamStart);
        connection.RaiseBinaryMessageReceived(AudioFrame(1_000));
        connection.RaiseBinaryMessageReceived(AudioFrame(2_000));

        pipe.IsReady = true;
        held.SetResult();
        await WithTimeout(pipe.CallsCompleted(3));

        Assert.Equal(new[] { "start", "stop", "start", "chunk@1000", "chunk@2000" }, pipe.Timeline);
    }

    [Fact]
    public async Task ChunksBehindAQueuedStart_WaitForTheFormatItBrings()
    {
        var pipe = new FakeAudioPipeline { HoldNextStart = Hold() };
        var (client, connection) = PlayerClient(pipe);
        using var _c = client;

        var held = pipe.HoldNextStart!;
        connection.RaiseTextMessageReceived(PlayerStreamStart);
        await WithTimeout(pipe.StartEntered);

        // Received under the first start, whose decoder and ring exist while its device opens:
        // handed over at once, as before.
        connection.RaiseBinaryMessageReceived(AudioFrame(1_000));

        // A second start, queued behind the first; what follows it is in its format.
        pipe.StartOutcome = AudioPipelineStartOutcome.DecoderReplaced;
        connection.RaiseTextMessageReceived(PlayerStreamStart);
        connection.RaiseBinaryMessageReceived(AudioFrame(2_000));

        held.SetResult();
        await WithTimeout(pipe.CallsCompleted(2));

        Assert.Equal(new[] { "chunk@1000", "start", "start", "chunk@2000" }, pipe.Timeline);
    }

    [Fact]
    public async Task ChunksBehindAQueuedClear_AreNotClearedByIt()
    {
        // A seek while the stream is still starting. The clear ran once the start had finished,
        // after the post-seek chunks had been written, and discarded them with the rest.
        var pipe = new FakeAudioPipeline { HoldNextStart = Hold() };
        var (client, connection) = PlayerClient(pipe);
        using var _c = client;

        var held = pipe.HoldNextStart!;
        connection.RaiseTextMessageReceived(PlayerStreamStart);
        await WithTimeout(pipe.StartEntered);

        connection.RaiseBinaryMessageReceived(AudioFrame(1_000));
        connection.RaiseTextMessageReceived(StreamClear);
        connection.RaiseBinaryMessageReceived(AudioFrame(2_000));

        // A second seek: the chunk between the two is the first one's, and is cleared by the
        // second only after it has been handed over.
        connection.RaiseTextMessageReceived(StreamClear);
        connection.RaiseBinaryMessageReceived(AudioFrame(3_000));

        held.SetResult();
        await WithTimeout(pipe.CallsCompleted(3));

        Assert.Equal(
            new[] { "chunk@1000", "start", "clear", "chunk@2000", "clear", "chunk@3000" },
            pipe.Timeline);
    }

    [Fact]
    public async Task AQueuedStartWithNoPlayerObject_DoesNotHoldAudioBack()
    {
        // A server starts the display roles' streams alongside the player's. Their stream/start
        // queues behind the player's device open like any other, but it changes nothing about
        // the audio, and the opening burst held behind it would overflow the early-chunk queue.
        var pipe = new FakeAudioPipeline { HoldNextStart = Hold() };
        var (client, connection) = PlayerClient(pipe);
        using var _c = client;

        var held = pipe.HoldNextStart!;
        connection.RaiseTextMessageReceived(PlayerStreamStart);
        await WithTimeout(pipe.StartEntered);

        connection.RaiseTextMessageReceived(DisplayOnlyStreamStart);
        connection.RaiseBinaryMessageReceived(AudioFrame(1_000));

        Assert.Equal(new[] { "chunk@1000" }, pipe.Timeline);

        held.SetResult();
        await WithTimeout(pipe.CallsCompleted(1));
    }
}
