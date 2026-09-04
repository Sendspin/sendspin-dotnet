using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using Sendspin.SDK.Client;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Tests.Audio;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Spec PR #204: "the server MUST NOT send a role's binary data until it has received that
/// role's <c>client/state</c> object." The client enforces the same gate on the receive side,
/// because a frame that arrives ahead of the object was scheduled against timings
/// (<c>static_delay_ms</c>, <c>required_lead_time_ms</c>, <c>min_buffer_ms</c>) and a channel
/// configuration this connection never sent — so treating it as authoritative is worse than
/// dropping it.
/// </summary>
/// <remarks>
/// The gate is per connection and keyed on what this connection actually reported, which is why
/// every test here drives a real handshake: before <c>server/hello</c> there is no statement
/// about active roles at all, and the gate deliberately stays open in that window so the many
/// harnesses that raise binary frames without a handshake are unaffected.
/// </remarks>
public class RoleStateBinaryGatingTests
{
    private static byte[] Frame(byte type, long timestamp, params byte[] data)
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

    /// <summary>
    /// Client that advertises all four roles so which state objects go out is decided purely by
    /// <c>active_roles</c> — the thing under test — rather than by capabilities.
    /// </summary>
    private static (SendspinClientService Client, FakeSendspinConnection Connection, FakeAudioPipeline Pipeline)
        GatingClient(ILogger<SendspinClientService>? logger = null)
    {
        var pipe = new FakeAudioPipeline();
        var (client, connection, _) = TestClient.Create(
            connected: false,
            configure: options => options with
            {
                AudioPipeline = pipe,
                ClockSynchronizer = new ConvergedClockSynchronizer(),
                Capabilities = new ClientCapabilities
                {
                    Roles = new List<string> { "player@v1", "artwork@v1", "visualizer@v1" },
                    VisualizerRoleSupport = new VisualizerRoleSupport
                    {
                        BufferCapacity = 65_536,
                        RateMax = 30,
                        Types = new List<string> { VisualizerTypes.Loudness },
                    },
                },
            },
            logger: logger);
        return (client, connection, pipe);
    }

    private static List<ClientStateMessage> ClientStates(FakeSendspinConnection connection) =>
        connection.SnapshotSentMessages().OfType<ClientStateMessage>().ToList();

    private static void Activate(FakeSendspinConnection connection, params string[] roles)
    {
        string list = string.Join(",", roles.Select(r => $"\"{r}\""));
        connection.RaiseTextMessageReceived(
            $$$"""
            {"type":"server/activate","payload":{"activities":["playback"],"active_roles":[{{{list}}}]}}
            """);
    }

    [Fact]
    public void PlayerAudio_BeforeThePlayerStateObjectIsSent_IsDropped()
    {
        var logger = new CapturingLogger<SendspinClientService>();
        var (client, connection, pipe) = GatingClient(logger);
        using var _c = client;

        // Handshake grants artwork only, so this connection's client/state carries no player
        // object — the server has never been told this client's playback timings.
        TestClient.CompleteHandshake(connection, "artwork@v1");

        connection.RaiseBinaryMessageReceived(Frame(BinaryMessageTypes.PlayerAudio0, 5_000, 1, 2, 3));

        Assert.Empty(pipe.Chunks);
        Assert.Contains(
            logger.MessagesAt(LogLevel.Warning),
            m => m.Contains("player", StringComparison.Ordinal)
                && m.Contains("client/state", StringComparison.Ordinal));
    }

    [Fact]
    public void UngatedRoleBinary_IsWarnedAboutOncePerRole_NotOncePerFrame()
    {
        var logger = new CapturingLogger<SendspinClientService>();
        var (client, connection, _) = GatingClient(logger);
        using var _c = client;

        TestClient.CompleteHandshake(connection, "artwork@v1");

        for (int i = 0; i < 5; i++)
        {
            connection.RaiseBinaryMessageReceived(Frame(BinaryMessageTypes.PlayerAudio0, 5_000 + i, 1));
        }

        // A server streaming ahead of the gate does so at chunk rate; one line per role keeps
        // the deviation visible without drowning the log.
        Assert.Single(
            logger.MessagesAt(LogLevel.Warning),
            m => m.Contains("Dropping player binary data", StringComparison.Ordinal));
    }

    [Fact]
    public void Artwork_BeforeItsStateObjectIsSent_IsDropped()
    {
        var (client, connection, _) = GatingClient();
        using var _c = client;

        TestClient.CompleteHandshake(connection, "player@v1");

        ArtworkReceivedEventArgs? received = null;
        client.ArtworkReceived += (_, e) => received = e;

        // Stamped in the past, so nothing but the gate can be holding it back.
        connection.RaiseBinaryMessageReceived(Frame(BinaryMessageTypes.Artwork0, 1, 1, 2, 3));

        Assert.Null(received);
    }

    [Fact]
    public void Visualizer_BeforeItsStateObjectIsSent_IsDropped()
    {
        var (client, connection, _) = GatingClient();
        using var _c = client;

        TestClient.CompleteHandshake(connection, "player@v1");

        var frames = new List<VisualizerFrame>();
        client.VisualizationReceived += (_, e) => frames.Add(e);

        connection.RaiseBinaryMessageReceived(LoudnessFrame(1, 40_000));

        Assert.Empty(frames);
    }

    [Fact]
    public void PlayerAudio_OnceThePlayerStateObjectHasGoneOut_ReachesThePipeline()
    {
        var (client, connection, pipe) = GatingClient();
        using var _c = client;

        TestClient.CompleteHandshake(connection, "player@v1");

        // Positive control: the same frame the test above drops flows once the object it gates on
        // has been reported.
        Assert.Contains(ClientStates(connection), s => s.Payload.Player is not null);

        connection.RaiseBinaryMessageReceived(Frame(BinaryMessageTypes.PlayerAudio0, 5_000, 1, 2, 3));

        var chunk = Assert.Single(pipe.Chunks);
        Assert.Equal(new byte[] { 1, 2, 3 }, chunk.EncodedData);
    }

    [Fact]
    public void RoleAddedByALaterActivate_SendsItsStateObjectBeforeItsBinaryIsAccepted()
    {
        var (client, connection, _) = GatingClient();
        using var _c = client;

        TestClient.CompleteHandshake(connection, "player@v1");
        int beforeReactivation = ClientStates(connection).Count;

        ArtworkReceivedEventArgs? received = null;
        client.ArtworkReceived += (_, e) => received = e;

        // The server adds artwork to an already-running connection. #204 makes the full
        // client/state — now carrying the artwork object — the precondition for the role's
        // binary data, so the client must reannounce rather than wait for the next state change.
        Activate(connection, "player@v1", "artwork@v1");

        var states = ClientStates(connection);
        Assert.True(states.Count > beforeReactivation, "the added role must trigger a client/state");

        var latest = states[^1].Payload;
        Assert.NotNull(latest.Artwork);
        Assert.NotNull(latest.Player);

        connection.RaiseBinaryMessageReceived(Frame(BinaryMessageTypes.Artwork0, 1, 4, 5, 6));

        Assert.NotNull(received);
        Assert.Equal(new byte[] { 4, 5, 6 }, received.ImageData);
    }

    [Fact]
    public void UnchangedActiveRoles_DoNotReannounceOnEveryActivate()
    {
        var (client, connection, _) = GatingClient();
        using var _c = client;

        TestClient.CompleteHandshake(connection, "player@v1");
        int afterInitial = ClientStates(connection).Count;

        // A repeat activate carrying the same roles changes nothing the server has yet to
        // receive; resending would put a redundant full state on the wire per activate.
        Activate(connection, "player@v1");

        Assert.Equal(afterInitial, ClientStates(connection).Count);
    }

    [Fact]
    public void DeactivatedRole_StopsBeingReported_AndItsBinaryIsGatedAgain()
    {
        var (client, connection, _) = GatingClient();
        using var _c = client;

        TestClient.CompleteHandshake(connection, "player@v1", "artwork@v1");
        Assert.NotNull(ClientStates(connection)[^1].Payload.Artwork);

        Activate(connection, "player@v1");

        var latest = ClientStates(connection)[^1].Payload;
        Assert.Null(latest.Artwork);
        Assert.NotNull(latest.Player);
    }
}
