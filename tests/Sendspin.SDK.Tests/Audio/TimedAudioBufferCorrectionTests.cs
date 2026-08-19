using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;

namespace Sendspin.SDK.Tests.Audio;

/// <summary>
/// The correction policy as the spec and the C++ reference define it: a ~100 µs dead band
/// (issue #235), rate correction capped at ±0.5% (issue #228), a one-shot snap above ~5 ms
/// (issue #232), a content timeline that cannot silently shift (issue #229), and a startup
/// anchor tied to the schedule rather than to whenever the callback fired (issue #233).
/// </summary>
public class TimedAudioBufferCorrectionTests
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int SamplesPerMs = SampleRate * Channels / 1000; // 96
    private const int ChunkMs = 20;
    private const int StepMs = 10;
    private const long ServerT0 = 1_000_000;
    private const long LocalT0 = 9_000_000_000_000;

    private static readonly AudioFormat Format = new()
    {
        Codec = "pcm",
        SampleRate = SampleRate,
        Channels = Channels,
    };

    /// <summary>
    /// A player running in real time: 10 ms callbacks against a wall clock, with the producer
    /// kept a fixed distance ahead. Disturbances are injected by moving the wall clock or the
    /// content timeline, exactly as a stall or a lost chunk would.
    /// </summary>
    private sealed class Player : IDisposable
    {
        private readonly float[] _chunk = new float[ChunkMs * SamplesPerMs];
        private readonly float[] _callback = new float[StepMs * SamplesPerMs];
        private readonly bool _rawReads;

        public FakeClockSynchronizer ClockSync { get; } = new();

        public TimedAudioBuffer Buffer { get; }

        public long WallNow { get; private set; } = LocalT0;

        public long WriteServerTs { get; private set; } = ServerT0;

        /// <summary>Server lead maintained by the producer, in microseconds.</summary>
        public long LeadMicroseconds { get; set; } = 500_000;

        public Player(SyncCorrectionOptions? options = null, bool rawReads = false)
        {
            _rawReads = rawReads;
            Buffer = new TimedAudioBuffer(Format, ClockSync, bufferCapacityMs: 5_000, options);
            Array.Fill(_chunk, 0.25f);

            // Converged clock scheduling the first chunk exactly now, so playback starts on
            // time and nothing is snapped at startup.
            ClockSync.OffsetMicroseconds = ServerT0 - LocalT0;
            ClockSync.IsConverged = true;
            ClockSync.HasMinimalSync = true;

            PumpProducer();

            // First callback lands exactly on the scheduled start, so nothing is snapped at
            // startup and a test's own disturbance is the only thing being measured.
            Read();
        }

        /// <summary>Server position the wall clock currently corresponds to.</summary>
        private long ServerNow => WallNow + ClockSync.OffsetMicroseconds;

        /// <summary>Writes chunks until the producer is <see cref="LeadMicroseconds"/> ahead.</summary>
        public void PumpProducer()
        {
            while (WriteServerTs < ServerNow + LeadMicroseconds)
            {
                Buffer.Write(_chunk, WriteServerTs);
                WriteServerTs += ChunkMs * 1000L;
            }
        }

        /// <summary>One 10 ms callback: wall clock advances, producer keeps up, buffer is read.</summary>
        public void Step()
        {
            WallNow += StepMs * 1000L;
            PumpProducer();
            Read();
        }

        public void Steps(int count)
        {
            for (var i = 0; i < count; i++)
            {
                Step();
            }
        }

        /// <summary>Reads one callback without advancing the wall clock (runs the player ahead).</summary>
        public void Read()
        {
            if (_rawReads)
            {
                Buffer.ReadRaw(_callback, WallNow);
            }
            else
            {
                Buffer.Read(_callback, WallNow);
            }
        }

        /// <summary>Wall clock advances with no callback: the player falls behind.</summary>
        public void Stall(long microseconds) => WallNow += microseconds;

        /// <summary>Skips <paramref name="chunks"/> chunks of content without writing them.</summary>
        public void LoseChunks(int chunks) => WriteServerTs += chunks * ChunkMs * 1000L;

        /// <summary>Writes one chunk at an explicit timestamp, bypassing the producer.</summary>
        public void WriteAt(long serverTimestamp) => Buffer.Write(_chunk, serverTimestamp);

        /// <summary>Runs past the 500 ms startup grace so the baseline is captured and error ~0.</summary>
        public Player Settled()
        {
            Steps(100);
            return this;
        }

        public void Dispose() => Buffer.Dispose();
    }

    private static double Ms(long samples) => samples / (double)SamplesPerMs;

    [Fact]
    public void ErrorBelowDeadband_ProducesNoCorrectionAtAll()
    {
        using var player = new Player().Settled();

        // 50 µs out — half the ~100 µs dead band the spec suggests and the reference uses.
        player.Stall(50);
        player.Steps(30);

        var stats = player.Buffer.GetStats();
        Assert.Equal(SyncCorrectionMode.None, stats.CurrentCorrectionMode);
        Assert.Equal(1.0, stats.TargetPlaybackRate, 6);
        Assert.Equal(0, stats.HardSyncCount);
        Assert.Equal(0, stats.SamplesDroppedForSync);
        Assert.Equal(0, stats.SamplesInsertedForSync);
    }

    [Fact]
    public void ErrorAboveDeadbandBelowHardSync_CorrectsByRateWithinSpecCap()
    {
        using var player = new Player().Settled();

        // 3 ms: past the dead band, inside the hard-sync threshold.
        player.Stall(3_000);
        player.Steps(10);

        var stats = player.Buffer.GetStats();
        Assert.Equal(SyncCorrectionMode.Resampling, stats.CurrentCorrectionMode);
        Assert.True(stats.TargetPlaybackRate > 1.0, $"expected speed-up, got {stats.TargetPlaybackRate}");
        Assert.InRange(
            Math.Abs(stats.TargetPlaybackRate - 1.0),
            0,
            SyncCorrectionOptions.SpecMaxSpeedCorrection);
        Assert.Equal(0, stats.HardSyncCount);
        Assert.Equal(0, stats.SamplesDroppedForSync);
    }

    [Fact]
    public void ErrorAboveHardSyncThreshold_SnapsOnceInsteadOfGrinding()
    {
        using var player = new Player().Settled();

        // 10 ms behind. At the spec's ±0.5% cap a continuous correction needs two full
        // seconds to close this; the snap closes it in one callback and the spec exempts it.
        player.Stall(10_000);

        // The EMA has to climb past the 5 ms trigger before the snap is scheduled — about
        // seven callbacks at alpha 0.1. That detection latency is the noise filter doing its
        // job; the correction itself is still one step rather than the two seconds a capped
        // rate correction would need.
        player.Steps(20);

        var stats = player.Buffer.GetStats();
        Assert.Equal(1, stats.HardSyncCount);
        Assert.InRange(Ms(stats.SamplesDroppedForSync), 9, 11);
        Assert.InRange(Math.Abs(stats.SyncErrorMs), 0, 1);
        Assert.Equal(0, stats.SamplesInsertedForSync);

        // And it settles there rather than hunting.
        player.Steps(100);
        Assert.Equal(1, player.Buffer.GetStats().HardSyncCount);
    }

    [Fact]
    public void PlayingAhead_SnapsByInsertingSilence()
    {
        using var player = new Player().Settled();

        // Consume a callback's worth without letting the wall clock advance, twice: the
        // player is now 20 ms ahead of where it should be.
        player.Read();
        player.Read();
        player.Steps(30);

        var stats = player.Buffer.GetStats();
        Assert.Equal(1, stats.HardSyncCount);
        Assert.InRange(Ms(stats.SamplesInsertedForSync), 18, 22);
        Assert.InRange(Math.Abs(stats.SyncErrorMs), 0, 1);
    }

    [Fact]
    public void DropInsertEpisode_NeverExceedsSpecSpeedCapOverSlidingWindow()
    {
        // Route a large error through the discrete drop/insert band by collapsing the
        // resampling band and lifting the snap out of the way, then check the implied speed
        // over every sliding 150 ms window — the window the spec measures over.
        var options = new SyncCorrectionOptions
        {
            ResamplingThresholdMicroseconds = 1_000,
            HardSyncThresholdMicroseconds = 300_000,
        };
        using var player = new Player(options).Settled();

        player.Stall(100_000);

        var samples = new List<(long Net, long Output)>();
        for (var i = 0; i < 500; i++)
        {
            player.Step();
            var stats = player.Buffer.GetStats();
            samples.Add((
                stats.SamplesDroppedForSync - stats.SamplesInsertedForSync,
                stats.SamplesOutputSinceStart));
        }

        Assert.True(
            samples[^1].Net > 0,
            "the episode must actually correct, or the cap assertion proves nothing");

        var windowSamples = 150 * SamplesPerMs;
        var checkedWindows = 0;
        for (var end = 0; end < samples.Count; end++)
        {
            var start = end;
            while (start > 0 && samples[end].Output - samples[start].Output < windowSamples)
            {
                start--;
            }

            var outputDelta = samples[end].Output - samples[start].Output;
            if (outputDelta < windowSamples)
            {
                continue;
            }

            var netDelta = Math.Abs(samples[end].Net - samples[start].Net);
            var speedDeviation = netDelta / (double)outputDelta;

            // One correction may straddle a window edge; that granularity is not a speed.
            var tolerance = SyncCorrectionOptions.SpecMaxSpeedCorrection + (Channels / (double)outputDelta);
            Assert.True(
                speedDeviation <= tolerance,
                $"window ending at sample {samples[end].Output} deviated {speedDeviation:P3}, cap is {tolerance:P3}");
            checkedWindows++;
        }

        Assert.True(checkedWindows > 100, $"expected many windows to check, got {checkedWindows}");
    }

    [Fact]
    public void LostChunk_IsDetectedAndCompensatedWithSilence()
    {
        using var player = new Player().Settled();
        var before = player.Buffer.GetStats();

        // One chunk never arrives. The buffer stays full throughout — this is a hole in the
        // content, not an underrun — so every later sample would otherwise play 20 ms early
        // while the pace-based error read a contented zero.
        player.LoseChunks(1);

        var worstErrorMs = 0.0;
        for (var i = 0; i < 150; i++)
        {
            player.Step();
            worstErrorMs = Math.Max(worstErrorMs, Math.Abs(player.Buffer.GetStats().SyncErrorMs));
        }

        var stats = player.Buffer.GetStats();
        Assert.Equal(before.UnderrunCount, stats.UnderrunCount); // never ran dry
        Assert.Equal(1, stats.ContentHolesDetected);

        // Surfaced...
        Assert.InRange(worstErrorMs, 15, 25);

        // ...and closed by inserting exactly the missing duration, so the content after the
        // hole still plays at its scheduled time.
        Assert.InRange(Ms(stats.SamplesInsertedForSync), 18, 22);
        Assert.InRange(Math.Abs(stats.SyncErrorMs), 0, 1);
    }

    [Fact]
    public void LateChunkAfterPlaybackStarted_IsDropped()
    {
        using var player = new Player().Settled();
        var bufferedBefore = player.Buffer.BufferedMilliseconds;

        // A chunk whose content the read cursor passed a second ago. Enqueuing it would
        // splice already-played audio back into the timeline (spec roles/player/v1.md:145).
        player.WriteAt(ServerT0 - 1_000_000);

        var stats = player.Buffer.GetStats();
        Assert.Equal(1, stats.LateChunksDropped);
        Assert.Equal(bufferedBefore, player.Buffer.BufferedMilliseconds);
    }

    [Fact]
    public void ChunksStillDue_AreNotMistakenForLate()
    {
        using var player = new Player().Settled();
        var bufferedBefore = player.Buffer.BufferedMilliseconds;

        player.WriteAt(player.WriteServerTs);

        Assert.Equal(0, player.Buffer.GetStats().LateChunksDropped);
        Assert.True(player.Buffer.BufferedMilliseconds > bufferedBefore);
    }

    [Fact]
    public void ReadinessGate_FollowsNegotiatedMinBuffer_NotTheTargetDepth()
    {
        var clockSync = new FakeClockSynchronizer
        {
            OffsetMicroseconds = ServerT0 - LocalT0,
            IsConverged = true,
            HasMinimalSync = true,
        };
        using var buffer = new TimedAudioBuffer(Format, clockSync, bufferCapacityMs: 5_000)
        {
            TargetBufferMilliseconds = 250,
            MinBufferMilliseconds = 150,
        };

        // A live stream is scheduled only min_buffer_ms ahead, so 150 ms is all that will ever
        // be buffered before the first chunk is due. The old gate wanted 80% of 250 ms = 200 ms
        // and so could not be satisfied until the schedule was already 50 ms in the past.
        var chunk = new float[StepMs * SamplesPerMs];
        for (var i = 0; i < 14; i++)
        {
            buffer.Write(chunk, ServerT0 + (i * StepMs * 1000L));
        }

        Assert.Equal(140, buffer.BufferedMilliseconds);
        Assert.False(buffer.IsReadyForPlayback, "below the negotiated minimum, still buffering");

        buffer.Write(chunk, ServerT0 + (14 * StepMs * 1000L));

        Assert.Equal(150, buffer.BufferedMilliseconds);
        Assert.True(buffer.IsReadyForPlayback, "150ms of a 150ms live lead must be enough to start");
    }

    [Fact]
    public void LateStart_IsCorrectedRatherThanAnchoredAway()
    {
        var clockSync = new FakeClockSynchronizer
        {
            OffsetMicroseconds = ServerT0 - LocalT0,
            IsConverged = true,
            HasMinimalSync = true,
        };
        using var buffer = new TimedAudioBuffer(Format, clockSync, bufferCapacityMs: 5_000);

        // 300 ms of a live stream buffered; the first chunk was due 50 ms ago.
        var chunk = new float[ChunkMs * SamplesPerMs];
        for (var i = 0; i < 300 / ChunkMs; i++)
        {
            buffer.Write(chunk, ServerT0 + (i * ChunkMs * 1000L));
        }

        var output = new float[StepMs * SamplesPerMs];
        buffer.Read(output, LocalT0 + 50_000);

        var stats = buffer.GetStats();

        // The 50 ms must come off the front: whole stale segments skipped, then the
        // sub-segment residual snapped. Anchoring to "now" instead — which is what the old
        // code did — discarded nothing, declared the error zero, and left the player trailing
        // the group for the whole stream with clean-looking diagnostics.
        var discardedMs = Ms(stats.DroppedSamples + stats.SamplesDroppedForSync);
        Assert.InRange(discardedMs, 45, 55);
        Assert.Equal(0, stats.ContentHolesDetected);
    }

    [Fact]
    public void OnTimeStart_SnapsNothing()
    {
        using var player = new Player();
        player.Steps(20);

        var stats = player.Buffer.GetStats();
        Assert.Equal(0, stats.HardSyncCount);
        Assert.Equal(0, stats.DroppedSamples);
        Assert.Equal(0, stats.SamplesDroppedForSync);
        Assert.Equal(0, stats.SamplesInsertedForSync);
    }

    [Fact]
    public void RealisticClockDrift_IsTrackedByRateAlone_WithoutSnapping()
    {
        // The spec allows the one-shot snap only if it stays rare, so the continuous tier has
        // to comfortably outrun ordinary crystal drift. At 50 ppm the proportional correction
        // equilibrates around 150 µs — just above the dead band, two orders of magnitude below
        // the 5 ms trigger.
        using var player = new Player().Settled();

        // 50 ppm = 50 µs per second = 0.5 µs per 10 ms callback.
        for (var i = 0; i < 2_000; i++) // 20 s
        {
            player.ClockSync.OffsetMicroseconds += (i % 2 == 0) ? 1 : 0;
            player.Step();
        }

        var stats = player.Buffer.GetStats();
        Assert.Equal(0, stats.HardSyncCount);
        Assert.InRange(Math.Abs(stats.SmoothedSyncErrorMs), 0, 1_000);
        Assert.InRange(
            Math.Abs(stats.TargetPlaybackRate - 1.0),
            0,
            SyncCorrectionOptions.SpecMaxSpeedCorrection);
    }

    [Fact]
    public void HardSync_AppliesOnTheExternalCorrectionPath_Too()
    {
        // windowsSpin and every other host-resampler consumer drive ReadRaw. The snap is a
        // buffer-timeline operation, so it has to happen there as well or the flagship
        // consumer keeps the old grind.
        using var player = new Player(rawReads: true).Settled();

        player.Stall(10_000);
        player.Steps(20);

        var stats = player.Buffer.GetStats();
        Assert.Equal(1, stats.HardSyncCount);
        Assert.InRange(Ms(stats.SamplesDroppedForSync), 9, 11);
        Assert.InRange(Math.Abs(stats.SyncErrorMs), 0, 1);
    }

    [Fact]
    public void ExternalPath_DoesNotSnapBelowTheHardSyncThreshold()
    {
        // The raw path must apply the same threshold, not just the same suppression windows.
        // Snapping a sub-threshold error here would fight the external corrector, which is
        // routing that same error through the continuous tier — the double correction the
        // design exists to prevent.
        using var player = new Player(rawReads: true).Settled();

        player.Stall(3_000);
        player.Steps(50);

        var stats = player.Buffer.GetStats();
        Assert.Equal(0, stats.HardSyncCount);
        Assert.Equal(0, stats.SamplesDroppedForSync);
        Assert.Equal(0, stats.SamplesInsertedForSync);
    }

    [Fact]
    public void ExternalPath_DoesNotSnapWithinTheDeadband()
    {
        using var player = new Player(rawReads: true).Settled();

        player.Stall(50);
        player.Steps(50);

        var stats = player.Buffer.GetStats();
        Assert.Equal(0, stats.HardSyncCount);
        Assert.Equal(0, stats.SamplesDroppedForSync);
        Assert.Equal(0, stats.SamplesInsertedForSync);
    }

    [Fact]
    public void HardSyncThresholdOfZero_DisablesTheTierOnBothPaths()
    {
        foreach (var rawReads in new[] { false, true })
        {
            var options = new SyncCorrectionOptions { HardSyncThresholdMicroseconds = 0 };
            using var player = new Player(options, rawReads).Settled();

            player.Stall(50_000);
            player.Steps(50);

            Assert.Equal(0, player.Buffer.GetStats().HardSyncCount);
        }
    }

    [Fact]
    public void InternalAndExternalCorrectors_AgreeOnTheSameError()
    {
        // AUD-9: the decision ladder lived in two places and drifted. Both now route through
        // SyncCorrectionPolicy, and this pins that a real buffer error and a calculator fed
        // that same error reach the identical correction.
        using var player = new Player().Settled();

        var calculator = new SyncCorrectionCalculator(SyncCorrectionOptions.Default, SampleRate, Channels);
        calculator.NotifySamplesProcessed(SampleRate * Channels); // past its startup grace

        player.Stall(3_000);

        for (var i = 0; i < 20; i++)
        {
            player.Step();

            var stats = player.Buffer.GetStats();
            calculator.UpdateFromSyncError(
                player.Buffer.SyncErrorMicroseconds,
                player.Buffer.SmoothedSyncErrorMicroseconds);

            Assert.Equal(stats.CurrentCorrectionMode, calculator.CurrentMode);

            // The buffer only re-applies a rate once it moves by more than 0.0001, so its
            // stored value can trail the decision by that much; the decision itself is the
            // same function call.
            Assert.True(
                Math.Abs(stats.TargetPlaybackRate - calculator.TargetPlaybackRate) <= 0.0001,
                $"step {i}: buffer {stats.TargetPlaybackRate} vs calculator {calculator.TargetPlaybackRate}");
        }
    }
}
