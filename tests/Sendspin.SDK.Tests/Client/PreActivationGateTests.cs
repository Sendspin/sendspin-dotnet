using System.Buffers.Binary;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// messaging.md: "The server MUST NOT send other Sendspin messages until it sends the initial
/// <c>server/activate</c>", and connection.md holds a re-handshake to the same: "The server MUST
/// NOT start new application messages after sending Noise message 1 ... except for the handshake
/// and <c>server/activate</c>." The activation is where the client checks what the peer may do,
/// so a peer that withholds it must not have its other messages applied (#312).
/// </summary>
public class PreActivationGateTests
{
    private const string Hello = """
        {"type":"server/hello","payload":{"name":"srv"}}
        """;

    private const string StreamStart = """
        {"type":"stream/start","payload":{"player":{"codec":"pcm","sample_rate":48000,"channels":2,"bit_depth":16}}}
        """;

    private const string Mute = """
        {"type":"server/command","payload":{"player":{"command":"mute","mute":true}}}
        """;

    private const string GroupUpdate = """
        {"type":"group/update","payload":{"playback_state":"playing","group_id":"g1","group_name":"Injected"}}
        """;

    private static byte[] AudioFrame(long timestamp)
    {
        var frame = new byte[13 + 4];
        frame[0] = BinaryMessageTypes.PlayerAudio0;
        BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(1, 8), timestamp);
        return frame;
    }

    private static (SendspinClientService Client, FakeSendspinConnection Connection, FakeNoiseSession Session, FakeAudioPipeline Pipeline)
        PlayerClient()
    {
        var pipe = new FakeAudioPipeline();
        var (client, connection, session) = TestClient.Create(configure: options => options with
        {
            AudioPipeline = pipe,
            ClockSynchronizer = new ConvergedClockSynchronizer(),
        });
        return (client, connection, session, pipe);
    }

    [Fact]
    public void TextMessages_AfterHelloButBeforeTheFirstActivate_AreDropped()
    {
        var (client, connection, _, pipe) = PlayerClient();
        using var _c = client;

        int groupChanges = 0, playerChanges = 0;
        client.GroupStateChanged += (_, _) => groupChanges++;
        client.PlayerStateChanged += (_, _) => playerChanges++;

        connection.RaiseTextMessageReceived(Hello);
        connection.RaiseTextMessageReceived(StreamStart);
        connection.RaiseTextMessageReceived(Mute);
        connection.RaiseTextMessageReceived(GroupUpdate);
        connection.RaiseTextMessageReceived("""{"type":"stream/clear","payload":{"roles":["player"]}}""");
        connection.RaiseTextMessageReceived("""{"type":"stream/end","payload":{}}""");

        Assert.Empty(pipe.CallLog);
        Assert.Equal(0, pipe.ClearCount);
        Assert.Equal(0, groupChanges);
        Assert.Equal(0, playerChanges);

        // Dropped, not closed over: the connection is still there for the activate.
        Assert.Null(connection.LastDisconnectReason);
    }

    [Fact]
    public void BinaryMessages_FromAPeerThatSkipsHelloToo_AreDropped()
    {
        var (client, connection, _, pipe) = PlayerClient();
        using var _c = client;

        connection.RaiseBinaryMessageReceived(AudioFrame(5_000));

        Assert.Empty(pipe.Chunks);
    }

    [Fact]
    public void Activate_FromAPeerThatSkipsHello_IsDropped_AndOpensNothing()
    {
        var (client, connection, _, pipe) = PlayerClient();
        using var _c = client;

        // Admissible on its own terms, so only the missing server/hello stops it. Without the
        // hello there are no recorded roles for the per-role checks to read.
        connection.RaiseTextMessageReceived("""
            {"type":"server/activate","payload":{"activities":["playback"],"active_roles":["player@v1"]}}
            """);
        connection.RaiseBinaryMessageReceived(AudioFrame(5_000));

        Assert.Null(client.LastServerActivate);
        Assert.Empty(pipe.Chunks);
    }

    [Fact]
    public async Task Messages_AfterTheFirstActivate_AreApplied()
    {
        var (client, connection, _, pipe) = PlayerClient();
        using var _c = client;

        TestClient.CompleteHandshake(connection, "player@v1");

        int groupChanges = 0;
        client.GroupStateChanged += (_, _) => groupChanges++;
        connection.RaiseTextMessageReceived(GroupUpdate);
        Assert.Equal(1, groupChanges);

        connection.RaiseTextMessageReceived(StreamStart);
        await pipe.CallsCompleted(1).WaitAsync(TimeSpan.FromSeconds(5));
        connection.RaiseBinaryMessageReceived(AudioFrame(5_000));

        Assert.Single(pipe.StartCalls);
        Assert.Single(pipe.Chunks);
    }

    [Fact]
    public void Messages_AfterARehandshake_AreDroppedUntilTheNewActivate()
    {
        var (client, connection, session, _) = PlayerClient();
        using var _c = client;

        TestClient.CompleteHandshake(connection, "player@v1");

        int groupChanges = 0;
        client.GroupStateChanged += (_, _) => groupChanges++;

        // An in-band re-handshake as the client observes it: a fresh handshake hash.
        session.MatchedPsk = new NoisePsk(NoiseConstants.SentinelPsk.ToArray(), PskCategory.LongTerm);
        session.HandshakeHash = Enumerable.Repeat((byte)0xAB, 32).ToArray();

        connection.RaiseTextMessageReceived(GroupUpdate);
        Assert.Equal(0, groupChanges);

        connection.RaiseTextMessageReceived("""
            {"type":"server/activate","payload":{"activities":["playback"]}}
            """);
        connection.RaiseTextMessageReceived(GroupUpdate);
        Assert.Equal(1, groupChanges);
    }
}
