using Sendspin.SDK.Audio;
using Sendspin.SDK.Client;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// The spec lets a player report <c>available: true</c> only after clock synchronization is
/// established, so a sync-requiring client (player/source role) defers its initial client/state
/// until the first convergence instead of sending it on activate. Deferral — not an early
/// <c>available: false</c> — because the server moves an unavailable client into a solo group,
/// sends stream/end, and MUST NOT auto-rejoin it, so a <c>false</c> sent during a routine
/// reconnect would silently and permanently drop the client out of its group. Clients without a
/// player/source role need no clock and still send immediately: <c>available</c> alone unlocks
/// the server's streams for them.
/// </summary>
public class InitialClientStateGatingTests
{
    private static readonly Uri ServerUri = new("ws://test.local:8927/sendspin");

    private static (SendspinClientService Client, FakeSendspinConnection Connection, ScriptedClockSynchronizer Clock)
        CreatePlayerClient()
    {
        var clock = new ScriptedClockSynchronizer();
        var (client, connection, _) = TestClient.Create(configure: options => options with { ClockSynchronizer = clock });
        return (client, connection, clock);
    }

    private static (SendspinClientService Client, FakeSendspinConnection Connection, ScriptedClockSynchronizer Clock, FakeAudioPipeline Pipeline)
        CreatePlayerClientWithPipeline()
    {
        var clock = new ScriptedClockSynchronizer();
        var pipe = new FakeAudioPipeline();
        var (client, connection, _) = TestClient.Create(configure: options => options with
        {
            ClockSynchronizer = clock,
            AudioPipeline = pipe,
        });
        return (client, connection, clock, pipe);
    }

    private static List<ClientStateMessage> ClientStates(FakeSendspinConnection connection) =>
        connection.SnapshotSentMessages().OfType<ClientStateMessage>().ToList();

    /// <summary>Everything sent except the time-sync probes, which are the machinery driving
    /// convergence rather than traffic under test.</summary>
    private static List<IMessage> NonTimeSyncMessages(FakeSendspinConnection connection) =>
        connection.SnapshotSentMessages().Where(m => m is not ClientTimeMessage).ToList();

    /// <summary>Reconnects after a drop via the client's own connect path, which resets the
    /// per-connection handshake state (<c>TestClient.CompleteHandshake</c> alone cannot: the
    /// activate would not count as the connection's first).</summary>
    private static async Task ReconnectAsync(SendspinClientService client, FakeSendspinConnection connection)
    {
        var connectTask = client.ConnectAsync(ServerUri);
        connection.RaiseTextMessageReceived("""
            {"type":"server/hello","payload":{"name":"srv"}}
            """);
        connection.RaiseTextMessageReceived("""
            {"type":"server/activate","payload":{"activities":["playback"],"active_roles":["player@v1"]}}
            """);
        await connectTask;
    }

    [Fact]
    public async Task PlayerClient_BeforeSyncConverges_SendsNoClientState()
    {
        var (client, connection, _) = CreatePlayerClient();
        using var _c = client;

        TestClient.CompleteHandshake(connection, "player@v1");

        // The initial send is fire-and-forget; give a wrongly-undeferred send time to land.
        await Task.Delay(200);
        Assert.Empty(ClientStates(connection));
    }

    [Fact]
    public async Task PlayerClient_OnFirstConvergence_SendsExactlyOneClientStateAvailableTrue()
    {
        // Positive control for the test above: an implementation that never sends the initial
        // state at all would pass it, so this one demands the deferred message actually arrives.
        var (client, connection, clock) = CreatePlayerClient();
        using var _c = client;
        connection.RespondToTimeSync = true;

        TestClient.CompleteHandshake(connection, "player@v1");

        await WaitForAsync(() => ClientStates(connection).Count > 0, TimeSpan.FromSeconds(5));

        // Let at least one more full burst apply a measurement: a converged burst on an
        // already-converged clock is not a transition and must not re-send the initial state.
        int measured = clock.Measurements;
        await WaitForAsync(() => clock.Measurements > measured, TimeSpan.FromSeconds(5));

        var state = Assert.Single(ClientStates(connection));
        Assert.Equal(true, state.Payload.Available);
        Assert.NotNull(state.Payload.Player);
    }

    [Fact]
    public async Task ArtworkOnlyClient_SendsInitialClientStateImmediatelyOnActivate()
    {
        // No player/source role => no clock-sync requirement. The default (unconverged) Kalman
        // synchronizer is kept deliberately: the send must not wait for it.
        var (client, connection, _) = TestClient.Create(configure: options =>
            options with { Capabilities = new ClientCapabilities { Roles = ["artwork@v1"] } });
        using var _c = client;

        TestClient.CompleteHandshake(connection, "artwork@v1");

        await WaitForAsync(() => ClientStates(connection).Count > 0, TimeSpan.FromSeconds(1));
        Assert.Equal(true, Assert.Single(ClientStates(connection)).Payload.Available);
    }

    [Fact]
    public async Task ReconnectWhileConverging_NeverSendsAvailableFalse()
    {
        // The design decision under test: a reconnecting client that has not yet re-converged
        // must stay silent, not report available: false — the server would move it to a solo
        // group and never auto-rejoin it.
        var (client, connection, _) = CreatePlayerClient();
        using var _c = client;
        connection.RespondToTimeSync = true;

        TestClient.CompleteHandshake(connection, "player@v1");
        await WaitForAsync(() => ClientStates(connection).Count > 0, TimeSpan.FromSeconds(5));

        await connection.DisconnectAsync("network_drop");

        // Reconnect with the clock reset and never converging this time.
        connection.RespondToTimeSync = false;
        await ReconnectAsync(client, connection);

        await Task.Delay(300);
        Assert.DoesNotContain(ClientStates(connection), m => m.Payload.Available == false);

        // And the deferred initial state has not been sent early either.
        Assert.Single(ClientStates(connection));
    }

    [Fact]
    public async Task Reconnect_SendsInitialClientStateAgainOnItsFirstConvergence()
    {
        // The once-per-connection latch must reset with the rest of the per-connection state,
        // or a reconnect would never send its initial state at all.
        var (client, connection, _) = CreatePlayerClient();
        using var _c = client;
        connection.RespondToTimeSync = true;

        TestClient.CompleteHandshake(connection, "player@v1");
        await WaitForAsync(() => ClientStates(connection).Count > 0, TimeSpan.FromSeconds(5));

        await connection.DisconnectAsync("network_drop");
        await ReconnectAsync(client, connection);

        await WaitForAsync(() => ClientStates(connection).Count >= 2, TimeSpan.FromSeconds(5));

        var states = ClientStates(connection);
        Assert.All(states, m => Assert.Equal(true, m.Payload.Available));
        Assert.All(states, m => Assert.NotNull(m.Payload.Player));
    }

    [Fact]
    public async Task SyncLossAfterInitialState_ProducesNoWireTraffic()
    {
        // The live defect this pins against: one RTT spike can push the Kalman offset
        // uncertainty over the convergence threshold, flipping IsConverged false mid-session.
        // Playback carries on regardless — the pipeline gates on minimal sync, not
        // convergence — so a client that published available: false here kept streaming
        // audio while telling the server it was not participating in playback, and the
        // server moves an unavailable client to a solo group it MUST NOT auto-rejoin.
        // Sync establishment is latched per connection at the first convergence: once the
        // initial state is out, a convergence loss must produce no wire traffic at all.
        var (client, connection, clock) = CreatePlayerClient();
        using var _c = client;
        connection.RespondToTimeSync = true;

        TestClient.CompleteHandshake(connection, "player@v1");
        await WaitForAsync(() => ClientStates(connection).Count > 0, TimeSpan.FromSeconds(5));

        int sentBeforeLoss = NonTimeSyncMessages(connection).Count;

        clock.ConvergeOnMeasurement = false; // the next burst loses convergence
        int measured = clock.Measurements;

        // Let the loss apply and a further burst complete, so a wrongly-published message
        // (fire-and-forget) has ample time to land before the absence is asserted.
        await WaitForAsync(() => clock.Measurements >= measured + 2, TimeSpan.FromSeconds(5));

        // Nothing but the time-sync probes themselves reaches the wire after the loss.
        Assert.Equal(sentBeforeLoss, NonTimeSyncMessages(connection).Count);
    }

    [Fact]
    public async Task SyncLossAndRegain_AfterInitialState_SendNoAvailabilityTraffic()
    {
        // Converted from the test that pinned the defect (it demanded a true/false/true
        // availability sequence over a loss/regain cycle). The contract now: availability
        // composes the per-connection ClockSyncEstablished latch, set at the first
        // convergence and never cleared. IsConverged is a statistical threshold that oscillates under RTT
        // jitter in normal operation, so neither losing it mid-session nor regaining it may
        // put anything on the wire — no available: false (the server would solo-group the
        // client and never auto-rejoin it), no availability delta on the regain, and no
        // second copy of the initial full state.
        var (client, connection, clock) = CreatePlayerClient();
        using var _c = client;
        connection.RespondToTimeSync = true;

        TestClient.CompleteHandshake(connection, "player@v1");
        await WaitForAsync(() => ClientStates(connection).Count > 0, TimeSpan.FromSeconds(5));

        clock.ConvergeOnMeasurement = false; // the next burst loses convergence
        int measured = clock.Measurements;
        await WaitForAsync(() => clock.Measurements > measured, TimeSpan.FromSeconds(5));

        clock.ConvergeOnMeasurement = true; // and a later one regains it
        measured = clock.Measurements;
        await WaitForAsync(() => clock.Measurements >= measured + 2, TimeSpan.FromSeconds(5));

        // The only client/state on the wire is still the one initial full state.
        var state = Assert.Single(ClientStates(connection));
        Assert.Equal(true, state.Payload.Available);
        Assert.NotNull(state.Payload.Player);
    }

    [Fact]
    public async Task RecoveryBeforeFirstConvergence_WithholdsAvailableTrue_UntilConvergenceReleasesIt()
    {
        // The spec ties a player's available: true to a synchronized clock, and that is not
        // scoped to the initial message — so when a genuine false promotes the initial inside
        // the converging window and the condition then clears, the recovery's true must NOT
        // go out while sync has never been established on this connection. The second half
        // matters as much: once convergence lands, the pending true must be released — an
        // implementation that never sends it would leave the server believing a recovered
        // client unavailable forever.
        var (client, connection, clock, pipe) = CreatePlayerClientWithPipeline();
        using var _c = client;

        TestClient.CompleteHandshake(connection, "player@v1");

        // Pipeline error inside the converging window: promotes the initial with the genuine
        // available: false.
        pipe.RaiseError();
        var first = Assert.Single(ClientStates(connection));
        Assert.Equal(false, first.Payload.Available);

        // The pipeline recovers, still before any convergence. The recovery ack (a player
        // delta without `available`) may go out, but no available: true may reach the wire.
        pipe.SetState(AudioPipelineState.Playing);
        await Task.Delay(200);
        Assert.DoesNotContain(ClientStates(connection), m => m.Payload.Available == true);

        // First convergence: the pending true is released, exactly once.
        connection.RespondToTimeSync = true;
        await WaitForAsync(
            () => ClientStates(connection).Any(m => m.Payload.Available == true),
            TimeSpan.FromSeconds(8));

        int measured = clock.Measurements;
        await WaitForAsync(() => clock.Measurements >= measured + 2, TimeSpan.FromSeconds(5));

        var states = ClientStates(connection);
        Assert.Single(states, m => m.Payload.Available == true);
        Assert.Single(states, m => m.Payload.Available == false);
    }

    [Fact]
    public async Task AvailabilityFlipWhileConverging_PromotesTheFullInitialState_NotABareDelta()
    {
        var (client, connection, clock) = CreatePlayerClient();
        using var _c = client;

        TestClient.CompleteHandshake(connection, "player@v1");

        // A genuine availability input flips inside the converging window. The server treats
        // the first client/state it receives as the initial one, which MUST carry all state
        // fields — so the publish must be promoted to the full initial message with the
        // composed available, never a bare availability delta.
        await client.EnterExternalSourceAsync();

        var first = Assert.Single(ClientStates(connection));
        Assert.Equal(false, first.Payload.Available);
        Assert.NotNull(first.Payload.Player);

        // Exiting while sync has never been established on this connection stays silent: the
        // spec ties a player's available: true to a synchronized clock, so the recovery is
        // withheld — availability composes the per-connection ClockSyncEstablished latch,
        // still unset here.
        await client.ExitExternalSourceAsync();
        Assert.Single(ClientStates(connection));

        // The first convergence releases the withheld true — as an availability-only delta:
        // one full initial message per connection.
        connection.RespondToTimeSync = true;
        await WaitForAsync(() => ClientStates(connection).Count >= 2, TimeSpan.FromSeconds(8));

        // And a further burst adds nothing more.
        int measured = clock.Measurements;
        await WaitForAsync(() => clock.Measurements >= measured + 2, TimeSpan.FromSeconds(5));

        var states = ClientStates(connection);
        Assert.Equal(new bool?[] { false, true }, states.Select(m => m.Payload.Available).ToArray());
        Assert.Single(states, m => m.Payload.Player is not null);
    }

    [Fact]
    public async Task UpdateTimingWhileConverging_SendsNothing_DeferredInitialCarriesTheNewValues()
    {
        var (client, connection, _) = CreatePlayerClient();
        using var _c = client;

        TestClient.CompleteHandshake(connection, "player@v1");

        // Timing configured at connect, inside the converging window: a player-only delta must
        // not become the server's "initial" client/state.
        await client.UpdateTimingAsync(requiredLeadTimeMs: 80, minBufferMs: 40);

        await Task.Delay(100);
        Assert.Empty(ClientStates(connection));

        // Nothing is lost: the deferred initial reads the timing fields live.
        connection.RespondToTimeSync = true;
        await WaitForAsync(() => ClientStates(connection).Count > 0, TimeSpan.FromSeconds(8));

        var initial = Assert.Single(ClientStates(connection));
        Assert.Equal(true, initial.Payload.Available);
        Assert.NotNull(initial.Payload.Player);
        Assert.Equal(80, initial.Payload.Player!.RequiredLeadTimeMs);
        Assert.Equal(40, initial.Payload.Player.MinBufferMs);
    }

    [Fact]
    public async Task PlayerStateWhileConverging_SendsNothing_DeferredInitialCarriesTheValues()
    {
        var (client, connection, _) = CreatePlayerClient();
        using var _c = client;

        TestClient.CompleteHandshake(connection, "player@v1");

        // Volume restored at connect, inside the converging window: a player-only delta must
        // not become the server's "initial" client/state, and with nothing but the converging
        // clock holding availability false, promoting the full initial now would carry the
        // spurious available: false the deferral forbids. The call stays silent instead.
        await client.SendPlayerStateAsync(volume: 55, muted: true);

        await Task.Delay(100);
        Assert.Empty(ClientStates(connection));

        // Nothing is lost: the values were persisted into the player state, which the
        // deferred initial reads live.
        connection.RespondToTimeSync = true;
        await WaitForAsync(() => ClientStates(connection).Count > 0, TimeSpan.FromSeconds(8));

        var initial = Assert.Single(ClientStates(connection));
        Assert.Equal(true, initial.Payload.Available);
        Assert.Equal(55, initial.Payload.Player!.Volume);
        Assert.Equal(true, initial.Payload.Player.Muted);
    }

    [Fact]
    public async Task ReconnectAfterError_RecoveryInsideConvergingWindow_FirstClientStateIsTheFullInitial()
    {
        // The cross-connection shape of the promotion guarantee. On connection 1 a pipeline
        // error publishes available: false, leaving the last-sent tracker false. On
        // connection 2 the pipeline recovers inside the converging window (e.g. an audio-sink
        // retry playing already-buffered samples, needing no server audio), which fires the
        // recovery player-state ack — and with the stale tracker suppressing the availability
        // publish, that ack's bare player delta used to be the first client/state the server
        // saw on the new connection.
        var (client, connection, _, pipe) = CreatePlayerClientWithPipeline();
        using var _c = client;
        connection.RespondToTimeSync = true;

        TestClient.CompleteHandshake(connection, "player@v1");
        await WaitForAsync(() => ClientStates(connection).Count > 0, TimeSpan.FromSeconds(5));

        pipe.RaiseError();
        await WaitForAsync(
            () => ClientStates(connection).Any(m => m.Payload.Available == false),
            TimeSpan.FromSeconds(5));

        // Drop and reconnect; the clock resets, and unanswered probes hold the converging
        // window open.
        connection.RespondToTimeSync = false;
        await connection.DisconnectAsync("network_drop");
        int sentOnConnection1 = ClientStates(connection).Count;
        await ReconnectAsync(client, connection);

        // Recovery inside the window: the ack path must not put a bare player delta on the wire.
        pipe.SetState(AudioPipelineState.Playing);
        await Task.Delay(200);
        Assert.Equal(sentOnConnection1, ClientStates(connection).Count);

        // Convergence releases the deferred initial: the first client/state of connection 2
        // is the full message — available again, carrying the player object.
        connection.RespondToTimeSync = true;
        await WaitForAsync(() => ClientStates(connection).Count > sentOnConnection1, TimeSpan.FromSeconds(8));

        var first = ClientStates(connection)[sentOnConnection1];
        Assert.Equal(true, first.Payload.Available);
        Assert.NotNull(first.Payload.Player);
    }

    [Fact]
    public async Task ReconnectWithErrorOutstanding_PlayerStateCallPromotesTheFullInitial()
    {
        // Same cross-connection staleness, but the error never recovers: a direct
        // SendPlayerStateAsync call (an app restoring volume on connect) inside the
        // converging window must promote the full initial. Availability is genuinely false
        // here — the outstanding error, not the converging clock — so the promotion carries
        // it rather than staying silent.
        var (client, connection, _, pipe) = CreatePlayerClientWithPipeline();
        using var _c = client;
        connection.RespondToTimeSync = true;

        TestClient.CompleteHandshake(connection, "player@v1");
        await WaitForAsync(() => ClientStates(connection).Count > 0, TimeSpan.FromSeconds(5));

        pipe.RaiseError();
        await WaitForAsync(
            () => ClientStates(connection).Any(m => m.Payload.Available == false),
            TimeSpan.FromSeconds(5));

        connection.RespondToTimeSync = false;
        await connection.DisconnectAsync("network_drop");
        int sentOnConnection1 = ClientStates(connection).Count;
        await ReconnectAsync(client, connection);

        await client.SendPlayerStateAsync(volume: 55, muted: true);

        var first = ClientStates(connection)[sentOnConnection1];
        Assert.Equal(false, first.Payload.Available);
        Assert.Equal(55, first.Payload.Player!.Volume);
        Assert.Equal(true, first.Payload.Player.Muted);
    }

    [Fact]
    public async Task SourceSignalWhileConverging_SendsNothing_AndResumesAfterTheInitialState()
    {
        var clock = new ScriptedClockSynchronizer();
        var (client, connection, _) = TestClient.Create(configure: options => options with
        {
            ClockSynchronizer = clock,
            Capabilities = new ClientCapabilities
            {
                Roles = ["source@v1"],
                SourceRoleSupport = new SourceRoleSupport { LineSense = true }
            },
        });
        using var _c = client;

        TestClient.CompleteHandshake(connection, "source@v1");

        // A source-only delta inside the converging window would become the server's
        // "initial" client/state; it must be skipped.
        await client.SetSourceSignalAsync(present: true);

        await Task.Delay(100);
        Assert.Empty(ClientStates(connection));

        connection.RespondToTimeSync = true;
        await WaitForAsync(() => ClientStates(connection).Count > 0, TimeSpan.FromSeconds(8));

        // After the initial state is out, signal reporting works again.
        await client.SetSourceSignalAsync(present: true);

        var signal = ClientStates(connection).Last();
        Assert.Equal("present", signal.Payload.Source!.Signal);
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
    /// <see cref="ConvergeOnMeasurement"/> into <see cref="IsConverged"/>, so a fixture that
    /// answers time-sync probes (see <see cref="FakeSendspinConnection.RespondToTimeSync"/>)
    /// drives convergence — and its loss — through the client's real burst/apply path.
    /// </summary>
    private sealed class ScriptedClockSynchronizer : IClockSynchronizer
    {
        public bool ConvergeOnMeasurement { get; set; } = true;

        public int Measurements { get; private set; }

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

        // Deliberately reports an unconverged status whatever IsConverged says. The time-sync
        // loop paces on this — 500 ms while converging, the reference's 10 s once synced — and
        // these tests are about the availability gate, which reads IsConverged instead. Left
        // unconverged, they drive several bursts in a moment rather than spending ten seconds
        // apiece waiting for the steady-state interval.
        public ClockSyncStatus GetStatus() => new();
    }
}
