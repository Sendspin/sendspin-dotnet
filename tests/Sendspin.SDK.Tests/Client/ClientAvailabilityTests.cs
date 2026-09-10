using Sendspin.SDK.Audio;
using Sendspin.SDK.Client;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Tests.Audio;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Availability is a single computed value — <c>(!RequiresClockSync || ClockSyncEstablished)
/// &amp;&amp; !IsExternalSource &amp;&amp; !pipelineErrored</c>, where ClockSyncEstablished is
/// latched at the connection's first convergence rather than tracking the live statistic —
/// published only when it changes, rather than call sites each asserting it independently (the
/// shape that let the <c>SendPlayerStateAsync</c> defect happen). These tests exercise the
/// composition and the publish-on-change suppression that a set of independent booleans could
/// not provide.
/// </summary>
public class ClientAvailabilityTests
{
    /// <summary>Clock already converged, so external-source/error transitions aren't confounded
    /// by the default (unconverged) clock-sync gate on a default player@v1 client.</summary>
    private static (SendspinClientService Client, FakeSendspinConnection Connection, FakeAudioPipeline Pipeline)
        SyncedClient()
    {
        var pipe = new FakeAudioPipeline();
        var (client, connection, _) = TestClient.Create(configure: options => options with
        {
            AudioPipeline = pipe,
            ClockSynchronizer = new FakeClockSynchronizer { IsConverged = true },
        });
        return (client, connection, pipe);
    }

    private static IReadOnlyList<bool?> AvailableValuesSent(FakeSendspinConnection connection) =>
        connection.SentMessages.OfType<ClientStateMessage>().Select(m => (bool?)m.Payload.Available).ToList();

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

        // Every client/state carries available since spec PR #175, and the Playing recovery also
        // sends a player-state acknowledgement — so collapse consecutive repeats rather than
        // filtering on a null that no longer occurs. What matters is the sequence of values the
        // server is told, not how many messages carried them.
        var transitions = AvailableValuesSent(connection)
            .Aggregate(new List<bool?>(), (acc, a) => { if (acc.Count == 0 || acc[^1] != a) acc.Add(a); return acc; });
        Assert.Equal(new bool?[] { false, true }, transitions);
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
    public async Task VolumeChangeWhileExternalSource_ReportsTheComposedAvailability_NotTrue()
    {
        // Since spec PR #175 every client/state carries available, so a volume change can no
        // longer stay silent about it — but it must report the composed value. Asserting true
        // here would be the §4 defect in its new form: a volume change silently telling the
        // server the output is free while an external source holds it.
        var (client, connection, _) = SyncedClient();
        using var _c = client;

        await client.EnterExternalSourceAsync();
        await client.SendPlayerStateAsync(volume: 42, muted: false);

        var last = connection.SentMessages.OfType<ClientStateMessage>().Last();
        Assert.NotNull(last.Payload.Player);
        Assert.Equal(42, last.Payload.Player.Volume);
        Assert.False(last.Payload.Available);
    }
}
