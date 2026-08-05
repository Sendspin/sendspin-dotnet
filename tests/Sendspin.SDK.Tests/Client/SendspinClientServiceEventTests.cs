using Sendspin.SDK.Client;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

public class SendspinClientServiceEventTests
{
    [Fact]
    public void ServerHello_RaisesTypedEventAndPopulatesLastServerHello()
    {
        var (client, connection, session) = TestClient.Create();
        using var _c = client;

        // Under the encrypted protocol the server identity comes from the Noise session
        // (server/init), not from server/hello.
        session.ServerId = "srv-abc";

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

        // The encrypted handshake completes on the initial server/activate, which is also
        // where ServerHelloReceived fires.
        connection.RaiseTextMessageReceived(helloJson);
        connection.RaiseTextMessageReceived("""
            {"type":"server/activate","payload":{"activities":["playback"],"active_roles":["player@v1","artwork@v1"]}}
            """);

        Assert.NotNull(received);
        Assert.Equal("srv-abc", received.ServerId);
        Assert.Equal("Kitchen", received.Name);
        Assert.Equal(1, received.Version);
        Assert.Equal(new[] { "player@v1", "artwork@v1" }, received.ActiveRoles);

        Assert.NotNull(client.LastServerHello);
        Assert.Same(received, client.LastServerHello);

        // Scalar backcompat accessors still set:
        Assert.Equal("srv-abc", client.ServerId);
        Assert.Equal("Kitchen", client.ServerName);
    }

    [Fact]
    public void ServerHello_EventFiresBeforeHandshakeCompletes()
    {
        var (client, connection, session) = TestClient.Create();
        using var _c = client;
        session.ServerId = "srv-1";

        ServerHelloPayload? seenPayload = null;
        string? serverIdAtEventTime = null;
        client.ServerHelloReceived += (_, payload) =>
        {
            seenPayload = payload;
            serverIdAtEventTime = client.ServerId;
        };

        TestClient.CompleteHandshake(connection);

        Assert.NotNull(seenPayload);
        // Subscribers observe the scalar property already set when the event fires.
        Assert.Equal("srv-1", serverIdAtEventTime);
    }

    [Fact]
    public void StreamStart_WithPlayerAndArtwork_RaisesEventAndCachesPayload()
    {
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        StreamStartPayload? received = null;
        client.StreamStartReceived += (_, payload) => received = payload;

        const string json = """
        {
            "type": "stream/start",
            "payload": {
                "player": { "codec": "pcm", "sample_rate": 48000, "channels": 2, "bit_depth": 16 },
                "artwork": { "channels": [ { "source": "album", "format": "jpeg", "width": 512, "height": 512 } ] }
            }
        }
        """;

        connection.RaiseTextMessageReceived(json);

        Assert.NotNull(received);
        Assert.NotNull(received.Format);
        Assert.Equal("pcm", received.Format.Codec);
        Assert.NotNull(received.Artwork);
        Assert.Single(received.Artwork.Channels);
        Assert.Same(received, client.LastStreamStart);
    }

    [Fact]
    public void StreamStart_ArtworkOnly_StillRaisesEvent()
    {
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        StreamStartPayload? received = null;
        client.StreamStartReceived += (_, payload) => received = payload;

        const string json = """
        {
            "type": "stream/start",
            "payload": {
                "artwork": { "channels": [ { "source": "album", "format": "jpeg", "width": 256, "height": 256 } ] }
            }
        }
        """;

        connection.RaiseTextMessageReceived(json);

        Assert.NotNull(received);
        Assert.Null(received.Format);
        Assert.NotNull(received.Artwork);
        Assert.Equal(256, received.Artwork.Channels[0].Width);
    }

    [Fact]
    public void StreamStart_PlayerOnly_ArtworkNullOnPayload()
    {
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        StreamStartPayload? received = null;
        client.StreamStartReceived += (_, payload) => received = payload;

        const string json = """
        {
            "type": "stream/start",
            "payload": {
                "player": { "codec": "pcm", "sample_rate": 44100, "channels": 2, "bit_depth": 16 }
            }
        }
        """;

        connection.RaiseTextMessageReceived(json);

        Assert.NotNull(received);
        Assert.NotNull(received.Format);
        Assert.Null(received.Artwork);
    }
}
