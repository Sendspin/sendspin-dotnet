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
        var (client, connection, _) = TestClient.Create(configure: options => options.ClockSynchronizer = clock);
        return (client, connection, clock);
    }

    private static List<ClientStateMessage> ClientStates(FakeSendspinConnection connection) =>
        connection.SnapshotSentMessages().OfType<ClientStateMessage>().ToList();

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
            options.Capabilities = new ClientCapabilities { Roles = ["artwork@v1"] });
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
    public async Task SyncLoss_PublishesAvailableFalse_AndRegainRecoversWithoutResendingInitial()
    {
        // Both directions of the convergence input flow through the availability publisher:
        // losing sync mid-session reports available: false, regaining it reports true — as
        // deltas, never as a second copy of the initial full state.
        var (client, connection, clock) = CreatePlayerClient();
        using var _c = client;
        connection.RespondToTimeSync = true;

        TestClient.CompleteHandshake(connection, "player@v1");
        await WaitForAsync(() => ClientStates(connection).Count > 0, TimeSpan.FromSeconds(5));

        clock.ConvergeOnMeasurement = false; // the next burst loses convergence
        await WaitForAsync(
            () => ClientStates(connection).Any(m => m.Payload.Available == false),
            TimeSpan.FromSeconds(5));

        clock.ConvergeOnMeasurement = true; // and a later one regains it
        await WaitForAsync(() => ClientStates(connection).Count >= 3, TimeSpan.FromSeconds(5));

        var states = ClientStates(connection);
        Assert.Equal(new bool?[] { true, false, true }, states.Select(m => m.Payload.Available).ToArray());

        // Exactly one full initial state; the loss/regain travel as availability-only deltas.
        Assert.Single(states, m => m.Payload.Player is not null);
    }

    [Fact]
    public async Task AvailabilityFlipWhileConverging_PromotesTheFullInitialState_NotABareDelta()
    {
        var (client, connection, _) = CreatePlayerClient();
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

        // Exiting while still unconverged stays silent: the clock alone now holds availability
        // false, which is exactly the spurious false the deferral forbids.
        await client.ExitExternalSourceAsync();
        Assert.Single(ClientStates(connection));

        // The latch then routes the eventual convergence through the delta path — one full
        // initial message per connection, recovery as an availability-only delta.
        connection.RespondToTimeSync = true;
        await WaitForAsync(() => ClientStates(connection).Count >= 2, TimeSpan.FromSeconds(8));

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
    public async Task SourceSignalWhileConverging_SendsNothing_AndResumesAfterTheInitialState()
    {
        var clock = new ScriptedClockSynchronizer();
        var (client, connection, _) = TestClient.Create(configure: options =>
        {
            options.ClockSynchronizer = clock;
            options.Capabilities = new ClientCapabilities { Roles = ["source@v1"], SourceLineSense = true };
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
