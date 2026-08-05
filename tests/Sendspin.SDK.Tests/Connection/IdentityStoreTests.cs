using Sendspin.SDK.Connection.Noise;

namespace Sendspin.SDK.Tests.Connection;

public class IdentityStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "sendspin-identity-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Records what the SDK handed it, so the blob stays opaque to the test too.</summary>
    private sealed class MemoryIdentityStore : ISendspinIdentityStore
    {
        internal byte[]? Blob;
        internal int SaveCount;

        public byte[]? Load() => Blob;

        public void Save(byte[] identityBlob)
        {
            Blob = identityBlob;
            SaveCount++;
        }
    }

    [Fact]
    public void FromStore_OnFirstRun_GeneratesAndPersists()
    {
        var store = new MemoryIdentityStore();

        var identity = SendspinIdentity.FromStore(store);

        Assert.NotNull(identity.PeerId);
        Assert.Equal(43, identity.PeerId.Length);
        Assert.Equal(1, store.SaveCount);
        Assert.NotNull(store.Blob);
    }

    [Fact]
    public void FromStore_Twice_YieldsTheSameIdentity()
    {
        // This is the whole point of the seam: client_id must survive a restart. PeerId alone
        // isn't enough to prove that — it's derived only from the public half, so a mutation
        // that garbles the private half while leaving the public half intact would still pass
        // a PeerId-only check, and every Noise handshake after the "restart" would then fail.
        var store = new MemoryIdentityStore();

        var first = SendspinIdentity.FromStore(store);
        var second = SendspinIdentity.FromStore(store);

        Assert.Equal(first.PeerId, second.PeerId);
        Assert.Equal(first.PrivateKey.ToArray(), second.PrivateKey.ToArray());
        Assert.Equal(1, store.SaveCount);   // the second call loads, it does not re-save
    }

    [Fact]
    public void FromStore_OnCorruptBlob_ThrowsSomethingActionable()
    {
        var store = new MemoryIdentityStore { Blob = [1, 2, 3] };

        var ex = Assert.Throws<InvalidOperationException>(() => SendspinIdentity.FromStore(store));

        // The message must name what failed, not surface a bare FormatException from a decoder.
        Assert.Contains("identity", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FileSendspinIdentityStore_RoundTripsAcrossInstances()
    {
        string path = Path.Combine(_dir, "identity.json");

        var first = SendspinIdentity.FromStore(new FileSendspinIdentityStore(path));
        var second = SendspinIdentity.FromStore(new FileSendspinIdentityStore(path));

        Assert.Equal(first.PeerId, second.PeerId);
        Assert.Equal(first.PrivateKey.ToArray(), second.PrivateKey.ToArray());
    }

    [Fact]
    public void FileSendspinIdentityStore_Load_ReturnsNull_WhenAbsent()
    {
        Assert.Null(new FileSendspinIdentityStore(Path.Combine(_dir, "nope.json")).Load());
    }

    [Fact]
    public void FileSendspinIdentityStore_OnGarbageContents_NamesThePath()
    {
        // Not a wrong-length blob (the length check already covers that) — genuine corruption,
        // e.g. a hand-edited file or a stale file left by a consumer migrating off hand-rolled
        // storage. This must not surface a bare FormatException from Convert.FromBase64String.
        string path = Path.Combine(_dir, "identity.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "{\"identity\":\"not-base64\"}");

        var ex = Assert.Throws<InvalidOperationException>(
            () => SendspinIdentity.FromStore(new FileSendspinIdentityStore(path)));

        Assert.Contains(path, ex.Message);
    }
}
