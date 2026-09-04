using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Tests.Client;

namespace Sendspin.SDK.Tests.Connection;

/// <summary>
/// Spec #183, "Pairing records": a pairing record is a long-term PSK bound to one
/// <c>server_id</c>; re-pairing with a server replaces that server's record rather than adding
/// a second; a pairing that completes with the store at capacity must still succeed, by
/// evicting a record that is not backing a currently-open connection; and <c>psk_id</c>
/// uniqueness is enforced where the PSK is drawn.
/// </summary>
public class PairingRecordSemanticsTests
{
    private static byte[] Psk(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    private static PairingRecord LongTerm(byte fill, string serverId, DateTimeOffset? lastUsed = null) =>
        new(Psk(fill), PskCategory.LongTerm, serverId, lastUsed);

    private static void Persist(
        IPairingRecordStore store,
        byte[] psk,
        string serverId,
        params string[] livePskIds) =>
        PairingRecords.PersistLongTerm(store, psk, serverId, livePskIds, NullLogger.Instance);

    [Fact]
    public void RePairingWithTheSameServer_ReplacesThatServersRecord()
    {
        // One record per server: the second pairing carries a new PSK, and the old one must
        // not linger. Left behind it would keep matching handshakes the server has already
        // stopped using, and it would consume a slot the capacity rule counts.
        var store = new InMemoryPairingRecordStore();
        store.Upsert(LongTerm(0x11, "srv-A"));
        store.Upsert(LongTerm(0x22, "srv-B"));

        Persist(store, Psk(0x33), "srv-A");

        var forA = Assert.Single(store.List(), r => r.ServerId == "srv-A");
        Assert.Equal(NoiseConstants.DerivePskId(Psk(0x33)), forA.PskId);
        Assert.Equal(2, store.List().Count);
        Assert.Contains(store.List(), r => r.ServerId == "srv-B");
    }

    [Fact]
    public void RePairingWithTheSameServer_DoesNotEvictAnything_EvenAtCapacity()
    {
        // The replacement frees the slot before the capacity check, so a re-pair at capacity
        // is a like-for-like swap. A store that evicted here would unpair an unrelated server
        // for no reason.
        var store = new BoundedPairingRecordStore(
            2,
            LongTerm(0x11, "srv-A", DateTimeOffset.UnixEpoch),
            LongTerm(0x22, "srv-B", DateTimeOffset.UnixEpoch.AddDays(1)));

        Persist(store, Psk(0x33), "srv-B");

        Assert.Equal(2, store.List().Count);
        Assert.Contains(store.List(), r => r.ServerId == "srv-A");
        Assert.Equal(
            NoiseConstants.DerivePskId(Psk(0x33)),
            Assert.Single(store.List(), r => r.ServerId == "srv-B").PskId);
    }

    [Fact]
    public void PairingAtCapacity_EvictsTheLeastRecentlyUsedRecord_AndPersists()
    {
        // "a client that is at capacity MUST evict an existing pairing record" — the pairing
        // succeeds. BoundedPairingRecordStore throws if the SDK tries to overflow it, so a
        // missing eviction fails here rather than quietly losing the new record.
        var store = new BoundedPairingRecordStore(
            3,
            LongTerm(0x11, "srv-old", DateTimeOffset.UnixEpoch),
            LongTerm(0x22, "srv-mid", DateTimeOffset.UnixEpoch.AddDays(1)),
            LongTerm(0x33, "srv-new", DateTimeOffset.UnixEpoch.AddDays(2)));

        Persist(store, Psk(0x44), "srv-fresh");

        Assert.Equal(3, store.List().Count);
        Assert.DoesNotContain(store.List(), r => r.ServerId == "srv-old");
        Assert.Contains(store.List(), r => r.ServerId == "srv-fresh");
        Assert.Contains(store.List(), r => r.ServerId == "srv-mid");
        Assert.Contains(store.List(), r => r.ServerId == "srv-new");
    }

    [Fact]
    public void PairingAtCapacity_StampsTheNewRecordsLastUse_SoItIsNotTheNextVictim()
    {
        var store = new BoundedPairingRecordStore(2, LongTerm(0x11, "srv-A", DateTimeOffset.UnixEpoch));
        var before = DateTimeOffset.UtcNow;

        Persist(store, Psk(0x44), "srv-fresh");

        var fresh = Assert.Single(store.List(), r => r.ServerId == "srv-fresh");
        Assert.NotNull(fresh.LastUsedUtc);
        Assert.True(fresh.LastUsedUtc >= before);
    }

    [Fact]
    public void PairingAtCapacity_NeverEvictsARecordBackingAnOpenConnection()
    {
        // "MUST NOT evict a pairing record that is currently in use by an open connection."
        // The oldest record is also the live one, so LRU alone would pick exactly the record
        // the rule forbids — that is the point of the fixture.
        var live = LongTerm(0x11, "srv-live", DateTimeOffset.UnixEpoch);
        var store = new BoundedPairingRecordStore(
            2,
            live,
            LongTerm(0x22, "srv-idle", DateTimeOffset.UnixEpoch.AddDays(1)));

        Persist(store, Psk(0x44), "srv-fresh", live.PskId);

        Assert.Contains(store.List(), r => r.PskId == live.PskId);
        Assert.DoesNotContain(store.List(), r => r.ServerId == "srv-idle");
        Assert.Contains(store.List(), r => r.ServerId == "srv-fresh");
    }

    [Fact]
    public void PairingAtCapacity_NeverEvictsTheClientsOwnPairingPsk()
    {
        // The Pairing PSK is this client's bootstrap secret, not a pairing record. Dropping it
        // would strand the client with no way to be paired again.
        var pairingPsk = new PairingRecord(Psk(0x11), PskCategory.Pairing);
        var store = new BoundedPairingRecordStore(
            2,
            pairingPsk,
            LongTerm(0x22, "srv-idle", DateTimeOffset.UnixEpoch.AddDays(1)));

        Persist(store, Psk(0x44), "srv-fresh");

        Assert.Contains(store.List(), r => r.Category == PskCategory.Pairing);
        Assert.DoesNotContain(store.List(), r => r.ServerId == "srv-idle");
    }

    [Fact]
    public void GenerateUniquePsk_RetriesUntilThePskIdIsUnused()
    {
        // psk_id uniqueness is enforced where the PSK is drawn, because the client selects a
        // record by that identifier alone. A real CSPRNG never collides, so the draw is
        // substituted to make the retry observable at all.
        var store = new InMemoryPairingRecordStore();
        store.Upsert(LongTerm(0x11, "srv-A"));
        store.Upsert(LongTerm(0x22, "srv-B"));

        var draws = new Queue<byte[]>([Psk(0x11), Psk(0x22), Psk(0x33)]);
        byte[] psk = PairingRecords.GenerateUniquePsk(store, draws.Dequeue);

        Assert.Equal(Psk(0x33), psk);
        Assert.Empty(draws);
    }

    [Fact]
    public void GenerateUniquePsk_GivesUpRatherThanSpinning_WhenEveryDrawCollides()
    {
        var store = new InMemoryPairingRecordStore();
        store.Upsert(LongTerm(0x11, "srv-A"));

        int draws = 0;
        byte[] Always()
        {
            draws++;
            return Psk(0x11);
        }

        Assert.Throws<InvalidOperationException>(() => PairingRecords.GenerateUniquePsk(store, Always));
        Assert.Equal(PairingRecords.PskGenerationAttempts, draws);
    }

    [Fact]
    public void GenerateUniquePsk_DrawsThirtyTwoBytes_ByDefault()
    {
        var store = new InMemoryPairingRecordStore();

        Assert.Equal(32, PairingRecords.GenerateUniquePsk(store).Length);
    }
}
