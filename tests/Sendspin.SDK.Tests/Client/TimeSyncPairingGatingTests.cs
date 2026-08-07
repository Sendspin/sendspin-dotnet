using Sendspin.SDK.Client;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Connection.Noise.Pairing;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// The time-sync loop must not run while a pairing activation is in effect. A pairing
/// activate grants no roles, so there is nothing to synchronize a clock for — and the
/// reference server (aiosendspin) stops reading the socket while the operator enters the
/// PIN, then treats the first buffered frame as the next pairing message, so a client/time
/// probe sent during that window aborts the whole attempt as a protocol error. Probing
/// resumes on the first activation that is not a pairing one.
/// </summary>
public class TimeSyncPairingGatingTests
{
    private const string ServerHello =
        """{"type":"server/hello","payload":{"name":"srv"}}""";

    private const string PairingActivate =
        """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"selected_pair_method":"dynamic_pin"}}""";

    private const string PlaybackActivate =
        """{"type":"server/activate","payload":{"activities":["playback"],"active_roles":["player@v1"]}}""";

    private const string ArtworkActivate =
        """{"type":"server/activate","payload":{"activities":["playback"],"active_roles":["artwork@v1"]}}""";

    /// <summary>
    /// Client able to run a real dynamic-PIN attempt (lockout store and PIN presenter
    /// configured), with a scripted clock so the time-sync loop keeps its dense initial
    /// cadence (~one probe burst per 500 ms) for the duration of the test. Roles default
    /// to the capability defaults (controller/player/...); pass <paramref name="roles"/>
    /// to narrow them.
    /// </summary>
    private static (SendspinClientService Client, FakeSendspinConnection Connection, ScriptedClockSynchronizer Clock)
        CreatePinPairableClient(PskCategory category, bool unpairedAccess = false, string[]? roles = null)
    {
        var clock = new ScriptedClockSynchronizer();
        var (client, connection, _) = TestClient.Create(
            category,
            unpairedAccess,
            configure: options =>
            {
                var caps = new ClientCapabilities { PinPairingMethods = { "dynamic_pin" } };
                if (roles is not null)
                {
                    caps.Roles = [.. roles];
                }

                options.Capabilities = caps;
                options.ClockSynchronizer = clock;
                options.PinLockoutStore = new InMemoryPinLockoutStore();
                options.PresentPinAsync = (_, _) => ValueTask.CompletedTask;
            });
        return (client, connection, clock);
    }

    private static List<ClientTimeMessage> Probes(FakeSendspinConnection connection) =>
        connection.SnapshotSentMessages().OfType<ClientTimeMessage>().ToList();

    private static List<ClientStateMessage> ClientStates(FakeSendspinConnection connection) =>
        connection.SnapshotSentMessages().OfType<ClientStateMessage>().ToList();

    [Fact]
    public async Task PairingFirstActivate_SendsNoTimeSyncProbes_UntilANonPairingActivateArrives()
    {
        // The interop shape on an unpaired (sentinel) session: the FIRST activate on the
        // connection is the pairing one. FinishHandshake used to start the time-sync loop
        // unconditionally here, and its probes queued in the reference server — blocked
        // awaiting the operator's PIN — which then read a buffered client/time where it
        // required client/pair-auth and closed the connection.
        var (client, connection, _) = CreatePinPairableClient(PskCategory.Sentinel, unpairedAccess: true);
        using var _c = client;

        connection.RaiseTextMessageReceived(ServerHello);
        connection.RaiseTextMessageReceived(PairingActivate);

        // The attempt itself is under way...
        Assert.Contains(connection.SnapshotSentMessages(), m => m is ClientPairInitMessage);

        // ...and stays unpolluted. A wrongly-started loop sends its first probe on start,
        // but the loop is fire-and-forget, so give a stray probe ample time to land.
        await Task.Delay(300);
        Assert.DoesNotContain(connection.SnapshotSentMessages(), m => m is ClientTimeMessage);

        // The first non-pairing activate resumes sync for the granted roles.
        connection.RaiseTextMessageReceived(PlaybackActivate);
        await WaitForAsync(() => Probes(connection).Count > 0, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task MidSessionPairingActivate_StopsTheRunningTimeSyncLoop_AndANonPairingActivateResumesIt()
    {
        // The exact shape of the live failure: music playing — sync loop running under a
        // playback activation — when the server sends server/activate
        // {activities:["pairing"], active_roles:[]} to start a PIN attempt. The already-
        // running loop must STOP; declining to start one is not enough.
        var (client, connection, clock) = CreatePinPairableClient(PskCategory.LongTerm);
        using var _c = client;
        connection.RespondToTimeSync = true; // bursts complete, so probes flow continuously

        TestClient.CompleteHandshake(connection, "player@v1");

        // Sync genuinely established before pairing begins, as in the live session: at
        // least one full burst has applied a measurement and converged the clock.
        await WaitForAsync(() => clock.Measurements > 0, TimeSpan.FromSeconds(5));
        Assert.True(clock.IsConverged);

        connection.RaiseTextMessageReceived(PairingActivate);

        // Let a probe already past the loop's cancellation check land, then require
        // silence for longer than the loop's densest cadence (50 ms between probes,
        // 500 ms between bursts).
        await Task.Delay(200);
        int probesWhenStopped = Probes(connection).Count;
        await Task.Delay(700);
        Assert.Equal(probesWhenStopped, Probes(connection).Count);

        // Stopping the loop must not reset the synchronizer: its measurements remain
        // valid across the pairing window, so playback resumes without re-converging.
        Assert.True(clock.IsConverged);

        // Leaving pairing resumes probing.
        connection.RaiseTextMessageReceived(PlaybackActivate);
        await WaitForAsync(() => Probes(connection).Count > probesWhenStopped, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PairingFirstActivate_KeepsInitialClientStateDeferred_UntilSyncResumesAndConverges()
    {
        // The deferral interplay: with sync stopped for the pairing window, convergence
        // never arrives, so the deferred initial client/state stays deferred — correct,
        // since no roles are active. Once a non-pairing activate resumes the loop and it
        // converges, the first-convergence branch must still release the initial state,
        // exactly once.
        var (client, connection, clock) = CreatePinPairableClient(PskCategory.Sentinel, unpairedAccess: true);
        using var _c = client;
        connection.RespondToTimeSync = true;

        connection.RaiseTextMessageReceived(ServerHello);
        connection.RaiseTextMessageReceived(PairingActivate);

        // Nothing but pairing traffic during the window: no probes (answered probes would
        // converge the scripted clock and release the initial state mid-attempt) and no
        // client/state (there are no active roles to report for).
        await Task.Delay(300);
        Assert.DoesNotContain(
            connection.SnapshotSentMessages(),
            m => m is ClientTimeMessage or ClientStateMessage);

        connection.RaiseTextMessageReceived(PlaybackActivate);
        await WaitForAsync(() => ClientStates(connection).Count > 0, TimeSpan.FromSeconds(8));

        // A further converged measurement is not a transition and must not re-send it.
        int measured = clock.Measurements;
        await WaitForAsync(() => clock.Measurements > measured, TimeSpan.FromSeconds(5));

        var initial = Assert.Single(ClientStates(connection));
        Assert.Equal(true, initial.Payload.Available);
        Assert.NotNull(initial.Payload.Player);
    }

    [Fact]
    public async Task ArtworkOnlyClient_PairingFirstActivate_WithholdsInitialClientState_UntilANonPairingActivateArrives()
    {
        // A client with no sync-requiring role sends its initial client/state on activate
        // rather than deferring it to convergence — so without a pairing hold it would land
        // in the blocked reference server's buffer exactly the way client/time did. It must
        // stay off the wire while the pairing activation is in effect, and go out exactly
        // once when a non-pairing activate arrives: an implementation that simply never
        // sent it would pass the first half alone.
        var (client, connection, _) = CreatePinPairableClient(
            PskCategory.Sentinel, unpairedAccess: true, roles: ["artwork@v1"]);
        using var _c = client;

        connection.RaiseTextMessageReceived(ServerHello);
        connection.RaiseTextMessageReceived(PairingActivate);

        // The initial send is fire-and-forget; give a wrongly-unwithheld send time to land.
        await Task.Delay(300);
        Assert.Empty(ClientStates(connection));

        connection.RaiseTextMessageReceived(ArtworkActivate);
        await WaitForAsync(() => ClientStates(connection).Count > 0, TimeSpan.FromSeconds(5));

        // And nothing further trickles out after the release.
        await Task.Delay(200);
        var initial = Assert.Single(ClientStates(connection));
        Assert.Equal(true, initial.Payload.Available);
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        if (!condition())
        {
            throw new TimeoutException("Condition not met within timeout");
        }
    }

    /// <summary>
    /// Clock synchronizer the test scripts: each processed measurement copies
    /// <see cref="ConvergeOnMeasurement"/> into <see cref="IsConverged"/>, and the reported
    /// status never accumulates a measurement count, so the client's time-sync loop keeps
    /// its dense initial cadence instead of backing off to 10 s intervals.
    /// </summary>
    private sealed class ScriptedClockSynchronizer : IClockSynchronizer
    {
        public bool ConvergeOnMeasurement { get; set; } = true;

        public int Measurements { get; private set; }

        public bool IsConverged { get; private set; }

        public bool HasMinimalSync => IsConverged;

        public double StaticDelayMs { get; set; }

        public void ProcessMeasurement(long t1, long t2, long t3, long t4)
        {
            Measurements++;
            IsConverged = ConvergeOnMeasurement;
        }

        public void Reset() => IsConverged = false;

        public long ServerToClientTime(long serverTime) => serverTime;

        public long ClientToServerTime(long clientTime) => clientTime;

        public ClockSyncStatus GetStatus() => new() { IsConverged = IsConverged };
    }
}
