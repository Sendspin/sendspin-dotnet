using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

public class SendspinClientServiceEventTests
{
    [Fact]
    public void ServerHello_RaisesTypedEventAndPopulatesLastServerHello()
    {
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection);

        ServerHelloPayload? received = null;
        client.ServerHelloReceived += (_, payload) => received = payload;

        const string helloJson = """
        {
            "type": "server/hello",
            "payload": {
                "server_id": "srv-abc",
                "name": "Kitchen",
                "version": 1,
                "active_roles": ["player@v1", "artwork@v1"],
                "connection_reason": "playback"
            }
        }
        """;

        connection.RaiseTextMessageReceived(helloJson);

        Assert.NotNull(received);
        Assert.Equal("srv-abc", received.ServerId);
        Assert.Equal("Kitchen", received.Name);
        Assert.Equal(1, received.Version);
        Assert.Equal(new[] { "player@v1", "artwork@v1" }, received.ActiveRoles);
        Assert.Equal("playback", received.ConnectionReason);

        Assert.NotNull(client.LastServerHello);
        Assert.Same(received, client.LastServerHello);

        // Scalar backcompat accessors still set:
        Assert.Equal("srv-abc", client.ServerId);
        Assert.Equal("Kitchen", client.ServerName);
        Assert.Equal("playback", client.ConnectionReason);
    }

    [Fact]
    public void ServerHello_EventFiresBeforeHandshakeCompletes()
    {
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection);

        ServerHelloPayload? seenPayload = null;
        string? serverIdAtEventTime = null;
        client.ServerHelloReceived += (_, payload) =>
        {
            seenPayload = payload;
            serverIdAtEventTime = client.ServerId;
        };

        connection.RaiseTextMessageReceived("""
            { "type": "server/hello", "payload": { "server_id": "srv-1", "version": 1, "active_roles": [] } }
            """);

        Assert.NotNull(seenPayload);
        // Subscribers observe the scalar property already set when the event fires.
        Assert.Equal("srv-1", serverIdAtEventTime);
    }
}
