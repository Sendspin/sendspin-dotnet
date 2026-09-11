using System.Reflection;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Spec #183 removed the <c>management/*</c> namespace outright: pairing configuration and the
/// pairing window are local, manufacturer-defined concerns, and no server may drive them. These
/// tests pin the absence — a management message is unknown vocabulary that draws no reply and
/// changes nothing, 'management' is not an activity, and it carries no arbitration priority.
/// </summary>
public class ManagementRemovedTests
{
    private const string ServerId = FakeNoiseSession.FakeServerId;

    private static readonly byte[] SessionPsk = Enumerable.Repeat((byte)7, 32).ToArray();

    private static (SendspinClientService Client, FakeSendspinConnection Connection, InMemoryPairingRecordStore Store)
        CreatePairedClient()
    {
        var store = new InMemoryPairingRecordStore();
        store.Upsert(new PairingRecord(SessionPsk, PskCategory.LongTerm, ServerId));
        var (client, connection, session) = TestClient.Create(
            configure: options => options with { PairingRecordStore = store });

        session.MatchedPsk = new NoisePsk(SessionPsk, PskCategory.LongTerm, ServerId);
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["playback"],"active_roles":[]}}""");
        return (client, connection, store);
    }

    [Theory]
    [InlineData("""{"type":"management/list-records","payload":{}}""")]
    [InlineData("""{"type":"management/add-record","payload":{"psk":"AAAA"}}""")]
    [InlineData("""{"type":"management/remove-record","payload":{"psk_id":"anything"}}""")]
    [InlineData("""{"type":"management/get-pairing-config","payload":{}}""")]
    [InlineData("""{"type":"management/set-pairing-config","payload":{"pairing_psk":{"enabled":false}}}""")]
    [InlineData("""{"type":"management/open-pairing-window","payload":{}}""")]
    public void ManagementRequest_OnAFullyTrustedSession_IsNotAccepted(string json)
    {
        // Even at trust 'user' with every activity a server can now hold, the request must be
        // ignored: unknown vocabulary draws no reply, nothing is written to the record store,
        // and the connection is left alone rather than closed.
        var (client, connection, store) = CreatePairedClient();
        using var _c = client;

        int sentBefore = connection.SentMessages.Count;

        connection.RaiseTextMessageReceived(json);

        Assert.Equal(sentBefore, connection.SentMessages.Count);
        Assert.Single(store.List());
        Assert.Null(connection.LastDisconnectReason);
    }

    [Fact]
    public void ManagementActivity_IsNotAdmissible_OnALongTermSession()
    {
        // 'management' is no longer an activity at all, so an activate declaring it is
        // inadmissible however the session is keyed.
        var (client, connection, _) = CreatePairedClient();
        using var _c = client;

        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["playback","management"],"active_roles":[]}}""");

        Assert.Equal("unauthorized", connection.LastDisconnectReason);
    }

    [Fact]
    public void ManagementActivity_IsNotAdmissible_OnASentinelSession()
    {
        var (client, connection, _) = TestClient.Create(PskCategory.Sentinel);
        using var _c = client;

        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["management"],"active_roles":[]}}""");

        Assert.Equal("unauthorized", connection.LastDisconnectReason);
        Assert.Null(client.LastServerActivate);
    }

    [Fact]
    public void PairingActivity_IsNotAdmissible_OnALongTermSession()
    {
        // Spec #183: a client authenticated with a long-term PSK is already paired, so a
        // pairing activity on that session has nothing to establish and is refused.
        var (client, connection, _) = CreatePairedClient();
        using var _c = client;

        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"pairing":{"method":"pairing_psk"}}}""");

        Assert.Equal("unauthorized", connection.LastDisconnectReason);
    }

    [Fact]
    public void PlaybackActivity_IsAdmissible_OnALongTermSession()
    {
        // Positive control for the two refusals above: the normal case must still be admitted,
        // or a client that refused every activate would pass them both.
        var (client, connection, _) = CreatePairedClient();
        using var _c = client;

        Assert.Null(connection.LastDisconnectReason);
        Assert.NotNull(client.LastServerActivate);
    }

    [Fact]
    public void Activities_AreOnlyPlaybackAndPairing()
    {
        var names = typeof(Activities)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false })
            .Select(f => (string)f.GetValue(null)!)
            .ToList();

        Assert.Equal(["pairing", "playback"], names.Order().ToList());
    }

    [Fact]
    public void MessageTypes_CarryNoManagementVocabulary()
    {
        var types = typeof(MessageTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false })
            .Select(f => (string)f.GetValue(null)!)
            .ToList();

        Assert.DoesNotContain(types, t => t.StartsWith("management/", StringComparison.Ordinal));

        // server/unpair survived the removal (#183) and must stay in the vocabulary.
        Assert.Contains(MessageTypes.ServerUnpair, types);
    }

    [Theory]
    [InlineData("""{"type":"management/result","payload":{"result":"ok"}}""")]
    [InlineData("""{"type":"management/list-records","payload":{}}""")]
    public void ManagementMessages_DeserializeToNull(string json)
    {
        Assert.Null(MessageSerializer.Deserialize(json));
    }
}
