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
            configure: options => options with { ClockSynchronizer = new FakeClockSynchronizer { IsConverged = true } });
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
    public async Task EnterExternalSource_WhileNotConnected_ThrowsBeforeFlippingFlag()
    {
        // The up-front guard, not the rollback: EnterExternalSourceAsync checks connection
        // state itself and throws before flipping IsExternalSource, rather than routing the
        // failure through the publisher's own guard (which skips silently — right for the
        // event-driven pipeline callers, wrong here: it would leave the flag flipped with
        // nothing ever told to the server). The flag must never flip and no message may be
        // sent. The catch-based rollback for a send that fails while Connected is pinned
        // separately below.
        var (client, connection) = SyncedClient(connected: false);
        using var _c = client;

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.EnterExternalSourceAsync());

        Assert.False(client.IsExternalSource);
        Assert.Empty(connection.SentMessages.OfType<ClientStateMessage>());
    }

    [Fact]
    public async Task EnterExternalSource_SendFailsWhileConnected_RollsBackFlagAndPropagates()
    {
        // The catch-based rollback itself, reachable only when the send throws while the
        // connection IS Connected (the up-front guard passes). This is the promoted
        // SendInitialClientStateAsync path: the latch was not yet set, so the notification
        // travels as the full initial message, and its failure must throw into the catch so
        // the notify-first contract holds — IsExternalSource back to its prior value, the
        // exception surfaced to the caller.
        var (client, connection) = SyncedClient();
        using var _c = client;
        connection.ThrowOnNextSend = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.EnterExternalSourceAsync());

        Assert.False(client.IsExternalSource);
    }

    [Fact]
    public async Task ExitExternalSource_SendFailsWhileConnected_RollsBackFlagAndPropagates()
    {
        var (client, connection) = SyncedClient();
        using var _c = client;
        await client.EnterExternalSourceAsync();

        connection.ThrowOnNextSend = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ExitExternalSourceAsync());

        // Still in external source: the server was never told otherwise.
        Assert.True(client.IsExternalSource);
    }
}
