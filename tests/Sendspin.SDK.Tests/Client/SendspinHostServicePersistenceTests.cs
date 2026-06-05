using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Coverage for the additive ILastPlayedServerStore persistence seam on the host service.
/// The host is constructed but never started, so these tests touch no network.
/// </summary>
public class SendspinHostServicePersistenceTests
{
    private static SendspinHostService NewHost(ILastPlayedServerStore? store = null, string? seed = null) =>
        new(NullLoggerFactory.Instance, lastPlayedServerId: seed, lastPlayedServerStore: store);

    [Fact]
    public async Task Construction_SeedsLastPlayedFromStore()
    {
        var store = new FakeLastPlayedServerStore { Stored = "srv-x" };
        await using var host = NewHost(store: store);
        Assert.Equal("srv-x", host.LastPlayedServerId);
    }

    [Fact]
    public async Task SeedParam_WinsOverStore()
    {
        var store = new FakeLastPlayedServerStore { Stored = "srv-store" };
        await using var host = NewHost(store: store, seed: "srv-seed");
        Assert.Equal("srv-seed", host.LastPlayedServerId);
    }

    [Fact]
    public async Task SetLastPlayedServerId_PersistsToStore()
    {
        var store = new FakeLastPlayedServerStore();
        await using var host = NewHost(store: store);
        host.SetLastPlayedServerId("srv-y");
        Assert.Contains("srv-y", store.Saved);
    }

    [Fact]
    public async Task ThrowingStore_OnLoad_DoesNotBreakConstruction()
    {
        var store = new FakeLastPlayedServerStore { ThrowOnLoad = true };
        await using var host = NewHost(store: store);
        Assert.Null(host.LastPlayedServerId);
    }

    [Fact]
    public async Task ThrowingStore_OnSave_DoesNotThrow()
    {
        var store = new FakeLastPlayedServerStore { ThrowOnSave = true };
        await using var host = NewHost(store: store);
        host.SetLastPlayedServerId("srv-z"); // must not throw
        Assert.Equal("srv-z", host.LastPlayedServerId);
    }

    private sealed class FakeLastPlayedServerStore : ILastPlayedServerStore
    {
        public string? Stored { get; set; }
        public List<string> Saved { get; } = new();
        public bool ThrowOnLoad { get; set; }
        public bool ThrowOnSave { get; set; }

        public string? Load()
        {
            if (ThrowOnLoad) throw new InvalidOperationException("load failed");
            return Stored;
        }

        public void Save(string serverId)
        {
            if (ThrowOnSave) throw new InvalidOperationException("save failed");
            Saved.Add(serverId);
        }
    }
}
