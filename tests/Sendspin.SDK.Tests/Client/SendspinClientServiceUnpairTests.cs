using Sendspin.SDK.Client;
using Sendspin.SDK.Connection.Noise;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Coverage for <c>server/unpair</c>: the record removal, the "unpaired" goodbye, and the
/// trust gate that keeps an unauthenticated peer from triggering either. The message survived
/// the removal of the <c>management/*</c> family (spec #183), so its behavior is pinned here
/// rather than alongside vocabulary that no longer exists.
/// </summary>
public class SendspinClientServiceUnpairTests
{
    private const string ServerId = FakeNoiseSession.FakeServerId;

    private static readonly byte[] SessionPsk = Enumerable.Repeat((byte)7, 32).ToArray();

    /// <summary>
    /// A paired client whose session PSK is the one its store holds, so an unpair removes the
    /// record the session was authenticated with rather than an unrelated one.
    /// </summary>
    private static (SendspinClientService Client, FakeSendspinConnection Connection, InMemoryPairingRecordStore Store) Create()
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

    [Fact]
    public void ServerUnpair_RemovesRecord_AndSaysGoodbyeUnpaired()
    {
        var (client, connection, store) = Create();
        using var _c = client;

        connection.RaiseTextMessageReceived("""{"type":"server/unpair","payload":{}}""");

        Assert.Empty(store.List());
        Assert.Equal("unpaired", connection.LastDisconnectReason);
    }

    [Fact]
    public void ServerUnpair_NamingAnotherServersRecord_RemovesNothing()
    {
        // One record per server (spec #183): a record is bound to the server its pairing
        // created it for. If a server somehow authenticates with a record carrying a
        // different server_id, the binding — not the psk_id match — decides, and the record
        // stays. Without this a store rebuilt from another device's file could be emptied by
        // whichever server got there first.
        var store = new InMemoryPairingRecordStore();
        store.Upsert(new PairingRecord(SessionPsk, PskCategory.LongTerm, "some-other-server"));
        var (client, connection, session) = TestClient.Create(
            configure: options => options with { PairingRecordStore = store });
        using var _c = client;

        session.MatchedPsk = new NoisePsk(SessionPsk, PskCategory.LongTerm, ServerId);
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["playback"],"active_roles":[]}}""");

        connection.RaiseTextMessageReceived("""{"type":"server/unpair","payload":{}}""");

        Assert.Single(store.List());
        Assert.Equal("unpaired", connection.LastDisconnectReason);
    }

    [Fact]
    public void ServerUnpair_OnARecordMigratedWithoutAServerId_StillRemovesIt()
    {
        // Records written before #183 carry no server_id. The matched psk_id is then the only
        // binding there is, and refusing to act would leave the record un-removable forever.
        var store = new InMemoryPairingRecordStore();
        store.Upsert(new PairingRecord(SessionPsk, PskCategory.LongTerm, null));
        var (client, connection, session) = TestClient.Create(
            configure: options => options with { PairingRecordStore = store });
        using var _c = client;

        session.MatchedPsk = new NoisePsk(SessionPsk, PskCategory.LongTerm, ServerId);
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["playback"],"active_roles":[]}}""");

        connection.RaiseTextMessageReceived("""{"type":"server/unpair","payload":{}}""");

        Assert.Empty(store.List());
    }

    [Fact]
    public void ServerUnpair_AtTrustNone_IsIgnored()
    {
        var store = new InMemoryPairingRecordStore();
        store.Upsert(new PairingRecord(SessionPsk, PskCategory.LongTerm, ServerId));
        var (client, connection, _) = TestClient.Create(
            PskCategory.Sentinel,
            configure: options => options with { PairingRecordStore = store });
        using var _c = client;
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();

        connection.RaiseTextMessageReceived("""{"type":"server/unpair","payload":{}}""");

        Assert.Single(store.List());
        Assert.Null(connection.LastDisconnectReason);
    }

    [Fact]
    public void MessageArrivingAfterTheClientClosed_IsDropped()
    {
        // Neither receive path stops when the client decides to close, and every close is
        // fire-and-forget, so frames keep arriving during the teardown. They must not be
        // handled at all.
        var (client, connection, _) = Create();
        using var _c = client;

        connection.RaiseTextMessageReceived("""{"type":"server/unpair","payload":{}}""");
        Assert.Equal("unpaired", connection.LastDisconnectReason);

        bool activateSeen = false;
        client.ServerActivateReceived += (_, _) => activateSeen = true;

        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["playback"],"active_roles":[]}}""");

        Assert.False(activateSeen, "frames arriving after the close must not be handled");
    }
}
