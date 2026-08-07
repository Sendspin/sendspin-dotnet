using Sendspin.SDK.Audio;
using Sendspin.SDK.Client;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Tests.Audio;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Availability is a single computed value — <c>!IsExternalSource &amp;&amp; !pipelineErrored</c>
/// (clock sync gates only the initial client/state, not ongoing availability) — published only
/// when it changes, rather than call sites each asserting it independently (the shape that let
/// the <c>SendPlayerStateAsync</c> defect happen). These tests exercise the composition and the
/// publish-on-change suppression that a set of independent booleans could not provide.
/// </summary>
public class ClientAvailabilityTests
{
    /// <summary>Clock already converged, so external-source/error transitions aren't confounded
    /// by the default (unconverged) clock-sync gate on a default player@v1 client.</summary>
    private static (SendspinClientService Client, FakeSendspinConnection Connection, FakeAudioPipeline Pipeline)
        SyncedClient()
    {
        var pipe = new FakeAudioPipeline();
        var (client, connection, _) = TestClient.Create(configure: options =>
        {
            options.AudioPipeline = pipe;
            options.ClockSynchronizer = new FakeClockSynchronizer { IsConverged = true };
        });
        return (client, connection, pipe);
    }

    private static IReadOnlyList<bool?> AvailableValuesSent(FakeSendspinConnection connection) =>
        connection.SentMessages.OfType<ClientStateMessage>().Select(m => m.Payload.Available).ToList();

    [Fact]
    public async Task EnterThenExitExternalSource_SendsFalseThenTrue()
    {
        var (client, connection, _) = SyncedClient();
        using var _c = client;

        await client.EnterExternalSourceAsync();
        await client.ExitExternalSourceAsync();

        Assert.Equal(new bool?[] { false, true }, AvailableValuesSent(connection));
    }

    [Fact]
    public async Task PipelineError_ThenRecoveryToPlaying_SendsFalseThenTrue()
    {
        var (client, connection, pipe) = SyncedClient();
        using var _c = client;

        pipe.RaiseError();
        pipe.SetState(AudioPipelineState.Playing);

        // Filter out the player-state ack the Playing recovery also sends (Available: null) —
        // only the availability deltas matter here.
        var availabilityDeltas = AvailableValuesSent(connection).Where(a => a.HasValue).ToList();
        Assert.Equal(new bool?[] { false, true }, availabilityDeltas);
    }

    [Fact]
    public async Task RepeatedPipelineErrors_SendExactlyOneAvailableFalse()
    {
        var (client, connection, pipe) = SyncedClient();
        using var _c = client;

        // The pipeline can genuinely raise more than one error notification for the same
        // underlying condition; only the publisher's compare-to-last-sent — not a gate on
        // OnPipelineError's entry — stops these from becoming two wire messages.
        pipe.RaiseError();
        pipe.RaiseError();

        Assert.Single(AvailableValuesSent(connection), a => a == false);
    }

    [Fact]
    public async Task ExitExternalSource_WithPipelineErrorOutstanding_DoesNotPublishAvailableTrue()
    {
        var (client, connection, pipe) = SyncedClient();
        using var _c = client;

        await client.EnterExternalSourceAsync();
        pipe.RaiseError();
        await client.ExitExternalSourceAsync();

        // Composition, not three independent booleans: leaving external source does not make the
        // client available again while the pipeline error is still outstanding.
        Assert.DoesNotContain(true, AvailableValuesSent(connection));
        Assert.False(client.IsExternalSource);
    }

    [Fact]
    public async Task DeltaSentWhileInitialInFlight_NextGenuineChangeIsStillPublished()
    {
        // The initial client/state seeds the publisher's last-sent tracker. Seeded after the
        // send's await, a delta publishing while the initial was in flight had its fresher
        // tracker value overwritten by the stale seed — and the next genuine change was then
        // suppressed as a repeat while the server believed the delta's value, with nothing
        // scheduled to ever correct the divergence.
        var (client, connection, _) = SyncedClient();
        using var _c = client;

        // Hold the promoted initial (available: false) in flight.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.HoldNextSend = gate;
        var enterTask = client.EnterExternalSourceAsync();

        // While it is in flight, a delta publishes: exit reports available: true.
        await client.ExitExternalSourceAsync();

        // The initial's send completes after the delta went out; its seed must not clobber
        // the tracker the delta has since written.
        gate.SetResult();
        await enterTask;

        // The next genuine false must reach the wire, not be suppressed as a repeat.
        await client.EnterExternalSourceAsync();

        Assert.Equal(new bool?[] { false, true, false }, AvailableValuesSent(connection));
    }

    [Fact]
    public async Task VolumeChangeWhileExternalSource_DeltaOmitsAvailable()
    {
        var (client, connection, _) = SyncedClient();
        using var _c = client;

        await client.EnterExternalSourceAsync();
        await client.SendPlayerStateAsync(volume: 42, muted: false);

        var last = connection.SentMessages.OfType<ClientStateMessage>().Last();
        Assert.NotNull(last.Payload.Player);
        Assert.Null(last.Payload.Available);
    }
}
