using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Tests.Audio;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Coverage for the artwork role: multi-channel client/state declaration, the announce/part/cancel
/// image transfer (spec PRs #188, #266) with its channel/timestamp plumbing and protocol errors,
/// and the dynamic channel reconfiguration path that spec PR #195 moved out of
/// stream/request-format.
/// </summary>
public class SendspinClientServiceArtworkTests
{
    private static List<ArtworkChannelState> StateChannels(FakeSendspinConnection connection)
    {
        var state = connection.SentMessages.OfType<ClientStateMessage>().Last();
        Assert.NotNull(state.Payload.Artwork);
        return state.Payload.Artwork.Channels;
    }

    /// <summary>
    /// Client whose local clock is frozen at zero and maps server time to itself, so an image
    /// stamped ahead of zero stays pending until the test moves the clock.
    /// </summary>
    private static (SendspinClientService Client, FakeSendspinConnection Connection, FakePrecisionTimer Timer)
        SchedulingClient()
    {
        var timer = new FakePrecisionTimer();
        var (client, connection, _) = TestClient.Create(configure: options =>
            options with { PrecisionTimer = timer, ClockSynchronizer = new ConvergedClockSynchronizer() });
        return (client, connection, timer);
    }

    /// <summary>
    /// Client whose clock is converged, so it is available — an unavailable client discards image
    /// data — and the external-source flag alone decides otherwise.
    /// </summary>
    private static (SendspinClientService Client, FakeSendspinConnection Connection) SyncedClient()
    {
        var (client, connection, _) = TestClient.Create(configure: options =>
            options with { ClockSynchronizer = new FakeClockSynchronizer { IsConverged = true } });
        return (client, connection);
    }

    private static void AssertClosedOnProtocolError(FakeSendspinConnection connection)
    {
        Assert.Equal(ConnectionState.Disconnected, connection.State);

        // The goodbye reason list has no protocol-error value; see InboundMessageHardeningTests.
        Assert.Equal("unauthorized", connection.LastDisconnectReason);
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
    public async Task ReEnablingAnAppSuppliedDisabledChannel_WithSourceOnly_CarriesTheDefaults()
    {
        // An app can hand over a disabled channel with the format and size nulled. Re-enabling it
        // with a source alone must still declare the format/width/height the spec requires of an
        // active source, so the wire object carries the channel defaults rather than a bare source.
        var (client, connection) = ArtworkClient(new List<ArtworkChannelState>
        {
            new() { Source = ArtworkSources.None, Format = null, Width = null, Height = null },
        });
        using var _c = client;

        TestClient.CompleteHandshake(connection, "artwork@v1");

        await client.SetArtworkChannelAsync(channel: 0, source: ArtworkSources.Album);

        var only = Assert.Single(StateChannels(connection));
        Assert.Equal(ArtworkSources.Album, only.Source);
        Assert.Equal("jpeg", only.Format);
        Assert.Equal(512, only.Width);
        Assert.Equal(512, only.Height);
    }

    [Fact]
    public void ClientState_EmptyCapabilitiesList_IsNormalizedToOneDisabledChannel()
    {
        var (client, connection) = ArtworkClient(new List<ArtworkChannelState>());
        using var _c = client;

        TestClient.CompleteHandshake(connection, "artwork@v1");

        var only = Assert.Single(StateChannels(connection));
        Assert.Equal(ArtworkSources.None, only.Source);
        Assert.Null(only.Format);
        Assert.Null(only.Width);
        Assert.Null(only.Height);
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
            options with
            {
                PrecisionTimer = new FakePrecisionTimer { CurrentTime = timestamp },

                // Display binary is dropped while the client is unavailable (spec #266/#271); a
                // converged clock keeps this default (player) client available so the plumbing runs.
                ClockSynchronizer = new ConvergedClockSynchronizer(),
            });
        using var _c = client;

        ArtworkReceivedEventArgs? received = null;
        client.ArtworkReceived += (_, e) => received = e;

        var image = new byte[] { 1, 2, 3, 4 };
        connection.RaiseArtwork(type, timestamp, image);

        Assert.NotNull(received);
        Assert.Equal(expectedChannel, received.Channel);
        Assert.Equal(timestamp, received.Timestamp);
        Assert.Equal(image, received.ImageData);
    }

    [Fact]
    public void MultiPartImage_IsRaisedOnceTheReceivedDataReachesTotalSize()
    {
        var (client, connection) = SyncedClient();
        using var _c = client;

        var received = new List<ArtworkReceivedEventArgs>();
        client.ArtworkReceived += (_, e) => received.Add(e);

        connection.RaiseBinaryMessageReceived(ArtworkWire.Announce(BinaryMessageTypes.Artwork1, 777, 5));
        connection.RaiseBinaryMessageReceived(ArtworkWire.Part(BinaryMessageTypes.Artwork1, 1, 2));
        connection.RaiseBinaryMessageReceived(ArtworkWire.Part(BinaryMessageTypes.Artwork1, 3, 4));

        // "The transfer is complete when the received data reaches total_size", and not before.
        Assert.Empty(received);

        connection.RaiseBinaryMessageReceived(ArtworkWire.Part(BinaryMessageTypes.Artwork1, 5));

        var image = Assert.Single(received);
        Assert.Equal(1, image.Channel);
        Assert.Equal(777, image.Timestamp);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, image.ImageData);
        Assert.Null(connection.LastDisconnectReason);
    }

    [Fact]
    public void OverSizeAnnounce_IsCountedAndDiscarded_AndTheNextImageIsDelivered()
    {
        var (client, connection) = SyncedClient();
        using var _c = client;

        var received = new List<ArtworkReceivedEventArgs>();
        client.ArtworkReceived += (_, e) => received.Add(e);

        var oversize = ArtworkTransfer.MaxImageBytes + 1;
        connection.RaiseBinaryMessageReceived(ArtworkWire.Announce(BinaryMessageTypes.Artwork0, 1, oversize));

        // A part is at most MaxArtworkMessageSize bytes, so the image arrives in many.
        var part = new byte[BinaryMessageParser.MaxArtworkMessageSize - BinaryMessageParser.ArtworkPrefixSize];
        for (long sent = 0; sent < oversize;)
        {
            var next = (int)Math.Min(part.Length, oversize - sent);
            connection.RaiseBinaryMessageReceived(
                ArtworkWire.Part(BinaryMessageTypes.Artwork0, part.AsSpan(0, next).ToArray()));
            sent += next;
        }

        Assert.Empty(received);
        Assert.Null(connection.LastDisconnectReason);

        connection.RaiseArtwork(BinaryMessageTypes.Artwork0, 2, new byte[] { 7, 8 });

        var image = Assert.Single(received);
        Assert.Equal(new byte[] { 7, 8 }, image.ImageData);
        Assert.Null(connection.LastDisconnectReason);
    }

    [Fact]
    public void ZeroSizeAnnounce_RaisesClearedWithChannel_AndLeavesNoTransferInFlight()
    {
        // A converged clock keeps this default (player) client available; display binary is
        // dropped while unavailable (spec #266/#271).
        var (client, connection, _) = TestClient.Create(configure: options =>
            options with { ClockSynchronizer = new ConvergedClockSynchronizer() });
        using var _c = client;

        ArtworkClearedEventArgs? cleared = null;
        ArtworkReceivedEventArgs? received = null;
        client.ArtworkCleared += (_, e) => cleared = e;
        client.ArtworkReceived += (_, e) => received = e;

        // Channel 2 clear: an announce with total_size 0 "completes immediately, with no parts".
        connection.RaiseBinaryMessageReceived(ArtworkWire.Announce(BinaryMessageTypes.Artwork2, 777, 0));

        Assert.Null(received);
        Assert.NotNull(cleared);
        Assert.Equal(2, cleared.Channel);
        Assert.Equal(777, cleared.Timestamp);

        // Nothing is in flight after it, so the next announce is in sequence.
        connection.RaiseArtwork(BinaryMessageTypes.Artwork0, 778, new byte[] { 9 });

        Assert.NotNull(received);
        Assert.Null(connection.LastDisconnectReason);
    }

    [Fact]
    public void Cancel_DiscardsACompletePendingImage()
    {
        // The local clock stays at zero, so an image stamped ahead of it is complete but pending.
        var (client, connection, timer) = SchedulingClient();
        using var _c = client;

        var received = new List<ArtworkReceivedEventArgs>();
        client.ArtworkReceived += (_, e) => received.Add(e);

        connection.RaiseArtwork(BinaryMessageTypes.Artwork0, 5_000_000, new byte[] { 1 });
        connection.RaiseBinaryMessageReceived(ArtworkWire.Cancel(BinaryMessageTypes.Artwork0));

        // A past-stamped image on another channel is raised inline only when nothing is still
        // pending, so its arrival is also the proof that the cancelled image is gone.
        timer.CurrentTime = 10_000_000;
        connection.RaiseArtwork(BinaryMessageTypes.Artwork1, 1, new byte[] { 2 });

        var only = Assert.Single(received);
        Assert.Equal(1, only.Channel);
        Assert.Null(connection.LastDisconnectReason);
    }

    [Fact]
    public void Cancel_EndsTheTransferInFlightOnItsChannel()
    {
        var (client, connection) = SyncedClient();
        using var _c = client;

        var received = new List<ArtworkReceivedEventArgs>();
        client.ArtworkReceived += (_, e) => received.Add(e);

        connection.RaiseBinaryMessageReceived(ArtworkWire.Announce(BinaryMessageTypes.Artwork0, 1, 4));
        connection.RaiseBinaryMessageReceived(ArtworkWire.Part(BinaryMessageTypes.Artwork0, 1, 2));
        connection.RaiseBinaryMessageReceived(ArtworkWire.Cancel(BinaryMessageTypes.Artwork0));

        // "To replace an image still in flight, it cancels that transfer first": the announce
        // that follows is in sequence, and the image it carries is not joined to the old parts.
        connection.RaiseArtwork(BinaryMessageTypes.Artwork0, 2, new byte[] { 7, 8 });

        var only = Assert.Single(received);
        Assert.Equal(new byte[] { 7, 8 }, only.ImageData);
        Assert.Null(connection.LastDisconnectReason);
    }

    [Fact]
    public void Cancel_OnAnotherChannel_LeavesTheTransferInFlight()
    {
        var (client, connection) = SyncedClient();
        using var _c = client;

        ArtworkReceivedEventArgs? received = null;
        client.ArtworkReceived += (_, e) => received = e;

        connection.RaiseBinaryMessageReceived(ArtworkWire.Announce(BinaryMessageTypes.Artwork0, 1, 2));
        connection.RaiseBinaryMessageReceived(ArtworkWire.Cancel(BinaryMessageTypes.Artwork1));
        connection.RaiseBinaryMessageReceived(ArtworkWire.Part(BinaryMessageTypes.Artwork0, 1, 2));

        Assert.NotNull(received);
        Assert.Equal(new byte[] { 1, 2 }, received.ImageData);
    }

    [Fact]
    public void Announce_DiscardsTheChannelsPendingImage_BeforeItsOwnTransferCompletes()
    {
        var (client, connection, timer) = SchedulingClient();
        using var _c = client;

        var received = new List<ArtworkReceivedEventArgs>();
        client.ArtworkReceived += (_, e) => received.Add(e);

        connection.RaiseArtwork(BinaryMessageTypes.Artwork0, 5_000_000, new byte[] { 1 });

        // "An announce discards that channel's pending image" — the announce, not the image
        // it opens, which here never arrives.
        connection.RaiseBinaryMessageReceived(ArtworkWire.Announce(BinaryMessageTypes.Artwork0, 6_000_000, 2));
        connection.RaiseBinaryMessageReceived(ArtworkWire.Cancel(BinaryMessageTypes.Artwork0));

        timer.CurrentTime = 10_000_000;
        connection.RaiseArtwork(BinaryMessageTypes.Artwork1, 1, new byte[] { 2 });

        var only = Assert.Single(received);
        Assert.Equal(1, only.Channel);
    }

    [Fact]
    public async Task UnavailableClient_DiscardsImageData_ButStillCountsItsBytes()
    {
        var (client, connection) = SyncedClient();
        using var _c = client;

        var received = new List<ArtworkReceivedEventArgs>();
        client.ArtworkReceived += (_, e) => received.Add(e);

        await client.EnterExternalSourceAsync();
        connection.RaiseArtwork(BinaryMessageTypes.Artwork0, 1, new byte[] { 1, 2, 3 });

        Assert.Empty(received);

        // The discarded image's bytes were counted toward total_size, so its transfer completed:
        // were it still in flight, this announce would be a protocol error.
        await client.ExitExternalSourceAsync();
        connection.RaiseArtwork(BinaryMessageTypes.Artwork0, 2, new byte[] { 4 });

        var only = Assert.Single(received);
        Assert.Equal(new byte[] { 4 }, only.ImageData);
        Assert.Null(connection.LastDisconnectReason);
    }

    [Fact]
    public async Task ImageStartedWhileUnavailable_IsNotRaised_EvenIfTheClientReturnsMidTransfer()
    {
        var (client, connection) = SyncedClient();
        using var _c = client;

        var fired = false;
        client.ArtworkReceived += (_, _) => fired = true;

        await client.EnterExternalSourceAsync();
        connection.RaiseBinaryMessageReceived(ArtworkWire.Announce(BinaryMessageTypes.Artwork0, 1, 2));
        connection.RaiseBinaryMessageReceived(ArtworkWire.Part(BinaryMessageTypes.Artwork0, 1));
        await client.ExitExternalSourceAsync();
        connection.RaiseBinaryMessageReceived(ArtworkWire.Part(BinaryMessageTypes.Artwork0, 2));

        // Half the image was thrown away; what is left is not an image.
        Assert.False(fired);
        Assert.Null(connection.LastDisconnectReason);
    }

    [Fact]
    public async Task ImageStartedWhileAvailable_IsNotRaised_IfTheClientGoesUnavailableMidTransfer()
    {
        var (client, connection) = SyncedClient();
        using var _c = client;

        var received = new List<ArtworkReceivedEventArgs>();
        client.ArtworkReceived += (_, e) => received.Add(e);

        connection.RaiseBinaryMessageReceived(ArtworkWire.Announce(BinaryMessageTypes.Artwork0, 1, 2));
        connection.RaiseBinaryMessageReceived(ArtworkWire.Part(BinaryMessageTypes.Artwork0, 1));
        await client.EnterExternalSourceAsync();
        connection.RaiseBinaryMessageReceived(ArtworkWire.Part(BinaryMessageTypes.Artwork0, 2));

        Assert.Empty(received);

        // The part that arrived while unavailable was still counted, so the transfer completed
        // and the announce that follows is in sequence rather than a reason to close.
        await client.ExitExternalSourceAsync();
        connection.RaiseArtwork(BinaryMessageTypes.Artwork0, 2, new byte[] { 4 });

        var only = Assert.Single(received);
        Assert.Equal(new byte[] { 4 }, only.ImageData);
        Assert.Null(connection.LastDisconnectReason);
    }

    [Fact]
    public async Task UnavailableClient_StillAppliesAClear()
    {
        var (client, connection) = SyncedClient();
        using var _c = client;

        ArtworkClearedEventArgs? cleared = null;
        client.ArtworkCleared += (_, e) => cleared = e;

        await client.EnterExternalSourceAsync();

        // An empty image has no data to discard, and the server will not send the clear twice.
        connection.RaiseBinaryMessageReceived(ArtworkWire.Announce(BinaryMessageTypes.Artwork0, 1, 0));

        Assert.NotNull(cleared);
    }

    [Fact]
    public void PartWithNoTransferInFlight_ClosesTheConnection()
    {
        var (client, connection) = SyncedClient();
        using var _c = client;

        connection.RaiseBinaryMessageReceived(ArtworkWire.Part(BinaryMessageTypes.Artwork0, 1, 2, 3));

        AssertClosedOnProtocolError(connection);
    }

    [Fact]
    public void PartOnAChannelOtherThanTheTransfers_ClosesTheConnection()
    {
        var (client, connection) = SyncedClient();
        using var _c = client;

        connection.RaiseBinaryMessageReceived(ArtworkWire.Announce(BinaryMessageTypes.Artwork0, 1, 4));
        connection.RaiseBinaryMessageReceived(ArtworkWire.Part(BinaryMessageTypes.Artwork1, 1, 2));

        AssertClosedOnProtocolError(connection);
    }

    [Fact]
    public void PartExtendingPastTotalSize_ClosesTheConnection_AndRaisesNoImage()
    {
        var (client, connection) = SyncedClient();
        using var _c = client;

        var fired = false;
        client.ArtworkReceived += (_, _) => fired = true;

        connection.RaiseBinaryMessageReceived(ArtworkWire.Announce(BinaryMessageTypes.Artwork0, 1, 2));
        connection.RaiseBinaryMessageReceived(ArtworkWire.Part(BinaryMessageTypes.Artwork0, 1, 2, 3));

        Assert.False(fired);
        AssertClosedOnProtocolError(connection);
    }

    [Fact]
    public void AnnounceWhileATransferIsInFlight_ClosesTheConnection()
    {
        var (client, connection) = SyncedClient();
        using var _c = client;

        connection.RaiseBinaryMessageReceived(ArtworkWire.Announce(BinaryMessageTypes.Artwork0, 1, 4));

        // On any channel: at most one transfer is in flight across all of the role's channels.
        connection.RaiseBinaryMessageReceived(ArtworkWire.Announce(BinaryMessageTypes.Artwork1, 1, 4));

        AssertClosedOnProtocolError(connection);
    }

    [Theory]
    [InlineData(new byte[] { BinaryMessageTypes.Artwork0 })]
    [InlineData(new byte[] { BinaryMessageTypes.Artwork0, 0x04 })]
    [InlineData(new byte[] { BinaryMessageTypes.Artwork0, 0x03 })]
    [InlineData(new byte[] { BinaryMessageTypes.Artwork0, 0x01, 0 })]
    [InlineData(new byte[] { BinaryMessageTypes.Artwork0, 0x02, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0 })]
    public void MalformedArtworkMessage_ClosesTheConnection(byte[] message)
    {
        var (client, connection) = SyncedClient();
        using var _c = client;

        var fired = false;
        client.ArtworkReceived += (_, _) => fired = true;
        client.ArtworkCleared += (_, _) => fired = true;

        connection.RaiseBinaryMessageReceived(message);

        Assert.False(fired);
        AssertClosedOnProtocolError(connection);
    }

    [Fact]
    public void TransferInFlight_DoesNotSurviveTheConnection()
    {
        var (client, connection) = SyncedClient();
        using var _c = client;

        ArtworkReceivedEventArgs? received = null;
        client.ArtworkReceived += (_, e) => received = e;

        connection.RaiseBinaryMessageReceived(ArtworkWire.Announce(BinaryMessageTypes.Artwork0, 1, 4));
        connection.RaiseBinaryMessageReceived(ArtworkWire.Part(BinaryMessageTypes.Artwork0, 1, 2));

        connection.SimulateConnectionLoss();

        // The next connection's server knows nothing of the old transfer: its first announce
        // must be in sequence, and its image must not be joined to the old parts.
        connection.RaiseArtwork(BinaryMessageTypes.Artwork0, 2, new byte[] { 7, 8 });

        Assert.NotNull(received);
        Assert.Equal(new byte[] { 7, 8 }, received.ImageData);
        Assert.Null(connection.LastDisconnectReason);
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
