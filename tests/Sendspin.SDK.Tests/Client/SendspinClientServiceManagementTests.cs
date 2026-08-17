using Microsoft.Extensions.Logging;
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
    // logger is for tests whose subject is a rejection's diagnostic rather than its
    // management/result, which is a bare "invalid" for every reason alike (#110).
    internal static (SendspinClientService, FakeSendspinConnection, FakeNoiseSession, InMemoryPairingRecordStore) Create(
        bool managementActive = true,
        ILogger<SendspinClientService>? logger = null)
    {
        var store = new InMemoryPairingRecordStore();
        store.Upsert(new PairingRecord(SessionPsk, PskCategory.LongTerm, ServerId));
        var (client, connection, session) = TestClient.Create(
            configure: options => options with { PairingRecordStore = store },
            logger: logger);

        // The management tests remove their own record by psk_id, so the session must be
        // keyed with the same PSK the store holds.
        session.MatchedPsk = new NoisePsk(SessionPsk, PskCategory.LongTerm, ServerId);
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        string activities = managementActive ? """["playback","management"]""" : """["playback"]""";
        connection.RaiseTextMessageReceived(
            $$$"""{"type":"server/activate","payload":{"activities":{{{activities}}},"active_roles":[]}}""");
        return (client, connection, session, store);
    }

    internal static ManagementResultPayload LastResult(FakeSendspinConnection connection) =>
        connection.SentMessages.OfType<ManagementResultMessage>().Last().Payload;

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void Management_WithoutManagementActivity_IsPermissionDenied()
    {
        var (client, connection, _, _) = Create(managementActive: false);
        using var _c = client;

        connection.RaiseTextMessageReceived("""{"type":"management/list-records","payload":{}}""");

        Assert.Equal("permission_denied", LastResult(connection).Result);
    }

    [Fact]
    public void ListRecords_ReturnsStoredRecords()
    {
        var (client, connection, _, store) = Create();
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
        var (client, connection, _, store) = Create();
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
    public void AddRecord_CarryingTheSentinelPsk_IsRejected_AndDoesNotShadowSentinelResolution()
    {
        // management.md:37 — a psk_id already known as the Sentinel PSK is already_exists.
        // Without this, a LongTerm record holding the published Sentinel constant shadows
        // Sentinel resolution (RecordPskResolver searches records before falling back), so
        // every anonymous peer that knows the constant authenticates at trust 'user'.
        var (client, connection, _, store) = Create();
        using var _c = client;

        string sentinel = ToBase64Url(NoiseConstants.SentinelPsk.ToArray());
        connection.RaiseTextMessageReceived(
            """{"type":"management/add-record","payload":{"psk":"PSK"}}""".Replace("PSK", sentinel));

        Assert.Equal("already_exists", LastResult(connection).Result);

        // Nothing was written: the Sentinel psk_id must not appear in the store at all,
        // which is the property that keeps resolution unambiguous.
        Assert.DoesNotContain(store.List(), r => r.PskId == NoiseConstants.SentinelPskId);
    }

    [Fact]
    public void AddRecord_CarryingAnUnrelatedPsk_StillSucceeds()
    {
        // Positive control: a guard that rejected every add would pass the test above.
        var (client, connection, _, store) = Create();
        using var _c = client;

        var fresh = Enumerable.Repeat((byte)0x5A, 32).ToArray();
        connection.RaiseTextMessageReceived(
            """{"type":"management/add-record","payload":{"psk":"PSK"}}""".Replace("PSK", ToBase64Url(fresh)));

        Assert.Equal("ok", LastResult(connection).Result);
        Assert.Contains(store.List(), r => r.PskId == NoiseConstants.DerivePskId(fresh));
    }

    [Fact]
    public void AddRecord_StoreFull_AnswersStorageExhausted()
    {
        // #128: Upsert can now report a full store, and add-record must relay that rather
        // than claiming ok for a record it never held.
        var store = new FullPairingRecordStore();
        var (client, connection, _) = TestClient.Create(
            configure: options => options with { PairingRecordStore = store });
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["playback","management"],"active_roles":[]}}""");
        using var _c = client;

        var fresh = Enumerable.Repeat((byte)0x5A, 32).ToArray();
        connection.RaiseTextMessageReceived(
            """{"type":"management/add-record","payload":{"psk":"PSK"}}""".Replace("PSK", ToBase64Url(fresh)));

        Assert.Equal("storage_exhausted", LastResult(connection).Result);
        Assert.Empty(store.List());
    }

    [Fact]
    public void RemoveRecord_NotFound_And_SelfRemovalClosesSession()
    {
        var (client, connection, _, store) = Create();
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
    public void RemoveRecord_TargetingThePairingRecord_RaisesPairingConfigChanged_AndStalesTheToken()
    {
        // list-records hands every management server the Pairing record's psk_id, and
        // remove-record removes any record by psk_id — so a server can kill the token
        // behind a QR code an app is currently displaying. The app must hear about it.
        var (client, connection, _, store) = Create();
        using var _c = client;
        string before = client.EnsurePairingPsk();
        string pairingPskId = store.List().Single(r => r.Category == PskCategory.Pairing).PskId;
        var events = new List<PairingConfigChangedEventArgs>();
        client.PairingConfigChanged += (_, e) => events.Add(e);

        connection.RaiseTextMessageReceived(
            $$$"""{"type":"management/remove-record","payload":{"psk_id":"{{{pairingPskId}}}"}}""");

        Assert.Equal("ok", LastResult(connection).Result);
        var change = Assert.Single(events);
        Assert.True(change.PairingPskReplaced);
        // The documented contract: the old token stopped being current.
        Assert.NotEqual(before, client.EnsurePairingPsk());
    }

    [Fact]
    public void RemoveRecord_TargetingALongTermRecord_DoesNotRaisePairingConfigChanged()
    {
        // Guards the positive test against an implementation that fires on every removal.
        var (client, connection, _, store) = Create();
        using var _c = client;
        byte[] otherPsk = Enumerable.Repeat((byte)8, 32).ToArray();
        store.Upsert(new PairingRecord(otherPsk, PskCategory.LongTerm, ServerId));
        var events = new List<PairingConfigChangedEventArgs>();
        client.PairingConfigChanged += (_, e) => events.Add(e);

        connection.RaiseTextMessageReceived(
            $$$"""{"type":"management/remove-record","payload":{"psk_id":"{{{NoiseConstants.DerivePskId(otherPsk)}}}"}}""");

        Assert.Equal("ok", LastResult(connection).Result);
        Assert.Empty(events);
    }

    [Fact]
    public void PairingConfig_GetAndPatch()
    {
        var (client, connection, _, store) = Create();
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
    public void SetPairingConfig_PairingPsk_StoreFull_AnswersStorageExhausted_AndDoesNotRaiseEvent()
    {
        // #128: same discipline as AddRecord_StoreFull_AnswersStorageExhausted, applied to the
        // pairing_psk write in set-pairing-config. The request also bundles unpaired_access, to
        // prove the handler's parse-before-apply discipline still holds: a refusal from the
        // store write must leave the bundled field unapplied too, not just skip the event.
        var store = new FullPairingRecordStore();
        var (client, connection, _) = TestClient.Create(
            configure: options => options with { PairingRecordStore = store });
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["playback","management"],"active_roles":[]}}""");
        using var _c = client;

        var events = new List<PairingConfigChangedEventArgs>();
        client.PairingConfigChanged += (_, e) => events.Add(e);

        string psk = Convert.ToBase64String(Enumerable.Repeat((byte)3, 32).ToArray())
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"unpaired_access":{"enabled":true},"pairing_psk":{"psk":"PSK"}}}"""
                .Replace("PSK", psk));

        Assert.Equal("storage_exhausted", LastResult(connection).Result);
        Assert.Empty(events);

        // Nothing else from the bundled request applied either — a failed store write must
        // not leave a partially-applied config.
        connection.RaiseTextMessageReceived("""{"type":"management/get-pairing-config","payload":{}}""");
        Assert.False(LastResult(connection).Data!.Value
            .GetProperty("unpaired_access").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void SetPairingConfig_PairingPsk_StoreFull_LeavesOldPairingRecordIntact()
    {
        // #128 review: the store write must Upsert the new Pairing PSK before removing the
        // old one. Removing first and only then discovering the store refuses the new record
        // would destroy the client's only Pairing PSK while still answering storage_exhausted
        // — worse than the answer alone, since EnsurePairingPsk's already-issued token is now
        // dead with no PairingConfigChanged to say so. The store here starts "full" with one
        // Pairing record already seeded, so rotating to a genuinely different PSK is refused;
        // that record must still be there afterward.
        var oldPsk = Enumerable.Repeat((byte)0x11, 32).ToArray();
        var store = new FullPairingRecordStore(new PairingRecord(oldPsk, PskCategory.Pairing));
        var (client, connection, _) = TestClient.Create(
            configure: options => options with { PairingRecordStore = store });
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["playback","management"],"active_roles":[]}}""");
        using var _c = client;

        var events = new List<PairingConfigChangedEventArgs>();
        client.PairingConfigChanged += (_, e) => events.Add(e);

        string newPsk = Convert.ToBase64String(Enumerable.Repeat((byte)0x22, 32).ToArray())
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"pairing_psk":{"psk":"PSK"}}}"""
                .Replace("PSK", newPsk));

        Assert.Equal("storage_exhausted", LastResult(connection).Result);
        Assert.Empty(events);

        var record = Assert.Single(store.List());
        Assert.Equal(PskCategory.Pairing, record.Category);
        Assert.Equal(NoiseConstants.DerivePskId(oldPsk), record.PskId);
    }

    [Fact]
    public void RecordMode_MustReferenceASharedPskRecord_AndProtectsItFromRemoval()
    {
        // management.md:111 — psk_id MUST reference a shared-PSK record, enforced at
        // configuration time, and the referenced record cannot be removed while referenced.
        var (client, connection, _, store) = Create();
        using var _c = client;

        var shared = Enumerable.Repeat((byte)3, 32).ToArray();
        store.Upsert(new PairingRecord(shared, PskCategory.LongTerm)); // no ServerId => shared
        string sharedId = NoiseConstants.DerivePskId(shared);

        // A stored-pubkey record is not a legal target: SessionPsk is bound to a server_id.
        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"record_mode":{"psk_id":"PSKID"}}}"""
                .Replace("PSKID", NoiseConstants.DerivePskId(SessionPsk)));
        Assert.Equal("invalid", LastResult(connection).Result);

        // Nor is one that does not exist.
        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"record_mode":{"psk_id":"nope"}}}""");
        Assert.Equal("invalid", LastResult(connection).Result);

        // The shared record is accepted and reported back.
        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"record_mode":{"psk_id":"PSKID"}}}"""
                .Replace("PSKID", sharedId));
        Assert.Equal("ok", LastResult(connection).Result);
        connection.RaiseTextMessageReceived("""{"type":"management/get-pairing-config","payload":{}}""");
        Assert.Equal(sharedId, LastResult(connection).Data!.Value
            .GetProperty("record_mode").GetProperty("psk_id").GetString());

        // And it is now protected from removal.
        connection.RaiseTextMessageReceived(
            """{"type":"management/remove-record","payload":{"psk_id":"PSKID"}}"""
                .Replace("PSKID", sharedId));
        Assert.Equal("invalid", LastResult(connection).Result);
        Assert.Contains(store.List(), r => r.PskId == sharedId);
    }

    [Fact]
    public void RecordMode_SuccessfulSet_RaisesPairingConfigChangedOnce_AndNotOnANoOpSet()
    {
        // Same hazard PairingConfigOwnershipTests.SetPairingPskEnabled_RaisesEventExactlyOnce_...
        // guards for pairing_psk: a section's changed-flag must be folded into the final
        // event condition by hand, and it is easy to add a new section and forget that
        // third touch point, silently suppressing PairingConfigChanged with nothing failing.
        var (client, connection, _, store) = Create();
        using var _c = client;

        var shared = Enumerable.Repeat((byte)4, 32).ToArray();
        store.Upsert(new PairingRecord(shared, PskCategory.LongTerm));
        string sharedId = NoiseConstants.DerivePskId(shared);
        var events = new List<PairingConfigChangedEventArgs>();
        client.PairingConfigChanged += (_, e) => events.Add(e);

        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"record_mode":{"psk_id":"PSKID"}}}"""
                .Replace("PSKID", sharedId));
        Assert.Equal("ok", LastResult(connection).Result);
        Assert.Single(events);

        // Re-asserting the value it already holds is a no-op: applied, but changes nothing,
        // so it must not raise again.
        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"record_mode":{"psk_id":"PSKID"}}}"""
                .Replace("PSKID", sharedId));
        Assert.Equal("ok", LastResult(connection).Result);
        Assert.Single(events);
    }

    [Fact]
    public void RemoveRecord_SharedPskRecord_NotTheRecordModeTarget_IsStillRemovable()
    {
        // The removal constraint has an obvious hole to prove closed: a shared-PSK record
        // that record_mode does NOT reference must still be removable. Without this
        // positive control, a remove-record that rejected everything would also pass
        // RecordMode_MustReferenceASharedPskRecord_AndProtectsItFromRemoval above.
        var (client, connection, _, store) = Create();
        using var _c = client;

        var referenced = Enumerable.Repeat((byte)5, 32).ToArray();
        var other = Enumerable.Repeat((byte)6, 32).ToArray();
        store.Upsert(new PairingRecord(referenced, PskCategory.LongTerm));
        store.Upsert(new PairingRecord(other, PskCategory.LongTerm));
        string otherId = NoiseConstants.DerivePskId(other);

        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"record_mode":{"psk_id":"PSKID"}}}"""
                .Replace("PSKID", NoiseConstants.DerivePskId(referenced)));
        Assert.Equal("ok", LastResult(connection).Result);

        connection.RaiseTextMessageReceived(
            """{"type":"management/remove-record","payload":{"psk_id":"PSKID"}}"""
                .Replace("PSKID", otherId));
        Assert.Equal("ok", LastResult(connection).Result);
        Assert.DoesNotContain(store.List(), r => r.PskId == otherId);
    }

    [Fact]
    public void ServerUnpair_RemovesRecord_AndSaysGoodbyeUnpaired()
    {
        var (client, connection, _, store) = Create();
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
            configure: options => options with { PairingRecordStore = store });
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
    public void ManagementPermittedOnOneConnection_IsNotPermittedOnTheNextBeforeItsOwnActivate()
    {
        // A permission decision must be read from the session it was made for. Nothing
        // cleared LastServerActivate on reconnect, so the window between a new handshake
        // completing and that session's first server/activate honoured the PREVIOUS
        // session's grant — even when the new session is keyed differently.
        var (client, connection, _, _) = Create();
        using var _c = client;

        // Positive control first: management is genuinely permitted on this connection.
        connection.RaiseTextMessageReceived(
            """{"type":"management/list-records","payload":{}}""");
        Assert.Equal("ok", LastResult(connection).Result);

        // Reconnect. No server/activate arrives on the new connection.
        connection.SimulateConnectionLoss();
        connection.SimulateReconnected();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");

        connection.RaiseTextMessageReceived(
            """{"type":"management/list-records","payload":{}}""");

        Assert.Equal("permission_denied", LastResult(connection).Result);
    }

    [Fact]
    public void ManagementGrantedBeforeAnInBandRekey_IsNotHonouredAfterIt()
    {
        // pairing.md:63 lets a server re-handshake an established LongTerm session DOWN to
        // the Pairing PSK. The grant belonged to the session that was replaced; honouring it
        // afterwards gives management to a session that is admissible only for ['pairing'].
        var (client, connection, session, _) = Create();
        using var _c = client;

        // Positive control: management is genuinely permitted on the pre-rekey session.
        connection.RaiseTextMessageReceived("""{"type":"management/list-records","payload":{}}""");
        Assert.Equal("ok", LastResult(connection).Result);

        // An in-band re-key installs a fresh handshake hash. Nothing else about the
        // connection changes — this is the same WebSocket.
        session.HandshakeHash = Enumerable.Repeat((byte)0xAB, 32).ToArray();

        connection.RaiseTextMessageReceived("""{"type":"management/list-records","payload":{}}""");

        Assert.Equal("permission_denied", LastResult(connection).Result);

        // Positive control (closes the loop the rest of this test only proves half of): a
        // fresh server/activate on the new session re-grants management. Without this, an
        // implementation that nulled LastServerActivate permanently — and never re-honoured
        // a later activate at all — would also pass the denial assertion above.
        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["playback","management"],"active_roles":[]}}""");
        connection.RaiseTextMessageReceived("""{"type":"management/list-records","payload":{}}""");

        Assert.Equal("ok", LastResult(connection).Result);
    }

    [Fact]
    public void MessageArrivingAfterTheClientClosed_IsDropped_WithNoReply()
    {
        // Defence in depth for the same window: neither receive path stops when the client
        // decides to close, and every close is fire-and-forget, so frames keep arriving
        // during the teardown. They must not be handled at all — not even answered.
        var (client, connection, _, store) = Create();
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
            configure: options => options with { PairingRecordStore = store });
        using var _c = client;
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();

        connection.RaiseTextMessageReceived("""{"type":"server/unpair","payload":{}}""");

        Assert.Single(store.List());
        Assert.Null(connection.LastDisconnectReason);
    }
}
