using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Synchronization;

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
    private const string HelloJson = """
        { "type": "server/hello", "payload": { "server_id": "srv-1", "version": 1, "active_roles": ["player@v1"] } }
        """;

    [Fact]
    public async Task InitialState_AddsTheExpectedOutputLatencyToBothLeads()
    {
        var connection = new FakeSendspinConnection();
        var pipeline = new FakeAudioPipeline();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection,
            new KalmanClockSynchronizer(),
            new ClientCapabilities { RequiredLeadTimeMs = 200, MinBufferMs = 150, ExpectedOutputLatencyMs = 100 },
            audioPipeline: pipeline);

        connection.RaiseTextMessageReceived(HelloJson);
        var player = await WaitForPlayerStateAsync(connection, _ => true);

        Assert.Equal(250, player.MinBufferMs);
        Assert.Equal(300, player.RequiredLeadTimeMs);
    }

    [Fact]
    public async Task NoLatencyKnown_ReportsTheConfiguredLeadsUnchanged()
    {
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection,
            new KalmanClockSynchronizer(),
            new ClientCapabilities { RequiredLeadTimeMs = 200, MinBufferMs = 150 },
            audioPipeline: new FakeAudioPipeline());

        connection.RaiseTextMessageReceived(HelloJson);
        var player = await WaitForPlayerStateAsync(connection, _ => true);

        Assert.Equal(150, player.MinBufferMs);
        Assert.Equal(200, player.RequiredLeadTimeMs);
    }

    [Fact]
    public async Task MeasuredLatency_ReplacesTheExpectedOne_AndIsReportedAgain()
    {
        var connection = new FakeSendspinConnection();
        var pipeline = new FakeAudioPipeline();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection,
            new KalmanClockSynchronizer(),
            new ClientCapabilities { RequiredLeadTimeMs = 200, MinBufferMs = 150, ExpectedOutputLatencyMs = 100 },
            audioPipeline: pipeline);

        await connection.ConnectAsync(new Uri("ws://test"));
        connection.RaiseTextMessageReceived(HelloJson);
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
        var connection = new FakeSendspinConnection();
        var pipeline = new FakeAudioPipeline();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection,
            new KalmanClockSynchronizer(),
            new ClientCapabilities { RequiredLeadTimeMs = 200, MinBufferMs = 150, ExpectedOutputLatencyMs = 100 },
            audioPipeline: pipeline);

        await connection.ConnectAsync(new Uri("ws://test"));
        connection.RaiseTextMessageReceived(HelloJson);
        await WaitForPlayerStateAsync(connection, _ => true);
        pipeline.SetDetectedOutputLatency(120);
        await WaitForPlayerStateAsync(connection, p => p.MinBufferMs == 270);
        var sent = connection.SentMessages.OfType<ClientStateMessage>().Count();

        // Every later stream start reports the same latency; the server already has it.
        pipeline.SetDetectedOutputLatency(120);
        pipeline.SetDetectedOutputLatency(120);
        await Task.Delay(100);

        Assert.Equal(sent, connection.SentMessages.OfType<ClientStateMessage>().Count());
    }

    private static async Task<PlayerStatePayload> WaitForPlayerStateAsync(
        FakeSendspinConnection connection, Func<PlayerStatePayload, bool> match)
    {
        // The client sends state from fire-and-forget continuations; poll briefly for it.
        for (var i = 0; i < 100; i++)
        {
            var player = connection.SentMessages.OfType<ClientStateMessage>()
                .Select(m => m.Payload.Player)
                .LastOrDefault(p => p is not null && match(p));
            if (player is not null)
            {
                return player;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("No matching client/state with a player object was sent.");
    }
}
