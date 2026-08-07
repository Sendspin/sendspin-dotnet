using Sendspin.SDK.Client;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Tests.Audio;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Coverage for the external_source enter/exit API: availability-only client/state notifications,
/// the IsExternalSource flag, and behavior when the server notification can't be sent.
/// </summary>
public class SendspinClientServiceExternalSourceTests
{
    /// <summary>Clock already converged, so entering/exiting external source isn't confounded by
    /// the default (unconverged) clock-sync gate on a default player@v1 client — see
    /// <c>ClientAvailabilityTests</c> for the composed formula these two inputs both feed.</summary>
    private static (SendspinClientService Client, FakeSendspinConnection Connection) SyncedClient(bool connected = true)
    {
        var (client, connection, _) = TestClient.Create(
            connected: connected,
            configure: options => options.ClockSynchronizer = new FakeClockSynchronizer { IsConverged = true });
        return (client, connection);
    }

    private static bool? LastAvailable(FakeSendspinConnection connection) =>
        connection.SentMessages.OfType<ClientStateMessage>().Last().Payload.Available;

    [Fact]
    public async Task EnterExternalSource_SendsAvailableFalseAndSetsFlag()
    {
        var (client, connection) = SyncedClient();
        using var _c = client;

        await client.EnterExternalSourceAsync();

        Assert.Equal(false, LastAvailable(connection));
        Assert.True(client.IsExternalSource);
    }

    [Fact]
    public async Task ExitExternalSource_ReportsAvailableTrueAndClearsFlag()
    {
        var (client, connection) = SyncedClient();
        using var _c = client;

        await client.EnterExternalSourceAsync();
        await client.ExitExternalSourceAsync();

        Assert.Equal(true, LastAvailable(connection));
        Assert.False(client.IsExternalSource);
    }

    [Fact]
    public async Task EnterExternalSource_WhileDisconnected_UpdatesFlagWithoutNotifying()
    {
        // EnterExternalSourceAsync now routes its wire notification through the single
        // availability publisher (PublishAvailabilityAsync), which guards on connection state the
        // same way the pipeline error/recovery paths always have: a publish attempted while not
        // Connected is silently skipped rather than thrown. That is a deliberate behavior change
        // from the previous unguarded send (which threw and left IsExternalSource unset on
        // failure) — see the task report. The reconnect handshake resynchronizes availability via
        // SendInitialClientStateAsync, so this local flag does not go stale for long.
        var (client, connection) = SyncedClient(connected: false);
        connection.EnforceConnectionState = true;
        using var _c = client;

        await client.EnterExternalSourceAsync();

        Assert.True(client.IsExternalSource);
        Assert.Empty(connection.SentMessages.OfType<ClientStateMessage>());
    }
}
