using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;

namespace Sendspin.SDK.Tests.Audio;

/// <summary>
/// Issue #352: what the buffer does with a chunk it has no room for once playback is running.
/// <c>buffer_capacity</c> is a byte figure and the ring is a duration, so a stream that
/// compresses harder than the advertisement assumed can legally arrive faster than the ring
/// drains. The server will not resend, so the only choice is which audio is lost: the audio
/// due next, or the audio that just arrived.
/// </summary>
public class TimedAudioBufferOverrunTests
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int FramesPerMs = SampleRate / 1000;
    private const int SamplesPerMs = FramesPerMs * Channels;
    private const int ChunkMs = 20;
    private const int StepMs = 10;
    private const int RingMs = 5_000;
    private const long ServerT0 = 1_000_000;
    private const long LocalT0 = 9_000_000_000_000;

    private static readonly AudioFormat Format = new()
    {
        Codec = "flac",
        SampleRate = SampleRate,
        Channels = Channels,
    };

    /// <summary>
    /// A player on a 5 s ring fed by a server that keeps a fixed horizon of audio outstanding,
    /// as aiosendspin's buffer tracker does: a chunk is sent as soon as its end is within the
    /// horizon. Every frame carries its own position in the stream, so each frame that comes
    /// out can be checked against the instant it was due.
    /// </summary>
    private sealed class Rig : IDisposable
    {
        private readonly float[] _chunk = new float[ChunkMs * SamplesPerMs];
        private readonly float[] _callback = new float[StepMs * SamplesPerMs];
        private readonly FakeClockSynchronizer _clockSync = new();
        private readonly AutoResetEvent _reanchorRaised = new(false);
        private long _wallNow = LocalT0;
        private long _writeServerTs = ServerT0;
        private long _reanchorsSeen;
        private bool _reanchorRequested;

        public Rig(long horizonMicroseconds)
        {
            HorizonMicroseconds = horizonMicroseconds;
            Buffer = new TimedAudioBuffer(Format, _clockSync, RingMs);
            Buffer.ReanchorRequired += (_, _) => _reanchorRaised.Set();

            _clockSync.OffsetMicroseconds = ServerT0 - LocalT0;
            _clockSync.IsConverged = true;
            _clockSync.HasMinimalSync = true;
        }

        public TimedAudioBuffer Buffer { get; }

        public long HorizonMicroseconds { get; set; }

        /// <summary>Frames that came out within 6 ms of the instant they were due.</summary>
        public long FramesOnTime { get; private set; }

        /// <summary>Frames of silence that came out.</summary>
        public long FramesSilent { get; private set; }

        /// <summary>Frames that came out, but not when they were due.</summary>
        public long FramesMisplaced { get; private set; }

        /// <summary>Sends every chunk whose end is inside the server's horizon.</summary>
        public void Produce()
        {
            var serverNow = _wallNow + _clockSync.OffsetMicroseconds;
            while (_writeServerTs + (ChunkMs * 1000L) <= serverNow + HorizonMicroseconds)
            {
                var firstFrame = (_writeServerTs - ServerT0) / 1000 * FramesPerMs;
                for (var i = 0; i < _chunk.Length; i++)
                {
                    _chunk[i] = firstFrame + (i / Channels) + 1;
                }

                Buffer.Write(_chunk, _writeServerTs);
                _writeServerTs += ChunkMs * 1000L;
            }
        }

        /// <summary>
        /// One 10 ms output callback at the current wall clock. A re-anchor is answered as
        /// <see cref="AudioPipeline"/> answers it, by clearing the buffer.
        /// </summary>
        public void Read()
        {
            Buffer.Read(_callback, _wallNow);

            // Requested in one read, raised by the next.
            if (_reanchorRequested)
            {
                _reanchorRequested = false;
                Assert.True(_reanchorRaised.WaitOne(TimeSpan.FromSeconds(10)));
                Buffer.Clear();
            }

            var reanchors = Buffer.GetStats().ReanchorCount;
            _reanchorRequested = reanchors > _reanchorsSeen;
            _reanchorsSeen = reanchors;

            var dueFrame = (_wallNow - LocalT0) / 1000 * FramesPerMs;
            for (var frame = 0; frame < _callback.Length / Channels; frame++)
            {
                var value = _callback[frame * Channels];
                if (value == 0f)
                {
                    FramesSilent++;
                }
                else if (Math.Abs((long)value - 1 - (dueFrame + frame)) <= 6 * FramesPerMs)
                {
                    FramesOnTime++;
                }
                else
                {
                    FramesMisplaced++;
                }
            }
        }

        public void Run(int milliseconds)
        {
            for (var elapsed = 0; elapsed < milliseconds; elapsed += StepMs)
            {
                _wallNow += StepMs * 1000L;
                Produce();
                Read();
            }
        }

        public void Dispose()
        {
            Buffer.Dispose();
            _reanchorRaised.Dispose();
        }
    }

    [Fact]
    public void ChunkThatDoesNotFit_DuringPlayback_IsDiscarded_NotTheAudioDueNext()
    {
        // The ring is full and playing, with room for half a chunk. One more chunk arrives.
        using var rig = new Rig(horizonMicroseconds: RingMs * 1000L);
        rig.Produce();
        rig.Read();
        rig.HorizonMicroseconds += (StepMs + ChunkMs) * 1000L;
        rig.Produce();
        rig.HorizonMicroseconds = RingMs * 1000L;

        // What comes out next is still the audio that is due next.
        rig.Run(1_000);

        Assert.Equal(0, rig.FramesMisplaced);
        Assert.Equal(0, rig.FramesSilent);

        var stats = rig.Buffer.GetStats();
        Assert.Equal(1, stats.OverrunCount);
        Assert.Equal(ChunkMs * SamplesPerMs, stats.DroppedSamples);
        Assert.Equal(0, stats.ContentHolesDetected);
    }

    [Fact]
    public void ServerHoldingMoreThanTheRing_CostsTheExcess_NotThePlayback()
    {
        // 300 ms more outstanding than the ring holds, for a minute. Something has to be lost:
        // 300 ms in every 5.3 s is the least it can be.
        using var rig = new Rig(horizonMicroseconds: (RingMs + 300) * 1000L);
        rig.Produce();
        rig.Read();
        rig.Run(60_000);

        var total = rig.FramesOnTime + rig.FramesSilent + rig.FramesMisplaced;
        var onTime = rig.FramesOnTime / (double)total;

        Assert.True(
            onTime > 0.9,
            $"only {onTime:P0} of the output was audio played when it was due " +
            $"({rig.FramesSilent / (double)total:P0} silence, {rig.FramesMisplaced / (double)total:P0} misplaced)");
    }
}
