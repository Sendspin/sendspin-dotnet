using Sendspin.SDK.Client;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// A live source delivers audio in real time from its first chunk, and the server schedules that
/// chunk only <c>min_buffer_ms</c> ahead (it extends toward <c>required_lead_time_ms</c> for
/// buffered sources only). The buffer pre-rolls playback by the host's output latency, so every
/// millisecond of that latency is lead spent before the chunk's timestamp: a WASAPI host with a
/// 100 ms prefill and the default 150 ms <c>min_buffer_ms</c> had about 50 ms left for the network
/// and the decoder, and real-time source audio cut out. The reported leads must carry the output
/// latency, from the first <c>client/state</c> on.
/// </summary>
public class SendspinClientServiceLiveLeadTests
{
    private static (SendspinClientService Client, FakeSendspinConnection Connection, FakeAudioPipeline Pipeline)
        PlayerClient(int expectedOutputLatencyMs)
    {
        var pipe = new FakeAudioPipeline();
        var (client, connection, _) = TestClient.Create(configure: options => options with
        {
            Capabilities = new ClientCapabilities
            {
                RequiredLeadTimeMs = 200,
                MinBufferMs = 150,
                ExpectedOutputLatencyMs = expectedOutputLatencyMs,
            },
            AudioPipeline = pipe,
            ClockSynchronizer = new ConvergedClockSynchronizer(),
        });
        return (client, connection, pipe);
    }

    [Fact]
    public async Task InitialState_AddsTheExpectedOutputLatencyToBothLeads()
    {
        var (client, connection, _) = PlayerClient(expectedOutputLatencyMs: 100);
        using var _c = client;

        TestClient.CompleteHandshake(connection, "player@v1");
        var player = await WaitForPlayerStateAsync(connection, _ => true);

        Assert.Equal(250, player.MinBufferMs);
        Assert.Equal(300, player.RequiredLeadTimeMs);
    }

    [Fact]
    public async Task NoLatencyKnown_ReportsTheConfiguredLeadsUnchanged()
    {
        var (client, connection, _) = PlayerClient(expectedOutputLatencyMs: 0);
        using var _c = client;

        TestClient.CompleteHandshake(connection, "player@v1");
        var player = await WaitForPlayerStateAsync(connection, _ => true);

        Assert.Equal(150, player.MinBufferMs);
        Assert.Equal(200, player.RequiredLeadTimeMs);
    }

    [Fact]
    public async Task MeasuredLatency_ReplacesTheExpectedOne_AndIsReportedAgain()
    {
        var (client, connection, pipeline) = PlayerClient(expectedOutputLatencyMs: 100);
        using var _c = client;

        TestClient.CompleteHandshake(connection, "player@v1");
        await WaitForPlayerStateAsync(connection, _ => true);

        // The first stream starts, the WASAPI client is initialized, and the real latency is read.
        pipeline.SetDetectedOutputLatency(120);
        var player = await WaitForPlayerStateAsync(connection, p => p.MinBufferMs == 270);

        Assert.Equal(270, player.MinBufferMs);
        Assert.Equal(320, player.RequiredLeadTimeMs);
    }

    [Fact]
    public async Task UnchangedLatency_IsNotReportedAgain()
    {
        var (client, connection, pipeline) = PlayerClient(expectedOutputLatencyMs: 100);
        using var _c = client;

        TestClient.CompleteHandshake(connection, "player@v1");
        await WaitForPlayerStateAsync(connection, _ => true);
        pipeline.SetDetectedOutputLatency(120);
        await WaitForPlayerStateAsync(connection, p => p.MinBufferMs == 270);
        var sent = connection.SnapshotSentMessages().OfType<ClientStateMessage>().Count();

        // Every later stream start reports the same latency; the server already has it.
        pipeline.SetDetectedOutputLatency(120);
        pipeline.SetDetectedOutputLatency(120);
        await Task.Delay(100);

        Assert.Equal(sent, connection.SnapshotSentMessages().OfType<ClientStateMessage>().Count());
    }

    private static async Task<PlayerStatePayload> WaitForPlayerStateAsync(
        FakeSendspinConnection connection, Func<PlayerStatePayload, bool> match)
    {
        // The client sends state from fire-and-forget continuations; poll briefly for it.
        for (var i = 0; i < 100; i++)
        {
            var player = connection.SnapshotSentMessages().OfType<ClientStateMessage>()
                .Select(m => m.Payload.Player)
                .LastOrDefault(p => p is not null && match(p));
            if (player is not null)
            {
                return player;
            }

            await Task.Delay(10);
        }

        var sent = string.Join("; ", connection.SnapshotSentMessages().Select(m =>
            m is ClientStateMessage c
                ? $"client/state(available={c.Payload.Available}, player={(c.Payload.Player is { } p ? $"lead={p.RequiredLeadTimeMs},min={p.MinBufferMs}" : "null")})"
                : m.GetType().Name));
        throw new TimeoutException($"No matching client/state with a player object was sent. Sent: [{sent}]");
    }
}
