using Sendspin.SDK.Audio;
using Sendspin.SDK.Client;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// The client reports available: false when the audio pipeline can't keep up (underrun / sync
/// failure), per the spec. Recovery re-sends the player-state object via
/// <c>SendPlayerStateAckAsync</c> -&gt; <c>CreatePlayerState</c>, which deliberately omits
/// <c>available</c> (see the §4 fix), and separately calls the availability publisher so a
/// recovery can re-assert <c>available: true</c> once nothing else keeps the client unavailable.
/// This fixture never completes a handshake or establishes clock sync: the first error
/// therefore promotes the full initial client/state (carrying the genuine available: false),
/// and — since clock sync gates only the initial message, never ongoing availability — the
/// recovery's available: true goes out immediately rather than waiting for a convergence that
/// never comes (<c>ClientAvailabilityTests</c> covers the composition itself).
/// </summary>
public class SendspinClientServiceErrorStateTests
{
    private static async Task<(FakeSendspinConnection conn, FakeAudioPipeline pipe, SendspinClientService client)> ConnectedClientAsync()
    {
        var pipe = new FakeAudioPipeline();
        var (client, conn, _) = TestClient.Create(configure: options => options.AudioPipeline = pipe);
        await conn.ConnectAsync(new Uri("ws://test"));
        return (conn, pipe, client);
    }

    private static IEnumerable<bool?> AvailableValuesSent(FakeSendspinConnection conn) =>
        conn.SentMessages.OfType<ClientStateMessage>().Select(m => m.Payload.Available);

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
    public async Task RecoveryToPlaying_SendsAvailableTrue_ThenPlayerStateAckWithoutAvailable()
    {
        var (conn, pipe, client) = await ConnectedClientAsync();
        using (client)
        {
            pipe.RaiseError();
            pipe.SetState(AudioPipelineState.Playing); // recovered

            var messages = conn.SentMessages.OfType<ClientStateMessage>().ToList();

            // messages[0] is the error report (the promoted full initial, available: false).
            // Recovery calls both the availability publisher and the player-state ack: the
            // publisher re-asserts available: true as a bare delta — clock sync gates only the
            // initial message, so this fixture's never-converging clock no longer suppresses
            // the recovery report, which under the old composition left the server believing
            // the client unavailable forever — and the ack then carries the player object with
            // no `available` (the §4 fix).
            Assert.Equal(3, messages.Count);
            Assert.Equal(true, messages[1].Payload.Available);
            Assert.Null(messages[1].Payload.Player);
            Assert.NotNull(messages[2].Payload.Player);
            Assert.Null(messages[2].Payload.Available);
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
