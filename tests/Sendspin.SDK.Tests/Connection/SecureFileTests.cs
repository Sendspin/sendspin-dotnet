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
        string path = Path.Combine(_dir, "data.json");
        SecureFile.WriteAllTextAtomic(path, "original");

        // Obstruct the temp path with a directory so the temp-file write step fails before
        // any move can happen. A direct-write implementation (skipping the temp file and
        // writing straight to `path`) would never touch this obstruction and would silently
        // clobber `path` instead — that's what this test needs to fail against.
        Directory.CreateDirectory(path + ".tmp");

        Exception? ex = Record.Exception(() => SecureFile.WriteAllTextAtomic(path, "corrupted"));

        Assert.NotNull(ex);
        Assert.Equal("original", File.ReadAllText(path));
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
    public void ReadAllTextOrNull_ReturnsNull_WhenAbsent()
    {
        Assert.Null(SecureFile.ReadAllTextOrNull(Path.Combine(_dir, "missing.json")));
    }
}
