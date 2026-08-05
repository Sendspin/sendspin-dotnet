using Sendspin.SDK.Client;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Coverage for the management message family and server/unpair: the permission gate,
/// record CRUD via management/result, pairing-config get/set patch semantics, and the
/// unpair record-removal + goodbye behavior.
/// </summary>
public class SendspinClientServiceManagementTests
{
    private const string ServerId = FakeNoiseSession.FakeServerId;

    private static readonly byte[] SessionPsk = Enumerable.Repeat((byte)7, 32).ToArray();

    // Internal so ManagementInputValidationTests can reuse the same management-activated client.
    internal static (SendspinClientService, FakeSendspinConnection, InMemoryPairingRecordStore) Create(
        bool managementActive = true)
    {
        var store = new InMemoryPairingRecordStore();
        store.Upsert(new PairingRecord(SessionPsk, PskCategory.LongTerm, ServerId));
        var (client, connection, session) = TestClient.Create(
            configure: options => options.PairingRecordStore = store);

        // The management tests remove their own record by psk_id, so the session must be
        // keyed with the same PSK the store holds.
        session.MatchedPsk = new NoisePsk(SessionPsk, PskCategory.LongTerm, ServerId);
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        string activities = managementActive ? """["playback","management"]""" : """["playback"]""";
        connection.RaiseTextMessageReceived(
            $$$"""{"type":"server/activate","payload":{"activities":{{{activities}}},"active_roles":[]}}""");
        return (client, connection, store);
    }

    internal static ManagementResultPayload LastResult(FakeSendspinConnection connection) =>
        connection.SentMessages.OfType<ManagementResultMessage>().Last().Payload;

    [Fact]
    public void Management_WithoutManagementActivity_IsPermissionDenied()
    {
        var (client, connection, _) = Create(managementActive: false);
        using var _c = client;

        connection.RaiseTextMessageReceived("""{"type":"management/list-records","payload":{}}""");

        Assert.Equal("permission_denied", LastResult(connection).Result);
    }

    [Fact]
    public void ListRecords_ReturnsStoredRecords()
    {
        var (client, connection, store) = Create();
        using var _c = client;

        connection.RaiseTextMessageReceived("""{"type":"management/list-records","payload":{}}""");

        var result = LastResult(connection);
        Assert.Equal("ok", result.Result);
        var records = result.Data!.Value.GetProperty("records");
        var entry = Assert.Single(records.EnumerateArray());
        Assert.Equal(store.List().Single().PskId, entry.GetProperty("psk_id").GetString());
        Assert.Equal(ServerId, entry.GetProperty("server_id").GetString());
    }

    [Fact]
    public void AddRecord_PersistsAndRejectsDuplicates()
    {
        var (client, connection, store) = Create();
        using var _c = client;
        string psk = Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray())
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        connection.RaiseTextMessageReceived(
            $$$"""{"type":"management/add-record","payload":{"psk":"{{{psk}}}"}}""");
        Assert.Equal("ok", LastResult(connection).Result);
        Assert.Equal(2, store.List().Count);

        connection.RaiseTextMessageReceived(
            $$$"""{"type":"management/add-record","payload":{"psk":"{{{psk}}}"}}""");
        Assert.Equal("already_exists", LastResult(connection).Result);

        connection.RaiseTextMessageReceived(
            """{"type":"management/add-record","payload":{"psk":"tooshort"}}""");
        Assert.Equal("invalid", LastResult(connection).Result);
    }

    [Fact]
    public void RemoveRecord_NotFound_And_SelfRemovalClosesSession()
    {
        var (client, connection, store) = Create();
        using var _c = client;

        connection.RaiseTextMessageReceived(
            """{"type":"management/remove-record","payload":{"psk_id":"nope"}}""");
        Assert.Equal("not_found", LastResult(connection).Result);

        string ownPskId = NoiseConstants.DerivePskId(SessionPsk);
        connection.RaiseTextMessageReceived(
            $$$"""{"type":"management/remove-record","payload":{"psk_id":"{{{ownPskId}}}"}}""");

        Assert.Equal("ok", LastResult(connection).Result);
        Assert.Empty(store.List());
        // Removing the requester's own record closes with 'unauthorized' after the reply.
        Assert.Equal("unauthorized", connection.LastDisconnectReason);
    }

    [Fact]
    public void PairingConfig_GetAndPatch()
    {
        var (client, connection, store) = Create();
        using var _c = client;

        connection.RaiseTextMessageReceived("""{"type":"management/get-pairing-config","payload":{}}""");
        var data = LastResult(connection).Data!.Value;
        Assert.True(data.GetProperty("pairing_psk").GetProperty("enabled").GetBoolean());
        Assert.False(data.GetProperty("unpaired_access").GetProperty("enabled").GetBoolean());

        // Patch: enable unpaired access and stage a new Pairing PSK.
        string psk = Convert.ToBase64String(Enumerable.Repeat((byte)3, 32).ToArray())
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"unpaired_access":{"enabled":true},"pairing_psk":{"psk":"PSK"}}}"""
                .Replace("PSK", psk));
        Assert.Equal("ok", LastResult(connection).Result);
        Assert.Contains(store.List(), r => r.Category == PskCategory.Pairing);

        connection.RaiseTextMessageReceived("""{"type":"management/get-pairing-config","payload":{}}""");
        Assert.True(LastResult(connection).Data!.Value
            .GetProperty("unpaired_access").GetProperty("enabled").GetBoolean());

        // Setting fields on an unimplemented PIN method is invalid.
        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"static_pin":{"enabled":true}}}""");
        Assert.Equal("invalid", LastResult(connection).Result);
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
    public void RefusedManagementActivate_DoesNotGrantManagement_OnALaterConnection()
    {
        // A Sentinel-keyed peer asks for the management activity. The admissibility table
        // refuses it and the client closes — but the refused activate must leave nothing
        // behind, or management/add-record on any later connection writes an
        // attacker-chosen long-term PSK and hands the peer trust 'user' with no pairing.
        //
        // The second request deliberately lands on a *later* connection: on the same one
        // the receive-path state guard would drop it, which would not pin this.
        var store = new InMemoryPairingRecordStore();
        var (client, connection, _) = TestClient.Create(
            PskCategory.Sentinel,
            configure: options => options.PairingRecordStore = store);
        using var _c = client;

        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["management"],"active_roles":[]}}""");
        Assert.Equal("unauthorized", connection.LastDisconnectReason);
        Assert.Null(client.LastServerActivate);

        // A fresh connection, with no server/activate at all.
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");

        string psk = Convert.ToBase64String(Enumerable.Repeat((byte)11, 32).ToArray())
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        connection.RaiseTextMessageReceived(
            $$$"""{"type":"management/add-record","payload":{"psk":"{{{psk}}}"}}""");

        Assert.Equal("permission_denied", LastResult(connection).Result);
        Assert.Empty(store.List());
    }

    [Fact]
    public void MessageArrivingAfterTheClientClosed_IsDropped_WithNoReply()
    {
        // Defence in depth for the same window: neither receive path stops when the client
        // decides to close, and every close is fire-and-forget, so frames keep arriving
        // during the teardown. They must not be handled at all — not even answered.
        var (client, connection, store) = Create();
        using var _c = client;

        connection.RaiseTextMessageReceived("""{"type":"server/unpair","payload":{}}""");
        Assert.Equal("unpaired", connection.LastDisconnectReason);
        int repliesBefore = connection.SentMessages.OfType<ManagementResultMessage>().Count();

        string psk = Convert.ToBase64String(Enumerable.Repeat((byte)12, 32).ToArray())
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        connection.RaiseTextMessageReceived(
            $$$"""{"type":"management/add-record","payload":{"psk":"{{{psk}}}"}}""");

        Assert.Equal(repliesBefore, connection.SentMessages.OfType<ManagementResultMessage>().Count());
        Assert.Empty(store.List());
    }

    [Fact]
    public void ServerUnpair_AtTrustNone_IsIgnored()
    {
        var store = new InMemoryPairingRecordStore();
        store.Upsert(new PairingRecord(SessionPsk, PskCategory.LongTerm, ServerId));
        var (client, connection, _) = TestClient.Create(
            PskCategory.Sentinel,
            configure: options => options.PairingRecordStore = store);
        using var _c = client;
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();

        connection.RaiseTextMessageReceived("""{"type":"server/unpair","payload":{}}""");

        Assert.Single(store.List());
        Assert.Null(connection.LastDisconnectReason);
    }
}
