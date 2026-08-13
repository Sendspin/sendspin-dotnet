using Sendspin.SDK.Client;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Connection.Noise.Pairing;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Between a pairing <c>server/activate</c> and the first non-pairing one, the client sends
/// nothing but pairing messages (#118).
/// </summary>
/// <remarks>
/// <para>
/// aiosendspin's <c>_receive_pairing</c> treats any non-pairing frame as a protocol error and
/// closes the socket with no application-level message, so anything that slips through here
/// does not degrade the pairing attempt — it ends it. #117 closed the paths that spoke without
/// app action; these cover the ones that need a caller.
/// </para>
/// <para>
/// Two kinds of message are blocked and they fare differently. <em>State</em> —
/// <c>SendPlayerStateAsync</c>, <c>UpdateTimingAsync</c>, availability — is last-write-wins, so
/// the full <c>client/state</c> sent on leaving the window restores it exactly. <em>Requests</em>
/// — <c>SetVolumeAsync</c> and <c>SetMuteAsync</c> (which ask the server to change volume, via
/// <c>client/command</c>), and the <c>stream/request-format</c> calls — are genuinely lost, and
/// the app reissues them if it still wants them. That distinction is why dropping is sound
/// without a queue.
/// </para>
/// </remarks>
public class PairingQuiescenceTests
{
    private const string PairingActivate =
        """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"pairing":{"method":"dynamic_pin","pin_length":6}}}""";

    private const string PlaybackActivate =
        """{"type":"server/activate","payload":{"activities":["playback"],"active_roles":["player@v1"]}}""";

    private static (SendspinClientService Client, FakeSendspinConnection Connection) PairableClient()
    {
        var (client, connection, _) = TestClient.Create(
            unpairedAccess: true,
            configure: options => options with
            {
                ClockSynchronizer = new ConvergedClock(),
                Capabilities = new ClientCapabilities { PinPairingMethods = { "dynamic_pin" } },
                PairingRecordStore = new InMemoryPairingRecordStore(),
                PinLockoutStore = new InMemoryPinLockoutStore(),
                PresentPinAsync = (_, _) => ValueTask.CompletedTask,
            });

        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");

        // Established and playing before any pairing activate — the realistic shape, and the
        // one the re-report is for. A connection whose FIRST activate is the pairing one takes a
        // different path: FinishHandshake withholds the initial client/state wholesale and the
        // first non-pairing activate releases it (#117), so there is nothing to re-report.
        connection.RaiseTextMessageReceived(PlaybackActivate);
        WaitForAsync(
            () => connection.SnapshotSentMessages().OfType<ClientStateMessage>().Any(),
            TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();

        return (client, connection);
    }

    private static int NonPairingSends(FakeSendspinConnection connection) =>
        connection.SnapshotSentMessages().Count(m => m is not (
            ClientPairInitMessage or ClientPairAuthMessage or ClientPairConfirmMessage
            or ClientPairFinalizeMessage or ClientPairPendingMessage or PairAbortMessage
            or ClientHelloMessage));

    [Theory]
    [InlineData("volume")]
    [InlineData("mute")]
    [InlineData("player-state")]
    [InlineData("timing")]
    [InlineData("artwork-format")]
    [InlineData("visualizer-format")]
    public async Task AppDrivenSends_AreWithheldDuringAPairingActivation(string kind)
    {
        var (client, connection) = PairableClient();
        using var _c = client;

        connection.RaiseTextMessageReceived(PairingActivate);
        int before = NonPairingSends(connection);

        switch (kind)
        {
            case "volume": await client.SetVolumeAsync(42); break;
            case "mute": await client.SetMuteAsync(true); break;
            case "player-state": await client.SendPlayerStateAsync(volume: 42, muted: false); break;
            case "timing": await client.UpdateTimingAsync(300, 200); break;
            case "artwork-format": await client.RequestArtworkFormatAsync(0, source: "album"); break;
            case "visualizer-format": await client.RequestVisualizerFormatAsync(rateMax: 15); break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Assert.Equal(before, NonPairingSends(connection));
    }

    [Fact]
    public async Task TheSameSends_DoReachTheWireOutsideAPairingActivation()
    {
        // Positive control. Every assertion above is satisfied by a client that sends nothing
        // at all, which is exactly the bug a too-broad gate would introduce.
        var (client, connection) = PairableClient();
        using var _c = client;

        int before = NonPairingSends(connection);

        await client.SetVolumeAsync(42);

        Assert.True(NonPairingSends(connection) > before);
    }

    [Fact]
    public void PairingMessagesThemselves_StillTravel()
    {
        // The gate is by message type, so the exchange it protects must pass through it.
        var (client, connection) = PairableClient();
        using var _c = client;

        connection.RaiseTextMessageReceived(PairingActivate);

        Assert.NotEmpty(connection.SnapshotSentMessages().OfType<ClientPairInitMessage>());
    }

    [Fact]
    public async Task LeavingTheWindow_ReReportsTheFullStateTheWindowDropped()
    {
        // The whole reason dropping is acceptable for state: a player state reported during the
        // window is not lost, it is restored by the full report on the way out. Without it the
        // server would keep believing the pre-window values indefinitely.
        //
        // SendPlayerStateAsync, not SetVolumeAsync: the latter sends a client/command asking the
        // *server* to change volume, which is a request rather than state and is genuinely lost
        // — see the class remarks.
        var (client, connection) = PairableClient();
        using var _c = client;

        connection.RaiseTextMessageReceived(PairingActivate);
        int statesBefore = connection.SnapshotSentMessages().OfType<ClientStateMessage>().Count();

        await client.SendPlayerStateAsync(volume: 42, muted: false);
        Assert.Equal(
            statesBefore,
            connection.SnapshotSentMessages().OfType<ClientStateMessage>().Count());

        connection.RaiseTextMessageReceived(PlaybackActivate);
        await WaitForAsync(
            () => connection.SnapshotSentMessages().OfType<ClientStateMessage>().Count() > statesBefore,
            TimeSpan.FromSeconds(5));

        var state = connection.SnapshotSentMessages().OfType<ClientStateMessage>().Last();
        Assert.Equal(42, state.Payload.Player!.Volume);

        // Full state, not a delta: the client cannot know which values the server last saw,
        // because it does not track what the gate discarded.
        Assert.NotNull(state.Payload.Available);
    }

    [Fact]
    public async Task SourceAudioFrames_AreWithheldDuringAPairingActivation()
    {
        // A pairing activate that omits active_roles leaves the prior roles standing, so a
        // streaming source is never stopped. The binary path needs its own gate: no binary
        // frame is ever a pairing message.
        var (client, connection) = PairableClient();
        using var _c = client;

        connection.RaiseTextMessageReceived(PairingActivate);
        int binaryBefore = connection.SnapshotSentBinary().Count;
        int textBefore = NonPairingSends(connection);

        await client.SetSourceSignalAsync(present: true);

        Assert.Equal(binaryBefore, connection.SnapshotSentBinary().Count);

        // The signal itself is a client/state, so the text gate has to hold as well.
        Assert.Equal(textBefore, NonPairingSends(connection));
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(10);
        }

        Assert.Fail("condition not met within the timeout");
    }

    private sealed class ConvergedClock : IClockSynchronizer
    {
        public double StaticDelayMs { get; set; }

        public bool IsConverged => true;

        public bool HasMinimalSync => true;

        public void ProcessMeasurement(long t1, long t2, long t3, long t4)
        {
        }

        public void Reset()
        {
        }

        public long ClientToServerTime(long clientTime) => clientTime;

        public long ServerToClientTime(long serverTime) => serverTime;

        public ClockSyncStatus GetStatus() => new() { IsConverged = true };
    }
}
