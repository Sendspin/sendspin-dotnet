using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Client;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// The client reports client/state: 'error' when the audio pipeline can't keep up (underrun / sync
/// failure) and client/state: 'synchronized' once it recovers, per the spec.
/// </summary>
public class SendspinClientServiceErrorStateTests
{
    private static async Task<(FakeSendspinConnection conn, FakeAudioPipeline pipe, SendspinClientService client)> ConnectedClientAsync()
    {
        var conn = new FakeSendspinConnection();
        var pipe = new FakeAudioPipeline();
        var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance, conn, audioPipeline: pipe);
        await conn.ConnectAsync(new Uri("ws://test"));
        return (conn, pipe, client);
    }

    private static IEnumerable<string?> StatesSent(FakeSendspinConnection conn) =>
        conn.SentMessages.OfType<ClientStateMessage>().Select(m => m.Payload.State);

    [Fact]
    public async Task PipelineError_ReportsClientStateError()
    {
        var (conn, pipe, client) = await ConnectedClientAsync();
        using (client)
        {
            pipe.RaiseError("buffer underrun");
            Assert.Contains("error", StatesSent(conn));
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

            Assert.Single(StatesSent(conn), s => s == "error");
        }
    }

    [Fact]
    public async Task RecoveryToPlaying_ReportsSynchronized()
    {
        var (conn, pipe, client) = await ConnectedClientAsync();
        using (client)
        {
            pipe.RaiseError();
            pipe.SetState(AudioPipelineState.Playing); // recovered

            Assert.Contains("synchronized", StatesSent(conn));
        }
    }

    [Fact]
    public async Task RecoveryWhileDisconnected_DoesNotReportSynchronized()
    {
        var (conn, pipe, client) = await ConnectedClientAsync();
        using (client)
        {
            pipe.RaiseError();                          // error reported while connected
            await conn.DisconnectAsync();               // connection drops
            pipe.SetState(AudioPipelineState.Playing);  // pipeline recovers while disconnected

            // The recovery report is guarded on connection state, like the error path; the
            // reconnect handshake would re-report synchronized.
            Assert.DoesNotContain("synchronized", StatesSent(conn));
        }
    }

    [Fact]
    public async Task PlayingWithoutPriorError_DoesNotReportSynchronized()
    {
        var (conn, pipe, client) = await ConnectedClientAsync();
        using (client)
        {
            // Normal first playback — no error was reported, so no redundant synchronized message.
            pipe.SetState(AudioPipelineState.Playing);

            Assert.DoesNotContain("synchronized", StatesSent(conn));
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

            // ReportClientErrorAsync guards on connection state, so the error report is skipped.
            // The default (non-throwing) fake is deliberate: a removed guard would record the
            // message and fail this assertion, rather than being masked by an enforced throw.
            Assert.DoesNotContain("error", StatesSent(conn));
        }
    }
}
