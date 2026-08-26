using Sendspin.SDK.Client;
using Sendspin.SDK.Models;
using Sendspin.SDK.Tests.Audio;

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

    private static (SendspinClientService Client, FakeSendspinConnection Connection, FakeAudioPipeline Pipeline)
        PlayerClient(FakeAudioPipeline? pipeline = null)
    {
        var pipe = pipeline ?? new FakeAudioPipeline();
        var (client, connection, _) = TestClient.Create(configure: options => options with
        {
            AudioPipeline = pipe,
            ClockSynchronizer = new ConvergedClockSynchronizer(),
        });
        return (client, connection, pipe);
    }

    private static Task WithTimeout(Task task) => task.WaitAsync(TimeSpan.FromSeconds(30));

    [Fact]
    public async Task AnEndThenAStart_LeavesTheStreamRunning()
    {
        // The track boundary. Dispatched independently, the end's StopAsync finished after the
        // start's StartAsync and the pipeline was left stopped for a stream the server had
        // started — silent until the next stream/start.
        var pipe = new FakeAudioPipeline { HoldNextStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var (client, connection, _) = PlayerClient(pipe);
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
        var pipe = new FakeAudioPipeline { HoldNextStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var (client, connection, _) = PlayerClient(pipe);
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
        var pipe = new FakeAudioPipeline { HoldNextStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var (client, connection, _) = PlayerClient(pipe);
        using var _c = client;

        var held = pipe.HoldNextStart!;
        connection.RaiseTextMessageReceived(PlayerStreamStart);
        await WithTimeout(pipe.StartEntered);

        connection.RaiseTextMessageReceived(StreamClear);

        held.SetResult();
        await WithTimeout(pipe.CallsCompleted(2));

        Assert.Equal(new[] { "start", "clear" }, pipe.CallLog);
    }
}
