using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;

namespace Sendspin.SDK.Tests.Audio;

/// <summary>
/// Issue #63 drift half: the sync error must track post-anchor movement of the Kalman
/// clock offset (TrackClockDrift, default on). Without it, relative crystal drift
/// accumulates as absolute misalignment that no counter or correction ever sees.
/// Offset convention (Task 1): offset = server - client; scheduled client time for
/// server position P is P - offset, so a RISING offset moves the schedule earlier,
/// the player is LATE, and the sync error must go POSITIVE (positive = drop/speed up).
/// </summary>
public class TimedAudioBufferClockDriftTests
{
    private const int SamplesPerMs = 96; // 48kHz stereo interleaved
    private const int StepMs = 10;
    private const int StepSamples = StepMs * SamplesPerMs;
    private const long ServerT0 = 1_000_000;
    private const long LocalT0 = 9_000_000_000_000;

    private static readonly AudioFormat Format = new()
    {
        Codec = "pcm",
        SampleRate = 48_000,
        Channels = 2,
    };

    private sealed class Session : IDisposable
    {
        public FakeClockSynchronizer ClockSync { get; } = new();

        public TimedAudioBuffer Buffer { get; }

        public long WallNow { get; private set; } = LocalT0;

        public long WriteServerTs { get; private set; } = ServerT0;

        private readonly float[] _chunk = new float[StepSamples];
        private readonly float[] _readBuf = new float[StepSamples];
        private readonly bool _useRawReads;

        public Session(SyncCorrectionOptions? options, bool useRawReads)
        {
            _useRawReads = useRawReads;
            Buffer = new TimedAudioBuffer(Format, ClockSync, bufferCapacityMs: 5000, options);
            Array.Fill(_chunk, 0.25f);

            // Converged clock scheduling the first chunk right now.
            ClockSync.OffsetMicroseconds = ServerT0 - LocalT0;
            ClockSync.IsConverged = true;
            ClockSync.HasMinimalSync = true;

            // ~2s producer pre-roll (server transmit-ahead).
            for (var i = 0; i < 200; i++)
            {
                WriteChunk();
            }
        }

        public void WriteChunk()
        {
            Buffer.Write(_chunk, WriteServerTs);
            WriteServerTs += StepMs * 1000L;
        }

        /// <summary>Advances wall time one 10ms step, keeps the producer ahead, reads one step.</summary>
        public void Step()
        {
            WallNow += StepMs * 1000L;
            WriteChunk();
            if (_useRawReads)
            {
                Buffer.ReadRaw(_readBuf, WallNow);
            }
            else
            {
                Buffer.Read(_readBuf, WallNow);
            }
        }

        public void Steps(int count)
        {
            for (var i = 0; i < count; i++)
            {
                Step();
            }
        }

        /// <summary>
        /// True misalignment of the read cursor vs the schedule, in microseconds
        /// (positive = playing late). Cursor server position = write head minus
        /// buffered depth; it SHOULD play at ServerToClientTime(position).
        /// </summary>
        public long TrueMisalignmentUs()
        {
            var cursorServerPos = WriteServerTs - (long)(Buffer.BufferedMilliseconds * 1000);
            return WallNow - ClockSync.ServerToClientTime(cursorServerPos);
        }

        /// <summary>Slews the Kalman offset by <paramref name="totalUs"/> evenly across steps.</summary>
        public void SlewOffset(long totalUs, int steps)
        {
            for (var i = 0; i < steps; i++)
            {
                ClockSync.OffsetMicroseconds += totalUs / steps;
                Step();
            }
        }

        public void Dispose() => Buffer.Dispose();
    }

    [Fact]
    public void OffsetSlew_SurfacesInSyncError_OnRawPath()
    {
        // ReadRaw applies no corrections itself (external correctors do), so the
        // error must accumulate to roughly the slewed amount.
        using var session = new Session(options: null, useRawReads: true);
        session.Steps(300); // 3s: anchor + startup grace + baseline capture settle

        session.SlewOffset(totalUs: 200_000, steps: 600); // +200ms over 6s

        Assert.InRange(session.Buffer.SmoothedSyncErrorMicroseconds, 150_000, 250_000);
    }

    [Fact]
    public void OffsetSlew_InternalReadPath_CatchesUp()
    {
        // Internal Read applies drop/insert above ResamplingThreshold; with a tight
        // threshold the loop closes and TRUE misalignment stays bounded near the
        // threshold instead of reaching the slewed 200ms.
        var options = new SyncCorrectionOptions { ResamplingThresholdMicroseconds = 5_000 };
        using var session = new Session(options, useRawReads: false);
        session.Steps(300);

        session.SlewOffset(totalUs: 200_000, steps: 600);
        session.Steps(600); // 6s settle after slew ends

        var stats = session.Buffer.GetStats();
        Assert.True(stats.SamplesDroppedForSync > 0, "drift should trigger catch-up drops");
        Assert.InRange(session.TrueMisalignmentUs(), -20_000, 20_000);
    }

    [Fact]
    public void OffsetSlew_FlagOff_PinsPreDriftBehavior()
    {
        var options = new SyncCorrectionOptions { TrackClockDrift = false };
        using var session = new Session(options, useRawReads: true);
        session.Steps(300);

        session.SlewOffset(totalUs: 200_000, steps: 600);

        // Pre-9.2 behavior: the pace servo is blind to the slew...
        Assert.InRange(session.Buffer.SmoothedSyncErrorMicroseconds, -5_000, 5_000);
        // ...while true misalignment reaches the full slew.
        Assert.InRange(session.TrueMisalignmentUs(), 150_000, 250_000);
    }
}
