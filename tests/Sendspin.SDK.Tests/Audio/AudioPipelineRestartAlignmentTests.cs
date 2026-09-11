using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Tests.Audio;

/// <summary>
/// Issue #272 reported that every stop/start cycle left the player displaced against the
/// group by a roughly constant amount, and that the displacements accumulated — 22 restarts
/// put it half a second out. The displacement was traced to the recording rig rather than to
/// the player, but the property it claimed the SDK lacked is worth pinning: alignment after
/// N restarts is no worse than after one. A test that measures one start cannot see it.
/// </summary>
/// <remarks>
/// The pipeline is driven with a perfect player (no hidden output latency, no queued audio
/// surviving a stop) and a clock that never moves, so anything that accumulates here is the
/// SDK's own doing. Alignment is measured as where the first content sample of each stream
/// actually lands on the local clock, against where the buffer scheduled it.
/// </remarks>
public class AudioPipelineRestartAlignmentTests
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int ChunkMs = 20;
    private const int ChunkFrames = ChunkMs * SampleRate / 1000;
    private const int ChunkSamples = ChunkFrames * Channels;
    private const int PullMs = 10;
    private const int PullSamples = PullMs * SampleRate / 1000 * Channels;
    private const double MicrosecondsPerFrame = 1_000_000.0 / SampleRate;

    // Server clock is small (booted recently); local is a typical large monotonic value.
    private const long ServerMinusClient = 1_000_000_000L;
    private const long LocalStart = 9_000_000_000_000L;

    private static AudioFormat Pcm() => new()
    {
        Codec = "pcm", SampleRate = SampleRate, Channels = Channels, BitDepth = 16,
    };

    /// <summary>
    /// The issue's reproducing shape: a <c>next</c> puts the pipeline through an aborted
    /// open (start, receive audio, stopped before playback begins) and then a completed one.
    /// </summary>
    [Fact]
    public async Task TwentyStopStartCycles_WithAnAbortedOpenEach_DoNotAccumulateDisplacement()
    {
        await using var harness = new Harness();
        var errors = new List<long>();

        for (var cycle = 0; cycle < 20; cycle++)
        {
            await harness.RunAbortedOpenAsync();
            errors.Add(await harness.RunCompletedStreamAsync());
        }

        AssertNoAccumulation(errors);
    }

    /// <summary>
    /// The issue's control: a plain <c>seek</c> is one completed open, and displaced by an
    /// order of magnitude less. Both shapes must hold alignment across repetitions.
    /// </summary>
    [Fact]
    public async Task TwentyStopStartCycles_CompletedOnly_DoNotAccumulateDisplacement()
    {
        await using var harness = new Harness();
        var errors = new List<long>();

        for (var cycle = 0; cycle < 20; cycle++)
        {
            errors.Add(await harness.RunCompletedStreamAsync());
        }

        AssertNoAccumulation(errors);
    }

    private static void AssertNoAccumulation(List<long> errorsMicroseconds)
    {
        var first = errorsMicroseconds[0];
        var worst = errorsMicroseconds.Max(e => Math.Abs(e - first));
        var trace = string.Join(", ", errorsMicroseconds.Select(e => $"{e / 1000.0:+0.0;-0.0}"));

        // Two milliseconds is well inside one 10 ms callback and far outside the ~24 ms per
        // restart the issue measured, so a ratchet fails this loudly and jitter does not.
        Assert.True(
            worst < 2_000,
            $"Alignment moved {worst / 1000.0:F1}ms from the first stream across " +
            $"{errorsMicroseconds.Count} restarts. Per-stream error (ms): {trace}");
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly SettableTimer _timer = new() { Now = LocalStart };
        private readonly FakeClockSynchronizer _clock = new()
        {
            OffsetMicroseconds = ServerMinusClient, IsConverged = true, HasMinimalSync = true,
        };

        private readonly PullingPlayer _player = new();
        private RawSource? _source;

        public Harness()
        {
            Pipeline = new AudioPipeline(
                NullLogger<AudioPipeline>.Instance,
                new AudioDecoderFactory(),
                _clock,
                (format, clockSync) => new TimedAudioBuffer(format, clockSync, bufferCapacityMs: 4000),
                () => _player,
                (buffer, time) => _source = new RawSource(buffer, time),
                precisionTimer: _timer,
                useMonotonicTimer: false);
        }

        public AudioPipeline Pipeline { get; }

        /// <summary>
        /// Start, receive enough audio to be ready, let the player pull a few callbacks of
        /// silence (the schedule is two seconds out, so nothing is due), then stop.
        /// </summary>
        public async Task RunAbortedOpenAsync()
        {
            await Pipeline.StartAsync(Pcm());
            Feed(scheduledLeadMs: 2_000, chunks: 20);

            // The issue's aborted opens reached the render loop: the readiness gate released the
            // player and it pulled silence before the stop arrived. Pin that this one does too,
            // rather than stopping a pipeline that never got past Buffering.
            Assert.Equal(AudioPipelineState.Playing, Pipeline.State);

            for (var i = 0; i < 5; i++)
            {
                Advance();
            }

            Assert.Null(_source!.FirstContentLocalTime);
            await Pipeline.StopAsync();
        }

        /// <summary>
        /// Start, receive audio scheduled 200 ms out, pull until the first content sample
        /// lands, keep pulling well past the startup grace so the baseline is captured, then
        /// stop. Returns where that first sample landed relative to its schedule.
        /// </summary>
        public async Task<long> RunCompletedStreamAsync()
        {
            await Pipeline.StartAsync(Pcm());
            var firstServerTimestamp = Feed(scheduledLeadMs: 200, chunks: 40);

            // Keep the stream fed while it plays: audio arrives ahead of need, as a server does.
            var pulls = 0;
            while (_source!.FirstContentLocalTime is null && pulls++ < 200)
            {
                Advance();
            }

            Assert.NotNull(_source.FirstContentLocalTime);

            for (var i = 0; i < 80; i++) // 800 ms: past the 500 ms startup grace
            {
                Advance();
                if (i % 2 == 0)
                {
                    FeedOne();
                }
            }

            var scheduled = _clock.ServerToClientTime(firstServerTimestamp);
            var error = _source.FirstContentLocalTime!.Value - scheduled;

            await Pipeline.StopAsync();
            return error;
        }

        private long _nextServerTimestamp;

        /// <summary>
        /// Feeds <paramref name="chunks"/> consecutive chunks of non-silent PCM, the first
        /// scheduled <paramref name="scheduledLeadMs"/> after the current local time.
        /// Returns the first chunk's server timestamp.
        /// </summary>
        private long Feed(int scheduledLeadMs, int chunks)
        {
            _nextServerTimestamp = _clock.ClientToServerTime(_timer.Now + (scheduledLeadMs * 1000L));
            var first = _nextServerTimestamp;
            for (var i = 0; i < chunks; i++)
            {
                FeedOne();
            }

            return first;
        }

        private void FeedOne()
        {
            Pipeline.ProcessAudioChunk(new AudioChunk
            {
                EncodedData = Content(), ServerTimestamp = _nextServerTimestamp,
            });
            _nextServerTimestamp += ChunkMs * 1000L;
        }

        /// <summary>Advances the local clock by one callback and lets the player pull it.</summary>
        private void Advance()
        {
            _timer.Now += PullMs * 1000L;
            _player.Pull(PullSamples);
        }

        /// <summary>16-bit little-endian PCM at half scale: decodes to 0.5f, never to silence.</summary>
        private static byte[] Content()
        {
            var bytes = new byte[ChunkSamples * 2];
            for (var i = 0; i < bytes.Length; i += 2)
            {
                bytes[i] = 0x00;
                bytes[i + 1] = 0x40;
            }

            return bytes;
        }

        public ValueTask DisposeAsync() => Pipeline.DisposeAsync();
    }

    private sealed class SettableTimer : IHighPrecisionTimer
    {
        public long Now { get; set; }

        public long GetCurrentTimeMicroseconds() => Now;

        public long GetElapsedMicroseconds(long fromTimeMicroseconds) => Now - fromTimeMicroseconds;
    }

    /// <summary>
    /// Reads through the external-correction path the issue's host uses, and records the
    /// local time at which the first non-silent sample was handed out — including its offset
    /// within the callback, so a startup snap's silence prefix is accounted for.
    /// </summary>
    private sealed class RawSource : IAudioSampleSource
    {
        private readonly ITimedAudioBuffer _buffer;
        private readonly Func<long> _time;

        internal RawSource(ITimedAudioBuffer buffer, Func<long> time)
        {
            _buffer = buffer;
            _time = time;
        }

        public long? FirstContentLocalTime { get; private set; }

        public AudioFormat Format => _buffer.Format;

        public int Read(float[] buffer, int offset, int count)
        {
            var now = _time();
            var span = buffer.AsSpan(offset, count);
            var read = _buffer.ReadRaw(span, now);

            if (FirstContentLocalTime is null)
            {
                for (var i = 0; i < count; i++)
                {
                    if (span[i] != 0f)
                    {
                        FirstContentLocalTime = now + (long)(i / Channels * MicrosecondsPerFrame);
                        break;
                    }
                }
            }

            return read;
        }
    }

    /// <summary>
    /// A player with nothing of its own: no output latency, no queue, no state across a stop.
    /// The harness pulls through it explicitly so callback timing is under test control.
    /// </summary>
    private sealed class PullingPlayer : IAudioPlayer
    {
        private IAudioSampleSource? _source;

        event EventHandler<AudioPlayerState>? IAudioPlayer.StateChanged
        {
            add => _ = value;
            remove => _ = value;
        }

        event EventHandler<AudioPlayerError>? IAudioPlayer.ErrorOccurred
        {
            add => _ = value;
            remove => _ = value;
        }

        public AudioPlayerState State { get; private set; } = AudioPlayerState.Uninitialized;

        public float Volume { get; set; }

        public bool IsMuted { get; set; }

        public int OutputLatencyMs => 0;

        public Task InitializeAsync(AudioFormat format, CancellationToken cancellationToken = default)
        {
            State = AudioPlayerState.Stopped;
            return Task.CompletedTask;
        }

        public void SetSampleSource(IAudioSampleSource source) => _source = source;

        public void Play() => State = AudioPlayerState.Playing;

        public void Pause() => State = AudioPlayerState.Paused;

        public void Stop() => State = AudioPlayerState.Stopped;

        public void Pull(int samples)
        {
            if (_source is null)
            {
                return;
            }

            _source.Read(new float[samples], 0, samples);
        }

        public Task SwitchDeviceAsync(string? deviceId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            _source = null;
            return ValueTask.CompletedTask;
        }
    }
}
