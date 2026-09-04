using Sendspin.SDK.Client;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Connection.Noise.Pairing;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Coverage for app-initiated pairing: EnsurePairingPsk / RotatePairingPsk and spec
/// #122's Pairing PSK lifecycle rules — CSPRNG-generated, persisted, per-client,
/// never consumed by a successful pairing, and never rotated except deliberately.
/// </summary>
public class PairingInitiationTests
{
    [Fact]
    public void EnsurePairingPsk_IsIdempotent_AndStoresExactlyOneRecord()
    {
        var store = new InMemoryPairingRecordStore();
        var (client, _, _) = CreateWithStore(store);
        using var _c = client;

        string first = client.EnsurePairingPsk();
        string second = client.EnsurePairingPsk();

        Assert.Equal(first, second);
        Assert.Single(store.List(), r => r.Category == PskCategory.Pairing);
    }

    [Fact]
    public void EnsurePairingPsk_PersistsAcrossClientRestart()
    {
        // Same store, new client = the reboot the spec's "persists across reboots" means.
        var store = new InMemoryPairingRecordStore();
        var identity = SendspinIdentity.Generate();

        var (first, _, _) = CreateWithStore(store, identity);
        string token;
        using (first)
        {
            token = first.EnsurePairingPsk();
        }

        var (second, _, _) = CreateWithStore(store, identity);
        using var _c = second;

        Assert.Equal(token, second.EnsurePairingPsk());
    }

    [Fact]
    public void EnsurePairingPsk_TokenEmbedsIdentityKeyAndStoredPsk()
    {
        var store = new InMemoryPairingRecordStore();
        var identity = SendspinIdentity.Generate();
        var (client, _, _) = CreateWithStore(store, identity);
        using var _c = client;

        var (clientKey, pairingPsk) = PairingToken.Decode(client.EnsurePairingPsk());

        Assert.Equal(identity.PublicKey.ToArray(), clientKey);
        var record = Assert.Single(store.List());
        Assert.Equal(PskCategory.Pairing, record.Category);
        Assert.Equal(record.Psk.ToArray(), pairingPsk);
    }

    [Fact]
    public void PairFinalize_WithAStoreButNoServerId_PersistsNothingAndDoesNotClaimSuccess()
    {
        // The other half of #158's rule. Now that CanOffer guarantees a record store for every
        // pair method, the remaining way to reach pair-finalize with nothing to persist is an
        // unknown server id — that comes from the Noise session, not from configuration, so a
        // degenerate peer can still produce it.
        //
        // The old shape logged "no record store configured" (untrue here) and then raised
        // PairingCompleted with an empty server id, telling the app a pairing had succeeded
        // that it holds no record for.
        var store = new InMemoryPairingRecordStore();
        var (client, connection, session) = CreateWithStore(store, category: PskCategory.Pairing);
        using var _c = client;

        client.EnsurePairingPsk();
        var stored = Assert.Single(store.List(), r => r.Category == PskCategory.Pairing);
        session.MatchedPsk = new NoisePsk(stored.Psk, PskCategory.Pairing);

        // Set before server/hello: SendSpinClient captures ServerId from the session there.
        session.ServerId = null;

        bool paired = false;
        client.PairingCompleted += (_, _) => paired = true;

        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"pairing":{"method":"pairing_psk"}}}""");
        connection.RaiseTextMessageReceived("""{"type":"server/pair-finalize","payload":{}}""");

        Assert.False(paired, "PairingCompleted must not fire for a pairing that persisted nothing");
        Assert.DoesNotContain(store.List(), r => r.Category == PskCategory.LongTerm);
    }

    [Fact]
    public void CompletedPairing_DoesNotConsumeThePairingPsk()
    {
        // Spec #122's most accident-prone rule: a successful pairing writes a long-term
        // record but must NOT retire the Pairing record — one Pairing PSK pairs this
        // client with any number of servers over its lifetime.
        var store = new InMemoryPairingRecordStore();
        var (client, connection, session) = CreateWithStore(store, category: PskCategory.Pairing);
        using var _c = client;

        string token = client.EnsurePairingPsk();
        var stored = Assert.Single(store.List());
        string pairingPskId = stored.PskId;

        // Key the session by the PSK EnsurePairingPsk actually generated, as a server
        // dialing in with the token is. A retirement bug keyed off the matched psk_id —
        // say in the used-marking or the finalize handler — now has a real record to
        // hit; with the fake's default sentinel-keyed session it would slip past unseen.
        session.MatchedPsk = new NoisePsk(stored.Psk, PskCategory.Pairing);

        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"pairing":{"method":"pairing_psk"}}}""");
        connection.RaiseTextMessageReceived("""{"type":"server/pair-finalize","payload":{}}""");

        // Positive control: without this, deleting the pairing flow entirely would leave
        // the Pairing record trivially "surviving" and the test would still pass.
        Assert.Single(store.List(), r => r.Category == PskCategory.LongTerm);

        var pairing = Assert.Single(store.List(), r => r.Category == PskCategory.Pairing);
        Assert.Equal(pairingPskId, pairing.PskId);
        Assert.NotNull(pairing.LastUsedUtc);
        Assert.Equal(token, client.EnsurePairingPsk());
    }

    [Fact]
    public void RotatePairingPsk_ReplacesTheRecord_LeavingExactlyOne()
    {
        var store = new InMemoryPairingRecordStore();
        var (client, _, _) = CreateWithStore(store);
        using var _c = client;

        string before = client.EnsurePairingPsk();
        string after = client.RotatePairingPsk();

        Assert.NotEqual(before, after);
        var record = Assert.Single(store.List(), r => r.Category == PskCategory.Pairing);
        Assert.Equal(record.Psk.ToArray(), PairingToken.Decode(after).PairingPsk);
        Assert.Equal(after, client.EnsurePairingPsk());
    }

    [Fact]
    public void RotatePairingPsk_RemovesEveryExistingPairingRecord()
    {
        // Two Pairing records should never arise, but rotation must still resolve to a
        // deterministic single record — a "remove the first" implementation would leave
        // the second seeded record behind and pass the single-record rotation test.
        var store = new InMemoryPairingRecordStore();
        var pskA = new byte[32];
        pskA[0] = 1;
        var pskB = new byte[32];
        pskB[0] = 2;
        store.Upsert(new PairingRecord(pskA, PskCategory.Pairing));
        store.Upsert(new PairingRecord(pskB, PskCategory.Pairing));

        var (client, _, _) = CreateWithStore(store);
        using var _c = client;

        string token = client.RotatePairingPsk();

        var record = Assert.Single(store.List(), r => r.Category == PskCategory.Pairing);
        Assert.Equal(record.Psk.ToArray(), PairingToken.Decode(token).PairingPsk);
    }

    [Fact]
    public void NoStoreConfigured_Throws_RatherThanReturningAnEphemeralToken()
    {
        // An ephemeral token would look valid, pair, and evaporate on restart with no
        // error anywhere — the degrade-silently failure mode this surface must refuse.
        var (client, _, _) = TestClient.Create();
        using var _c = client;

        Assert.Throws<InvalidOperationException>(() => client.EnsurePairingPsk());
        Assert.Throws<InvalidOperationException>(() => client.RotatePairingPsk());
    }

    [Fact]
    public void TwoFreshClients_GenerateDifferentPsks()
    {
        // Weak as a randomness test, but it catches a hard-coded or seeded default,
        // which is the realistic failure.
        var storeA = new InMemoryPairingRecordStore();
        var storeB = new InMemoryPairingRecordStore();
        var (clientA, _, _) = CreateWithStore(storeA);
        var (clientB, _, _) = CreateWithStore(storeB);
        using var _a = clientA;
        using var _b = clientB;

        byte[] pskA = PairingToken.Decode(clientA.EnsurePairingPsk()).PairingPsk;
        byte[] pskB = PairingToken.Decode(clientB.EnsurePairingPsk()).PairingPsk;

        Assert.NotEqual(pskA, pskB);
    }

    private static (SendspinClientService Client, FakeSendspinConnection Connection, FakeNoiseSession Session)
        CreateWithStore(
            IPairingRecordStore store,
            SendspinIdentity? identity = null,
            PskCategory category = PskCategory.LongTerm)
    {
        return TestClient.Create(
            category,
            configure: options =>
            {
                options = options with { PairingRecordStore = store };
                if (identity is not null)
                {
                    options = options with { Identity = identity };
                }

                return options;
            });
    }
}
