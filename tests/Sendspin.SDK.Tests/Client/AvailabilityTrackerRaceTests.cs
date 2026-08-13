using Sendspin.SDK.Audio;
using Sendspin.SDK.Client;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Tests.Audio;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// The availability publisher claims its transition before sending, so a publish that starts
/// while another is in flight compares against a current value rather than a stale one (#114
/// item 2).
/// </summary>
/// <remarks>
/// Deterministic, not timing-dependent: <see cref="FakeSendspinConnection.HoldNextSend"/> parks
/// the first send until the test releases it, so the second publish provably runs while the
/// first is mid-flight. Nothing here sleeps or races the scheduler.
/// </remarks>
public class AvailabilityTrackerRaceTests
{
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
        connection.SnapshotSentMessages().OfType<ClientStateMessage>()
            .Select(m => m.Payload.Available).ToList();

    [Fact]
    public async Task PublishWhileAnotherIsInFlight_IsNotSuppressedByTheStaleTracker()
    {
        var (client, connection, _) = SyncedClient();
        using var _c = client;

        // Establish the tracker at true via the initial state.
        await client.EnterExternalSourceAsync();
        await client.ExitExternalSourceAsync();
        int before = AvailableValuesSent(connection).Count;

        // Park the available:false send mid-flight.
        var gate = new TaskCompletionSource();
        connection.HoldNextSend = gate;
        var entering = client.EnterExternalSourceAsync();

        // ...and flip back while it is still in flight. Reading the tracker after the send would
        // have found it still true here, matched, and dropped this transition on the floor.
        await client.ExitExternalSourceAsync();

        gate.SetResult();
        await entering;

        var sent = AvailableValuesSent(connection).Skip(before).ToList();
        Assert.Equal(new bool?[] { false, true }, sent);
    }

    [Fact]
    public async Task AFailedSend_ReleasesTheClaim_SoTheNextPublishRetries()
    {
        // The claim is made before the send, so a send that throws must give it back — otherwise
        // the client would believe the server had been told something it never received, and
        // suppress the retry as a repeat.
        var (client, connection, pipe) = SyncedClient();
        using var _c = client;

        await client.EnterExternalSourceAsync();
        await client.ExitExternalSourceAsync();
        int before = AvailableValuesSent(connection).Count;

        connection.ThrowOnNextSend = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.EnterExternalSourceAsync());

        // EnterExternalSourceAsync rolled its own flag back on the throw, so drive the same
        // transition through a different input to prove the tracker did not swallow it.
        pipe.RaiseError("underrun");

        Assert.Contains(false, AvailableValuesSent(connection).Skip(before));
    }

    [Fact]
    public async Task RepeatedPublishesOfTheSameValue_AreStillSuppressed()
    {
        // Positive control: claiming before the send must not turn the publisher into one that
        // re-sends an unchanged value on every input event.
        var (client, connection, _) = SyncedClient();
        using var _c = client;

        await client.EnterExternalSourceAsync();
        int before = AvailableValuesSent(connection).Count;

        await client.EnterExternalSourceAsync();
        await client.EnterExternalSourceAsync();

        Assert.Equal(before, AvailableValuesSent(connection).Count);
    }
}
