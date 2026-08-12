using System.Text.Json;
using Sendspin.SDK.Client;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// The scheduler's static delay is a double over -5000..5000 — fractional from calibration,
/// negative to schedule later. The spec's <c>static_delay_ms</c> is an integer 0-5000 and states
/// negatives are not supported. Everything the client reports must be projected onto that.
/// </summary>
/// <remarks>
/// The negative case is not cosmetic: aiosendspin's PlayerStatePayload raises
/// <c>ValueError("static_delay_ms must be in range 0-5000")</c> on parse, so a negative delay
/// fails the connection rather than being tolerated.
/// </remarks>
public class StaticDelayWireProjectionTests
{
    private static JsonElement PlayerObjectOfLastState(FakeSendspinConnection connection)
    {
        string json = MessageSerializer.Serialize(
            connection.SentMessages.OfType<ClientStateMessage>().Last());

        return JsonDocument.Parse(json).RootElement
            .GetProperty("payload").GetProperty("player").Clone();
    }

    private static (SendspinClientService Client, FakeSendspinConnection Connection) Connected(
        double staticDelayMs)
    {
        var (client, connection, _) = TestClient.Create(
            configure: options => options with
            {
                ClockSynchronizer = new ConvergedClock { StaticDelayMs = staticDelayMs },
            });

        TestClient.CompleteHandshake(connection, "player@v1");
        return (client, connection);
    }

    /// <summary>
    /// Converged from the outset, so the initial client/state is not deferred. A player client
    /// holds its initial state back until clock sync establishes, which would otherwise leave
    /// these tests asserting against a message that was never sent.
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

        public ClockSyncStatus GetStatus() => new() { IsConverged = true };
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(250.0, 250)]
    [InlineData(12.5, 13)]        // fractional: rounded, never emitted as 12.5
    [InlineData(12.4, 12)]
    [InlineData(-200.0, 0)]       // negative: clamped, spec says unsupported
    [InlineData(-5000.0, 0)]
    [InlineData(9000.0, 5000)]    // above the spec maximum
    [InlineData(double.NaN, 0)]   // the setter is public and takes any double
    [InlineData(double.PositiveInfinity, 0)]
    public void ClientState_ReportsAnIntegerInSpecRange(double configured, int expected)
    {
        var (client, connection) = Connected(configured);
        using var _c = client;

        var player = PlayerObjectOfLastState(connection);
        var delay = player.GetProperty("static_delay_ms");

        Assert.Equal(JsonValueKind.Number, delay.ValueKind);
        Assert.Equal(expected, delay.GetInt32());
    }

    [Fact]
    public void ClientState_AlwaysCarriesStaticDelay_EvenAtZero()
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

    [Fact]
    public void SchedulerKeepsTheConfiguredValue_TheProjectionIsWireOnly()
    {
        // The clamp must not write back. A negative delay still schedules audio later; only the
        // report is constrained, and conflating the two would silently change playback timing.
        var sync = new ConvergedClock { StaticDelayMs = -200.0 };
        var (client, connection, _) = TestClient.Create(
            configure: options => options with { ClockSynchronizer = sync });
        using var _c = client;

        TestClient.CompleteHandshake(connection, "player@v1");

        Assert.Equal(0, PlayerObjectOfLastState(connection).GetProperty("static_delay_ms").GetInt32());
        Assert.Equal(-200.0, sync.StaticDelayMs);
    }

    [Fact]
    public async Task PlayerStateDelta_ProjectsTheSameWay()
    {
        // Two call sites build a client/state (the initial full state and the player delta).
        // Only one of them having the projection is exactly the defect shape this codebase
        // keeps hitting, so the delta is pinned separately rather than assumed.
        var (client, connection) = Connected(0.0);
        using var _c = client;

        await client.SendPlayerStateAsync(volume: 50, muted: false, staticDelayMs: -750.0);

        Assert.Equal(0, PlayerObjectOfLastState(connection).GetProperty("static_delay_ms").GetInt32());
    }
}
