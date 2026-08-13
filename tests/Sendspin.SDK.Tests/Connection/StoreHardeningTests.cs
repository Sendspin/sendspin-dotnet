using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Connection.Noise.Pairing;

namespace Sendspin.SDK.Tests.Connection;

/// <summary>
/// Residual storage findings from the secure-storage slice (#103): a file the process cannot
/// chmod must not stop the client starting, and the identity file — the one holding the private
/// key — narrows on load like the other two stores already did.
/// </summary>
public sealed class StoreHardeningTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "sendspin-hardening-" + Guid.NewGuid().ToString("N")[..8]);

    public StoreHardeningTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (!Directory.Exists(_dir))
            return;

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                _dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void IdentityStore_NarrowsALegacyWorldReadableFile()
    {
        // The README promised this for all three stores; only two did it. This is the file that
        // holds the private key, and a consumer who provisions or copies an identity.key at 0644
        // — which Load's own contract anticipates — got no narrowing at all.
        string path = Path.Combine(_dir, "identity.key");
        var store = new FileSendspinIdentityStore(path, NullLogger.Instance);
        store.Save(Enumerable.Repeat((byte)0x42, 69).ToArray());

        if (OperatingSystem.IsWindows())
        {
            Assert.NotNull(store.Load());
            return;
        }

        File.SetUnixFileMode(
            path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);

        Assert.NotNull(store.Load());

        var mode = File.GetUnixFileMode(path);
        Assert.Equal(UnixFileMode.None, mode & UnixFileMode.OtherRead);
        Assert.Equal(UnixFileMode.None, mode & UnixFileMode.GroupRead);
    }

    [Fact]
    public void AnUnChmodableFile_DoesNotStopTheStoresFromOpening()
    {
        // Owned by another uid on a Docker bind mount, or a mount that rejects chmod (CIFS,
        // exFAT). SetUnixFileMode threw straight out of the constructors, so a file the client
        // could still *read* stopped it booting — against the record store's own stated
        // principle that a single bad byte cannot stop the client from starting.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string records = Path.Combine(_dir, "records.json");
        File.WriteAllText(records, "[]");
        File.SetUnixFileMode(
            records, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);

        string lockouts = Path.Combine(_dir, "lockout.json");
        File.WriteAllText(lockouts, "{}");
        File.SetUnixFileMode(
            lockouts, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);

        // r-x on the parent: the files stay readable, but chmod on anything inside is refused.
        File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        Assert.Null(Record.Exception(() => new FilePairingRecordStore(records, NullLogger.Instance)));
        Assert.Null(Record.Exception(() => new FilePinLockoutStore(lockouts, NullLogger.Instance)));
    }

    [Fact]
    public void AChmodableFile_IsStillNarrowed()
    {
        // Positive control for the pair above: swallowing the chmod failure must not turn into
        // never attempting the chmod, which would satisfy both of those tests.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string records = Path.Combine(_dir, "records.json");
        File.WriteAllText(records, "[]");
        File.SetUnixFileMode(
            records, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);

        _ = new FilePairingRecordStore(records, NullLogger.Instance);

        Assert.Equal(
            UnixFileMode.None, File.GetUnixFileMode(records) & UnixFileMode.OtherRead);
    }

    [Fact]
    public void LockoutStore_DoesNotAdvanceItsCounterWhenThePersistFails()
    {
        // The counter is a brute-force guard. Mutating memory before persisting left a failed
        // write with memory ahead of disk, so a restart rolled the count back — small, but a
        // fail-open.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string path = Path.Combine(_dir, "lockout.json");
        var store = new FilePinLockoutStore(path, NullLogger.Instance);
        store.SetFailures("dynamic_pin", 3);

        File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.ThrowsAny<Exception>(() => store.SetFailures("dynamic_pin", 9));

            // In memory it must still read what disk holds, not the value that never landed.
            Assert.Equal(3, store.GetFailures("dynamic_pin"));
        }
        finally
        {
            File.SetUnixFileMode(
                _dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.Equal(3, new FilePinLockoutStore(path, NullLogger.Instance).GetFailures("dynamic_pin"));
    }

    [Fact]
    public void SecureFile_RefusesToWriteThroughAPlantedTempFile()
    {
        // POSIX applies open()'s mode only on creation, so a fixed temp name that already exists
        // is opened and truncated at whatever mode it carries — a planted 0666 file would have
        // received the plaintext private key. A random name plus CreateNew makes that
        // unreachable; this pins that a write still succeeds and lands owner-only with an
        // attacker-planted file sitting at the old predictable name.
        string path = Path.Combine(_dir, "identity.key");
        File.WriteAllText(path + ".tmp", "planted");

        SecureFile.WriteAllTextAtomic(path, "secret");

        Assert.Equal("secret", File.ReadAllText(path));
        Assert.Equal("planted", File.ReadAllText(path + ".tmp"));

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.None, File.GetUnixFileMode(path) & UnixFileMode.OtherRead);
        }
    }
}
