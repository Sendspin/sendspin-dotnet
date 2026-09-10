using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Connection.Noise;

namespace Sendspin.SDK.Tests.Connection;

public class PairingRecordStoreConcurrencyTests
{
    // SendspinHostService shares one store across every client it constructs, and
    // RecordPskResolver.Resolve reads it from the framing handshake path - neither is
    // reachable by a client's own private lock. This drives the collision that lock was
    // never able to prevent: one thread mutating the store's backing Dictionary while
    // another enumerates it via List()'s Values.ToList().
    //
    // The store has to hold more than a handful of records for this to bite: with 0-1
    // entries, List()'s Values.ToList() call - which reads the dictionary's Count, allocates
    // an array, then copies into it - completes in too few instructions for a concurrent
    // Upsert/Remove to realistically land in between. Keeping a rolling window of WindowSize
    // records live widens that window enough to make the race land inside a runnable
    // iteration count.
    private const int WindowSize = 500;
    private const int Iterations = 500_000;

    // FilePairingRecordStore's Save() writes the whole store to disk inside the lock (twice
    // per writer iteration), so the file variant runs a far smaller writer load - the
    // mutation count only needs to collide once with a full-speed reader - and a reader
    // count high enough to keep it spinning in List() for the writer's whole run.
    private const int FileWindowSize = 100;
    private const int FileWriterIterations = 1_000;
    private const int FileReaderIterations = 200_000;

    [Fact]
    public async Task List_DuringConcurrentUpsertRemove_DoesNotThrow()
    {
        var store = new InMemoryPairingRecordStore();

        await AssertListSurvivesConcurrentUpsertRemoveAsync(store, WindowSize, Iterations, Iterations);
    }

    [Fact]
    public async Task FileStore_List_DuringConcurrentUpsertRemove_DoesNotThrow()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sendspin-conc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var store = new FilePairingRecordStore(Path.Combine(dir, "records.json"));

            await AssertListSurvivesConcurrentUpsertRemoveAsync(
                store, FileWindowSize, FileWriterIterations, FileReaderIterations);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task PersistLongTerm_SerializesTheFullEvictAndUpsertTransaction_PerStore()
    {
        var store = new BlockingBoundedPairingRecordStore(
            2,
            new PairingRecord(MakePsk(0), PskCategory.LongTerm, "srv-0", DateTimeOffset.UnixEpoch));

        var first = Task.Run(() =>
            PairingRecords.PersistLongTerm(
                store,
                MakePsk(1),
                "srv-1",
                Array.Empty<string>(),
                NullLogger.Instance));

        store.WaitForFirstNewLongTermUpsert();

        int listCallsBeforeSecond = store.ListCallCount;
        var secondStarted = new ManualResetEventSlim();
        var second = Task.Run(() =>
        {
            secondStarted.Set();
            PairingRecords.PersistLongTerm(
                store,
                MakePsk(2),
                "srv-2",
                Array.Empty<string>(),
                NullLogger.Instance);
        });

        Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(5)), "the second pairing task never started");
        Assert.False(
            SpinWait.SpinUntil(
                () => store.ListCallCount > listCallsBeforeSecond,
                TimeSpan.FromMilliseconds(200)),
            "the second pairing reached the store before the first had finished its transaction");

        store.ReleaseFirstNewLongTermUpsert();
        await Task.WhenAll(first, second);

        var records = store.List()
            .Where(r => r.Category == PskCategory.LongTerm)
            .ToList();
        Assert.Equal(2, records.Count);
        Assert.DoesNotContain(records, r => r.ServerId == "srv-0");
        Assert.Contains(records, r => r.ServerId == "srv-1");
        Assert.Contains(records, r => r.ServerId == "srv-2");
    }

    private static async Task AssertListSurvivesConcurrentUpsertRemoveAsync(
        IPairingRecordStore store, int windowSize, int writerIterations, int readerIterations)
    {
        for (int i = 0; i < windowSize; i++)
            store.Upsert(new PairingRecord(MakePsk(i), PskCategory.LongTerm, "srv"));

        Exception? firstException = null;

        void Capture(Exception ex) => Interlocked.CompareExchange(ref firstException, ex, null);

        var writer = Task.Run(() =>
        {
            for (int i = 0; i < writerIterations; i++)
            {
                try
                {
                    var record = new PairingRecord(MakePsk(windowSize + i), PskCategory.LongTerm, "srv");
                    store.Upsert(record);
                    store.Remove(NoiseConstants.DerivePskId(MakePsk(i).Span));
                }
                catch (Exception ex)
                {
                    Capture(ex);
                    return;
                }
            }
        });

        var reader = Task.Run(() =>
        {
            for (int i = 0; i < readerIterations; i++)
            {
                try
                {
                    _ = store.List();
                }
                catch (Exception ex)
                {
                    Capture(ex);
                    return;
                }
            }
        });

        await Task.WhenAll(writer, reader);

        if (firstException is not null)
            Assert.Fail($"List/Upsert/Remove raced: {firstException}");
    }

    private static ReadOnlyMemory<byte> MakePsk(int i)
    {
        var bytes = new byte[32];
        BitConverter.GetBytes(i).CopyTo(bytes, 0);
        return bytes;
    }

    private sealed class BlockingBoundedPairingRecordStore : IPairingRecordStore
    {
        private readonly Dictionary<string, PairingRecord> _records;
        private readonly object _lock = new();
        private readonly ManualResetEventSlim _firstNewLongTermUpsertEntered = new(false);
        private readonly ManualResetEventSlim _allowFirstNewLongTermUpsert = new(false);
        private int _blockFirstNewLongTermUpsert = 1;
        private int _listCallCount;

        internal BlockingBoundedPairingRecordStore(int capacity, params PairingRecord[] seed)
        {
            Capacity = capacity;
            _records = seed.ToDictionary(r => r.PskId);
        }

        public int Capacity { get; }

        internal int ListCallCount => Volatile.Read(ref _listCallCount);

        public IReadOnlyList<PairingRecord> List()
        {
            Interlocked.Increment(ref _listCallCount);
            lock (_lock)
            {
                return _records.Values.ToList();
            }
        }

        public void Upsert(PairingRecord record)
        {
            if (record.Category == PskCategory.LongTerm
                && Interlocked.CompareExchange(ref _blockFirstNewLongTermUpsert, 0, 1) == 1)
            {
                _firstNewLongTermUpsertEntered.Set();
                if (!_allowFirstNewLongTermUpsert.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("Timed out waiting to release the first long-term upsert.");
                }
            }

            lock (_lock)
            {
                if (record.Category == PskCategory.LongTerm
                    && !_records.ContainsKey(record.PskId)
                    && _records.Values.Count(r => r.Category == PskCategory.LongTerm) >= Capacity)
                {
                    throw new InvalidOperationException(
                        $"Upsert of a new long-term record at capacity {Capacity}; the caller did not evict first.");
                }

                _records[record.PskId] = record;
            }
        }

        public void Remove(string pskId)
        {
            lock (_lock)
            {
                _records.Remove(pskId);
            }
        }

        internal void WaitForFirstNewLongTermUpsert()
        {
            Assert.True(
                _firstNewLongTermUpsertEntered.Wait(TimeSpan.FromSeconds(5)),
                "the first pairing never reached Upsert");
        }

        internal void ReleaseFirstNewLongTermUpsert() => _allowFirstNewLongTermUpsert.Set();
    }
}
