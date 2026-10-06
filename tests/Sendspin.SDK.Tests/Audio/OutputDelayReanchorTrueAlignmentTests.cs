using Microsoft.Extensions.Logging;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;
using Xunit.Abstractions;

namespace Sendspin.SDK.Tests.Audio;

/// <summary>
/// Investigation repro: a push-mode device (4 800-frame buffer filled whole before Start, then
/// topped up on periodic wakes) plays a live stream through <see cref="SyncCorrectedSampleSource"/>,
/// the output delay changes mid-stream, and the host re-anchors (the buffer call AudioPipeline.ReanchorTiming makes). Alignment is measured at the device's exit, from the frame
/// identities that actually leave it — not from anything the buffer reports.
/// </summary>
public class OutputDelayReanchorTrueAlignmentTests
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int SamplesPerMs = SampleRate * Channels / 1000;
    private const int ChunkMs = 20;
    private const long ServerT0 = 1_000_000;
    private const long LocalT0 = 9_000_000_000_000;
    private const int DeviceBufferFrames = 4_800;
    private const double FrameUs = 1_000_000.0 / SampleRate;

    private static readonly AudioFormat Format = new()
    {
        Codec = "pcm", SampleRate = SampleRate, Channels = Channels,
    };

    private readonly ITestOutputHelper _output;

    public OutputDelayReanchorTrueAlignmentTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData(0, 40, 10, 100, false)]
    [InlineData(0, 40, 50, 100, false)]
    [InlineData(0, 500, 10, 100, false)]
    [InlineData(0, 500, 50, 100, false)]
    [InlineData(0, 40, 10, 100, true)]
    [InlineData(0, 40, 50, 100, true)]
    [InlineData(0, 40, 10, 0, false)]
    [InlineData(0, 40, 50, 0, false)]
    [InlineData(0, 500, 10, 0, false)]
    [InlineData(0, 500, 50, 0, false)]
    [InlineData(100, 60, 10, 0, false)]
    [InlineData(700, 0, 50, 0, false)]
    public void OutputDelayChange_ThenReanchor_MovesPlaybackByExactlyTheDelay(
        int initialDelayMs, int delayMs, int wakeMs, int reportedLatencyMs, bool jitter)
    {
        using var player = new Player(wakeMs, reportedLatencyMs, jitter, initialDelayMs);

        player.Run(5_000);
        var before = player.Measure(500);
        var reportedBefore = player.Buffer.SyncErrorMicroseconds;
        var bufferedBefore = player.Buffer.BufferedMilliseconds;

        player.ClockSync.OutputDelayMs = delayMs;
        player.Run(300);
        player.Reanchor();
        player.Run(6_000);
        var after = player.Measure(500);

        _output.WriteLine(
            $"delay {initialDelayMs}->{delayMs}ms wake {wakeMs}ms reportedLatency {reportedLatencyMs}ms jitter {jitter}");
        foreach (var entry in player.Log.Entries.Where(e => e.Message.Contains("[Correction]") || e.Message.Contains("Skipped")))
        {
            _output.WriteLine("  log: " + entry.Message);
        }

        _output.WriteLine(
            $"  BEFORE: true lateness median {before.Median / 1000.0:+0.00;-0.00}ms (p1 {before.P1 / 1000.0:+0.00;-0.00}, p99 {before.P99 / 1000.0:+0.00;-0.00}), " +
            $"reported {reportedBefore / 1000.0:+0.00;-0.00}ms, buffered {bufferedBefore:F0}ms");
        _output.WriteLine(
            $"  AFTER : true lateness median {after.Median / 1000.0:+0.00;-0.00}ms (p1 {after.P1 / 1000.0:+0.00;-0.00}, p99 {after.P99 / 1000.0:+0.00;-0.00}), " +
            $"reported {player.Buffer.SyncErrorMicroseconds / 1000.0:+0.00;-0.00}ms, " +
            $"buffered {player.Buffer.BufferedMilliseconds:F0}ms, device underruns {player.DeviceUnderruns}, re-anchors {player.Buffer.GetStats().ReanchorCount}");

        // Lateness is measured against ServerToClientTime, which already carries the delay, so
        // a re-anchor that moved playback by exactly the delay leaves it where it was.
        var moved = after.Median - before.Median;
        Assert.True(
            Math.Abs(moved) < 1_000,
            $"The re-anchor moved playback {moved / 1000.0:+0.00;-0.00}ms beyond the requested delay change: " +
            $"{before.Median / 1000.0:+0.00;-0.00}ms from schedule before, {after.Median / 1000.0:+0.00;-0.00}ms after, " +
            $"while the buffer reports {player.Buffer.SyncErrorMicroseconds / 1000.0:+0.00;-0.00}ms.");

        // With no latency reported, this device's cold start is on schedule, so the absolute
        // figure can be held to the spec's 1 ms as well.
        if (reportedLatencyMs == 0)
        {
            Assert.True(
                Math.Abs(after.Median) < 1_000,
                $"Audio leaves the device {after.Median / 1000.0:+0.00;-0.00}ms from its schedule after the re-anchor.");
        }
    }

    /// <summary>
    /// A live stream holds almost nothing: here about 80 ms. A +500 ms delay asks for audio the
    /// server has not sent yet, so the only honest outcome is silence until it has, and then
    /// playback exactly where it was against the schedule — no re-anchor, no residue.
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(0)]
    public void DelayChangeLargerThanTheBufferedAudio_GoesSilentThenResumesAligned(int reportedLatencyMs)
    {
        // Cold start reads one device buffer ahead, plus the pre-roll: lead the server by that
        // much and 80 ms more.
        var lead = (180 + reportedLatencyMs) * 1000L;
        using var player = new Player(wakeMs: 10, reportedLatencyMs, jitter: false, initialDelayMs: 0, lead);

        player.Run(5_000);
        var before = player.Measure(500);
        var bufferedBefore = player.Buffer.BufferedMilliseconds;
        Assert.InRange(bufferedBefore, 60, 100);

        player.ClockSync.OutputDelayMs = 500;
        player.Run(300);
        player.Reanchor();

        // The server takes 200 ms to start sending 500 ms further ahead.
        player.Run(200);
        var underrunsWhileDry = player.Buffer.GetStats().UnderrunCount;
        player.LeadMicroseconds = lead + 500_000;
        player.Run(6_000);
        var after = player.Measure(500);
        var stats = player.Buffer.GetStats();

        _output.WriteLine(
            $"reportedLatency {reportedLatencyMs}ms: buffered {bufferedBefore:F0}ms -> {stats.BufferedMs:F0}ms, " +
            $"true lateness {before.Median / 1000.0:+0.00;-0.00}ms -> {after.Median / 1000.0:+0.00;-0.00}ms, " +
            $"reported {stats.SyncErrorMicroseconds / 1000.0:+0.00;-0.00}ms, underruns while dry {underrunsWhileDry}, " +
            $"re-anchors {stats.ReanchorCount}, hard syncs {stats.HardSyncCount}");
        foreach (var entry in player.Log.Entries.Where(e => e.Message.Contains("[Correction]") || e.Message.Contains("Skipped")))
        {
            _output.WriteLine("  log: " + entry.Message);
        }

        Assert.True(underrunsWhileDry > 0, "the buffer should have run dry while the server caught up");
        Assert.Equal(0, stats.ReanchorCount);
        Assert.InRange(after.Median - before.Median, -1_000, 1_000);
        Assert.InRange(stats.BufferedMs, bufferedBefore - 25, bufferedBefore + 25);
    }

    /// <summary>
    /// The device is a FIFO of frame identities drained at exactly the nominal rate on the wall
    /// clock. Each frame carries the index of the stream frame it came from (0 for silence), so a
    /// frame's lateness at the moment it leaves is exit time minus ServerToClientTime(its
    /// timestamp) — the conversion that already subtracts the output delay.
    /// </summary>
    private sealed class Player : IDisposable
    {
        private const int IdModulus = 1 << 16;

        private readonly int _wakeMs;
        private readonly bool _jitter;
        private readonly SyncCorrectedSampleSource _source;
        private readonly Queue<float> _device = new();
        private readonly float[] _fill = new float[DeviceBufferFrames * Channels];
        private readonly List<(long ExitAt, double LatenessUs)> _exits = new();
        private double _headExitAt;
        private long _nextFrameIndex;
        private uint _rng = 0x9E3779B9;

        public Player(int wakeMs, int reportedLatencyMs, bool jitter, int initialDelayMs, long leadMicroseconds = 1_500_000)
        {
            _wakeMs = wakeMs;
            LeadMicroseconds = leadMicroseconds;
            _jitter = jitter;
            Buffer = new TimedAudioBuffer(Format, ClockSync, bufferCapacityMs: 5_000, logger: Log)
            {
                OutputLatencyMicroseconds = reportedLatencyMs * 1000L,
                TimingSourceName = "monotonic",
            };
            _source = new SyncCorrectedSampleSource(Buffer, () => WallNow);

            ClockSync.OutputDelayMs = initialDelayMs;
            ClockSync.OffsetMicroseconds = ServerT0 - LocalT0;
            ClockSync.IsConverged = true;
            ClockSync.HasMinimalSync = true;
            PumpProducer();

            // NAudio push mode: fill the whole device buffer once, then start the device.
            Refill(DeviceBufferFrames);
        }

        public FakeClockSynchronizer ClockSync { get; } = new();

        public CapturingLogger<TimedAudioBuffer> Log { get; } = new();

        public TimedAudioBuffer Buffer { get; }

        public long WallNow { get; private set; } = LocalT0;

        public int DeviceUnderruns { get; private set; }

        /// <summary>How far ahead of its own clock the server has sent.</summary>
        public long LeadMicroseconds { get; set; } = 1_500_000;

        private long ServerNow => WallNow + ClockSync.OffsetMicroseconds;

        public void Run(int milliseconds)
        {
            var until = WallNow + (milliseconds * 1000L);
            while (WallNow < until)
            {
                Advance((_wakeMs * 1000L) + (_jitter ? NextJitterUs() : 0));
                PumpProducer();
                var available = DeviceBufferFrames - _device.Count;
                if (available > 10)
                {
                    Refill(available);
                }
            }
        }

        /// <summary>Lateness of the audio frames that left the device in the last window (µs, + = late).</summary>
        public (double Median, double P1, double P99) Measure(int windowMs)
        {
            var since = WallNow - (windowMs * 1000L);
            var window = _exits.Where(e => e.ExitAt >= since).Select(e => e.LatenessUs).ToList();
            Assert.NotEmpty(window);
            // Median and 1st/99th percentiles: a resampled frame that straddles an id wrap or a
            // silence boundary decodes to garbage, one frame at a time.
            window.Sort();
            return (window[window.Count / 2], window[window.Count / 100], window[window.Count - 1 - (window.Count / 100)]);
        }

        /// <summary>What AudioPipeline.ReanchorTiming does to the buffer.</summary>
        public void Reanchor() => Buffer.ApplyOutputDelayChange();

        public void Dispose()
        {
            _source.Dispose();
            Buffer.Dispose();
        }

        /// <summary>A live stream: the server stays its lead ahead, contiguous timestamps.</summary>
        private void PumpProducer()
        {
            var chunk = new float[ChunkMs * SamplesPerMs];
            while (ServerT0 + (long)Math.Round(_nextFrameIndex * FrameUs) < ServerNow + LeadMicroseconds)
            {
                for (var f = 0; f < chunk.Length / Channels; f++)
                {
                    var id = (((_nextFrameIndex + f) % IdModulus) + 1) / (float)(IdModulus * 2);
                    chunk[f * Channels] = id;
                    chunk[(f * Channels) + 1] = id;
                }

                Buffer.Write(chunk, ServerT0 + (long)Math.Round(_nextFrameIndex * FrameUs));
                _nextFrameIndex += chunk.Length / Channels;
            }
        }

        private void Advance(long microseconds)
        {
            WallNow += microseconds;
            while (_device.Count > 0 && _headExitAt <= WallNow)
            {
                var value = _device.Dequeue();
                if (value != 0f)
                {
                    _exits.Add(((long)_headExitAt, _headExitAt - ScheduledFor(value)));
                }

                _headExitAt += FrameUs;
            }

            if (_device.Count == 0 && _headExitAt <= WallNow)
            {
                DeviceUnderruns++;
            }
        }

        /// <summary>Schedule of the stream frame a device sample came from, per the spec's conversion.</summary>
        private double ScheduledFor(float value)
        {
            // Recover the frame index: the encoded id, unwrapped to the candidate nearest "now".
            var residue = (long)Math.Round((value * IdModulus * 2) - 1);
            var nowIndex = (long)((ServerNow - ServerT0) / FrameUs);
            var d = (((nowIndex - residue) % IdModulus) + IdModulus) % IdModulus;
            if (d > IdModulus / 2)
            {
                d -= IdModulus;
            }

            var index = nowIndex - d;
            return ClockSync.ServerToClientTime(ServerT0 + (long)Math.Round(index * FrameUs));
        }

        private void Refill(int frames)
        {
            _source.Read(_fill, 0, frames * Channels);
            if (_device.Count == 0)
            {
                _headExitAt = Math.Max(_headExitAt, WallNow);
            }

            for (var f = 0; f < frames; f++)
            {
                _device.Enqueue(_fill[f * Channels]);
            }
        }

        private long NextJitterUs()
        {
            _rng ^= _rng << 13;
            _rng ^= _rng >> 17;
            _rng ^= _rng << 5;
            return (_rng % 16) * 1000L;
        }
    }
}
