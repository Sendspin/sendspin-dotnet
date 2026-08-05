using System.Security.Cryptography;
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
    public void FromStore_WritesAVersionedChecksummedBlob_AndReadsItBack()
    {
        var store = new MemoryIdentityStore();

        var identity = SendspinIdentity.FromStore(store);

        // Format lock: [version:1][private:32][public:32][truncated SHA-256 of the key material:4].
        byte[] blob = store.Blob!;
        Assert.Equal(69, blob.Length);
        Assert.Equal(1, blob[0]);
        Assert.Equal(identity.PublicKey.ToArray(), blob[33..65]);
        Assert.Equal(SHA256.HashData(blob[1..65])[..4], blob[65..]);

        Assert.Equal(identity.PrivateKey.ToArray(), SendspinIdentity.FromStore(store).PrivateKey.ToArray());
    }

    [Fact]
    public void FromStore_OnUnrecognisedBlobVersion_ThrowsNamingTheIdentity()
    {
        // A newer SDK's format, or a flipped byte in the version position. Either way, guessing
        // at the layout is worse than refusing to load it.
        var store = new MemoryIdentityStore();
        SendspinIdentity.FromStore(store);
        store.Blob![0] = 0x7F;

        var ex = Assert.Throws<InvalidOperationException>(() => SendspinIdentity.FromStore(store));

        Assert.Contains("identity", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("version", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromStore_OnCorruptedKeyMaterial_IsRejected_NotSilentlyAccepted()
    {
        // The corruption shape nothing else catches: one flipped bit in the private half. The
        // length is still right and the public half still decodes, so before the checksum this
        // returned an identity whose PeerId looked correct and whose every Noise handshake
        // failed at MAC verification, forever, with no error naming the identity or its file.
        var store = new MemoryIdentityStore();
        var original = SendspinIdentity.FromStore(store);
        store.Blob![1] ^= 0x01;

        // The public half - and therefore the PeerId a length-only check would hand back - is
        // untouched, which is exactly why this needed detecting.
        Assert.Equal(original.PeerId, Base64UrlText.Encode(store.Blob[33..65]));

        var ex = Assert.Throws<InvalidOperationException>(() => SendspinIdentity.FromStore(store));

        Assert.Contains("identity", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("checksum", ex.Message, StringComparison.OrdinalIgnoreCase);
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
