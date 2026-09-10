using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;
using Xunit.Abstractions;

namespace Sendspin.SDK.Tests.Audio;

/// <summary>
/// windowsSpin #63: a Focusrite Scarlett 2i4 (1st gen) through NAudio's WASAPI push mode. The
/// host sleeps half its 100 ms buffer, asks the driver how much has played, and refills the
/// difference — and this driver reports playback progress in whole 512-frame periods (10.67 ms
/// at 48 kHz). Two things followed in the reporter's logs, both the SDK's to answer:
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>The one-shot snap tier limit-cycled: 202 hard syncs in an hour, 134 of them exactly one
/// device period, 180 of 201 alternating in sign, half a second apart. Nothing was out of sync;
/// the snap tier was chasing the granularity of the driver's progress report.</item>
/// <item>After a re-anchor storm, playback restarted 527 ms late inside the re-anchor cooldown.
/// The startup alignment refused it as catastrophic, the re-anchor tier was cooling down, and at
/// the end of the startup grace the baseline capture absorbed 477 ms as a "constant offset that
/// will not be corrected". The stream then played half a second late with a reported error of
/// zero for its whole duration.</item>
/// </list>
/// The device model here is deliberately literal: a 4 800-frame device buffer, 50 ms wakes with
/// 0-15 ms of timer jitter, and progress reported in configurable period steps.
/// </remarks>
public class TimedAudioBufferPushModeDeviceTests
{
    private readonly ITestOutputHelper _output;

    public TimedAudioBufferPushModeDeviceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int SamplesPerMs = SampleRate * Channels / 1000; // 96
    private const int ChunkMs = 20;
    private const long ServerT0 = 1_000_000;
    private const long LocalT0 = 9_000_000_000_000;

    /// <summary>NAudio WasapiOut with the host's 100 ms request.</summary>
    private const int DeviceBufferFrames = 4_800;

    /// <summary>NAudio push mode sleeps half the requested latency between refills.</summary>
    private const int WakeMs = 50;

    /// <summary>The Focusrite control panel's buffer size: progress is reported per period.</summary>
    private const int FocusritePeriodFrames = 512;

    /// <summary>A driver that reports progress to the frame — every built-in codec does.</summary>
    private const int FinePeriodFrames = 1;

    private static readonly AudioFormat Format = new()
    {
        Codec = "pcm", SampleRate = SampleRate, Channels = Channels,
    };

    /// <summary>
    /// The issue's shape: a device that is otherwise perfectly on rate, one genuine 20 ms
    /// disturbance, and a driver that reports progress in 512-frame steps. One snap closes the
    /// disturbance; anything after that is the snap tier reacting to the report granularity.
    /// </summary>
    [Fact]
    public void CoarseProgressReports_DoNotKeepTheSnapTierOscillating()
    {
        using var player = new PushModePlayer(FocusritePeriodFrames);
        player.Run(2_000);
        var before = player.HardSyncs.Count;

        player.DeviceStall(20_000);
        player.Run(30_000);

        AssertSnapTierSettled(player, before);
    }

    /// <summary>
    /// The same run against a driver that reports to the frame. This is what every other
    /// windowsSpin user has, and why nobody else reported it; it pins that the fix for the
    /// coarse case does not come from disabling the snap tier.
    /// </summary>
    [Fact]
    public void FineProgressReports_SnapOnceForAGenuineDisturbance()
    {
        using var player = new PushModePlayer(FinePeriodFrames);
        player.Run(2_000);
        var before = player.HardSyncs.Count;

        player.DeviceStall(20_000);
        player.Run(30_000);

        AssertSnapTierSettled(player, before);
    }

    /// <summary>
    /// A restart that lands 527 ms late while the re-anchor tier is in its cooldown must not be
    /// absorbed into the startup baseline as a constant offset. Either the error stays visible,
    /// or a re-anchor follows once the cooldown lapses. What must not happen is the reporter's
    /// outcome: half a second late, reported error zero, for the rest of the stream.
    /// </summary>
    [Fact]
    public void LateRestartInsideReanchorCooldown_IsNotAbsorbedAsAConstantOffset()
    {
        using var player = new PushModePlayer(FinePeriodFrames);
        player.Run(2_000);

        // A stall longer than the re-anchor threshold: the buffer asks for a re-anchor and the
        // pipeline clears it, exactly as AudioPipeline.OnReanchorRequired does.
        player.DeviceStall(600_000);
        player.Run(200);
        Assert.Equal(1, player.Reanchors);

        // The stream resumes 527 ms behind schedule with a single chunk buffered — the shape the
        // log shows: "timeUntilStart=-527.0ms, buffered=96ms, segments=1".
        player.RestartLate(latenessMicroseconds: 527_000, firstChunkMs: 96);

        // Through the startup grace and on until the re-anchor cooldown has lapsed.
        player.Run(6_500);

        // The outcome that matters is at the device: the lateness has been closed, by the snap
        // tier once the grace ends or by a re-anchor once the cooldown lapses. What must not
        // happen is the reporter's: half a second late, reported error zero, for the rest of the
        // stream.
        var trueLateness = player.TrueLatenessUs();
        var reported = player.Buffer.SyncErrorMicroseconds;
        Assert.True(
            Math.Abs(trueLateness) < 50_000,
            $"Playback is {trueLateness / 1000.0:F0}ms late at the device, the buffer reports " +
            $"{reported / 1000.0:F1}ms, and {player.Reanchors - 1} re-anchor(s) followed the late " +
            "restart. The lateness was absorbed into the baseline as a constant offset.");
    }

    private void AssertSnapTierSettled(PushModePlayer player, int hardSyncsBefore)
    {
        var after = player.HardSyncs.Skip(hardSyncsBefore).ToList();
        _output.WriteLine(
            $"hard syncs before disturbance: {hardSyncsBefore}, after: {after.Count}, underruns: {player.Underruns}, " +
            $"final reported error {player.Buffer.SyncErrorMicroseconds / 1000.0:F1}ms, true lateness {player.TrueLatenessUs() / 1000.0:F1}ms");
        foreach (var snap in after.Take(30))
        {
            _output.WriteLine($"  snap at +{(snap.At - LocalT0) / 1000.0:F0}ms: {snap.Frames / (SampleRate / 1000.0):+0.00;-0.00}ms");
        }
        var flips = 0;
        for (var i = 1; i < after.Count; i++)
        {
            if (Math.Sign(after[i].Frames) != Math.Sign(after[i - 1].Frames))
            {
                flips++;
            }
        }

        var amounts = string.Join(", ", after.Take(12).Select(s => $"{s.Frames / (SampleRate / 1000.0):+0.0;-0.0}"));
        var period = FocusritePeriodFrames / (SampleRate / 1000.0);

        // A 20 ms disturbance is one snap. Allow a second for the EMA re-seed to settle. Beyond
        // that the tier is chasing something that is not misalignment.
        Assert.True(
            after.Count <= 2,
            $"{after.Count} hard syncs in the 30 s after one 20 ms disturbance, {flips} sign flips, " +
            $"first amounts (ms): {amounts}. One device period is {period:F2}ms. " +
            $"Underruns: {player.Underruns}, hard syncs before: {hardSyncsBefore}.");
    }

    /// <summary>
    /// NAudio's WasapiOut push loop against a modelled device: sleep half the latency, ask the
    /// driver for its padding, refill the difference. The device consumes at exactly the nominal
    /// rate on the wall clock; only its progress REPORT is quantised.
    /// </summary>
    private sealed class PushModePlayer : IDisposable
    {
        private readonly float[] _chunk = new float[ChunkMs * SamplesPerMs];
        private readonly float[] _fill = new float[DeviceBufferFrames * Channels];
        private readonly int _periodFrames;
        private double _truePaddingFrames;
        private long _lastDropped;
        private long _lastInserted;
        private uint _rng = 0x9E3779B9;

        public FakeClockSynchronizer ClockSync { get; } = new();

        public TimedAudioBuffer Buffer { get; }

        public long WallNow { get; private set; } = LocalT0;

        public long WriteServerTs { get; private set; } = ServerT0;

        public long LeadMicroseconds { get; set; } = 500_000;

        public int Underruns { get; private set; }

        public int Reanchors { get; private set; }

        /// <summary>Every snap the buffer scheduled: wall time and signed frames (positive = skipped).</summary>
        public List<(long At, long Frames)> HardSyncs { get; } = new();

        public PushModePlayer(int periodFrames)
        {
            _periodFrames = periodFrames;
            Buffer = new TimedAudioBuffer(Format, ClockSync, bufferCapacityMs: 5_000);
            Array.Fill(_chunk, 0.25f);

            ClockSync.OffsetMicroseconds = ServerT0 - LocalT0;
            ClockSync.IsConverged = true;
            ClockSync.HasMinimalSync = true;
            PumpProducer();

            // NAudio fills the whole device buffer once before starting the device.
            Refill(DeviceBufferFrames);
        }

        private long ServerNow => WallNow + ClockSync.OffsetMicroseconds;

        /// <summary>Keeps the server the configured lead ahead, contiguous timestamps.</summary>
        public void PumpProducer()
        {
            while (WriteServerTs < ServerNow + LeadMicroseconds)
            {
                Buffer.Write(_chunk, WriteServerTs);
                WriteServerTs += ChunkMs * 1000L;
            }
        }

        /// <summary>Runs the push loop for <paramref name="milliseconds"/> of wall time.</summary>
        public void Run(int milliseconds)
        {
            var until = WallNow + (milliseconds * 1000L);
            while (WallNow < until)
            {
                Wake();
            }
        }

        /// <summary>One NAudio loop iteration: sleep, consume, ask for padding, refill.</summary>
        private void Wake()
        {
            var sleepUs = (WakeMs * 1000L) + NextJitterUs();
            Advance(sleepUs, deviceRunning: true);
            PumpProducer();

            var reported = Math.Min(
                DeviceBufferFrames,
                (long)Math.Ceiling(_truePaddingFrames / _periodFrames) * _periodFrames);
            var available = (int)(DeviceBufferFrames - reported);
            if (available > 10)
            {
                Refill(available);
            }
        }

        /// <summary>The device stops consuming for a while; the wall clock does not. Content is now late.</summary>
        public void DeviceStall(long microseconds) => Advance(microseconds, deviceRunning: false);

        /// <summary>
        /// After a clear: a single <paramref name="firstChunkMs"/> chunk arrives whose schedule is
        /// already <paramref name="latenessMicroseconds"/> in the past, and the stream continues
        /// from there — a source that itself fell behind and is now delivering just in time.
        /// </summary>
        public void RestartLate(long latenessMicroseconds, int firstChunkMs)
        {
            WriteServerTs = ServerNow - latenessMicroseconds;
            var first = new float[firstChunkMs * SamplesPerMs];
            Array.Fill(first, 0.25f);
            Buffer.Write(first, WriteServerTs);
            WriteServerTs += firstChunkMs * 1000L;

            // The first read after the clear starts playback from that chunk before anything
            // else has arrived: this is the "segments=1" start.
            Advance(WakeMs * 1000L, deviceRunning: true);
            Refill(DeviceBufferFrames - (int)Math.Ceiling(_truePaddingFrames));
            LeadMicroseconds = 200_000;
        }

        /// <summary>
        /// Lateness of the audio at the device against its schedule, in microseconds (positive =
        /// late). The sample at the read cursor plays after everything still queued in the device.
        /// Independent of what the buffer believes, which is the point.
        /// </summary>
        public long TrueLatenessUs()
        {
            var cursorServerPos = WriteServerTs - (long)(Buffer.BufferedMilliseconds * 1000);
            var playsAt = WallNow + (long)(_truePaddingFrames * 1_000_000.0 / SampleRate);
            return playsAt - ClockSync.ServerToClientTime(cursorServerPos);
        }

        private void Advance(long microseconds, bool deviceRunning)
        {
            WallNow += microseconds;
            if (!deviceRunning)
            {
                return;
            }

            _truePaddingFrames -= microseconds * SampleRate / 1_000_000.0;
            if (_truePaddingFrames < 0)
            {
                Underruns++;
                _truePaddingFrames = 0;
            }
        }

        private void Refill(int frames)
        {
            if (frames <= 0)
            {
                return;
            }

            Buffer.ReadRaw(_fill.AsSpan(0, frames * Channels), WallNow);
            _truePaddingFrames += frames;
            RecordSnaps();
        }

        /// <summary>
        /// What AudioPipeline.OnReanchorRequired does, applied synchronously: the buffer raises
        /// its event from a thread-pool task, which a deterministic harness cannot wait on.
        /// </summary>
        private void HandleReanchor(long reanchorCount)
        {
            if (reanchorCount == Reanchors)
            {
                return;
            }

            Reanchors = (int)reanchorCount;
            Buffer.Clear();
            _truePaddingFrames = 0;
        }

        private void RecordSnaps()
        {
            var stats = Buffer.GetStats();
            HandleReanchor(stats.ReanchorCount);
            var dropped = stats.SamplesDroppedForSync - _lastDropped;
            var inserted = stats.SamplesInsertedForSync - _lastInserted;
            _lastDropped = stats.SamplesDroppedForSync;
            _lastInserted = stats.SamplesInsertedForSync;
            if (dropped != 0 || inserted != 0)
            {
                HardSyncs.Add((WallNow, (dropped - inserted) / Channels));
            }
        }

        /// <summary>Windows' default timer granularity: wakes land 0-15 ms late, deterministically.</summary>
        private long NextJitterUs()
        {
            _rng ^= _rng << 13;
            _rng ^= _rng >> 17;
            _rng ^= _rng << 5;
            return (_rng % 16) * 1000L;
        }

        public void Dispose() => Buffer.Dispose();
    }
}
