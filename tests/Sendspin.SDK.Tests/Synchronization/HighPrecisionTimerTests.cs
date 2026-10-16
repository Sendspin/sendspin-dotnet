using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Tests.Synchronization;

public class HighPrecisionTimerTests
{
    [Fact]
    public void CountsOnRawMonotonicClock_OnLinuxOnly()
    {
        // aiosendspin timestamps on CLOCK_MONOTONIC_RAW. Stopwatch reads CLOCK_MONOTONIC on
        // Linux, which NTP slews, so Linux must use the raw clock; Windows and macOS Stopwatch
        // is already unslewed. On a Linux runner this also proves the libc binding resolves.
        Assert.Equal(OperatingSystem.IsLinux(), HighPrecisionTimer.UsesRawMonotonicClock);
    }

    [Fact]
    public async Task Elapsed_TracksRealTime()
    {
        // Pins the tick-to-microsecond conversion: a clock counted in the wrong unit would run
        // a thousand times fast or slow here.
        var timer = new HighPrecisionTimer();
        var wall = System.Diagnostics.Stopwatch.StartNew();

        var start = timer.GetCurrentTimeMicroseconds();
        await Task.Delay(200);
        var elapsedUs = timer.GetElapsedMicroseconds(start);
        wall.Stop();

        var wallUs = wall.Elapsed.Ticks / 10;
        Assert.InRange(elapsedUs, wallUs - 20_000, wallUs + 20_000);
    }

    [Fact]
    public void CurrentTime_IsUnixEpochMicroseconds_AndNeverDecreases()
    {
        var timer = new HighPrecisionTimer();
        var nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;

        var previous = timer.GetCurrentTimeMicroseconds();
        Assert.InRange(previous, nowUs - 1_000_000, nowUs + 1_000_000);

        for (var i = 0; i < 10_000; i++)
        {
            var current = timer.GetCurrentTimeMicroseconds();
            Assert.True(current >= previous, $"time went backwards: {previous} -> {current}");
            previous = current;
        }
    }
}
