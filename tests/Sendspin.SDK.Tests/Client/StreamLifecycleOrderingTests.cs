using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// <c>stream/start</c>, <c>stream/end</c> and <c>stream/clear</c> are handled off the receive
/// loop — the pipeline calls they make open and close an output device, and the receive loop must
/// not block on that — so the order they take effect in is not the order they arrived in unless
/// something makes it so.
/// </summary>
/// <remarks>
/// A track boundary sends <c>stream/end</c> then <c>stream/start</c> back to back. Dispatched
/// independently, the end's teardown could land after the start's build and leave the pipeline
/// stopped with a stream running on the server: silence until the next track. The reverse order
/// leaves a pipeline running for a stream that has ended. Each test here drives exactly that
/// interleaving through a held pipeline call rather than by timing.
/// </remarks>
public class StreamLifecycleOrderingTests
{
    private const string PlayerStreamStart =
        """{"type":"stream/start","payload":{"player":{"codec":"pcm","channels":2,"sample_rate":48000,"bit_depth":16}}}""";

    private const string StreamEnd =
        """{"type":"stream/end","payload":{"server_transmitted":1000}}""";

    private const string StreamClear =
        """{"type":"stream/clear","payload":{"server_transmitted":2000}}""";

    private static (SendspinClientService Client, FakeSendspinConnection Connection) PlayerClient(
        FakeAudioPipeline pipeline)
    {
        var connection = new FakeSendspinConnection();
        var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection,
            audioPipeline: pipeline);
        return (client, connection);
    }

    private static byte[] AudioFrame(long timestamp, params byte[] audio)
    {
        var buf = new byte[9 + audio.Length];
        buf[0] = BinaryMessageTypes.PlayerAudio0;
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(1, 8), timestamp);
        audio.CopyTo(buf, 9);
        return buf;
    }

    private static Task WithTimeout(Task task) => task.WaitAsync(TimeSpan.FromSeconds(30));

    private static TaskCompletionSource Hold() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task AnEndThenAStart_LeavesTheStreamRunning()
    {
        // The track boundary. Dispatched independently, the end's StopAsync finished after the
        // start's StartAsync and the pipeline was left stopped for a stream the server had
        // started — silent until the next stream/start.
        var pipe = new FakeAudioPipeline { HoldNextStop = Hold() };
        var (client, connection) = PlayerClient(pipe);
        using var _c = client;

        connection.RaiseTextMessageReceived(PlayerStreamStart);
        await WithTimeout(pipe.CallsCompleted(1));

        var held = pipe.HoldNextStop!;
        connection.RaiseTextMessageReceived(StreamEnd);
        await WithTimeout(pipe.StopEntered);

        // Delivered while the end is still tearing the pipeline down.
        connection.RaiseTextMessageReceived(PlayerStreamStart);

        held.SetResult();
        await WithTimeout(pipe.CallsCompleted(3));

        Assert.Equal(2, pipe.StartCalls.Count);
        Assert.Equal(new[] { "start", "stop", "start" }, pipe.CallLog);
    }

    [Fact]
    public async Task AStartThenAnEnd_LeavesTheStreamStopped()
    {
        var pipe = new FakeAudioPipeline { HoldNextStart = Hold() };
        var (client, connection) = PlayerClient(pipe);
        using var _c = client;

        var held = pipe.HoldNextStart!;
        connection.RaiseTextMessageReceived(PlayerStreamStart);
        await WithTimeout(pipe.StartEntered);

        // Delivered while the start is still initializing the output device.
        connection.RaiseTextMessageReceived(StreamEnd);

        held.SetResult();
        await WithTimeout(pipe.CallsCompleted(2));

        Assert.Equal(new[] { "start", "stop" }, pipe.CallLog);
    }

    [Fact]
    public async Task AStartThenAClear_ClearsTheStreamItStarted()
    {
        // stream/clear is a seek. Reaching the pipeline before the start it follows, it cleared
        // buffers that did not exist yet and the pre-seek audio played on regardless.
        var pipe = new FakeAudioPipeline { HoldNextStart = Hold() };
        var (client, connection) = PlayerClient(pipe);
        using var _c = client;

        var held = pipe.HoldNextStart!;
        connection.RaiseTextMessageReceived(PlayerStreamStart);
        await WithTimeout(pipe.StartEntered);

        connection.RaiseTextMessageReceived(StreamClear);

        held.SetResult();
        await WithTimeout(pipe.CallsCompleted(2));

        Assert.Equal(new[] { "start", "clear" }, pipe.CallLog);
    }

    [Fact]
    public async Task AChunkArrivingDuringAStart_StaysBehindTheQueuedOnes()
    {
        // ProcessAudioChunk is reachable from two threads: the receive loop hands chunks straight
        // to a ready pipeline, and the stream/start handler drains what queued before it. A chunk
        // arriving mid-start went in front of the queue, so the pipeline saw it out of order — and
        // both callers wrote through the pipeline's one decode scratch buffer at the same time.
        var pipe = new FakeAudioPipeline { IsReady = false, HoldNextStart = Hold() };
        var (client, connection) = PlayerClient(pipe);
        using var _c = client;

        var held = pipe.HoldNextStart!;
        connection.RaiseTextMessageReceived(PlayerStreamStart);
        await WithTimeout(pipe.StartEntered);

        // Arrives while the start is still opening the device: queued, because the pipeline has
        // not reported itself ready yet. (A chunk that arrived before the stream/start would
        // have been dropped by the handler — it was encoded for the previous stream, which this
        // start rebuilds the decoder away from.)
        connection.RaiseBinaryMessageReceived(AudioFrame(1_000, 0x01));

        // The decoder and ring exist part-way through a real StartAsync, so the pipeline reports
        // itself ready while the start is still running. The chunk arriving now must not be
        // handed straight over: the one before it is still waiting for the drain.
        pipe.IsReady = true;
        connection.RaiseBinaryMessageReceived(AudioFrame(2_000, 0x02));

        // Queued behind the start handler, so its Clear landing is proof that handler — drain
        // included — has finished, without polling for it.
        connection.RaiseTextMessageReceived(StreamClear);

        held.SetResult();
        await WithTimeout(pipe.CallsCompleted(2));

        Assert.Equal(
            new long[] { 1_000, 2_000 },
            pipe.Chunks.Select(c => c.ServerTimestamp).ToArray());
    }
}
