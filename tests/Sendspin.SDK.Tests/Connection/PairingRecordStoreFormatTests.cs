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
        // JsonSerializer.Serialize produced with default options.
        var psk = Enumerable.Repeat((byte)0x42, 32).ToArray();
        string pskB64 = Convert.ToBase64String(psk).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        File.WriteAllText(
            _path,
            $$"""[{"Psk":"{{pskB64}}","Category":"LongTerm","ServerId":null,"Used":true}]""");

        var store = new FilePairingRecordStore(_path);

        var record = Assert.Single(store.List());
        Assert.Equal(PskCategory.LongTerm, record.Category);
        Assert.Null(record.ServerId);
        Assert.True(record.Used);
        Assert.Equal(psk, record.Psk.ToArray());
    }

    [Fact]
    public void WhatThisVersionWrites_IsStillPascalCase()
    {
        // The other direction: a file this version writes must remain readable by anything
        // expecting the established shape. Asserted on the raw text, not by round-tripping.
        var store = new FilePairingRecordStore(_path);
        store.Upsert(new PairingRecord(
            Enumerable.Repeat((byte)0x42, 32).ToArray(), PskCategory.LongTerm, ServerId: null));

        string written = File.ReadAllText(_path);

        Assert.Contains("\"Psk\":", written, StringComparison.Ordinal);
        Assert.Contains("\"Category\":", written, StringComparison.Ordinal);
        Assert.Contains("\"Used\":", written, StringComparison.Ordinal);
        Assert.DoesNotContain("\"psk\":", written, StringComparison.Ordinal);
        Assert.DoesNotContain("server_id", written, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
        GC.SuppressFinalize(this);
    }
}
