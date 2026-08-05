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
        // This is the whole point of the seam: client_id must survive a restart.
        var store = new MemoryIdentityStore();

        string first = SendspinIdentity.FromStore(store).PeerId;
        string second = SendspinIdentity.FromStore(store).PeerId;

        Assert.Equal(first, second);
        Assert.Equal(1, store.SaveCount);   // the second call loads, it does not re-save
    }

    [Fact]
    public void FromStore_OnCorruptBlob_ThrowsSomethingActionable()
    {
        var store = new MemoryIdentityStore { Blob = [1, 2, 3] };

        var ex = Assert.ThrowsAny<Exception>(() => SendspinIdentity.FromStore(store));

        // The message must name what failed, not surface a bare FormatException from a decoder.
        Assert.Contains("identity", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FileSendspinIdentityStore_RoundTripsAcrossInstances()
    {
        string path = Path.Combine(_dir, "identity.json");

        string first = SendspinIdentity.FromStore(new FileSendspinIdentityStore(path)).PeerId;
        string second = SendspinIdentity.FromStore(new FileSendspinIdentityStore(path)).PeerId;

        Assert.Equal(first, second);
    }

    [Fact]
    public void FileSendspinIdentityStore_Load_ReturnsNull_WhenAbsent()
    {
        Assert.Null(new FileSendspinIdentityStore(Path.Combine(_dir, "nope.json")).Load());
    }
}
