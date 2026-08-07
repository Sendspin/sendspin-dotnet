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
/// This fixture never establishes clock sync, so the composed availability
/// (<c>ClientAvailabilityTests</c> covers the formula) stays <c>false</c> throughout regardless of
/// the pipeline's error/recovery state — the tests below still hold, but see each one's comment
/// for what it actually proves under that constraint.
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
    public async Task RecoveryToPlaying_SendsPlayerStateAck_ButNotAvailability()
    {
        var (conn, pipe, client) = await ConnectedClientAsync();
        using (client)
        {
            pipe.RaiseError();
            pipe.SetState(AudioPipelineState.Playing); // recovered

            var messages = conn.SentMessages.OfType<ClientStateMessage>().ToList();

            // messages[0] is the error report (available: false). Recovery calls both the
            // availability publisher and the player-state ack, but this fixture's clock never
            // converges, so the publisher's computed value is still false and its own
            // compare-to-last-sent suppresses a second wire message — leaving messages[1] as the
            // player-state ack, which carries the player object but no `available`
            // (ClientAvailabilityTests exercises the synced-clock case, where recovery does send a
            // second message with available: true).
            Assert.Equal(2, messages.Count);
            Assert.NotNull(messages[1].Payload.Player);
            Assert.Null(messages[1].Payload.Available);
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
