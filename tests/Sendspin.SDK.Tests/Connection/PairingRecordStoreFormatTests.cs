using Sendspin.SDK.Connection.Noise;

namespace Sendspin.SDK.Tests.Connection;

/// <summary>
/// The pairing store's on-disk format must not move.
/// </summary>
/// <remarks>
/// Moving the store from reflection-based <c>System.Text.Json</c> to a source-generated
/// context (#89) is only safe if the property names come out the same. The old code used
/// default options, so members were written PascalCase. Attaching the protocol context —
/// which applies <c>SnakeCaseLower</c> — would have renamed every field and orphaned every
/// file already on disk: the store would read a file it could not map, find no records, and
/// the device would look unpaired while its long-term PSKs sat there untouched. That failure
/// is invisible to a round-trip test, because a round-trip renames both sides at once.
/// </remarks>
public class PairingRecordStoreFormatTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"sendspin-store-{Guid.NewGuid():N}.json");

    [Fact]
    public void AFileWrittenByAnEarlierVersion_StillLoads()
    {
        // Written by hand in the pre-source-gen shape: PascalCase members, exactly what
        // JsonSerializer.Serialize produced with default options. The bare Used flag is the
        // pre-#183 shape, before the store needed a timestamp to evict by.
        var psk = Enumerable.Repeat((byte)0x42, 32).ToArray();
        string pskB64 = Convert.ToBase64String(psk).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        File.WriteAllText(
            _path,
            $$"""[{"Psk":"{{pskB64}}","Category":"LongTerm","ServerId":null,"Used":true}]""");

        var store = new FilePairingRecordStore(_path);

        var record = Assert.Single(store.List());
        Assert.Equal(PskCategory.LongTerm, record.Category);
        Assert.Null(record.ServerId);

        // Used-at-an-unknown-time, so it sorts ahead of every dated record for eviction rather
        // than being promoted to never-used.
        Assert.Equal(DateTimeOffset.MinValue, record.LastUsedUtc);
        Assert.Equal(psk, record.Psk.ToArray());
    }

    [Fact]
    public void WhatThisVersionWrites_IsStillPascalCase()
    {
        // The other direction: a file this version writes must remain readable by anything
        // expecting the established shape. Asserted on the raw text, not by round-tripping.
        var store = new FilePairingRecordStore(_path);
        store.Upsert(new PairingRecord(
            Enumerable.Repeat((byte)0x42, 32).ToArray(),
            PskCategory.LongTerm,
            "server-1",
            DateTimeOffset.UnixEpoch));

        string written = File.ReadAllText(_path);

        Assert.Contains("\"Psk\":", written, StringComparison.Ordinal);
        Assert.Contains("\"Category\":", written, StringComparison.Ordinal);
        Assert.Contains("\"LastUsed\":", written, StringComparison.Ordinal);

        // The retired flag is no longer written; it is read only to migrate an old file.
        Assert.DoesNotContain("\"Used\":", written, StringComparison.Ordinal);
        Assert.DoesNotContain("\"psk\":", written, StringComparison.Ordinal);
        Assert.DoesNotContain("server_id", written, StringComparison.Ordinal);
    }

    [Fact]
    public void ARecordWrittenByThisVersion_RoundTripsItsLastUsedInstant()
    {
        var when = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var store = new FilePairingRecordStore(_path);
        store.Upsert(new PairingRecord(
            Enumerable.Repeat((byte)0x11, 32).ToArray(), PskCategory.LongTerm, "server-1", when));

        var reloaded = Assert.Single(new FilePairingRecordStore(_path).List());
        Assert.Equal(when, reloaded.LastUsedUtc);
        Assert.Equal("server-1", reloaded.ServerId);
    }

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
        GC.SuppressFinalize(this);
    }
}
