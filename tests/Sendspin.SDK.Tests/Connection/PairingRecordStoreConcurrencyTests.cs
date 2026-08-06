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

    [Fact]
    public async Task List_DuringConcurrentUpsertRemove_DoesNotThrow()
    {
        var store = new InMemoryPairingRecordStore();
        for (int i = 0; i < WindowSize; i++)
            store.Upsert(new PairingRecord(MakePsk(i), PskCategory.LongTerm, "srv"));

        Exception? firstException = null;

        void Capture(Exception ex) => Interlocked.CompareExchange(ref firstException, ex, null);

        var writer = Task.Run(() =>
        {
            for (int i = 0; i < Iterations; i++)
            {
                try
                {
                    var record = new PairingRecord(MakePsk(WindowSize + i), PskCategory.LongTerm, "srv");
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
            for (int i = 0; i < Iterations; i++)
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
}
