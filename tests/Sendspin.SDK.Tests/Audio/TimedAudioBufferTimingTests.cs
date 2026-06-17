using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;

namespace Sendspin.SDK.Tests.Audio;

/// <summary>
/// Timing-path regression tests for TimedAudioBuffer covering:
/// - Segments enqueued before clock sync must be re-evaluated with the
///   converged clock at playback start (mid-track join bug, 9.0.3 item 1).
/// - Output-device prefill must not register as a persistent sync error
///   that triggers audible corrections (phantom -100ms bug, 9.0.3 item 2).
/// </summary>
public class TimedAudioBufferTimingTests
{
    // 48kHz stereo -> 96 interleaved samples per millisecond
    private const int SamplesPerMs = 96;
    private const int ChunkMs = 20;
    private const int ChunkSamples = ChunkMs * SamplesPerMs;

    // Server's monotonic clock is small (booted recently); the local clock is
    // a typical large monotonic value. Pre-sync conversion (offset 0) returns
    // raw server time, which is absurdly far in the local past.
    private const long ServerT0 = 1_000_000;
    private const long LocalNow = 9_000_000_000_000;

    private static readonly AudioFormat Format = new()
    {
        Codec = "pcm",
        SampleRate = 48_000,
        Channels = 2,
    };

    private static TimedAudioBuffer CreateBuffer(FakeClockSynchronizer clockSync) =>
        new(Format, clockSync, bufferCapacityMs: 2000);

    /// <summary>
    /// Enqueues <paramref name="chunks"/> consecutive 20ms chunks starting at ServerT0.
    /// </summary>
    private static void EnqueueBurst(TimedAudioBuffer buffer, int chunks)
    {
        var samples = new float[ChunkSamples];
        for (var i = 0; i < chunks; i++)
        {
            buffer.Write(samples, ServerT0 + (i * ChunkMs * 1000L));
        }
    }

    [Fact]
    public void MidTrackJoin_BurstEnqueuedBeforeSync_OnlyGenuinelyPastAudioSkipped()
    {
        var clockSync = new FakeClockSynchronizer(); // offset 0 = pre-sync
        using var buffer = CreateBuffer(clockSync);

        // 1600ms burst arrives before any time-sync measurement completes
        EnqueueBurst(buffer, 80);

        // Clock sync converges: the first burst segment maps to 400ms in the past
        // (we joined mid-track; the first 400ms of the burst has already played
        // elsewhere). The remaining 1200ms is the only copy of upcoming audio.
        clockSync.OffsetMicroseconds = LocalNow - 400_000 - ServerT0;
        clockSync.IsConverged = true;
        clockSync.HasMinimalSync = true;

        var output = new float[ChunkSamples];
        var read = buffer.Read(output, LocalNow);

        var stats = buffer.GetStats();
        var droppedMs = stats.DroppedSamples / (double)SamplesPerMs;

        Assert.True(read > 0, "playback should start within the grace window");
        Assert.InRange(droppedMs, 350, 450); // only the genuinely-past ~400ms
        Assert.InRange(buffer.BufferedMilliseconds, 1000, 1300); // burst preserved
    }

    [Fact]
    public void MidTrackJoin_BurstMapsToFuture_NothingSkippedWhileWaiting()
    {
        var clockSync = new FakeClockSynchronizer();
        using var buffer = CreateBuffer(clockSync);

        EnqueueBurst(buffer, 80);

        // After convergence the first segment is 100ms in the future:
        // we should wait silently, not discard anything.
        clockSync.OffsetMicroseconds = LocalNow + 100_000 - ServerT0;
        clockSync.IsConverged = true;
        clockSync.HasMinimalSync = true;

        var output = new float[ChunkSamples];
        var read = buffer.Read(output, LocalNow);

        var stats = buffer.GetStats();

        Assert.Equal(0, read);
        Assert.Equal(0, stats.DroppedSamples);
        Assert.InRange(buffer.BufferedMilliseconds, 1500, 1700);
    }

    /// <summary>
    /// Drives a WASAPI-style startup: the device gulps 100ms instantly at start,
    /// then settles into steady 10ms callbacks. Returns the buffer mid-session
    /// at wall time LocalNow + 800ms with stabilization complete.
    /// </summary>
    private static (TimedAudioBuffer Buffer, long WallTime) RunPrefillStartup(
        FakeClockSynchronizer clockSync)
    {
        var buffer = CreateBuffer(clockSync);

        // Segments map to "now": first chunk is due immediately
        clockSync.OffsetMicroseconds = LocalNow - ServerT0;
        clockSync.IsConverged = true;
        clockSync.HasMinimalSync = true;

        EnqueueBurst(buffer, 80); // 1600ms of audio

        // Prefill gulp: device reads its full 100ms output buffer at Play()
        var gulp = new float[100 * SamplesPerMs];
        buffer.Read(gulp, LocalNow);

        // Steady state: 10ms callback every 10ms of wall time for 800ms.
        // Startup grace (500ms of output) ends partway through, after which
        // the corrector sees whatever sync error remains.
        var callback = new float[10 * SamplesPerMs];
        long wallTime = LocalNow;
        for (var k = 1; k <= 80; k++)
        {
            wallTime = LocalNow + (k * 10_000L);
            buffer.Read(callback, wallTime);
        }

        return (buffer, wallTime);
    }

    /// <summary>
    /// Same WASAPI-style startup as <see cref="RunPrefillStartup"/>, but drives the buffer through the
    /// external-correction <c>ReadRaw</c> path instead of <c>Read</c> — so the ReadRaw baseline capture
    /// (the host-resampler path the Windows client uses) is what's exercised.
    /// </summary>
    private static (TimedAudioBuffer Buffer, long WallTime) RunPrefillStartupRaw(
        FakeClockSynchronizer clockSync)
    {
        var buffer = CreateBuffer(clockSync);

        clockSync.OffsetMicroseconds = LocalNow - ServerT0;
        clockSync.IsConverged = true;
        clockSync.HasMinimalSync = true;

        EnqueueBurst(buffer, 80);

        var gulp = new float[100 * SamplesPerMs];
        buffer.ReadRaw(gulp, LocalNow);

        var callback = new float[10 * SamplesPerMs];
        long wallTime = LocalNow;
        for (var k = 1; k <= 80; k++)
        {
            wallTime = LocalNow + (k * 10_000L);
            buffer.ReadRaw(callback, wallTime);
        }

        return (buffer, wallTime);
    }

    [Fact]
    public void PrefillGulp_BaselineSelfZeroes_NoAudibleCorrections()
    {
        var clockSync = new FakeClockSynchronizer();
        var (buffer, _) = RunPrefillStartup(clockSync);
        using (buffer)
        {
            var stats = buffer.GetStats();

            // The constant 100ms read-ahead is plumbing, not drift: after
            // stabilization the reported error must be ~zero and no corrective
            // drops/inserts may have been issued.
            Assert.InRange(Math.Abs(stats.SyncErrorMs), 0, 5);
            Assert.Equal(0, stats.SamplesInsertedForSync);
            Assert.Equal(0, stats.SamplesDroppedForSync);
            Assert.Equal(SyncCorrectionMode.None, stats.CurrentCorrectionMode);
        }
    }

    [Fact]
    public void PrefillGulp_RawPath_BaselineSelfZeroes()
    {
        var clockSync = new FakeClockSynchronizer();
        var (buffer, _) = RunPrefillStartupRaw(clockSync);
        using (buffer)
        {
            // The external-correction (ReadRaw) path must subtract the constant 100ms prefill
            // baseline just like the internal Read path, so the reported error settles to ~zero.
            // Without the ReadRaw baseline capture this reads ~-100ms and the external resampler
            // would grind it out — guards that the ReadRaw startup-baseline block isn't deleted.
            Assert.InRange(Math.Abs(buffer.GetStats().SyncErrorMs), 0, 5);
        }
    }

    [Fact]
    public void Reconnect_RawPath_ReCapturesBaseline_AbsorbsNewPrefill()
    {
        var clockSync = new FakeClockSynchronizer();

        // Larger buffer/burst so the full 2s reconnect-stabilization window runs via ReadRaw.
        var buffer = new TimedAudioBuffer(Format, clockSync, bufferCapacityMs: 8000);
        using (buffer)
        {
            clockSync.OffsetMicroseconds = LocalNow - ServerT0;
            clockSync.IsConverged = true;
            clockSync.HasMinimalSync = true;

            var chunk = new float[ChunkSamples];
            for (var i = 0; i < 300; i++) // ~6s of audio
            {
                buffer.Write(chunk, ServerT0 + (i * ChunkMs * 1000L));
            }

            // Startup via ReadRaw past the 500ms grace: baseline captured, error ~0.
            var gulp = new float[100 * SamplesPerMs];
            buffer.ReadRaw(gulp, LocalNow);
            var cb = new float[10 * SamplesPerMs];
            long wall = LocalNow;
            for (var k = 1; k <= 80; k++)
            {
                wall = LocalNow + (k * 10_000L);
                buffer.ReadRaw(cb, wall);
            }

            Assert.InRange(Math.Abs(buffer.GetStats().SyncErrorMs), 0, 5);

            // Reconnect, then the backend re-primes (another 100ms gulp). Without the ReadRaw
            // reconnect re-capture this leaks as a persistent ~-100ms error after stabilization
            // — the same prefill bug relocated to the reconnect case.
            buffer.NotifyReconnect();
            buffer.ReadRaw(gulp, wall);

            // Run past the 2s reconnect-stabilization window.
            for (var k = 0; k < 230; k++)
            {
                wall += 10_000L;
                buffer.ReadRaw(cb, wall);
            }

            // Re-capture must have absorbed the new prefill: error back to ~0.
            Assert.InRange(Math.Abs(buffer.GetStats().SyncErrorMs), 0, 5);
        }
    }

    [Fact]
    public void StallAfterStabilization_IsStillCorrected_ViaResampling()
    {
        var clockSync = new FakeClockSynchronizer();
        var (buffer, wallTime) = RunPrefillStartup(clockSync);
        using (buffer)
        {
            // Genuine 60ms stall: wall clock advances, no reads happen.
            wallTime += 60_000;

            // Resume steady callbacks; the corrector should engage.
            var callback = new float[10 * SamplesPerMs];
            for (var k = 0; k < 30; k++)
            {
                buffer.Read(callback, wallTime);
                wallTime += 10_000;
            }

            var stats = buffer.GetStats();

            // A real 60ms lag must be corrected — but inaudibly: moderate
            // errors route through rate adjustment, not frame drop/insert.
            Assert.Equal(SyncCorrectionMode.Resampling, stats.CurrentCorrectionMode);
            Assert.True(
                stats.TargetPlaybackRate > 1.0,
                $"expected speed-up rate > 1.0, got {stats.TargetPlaybackRate}");
        }
    }

    [Fact]
    public void OutputLatency_PreRollsScheduledStart_StartsAudioThatWouldOtherwiseWait()
    {
        // Same setup as MidTrackJoin_BurstMapsToFuture: the first segment maps 100ms into the future,
        // so a latency-free buffer waits silently (that test asserts read == 0). A 100ms output latency
        // must pre-roll the scheduled start to "now" — the buffer has to hand the sample to the device
        // 100ms early so it reaches the speaker on the server's clock — so playback begins immediately.
        var clockSync = new FakeClockSynchronizer
        {
            OffsetMicroseconds = LocalNow + 100_000 - ServerT0,
            IsConverged = true,
            HasMinimalSync = true,
        };
        using var buffer = CreateBuffer(clockSync);
        buffer.OutputLatencyMicroseconds = 100_000;

        EnqueueBurst(buffer, 80);

        var output = new float[ChunkSamples];
        var read = buffer.Read(output, LocalNow);

        Assert.True(read > 0, "output latency should pre-roll the scheduled start so playback begins now");
    }
}
