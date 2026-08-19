using System.Text.Json;
using Sendspin.SDK.Client;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// A client/state role object belongs exactly when the server activated that role. aiosendspin
/// flags one for an inactive role ("carried a {family} object for an inactive role") and rejects
/// the client when run with <c>allow_noncompliant_clients=False</c>.
/// </summary>
/// <remarks>
/// Also covers the other side of the same omission: the initial message never built a
/// <c>source</c> object at all, so a line-sense signal reported during the pre-initial window
/// had nowhere to go and was discarded (#114).
/// </remarks>
public class ClientStateRoleObjectTests
{
    private static JsonElement LastStatePayload(FakeSendspinConnection connection)
    {
        string json = MessageSerializer.Serialize(
            connection.SentMessages.OfType<ClientStateMessage>().Last());

        return JsonDocument.Parse(json).RootElement.GetProperty("payload").Clone();
    }

    private static (SendspinClientService Client, FakeSendspinConnection Connection) Create(
        string[] roles, bool lineSense = false)
    {
        var (client, connection, _) = TestClient.Create(
            configure: options => options with
            {
                ClockSynchronizer = new ConvergedClock(),
                Capabilities = new ClientCapabilities
                {
                    Roles = [.. roles],
                    SourceRoleSupport = lineSense ? new SourceRoleSupport { LineSense = true } : null,
                },
            });
        return (client, connection);
    }

    [Fact]
    public void InitialState_OmitsPlayerObject_WhenPlayerIsNotActive()
    {
        var (client, connection) = Create(["source@v1"], lineSense: true);
        using var _c = client;

        TestClient.CompleteHandshake(connection, "source@v1");

        Assert.False(
            LastStatePayload(connection).TryGetProperty("player", out _),
            "a player object for a role the server did not activate is a client deviation");
    }

    [Fact]
    public void InitialState_CarriesPlayerObject_WhenPlayerIsActive()
    {
        // Positive control: the test above passes trivially if the player object stops being
        // built at all.
        var (client, connection) = Create(["player@v1"]);
        using var _c = client;

        TestClient.CompleteHandshake(connection, "player@v1");

        Assert.True(LastStatePayload(connection).TryGetProperty("player", out _));
    }

    [Fact]
    public async Task PlayerDelta_IsSuppressed_WhenPlayerIsNotActive()
    {
        // The same rule on the delta path. Enforcing it only on the initial message would leave
        // the deviation reachable through every app-driven volume change.
        var (client, connection) = Create(["source@v1"], lineSense: true);
        using var _c = client;

        TestClient.CompleteHandshake(connection, "source@v1");
        int before = connection.SentMessages.OfType<ClientStateMessage>().Count();

        await client.SendPlayerStateAsync(volume: 42, muted: false);

        Assert.Equal(before, connection.SentMessages.OfType<ClientStateMessage>().Count());
    }

    [Fact]
    public async Task SourceSignal_ReportedBeforeTheInitialState_IsCarriedByIt()
    {
        // #114 item 1: the signal used to be dropped inside the pre-initial window, and a client
        // that reports only transitions never sent it again — so the server never learned there
        // was signal until it changed.
        var (client, connection) = Create(["source@v1"], lineSense: true);
        using var _c = client;

        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        await client.SetSourceSignalAsync(present: true);

        Assert.Empty(connection.SentMessages.OfType<ClientStateMessage>());

        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["playback"],"active_roles":["source@v1"]}}""");

        var source = LastStatePayload(connection).GetProperty("source");
        Assert.Equal("present", source.GetProperty("signal").GetString());
    }

    [Fact]
    public void InitialState_OmitsSourceObject_WhenNothingHasReportedASignal()
    {
        // signal is the source object's only field and is itself optional, so with nothing
        // reported there is nothing truthful to say — asserting 'absent' would invent it.
        var (client, connection) = Create(["source@v1"], lineSense: true);
        using var _c = client;

        TestClient.CompleteHandshake(connection, "source@v1");

        Assert.False(LastStatePayload(connection).TryGetProperty("source", out _));
    }

    [Fact]
    public async Task SourceSignal_SurvivesAReconnect()
    {
        // The signal describes the device's input, not the session, so a reconnect's initial
        // state reports what is still true rather than starting blank.
        var (client, connection) = Create(["source@v1"], lineSense: true);
        using var _c = client;

        TestClient.CompleteHandshake(connection, "source@v1");
        await client.SetSourceSignalAsync(present: true);

        TestClient.CompleteHandshake(connection, "source@v1");

        Assert.Equal(
            "present",
            LastStatePayload(connection).GetProperty("source").GetProperty("signal").GetString());
    }

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
