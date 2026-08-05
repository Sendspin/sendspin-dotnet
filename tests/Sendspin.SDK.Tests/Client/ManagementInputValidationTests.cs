using System.Text.Json;
using Sendspin.SDK.Connection.Noise;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Hostile-input coverage for the management family (#88 item 1): server_id is validated
/// on ingest at add-record, and the list-records / pairing-config data payloads are built
/// by the serializer, so a hostile server_id cannot inject structure into the
/// management/result the client sends.
/// </summary>
public class ManagementInputValidationTests
{
    /// <summary>A syntactically valid 43-character base64url key (32 bytes).</summary>
    private static string EncodeKey(byte fill) =>
        Base64UrlText.Encode(Enumerable.Repeat(fill, 32).ToArray());

    [Fact]
    public void AddRecord_ServerIdWithInjectedJsonStructure_IsRejectedBeforeTheStore()
    {
        var (client, connection, store) = SendspinClientServiceManagementTests.Create();
        using var _c = client;
        // Create() seeds the requester's own record, so "unchanged" is a snapshot
        // comparison rather than emptiness.
        var before = store.List().Select(r => r.PskId).ToList();
        string psk = EncodeKey(21);

        // The server_id value is srv","used":true,"x":" — under interpolation it would
        // inject structure into the list-records payload the client later emits.
        connection.RaiseTextMessageReceived(
            $$$"""{"type":"management/add-record","payload":{"psk":"{{{psk}}}","server_id":"srv\",\"used\":true,\"x\":\""}}""");

        Assert.Equal("invalid", SendspinClientServiceManagementTests.LastResult(connection).Result);
        Assert.Equal(before, store.List().Select(r => r.PskId).ToList());
    }

    [Fact]
    public void ListRecords_ServerIdRoundTripsExactly()
    {
        var (client, connection, store) = SendspinClientServiceManagementTests.Create();
        using var _c = client;
        string serverId = EncodeKey(5);
        Assert.Equal(43, serverId.Length);
        byte[] psk = Enumerable.Repeat((byte)6, 32).ToArray();
        store.Upsert(new PairingRecord(psk, PskCategory.LongTerm, serverId));

        connection.RaiseTextMessageReceived("""{"type":"management/list-records","payload":{}}""");

        var result = SendspinClientServiceManagementTests.LastResult(connection);
        Assert.Equal("ok", result.Result);
        using var doc = JsonDocument.Parse(result.Data!.Value.GetRawText());
        var entry = doc.RootElement.GetProperty("records").EnumerateArray()
            .Single(r => r.GetProperty("psk_id").GetString() == NoiseConstants.DerivePskId(psk));
        // Exact equality: a regression to interpolation could still pass a contains-check.
        Assert.Equal(serverId, entry.GetProperty("server_id").GetString());
    }

    [Fact]
    public void AddRecord_ServerIdOfWrongLength_IsRejected()
    {
        var (client, connection, store) = SendspinClientServiceManagementTests.Create();
        using var _c = client;
        var before = store.List().Select(r => r.PskId).ToList();

        foreach (int length in new[] { 42, 44 })
        {
            string psk = EncodeKey(22);
            string serverId = new string('A', length);

            connection.RaiseTextMessageReceived(
                $$$"""{"type":"management/add-record","payload":{"psk":"{{{psk}}}","server_id":"{{{serverId}}}"}}""");

            Assert.Equal("invalid", SendspinClientServiceManagementTests.LastResult(connection).Result);
            Assert.Equal(before, store.List().Select(r => r.PskId).ToList());
        }
    }

    [Fact]
    public void AddRecord_MalformedPsk_IsRejected_AndTheErrorNamesThePsk()
    {
        var (client, connection, store) = SendspinClientServiceManagementTests.Create();
        using var _c = client;
        var before = store.List().Select(r => r.PskId).ToList();
        string serverId = EncodeKey(23);

        // "AAAA" decodes cleanly but to 3 bytes, so it fails DecodePsk's own length check.
        connection.RaiseTextMessageReceived(
            $$$"""{"type":"management/add-record","payload":{"psk":"AAAA","server_id":"{{{serverId}}}"}}""");

        Assert.Equal("invalid", SendspinClientServiceManagementTests.LastResult(connection).Result);
        Assert.Equal(before, store.List().Select(r => r.PskId).ToList());

        // The add-record path decodes via DecodePsk; its error text names the PSK, not
        // "peer id". The client swallows the message (NullLogger), so assert it here.
        var ex = Assert.Throws<FormatException>(() => SendspinIdentity.DecodePsk("AAAA"));
        Assert.Contains("PSK", ex.Message);
        Assert.DoesNotContain("peer id", ex.Message);
    }

    [Fact]
    public void SetPairingConfig_UnpairedAccess_RoundTripsThroughGetPairingConfig()
    {
        var (client, connection, _) = SendspinClientServiceManagementTests.Create();
        using var _c = client;

        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"unpaired_access":{"enabled":true}}}""");
        Assert.Equal("ok", SendspinClientServiceManagementTests.LastResult(connection).Result);

        connection.RaiseTextMessageReceived("""{"type":"management/get-pairing-config","payload":{}}""");
        var result = SendspinClientServiceManagementTests.LastResult(connection);
        Assert.Equal("ok", result.Result);
        using var doc = JsonDocument.Parse(result.Data!.Value.GetRawText());
        Assert.True(doc.RootElement.GetProperty("unpaired_access").GetProperty("enabled").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("pairing_psk").GetProperty("enabled").GetBoolean());
    }
}
