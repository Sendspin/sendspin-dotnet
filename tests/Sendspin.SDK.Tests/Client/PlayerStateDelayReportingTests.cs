using System.Text.Json;
using Sendspin.SDK.Client;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// What <c>client/state</c> reports for <c>static_delay_ms</c>, and what supplying one to
/// <see cref="ISendSpinClient.SendPlayerStateAsync"/> means.
/// </summary>
/// <remarks>
/// Two spec rules drive this. The server "MUST merge each update into existing state, retaining
/// the last value of any field that is absent", so a value on the wire overwrites — reporting a
/// delay the client is not applying is not merely redundant, it replaces the real one. And
/// clients "must persist static_delay_ms locally across reboots and server reconnections", so a
/// client-initiated update that does not persist is not an update at all.
/// </remarks>
public class PlayerStateDelayReportingTests
{
    private static int ReportedDelay(FakeSendspinConnection connection)
    {
        string json = MessageSerializer.Serialize(
            connection.SentMessages.OfType<ClientStateMessage>().Last());

        return JsonDocument.Parse(json).RootElement
            .GetProperty("payload").GetProperty("player")
            .GetProperty("static_delay_ms").GetInt32();
    }

    private static (SendspinClientService Client, FakeSendspinConnection Connection, ConvergedClock Clock, RecordingDelayStore Store)
        Connected()
    {
        var clock = new ConvergedClock();
        var store = new RecordingDelayStore();
        var (client, connection, _) = TestClient.Create(
            configure: options => options with
            {
                ClockSynchronizer = clock,
                StaticDelayStore = store,
            });

        TestClient.CompleteHandshake(connection, "player@v1");
        return (client, connection, clock, store);
    }

    [Fact]
    public async Task VolumeChange_DoesNotOverwriteAServerSetDelay()
    {
        // The reported defect: a server sets 250 ms, then the next volume change reported
        // static_delay_ms 0 — which the server MUST merge, wiping the delay it had just set.
        var (client, connection, clock, _) = Connected();
        using var _c = client;

        connection.RaiseTextMessageReceived(
            """{"type":"server/command","payload":{"player":{"command":"set_static_delay","static_delay_ms":250}}}""");
        Assert.Equal(250, ReportedDelay(connection));

        await client.SendPlayerStateAsync(volume: 60, muted: false);

        Assert.Equal(250, ReportedDelay(connection));
        Assert.Equal(250.0, clock.StaticDelayMs);
    }

    [Fact]
    public async Task SuppliedDelay_IsApplied_Persisted_AndReported()
    {
        // Supplying a delay is a client-initiated update, so all three must happen. Reporting
        // alone left the server calibrating against a delay playback was not using, and the
        // unpersisted value reverted on the next reconnect.
        var (client, connection, clock, store) = Connected();
        using var _c = client;

        await client.SendPlayerStateAsync(volume: 50, muted: false, staticDelayMs: 400);

        Assert.Equal(400, ReportedDelay(connection));
        Assert.Equal(400.0, clock.StaticDelayMs);
        Assert.Equal(new[] { 400.0 }, store.Saved);
    }

    [Fact]
    public async Task OmittedDelay_ChangesNothingAndPersistsNothing()
    {
        // Positive control for the two above: if a supplied delay were ignored, the first test
        // would pass for the wrong reason, and if an omitted one wrote through, the store would
        // fill with redundant saves on every volume change.
        var (client, connection, clock, store) = Connected();
        using var _c = client;

        clock.StaticDelayMs = 120.0;
        await client.SendPlayerStateAsync(volume: 70, muted: true);

        Assert.Equal(120, ReportedDelay(connection));
        Assert.Equal(120.0, clock.StaticDelayMs);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task RepeatingTheSameDelay_DoesNotRePersist()
    {
        var (client, connection, _, store) = Connected();
        using var _c = client;

        await client.SendPlayerStateAsync(volume: 50, muted: false, staticDelayMs: 400);
        await client.SendPlayerStateAsync(volume: 51, muted: false, staticDelayMs: 400);

        Assert.Equal(new[] { 400.0 }, store.Saved);
        Assert.Equal(400, ReportedDelay(connection));
    }

    [Fact]
    public async Task SuppliedDelay_IsStillProjectedOntoTheWireRange()
    {
        // The projection and the apply-and-persist path have to compose: negatives still
        // schedule audio later, and still must not reach the wire.
        var (client, connection, clock, store) = Connected();
        using var _c = client;

        await client.SendPlayerStateAsync(volume: 50, muted: false, staticDelayMs: -300);

        Assert.Equal(0, ReportedDelay(connection));
        Assert.Equal(-300.0, clock.StaticDelayMs);
        Assert.Equal(new[] { -300.0 }, store.Saved);
    }

    private sealed class RecordingDelayStore : IStaticDelayStore
    {
        public List<double> Saved { get; } = new();

        public double? Load() => null;

        public void Save(double staticDelayMs) => Saved.Add(staticDelayMs);
    }

    /// <summary>
    /// Converged from the outset, so the initial client/state is not deferred behind clock sync.
    /// </summary>
    private sealed class ConvergedClock : IClockSynchronizer
    {
        public double StaticDelayMs { get; set; }

        public bool IsConverged => true;

        public bool HasMinimalSync => true;

        public void ProcessMeasurement(long t1, long t2, long t3, long t4)
        {
        }

        public void Reset()
        {
        }

        public long ClientToServerTime(long clientTime) => clientTime;

        public long ServerToClientTime(long serverTime) => serverTime;

        public long ServerToClientTimeUncompensated(long serverTime) => serverTime;

        public ClockSyncStatus GetStatus() => new() { IsConverged = true };
    }
}
