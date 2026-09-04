using System.Buffers.Binary;
using Sendspin.SDK.Client;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Coverage for the artwork role: multi-channel client/state declaration, per-channel binary
/// dispatch (image + clear) with channel/timestamp, and the dynamic channel reconfiguration path
/// that spec PR #195 moved out of stream/request-format.
/// </summary>
public class SendspinClientServiceArtworkTests
{
    private static byte[] ArtworkBinary(byte type, long timestamp, byte[] image)
    {
        var buf = new byte[9 + image.Length];
        buf[0] = type;
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(1, 8), timestamp);
        image.CopyTo(buf, 9);
        return buf;
    }

    private static List<ArtworkChannelState> StateChannels(FakeSendspinConnection connection)
    {
        var state = connection.SentMessages.OfType<ClientStateMessage>().Last();
        Assert.NotNull(state.Payload.Artwork);
        return state.Payload.Artwork.Channels;
    }

    /// <summary>
    /// An artwork-only client. Deliberately without the player role: a player defers its initial
    /// client/state until the clock converges, which would leave these tests with no state
    /// message to read rather than with the artwork object they are about.
    /// </summary>
    private static (SendspinClientService Client, FakeSendspinConnection Connection) ArtworkClient(
        List<ArtworkChannelState>? channels = null)
    {
        var (client, connection, _) = TestClient.Create(configure: options =>
            options with
            {
                Capabilities = channels is null
                    ? new ClientCapabilities { Roles = new List<string> { "artwork@v1" } }
                    : new ClientCapabilities { Roles = new List<string> { "artwork@v1" }, ArtworkChannels = channels },
            });
        return (client, connection);
    }

    [Fact]
    public void ClientState_DeclaresAllConfiguredArtworkChannels()
    {
        // Spec PR #195 deleted artwork@v1_support: the channel declaration lives in the
        // client/state artwork object, and the wire names are width/height, not media_*.
        var (client, connection) = ArtworkClient(new List<ArtworkChannelState>
        {
            new() { Source = ArtworkSources.Album, Format = "jpeg", Width = 512, Height = 512 },
            new() { Source = ArtworkSources.Artist, Format = "png", Width = 256, Height = 256 },
        });
        using var _c = client;

        TestClient.CompleteHandshake(connection, "artwork@v1");

        var channels = StateChannels(connection);
        Assert.Equal(2, channels.Count);
        Assert.Equal(ArtworkSources.Artist, channels[1].Source);
        Assert.Equal("png", channels[1].Format);
        Assert.Equal(256, channels[1].Width);

        var json = Sendspin.SDK.Protocol.MessageSerializer.Serialize(
            connection.SentMessages.OfType<ClientStateMessage>().Last());
        Assert.Contains("\"width\":512", json);
        Assert.DoesNotContain("media_width", json);
    }

    [Fact]
    public void ClientHello_NoLongerCarriesArtworkSupport()
    {
        var (client, connection) = ArtworkClient();
        using var _c = client;

        TestClient.CompleteHandshake(connection, "artwork@v1");

        var json = Sendspin.SDK.Protocol.MessageSerializer.Serialize(
            connection.SentMessages.OfType<ClientHelloMessage>().Single());
        Assert.DoesNotContain("artwork@v1_support", json);
    }

    [Fact]
    public void ClientState_DefaultCapabilities_DeclaresSingleAlbumChannel()
    {
        var (client, connection) = ArtworkClient(); // default channel configuration
        using var _c = client;

        TestClient.CompleteHandshake(connection, "artwork@v1");

        var only = Assert.Single(StateChannels(connection));
        Assert.Equal(ArtworkSources.Album, only.Source);
        Assert.Equal("jpeg", only.Format);
        Assert.Equal(512, only.Width);
        Assert.Equal(512, only.Height);
    }

    [Fact]
    public void ClientState_CapsDeclaredChannelsAtFour()
    {
        // An array longer than four is a protocol error the server closes the connection over,
        // so an over-configured client is truncated rather than allowed to trip it.
        var (client, connection) = ArtworkClient(Enumerable.Range(0, 6)
            .Select(i => new ArtworkChannelState { Source = ArtworkSources.Album, Format = "jpeg", Width = i, Height = i })
            .ToList());
        using var _c = client;

        TestClient.CompleteHandshake(connection, "artwork@v1");

        var channels = StateChannels(connection);
        Assert.Equal(4, channels.Count);
        // The first four are kept, in order.
        Assert.Equal(0, channels[0].Width);
        Assert.Equal(3, channels[3].Width);
    }

    [Fact]
    public void ClientState_OmitsArtworkObject_WhenArtworkIsNotAnActiveRole()
    {
        var (client, connection) = ArtworkClient();
        using var _c = client;

        TestClient.CompleteHandshake(connection, "metadata@v1");

        var state = connection.SentMessages.OfType<ClientStateMessage>().Last();
        Assert.Null(state.Payload.Artwork);
    }

    [Theory]
    [InlineData(BinaryMessageTypes.Artwork0, 0)]
    [InlineData(BinaryMessageTypes.Artwork1, 1)]
    [InlineData(BinaryMessageTypes.Artwork2, 2)]
    [InlineData(BinaryMessageTypes.Artwork3, 3)]
    public void ArtworkBinary_RaisesReceivedWithChannelAndTimestamp(byte type, int expectedChannel)
    {
        // A timestamp with every byte distinct so a little-endian regression can't pass. The
        // local clock is frozen at that same instant, making the image due the moment it
        // arrives: this test's subject is channel/timestamp plumbing, not the display
        // scheduling that MediaDisplaySchedulingTests covers.
        const long timestamp = 0x0102030405060708;

        var (client, connection, _) = TestClient.Create(configure: options =>
            options with { PrecisionTimer = new FakePrecisionTimer { CurrentTime = timestamp } });
        using var _c = client;

        ArtworkReceivedEventArgs? received = null;
        client.ArtworkReceived += (_, e) => received = e;

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
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

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
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

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
    public async Task SetArtworkChannelAsync_ReannouncesTheFullArtworkObject()
    {
        var (client, connection) = ArtworkClient();
        using var _c = client;

        TestClient.CompleteHandshake(connection, "artwork@v1");
        int before = connection.SentMessages.OfType<ClientStateMessage>().Count();

        await client.SetArtworkChannelAsync(channel: 1, source: ArtworkSources.Artist, format: "png", width: 128, height: 128);

        var states = connection.SentMessages.OfType<ClientStateMessage>().ToList();
        Assert.Equal(before + 1, states.Count);

        var channels = states[^1].Payload.Artwork!.Channels;
        Assert.Equal(2, channels.Count);
        // Channel 0 is re-sent unchanged: the object is full state, not a per-channel delta.
        Assert.Equal(ArtworkSources.Album, channels[0].Source);
        Assert.Equal(ArtworkSources.Artist, channels[1].Source);
        Assert.Equal("png", channels[1].Format);
        Assert.Equal(128, channels[1].Width);
        Assert.Equal(128, channels[1].Height);
    }

    [Fact]
    public async Task SetArtworkChannelAsync_DisabledChannel_CarriesSourceAlone()
    {
        var (client, connection) = ArtworkClient();
        using var _c = client;

        TestClient.CompleteHandshake(connection, "artwork@v1");
        await client.SetArtworkChannelAsync(channel: 0, source: ArtworkSources.None);

        var json = Sendspin.SDK.Protocol.MessageSerializer.Serialize(
            connection.SentMessages.OfType<ClientStateMessage>().Last());
        Assert.Contains("\"source\":\"none\"", json);
        // format/width/height are required only when the source is not 'none'; sending them for
        // a disabled channel declares a size the client is not asking for.
        Assert.DoesNotContain("\"format\":", json);
        Assert.DoesNotContain("\"width\":", json);
        Assert.DoesNotContain("\"height\":", json);
    }

    [Fact]
    public async Task SetArtworkChannelAsync_FillsTheGapWithDisabledChannels()
    {
        // The wire array is positional from channel 0, so configuring channel 2 first cannot
        // silently renumber it to 1.
        var (client, connection) = ArtworkClient();
        using var _c = client;

        TestClient.CompleteHandshake(connection, "artwork@v1");
        await client.SetArtworkChannelAsync(channel: 2, source: ArtworkSources.Artist, format: "png", width: 64, height: 64);

        var channels = StateChannels(connection);
        Assert.Equal(3, channels.Count);
        Assert.Equal(ArtworkSources.None, channels[1].Source);
        Assert.Equal(ArtworkSources.Artist, channels[2].Source);
    }

    [Fact]
    public async Task SetArtworkChannelAsync_EnablingAGapFilledChannel_CarriesFormatAndSize()
    {
        // The documented way to turn a channel on is a source alone. The gap filler the SDK
        // inserts for the skipped channel used to null format/width/height, so this call put
        // source=album on the wire with the three fields the spec requires of an active channel
        // missing — a protocol error from an API call that looks entirely reasonable.
        var (client, connection) = ArtworkClient(); // the single default album channel
        using var _c = client;

        TestClient.CompleteHandshake(connection, "artwork@v1");
        await client.SetArtworkChannelAsync(channel: 1, source: ArtworkSources.Album);

        var channels = StateChannels(connection);
        Assert.Equal(2, channels.Count);

        var enabled = channels[1];
        Assert.Equal(ArtworkSources.Album, enabled.Source);
        Assert.Equal("jpeg", enabled.Format);
        Assert.Equal(512, enabled.Width);
        Assert.Equal(512, enabled.Height);

        // ...and the same on the wire, which is what the server reads.
        var json = Sendspin.SDK.Protocol.MessageSerializer.Serialize(
            connection.SentMessages.OfType<ClientStateMessage>().Last());
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var wire = doc.RootElement.GetProperty("payload").GetProperty("artwork")
            .GetProperty("channels")[1];

        Assert.Equal(ArtworkSources.Album, wire.GetProperty("source").GetString());
        Assert.Equal("jpeg", wire.GetProperty("format").GetString());
        Assert.Equal(512, wire.GetProperty("width").GetInt32());
        Assert.Equal(512, wire.GetProperty("height").GetInt32());
    }

    [Fact]
    public async Task GapFiller_StaysSourceOnlyOnTheWire_WhileItIsDisabled()
    {
        // The counterpart of the test above: a filler carries the defaults so it can be enabled
        // with a source alone, but ForWire must still omit them for as long as it is 'none'.
        var (client, connection) = ArtworkClient();
        using var _c = client;

        TestClient.CompleteHandshake(connection, "artwork@v1");
        await client.SetArtworkChannelAsync(channel: 2, source: ArtworkSources.Artist, format: "png", width: 64, height: 64);

        var json = Sendspin.SDK.Protocol.MessageSerializer.Serialize(
            connection.SentMessages.OfType<ClientStateMessage>().Last());
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var filler = doc.RootElement.GetProperty("payload").GetProperty("artwork")
            .GetProperty("channels")[1];

        Assert.Equal(ArtworkSources.None, filler.GetProperty("source").GetString());
        Assert.False(filler.TryGetProperty("format", out _));
        Assert.False(filler.TryGetProperty("width", out _));
        Assert.False(filler.TryGetProperty("height", out _));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public async Task SetArtworkChannelAsync_RejectsChannelOutsideZeroToThree(int channel)
    {
        var (client, connection) = ArtworkClient();
        using var _c = client;

        TestClient.CompleteHandshake(connection, "artwork@v1");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => client.SetArtworkChannelAsync(channel, source: ArtworkSources.Album, format: "jpeg", width: 64, height: 64));
    }
}
