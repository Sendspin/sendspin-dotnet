using System.Text.Json;
using Sendspin.SDK.Client;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// The applied output delay is a double the clock synchronizer's setter clamps to the spec's
/// 0-5000 range; <c>static_delay_ms</c> on the wire is an integer, so the client rounds the
/// applied value onto it. These pin the rounding and the always-present field, and the end-to-end
/// clamp through the public path; the clamp in isolation is <c>KalmanClockSynchronizerTests</c>.
/// </summary>
/// <remarks>
/// aiosendspin's PlayerStatePayload raises <c>ValueError("static_delay_ms must be in range
/// 0-5000")</c> on parse, so a value the wire cannot carry fails the connection rather than being
/// tolerated — the reason the applied value is bounded before it is ever reported.
/// </remarks>
public class OutputDelayWireProjectionTests
{
    private static JsonElement PlayerObjectOfLastState(FakeSendspinConnection connection)
    {
        string json = MessageSerializer.Serialize(
            connection.SentMessages.OfType<ClientStateMessage>().Last());

        return JsonDocument.Parse(json).RootElement
            .GetProperty("payload").GetProperty("player").Clone();
    }

    private static (SendspinClientService Client, FakeSendspinConnection Connection) Connected(
        double outputDelayMs)
    {
        var (client, connection, _) = TestClient.Create(
            configure: options => options with
            {
                ClockSynchronizer = new ConvergedClock { OutputDelayMs = outputDelayMs },
            });

        TestClient.CompleteHandshake(connection, "player@v1");
        return (client, connection);
    }

    /// <summary>
    /// Converged from the outset, so the initial client/state is not deferred. A player client
    /// holds its initial state back until clock sync establishes, which would otherwise leave
    /// these tests asserting against a message that was never sent. Deliberately does not clamp
    /// its setter — that is the real synchronizer's job — so it can feed the projection the
    /// already-bounded values a real one would produce.
    /// </summary>
    private sealed class ConvergedClock : IClockSynchronizer
    {
        public double OutputDelayMs { get; set; }

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

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(250.0, 250)]
    [InlineData(12.5, 13)]        // fractional: rounded, never emitted as 12.5
    [InlineData(12.4, 12)]
    public void ClientState_RoundsTheAppliedValueToAnInteger(double configured, int expected)
    {
        var (client, connection) = Connected(configured);
        using var _c = client;

        var player = PlayerObjectOfLastState(connection);
        var delay = player.GetProperty("static_delay_ms");

        Assert.Equal(JsonValueKind.Number, delay.ValueKind);
        Assert.Equal(expected, delay.GetInt32());
    }

    [Fact]
    public void ClientState_AlwaysCarriesOutputDelay_EvenAtZero()
    {
        // Positive control for the theory above: if the field were dropped at its default,
        // every zero-expecting case there would fail on the missing property rather than on a
        // wrong value, which reads as a different bug.
        var (client, connection) = Connected(0.0);
        using var _c = client;

        Assert.True(
            PlayerObjectOfLastState(connection).TryGetProperty("static_delay_ms", out _),
            "static_delay_ms is REQUIRED for players and 0 is its default, so it must still be sent");
    }

    [Theory]
    [InlineData(-100.0, 0)]
    [InlineData(9000.0, 5000)]
    public void OutputDelay_ThroughPublicPath_ClampsInSchedulerAndOnWire(double requested, int expected)
    {
        // The public path: KalmanClockSynchronizer.OutputDelayMs is the single owner of the
        // applied value. Setting it clamps to the spec's 0-5000 in the scheduler, and the
        // client/state projects exactly that onto the wire. A real synchronizer is used so both
        // halves are the production path rather than a passthrough double.
        var sync = new KalmanClockSynchronizer();
        var (client, connection, _) = TestClient.Create(
            configure: options => options with { ClockSynchronizer = sync });
        using var _c = client;
        TestClient.CompleteHandshake(connection, "player@v1");

        sync.OutputDelayMs = requested;

        Assert.Equal((double)expected, sync.OutputDelayMs);

        var player = client.CreateClientStateMessage(
            available: true, client.SnapshotActiveRoleFamilies()).Payload.Player;
        Assert.NotNull(player);
        Assert.Equal(expected, player!.OutputDelayMs);
    }

    [Fact]
    public async Task PlayerStateDelta_ProjectsTheSameWay()
    {
        // Two call sites build a client/state (the initial full state and the player delta).
        // Only one of them projecting is exactly the defect shape this codebase keeps hitting,
        // so the delta is pinned separately rather than assumed.
        var (client, connection) = Connected(0.0);
        using var _c = client;

        await client.SendPlayerStateAsync(volume: 50, muted: false, outputDelayMs: 12.5);

        Assert.Equal(13, PlayerObjectOfLastState(connection).GetProperty("static_delay_ms").GetInt32());
    }
}
