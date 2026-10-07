using Microsoft.Extensions.Logging;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;

namespace Sendspin.SDK.Tests.Audio;

/// <summary>
/// The device is a FIFO of frame identities drained at exactly the nominal rate on the wall
/// clock. Each frame carries the index of the stream frame it came from (0 for silence), so a
/// frame's lateness at the moment it leaves is exit time minus ServerToClientTime(its
/// timestamp) — the conversion that already subtracts the output delay.
/// </summary>
internal sealed class PushModeDevicePlayer : IDisposable
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int SamplesPerMs = SampleRate * Channels / 1000;
    private const int ChunkMs = 20;
    private const long ServerT0 = 1_000_000;
    private const long LocalT0 = 9_000_000_000_000;
    private const int DeviceBufferFrames = 4_800;
    private const double FrameUs = 1_000_000.0 / SampleRate;
    private const int IdModulus = 1 << 16;

    private static readonly AudioFormat Format = new()
    {
        Codec = "pcm", SampleRate = SampleRate, Channels = Channels,
    };

    private readonly long _streamT0;
    private readonly bool _splitReads;

    private readonly int _wakeMs;
    private readonly bool _jitter;
    private readonly SyncCorrectedSampleSource _source;
    private readonly Queue<float> _device = new();
    private readonly float[] _fill = new float[DeviceBufferFrames * Channels];
    private readonly List<(long ExitAt, double LatenessUs)> _exits = new();
    private double _headExitAt;
    private long _nextFrameIndex;
    private uint _rng = 0x9E3779B9;

    /// <summary>Unequal shares of one device request, repeated until it is filled.</summary>
    private static readonly int[] SplitPercent = { 7, 31, 13, 2, 47 };

    /// <param name="wakeMs">How long the device loop sleeps between top-ups.</param>
    /// <param name="reportedLatencyMs">What the player reports as <c>OutputLatencyMs</c>.</param>
    /// <param name="jitter">Whether wakes land 0-15 ms late.</param>
    /// <param name="initialDelayMs">Output delay in force from the start.</param>
    /// <param name="leadMicroseconds">How far ahead of its own clock the server sends.</param>
    /// <param name="firstTimestampOffsetMicroseconds">
    /// Where the stream's first frame sits on the server clock, relative to the device's first read.
    /// </param>
    /// <param name="measuresQueue">
    /// Whether the player answers <c>GetCurrentOutputLatencyMicroseconds</c> with what is queued in
    /// the device. The model has no latency after its queue.
    /// </param>
    /// <param name="splitReads">
    /// Whether each device request reaches the SDK as several unequal reads, as it does behind a
    /// host's rate converter.
    /// </param>
    public PushModeDevicePlayer(
        int wakeMs,
        int reportedLatencyMs,
        bool jitter,
        int initialDelayMs,
        long leadMicroseconds = 1_500_000,
        long firstTimestampOffsetMicroseconds = 0,
        bool measuresQueue = false,
        bool splitReads = false)
    {
        _wakeMs = wakeMs;
        _streamT0 = ServerT0 + firstTimestampOffsetMicroseconds;
        _splitReads = splitReads;
        LeadMicroseconds = leadMicroseconds;
        _jitter = jitter;
        Buffer = new TimedAudioBuffer(Format, ClockSync, bufferCapacityMs: 5_000, logger: Log)
        {
            OutputLatencyMicroseconds = reportedLatencyMs * 1000L,
            TimingSourceName = "monotonic",
        };
        if (measuresQueue)
        {
            Buffer.CurrentOutputLatency = QueuedAheadMicroseconds;
        }

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

    /// <summary>Milliseconds of audio (not silence) that have left the device.</summary>
    public double AudioExitedMs => _exits.Count * FrameUs / 1000.0;

    private long _rebaseIndex = long.MaxValue;
    private long _rebaseShiftUs;

    /// <summary>
    /// The server moves its timeline later by <paramref name="shiftUs"/> from the next chunk
    /// on, and holds its lead that much further ahead: what a live stream does when a
    /// player's send-ahead floor rises by a larger output delay. The audio itself keeps
    /// arriving in real time; only its timestamps step.
    /// </summary>
    public void ServerShiftsTimelineLater(long shiftUs)
    {
        _rebaseIndex = _nextFrameIndex;
        _rebaseShiftUs = shiftUs;
        LeadMicroseconds += shiftUs;
    }

    private long TimestampOf(long frameIndex) =>
        _streamT0 + (long)Math.Round(frameIndex * FrameUs) + (frameIndex >= _rebaseIndex ? _rebaseShiftUs : 0);

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

    /// <summary>Lateness of the first <paramref name="windowMs"/> of audio that ever left the device.</summary>
    public (double Median, double P1, double P99) MeasureFirst(int windowMs)
    {
        Assert.NotEmpty(_exits);
        var until = _exits[0].ExitAt + (windowMs * 1000L);
        var window = _exits.Where(e => e.ExitAt <= until).Select(e => e.LatenessUs).ToList();
        window.Sort();
        return (window[window.Count / 2], window[window.Count / 100], window[window.Count - 1 - (window.Count / 100)]);
    }

    /// <summary>When the first audio frame left the device, after the device's first read (µs).</summary>
    public long FirstAudioExitMicroseconds => _exits[0].ExitAt - LocalT0;

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
        while (TimestampOf(_nextFrameIndex) < ServerNow + LeadMicroseconds)
        {
            for (var f = 0; f < chunk.Length / Channels; f++)
            {
                var id = (((_nextFrameIndex + f) % IdModulus) + 1) / (float)(IdModulus * 2);
                chunk[f * Channels] = id;
                chunk[(f * Channels) + 1] = id;
            }

            Buffer.Write(chunk, TimestampOf(_nextFrameIndex));
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
        // Recover the frame index: the encoded id, unwrapped to the candidate nearest the frame
        // that is due now, which the output delay puts that far ahead of the server's clock.
        var residue = (long)Math.Round((value * IdModulus * 2) - 1);
        var nowIndex = (long)((ServerNow + (long)(ClockSync.OutputDelayMs * 1000) - _streamT0 - (_rebaseIndex == long.MaxValue ? 0 : _rebaseShiftUs)) / FrameUs);
        var d = (((nowIndex - residue) % IdModulus) + IdModulus) % IdModulus;
        if (d > IdModulus / 2)
        {
            d -= IdModulus;
        }

        var index = nowIndex - d;
        return ClockSync.ServerToClientTime(TimestampOf(index));
    }

    private void Refill(int frames)
    {
        // One request, possibly as several reads. Each lands behind the ones before it, which is
        // why the queue is extended as they arrive rather than once at the end.
        var remaining = frames;
        var part = 0;
        while (remaining > 0)
        {
            var take = _splitReads
                ? Math.Min(remaining, Math.Max(1, frames * SplitPercent[part++ % SplitPercent.Length] / 100))
                : remaining;

            _source.Read(_fill, 0, take * Channels);
            if (_device.Count == 0)
            {
                _headExitAt = Math.Max(_headExitAt, WallNow);
            }

            for (var f = 0; f < take; f++)
            {
                _device.Enqueue(_fill[f * Channels]);
            }

            remaining -= take;
        }
    }

    /// <summary>When a frame handed to the device now would leave it, from now (µs).</summary>
    private long? QueuedAheadMicroseconds() =>
        _device.Count == 0 ? 0 : (long)Math.Round(_headExitAt + (_device.Count * FrameUs) - WallNow);

    private long NextJitterUs()
    {
        _rng ^= _rng << 13;
        _rng ^= _rng >> 17;
        _rng ^= _rng << 5;
        return (_rng % 16) * 1000L;
    }
}
