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
/// pairing code, then treats the first buffered frame as the next pairing message, so a client/time
/// probe sent during that window aborts the whole attempt as a protocol error. Probing
/// resumes on the first activation that is not a pairing one.
/// </summary>
public class TimeSyncPairingGatingTests
{
    private const string ServerHello =
        """{"type":"server/hello","payload":{"name":"srv"}}""";

    private const string PairingActivate =
        """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"pairing":{"method":"dynamic_pairing_code","format":"digits"}}}""";

    private const string PlaybackActivate =
        """{"type":"server/activate","payload":{"activities":["playback"],"active_roles":["player@v1"]}}""";

    private const string ArtworkActivate =
        """{"type":"server/activate","payload":{"activities":["playback"],"active_roles":["artwork@v1"]}}""";

    private const string StreamStart =
        """{"type":"stream/start","payload":{"player":{"codec":"pcm","channels":2,"sample_rate":48000,"bit_depth":16}}}""";

    /// <summary>
    /// Client able to run a real dynamic-pairing code attempt (lockout store and pairing code presenter
    /// configured), with a scripted clock so the time-sync loop keeps its dense initial
    /// cadence (~one probe burst per 500 ms) for the duration of the test. Roles default
    /// to the capability defaults (controller/player/...); pass <paramref name="roles"/>
    /// to narrow them.
    /// </summary>
    private static (SendspinClientService Client, FakeSendspinConnection Connection, ScriptedClockSynchronizer Clock)
        CreatePairingCodePairableClient(PskCategory category, bool unpairedAccess = false, string[]? roles = null)
    {
        var clock = new ScriptedClockSynchronizer();
        var (client, connection, _) = TestClient.Create(
            category,
            unpairedAccess,
            configure: options =>
            {
                var caps = new ClientCapabilities { PairingCodeMethods = { "dynamic_pairing_code" } };
                if (roles is not null)
                {
                    caps.Roles = [.. roles];
                }

                return options with
                {
                    Capabilities = caps,
                    ClockSynchronizer = clock,
                    // dynamic_pairing_code needs a record store to be runnable at all (#158); without
                    // one the activation aborts and there is no pairing attempt to gate.
                    PairingRecordStore = new InMemoryPairingRecordStore(),
                    PairingCodeLockoutStore = new InMemoryPairingCodeLockoutStore(),
                    PresentPairingCodeAsync = (_, _) => ValueTask.CompletedTask,
                };
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
        // awaiting the operator's pairing code — which then read a buffered client/time where it
        // required client/pair-auth and closed the connection.
        var (client, connection, _) = CreatePairingCodePairableClient(PskCategory.Sentinel, unpairedAccess: true);
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
        // {activities:["pairing"], active_roles:[]} to start a pairing code attempt. The already-
        // running loop must STOP; declining to start one is not enough.
        //
        // Sentinel-keyed with unpaired access: since spec #183 a long-term (paired) PSK admits
        // no pairing activity, so an unpaired session is where this mid-session shape lives.
        var (client, connection, clock) = CreatePairingCodePairableClient(PskCategory.Sentinel, unpairedAccess: true);
        using var _c = client;
        connection.RespondToTimeSync = true; // bursts complete, so probes flow continuously

        TestClient.CompleteHandshake(connection, "player@v1");

        // Sync genuinely established before pairing begins, as in the live session: at
        // least one full burst has applied a measurement and converged the clock.
        await WaitForAsync(() => clock.Measurements > 0, TimeSpan.FromSeconds(5));
        Assert.True(clock.IsConverged);

        connection.RaiseTextMessageReceived(PairingActivate);

        // Let a probe already past the loop's cancellation check land, then require
        // silence for longer than the loop's densest cadence (probes back to back within a
        // burst, 500 ms between bursts).
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
        var (client, connection, clock) = CreatePairingCodePairableClient(PskCategory.Sentinel, unpairedAccess: true);
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
    public async Task StreamStartSmartSyncBurst_DoesNotFire_WhileAPairingActivationIsInEffect()
    {
        // The second client/time source: HandleStreamStartCoreAsync fires a smart-sync
        // burst with CancellationToken.None when the clock lacks minimal sync, so
        // StopTimeSyncLoop cannot reach it and it fires without app action. A stream/start
        // crossing a mid-session pairing activate must stay silent — but the same
        // stream/start outside a pairing window must still burst, because that burst is
        // what lets playback start before full convergence.
        var (client, connection, clock) = CreatePairingCodePairableClient(PskCategory.Sentinel, unpairedAccess: true);
        using var _c = client;
        connection.RespondToTimeSync = true;
        clock.ConvergeOnMeasurement = false;  // HasMinimalSync stays false: burst-eligible
        clock.StatusIsConverged = true;       // loop reports a synced clock and sleeps ~10 s

        TestClient.CompleteHandshake(connection, "player@v1");

        // The loop's first burst completes, then it sleeps ~10 s: until it wakes, any new
        // probe is attributable to the stream-start smart-sync trigger alone.
        await WaitForAsync(() => clock.Measurements > 0, TimeSpan.FromSeconds(5));
        int probesAfterLoopBurst = Probes(connection).Count;

        // Pairing arrives mid-session; the sleeping loop is stopped for good.
        connection.RaiseTextMessageReceived(PairingActivate);
        await Task.Delay(100);

        // stream/start inside the pairing window, clock without minimal sync: no burst.
        connection.RaiseTextMessageReceived(StreamStart);
        await Task.Delay(500);
        Assert.Equal(probesAfterLoopBurst, Probes(connection).Count);

        // Leave pairing: the restarted loop bursts once and goes back to sleep. The count is
        // read before the activate, not after: a burst against this fixture completes in
        // microseconds now that probes advance on the reply rather than on a fixed delay, so
        // reading it afterwards races the very measurement being waited for.
        int measured = clock.Measurements;
        connection.RaiseTextMessageReceived(PlaybackActivate);
        await WaitForAsync(() => clock.Measurements > measured, TimeSpan.FromSeconds(5));
        int probesAfterResume = Probes(connection).Count;

        // The same stream/start outside a pairing window still bursts.
        connection.RaiseTextMessageReceived(StreamStart);
        await WaitForAsync(() => Probes(connection).Count > probesAfterResume, TimeSpan.FromSeconds(2));
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
        var (client, connection, _) = CreatePairingCodePairableClient(
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
    /// <see cref="ConvergeOnMeasurement"/> into <see cref="IsConverged"/>. By default the
    /// reported status is unconverged, so the client's time-sync loop keeps its dense
    /// converging cadence instead of backing off to 10 s intervals; a test that wants the
    /// loop asleep between bursts scripts <see cref="StatusIsConverged"/> instead.
    /// </summary>
    private sealed class ScriptedClockSynchronizer : IClockSynchronizer
    {
        public bool ConvergeOnMeasurement { get; set; } = true;

        public int Measurements { get; private set; }

        /// <summary>
        /// Convergence as reported through <see cref="GetStatus"/>, which is what the
        /// time-sync loop paces on — scripted separately from <see cref="IsConverged"/>, the
        /// flag the availability gate reads.
        /// </summary>
        public bool StatusIsConverged { get; set; }

        public bool IsConverged { get; private set; }

        public bool HasMinimalSync => IsConverged;

        public double OutputDelayMs { get; set; }

        public void ProcessMeasurement(long t1, long t2, long t3, long t4)
        {
            Measurements++;
            IsConverged = ConvergeOnMeasurement;
        }

        public void Reset() => IsConverged = false;

        public long ServerToClientTime(long serverTime) => serverTime;

        public long ServerToClientTimeUncompensated(long serverTime) => serverTime;

        public long ClientToServerTime(long clientTime) => clientTime;

        public ClockSyncStatus GetStatus() => new() { IsConverged = StatusIsConverged };
    }
}
