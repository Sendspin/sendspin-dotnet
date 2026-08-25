using System.Buffers.Binary;
using Sendspin.SDK.Client;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Display-timestamp scheduling for the visualizer and artwork roles (#198, #199) and for the
/// scheduled <c>metadata</c> and <c>color</c> updates of spec #135 (pending merge): each carries a
/// server-clock time at which its data takes effect, which the SDK translates to the local clock
/// and holds against. The roles differ on lateness — a stale visualizer frame is never rendered,
/// whereas late artwork and late state are applied immediately.
/// </summary>
/// <remarks>
/// Every test here freezes the local clock with <see cref="FakePrecisionTimer"/> and advances it
/// by hand, so "due", "held", and "raised at its display time" are all decided by a clock the test
/// controls rather than by elapsed wall time (#239). Whether an item surfaced is then settled by
/// an event — <see cref="WaitUntilAsync"/> for one that must arrive, <see cref="DrainPastAsync"/>
/// for one that must not — never by sleeping long enough to assume it would have.
/// </remarks>
public class MediaDisplaySchedulingTests
{
    /// <summary>An arbitrary "now" far enough from zero to stamp frames on either side of it.</summary>
    private const long Now = 10_000_000;

    private static byte[] Frame(byte type, long timestamp, byte[] data)
    {
        var buf = new byte[9 + data.Length];
        buf[0] = type;
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(1, 8), timestamp);
        data.CopyTo(buf, 9);
        return buf;
    }

    private static byte[] LoudnessFrame(long timestamp, ushort value)
    {
        var data = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(data, value);
        return Frame(BinaryMessageTypes.VisualizerLoudness, timestamp, data);
    }

    private static byte[] ArtworkBinary(long timestamp, byte[] image, byte type = BinaryMessageTypes.Artwork0)
    {
        var buf = new byte[9 + image.Length];
        buf[0] = type;
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(1, 8), timestamp);
        image.CopyTo(buf, 9);
        return buf;
    }

    /// <summary>
    /// Client whose local clock is frozen at <paramref name="now"/> and whose clock synchronizer
    /// maps server to client time identically, so a frame's raw timestamp is also its local
    /// display time and the arithmetic in each test stays readable.
    /// </summary>
    private static (SendspinClientService Client, FakeSendspinConnection Connection, FakePrecisionTimer Timer)
        SchedulingClient(
            long now = Now,
            int bufferCapacity = 65_536,
            IClockSynchronizer? clockSynchronizer = null,
            FakeAudioPipeline? audioPipeline = null)
    {
        var timer = new FakePrecisionTimer { CurrentTime = now };
        var (client, connection, _) = TestClient.Create(configure: options =>
            options with
            {
                PrecisionTimer = timer,
                ClockSynchronizer = clockSynchronizer ?? new ConvergedClockSynchronizer(),
                AudioPipeline = audioPipeline,
                Capabilities = new ClientCapabilities
                {
                    Roles = new List<string> { "visualizer@v1", "artwork@v1" },
                    VisualizerSupport = new VisualizerSupport
                    {
                        BufferCapacity = bufferCapacity,
                        RateMax = 30,
                        Types = new List<string> { VisualizerTypes.Loudness },
                    },
                },
            });
        return (client, connection, timer);
    }

    /// <summary>
    /// Waits for the scheduler loop to act on a clock the test has already moved. The deadline is
    /// a stuck-test guard, not a schedule: every wait here is for a loop pass, never for wall time
    /// to reach a display moment.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {because}");
            await Task.Delay(10);
        }
    }

    /// <summary>Artwork channel the drain marker uses, so no test's own images collide with it.</summary>
    private const int MarkerChannel = 3;

    /// <summary>
    /// Moves the frozen clock past <paramref name="through"/> and waits for a marker the
    /// scheduler can only raise once it has processed everything due up to that point, so
    /// "this item never surfaced" is decided by an event rather than by a sleep.
    /// </summary>
    /// <remarks>
    /// The marker is an empty image on <see cref="MarkerChannel"/> stamped one microsecond after
    /// <paramref name="through"/>. Anything due at or before <paramref name="through"/> is taken
    /// in the same dispatch pass as the marker at the latest, and a pass raises its state updates
    /// and visualizer frames before its artwork — so had the item survived, it would already have
    /// surfaced by the time this returns.
    /// </remarks>
    /// <param name="through">Local-clock time to drain past, in microseconds.</param>
    /// <param name="clock">
    /// The client's synchronizer, for a test that gave it a non-zero offset. The marker arrives on
    /// the wire in server time like every other message, so it has to be stamped through the same
    /// translation the data under test goes through. Omit when the offset is zero and the two
    /// clocks coincide.
    /// </param>
    private static async Task DrainPastAsync(
        SendspinClientService client,
        FakeSendspinConnection connection,
        FakePrecisionTimer timer,
        long through,
        ConvergedClockSynchronizer? clock = null)
    {
        var marker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnCleared(object? sender, ArtworkClearedEventArgs e)
        {
            if (e.Channel == MarkerChannel)
            {
                marker.TrySetResult();
            }
        }

        client.ArtworkCleared += OnCleared;
        try
        {
            connection.RaiseBinaryMessageReceived(ArtworkBinary(
                clock is null ? through + 1 : clock.ClientToServerTime(through + 1),
                Array.Empty<byte>(),
                (byte)(BinaryMessageTypes.Artwork0 + MarkerChannel)));
            timer.CurrentTime = through + 1;
            await marker.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            client.ArtworkCleared -= OnCleared;
        }
    }

    [Fact]
    public void VisualizerFrame_StampedInThePast_IsDropped()
    {
        var (client, connection, _) = SchedulingClient();
        using var _c = client;

        var frames = new List<VisualizerFrame>();
        client.VisualizationReceived += (_, f) => frames.Add(f);

        // A frame whose display moment passed well before it arrived: the spec says stale
        // visualization frames are never rendered.
        connection.RaiseBinaryMessageReceived(
            LoudnessFrame(Now - MediaDisplayScheduler.StaleThresholdMicroseconds - 1, 40_000));

        Assert.Empty(frames);
    }

    [Fact]
    public void VisualizerFrame_SlightlyLateButWithinThreshold_IsRaisedImmediately()
    {
        var (client, connection, _) = SchedulingClient();
        using var _c = client;

        var frames = new List<VisualizerFrame>();
        client.VisualizationReceived += (_, f) => frames.Add(f);

        // Just inside the threshold. Dropping every frame that misses its deadline by a
        // scheduling quantum would blank the visualizer, so the C++ reference tolerates this
        // much lateness and so does the SDK.
        connection.RaiseBinaryMessageReceived(
            LoudnessFrame(Now - MediaDisplayScheduler.StaleThresholdMicroseconds + 1, 40_000));

        var only = Assert.Single(frames);
        Assert.Equal(40_000, only.Loudness);
    }

    [Fact]
    public void VisualizerFrame_StampedInTheFuture_IsHeldUntilItsDisplayTime()
    {
        var (client, connection, _) = SchedulingClient();
        using var _c = client;

        var frames = new List<VisualizerFrame>();
        client.VisualizationReceived += (_, f) => frames.Add(f);

        // Servers send ahead of display time — that is what buffer_capacity is for — so this
        // frame must not surface on arrival, or the visualization runs ahead of the audio.
        connection.RaiseBinaryMessageReceived(LoudnessFrame(Now + 5_000_000, 40_000));

        Assert.Empty(frames);
    }

    [Fact]
    public async Task VisualizerFrame_StampedInTheFuture_IsRaisedAtItsTranslatedDisplayTime()
    {
        // A non-zero offset is what makes the translation observable: the frame is stamped in
        // server time, four seconds beyond the local moment it belongs to, so a scheduler that
        // held it against the raw timestamp would still be holding it when this test ends.
        const long offsetMicroseconds = 4_000_000;
        var clock = new ConvergedClockSynchronizer { OffsetMicroseconds = offsetMicroseconds };
        var (client, connection, timer) = SchedulingClient(clockSynchronizer: clock);
        using var _c = client;

        var frames = new List<VisualizerFrame>();
        client.VisualizationReceived += (_, f) => frames.Add(f);

        long displayTime = Now + 1_000;
        long serverTimestamp = clock.ClientToServerTime(displayTime);
        connection.RaiseBinaryMessageReceived(LoudnessFrame(serverTimestamp, 40_000));

        // Not on arrival...
        Assert.Empty(frames);

        // ...nor at any local moment short of the translated display time.
        await DrainPastAsync(client, connection, timer, Now + 500, clock);
        Assert.Empty(frames);

        // ...but at that moment, carrying the server timestamp it arrived with.
        timer.CurrentTime = displayTime;
        await WaitUntilAsync(() => frames.Count == 1, "the scheduled visualizer frame");

        Assert.Equal(serverTimestamp, frames[0].Timestamp);
        Assert.Equal(40_000, frames[0].Loudness);
    }

    [Fact]
    public async Task VisualizerFrames_HeldForTheFuture_AreRaisedInTimestampOrder()
    {
        var (client, connection, timer) = SchedulingClient();
        using var _c = client;

        var frames = new List<VisualizerFrame>();
        client.VisualizationReceived += (_, f) => frames.Add(f);

        // Arriving out of order: what orders the events is the display timestamp, not arrival.
        connection.RaiseBinaryMessageReceived(LoudnessFrame(Now + 3_000, 300));
        connection.RaiseBinaryMessageReceived(LoudnessFrame(Now + 1_000, 100));
        connection.RaiseBinaryMessageReceived(LoudnessFrame(Now + 2_000, 200));

        Assert.Empty(frames);

        // Move the clock past all three and let the loop drain them.
        timer.CurrentTime = Now + 3_000;
        await WaitUntilAsync(() => frames.Count == 3, "all three frames");

        Assert.Equal(new[] { 100, 200, 300 }, frames.Select(f => f.Loudness!.Value).ToArray());
    }

    [Fact]
    public async Task VisualizerFrames_BeyondAdvertisedBufferCapacity_DropOldestFirst()
    {
        // 11 bytes on the wire per loudness frame (1 type + 8 timestamp + 2 data), so a 22-byte
        // capacity holds two. The SDK advertises buffer_capacity as the volume of undisplayed
        // visualization it can hold; honouring it is what keeps the advertisement truthful.
        var (client, connection, timer) = SchedulingClient(bufferCapacity: 22);
        using var _c = client;

        var frames = new List<VisualizerFrame>();
        client.VisualizationReceived += (_, f) => frames.Add(f);

        connection.RaiseBinaryMessageReceived(LoudnessFrame(Now + 1_000, 100));
        connection.RaiseBinaryMessageReceived(LoudnessFrame(Now + 2_000, 200));
        connection.RaiseBinaryMessageReceived(LoudnessFrame(Now + 3_000, 300));

        timer.CurrentTime = Now + 3_000;
        await WaitUntilAsync(() => frames.Count == 2, "the two frames within capacity");

        Assert.Equal(new[] { 200, 300 }, frames.Select(f => f.Loudness!.Value).ToArray());
    }

    [Fact]
    public void Artwork_StampedInThePast_IsRaisedImmediately()
    {
        var (client, connection, _) = SchedulingClient();
        using var _c = client;

        ArtworkReceivedEventArgs? received = null;
        client.ArtworkReceived += (_, e) => received = e;

        // Unlike a visualizer frame, artwork is never dropped for lateness: a timestamp already
        // in the past means display it now, however far past it is.
        connection.RaiseBinaryMessageReceived(ArtworkBinary(Now - 60_000_000, new byte[] { 1, 2, 3 }));

        Assert.NotNull(received);
        Assert.Equal(new byte[] { 1, 2, 3 }, received.ImageData);
    }

    [Fact]
    public void Artwork_StampedInTheFuture_IsHeldUntilItsDisplayTime()
    {
        var (client, connection, _) = SchedulingClient();
        using var _c = client;

        var received = new List<ArtworkReceivedEventArgs>();
        client.ArtworkReceived += (_, e) => received.Add(e);

        // The gapless case: the next track's cover, pre-sent with the timestamp it becomes
        // current at. Displaying it on arrival would change the art mid-track.
        connection.RaiseBinaryMessageReceived(ArtworkBinary(Now + 5_000_000, new byte[] { 9 }));

        Assert.Empty(received);
    }

    [Fact]
    public async Task Artwork_StampedInTheFuture_IsRaisedAtItsTranslatedDisplayTime()
    {
        // The artwork twin of the visualizer case: this role translates its timestamp with the
        // same clock offset, so the same non-zero offset separates a scheduler that converts
        // from one that schedules against the raw server value.
        const long offsetMicroseconds = 4_000_000;
        var clock = new ConvergedClockSynchronizer { OffsetMicroseconds = offsetMicroseconds };
        var (client, connection, timer) = SchedulingClient(clockSynchronizer: clock);
        using var _c = client;

        var received = new List<ArtworkReceivedEventArgs>();
        client.ArtworkReceived += (_, e) => received.Add(e);

        long displayTime = Now + 1_000;
        long serverTimestamp = clock.ClientToServerTime(displayTime);
        connection.RaiseBinaryMessageReceived(ArtworkBinary(serverTimestamp, new byte[] { 9 }));

        Assert.Empty(received);

        await DrainPastAsync(client, connection, timer, Now + 500, clock);
        Assert.Empty(received);

        timer.CurrentTime = displayTime;
        await WaitUntilAsync(() => received.Count == 1, "the scheduled artwork");

        // The event carries the server timestamp, not the local time it was translated to.
        Assert.Equal(serverTimestamp, received[0].Timestamp);
        Assert.Equal(new byte[] { 9 }, received[0].ImageData);
    }

    [Fact]
    public async Task Artwork_NewerImageForTheSameChannel_SupersedesTheOneStillHeld()
    {
        var (client, connection, timer) = SchedulingClient();
        using var _c = client;

        var received = new List<ArtworkReceivedEventArgs>();
        client.ArtworkReceived += (_, e) => received.Add(e);

        // "Latest wins" is per channel: only the second image may ever be displayed on
        // channel 0, while channel 1 is untouched by it.
        connection.RaiseBinaryMessageReceived(
            ArtworkBinary(Now + 1_000, new byte[] { 1 }, BinaryMessageTypes.Artwork0));
        connection.RaiseBinaryMessageReceived(
            ArtworkBinary(Now + 2_000, new byte[] { 2 }, BinaryMessageTypes.Artwork0));
        connection.RaiseBinaryMessageReceived(
            ArtworkBinary(Now + 3_000, new byte[] { 3 }, BinaryMessageTypes.Artwork1));

        timer.CurrentTime = Now + 3_000;
        await WaitUntilAsync(() => received.Count == 2, "both channels' images");

        Assert.Equal(new byte[] { 2 }, received[0].ImageData);
        Assert.Equal(0, received[0].Channel);
        Assert.Equal(new byte[] { 3 }, received[1].ImageData);
        Assert.Equal(1, received[1].Channel);
    }

    [Fact]
    public async Task ArtworkClear_StampedInTheFuture_IsHeldThenRaisedAsCleared()
    {
        var (client, connection, timer) = SchedulingClient();
        using var _c = client;

        var cleared = new List<ArtworkClearedEventArgs>();
        client.ArtworkCleared += (_, e) => cleared.Add(e);

        // An empty image is a clear, and it is scheduled exactly as an image is.
        connection.RaiseBinaryMessageReceived(
            ArtworkBinary(Now + 1_000, Array.Empty<byte>(), BinaryMessageTypes.Artwork2));

        Assert.Empty(cleared);

        timer.CurrentTime = Now + 1_000;
        await WaitUntilAsync(() => cleared.Count == 1, "the scheduled clear");

        Assert.Equal(2, cleared[0].Channel);
    }

    /// <summary>
    /// <c>static_delay_ms</c> compensates for hardware beyond the audio port, and the spec
    /// applies it to the player role alone: the visualizer and artwork roles translate their
    /// display timestamps with the clock offset only. Applying it here too would show every
    /// visual ahead of the sound it belongs to by the whole delay — up to the 5 s the setting
    /// allows — and ahead of what the C++ reference client shows.
    /// </summary>
    /// <remarks>
    /// Runs against a real <see cref="KalmanClockSynchronizer"/>, not a fake: the distinction
    /// under test is one only the real synchronizer draws, since it is the thing that subtracts
    /// the delay in the conversion the audio path uses.
    /// </remarks>
    [Theory]
    [InlineData(0.0)]
    [InlineData(5_000.0)]
    public async Task MediaDisplayTimes_AreNotShiftedByOutputDelay(double outputDelayMs)
    {
        var clock = new KalmanClockSynchronizer { OutputDelayMs = outputDelayMs };
        var (client, connection, timer) = SchedulingClient(clockSynchronizer: clock);
        using var _c = client;

        var frames = new List<VisualizerFrame>();
        var artwork = new List<ArtworkReceivedEventArgs>();
        client.VisualizationReceived += (_, f) => frames.Add(f);
        client.ArtworkReceived += (_, e) => artwork.Add(e);

        connection.RaiseBinaryMessageReceived(LoudnessFrame(Now + 1_000, 100));
        connection.RaiseBinaryMessageReceived(ArtworkBinary(Now + 1_000, new byte[] { 9 }));

        // Had the delay been folded in, both would have looked 5 s overdue on arrival: the
        // frame dropped as stale, the artwork displayed at once.
        Assert.Empty(frames);
        Assert.Empty(artwork);

        // Due at the timestamp itself, wherever the speakers are.
        timer.CurrentTime = Now + 1_000;
        await WaitUntilAsync(
            () => frames.Count == 1 && artwork.Count == 1, "both items at their display time");
    }

    [Fact]
    public async Task StreamClear_WithNoRoles_DiscardsMediaStillPending()
    {
        var (client, connection, timer) = SchedulingClient();
        using var _c = client;

        var frames = new List<VisualizerFrame>();
        var received = new List<ArtworkReceivedEventArgs>();
        client.VisualizationReceived += (_, f) => frames.Add(f);
        client.ArtworkReceived += (_, e) => received.Add(e);

        connection.RaiseBinaryMessageReceived(LoudnessFrame(Now + 1_000, 100));
        connection.RaiseBinaryMessageReceived(ArtworkBinary(Now + 1_000, new byte[] { 1 }));

        // A seek: buffered visualization is cleared and playback continues from what follows.
        // Handled synchronously, so the flush has happened by the time this returns.
        connection.RaiseTextMessageReceived("""{"type":"stream/clear","payload":{}}""");

        await DrainPastAsync(client, connection, timer, Now + 5_000);
        Assert.Empty(frames);
        Assert.Empty(received);
    }

    [Fact]
    public async Task StreamEnd_WithNoRoles_DiscardsMediaStillPending()
    {
        var (client, connection, timer) = SchedulingClient();
        using var _c = client;

        var frames = new List<VisualizerFrame>();
        client.VisualizationReceived += (_, f) => frames.Add(f);

        connection.RaiseBinaryMessageReceived(LoudnessFrame(Now + 1_000, 100));

        // stream/end is dispatched fire-and-forget, but its handler flushes the display roles
        // before its first await — so the flush has already happened when this call returns,
        // and no sleep is needed to separate it from the clock advance below.
        connection.RaiseTextMessageReceived("""{"type":"stream/end","payload":{"reason":"eos"}}""");

        await DrainPastAsync(client, connection, timer, Now + 5_000);
        Assert.Empty(frames);
    }

    /// <summary>
    /// A teardown naming <c>player</c> ends the audio and nothing else: buffered visualization
    /// and artwork already sent belong to roles it did not name, and the C++ reference client
    /// keeps both.
    /// </summary>
    [Fact]
    public async Task StreamClear_NamingOnlyThePlayer_KeepsMediaPending()
    {
        var (client, connection, timer) = SchedulingClient();
        using var _c = client;

        var frames = new List<VisualizerFrame>();
        var artwork = new List<ArtworkReceivedEventArgs>();
        client.VisualizationReceived += (_, f) => frames.Add(f);
        client.ArtworkReceived += (_, e) => artwork.Add(e);

        connection.RaiseBinaryMessageReceived(LoudnessFrame(Now + 1_000, 100));
        connection.RaiseBinaryMessageReceived(ArtworkBinary(Now + 1_000, new byte[] { 1 }));

        connection.RaiseTextMessageReceived(
            """{"type":"stream/clear","payload":{"server_transmitted":1,"roles":["player"]}}""");

        timer.CurrentTime = Now + 1_000;
        await WaitUntilAsync(
            () => frames.Count == 1 && artwork.Count == 1, "the media the clear did not name");
    }

    /// <summary>
    /// The mirror case, and the one the player gate makes easy to get wrong: <c>visualizer</c>
    /// is exactly the role that gate turns away, so a flush placed behind it would never run for
    /// the message whose data this scheduler is holding.
    /// </summary>
    [Fact]
    public async Task StreamEnd_NamingOnlyTheVisualizer_DropsItsFramesAndKeepsTheRest()
    {
        var pipe = new FakeAudioPipeline();
        var (client, connection, timer) = SchedulingClient(audioPipeline: pipe);
        using var _c = client;

        var frames = new List<VisualizerFrame>();
        var artwork = new List<ArtworkReceivedEventArgs>();
        client.VisualizationReceived += (_, f) => frames.Add(f);
        client.ArtworkReceived += (_, e) => artwork.Add(e);

        connection.RaiseBinaryMessageReceived(LoudnessFrame(Now + 1_000, 100));
        connection.RaiseBinaryMessageReceived(ArtworkBinary(Now + 1_000, new byte[] { 1 }));

        connection.RaiseTextMessageReceived(
            """{"type":"stream/end","payload":{"server_transmitted":1,"roles":["visualizer"]}}""");

        // Artwork is dispatched after any frame due in the same pass, so its arrival is the
        // point at which a surviving frame would have surfaced too.
        timer.CurrentTime = Now + 1_000;
        await WaitUntilAsync(() => artwork.Count == 1, "the artwork the end did not name");
        Assert.Empty(frames);
        Assert.Equal(0, pipe.StopCount);
    }

    /// <summary>
    /// The stream/clear twin of the visualizer-named end: the player gate turns the message
    /// away before the pipeline clear, so a flush placed behind the gate would never run for
    /// the role the clear names.
    /// </summary>
    [Fact]
    public async Task StreamClear_NamingOnlyTheVisualizer_DropsItsFramesAndKeepsTheRest()
    {
        var pipe = new FakeAudioPipeline();
        var (client, connection, timer) = SchedulingClient(audioPipeline: pipe);
        using var _c = client;

        var frames = new List<VisualizerFrame>();
        var artwork = new List<ArtworkReceivedEventArgs>();
        client.VisualizationReceived += (_, f) => frames.Add(f);
        client.ArtworkReceived += (_, e) => artwork.Add(e);

        connection.RaiseBinaryMessageReceived(LoudnessFrame(Now + 1_000, 100));
        connection.RaiseBinaryMessageReceived(ArtworkBinary(Now + 1_000, new byte[] { 1 }));

        connection.RaiseTextMessageReceived(
            """{"type":"stream/clear","payload":{"server_transmitted":1,"roles":["visualizer"]}}""");

        // Artwork is dispatched after any frame due in the same pass, so its arrival is the
        // point at which a surviving frame would have surfaced too.
        timer.CurrentTime = Now + 1_000;
        await WaitUntilAsync(() => artwork.Count == 1, "the artwork the clear did not name");
        Assert.Empty(frames);
        Assert.Equal(0, pipe.ClearCount);
    }

    /// <summary>
    /// An omitted <c>roles</c> means every stream; a present but empty one names no role at all,
    /// and so ends nothing — for the media held here as much as for the audio pipeline.
    /// </summary>
    [Fact]
    public async Task StreamTeardown_WithAnEmptyRoleArray_KeepsMediaPending()
    {
        var (client, connection, timer) = SchedulingClient();
        using var _c = client;

        var frames = new List<VisualizerFrame>();
        var artwork = new List<ArtworkReceivedEventArgs>();
        client.VisualizationReceived += (_, f) => frames.Add(f);
        client.ArtworkReceived += (_, e) => artwork.Add(e);

        connection.RaiseBinaryMessageReceived(LoudnessFrame(Now + 1_000, 100));
        connection.RaiseBinaryMessageReceived(ArtworkBinary(Now + 1_000, new byte[] { 1 }));

        connection.RaiseTextMessageReceived(
            """{"type":"stream/clear","payload":{"server_transmitted":1,"roles":[]}}""");
        connection.RaiseTextMessageReceived(
            """{"type":"stream/end","payload":{"server_transmitted":2,"roles":[]}}""");

        // Both handlers reach their flush decision before returning — stream/clear is fully
        // synchronous and stream/end flushes before its first await — so a flush either of them
        // wrongly performed has already happened here, ahead of the clock advance.
        timer.CurrentTime = Now + 1_000;
        await WaitUntilAsync(
            () => frames.Count == 1 && artwork.Count == 1, "the media neither message named");
    }

    [Fact]
    public async Task ConnectionLoss_DiscardsMediaStillPending()
    {
        var (client, connection, timer) = SchedulingClient();
        using var _c = client;

        var frames = new List<VisualizerFrame>();
        client.VisualizationReceived += (_, f) => frames.Add(f);

        connection.RaiseBinaryMessageReceived(LoudnessFrame(Now + 1_000, 100));

        // The clock synchronizer resets on re-handshake, so a display time computed against the
        // old offset cannot be honoured on the next connection. Raised synchronously.
        connection.SimulateConnectionLoss();

        await DrainPastAsync(client, connection, timer, Now + 5_000);
        Assert.Empty(frames);
    }

    // -- Scheduled metadata and color updates (spec #135, pending merge) --------------------

    /// <summary>A <c>server/state</c> carrying only the given <c>metadata</c> role object.</summary>
    private static string MetadataState(string metadata) =>
        """{"type":"server/state","payload":{"metadata":""" + metadata + "}}";

    /// <summary>A <c>server/state</c> carrying only the given <c>color</c> role object.</summary>
    private static string ColorState(string color) =>
        """{"type":"server/state","payload":{"color":""" + color + "}}";

    [Fact]
    public void Metadata_StampedInTheFuture_LeavesTheAppliedStateUntouched()
    {
        var (client, connection, _) = SchedulingClient();
        using var _c = client;

        connection.RaiseTextMessageReceived(
            MetadataState($$"""{"timestamp":{{Now - 1}},"title":"First"}"""));

        var seen = new List<string?>();
        client.GroupStateChanged += (_, g) => seen.Add(g.Metadata?.Title);

        // The gapless case the spec is written for: the next track's metadata, timed to the
        // audible track change. Merging it on receipt would relabel the track still playing.
        connection.RaiseTextMessageReceived(
            MetadataState($$"""{"timestamp":{{Now + 1_000}},"title":"Second"}"""));

        Assert.Equal("First", client.CurrentGroup!.Metadata!.Title);
        Assert.DoesNotContain("Second", seen);
    }

    [Fact]
    public async Task Metadata_StampedInTheFuture_IsAppliedAtItsDisplayTime()
    {
        var (client, connection, timer) = SchedulingClient();
        using var _c = client;

        connection.RaiseTextMessageReceived(
            MetadataState($$"""{"timestamp":{{Now - 1}},"title":"First"}"""));
        connection.RaiseTextMessageReceived(
            MetadataState($$"""{"timestamp":{{Now + 1_000}},"title":"Second","album":"Later"}"""));

        var applied = new List<string?>();
        client.GroupStateChanged += (_, g) => applied.Add(g.Metadata?.Title);

        timer.CurrentTime = Now + 1_000;
        await WaitUntilAsync(() => applied.Contains("Second"), "the scheduled metadata");

        // Applied as a merge onto the current state, not as a replacement of it.
        var meta = client.CurrentGroup!.Metadata!;
        Assert.Equal("Second", meta.Title);
        Assert.Equal("Later", meta.Album);
        Assert.Equal(Now + 1_000, meta.Timestamp);
    }

    [Fact]
    public async Task Metadata_NewerFutureUpdate_ReplacesTheHeldOne_EvenStampedEarlier()
    {
        var (client, connection, timer) = SchedulingClient();
        using var _c = client;

        // Newest wins outright: clients never compare timestamps between messages, so the
        // second update takes the slot even though it is due before the one it displaces —
        // and the displaced one must never surface.
        connection.RaiseTextMessageReceived(
            MetadataState($$"""{"timestamp":{{Now + 5_000}},"title":"Displaced"}"""));
        connection.RaiseTextMessageReceived(
            MetadataState($$"""{"timestamp":{{Now + 1_000}},"title":"Winner"}"""));

        timer.CurrentTime = Now + 1_000;
        await WaitUntilAsync(() => client.CurrentGroup?.Metadata?.Title == "Winner", "the replacement");

        await DrainPastAsync(client, connection, timer, Now + 5_000);
        Assert.Equal("Winner", client.CurrentGroup!.Metadata!.Title);
    }

    [Fact]
    public async Task Metadata_StampedNow_AppliesImmediatelyAndDiscardsTheHeldUpdate()
    {
        var (client, connection, timer) = SchedulingClient();
        using var _c = client;

        connection.RaiseTextMessageReceived(
            MetadataState($$"""{"timestamp":{{Now + 5_000}},"title":"Scheduled"}"""));

        // A present timestamp is not a future one: it applies on receipt, and takes the
        // scheduled update with it (the server cancels by re-sending now-timestamped values).
        connection.RaiseTextMessageReceived(
            MetadataState($$"""{"timestamp":{{Now}},"title":"Cancelled to"}"""));

        Assert.Equal("Cancelled to", client.CurrentGroup!.Metadata!.Title);

        await DrainPastAsync(client, connection, timer, Now + 5_000);
        Assert.Equal("Cancelled to", client.CurrentGroup.Metadata!.Title);
    }

    [Fact]
    public async Task MetadataRoleObject_ExplicitNull_ClearsNowAndDiscardsTheHeldUpdate()
    {
        var (client, connection, timer) = SchedulingClient();
        using var _c = client;

        connection.RaiseTextMessageReceived(
            MetadataState($$"""{"timestamp":{{Now - 1}},"title":"First"}"""));
        connection.RaiseTextMessageReceived(
            MetadataState($$"""{"timestamp":{{Now + 1_000}},"title":"Scheduled"}"""));

        // messaging.md: a null role object clears the role's state, taking effect immediately
        // and discarding any pending scheduled update — the role has left active_roles, so
        // nothing it had queued may still arrive.
        connection.RaiseTextMessageReceived(MetadataState("null"));

        Assert.Null(client.CurrentGroup!.Metadata);

        await DrainPastAsync(client, connection, timer, Now + 1_000);
        Assert.Null(client.CurrentGroup.Metadata);
    }

    [Fact]
    public async Task PendingMetadata_LeavesTheProgressAnchorAlone_UntilItApplies()
    {
        var (client, connection, timer) = SchedulingClient();
        using var _c = client;

        connection.RaiseTextMessageReceived(MetadataState(
            $$"""
            {"timestamp":{{Now - 1_000_000}},
             "progress":{"track_progress":5000,"track_duration":180000,"playback_speed":1000},
             "title":"First"}
            """));

        connection.RaiseTextMessageReceived(MetadataState(
            $$"""
            {"timestamp":{{Now + 1_000}},
             "progress":{"track_progress":0,"track_duration":200000,"playback_speed":1000},
             "title":"Second"}
            """));

        // Progress is extrapolated from the timestamp/progress pair of the most recent APPLIED
        // metadata. A pending update that moved either would rewind the position readout of the
        // track still playing, a second before its successor starts.
        var playing = client.CurrentGroup!.Metadata!;
        Assert.Equal(Now - 1_000_000, playing.Timestamp);
        Assert.Equal(5000, playing.Progress!.TrackProgress);

        timer.CurrentTime = Now + 1_000;
        await WaitUntilAsync(
            () => client.CurrentGroup?.Metadata?.Title == "Second", "the scheduled metadata");

        var next = client.CurrentGroup!.Metadata!;
        Assert.Equal(Now + 1_000, next.Timestamp);
        Assert.Equal(0, next.Progress!.TrackProgress);
    }

    [Fact]
    public async Task Color_StampedInTheFuture_IsHeldWithoutColorChanged_ThenAppliedAtItsTime()
    {
        var (client, connection, timer) = SchedulingClient();
        using var _c = client;

        connection.RaiseTextMessageReceived(
            ColorState($$"""{"timestamp":{{Now - 1}},"primary":[1,1,1]}"""));

        var raised = new List<RgbColor?>();
        client.ColorChanged += (_, p) => raised.Add(p.Primary);

        connection.RaiseTextMessageReceived(
            ColorState($$"""{"timestamp":{{Now + 1_000}},"primary":[2,2,2]}"""));

        // ColorChanged marks the moment the palette takes effect, never the moment one was
        // merely scheduled — a renderer that blended on this event would change colour early.
        Assert.Empty(raised);
        Assert.Equal(new RgbColor(1, 1, 1), client.CurrentGroup!.Colors.Primary);

        timer.CurrentTime = Now + 1_000;
        await WaitUntilAsync(() => raised.Count == 1, "the scheduled color update");

        Assert.Equal(new RgbColor(2, 2, 2), raised[0]);
        Assert.Equal(new RgbColor(2, 2, 2), client.CurrentGroup.Colors.Primary);
        Assert.Equal(Now + 1_000, client.CurrentGroup.Colors.Timestamp);
    }

    [Fact]
    public async Task Color_StampedNow_AppliesImmediatelyAndDiscardsTheHeldUpdate()
    {
        var (client, connection, timer) = SchedulingClient();
        using var _c = client;

        connection.RaiseTextMessageReceived(
            ColorState($$"""{"timestamp":{{Now + 5_000}},"primary":[9,9,9]}"""));
        connection.RaiseTextMessageReceived(
            ColorState($$"""{"timestamp":{{Now}},"primary":[3,3,3]}"""));

        Assert.Equal(new RgbColor(3, 3, 3), client.CurrentGroup!.Colors.Primary);

        await DrainPastAsync(client, connection, timer, Now + 5_000);
        Assert.Equal(new RgbColor(3, 3, 3), client.CurrentGroup.Colors.Primary);
    }

    [Fact]
    public async Task ConnectionLoss_DiscardsAPendingStateUpdate()
    {
        var (client, connection, timer) = SchedulingClient();
        using var _c = client;

        connection.RaiseTextMessageReceived(
            MetadataState($$"""{"timestamp":{{Now - 1}},"title":"First"}"""));
        connection.RaiseTextMessageReceived(
            MetadataState($$"""{"timestamp":{{Now + 1_000}},"title":"Scheduled"}"""));

        // Same reason the media roles are flushed: the display time was computed against a
        // clock offset that resets on re-handshake, and the first server/state of the next
        // connection has to carry the role's full state anyway.
        connection.SimulateConnectionLoss();

        await DrainPastAsync(client, connection, timer, Now + 1_000);
        Assert.Equal("First", client.CurrentGroup!.Metadata!.Title);
    }

    // -- Artwork rules of spec #135 not already covered above -------------------------------

    [Fact]
    public async Task Artwork_NewerImageStampedEarlier_StillSupersedesTheHeldOne()
    {
        var (client, connection, timer) = SchedulingClient();
        using var _c = client;

        var received = new List<ArtworkReceivedEventArgs>();
        client.ArtworkReceived += (_, e) => received.Add(e);

        // The channel keeps one pending image and the newest message takes the slot, with no
        // comparison against what is held — so an image due sooner than the one it displaces
        // still wins, and the displaced one is gone rather than merely reordered.
        connection.RaiseBinaryMessageReceived(
            ArtworkBinary(Now + 5_000, new byte[] { 1 }, BinaryMessageTypes.Artwork0));
        connection.RaiseBinaryMessageReceived(
            ArtworkBinary(Now + 1_000, new byte[] { 2 }, BinaryMessageTypes.Artwork0));

        timer.CurrentTime = Now + 1_000;
        await WaitUntilAsync(() => received.Count == 1, "the replacement image");
        Assert.Equal(new byte[] { 2 }, received[0].ImageData);

        await DrainPastAsync(client, connection, timer, Now + 5_000);
        Assert.Single(received);
    }

    [Fact]
    public async Task StreamEnd_NamingArtwork_DiscardsThePendingImage()
    {
        var (client, connection, timer) = SchedulingClient();
        using var _c = client;

        var received = new List<ArtworkReceivedEventArgs>();
        client.ArtworkReceived += (_, e) => received.Add(e);

        connection.RaiseBinaryMessageReceived(
            ArtworkBinary(Now + 1_000, new byte[] { 1 }, BinaryMessageTypes.Artwork0));

        // "On stream/end, clearing buffers includes discarding pending images": an image sent
        // ahead for a track the server has just stopped streaming must not still appear.
        connection.RaiseTextMessageReceived(
            """{"type":"stream/end","payload":{"server_transmitted":1,"roles":["artwork"]}}""");

        await DrainPastAsync(client, connection, timer, Now + 1_000);
        Assert.Empty(received);
    }

    [Fact]
    public async Task StreamStart_ChangingAChannelsConfiguration_DiscardsOnlyThatChannelsPendingImage()
    {
        var (client, connection, timer) = SchedulingClient();
        using var _c = client;

        var received = new List<ArtworkReceivedEventArgs>();
        client.ArtworkReceived += (_, e) => received.Add(e);

        connection.RaiseTextMessageReceived(ArtworkStreamStart(
            """{"source":"album","format":"jpeg","width":512,"height":512}""",
            """{"source":"artist","format":"jpeg","width":256,"height":256}"""));

        connection.RaiseBinaryMessageReceived(
            ArtworkBinary(Now + 1_000, new byte[] { 1 }, BinaryMessageTypes.Artwork0));
        connection.RaiseBinaryMessageReceived(
            ArtworkBinary(Now + 1_000, new byte[] { 2 }, BinaryMessageTypes.Artwork1));

        // Channel 0 is reconfigured, so the image held for it was encoded for a size that no
        // longer applies and the server will re-send it if it still does. Channel 1 is
        // re-announced unchanged and keeps what it is holding.
        connection.RaiseTextMessageReceived(ArtworkStreamStart(
            """{"source":"album","format":"jpeg","width":128,"height":128}""",
            """{"source":"artist","format":"jpeg","width":256,"height":256}"""));

        await DrainPastAsync(client, connection, timer, Now + 1_000);

        var only = Assert.Single(received);
        Assert.Equal(1, only.Channel);
        Assert.Equal(new byte[] { 2 }, only.ImageData);
    }

    /// <summary>
    /// An artwork-only <c>stream/start</c> (no <c>player</c> key) declaring the given channels.
    /// Artwork-only so the handler runs to completion synchronously, with no pipeline start to
    /// await, which keeps the discard decided by the time the message returns.
    /// </summary>
    private static string ArtworkStreamStart(params string[] channels) =>
        """{"type":"stream/start","payload":{"artwork":{"channels":["""
        + string.Join(",", channels)
        + "]}}}";

    // -- Scheduled applies racing a disconnect ----------------------------------------------

    [Fact]
    public async Task ScheduledStateUpdate_AppliedAsADisconnectLands_DoesNotResurrectTheGroup()
    {
        var (client, connection, timer) = SchedulingClient();
        using var _c = client;

        // Three items due in one dispatch pass: the two state roles, applied first and in role
        // order, and an artwork clear, which the pass raises after them. The disconnect lands
        // inside the announcement of the metadata apply, so the color apply that follows runs on
        // the scheduler loop with the group already gone — the interleaving the disconnect's
        // flush cannot prevent, since the loop lifted both updates out of their slots before it.
        connection.RaiseTextMessageReceived(
            MetadataState($$"""{"timestamp":{{Now + 1_000}},"title":"Second"}"""));
        connection.RaiseTextMessageReceived(
            ColorState($$"""{"timestamp":{{Now + 1_000}},"primary":[2,2,2]}"""));
        connection.RaiseBinaryMessageReceived(ArtworkBinary(
            Now + 1_000, Array.Empty<byte>(), (byte)(BinaryMessageTypes.Artwork0 + MarkerChannel)));

        var passComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ArtworkCleared += (_, e) =>
        {
            if (e.Channel == MarkerChannel)
            {
                passComplete.TrySetResult();
            }
        };

        void OnGroupState(object? sender, GroupState state)
        {
            // Once: the disconnect must not be re-entered from an announcement the color apply
            // would make if it wrongly went ahead.
            client.GroupStateChanged -= OnGroupState;
            client.DisconnectAsync().GetAwaiter().GetResult();
        }

        client.GroupStateChanged += OnGroupState;

        timer.CurrentTime = Now + 1_000;
        await passComplete.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The color apply ran between the disconnect and the marker. It had to find no group and
        // leave none behind: creating one would republish group state for a connection that is
        // gone, and the next connection's first server/state carries every role in full anyway.
        Assert.Null(client.CurrentGroup);
    }
}
