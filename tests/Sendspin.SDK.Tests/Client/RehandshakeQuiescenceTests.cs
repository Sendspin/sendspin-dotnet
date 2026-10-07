using Sendspin.SDK.Client;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// After an in-band re-handshake the client starts no application message until the new
/// session's <c>server/activate</c> has been admitted (connection.md, Re-handshake: "The server
/// MUST NOT start new application messages after sending Noise message 1, nor the client after
/// receiving it, except for the handshake and server/activate. This restriction ends when the
/// server sends, or the client receives, the new server/activate.") (#317).
/// </summary>
/// <remarks>
/// The fake session stands in for the framing: a re-handshake is its handshake hash changing,
/// which on a real connection happens as the client's reply is written.
/// </remarks>
public class RehandshakeQuiescenceTests
{
    private const string Activate =
        """{"type":"server/activate","payload":{"activities":["playback"],"active_roles":["player@v1","controller@v1"]}}""";

    private static (SendspinClientService Client, FakeSendspinConnection Connection, FakeNoiseSession Session)
        ConnectedClient()
    {
        var (client, connection, session) = TestClient.Create(
            PskCategory.LongTerm,
            configure: options => options with { ClockSynchronizer = new ConvergedClockSynchronizer() });

        // Answered, so the loop's opening burst completes and leaves no probe in flight: a
        // burst requested while one is running is skipped, which would pass these tests for
        // the wrong reason.
        connection.RespondToTimeSync = true;

        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        connection.RaiseTextMessageReceived(Activate);
        connection.RaiseTextMessageReceived(
            """{"type":"server/state","payload":{"controller":{"supported_commands":["volume"]}}}""");
        WaitForAsync(() => Probes(connection) >= 8 && States(connection) >= 1).GetAwaiter().GetResult();

        return (client, connection, session);
    }

    private static void Rehandshake(FakeNoiseSession session) =>
        session.HandshakeHash = Enumerable.Repeat((byte)0x5A, 32).ToArray();

    private static int Probes(FakeSendspinConnection connection) =>
        connection.SnapshotSentMessages().OfType<ClientTimeMessage>().Count();

    private static int States(FakeSendspinConnection connection) =>
        connection.SnapshotSentMessages().OfType<ClientStateMessage>().Count();

    [Theory]
    [InlineData("command")]
    [InlineData("player-state")]
    [InlineData("timing")]
    [InlineData("artwork")]
    public async Task ApplicationMessages_AreWithheldUntilTheNewActivate(string kind)
    {
        var (client, connection, session) = ConnectedClient();
        using var _c = client;

        Rehandshake(session);
        int before = connection.SnapshotSentMessages().Count;

        switch (kind)
        {
            case "command": await client.SetVolumeAsync(42); break;
            case "player-state": await client.SendPlayerStateAsync(volume: 42, muted: false); break;
            case "timing": await client.UpdateTimingAsync(300, 200); break;
            case "artwork": await client.SetArtworkChannelAsync(0, source: "album"); break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Assert.Equal(before, connection.SnapshotSentMessages().Count);
    }

    [Fact]
    public async Task TheSameSends_ReachTheWire_OnceTheNewActivateIsAdmitted()
    {
        // Positive control: the tests above are satisfied by a client that never sends again.
        var (client, connection, session) = ConnectedClient();
        using var _c = client;

        Rehandshake(session);
        connection.RaiseTextMessageReceived(Activate);
        int before = connection.SnapshotSentMessages().OfType<ClientCommandMessage>().Count();

        await client.SetVolumeAsync(42);

        Assert.Equal(before + 1, connection.SnapshotSentMessages().OfType<ClientCommandMessage>().Count());
    }

    [Fact]
    public async Task TimeProbes_AreWithheldUntilTheNewActivate()
    {
        var (client, connection, session) = ConnectedClient();
        using var _c = client;

        // The loop is asleep until its next burst, so a burst asked for here does run.
        int before = Probes(connection);
        await client.SendTimeSyncBurstAsync(CancellationToken.None);
        Assert.Equal(before + 8, Probes(connection));

        Rehandshake(session);
        await client.SendTimeSyncBurstAsync(CancellationToken.None);
        Assert.Equal(before + 8, Probes(connection));

        // The activate restarts the loop, whose first burst goes out at once.
        connection.RaiseTextMessageReceived(Activate);
        await WaitForAsync(() => Probes(connection) >= before + 16);
    }

    [Fact]
    public async Task AMessageOtherThanTheActivate_DoesNotEndTheWindow()
    {
        var (client, connection, session) = ConnectedClient();
        using var _c = client;

        // A probe, because it needs no role: the roles are set aside as soon as the re-key is
        // noticed, which already silences everything that depends on one.
        Rehandshake(session);
        connection.RaiseTextMessageReceived(
            """{"type":"group/update","payload":{"playback_state":"playing","group_id":"g1","group_name":"g"}}""");
        int before = Probes(connection);

        await client.SendTimeSyncBurstAsync(CancellationToken.None);

        Assert.Equal(before, Probes(connection));
    }

    [Fact]
    public async Task AClientStateWithheldInTheWindow_IsSentWhenTheNewActivateIsAdmitted()
    {
        // State is last-write-wins, so the one message dropped is recovered by a full report.
        // The activate here changes no role, so nothing else would send one.
        var (client, connection, session) = ConnectedClient();
        using var _c = client;

        Rehandshake(session);
        int before = States(connection);
        await client.SendPlayerStateAsync(volume: 42, muted: false);
        Assert.Equal(before, States(connection));

        connection.RaiseTextMessageReceived(Activate);
        await WaitForAsync(() => States(connection) > before);

        var state = connection.SnapshotSentMessages().OfType<ClientStateMessage>().Last();
        Assert.Equal(42, state.Payload.Player!.Volume);
    }

    [Fact]
    public async Task AnActivateWithNothingWithheld_SendsNoExtraClientState()
    {
        var (client, connection, session) = ConnectedClient();
        using var _c = client;

        Rehandshake(session);
        int before = States(connection);
        connection.RaiseTextMessageReceived(Activate);

        await Task.Delay(200);
        Assert.Equal(before, States(connection));
    }

    [Fact]
    public async Task SourceAudio_IsWithheldUntilTheNewActivate()
    {
        var capture = new FakeCaptureDevice();
        var (client, connection, session) = TestClient.Create(
            PskCategory.LongTerm,
            configure: options => options with
            {
                Capabilities = new ClientCapabilities { Roles = { "source@v1" } },
                CaptureDevice = capture,
                ClockSynchronizer = new ConvergedClockSynchronizer(),
            });
        using var _c = client;

        const string sourceActivate =
            """{"type":"server/activate","payload":{"activities":["playback"],"active_roles":["source@v1"]}}""";
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        connection.RaiseTextMessageReceived(sourceActivate);
        connection.RaiseTextMessageReceived("""{"type":"server/command","payload":{"source":{"command":"start"}}}""");

        capture.Emit([1, 2, 3, 4], captureTimeUs: 1000);
        await WaitForAsync(() => connection.SnapshotSentBinary().Count == 1);

        Rehandshake(session);
        capture.Emit([5, 6, 7, 8], captureTimeUs: 2000);
        await Task.Delay(200);
        Assert.Single(connection.SnapshotSentBinary());
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met within the timeout");
            await Task.Delay(10);
        }
    }
}
