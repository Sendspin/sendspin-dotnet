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
    public void UnparseableDocument_StillOpensEmpty_WhenQuarantineMoveFails()
    {
        // A device that cannot construct its store cannot boot, whether the document is
        // unparseable or the quarantine move itself fails (e.g. a permission-restricted
        // destination - realistic on the very deployments this task locks down). Obstruct
        // every quarantine target File.Move could compute for "now" (the constructor reads
        // its own DateTime.UtcNow, which could tick over a second boundary after ours) with a
        // directory: on Windows this makes File.Move throw UnauthorizedAccessException, not
        // IOException - the type the original catch (IOException) missed, which would have
        // escaped Quarantine() and the constructor entirely.
        string path = WriteStoreFile("this is not json at all");

        if (OperatingSystem.IsWindows())
        {
            // Obstruct every quarantine target File.Move could compute for "now" - the
            // constructor reads its own DateTime.UtcNow, which may tick past ours - with a
            // directory. On Windows that makes File.Move throw UnauthorizedAccessException,
            // the type the original catch (IOException) missed.
            DateTime now = DateTime.UtcNow;
            for (int offsetSeconds = -1; offsetSeconds <= 1; offsetSeconds++)
            {
                Directory.CreateDirectory($"{path}.corrupt-{now.AddSeconds(offsetSeconds):yyyyMMddTHHmmssZ}");
            }

            AssertQuarantineFailedButStoreOpened(path);
            return;
        }

        // On Linux the directory obstruction throws IOException, which the *original* narrow
        // catch already handled - so this test passed with or without the fix on the only
        // platform CI runs (#103 item 5). A read-only parent directory provokes EACCES ->
        // UnauthorizedAccessException instead, which is the case that needed covering. It also
        // removes the second-boundary race the timestamp obstruction carried: a stall across
        // one second let the move succeed and the test assert nothing.
        File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            AssertQuarantineFailedButStoreOpened(path);
        }
        finally
        {
            File.SetUnixFileMode(
                _dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void AssertQuarantineFailedButStoreOpened(string path)
    {
        var store = new FilePairingRecordStore(path);

        Assert.Empty(store.List());
        Assert.True(File.Exists(path), "the move failed, so the unparsed file should still be where it was");
    }

    [Fact]
    public void Construction_NarrowsALegacyWorldReadableFile()
    {
        // The steady state for a consumer upgrading from 9.1.0: a records.json written by the
        // old truncate-then-write Save() at the platform default 0644, with raw PSKs in it. It
        // does not self-heal, because Save() is only reached by a new pairing or a Remove - an
        // already-paired client never rewrites the file, so the mode has to be fixed on load.
        string good = Base64UrlText.Encode(Enumerable.Repeat((byte)0x77, 32).ToArray());
        string path = WriteStoreFile($$"""
            [{"Psk":"{{good}}","Category":"LongTerm","ServerId":"srv-a","Used":true}]
            """);

        if (OperatingSystem.IsWindows())
        {
            // No Unix file mode: assert only that loading such a file still works.
            Assert.Single(new FilePairingRecordStore(path).List());
        }
        else
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);

            var store = new FilePairingRecordStore(path);

            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Single(store.List());   // narrowing must not cost the records
        }
    }

    [Fact]
    public void Upsert_PersistsAndReloads()
    {
        string path = Path.Combine(_dir, "records.json");

        var store = new FilePairingRecordStore(path);
        store.Upsert(new PairingRecord(Enumerable.Repeat((byte)0x55, 32).ToArray(), PskCategory.LongTerm, "srv"));

        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.Single(new FilePairingRecordStore(path).List());
    }

    [Fact]
    public void Upsert_LeavesThePriorFileIntact_WhenTheWriteFails()
    {
        // Pre-create the directory ourselves (rather than relying on Save() to do it) so the
        // first Upsert below cannot fail for that unrelated reason - the obstruction and
        // assertion further down are what this test is actually about.
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "records.json");
        var store = new FilePairingRecordStore(path);
        store.Upsert(new PairingRecord(Enumerable.Repeat((byte)0x55, 32).ToArray(), PskCategory.LongTerm, "srv"));
        string before = File.ReadAllText(path);

        // Make the directory unwritable so the next Save() fails before any move can happen. A
        // Save() reverted to a direct File.WriteAllText would fail here too - but only after
        // truncating - so the surviving `before` below is what distinguishes the two.
        //
        // Unix-only: SecureFile's temp name is random per write since #103, so obstructing one
        // specific path no longer works, and Windows has no one-line equivalent of a read-only
        // directory. ubuntu-latest is the CI platform, which is where this needs to run.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Exception? ex = Record.Exception(() =>
                store.Upsert(new PairingRecord(Enumerable.Repeat((byte)0x66, 32).ToArray(), PskCategory.LongTerm, "srv2")));

            Assert.NotNull(ex);
            Assert.Equal(before, File.ReadAllText(path));
        }
        finally
        {
            File.SetUnixFileMode(
                _dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
