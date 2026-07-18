using Microsoft.Extensions.Logging.Abstractions;
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
    private const string ServerId = "GFsV9tLaSQm9HcFWpKsgYQOr7wFTvNUtkmFwuVz3zoo";

    private sealed class FakeNoiseSession : INoiseSessionInfo
    {
        public string? ServerId { get; set; } = SendspinClientServiceManagementTests.ServerId;
        public NoisePsk? MatchedPsk { get; set; }
        public ReadOnlyMemory<byte>? HandshakeHash { get; set; } = new byte[32];
    }

    private static readonly byte[] SessionPsk = Enumerable.Repeat((byte)7, 32).ToArray();

    private static (SendspinClientService, FakeSendspinConnection, InMemoryPairingRecordStore) Create(
        bool managementActive = true)
    {
        var connection = new FakeSendspinConnection();
        var store = new InMemoryPairingRecordStore();
        store.Upsert(new PairingRecord(SessionPsk, PskCategory.LongTerm, ServerId));
        var session = new FakeNoiseSession
        {
            MatchedPsk = new NoisePsk(SessionPsk, PskCategory.LongTerm, ServerId),
        };
        var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection,
            noiseSession: session,
            pairingRecordStore: store);
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        string activities = managementActive ? """["playback","management"]""" : """["playback"]""";
        connection.RaiseTextMessageReceived(
            $$$"""{"type":"server/activate","payload":{"activities":{{{activities}}},"active_roles":[]}}""");
        return (client, connection, store);
    }

    private static ManagementResultPayload LastResult(FakeSendspinConnection connection) =>
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
    public void ServerUnpair_AtTrustNone_IsIgnored()
    {
        var connection = new FakeSendspinConnection();
        var store = new InMemoryPairingRecordStore();
        store.Upsert(new PairingRecord(SessionPsk, PskCategory.LongTerm, ServerId));
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection,
            noiseSession: new FakeNoiseSession
            {
                MatchedPsk = new NoisePsk(NoiseConstants.SentinelPsk.ToArray(), PskCategory.Sentinel),
            },
            pairingRecordStore: store);
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();

        connection.RaiseTextMessageReceived("""{"type":"server/unpair","payload":{}}""");

        Assert.Single(store.List());
        Assert.Null(connection.LastDisconnectReason);
    }
}
