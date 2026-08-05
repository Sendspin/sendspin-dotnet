using Sendspin.SDK.Connection.Noise;

namespace Sendspin.SDK.Tests.Connection;

public class PairingRecordStoreResilienceTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "sendspin-records-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string WriteStoreFile(string json)
    {
        string path = Path.Combine(_dir, "records.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void OneMalformedEntry_DoesNotDiscardTheOthers()
    {
        // Previously Enum.Parse / Base64UrlText.Decode threw out of the constructor, so a
        // single bad byte lost every pairing the client had.
        string goodA = Base64UrlText.Encode(Enumerable.Repeat((byte)0x11, 32).ToArray());
        string goodB = Base64UrlText.Encode(Enumerable.Repeat((byte)0x22, 32).ToArray());
        string path = WriteStoreFile($$"""
            [
              {"Psk":"{{goodA}}","Category":"LongTerm","ServerId":"srv-a","Used":false},
              {"Psk":"not-valid-base64url!!","Category":"LongTerm","ServerId":"srv-bad","Used":false},
              {"Psk":"{{goodB}}","Category":"Pairing","ServerId":null,"Used":false}
            ]
            """);

        var store = new FilePairingRecordStore(path);

        Assert.Equal(2, store.List().Count);
    }

    [Fact]
    public void UnrecognisedCategory_SkipsOnlyThatEntry()
    {
        string good = Base64UrlText.Encode(Enumerable.Repeat((byte)0x33, 32).ToArray());
        string bad = Base64UrlText.Encode(Enumerable.Repeat((byte)0x44, 32).ToArray());
        string path = WriteStoreFile($$"""
            [
              {"Psk":"{{good}}","Category":"LongTerm","ServerId":"srv-a","Used":false},
              {"Psk":"{{bad}}","Category":"Telepathy","ServerId":"srv-b","Used":false}
            ]
            """);

        var store = new FilePairingRecordStore(path);

        Assert.Single(store.List());
    }

    [Fact]
    public void UnparseableDocument_IsQuarantined_AndTheStoreStillOpens()
    {
        // A device that cannot construct its store cannot boot. Quarantine, log, continue
        // empty: trust drops to 'none', which fails closed, and the user re-pairs.
        string path = WriteStoreFile("this is not json at all");

        var store = new FilePairingRecordStore(path);

        Assert.Empty(store.List());
        Assert.NotEmpty(Directory.GetFiles(_dir, "records.json.corrupt-*"));
        Assert.False(File.Exists(path), "the corrupt file should have been moved, not copied");
    }

    [Fact]
    public void Upsert_WritesAtomically_LeavingNoTempFile()
    {
        string path = Path.Combine(_dir, "records.json");

        var store = new FilePairingRecordStore(path);
        store.Upsert(new PairingRecord(Enumerable.Repeat((byte)0x55, 32).ToArray(), PskCategory.LongTerm, "srv"));

        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.Single(new FilePairingRecordStore(path).List());
    }
}
