using Sendspin.SDK.Client;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// messaging.md has <c>server/hello</c> and <c>client/hello</c> each "Sent once per connection",
/// and connection.md says of a re-handshake that "Neither <c>server/hello</c> nor
/// <c>client/hello</c> is re-sent". A <c>server/hello</c> after a connection's first is
/// therefore ignored: it is not answered, and it does not replace what the first one and the
/// activations since have recorded (#330).
/// </summary>
public class RepeatedServerHelloTests
{
    private const string SecondHello = """
        {"type":"server/hello","payload":{"name":"other"}}
        """;

    private static int ClientHellosSent(FakeSendspinConnection connection)
        => connection.SnapshotSentMessages().OfType<ClientHelloMessage>().Count();

    [Fact]
    public void SecondHello_MidSession_KeepsTheRoleGrant_AndIsNotAnswered()
    {
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        TestClient.CompleteHandshake(connection, "player@v1");
        Assert.Equal(1, ClientHellosSent(connection));

        connection.RaiseTextMessageReceived(SecondHello);

        Assert.Equal(["player@v1"], client.LastServerHello!.ActiveRoles);
        Assert.Equal("srv", client.ServerName);
        Assert.Equal(1, ClientHellosSent(connection));

        // Ignored, not closed over.
        Assert.Null(connection.LastDisconnectReason);
    }

    [Fact]
    public void SecondHello_BeforeTheFirstActivate_IsNotAnswered()
    {
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        connection.RaiseTextMessageReceived("""
            {"type":"server/hello","payload":{"name":"srv"}}
            """);
        connection.RaiseTextMessageReceived(SecondHello);

        Assert.Equal(1, ClientHellosSent(connection));
        Assert.Equal("srv", client.ServerName);
    }

    [Fact]
    public void Hello_AfterARehandshake_IsARepeat_AndLeavesTheSetAsideRolesAlone()
    {
        var (client, connection, session) = TestClient.Create();
        using var _c = client;

        TestClient.CompleteHandshake(connection, "player@v1");

        // An in-band re-handshake as the client observes it: a fresh handshake hash.
        session.MatchedPsk = new NoisePsk(NoiseConstants.SentinelPsk.ToArray(), PskCategory.LongTerm);
        session.HandshakeHash = Enumerable.Repeat((byte)0xAB, 32).ToArray();

        connection.RaiseTextMessageReceived(SecondHello);

        // The activate that follows a re-handshake persists the roles when it omits them.
        connection.RaiseTextMessageReceived("""
            {"type":"server/activate","payload":{"activities":["playback"]}}
            """);
        Assert.Equal(["player@v1"], client.LastServerHello!.ActiveRoles);
        Assert.Equal(1, ClientHellosSent(connection));
    }

    [Fact]
    public void Hello_OnANewConnection_IsThatConnectionsFirst()
    {
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        TestClient.CompleteHandshake(connection, "player@v1");

        connection.SimulateConnectionLoss();
        connection.SimulateReconnected();
        connection.RaiseTextMessageReceived(SecondHello);

        Assert.Equal(2, ClientHellosSent(connection));
        Assert.Equal("other", client.ServerName);
    }
}
