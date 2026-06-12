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
}
