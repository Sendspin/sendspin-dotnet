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
    private static (SendspinClientService Client, FakeSendspinConnection Connection) CreateWithStore(
        IPairingRecordStore store,
        SendspinIdentity? identity = null,
        PskCategory category = PskCategory.LongTerm)
    {
        var (client, connection, _) = TestClient.Create(
            category,
            configure: options =>
            {
                options.PairingRecordStore = store;
                if (identity is not null)
                {
                    options.Identity = identity;
                }
            });
        return (client, connection);
    }

    [Fact]
    public void EnsurePairingPsk_IsIdempotent_AndStoresExactlyOneRecord()
    {
        var store = new InMemoryPairingRecordStore();
        var (client, _) = CreateWithStore(store);
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

        var (first, _) = CreateWithStore(store, identity);
        string token;
        using (first)
        {
            token = first.EnsurePairingPsk();
        }

        var (second, _) = CreateWithStore(store, identity);
        using var _c = second;

        Assert.Equal(token, second.EnsurePairingPsk());
    }

    [Fact]
    public void EnsurePairingPsk_TokenEmbedsIdentityKeyAndStoredPsk()
    {
        var store = new InMemoryPairingRecordStore();
        var identity = SendspinIdentity.Generate();
        var (client, _) = CreateWithStore(store, identity);
        using var _c = client;

        var (clientKey, pairingPsk) = PairingToken.Decode(client.EnsurePairingPsk());

        Assert.Equal(identity.PublicKey.ToArray(), clientKey);
        var record = Assert.Single(store.List());
        Assert.Equal(PskCategory.Pairing, record.Category);
        Assert.Equal(record.Psk.ToArray(), pairingPsk);
    }

    [Fact]
    public void CompletedPairing_DoesNotConsumeThePairingPsk()
    {
        // Spec #122's most accident-prone rule: a successful pairing writes a long-term
        // record but must NOT retire the Pairing record — one Pairing PSK pairs this
        // client with any number of servers over its lifetime.
        var store = new InMemoryPairingRecordStore();
        var (client, connection) = CreateWithStore(store, category: PskCategory.Pairing);
        using var _c = client;

        string token = client.EnsurePairingPsk();
        string pairingPskId = Assert.Single(store.List()).PskId;

        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"selected_pair_method":"pairing_psk"}}""");
        connection.RaiseTextMessageReceived("""{"type":"server/pair-finalize","payload":{}}""");

        // Positive control: without this, deleting the pairing flow entirely would leave
        // the Pairing record trivially "surviving" and the test would still pass.
        Assert.Single(store.List(), r => r.Category == PskCategory.LongTerm);

        var pairing = Assert.Single(store.List(), r => r.Category == PskCategory.Pairing);
        Assert.Equal(pairingPskId, pairing.PskId);
        Assert.Equal(token, client.EnsurePairingPsk());
    }

    [Fact]
    public void RotatePairingPsk_ReplacesTheRecord_LeavingExactlyOne()
    {
        var store = new InMemoryPairingRecordStore();
        var (client, _) = CreateWithStore(store);
        using var _c = client;

        string before = client.EnsurePairingPsk();
        string after = client.RotatePairingPsk();

        Assert.NotEqual(before, after);
        var record = Assert.Single(store.List(), r => r.Category == PskCategory.Pairing);
        Assert.Equal(record.Psk.ToArray(), PairingToken.Decode(after).PairingPsk);
        Assert.Equal(after, client.EnsurePairingPsk());
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
        var (clientA, _) = CreateWithStore(storeA);
        var (clientB, _) = CreateWithStore(storeB);
        using var _a = clientA;
        using var _b = clientB;

        byte[] pskA = PairingToken.Decode(clientA.EnsurePairingPsk()).PairingPsk;
        byte[] pskB = PairingToken.Decode(clientB.EnsurePairingPsk()).PairingPsk;

        Assert.NotEqual(pskA, pskB);
    }
}
