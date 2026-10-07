using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Tests.Synchronization;

public class MonotonicTimerTests
{
    private sealed class FakeInnerTimer : IHighPrecisionTimer
    {
        public long CurrentTime { get; set; }

        public long GetCurrentTimeMicroseconds() => CurrentTime;

        public long GetElapsedMicroseconds(long fromTimeMicroseconds) =>
            CurrentTime - fromTimeMicroseconds;
    }

    [Fact]
    public void NormalChunkArrivalPolling_100msGaps_NotClamped()
    {
        // The timer is polled on audio chunk receipt (~10Hz / 100ms gaps in
        // production). Normal polling gaps must not trip the VM-pause clamp,
        // or the returned timeline runs at a fraction of real time (9.0.3 item 5).
        var inner = new FakeInnerTimer { CurrentTime = 1_000_000 };
        var timer = new MonotonicTimer(inner);

        var start = timer.GetCurrentTimeMicroseconds(); // initializes

        long last = start;
        for (var i = 0; i < 20; i++)
        {
            inner.CurrentTime += 100_000; // 100ms polling gap
            last = timer.GetCurrentTimeMicroseconds();
        }

        Assert.Equal(0, timer.ForwardJumpCount);
        Assert.Equal(start + (20 * 100_000L), last); // timeline tracks real time
    }

    [Fact]
    public void GenuineVmPause_MultiSecondJump_StillClamped()
    {
        var inner = new FakeInnerTimer { CurrentTime = 1_000_000 };
        var timer = new MonotonicTimer(inner);

        var start = timer.GetCurrentTimeMicroseconds();

        inner.CurrentTime += 5_000_000; // 5s VM pause
        var afterJump = timer.GetCurrentTimeMicroseconds();

        Assert.Equal(1, timer.ForwardJumpCount);
        Assert.Equal(timer.MaxDeltaMicroseconds, afterJump - start);
    }

    /// <summary>
    /// An inner clock that advances one microsecond per read, so every read is unique and ordered
    /// whichever thread makes it.
    /// </summary>
    private sealed class CountingInnerTimer : IHighPrecisionTimer
    {
        private long _now = 1_000_000;

        public long Now => Volatile.Read(ref _now);

        public long GetCurrentTimeMicroseconds() => Interlocked.Increment(ref _now);

        public long GetElapsedMicroseconds(long fromTimeMicroseconds) => Now - fromTimeMicroseconds;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConcurrentReads_NeverRunAheadOfTheInnerClockOrBackwards(bool resetWhileReading)
    {
        // The pipeline reads this timer from the audio callback and from the receive thread, and
        // resets it from a third (issue #349). Two reads that overlap must not both add their
        // delta, and a reset must not land halfway through a read.
        const int Readers = 4;
        const int ReadsPerThread = 200_000;

        var inner = new CountingInnerTimer();
        var timer = new MonotonicTimer(inner);
        var aheadOfInner = 0;
        var wentBackwards = 0;
        var readersRunning = Readers;
        using var start = new ManualResetEventSlim();

        var threads = new List<Thread>();
        for (var t = 0; t < Readers; t++)
        {
            threads.Add(new Thread(() =>
            {
                start.Wait();
                long last = 0;
                for (var i = 0; i < ReadsPerThread; i++)
                {
                    var value = timer.GetCurrentTimeMicroseconds();
                    if (value > inner.Now)
                    {
                        Interlocked.Increment(ref aheadOfInner);
                    }

                    if (value < last)
                    {
                        Interlocked.Increment(ref wentBackwards);
                    }

                    last = value;
                }

                Interlocked.Decrement(ref readersRunning);
            }));
        }

        if (resetWhileReading)
        {
            threads.Add(new Thread(() =>
            {
                start.Wait();
                while (Volatile.Read(ref readersRunning) > 0)
                {
                    timer.Reset();
                }
            }));
        }

        threads.ForEach(t => t.Start());
        start.Set();
        threads.ForEach(t => t.Join());

        Assert.Equal(0, aheadOfInner);
        Assert.Equal(0, wentBackwards);
        Assert.Equal(0, timer.ForwardJumpCount);
        Assert.Equal(0, timer.BackwardJumpCount);
    }
}
