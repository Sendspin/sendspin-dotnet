using System.Linq;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Coverage for the Pairing PSK flow: server/activate with the pairing activity
/// triggers client/pair-finalize with a fresh long-term PSK, and server/pair-finalize
/// persists the record bound to the server id.
/// </summary>
public class SendspinClientServicePairingTests
{
    private const string ServerId = FakeNoiseSession.FakeServerId;

    private static (SendspinClientService, FakeSendspinConnection, InMemoryPairingRecordStore) Create()
    {
        var store = new InMemoryPairingRecordStore();
        var (client, connection, _) = TestClient.Create(
            PskCategory.Pairing,
            configure: options => options with { PairingRecordStore = store });
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        return (client, connection, store);
    }

    [Fact]
    public void PairingActivate_SendsPairFinalize_WithFreshPsk()
    {
        var (client, connection, _) = Create();
        using var _c = client;

        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"pairing":{"method":"pairing_psk"}}}""");

        var finalize = Assert.Single(connection.SentMessages.OfType<ClientPairFinalizeMessage>());
        Assert.NotNull(finalize.Payload.LongTermPsk);
        Assert.Equal(43, finalize.Payload.LongTermPsk!.Length);
        Assert.Null(finalize.Payload.WrappedPsk);
    }

    [Fact]
    public void ServerPairFinalize_PersistsRecord_BoundToServer_AndRaisesEvent()
    {
        var (client, connection, store) = Create();
        using var _c = client;
        string? pairedWith = null;
        // Through the interface: #112 — an app coded against ISendspinClient could already
        // start pairing and be told its PSK went stale, so it must be able to observe
        // pairing finish too.
        ISendspinClient asInterface = client;
        asInterface.PairingCompleted += (_, id) => pairedWith = id;

        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"pairing":{"method":"pairing_psk"}}}""");
        connection.RaiseTextMessageReceived("""{"type":"server/pair-finalize","payload":{}}""");

        var record = Assert.Single(store.List());
        Assert.Equal(PskCategory.LongTerm, record.Category);
        Assert.Equal(ServerId, record.ServerId);
        Assert.Equal(ServerId, pairedWith);

        // The delivered PSK and the stored record agree.
        var finalize = connection.SentMessages.OfType<ClientPairFinalizeMessage>().Single();
        Assert.Equal(NoiseConstants.DerivePskId(record.Psk.Span),
            NoiseConstants.DerivePskId(record.Psk.Span));
        Assert.Equal(43, finalize.Payload.LongTermPsk!.Length);
    }

    [Fact]
    public void KnownButUnconfiguredPairMethod_SendsAbort()
    {
        // dynamic_pin is a method the SDK implements but this client has not enabled, so
        // CanOffer refuses it. The unknown-method case is covered separately, at
        // UnsupportedPairMethod_AbortsWithoutClosingTheConnection.
        var (client, connection, store) = Create();
        using var _c = client;

        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"pairing":{"method":"dynamic_pin"}}}""");

        var abort = Assert.Single(connection.SentMessages.OfType<PairAbortMessage>());
        Assert.Equal("method_not_supported", abort.Payload.Reason);
        Assert.Empty(store.List());
        Assert.Null(connection.LastDisconnectReason);
    }

    [Fact]
    public void PairAbort_ClearsPendingAttempt()
    {
        var (client, connection, store) = Create();
        using var _c = client;

        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"pairing":{"method":"pairing_psk"}}}""");
        connection.RaiseTextMessageReceived("""{"type":"pair/abort","payload":{"reason":"user_cancelled"}}""");
        connection.RaiseTextMessageReceived("""{"type":"server/pair-finalize","payload":{}}""");

        // The aborted attempt's PSK must not be persisted by a late finalize.
        Assert.Empty(store.List());
    }

    [Fact]
    public void PendingPairingPsk_AbandonedAcrossAReconnect_DoesNotFinalizeOnTheNewSession()
    {
        // Finding 2 (trust-boundary residuals): _pendingPairingPsk is a per-session
        // artifact — the long-term PSK a still-open attempt generated — but nothing
        // cleared it on reconnect or re-key. HandleServerPairFinalize's entire gate is
        // "_pendingPairingPsk is not null": no activity, trust, or session check. So an
        // abandoned attempt followed by a bare server/pair-finalize on ANY later session —
        // even one an anonymous Sentinel-keyed peer opened — would persist a permanent
        // LongTerm record.
        var (client, connection, store) = Create();
        using var _c = client;

        // Positive control first: within the attempt's own session, a finalize genuinely
        // persists the record. Without this, an implementation that broke pairing
        // entirely (e.g. never sets _pendingPairingPsk) would also pass the assertion
        // below for the wrong reason.
        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"pairing":{"method":"pairing_psk"}}}""");
        connection.RaiseTextMessageReceived("""{"type":"server/pair-finalize","payload":{}}""");
        Assert.Single(store.List());

        // A second, abandoned attempt: the server activates pairing again — a fresh
        // client/pair-finalize goes out, setting _pendingPairingPsk again — but the
        // connection drops before server/pair-finalize arrives.
        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"pairing":{"method":"pairing_psk"}}}""");
        connection.SimulateConnectionLoss();
        connection.SimulateReconnected();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");

        // Completes the reconnect handshake (any admissible activate does, per
        // FinishHandshake) without leaving it hanging on the 30s handshake timeout, and
        // without itself touching _pendingPairingPsk: an unknown pair method is admissible
        // (activities:["pairing"] alone is enough) but CanOffer refuses it before the
        // pairing_psk case — the only case that sets the field — is ever reached, so this
        // activate is not itself an activate the new session ever granted a fresh attempt
        // from. See UnsupportedPairMethod_AbortsWithoutClosingTheConnection for the same
        // "unknown method aborts, connection stays open" shape.
        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"pairing":{"method":"telepathy"}}}""");

        // The new session's peer sends a bare finalize, with no pairing_psk activate of
        // its own.
        connection.RaiseTextMessageReceived("""{"type":"server/pair-finalize","payload":{}}""");

        // Still just the one record from the positive control above: the abandoned
        // attempt's PSK must not be persisted on a session it was never generated for.
        Assert.Single(store.List());
    }

    [Fact]
    public void PendingPairingPsk_AbandonedAcrossAnInBandRekey_DoesNotFinalizeOnTheNewSession()
    {
        // The in-band twin of the reconnect test above, isolating DetectSessionRekey's
        // clear the same way that test isolates SendHandshakeAsync's: no reconnect here —
        // just a fresh handshake hash on the same connection, with no server/hello or any
        // other bounding message in between. pairing.md:63's down-re-handshake to the
        // Pairing PSK (the scenario ManagementGrantedBeforeAnInBandRekey_IsNotHonouredAfterIt
        // in SendspinClientServiceManagementTests.cs names explicitly) is exactly this
        // shape. Unlike the reconnect test, there is no SendHandshakeAsync call at all here,
        // so there is no 30s handshake-timeout wait to avoid; and HandleServerPairFinalize's
        // store write is fully synchronous (no SafeFireAndForget in its path), so the
        // assertions below need no WaitUntilAsync/Task.Delay — a plain synchronous read of
        // store.List() right after RaiseTextMessageReceived is not a vacuous pass here the
        // way it was for the source pipeline's fire-and-forget command chain.
        var store = new InMemoryPairingRecordStore();
        var (client, connection, session) = TestClient.Create(
            PskCategory.Pairing,
            configure: options => options with { PairingRecordStore = store });
        using var _c = client;
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");

        // Positive control first: within the attempt's own session, a finalize genuinely
        // persists the record. Without this, an implementation that broke pairing
        // entirely would also pass the assertion below for the wrong reason.
        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"pairing":{"method":"pairing_psk"}}}""");
        connection.RaiseTextMessageReceived("""{"type":"server/pair-finalize","payload":{}}""");
        Assert.Single(store.List());

        // A second, abandoned attempt: the server activates pairing again — a fresh
        // client/pair-finalize goes out, setting _pendingPairingPsk again.
        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"pairing":{"method":"pairing_psk"}}}""");

        // An in-band re-key installs a fresh handshake hash before the server's own
        // finalize ever arrives. Nothing else about the connection changes — this is the
        // same WebSocket, no reconnect.
        session.HandshakeHash = Enumerable.Repeat((byte)0xAB, 32).ToArray();

        // The retired session's peer sends a bare finalize, with no pairing_psk activate
        // of its own on the new (re-keyed) session.
        connection.RaiseTextMessageReceived("""{"type":"server/pair-finalize","payload":{}}""");

        // Still just the one record from the positive control above: the abandoned
        // attempt's PSK must not be persisted on a session it was never generated for.
        Assert.Single(store.List());
    }

    [Fact]
    public void EncryptedHello_AdvertisesPairingPskMethod()
    {
        var (client, connection, _) = Create();
        using var _c = client;

        var hello = connection.SentMessages.OfType<ClientHelloMessage>().Single();
        var method = Assert.Single(hello.Payload.SupportedPairMethods!);
        Assert.Equal("pairing_psk", method.Method);
    }

    /// <summary>
    /// Builds a client whose Noise session is keyed by the given PSK category, with an
    /// optional record store. The spec admits Sentinel + {pairing} because the PIN
    /// methods authenticate via CPace there — but pairing_psk specifically requires a
    /// session already keyed by the Pairing PSK.
    /// </summary>
    private static (SendspinClientService, FakeSendspinConnection) CreateWith(
        PskCategory category,
        bool withStore)
    {
        var (client, connection, _) = TestClient.Create(
            category,
            configure: options => withStore
                ? options with { PairingRecordStore = new InMemoryPairingRecordStore() }
                : options);
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        return (client, connection);
    }

    private static void SendPairingActivate(FakeSendspinConnection connection, string method) =>
        connection.RaiseTextMessageReceived(
            $$$$"""
            {"type":"server/activate","payload":{"activities":["pairing"],"pairing":{"method":"{{{{method}}}}"}}}
            """);

    [Fact]
    public void PairingPsk_OnSentinelKeyedSession_AbortsAndNeverSendsFinalize()
    {
        // #74: a server on the published Sentinel PSK must not be able to obtain a
        // permanent credential. The abort must not close the connection (#76).
        var (client, connection) = CreateWith(PskCategory.Sentinel, withStore: true);
        using var _c = client;

        SendPairingActivate(connection, "pairing_psk");

        Assert.DoesNotContain(connection.SentMessages, m => m is ClientPairFinalizeMessage);
        var abort = Assert.Single(connection.SentMessages.OfType<PairAbortMessage>());
        Assert.Equal("method_not_supported", abort.Payload.Reason);
        Assert.Equal(ConnectionState.Connected, connection.State);
        Assert.Null(connection.LastDisconnectReason);
    }

    [Fact]
    public void PairingPsk_WithNoRecordStore_AbortsAndDoesNotClaimSuccess()
    {
        // #87-1: aborting up front means the server never persists a half we cannot use.
        var (client, connection) = CreateWith(PskCategory.Pairing, withStore: false);
        using var _c = client;

        bool paired = false;
        client.PairingCompleted += (_, _) => paired = true;

        SendPairingActivate(connection, "pairing_psk");

        Assert.DoesNotContain(connection.SentMessages, m => m is ClientPairFinalizeMessage);
        var abort = Assert.Single(connection.SentMessages.OfType<PairAbortMessage>());
        Assert.Equal("method_not_supported", abort.Payload.Reason);
        Assert.Equal(ConnectionState.Connected, connection.State);

        // The event only ever fires from an inbound server/pair-finalize, so asserting on it
        // without one asserts nothing. A server that finalizes anyway — it never received a
        // client/pair-finalize, but the connection is deliberately still open — must not make
        // this client claim a pairing it cannot authenticate against.
        connection.RaiseTextMessageReceived("""{"type":"server/pair-finalize","payload":{}}""");

        Assert.False(paired, "PairingCompleted must not fire when the record cannot be persisted");
    }

    [Fact]
    public void UnsupportedPairMethod_AbortsWithoutClosingTheConnection()
    {
        // #76: spec #123 — reply method_not_supported and leave the connection open so
        // the server can re-activate with a method we do offer.
        var (client, connection) = CreateWith(PskCategory.Pairing, withStore: true);
        using var _c = client;

        SendPairingActivate(connection, "telepathy");

        var abort = Assert.Single(connection.SentMessages.OfType<PairAbortMessage>());
        Assert.Equal("method_not_supported", abort.Payload.Reason);
        Assert.Equal(ConnectionState.Connected, connection.State);
        Assert.Null(connection.LastDisconnectReason);
    }

    [Fact]
    public void PairingPsk_OnPairingKeyedSessionWithStore_StillSendsFinalize()
    {
        // Positive control: the gate must not refuse the legitimate case.
        var (client, connection) = CreateWith(PskCategory.Pairing, withStore: true);
        using var _c = client;

        SendPairingActivate(connection, "pairing_psk");

        Assert.Single(connection.SentMessages.OfType<ClientPairFinalizeMessage>());
        Assert.DoesNotContain(connection.SentMessages, m => m is PairAbortMessage);
    }

    [Fact]
    public void Resolve_DoesNotMutateTheStore()
    {
        // Resolve runs inside ProcessInbound on the crypto receive path. It must be a
        // pure lookup: no state change, no disk write, and no marking a record used
        // before the AEAD has actually verified anything.
        var store = new InMemoryPairingRecordStore();
        var psk = new byte[32];
        psk[0] = 0x42;
        store.Upsert(new PairingRecord(psk, PskCategory.LongTerm, "srv-1"));

        var resolver = new RecordPskResolver(store);
        var resolved = resolver.Resolve(NoiseConstants.DerivePskId(psk));

        Assert.NotNull(resolved);
        Assert.False(Assert.Single(store.List()).Used, "Resolve must not mark the record used");
    }

    [Fact]
    public void PostPairingPromotion_MarksTheRotatedRecordUsed_OnANonPairingActivate()
    {
        // The flow HandleServerPairFinalize documents: the record is persisted with
        // used=false, the server re-handshakes onto it, and the activate that follows
        // carries 'playback' — not 'pairing'. The used-marking is per session, so the
        // re-handshake has to restart it here too, or this record reports used=false to
        // the server forever and every PSK after the first is misreported.
        var store = new InMemoryPairingRecordStore();
        var (client, connection, session) = TestClient.Create(
            PskCategory.Pairing,
            configure: options => options with { PairingRecordStore = store });
        using var _c = client;

        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"pairing":{"method":"pairing_psk"}}}""");
        connection.RaiseTextMessageReceived("""{"type":"server/pair-finalize","payload":{}}""");

        var promoted = Assert.Single(store.List());
        Assert.False(promoted.Used, "the new record starts unused");

        // The server rotates onto the new PSK in band; the framing installs a fresh hash.
        session.MatchedPsk = new NoisePsk(promoted.Psk.ToArray(), PskCategory.LongTerm, ServerId);
        session.HandshakeHash = Enumerable.Repeat((byte)0xAB, 32).ToArray();

        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["playback"],"active_roles":["player@v1"]}}""");

        Assert.True(
            Assert.Single(store.List()).Used,
            "the rotated-to record must be marked used once a message decrypts under it");
    }

    [Fact]
    public void MatchedPsk_IsMarkedUsed_OnceAnEncryptedMessageArrives()
    {
        // The record is marked used at the first proof the AEAD verified: an encrypted
        // application message we could actually decrypt.
        var store = new InMemoryPairingRecordStore();
        var psk = NoiseConstants.SentinelPsk.ToArray();
        store.Upsert(new PairingRecord(psk, PskCategory.LongTerm, null));

        var (client, connection, _) = TestClient.Create(
            PskCategory.LongTerm,
            configure: options => options with { PairingRecordStore = store });
        using var _c = client;
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();

        Assert.False(Assert.Single(store.List()).Used, "not used before any message arrives");

        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");

        Assert.True(Assert.Single(store.List()).Used, "used once a decrypted message arrives");
    }
}
