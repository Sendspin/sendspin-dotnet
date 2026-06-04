using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Coverage for the artwork role: multi-channel hello advertisement, per-channel binary dispatch
/// (image + clear) with channel/timestamp, and the stream/request-format artwork send path.
/// </summary>
public class SendspinClientServiceArtworkTests
{
    private const string ServerHelloJson = """
        { "type": "server/hello", "payload": { "server_id": "s", "version": 1, "active_roles": ["artwork@v1"] } }
        """;

    private static byte[] ArtworkBinary(byte type, long timestamp, byte[] image)
    {
        var buf = new byte[9 + image.Length];
        buf[0] = type;
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(1, 8), timestamp);
        image.CopyTo(buf, 9);
        return buf;
    }

    [Fact]
    public async Task ClientHello_AdvertisesAllConfiguredArtworkChannels()
    {
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection,
            capabilities: new ClientCapabilities
            {
                ArtworkChannels = new List<ArtworkChannelSpec>
                {
                    new() { Source = ArtworkSources.Album, Format = "jpeg", MediaWidth = 512, MediaHeight = 512 },
                    new() { Source = ArtworkSources.Artist, Format = "png", MediaWidth = 256, MediaHeight = 256 },
                },
            });

        var connectTask = client.ConnectAsync(new Uri("ws://test"));
        connection.RaiseTextMessageReceived(ServerHelloJson); // completes the handshake
        await connectTask;

        var hello = connection.SentMessages.OfType<ClientHelloMessage>().Single();
        var channels = hello.Payload.ArtworkV1Support?.Channels;
        Assert.NotNull(channels);
        Assert.Equal(2, channels.Count);
        Assert.Equal(ArtworkSources.Artist, channels[1].Source);
        Assert.Equal("png", channels[1].Format);
        Assert.Equal(256, channels[1].MediaWidth);
    }

    [Fact]
    public async Task ClientHello_DefaultCapabilities_AdvertisesSingleAlbumChannel()
    {
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection); // default capabilities

        var connectTask = client.ConnectAsync(new Uri("ws://test"));
        connection.RaiseTextMessageReceived(ServerHelloJson);
        await connectTask;

        var channels = connection.SentMessages.OfType<ClientHelloMessage>().Single().Payload.ArtworkV1Support?.Channels;
        Assert.NotNull(channels);
        var only = Assert.Single(channels);
        Assert.Equal(ArtworkSources.Album, only.Source);
        Assert.Equal("jpeg", only.Format);
        Assert.Equal(512, only.MediaWidth);
        Assert.Equal(512, only.MediaHeight);
    }

    [Fact]
    public async Task ClientHello_CapsAdvertisedChannelsAtFour()
    {
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection,
            capabilities: new ClientCapabilities
            {
                ArtworkChannels = Enumerable.Range(0, 6)
                    .Select(i => new ArtworkChannelSpec { Source = ArtworkSources.Album, Format = "jpeg", MediaWidth = i, MediaHeight = i })
                    .ToList(),
            });

        var connectTask = client.ConnectAsync(new Uri("ws://test"));
        connection.RaiseTextMessageReceived(ServerHelloJson);
        await connectTask;

        var channels = connection.SentMessages.OfType<ClientHelloMessage>().Single().Payload.ArtworkV1Support?.Channels;
        Assert.NotNull(channels);
        Assert.Equal(4, channels.Count);
        // The first four are kept, in order.
        Assert.Equal(0, channels[0].MediaWidth);
        Assert.Equal(3, channels[3].MediaWidth);
    }

    [Theory]
    [InlineData(BinaryMessageTypes.Artwork0, 0)]
    [InlineData(BinaryMessageTypes.Artwork1, 1)]
    [InlineData(BinaryMessageTypes.Artwork2, 2)]
    [InlineData(BinaryMessageTypes.Artwork3, 3)]
    public void ArtworkBinary_RaisesReceivedWithChannelAndTimestamp(byte type, int expectedChannel)
    {
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection);

        ArtworkReceivedEventArgs? received = null;
        client.ArtworkReceived += (_, e) => received = e;

        // A timestamp with every byte distinct so a little-endian regression can't pass.
        const long timestamp = 0x0102030405060708;
        var image = new byte[] { 1, 2, 3, 4 };
        connection.RaiseBinaryMessageReceived(ArtworkBinary(type, timestamp, image));

        Assert.NotNull(received);
        Assert.Equal(expectedChannel, received.Channel);
        Assert.Equal(timestamp, received.Timestamp);
        Assert.Equal(image, received.ImageData);
    }

    [Fact]
    public void MalformedArtworkBinary_RaisesNoEvent()
    {
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection);

        var fired = false;
        client.ArtworkReceived += (_, _) => fired = true;
        client.ArtworkCleared += (_, _) => fired = true;

        // Shorter than the 9-byte header (type + 8-byte timestamp): not a valid frame, and
        // distinct from a valid empty (clear) frame which is exactly 9 bytes.
        connection.RaiseBinaryMessageReceived(new byte[] { BinaryMessageTypes.Artwork0, 1, 2, 3 });

        Assert.False(fired);
    }

    [Fact]
    public void EmptyArtworkBinary_RaisesClearedWithChannel()
    {
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection);

        ArtworkClearedEventArgs? cleared = null;
        ArtworkReceivedEventArgs? received = null;
        client.ArtworkCleared += (_, e) => cleared = e;
        client.ArtworkReceived += (_, e) => received = e;

        // Channel 2 clear: type byte + timestamp, no image data.
        connection.RaiseBinaryMessageReceived(ArtworkBinary(BinaryMessageTypes.Artwork2, 777, Array.Empty<byte>()));

        Assert.Null(received);
        Assert.NotNull(cleared);
        Assert.Equal(2, cleared.Channel);
        Assert.Equal(777, cleared.Timestamp);
    }

    [Fact]
    public async Task RequestArtworkFormatAsync_SendsArtworkRequestFormat()
    {
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection);

        await client.RequestArtworkFormatAsync(channel: 1, source: ArtworkSources.None, format: "png", mediaWidth: 128, mediaHeight: 128);

        var msg = Assert.IsType<StreamRequestFormatMessage>(connection.SentMessages.Single());
        var artwork = msg.Payload.Artwork;
        Assert.NotNull(artwork);
        Assert.Equal(1, artwork.Channel);
        Assert.Equal(ArtworkSources.None, artwork.Source);
        Assert.Equal("png", artwork.Format);
        Assert.Equal(128, artwork.MediaWidth);
        Assert.Equal(128, artwork.MediaHeight);
    }

    [Fact]
    public async Task RequestArtworkFormatAsync_OmitsUnsetFields()
    {
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection);

        await client.RequestArtworkFormatAsync(channel: 0, source: ArtworkSources.None);

        var msg = Assert.IsType<StreamRequestFormatMessage>(connection.SentMessages.Single());
        var json = Sendspin.SDK.Protocol.MessageSerializer.Serialize(msg);
        Assert.Contains("\"channel\":0", json);
        Assert.Contains("\"source\":\"none\"", json);
        // Note: "format" also appears in the type "stream/request-format", so match the JSON key.
        Assert.DoesNotContain("\"format\":", json);
        Assert.DoesNotContain("\"media_width\":", json);
    }
}
