using Sendspin.SDK.Connection.Noise.Pairing;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Tests.Client;

namespace Sendspin.SDK.Tests.Connection;

public class PairingCodeLockoutStoreConcurrencyTests
{
    // SendspinHostService hands one lockout store to every client it constructs, and each
    // client counts on its own connection's receive thread, so two servers pairing at once
    // reach the store from two threads.
    [Fact]
    public async Task InMemoryStore_ConcurrentSetFailures_KeepsEveryCounter()
    {
        var store = new InMemoryPairingCodeLockoutStore();

        await WriteDistinctMethodsConcurrentlyAsync(store, writers: 8, perWriter: 20_000);

        AssertEveryCounterPresent(store, writers: 8, perWriter: 20_000);
    }

    [Fact]
    public async Task FileStore_ConcurrentSetFailures_PersistsEveryCounter()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sendspin-lockout-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "lockout.json");

            // Every write is flushed to disk, so the load is small: each writer only has to
            // overlap another once for a stale snapshot to drop a counter from the file.
            await WriteDistinctMethodsConcurrentlyAsync(
                new FilePairingCodeLockoutStore(path), writers: 4, perWriter: 40);

            AssertEveryCounterPresent(new FilePairingCodeLockoutStore(path), writers: 4, perWriter: 40);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TwoConnectionsCountingARoundAtOnce_BothCount()
    {
        var lockouts = new RendezvousLockoutStore();
        await using var a = await PairingHarness.StartAsync(lockouts: lockouts);
        await using var b = await PairingHarness.StartAsync(lockouts: lockouts);
        a.SendPairingActivate(method: "dynamic_pairing_code");
        b.SendPairingActivate(method: "dynamic_pairing_code");
        await a.NextMessageAsync<ClientPairInitMessage>();
        await b.NextMessageAsync<ClientPairInitMessage>();

        // A round is counted when server/pair-init arrives. Each read now waits for the other
        // connection's read before returning, so unserialized both would see 0 and write 1.
        lockouts.Armed = true;
        await Task.WhenAll(
            Task.Run(() => a.SendServerPairInit()),
            Task.Run(() => b.SendServerPairInit()));
        lockouts.Armed = false;

        Assert.Equal(2, lockouts.GetFailures("dynamic_pairing_code"));
    }

    private static async Task WriteDistinctMethodsConcurrentlyAsync(
        IPairingCodeLockoutStore store, int writers, int perWriter)
    {
        using var start = new ManualResetEventSlim();
        Task[] tasks = Enumerable.Range(0, writers)
            .Select(w => Task.Run(() =>
            {
                start.Wait();
                for (int i = 0; i < perWriter; i++)
                {
                    store.SetFailures($"m{w}-{i}", i + 1);
                    store.GetFailures($"m{w}-{i}");
                }
            }))
            .ToArray();
        start.Set();
        await Task.WhenAll(tasks);
    }

    private static void AssertEveryCounterPresent(IPairingCodeLockoutStore store, int writers, int perWriter)
    {
        for (int w = 0; w < writers; w++)
        {
            for (int i = 0; i < perWriter; i++)
            {
                Assert.Equal(i + 1, store.GetFailures($"m{w}-{i}"));
            }
        }
    }

    /// <summary>
    /// A thread-safe store whose read, while armed, holds its result until a second read has
    /// started or a timeout passes — which makes two unserialized read-modify-writes overlap.
    /// </summary>
    private sealed class RendezvousLockoutStore : IPairingCodeLockoutStore
    {
        private readonly InMemoryPairingCodeLockoutStore _inner = new();
        private int _readers;

        public volatile bool Armed;

        public int GetFailures(string method)
        {
            int failures = _inner.GetFailures(method);
            if (Armed)
            {
                Interlocked.Increment(ref _readers);
                SpinWait.SpinUntil(() => Volatile.Read(ref _readers) >= 2, TimeSpan.FromMilliseconds(500));
            }

            return failures;
        }

        public void SetFailures(string method, int failures) => _inner.SetFailures(method, failures);
    }
}
