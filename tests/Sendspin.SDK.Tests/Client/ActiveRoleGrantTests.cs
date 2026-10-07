using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// The role grant a client acts on is what the server activated out of what the client listed,
/// and a <c>server/state</c> role object counts only while its role is in that grant (#333).
/// </summary>
public class ActiveRoleGrantTests
{
    private static (SendspinClientService Client, FakeSendspinConnection Connection) ClientListing(
        params string[] roles)
    {
        var (client, connection, _) = TestClient.Create(configure: options => options with
        {
            ClockSynchronizer = new ConvergedClockSynchronizer(),
            Capabilities = new ClientCapabilities { Roles = [.. roles] },
        });
        return (client, connection);
    }

    [Fact]
    public void Activate_WithARoleTheClientDidNotList_DropsThatRoleAndKeepsTheRest()
    {
        // README.md, "Priority and Activation": "A server MUST NOT activate a role or version
        // the client did not list in supported_roles."
        var (client, connection) = ClientListing("metadata@v1");
        using var _c = client;

        TestClient.CompleteHandshake(connection, "metadata@v1", "player@v1");

        Assert.Equal(ConnectionState.Connected, connection.State);
        Assert.Equal(new[] { "metadata@v1" }, client.LastServerHello!.ActiveRoles);

        // No pipeline behind it, so no player object for the server to stream at.
        var state = connection.SentMessages.OfType<ClientStateMessage>().Last();
        Assert.Null(state.Payload.Player);
    }

    [Fact]
    public void Activate_WithAVersionTheClientDidNotList_DropsIt()
    {
        var (client, connection) = ClientListing("metadata@v1");
        using var _c = client;

        TestClient.CompleteHandshake(connection, "metadata@v2");

        Assert.Empty(client.LastServerHello!.ActiveRoles);
    }

    [Fact]
    public void ActiveRoles_InServerHello_AreNotAGrant()
    {
        // server/hello defines no active_roles, and messaging.md has it that "A client treats a
        // first server/activate that omits it as carrying an empty active_roles" - so a list
        // read out of the hello must not be what such an activate persists.
        var (client, connection) = ClientListing("metadata@v1");
        using var _c = client;

        connection.RaiseTextMessageReceived("""
            {"type":"server/hello","payload":{"name":"srv","active_roles":["metadata@v1"]}}
            """);
        Assert.Empty(client.LastServerHello!.ActiveRoles);

        connection.RaiseTextMessageReceived("""
            {"type":"server/activate","payload":{"activities":["playback"]}}
            """);

        Assert.NotNull(client.LastServerActivate);
        Assert.Empty(client.LastServerHello!.ActiveRoles);
    }

    [Theory]
    [InlineData("""{"metadata":{"title":"Track A"}}""")]
    [InlineData("""{"controller":{"volume":40,"muted":false,"repeat":"all","shuffle":true}}""")]
    [InlineData("""{"color":{"primary":[1,2,3]}}""")]
    public void ServerState_ObjectForARoleThatIsNotActive_IsIgnored(string payload)
    {
        // messaging.md: "metadata?: object - only if the metadata role is active", and the same
        // for controller and color.
        var (client, connection) = ClientListing("metadata@v1", "controller@v1", "color@v1", "artwork@v1");
        using var _c = client;

        TestClient.CompleteHandshake(connection, "artwork@v1");
        var groupChanges = 0;
        client.GroupStateChanged += (_, _) => groupChanges++;

        connection.RaiseTextMessageReceived($$"""{"type":"server/state","payload":{{payload}}}""");

        Assert.Null(client.CurrentGroup?.Metadata);
        Assert.Null(client.CurrentGroup?.Repeat);
        Assert.Null(client.CurrentGroup?.Colors.Primary);
        Assert.Equal(0, groupChanges);
    }

    [Fact]
    public void ServerState_AfterItsRoleWasDeactivated_DoesNotBringTheStateBack()
    {
        var (client, connection) = ClientListing("metadata@v1", "controller@v1");
        using var _c = client;

        TestClient.CompleteHandshake(connection, "metadata@v1", "controller@v1");
        connection.RaiseTextMessageReceived("""
            {"type":"server/state","payload":{"metadata":{"title":"Track A"},"controller":{"volume":40}}}
            """);
        connection.RaiseTextMessageReceived("""
            {"type":"server/activate","payload":{"activities":["playback"],"active_roles":["controller@v1"]}}
            """);
        Assert.Null(client.CurrentGroup?.Metadata);

        connection.RaiseTextMessageReceived("""
            {"type":"server/state","payload":{"metadata":{"title":"Track B"},"controller":{"volume":55}}}
            """);

        Assert.Null(client.CurrentGroup?.Metadata);

        // The role still active is applied from the same message.
        Assert.Equal(55, client.CurrentGroup?.Volume);
    }
}
