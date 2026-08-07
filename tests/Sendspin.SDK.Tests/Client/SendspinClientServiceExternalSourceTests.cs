using Sendspin.SDK.Client;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Coverage for the external_source enter/exit API: availability-only client/state notifications,
/// the IsExternalSource flag, and rollback when the server notification fails.
/// </summary>
public class SendspinClientServiceExternalSourceTests
{
    private static bool? LastAvailable(FakeSendspinConnection connection) =>
        connection.SentMessages.OfType<ClientStateMessage>().Last().Payload.Available;

    [Fact]
    public async Task EnterExternalSource_SendsAvailableFalseAndSetsFlag()
    {
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        await client.EnterExternalSourceAsync();

        Assert.Equal(false, LastAvailable(connection));
        Assert.True(client.IsExternalSource);
    }

    [Fact]
    public async Task ExitExternalSource_ReportsAvailableTrueAndClearsFlag()
    {
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        await client.EnterExternalSourceAsync();
        await client.ExitExternalSourceAsync();

        Assert.Equal(true, LastAvailable(connection));
        Assert.False(client.IsExternalSource);
    }

    [Fact]
    public async Task EnterExternalSource_RollsBackWhenNotificationFails()
    {
        // A disconnected connection rejects the send, like the real transport would. The enter
        // notification is unguarded, so the throw must propagate and roll back the local flag.
        var (client, connection, _) = TestClient.Create(connected: false);
        connection.EnforceConnectionState = true;
        using var _c = client;

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.EnterExternalSourceAsync());

        // Notification failed, so the local state must not have flipped.
        Assert.False(client.IsExternalSource);
    }
}
