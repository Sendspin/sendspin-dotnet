using Microsoft.Extensions.Logging;
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

        public Player(
            SyncCorrectionOptions? options = null,
            bool rawReads = false,
            ILogger<TimedAudioBuffer>? logger = null)
        {
            _rawReads = rawReads;
            Buffer = new TimedAudioBuffer(Format, ClockSync, bufferCapacityMs: 5_000, options, logger);
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

        /// <summary>
        /// Server timestamp the read cursor currently sits on, derived from the producer's
        /// write head and the buffer depth so a test can place a chunk a known distance
        /// behind it.
        /// </summary>
        public long CursorServerTimestamp =>
            WriteServerTs - (long)(Buffer.BufferedMilliseconds * 1000);

        /// <summary>Runs past the 500 ms startup grace so the baseline is captured and error ~0.</summary>
        public Player Settled()
        {
            Steps(100);
            return this;
        }

        /// <summary>
        /// True misalignment of the read cursor against the schedule, in microseconds
        /// (positive = playing late). Derived from the producer's write head and the buffer
        /// depth, so it is independent of whatever the buffer believes its error to be — which
        /// is the point: the failures this guards against are the ones where the reported error
        /// reads zero while the audio is demonstrably shifted.
        /// </summary>
        public long TrueMisalignmentUs()
        {
            var cursorServerPos = WriteServerTs - (long)(Buffer.BufferedMilliseconds * 1000);
            return WallNow - ClockSync.ServerToClientTime(cursorServerPos);
        }

        public void Dispose() => Buffer.Dispose();
    }

    private static double Ms(long samples) => samples / (double)SamplesPerMs;

    /// <summary>
    /// Asserts that over every sliding 150 ms window — the window the spec measures over — the
    /// speed implied by the corrections applied stays within the spec's cap.
    /// </summary>
    /// <param name="samples">Per-callback (net corrected samples, total output samples).</param>
    private static void AssertSpeedCapRespectedOverEveryWindow(List<(long Net, long Output)> samples)
    {
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

    /// <summary>Captures warnings so a test can assert the SDK said something.</summary>
    private sealed class CapturingLogger : ILogger<TimedAudioBuffer>
    {
        public List<string> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

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
    public void ErrorAboveDeadbandBelowHardSync_CorrectsBySteppingWithinSpecCap()
    {
        using var player = new Player().Settled();

        // 3 ms: past the dead band, inside the hard-sync threshold.
        player.Stall(3_000);
        player.Steps(200);

        var stats = player.Buffer.GetStats();

        // This test used to assert a Resampling decision and a rate above 1.0. On the plain
        // Read path nothing applies a rate — the buffer has no resampler — so that correction
        // was advisory and the error simply accumulated until the hard-sync tier spliced it.
        // The band is now realized as frame stepping, the spec's own suggested strategy, and
        // the rate stays at 1.0 because nobody is being asked to resample.
        Assert.Equal(SyncCorrectionMode.Dropping, stats.CurrentCorrectionMode);
        Assert.Equal(1.0, stats.TargetPlaybackRate, 6);
        Assert.Equal(0, stats.HardSyncCount);

        // It actually corrects, and stays inside the cap while doing so.
        Assert.True(stats.SamplesDroppedForSync > 0, "the band must self-correct, not just advise");
        var impliedSpeed = stats.SamplesDroppedForSync / (double)stats.SamplesOutputSinceStart;
        Assert.InRange(impliedSpeed, 0, SyncCorrectionOptions.SpecMaxSpeedCorrection);
        Assert.InRange(Math.Abs(stats.SyncErrorMs), 0, 3);
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

        AssertSpeedCapRespectedOverEveryWindow(samples);
    }

    [Fact]
    public void OverPermissiveSpeedCap_IsClampedWhereItIsApplied_AndWarnedAboutOnce()
    {
        // windowsSpin's shipped configuration default is 2%, written before the cap was
        // enforced. Such a client must still start — and must still correct at 0.5%.
        var logger = new CapturingLogger();
        var options = new SyncCorrectionOptions
        {
            MaxSpeedCorrection = 0.02,
            ResamplingThresholdMicroseconds = 1_000,
            HardSyncThresholdMicroseconds = 300_000,
        };

        using var player = new Player(options, logger: logger).Settled();

        Assert.Single(logger.Warnings.Where(w => w.Contains("MaxSpeedCorrection", StringComparison.Ordinal)));

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

        Assert.True(samples[^1].Net > 0, "the episode must actually correct");
        AssertSpeedCapRespectedOverEveryWindow(samples);
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
    public void LateChunkAdmission_IsNotLoosenedByRaisingTheSnapThreshold()
    {
        // Admission is a write-side spec rule (roles/player/v1.md:145) and the snap threshold
        // is a read-side correction size. While admission borrowed the snap knob, a client
        // that tuned the snap silently changed which chunks it accepted.
        var options = new SyncCorrectionOptions { HardSyncThresholdMicroseconds = 300_000 };
        using var player = new Player(options).Settled();
        var bufferedBefore = player.Buffer.BufferedMilliseconds;

        // 100 ms behind the cursor: past the spec tolerance, but far inside the raised snap
        // threshold that used to double as the admission window.
        player.WriteAt(player.CursorServerTimestamp - 100_000);

        Assert.Equal(1, player.Buffer.GetStats().LateChunksDropped);
        Assert.Equal(bufferedBefore, player.Buffer.BufferedMilliseconds);
    }

    [Fact]
    public void LateChunkAdmission_SurvivesDisablingTheSnapTier()
    {
        // HardSyncThresholdMicroseconds = 0 disables the snap. It used to collapse admission
        // to the 1 ms segment-rounding tolerance too, so turning the snap off started dropping
        // chunks that are still perfectly playable.
        var options = new SyncCorrectionOptions { HardSyncThresholdMicroseconds = 0 };
        using var player = new Player(options).Settled();
        var bufferedBefore = player.Buffer.BufferedMilliseconds;

        // 2 ms behind the cursor: inside the default 5 ms tolerance, so it must be enqueued.
        player.WriteAt(player.CursorServerTimestamp - 2_000);

        Assert.Equal(0, player.Buffer.GetStats().LateChunksDropped);
        Assert.True(player.Buffer.BufferedMilliseconds > bufferedBefore);
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
    public void StartupSnap_CountsAsAHardSync()
    {
        // The startup alignment performs the same one-shot splice the hard-sync tier does, and
        // the spec requires those to be rare (roles/player/v1.md:140). GetStats is how a
        // deployment checks that, and a startup snap used to be invisible there: HardSyncCount
        // read 0 while a snap had just moved the audio.
        var clockSync = new FakeClockSynchronizer
        {
            OffsetMicroseconds = ServerT0 - LocalT0,
            IsConverged = true,
            HasMinimalSync = true,
        };
        using var buffer = new TimedAudioBuffer(Format, clockSync, bufferCapacityMs: 5_000);

        var chunk = new float[ChunkMs * SamplesPerMs];
        for (var i = 0; i < 300 / ChunkMs; i++)
        {
            buffer.Write(chunk, ServerT0 + (i * ChunkMs * 1000L));
        }

        // 5 ms late: inside the scheduled-start grace window, so nothing is discarded as stale
        // and the residual is closed by the startup snap alone.
        var output = new float[StepMs * SamplesPerMs];
        buffer.Read(output, LocalT0 + 5_000);

        var stats = buffer.GetStats();
        Assert.Equal(1, stats.HardSyncCount);
        Assert.Equal(0, stats.DroppedSamples);
        Assert.InRange(Ms(stats.SamplesDroppedForSync), 4, 6);
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

        // 50 ppm = 50 µs per second = 0.5 µs per 10 ms callback. Run two minutes: long enough
        // that a correction the buffer merely ADVISES — rather than applies — would have let
        // the error walk up to the 5 ms trigger and splice.
        for (var i = 0; i < 12_000; i++) // 120 s
        {
            player.ClockSync.OffsetMicroseconds += (i % 2 == 0) ? 1 : 0;
            player.Step();
        }

        var stats = player.Buffer.GetStats();
        Assert.Equal(0, stats.HardSyncCount);

        // SmoothedSyncErrorMs is milliseconds; the spec's steady-state MUST is ±1 ms.
        Assert.InRange(Math.Abs(stats.SmoothedSyncErrorMs), 0, 1.0);
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
    public void MidSegmentResetSyncTracking_PreservesAlignment()
    {
        // Every output-device switch and static-delay change goes through ResetSyncTracking,
        // which keeps the buffered audio and re-anchors on the next callback. When the head
        // segment is only half consumed, anchoring to the segment's START rather than to the
        // read cursor makes the schedule look one prefix too early, and the startup alignment
        // "corrects" a discrepancy that does not exist — shifting the audio permanently while
        // the reported error settles back to zero.
        using var player = new Player().Settled();

        // 101 callbacks of 10 ms against 20 ms chunks leaves the head half consumed.
        var alignmentBefore = player.TrueMisalignmentUs();
        var droppedBefore = player.Buffer.GetStats().SamplesDroppedForSync;

        player.Buffer.ResetSyncTracking();
        player.Steps(50);

        var stats = player.Buffer.GetStats();
        Assert.Equal(droppedBefore, stats.SamplesDroppedForSync);
        Assert.InRange(player.TrueMisalignmentUs() - alignmentBefore, -2_000, 2_000);
    }

    [Fact]
    public void SkipStaleAudio_TrimsOnlyTheStalePrefixOfTheHeadSegment()
    {
        var clockSync = new FakeClockSynchronizer
        {
            OffsetMicroseconds = ServerT0 - LocalT0,
            IsConverged = true,
            HasMinimalSync = true,
        };
        using var buffer = new TimedAudioBuffer(Format, clockSync, bufferCapacityMs: 5_000);

        var chunk = new float[ChunkMs * SamplesPerMs];
        for (var i = 0; i < 10; i++)
        {
            buffer.Write(chunk, ServerT0 + (i * ChunkMs * 1000L));
        }

        // Start 15 ms late: the head chunk's first 15 ms are past, but its last 5 ms are still
        // due. Discarding the whole chunk throws away audio that had not played yet.
        var output = new float[StepMs * SamplesPerMs];
        buffer.Read(output, LocalT0 + 15_000);

        var discardedMs = Ms(buffer.GetStats().DroppedSamples + buffer.GetStats().SamplesDroppedForSync);
        Assert.InRange(discardedMs, 13, 17);
    }

    [Fact]
    public void CatastrophicallyLateStart_ReanchorsInsteadOfPlayingLate()
    {
        var clockSync = new FakeClockSynchronizer
        {
            OffsetMicroseconds = ServerT0 - LocalT0,
            IsConverged = true,
            HasMinimalSync = true,
        };
        using var buffer = new TimedAudioBuffer(Format, clockSync, bufferCapacityMs: 5_000);

        // Every buffered chunk is more than the re-anchor threshold stale, so SkipStaleAudio
        // runs out of segments to discard and the residual stays catastrophic. Deferring to
        // "the re-anchor tier" is only a fix if something actually re-anchors: the startup
        // baseline used to zero the error before the re-anchor check ever ran.
        var chunk = new float[ChunkMs * SamplesPerMs];
        for (var i = 0; i < 5; i++)
        {
            buffer.Write(chunk, ServerT0 + (i * ChunkMs * 1000L));
        }

        var reanchored = 0;
        buffer.ReanchorRequired += (_, _) => Interlocked.Increment(ref reanchored);

        var output = new float[StepMs * SamplesPerMs];
        buffer.Read(output, LocalT0 + 700_000); // 700 ms past the first chunk's schedule

        Assert.True(
            buffer.GetStats().ReanchorCount > 0,
            "a start further late than the re-anchor threshold must re-anchor, not play late");
    }

    [Fact]
    public void RawErrorFarBeyondSmoothed_DoesNotSpliceMoreThanTheReanchorThreshold()
    {
        // A clock step lands the raw error past the re-anchor ceiling while the EMA is still
        // inside the hard-sync band, so the tier fires but the raw figure is far too large to
        // splice: doing it in one go performs exactly the surgery the policy reserves for
        // clearing the buffer.
        //
        // Getting there needs the EMA primed first. From a settled buffer the smoothed error
        // is exactly zero, and CalculateSyncError's reseed branch then jumps it straight to
        // the raw value — past the band entirely, so the tier never fires and the guard is
        // never reached. A small stall first puts the EMA at a nonzero in-band value, so the
        // big stall moves raw past the ceiling while smoothed lags inside the band.
        using var player = new Player().Settled();

        player.Stall(3_000);
        player.Step(); // EMA reseeds to ~3 ms — below the hard-sync threshold, so no snap

        var droppedBefore = player.Buffer.GetStats().SamplesDroppedForSync;
        var reanchorsBefore = player.Buffer.GetStats().ReanchorCount;

        player.Stall(600_000);
        player.Steps(30); // let any scheduled snap drain fully

        var stats = player.Buffer.GetStats();
        var spliced = Ms(stats.SamplesDroppedForSync - droppedBefore);

        Assert.True(
            spliced <= 500,
            $"spliced {spliced:F1}ms for one snap, above the 500ms re-anchor ceiling");

        // ...and the error goes where it belongs instead.
        Assert.True(
            stats.ReanchorCount > reanchorsBefore,
            "an error past the ceiling belongs to the re-anchor tier");
    }

    [Fact]
    public void ErrorPastTheReanchorThreshold_IsNotAbsorbedByTheStartupBaseline()
    {
        // The startup baseline exists to swallow a constant plumbing offset — an output
        // backend's prefill. It used to swallow anything, including misalignment far past the
        // re-anchor threshold, and it runs at the end of the grace window, before the
        // re-anchor check is allowed to look. So a start that was catastrophically late (or,
        // as here, a stall inside the grace window) came out the other side reporting zero
        // error while being most of a second out, with nothing left to trigger a recovery.
        using var player = new Player();

        player.Steps(20);          // inside the 500 ms startup grace
        player.Stall(700_000);
        player.Steps(60);          // past grace end, where the baseline is captured

        var stats = player.Buffer.GetStats();

        Assert.True(
            Math.Abs(stats.SyncErrorMs) > 400,
            $"a {700}ms error must survive the baseline capture, but reads {stats.SyncErrorMs:F1}ms");
        Assert.True(
            stats.ReanchorCount > 0,
            "and must reach the re-anchor tier that owns it");
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

        var options = SyncCorrectionOptions.Default;

        for (var i = 0; i < 20; i++)
        {
            player.Step();

            var smoothed = player.Buffer.SmoothedSyncErrorMicroseconds;
            calculator.UpdateFromSyncError(player.Buffer.SyncErrorMicroseconds, smoothed);

            // One decision, one currency. The calculator reports it verbatim.
            var decision = SyncCorrectionPolicy.Decide(smoothed, options);
            Assert.Equal(decision.Mode, calculator.CurrentMode);
            Assert.Equal(decision.TargetPlaybackRate, calculator.TargetPlaybackRate, 12);

            // The buffer's own read path has no resampler, so it spends that same rate as
            // whole-frame stepping and reports the mechanism it used.
            var (dropEveryN, insertEveryN) =
                SyncCorrectionPolicy.SteppingIntervalFrames(decision.TargetPlaybackRate, options, Channels);
            var expectedMode = dropEveryN > 0
                ? SyncCorrectionMode.Dropping
                : insertEveryN > 0 ? SyncCorrectionMode.Inserting : decision.Mode;

            Assert.Equal(expectedMode, player.Buffer.GetStats().CurrentCorrectionMode);

            // ...and the two realizations must converge at the same speed, or a group of mixed
            // players would drift apart during recovery, which is precisely what the speed cap
            // exists to prevent.
            if (dropEveryN + insertEveryN > 0)
            {
                var impliedByStepping = 1.0 / (dropEveryN + insertEveryN);
                var impliedByRate = Math.Abs(decision.TargetPlaybackRate - 1.0);

                Assert.True(
                    Math.Abs(impliedByStepping - impliedByRate) <= 1e-4,
                    $"step {i}: stepping implies {impliedByStepping:E3}, rate implies {impliedByRate:E3}");
                Assert.True(impliedByStepping <= SyncCorrectionOptions.SpecMaxSpeedCorrection);
            }
        }
    }

    [Fact]
    public void NotifyExternalCorrection_ReportsCorrectionsWithoutMovingTheReadCursor()
    {
        // ReadRaw credits every sample it hands over to the read cursor, and an external
        // corrector must size its read to the correction — reading a fixed block instead either
        // strands content or leaves the block short by exactly the corrections applied. So by the
        // time it reports, the consumption is already counted; adjusting again for the same
        // frames makes the error metric converge at twice the physical correction. The reported
        // error then reads ~0 while the player is still half the drift out of the group.
        using var player = new Player(rawReads: true).Settled();

        var before = player.Buffer.GetStats();

        player.Buffer.NotifyExternalCorrection(samplesDropped: 4 * Channels, samplesInserted: 0);
        player.Buffer.NotifyExternalCorrection(samplesDropped: 0, samplesInserted: 3 * Channels);

        var after = player.Buffer.GetStats();

        Assert.Equal(before.SamplesReadSinceStart, after.SamplesReadSinceStart);
        Assert.Equal(before.SamplesDroppedForSync + (4 * Channels), after.SamplesDroppedForSync);
        Assert.Equal(before.SamplesInsertedForSync + (3 * Channels), after.SamplesInsertedForSync);
    }
}
