using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Connection.Noise;

namespace Sendspin.SDK.Tests.Connection;

public class SecureFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "sendspin-securefile-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void WriteAllTextAtomic_RoundTrips_AndLeavesNoTempFile()
    {
        string path = Path.Combine(_dir, "data.json");

        SecureFile.WriteAllTextAtomic(path, """{"hello":"world"}""");

        Assert.Equal("""{"hello":"world"}""", File.ReadAllText(path));
        // A lingering .tmp means the move did not happen and a later run could read a stale half-write.
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void WriteAllTextAtomic_LeavesOriginalFileIntact_WhenTempWriteFails()
    {
        // Unix-only. The temp name is now random per write (#103 item 3), so a test cannot
        // obstruct one specific path any more; making the whole directory unwritable is what
        // fails every candidate name. Windows has no equivalent one-liner — SetAttributes
        // ReadOnly does not stop file creation — and ubuntu-latest is the CI platform, so this
        // is where the coverage has to live.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string path = Path.Combine(_dir, "data.json");
        SecureFile.WriteAllTextAtomic(path, "original");

        // r-x: entries can be read but none created. A direct-write implementation (straight to
        // `path`, no temp file) would also fail here — but it would fail *after* truncating, so
        // the surviving "original" below is what distinguishes the two.
        File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Exception? ex = Record.Exception(() => SecureFile.WriteAllTextAtomic(path, "corrupted"));

            Assert.NotNull(ex);
            Assert.Equal("original", File.ReadAllText(path));
        }
        finally
        {
            File.SetUnixFileMode(
                _dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void WriteAllTextAtomic_LeavesNoTempFileBehind_WhenTheMoveFails()
    {
        // The other half of the random-name change: a unique temp name that is never revisited
        // would leak a file holding the secret on every failed write, where the old fixed name
        // was at least overwritten by the next attempt.
        string path = Path.Combine(_dir, "data.json");
        Directory.CreateDirectory(path);   // target is a directory, so the move cannot land

        Exception? ex = Record.Exception(() => SecureFile.WriteAllTextAtomic(path, "contents"));

        Assert.NotNull(ex);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void WriteAllTextAtomic_Overwrites_RatherThanAppending()
    {
        string path = Path.Combine(_dir, "data.json");

        SecureFile.WriteAllTextAtomic(path, "first");
        SecureFile.WriteAllTextAtomic(path, "second");

        Assert.Equal("second", File.ReadAllText(path));
    }

    [Fact]
    public void WriteAllTextAtomic_CreatesMissingParentDirectory()
    {
        string path = Path.Combine(_dir, "nested", "deeper", "data.json");

        SecureFile.WriteAllTextAtomic(path, "x");

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void WriteAllTextAtomic_RestrictsPermissions_WhereThePlatformSupportsIt()
    {
        string path = Path.Combine(_dir, "secret.json");

        SecureFile.WriteAllTextAtomic(path, "psk");

        if (OperatingSystem.IsWindows())
        {
            // Windows has no Unix file mode: WriteAllTextAtomic leaves UnixCreateMode unset
            // there (assigning it throws PlatformNotSupportedException), and File.GetUnixFileMode
            // would throw too, so the only thing to assert here is that the write completed. The
            // file inherits the parent directory's ACL.
            Assert.True(File.Exists(path));
        }
        else
        {
            // CI runs ubuntu-latest, so this branch is the one that actually gates.
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    [Fact]
    public void NarrowExistingPermissions_TightensALegacyWorldReadableFile()
    {
        string path = Path.Combine(_dir, "legacy.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "psk");

        if (OperatingSystem.IsWindows())
        {
            // No Unix file mode to inspect, so the contract on Windows is "does nothing, does
            // not throw" - the file keeps its inherited ACL.
            Assert.False(SecureFile.NarrowExistingPermissions(path, NullLogger.Instance));
        }
        else
        {
            // 0644: what a file written before SecureFile owned the write path still looks like.
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);

            Assert.True(SecureFile.NarrowExistingPermissions(path, NullLogger.Instance));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            // Already narrow: reports no change, so callers can log only when it mattered.
            Assert.False(SecureFile.NarrowExistingPermissions(path, NullLogger.Instance));
        }
    }

    [Fact]
    public void ReadAllTextOrNull_ReturnsNull_WhenAbsent()
    {
        Assert.Null(SecureFile.ReadAllTextOrNull(Path.Combine(_dir, "missing.json")));
    }
}
