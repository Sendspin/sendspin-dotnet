using Sendspin.SDK.Client;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Tests.Audio;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Coverage for the external_source enter/exit API: availability-only client/state notifications,
/// the IsExternalSource flag, and rollback when the server notification can't be sent.
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
    public async Task EnterExternalSource_RollsBackWhenNotificationFails()
    {
        // EnterExternalSourceAsync checks connection state itself and throws before flipping
        // IsExternalSource, rather than routing the failure through the publisher's own guard
        // (which skips silently — right for the event-driven pipeline callers, wrong here: it
        // would leave the flag flipped with nothing ever told to the server). The flag must stay
        // in its prior state and no message may be sent.
        var (client, connection) = SyncedClient(connected: false);
        using var _c = client;

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.EnterExternalSourceAsync());

        Assert.False(client.IsExternalSource);
        Assert.Empty(connection.SentMessages.OfType<ClientStateMessage>());
    }
}
