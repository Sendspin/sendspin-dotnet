using Sendspin.SDK.Audio;
using Sendspin.SDK.Client;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// The client reports available: false when the audio pipeline can't keep up (underrun / sync
/// failure), per the spec. Recovery re-sends the player-state object via
/// <c>SendPlayerStateAckAsync</c>, which — like every other state send since spec PR #175 —
/// goes through the one construction path and therefore carries <c>available</c> plus the full
/// state of every active role. It separately calls the availability publisher so a recovery can
/// re-assert <c>available: true</c> once nothing else keeps the client unavailable.
/// This fixture never completes a handshake or establishes clock sync: the first error
/// therefore promotes the full initial client/state (carrying the genuine available: false),
/// and a recovery's available: true is withheld — availability composes the per-connection
/// ClockSyncEstablished latch, which this fixture never sets, and the spec ties a player's
/// true to a synchronized clock (<c>ClientAvailabilityTests</c> covers the composition
/// itself; the gating suite covers the release of a withheld true at first convergence).
/// </summary>
public class SendspinClientServiceErrorStateTests
{
    private static async Task<(FakeSendspinConnection conn, FakeAudioPipeline pipe, SendspinClientService client)> ConnectedClientAsync()
    {
        var pipe = new FakeAudioPipeline();
        var (client, conn, _) = TestClient.Create(configure: options => options with { AudioPipeline = pipe });
        await conn.ConnectAsync(new Uri("ws://test"));
        return (conn, pipe, client);
    }

    private static IEnumerable<bool?> AvailableValuesSent(FakeSendspinConnection conn) =>
        conn.SentMessages.OfType<ClientStateMessage>().Select(m => (bool?)m.Payload.Available);

    [Fact]
    public async Task PipelineError_ReportsAvailableFalse()
    {
        var (conn, pipe, client) = await ConnectedClientAsync();
        using (client)
        {
            pipe.RaiseError("buffer underrun");
            Assert.Contains(false, AvailableValuesSent(conn));
        }
    }

    [Fact]
    public async Task DuplicateErrors_ReportErrorOnce()
    {
        var (conn, pipe, client) = await ConnectedClientAsync();
        using (client)
        {
            pipe.RaiseError();
            pipe.RaiseError();
            pipe.SetState(AudioPipelineState.Error); // also surfaces error, must still dedupe

            Assert.Single(AvailableValuesSent(conn), a => a == false);
        }
    }

    [Fact]
    public async Task RecoveryToPlaying_BeforeSyncEstablished_WithholdsAvailableTrue_SendsAckOnly()
    {
        var (conn, pipe, client) = await ConnectedClientAsync();
        using (client)
        {
            pipe.RaiseError();
            pipe.SetState(AudioPipelineState.Playing); // recovered

            var messages = conn.SentMessages.OfType<ClientStateMessage>().ToList();

            // messages[0] is the error report (the full initial state, available: false).
            // Recovery calls both the availability publisher and the player-state ack, but
            // this fixture's clock never converges, so ClockSyncEstablished is unset and the
            // recovery's available: true is withheld — the spec ties a player's true to a
            // synchronized clock, and the first convergence would release it (the gating
            // suite's RecoveryBeforeFirstConvergence test covers that release; the synced
            // recovery path is ClientAvailabilityTests'). messages[1] is therefore the
            // player-state acknowledgement, which since spec PR #175 carries `available` too —
            // but the composed value, still false, not an asserted true (the §4 fix).
            Assert.Equal(2, messages.Count);
            Assert.NotNull(messages[1].Payload.Player);
            Assert.False(messages[1].Payload.Available);
        }
    }

    [Fact]
    public async Task RecoveryWhileDisconnected_DoesNotSendPlayerStateAck()
    {
        var (conn, pipe, client) = await ConnectedClientAsync();
        using (client)
        {
            pipe.RaiseError();                          // error reported while connected
            var countAfterError = conn.SentMessages.OfType<ClientStateMessage>().Count();
            await conn.DisconnectAsync();               // connection drops
            pipe.SetState(AudioPipelineState.Playing);  // pipeline recovers while disconnected

            // The recovery report is guarded on connection state, like the error path; the
            // reconnect handshake would re-report state via SendInitialClientStateAsync.
            Assert.Equal(countAfterError, conn.SentMessages.OfType<ClientStateMessage>().Count());
        }
    }

    [Fact]
    public async Task PlayingWithoutPriorError_DoesNotSendPlayerStateAck()
    {
        var (conn, pipe, client) = await ConnectedClientAsync();
        using (client)
        {
            // Normal first playback — no error was reported, so no redundant player-state report.
            pipe.SetState(AudioPipelineState.Playing);

            Assert.Empty(conn.SentMessages.OfType<ClientStateMessage>());
        }
    }

    [Fact]
    public async Task PipelineErrorWhileDisconnected_DoesNotReportError()
    {
        var (conn, pipe, client) = await ConnectedClientAsync();
        using (client)
        {
            await conn.DisconnectAsync();           // connection drops before the pipeline fails
            pipe.RaiseError("buffer underrun");     // error surfaces while disconnected

            // PublishAvailabilityAsync guards on connection state, so the error report is skipped.
            // The default (non-throwing) fake is deliberate: a removed guard would record the
            // message and fail this assertion, rather than being masked by an enforced throw.
            Assert.DoesNotContain(false, AvailableValuesSent(conn));
        }
    }
}
