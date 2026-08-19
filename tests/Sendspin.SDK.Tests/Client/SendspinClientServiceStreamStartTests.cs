using System.Buffers.Binary;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Client;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// A <c>stream/start</c> for a stream that is already running is a configuration update, not a
/// restart (#201). The client must not treat one as a fresh start: chunks queued for the running
/// stream stay queued for it instead of being purged, while every other <c>stream/start</c> —
/// cold, or a format change that rebuilds the decode chain — still drops what the previous stream
/// left behind. The message itself is reported to consumers either way.
/// </summary>
public class SendspinClientServiceStreamStartTests
{
    private const string RunningCodec = "pcm";
    private const int RunningSampleRate = 48_000;

    private static byte[] AudioFrame(long timestamp, byte[] encoded)
    {
        var frame = new byte[9 + encoded.Length];
        frame[0] = BinaryMessageTypes.PlayerAudio0;
        BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(1, 8), timestamp);
        encoded.CopyTo(frame, 9);
        return frame;
    }

    private static string StreamStartJson(int sampleRate) =>
        $$"""
        {
            "type": "stream/start",
            "payload": {
                "player": { "codec": "{{RunningCodec}}", "sample_rate": {{sampleRate}}, "channels": 2, "bit_depth": 16 }
            }
        }
        """;

    /// <summary>
    /// A client whose pipeline is mid-stream on 48 kHz stereo PCM but cannot take chunks directly,
    /// so anything arriving now queues — the state a stream/start has to classify correctly.
    /// </summary>
    private static (SendspinClientService Client, FakeSendspinConnection Connection, FakeAudioPipeline Pipeline)
        ClientWithRunningStream()
    {
        var pipeline = new FakeAudioPipeline
        {
            IsReady = false,
            CurrentFormat = new AudioFormat
            {
                Codec = RunningCodec,
                SampleRate = RunningSampleRate,
                Channels = 2,
                BitDepth = 16,
            },
        };

        var (client, connection, _) = TestClient.Create(configure: options => options with { AudioPipeline = pipeline });
        pipeline.SetState(AudioPipelineState.Playing);
        return (client, connection, pipeline);
    }

    [Fact]
    public void StreamStart_ReAnnouncingRunningFormat_DrainsQueuedChunksInsteadOfDroppingThem()
    {
        var (client, connection, pipeline) = ClientWithRunningStream();
        using var _c = client;

        connection.RaiseBinaryMessageReceived(AudioFrame(1_000, new byte[] { 1, 2, 3, 4 }));
        Assert.Empty(pipeline.Chunks);

        pipeline.IsReady = true;
        connection.RaiseTextMessageReceived(StreamStartJson(RunningSampleRate));

        var chunk = Assert.Single(pipeline.Chunks);
        Assert.Equal(1_000, chunk.ServerTimestamp);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, chunk.EncodedData);
    }

    [Fact]
    public void StreamStart_ReAnnouncingRunningFormat_DoesNotStopOrClearThePipeline()
    {
        var (client, connection, pipeline) = ClientWithRunningStream();
        using var _c = client;

        connection.RaiseTextMessageReceived(StreamStartJson(RunningSampleRate));

        // The pipeline applies the update in place (see IAudioPipeline.StartAsync); nothing here
        // may tear it down or dump its buffer.
        Assert.Equal(0, pipeline.StopCount);
        Assert.Equal(0, pipeline.ClearCount);
        Assert.Equal(RunningSampleRate, Assert.Single(pipeline.StartCalls).SampleRate);
    }

    [Fact]
    public void StreamStart_ReAnnouncingRunningFormat_StillReportsTheMessage()
    {
        var (client, connection, _) = ClientWithRunningStream();
        using var _c = client;

        StreamStartPayload? received = null;
        GroupState? group = null;
        client.StreamStartReceived += (_, payload) => received = payload;
        client.GroupStateChanged += (_, state) => group = state;

        connection.RaiseTextMessageReceived(StreamStartJson(RunningSampleRate));

        Assert.NotNull(received);
        Assert.Equal(RunningSampleRate, received.Format?.SampleRate);
        Assert.Same(received, client.LastStreamStart);
        Assert.Equal(PlaybackState.Playing, group?.PlaybackState);
    }

    [Fact]
    public void StreamStart_WithFormatChange_DropsChunksQueuedForThePreviousStream()
    {
        var (client, connection, pipeline) = ClientWithRunningStream();
        using var _c = client;

        connection.RaiseBinaryMessageReceived(AudioFrame(1_000, new byte[] { 1, 2, 3, 4 }));

        pipeline.IsReady = true;
        connection.RaiseTextMessageReceived(StreamStartJson(44_100));

        // A rate change rebuilds the decode chain, so the queued chunk is in a format the new
        // decoder cannot read.
        Assert.Empty(pipeline.Chunks);
        Assert.Equal(44_100, Assert.Single(pipeline.StartCalls).SampleRate);
    }

    [Fact]
    public void StreamStart_WhilePipelineIdle_DropsStaleChunksAndStartsCold()
    {
        var pipeline = new FakeAudioPipeline { IsReady = false };
        var (client, connection, _) = TestClient.Create(configure: options => options with { AudioPipeline = pipeline });
        using var _c = client;

        connection.RaiseBinaryMessageReceived(AudioFrame(1_000, new byte[] { 1, 2, 3, 4 }));

        pipeline.IsReady = true;
        connection.RaiseTextMessageReceived(StreamStartJson(RunningSampleRate));

        Assert.Empty(pipeline.Chunks);
        Assert.Single(pipeline.StartCalls);
    }
}
