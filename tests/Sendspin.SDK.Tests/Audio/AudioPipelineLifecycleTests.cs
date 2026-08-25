using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Tests.Audio;

/// <summary>
/// What the pipeline owns across a stream's life: forwarding the two events that invalidate a
/// correcting sample source's state (reconnect, clear), and not re-allocating a large-object-heap
/// decoded ring for a restart at an unchanged sample rate and channel count.
/// </summary>
public class AudioPipelineLifecycleTests
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int ChunkMs = 20;

    // 48 kHz stereo: 96 interleaved samples per millisecond.
    private const int ChunkSamples = ChunkMs * SampleRate / 1000 * Channels;

    private static AudioFormat Pcm(int sampleRate = SampleRate, int channels = Channels) =>
        new AudioFormat
        {
            Codec = "pcm", SampleRate = sampleRate, Channels = channels, BitDepth = 16,
        };

    /// <summary>
    /// A disposed buffer rejects reads and nothing else about it is observable, so this is how a
    /// test asks whether the pipeline let go of one. Harmless on a live buffer: with nothing
    /// buffered it fills silence and returns.
    /// </summary>
    private static bool IsDisposed(TimedAudioBuffer buffer)
    {
        try
        {
            buffer.Read(new float[Channels], 0);
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    [Fact]
    public async Task NotifyReconnect_ReachesALifecycleAwareSource()
    {
        // The source suppresses its corrections for the same stabilization window the buffer
        // does. Reaching only the buffer left a SyncCorrectedSampleSource correcting against an
        // error the re-converging clock had not finished re-measuring.
        await using var harness = new Harness();
        await harness.Pipeline.StartAsync(Pcm());

        harness.Pipeline.NotifyReconnect();

        Assert.Equal(1, harness.Source.ReconnectCount);
    }

    [Fact]
    public async Task Clear_ResetsALifecycleAwareSource()
    {
        // A stream/clear that stops at the buffer leaves the source's resampler primed with
        // pre-seek audio, which it then splices into the new position.
        await using var harness = new Harness();
        await harness.Pipeline.StartAsync(Pcm());

        harness.Pipeline.Clear();

        Assert.Equal(1, harness.Source.ResetCount);
    }

    [Fact]
    public async Task Clear_AndReconnect_WithAPlainSampleSource_AreNoOps()
    {
        // IAudioSampleSource is a pull loop and nothing more; a source with no state to
        // invalidate must not have to implement the lifecycle interface to keep working.
        await using var harness = new Harness(lifecycleAware: false);
        await harness.Pipeline.StartAsync(Pcm());

        harness.Pipeline.NotifyReconnect();
        harness.Pipeline.Clear();

        Assert.Equal(AudioPipelineState.Buffering, harness.Pipeline.State);
    }

    [Fact]
    public async Task ClearAndReconnect_BeforeAnyStream_AreNoOps()
    {
        await using var harness = new Harness();

        harness.Pipeline.NotifyReconnect();
        harness.Pipeline.Clear();

        Assert.Equal(AudioPipelineState.Idle, harness.Pipeline.State);
    }

    [Fact]
    public async Task RestartAtTheSameRateAndChannels_ReusesTheDecodedRing()
    {
        // The default ring is 30 s of float PCM — about 11.5 MB at 48 kHz stereo, straight onto
        // the large object heap. Building a fresh one for every stop/start cycle churns that for
        // nothing: Clear() already returns the buffer to its post-construction state, and the
        // ring's shape depends on nothing but the sample rate and channel count.
        await using var harness = new Harness();
        await harness.Pipeline.StartAsync(Pcm());

        await harness.Pipeline.StopAsync();
        await harness.Pipeline.StartAsync(Pcm());

        Assert.Equal(1, harness.BufferFactoryCalls);
        Assert.False(IsDisposed(harness.Buffers[0]));

        // ...and it is the buffer the restarted pipeline is actually writing into.
        harness.Feed(1);
        Assert.Equal(ChunkMs, harness.Buffers[0].BufferedMilliseconds);
    }

    [Fact]
    public async Task ReusedRing_ComesBackCleared()
    {
        // Reuse is only safe because the buffer is cleared on the way back in. Carrying the
        // previous stream's audio or timeline into the next one would be worse than the
        // allocation it avoids.
        await using var harness = new Harness();
        await harness.Pipeline.StartAsync(Pcm());
        harness.Feed(3);

        Assert.True(harness.Buffers[0].BufferedMilliseconds > 0);

        await harness.Pipeline.StopAsync();
        await harness.Pipeline.StartAsync(Pcm());

        Assert.Equal(0, harness.Buffers[0].BufferedMilliseconds);
        Assert.False(harness.Buffers[0].GetStats().IsPlaybackActive);
    }

    [Fact]
    public async Task RestartAtADifferentSampleRate_ReallocatesAndDisposesTheOldRing()
    {
        await using var harness = new Harness();
        await harness.Pipeline.StartAsync(Pcm());

        await harness.Pipeline.StopAsync();
        await harness.Pipeline.StartAsync(Pcm(sampleRate: 44_100));

        Assert.Equal(2, harness.BufferFactoryCalls);
        Assert.True(IsDisposed(harness.Buffers[0]));
    }

    [Fact]
    public async Task RestartAtADifferentChannelCount_Reallocates()
    {
        await using var harness = new Harness();
        await harness.Pipeline.StartAsync(Pcm());

        await harness.Pipeline.StopAsync();
        await harness.Pipeline.StartAsync(Pcm(channels: 1));

        Assert.Equal(2, harness.BufferFactoryCalls);
        Assert.True(IsDisposed(harness.Buffers[0]));
    }

    [Fact]
    public async Task DisposingThePipeline_DisposesTheRetainedRing()
    {
        var harness = new Harness();
        await harness.Pipeline.StartAsync(Pcm());
        await harness.Pipeline.StopAsync();

        await harness.DisposeAsync();

        Assert.True(IsDisposed(harness.Buffers[0]));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private long _nextTimestamp = 1_000_000;

        public Harness(bool lifecycleAware = true)
        {
            Pipeline = new AudioPipeline(
                NullLogger<AudioPipeline>.Instance,
                new AudioDecoderFactory(),
                new FakeClockSynchronizer { HasMinimalSync = true, IsConverged = true },
                (format, clockSync) =>
                {
                    BufferFactoryCalls++;
                    var buffer = new TimedAudioBuffer(format, clockSync, bufferCapacityMs: 2000);
                    Buffers.Add(buffer);
                    return buffer;
                },
                () => new StubAudioPlayer(),
                (buffer, _) =>
                {
                    var source = new TrackingSampleSource(buffer);
                    Sources.Add(source);
                    return lifecycleAware ? source : new PlainSampleSource(buffer);
                },
                precisionTimer: new StubTimer(),
                useMonotonicTimer: false);
        }

        public AudioPipeline Pipeline { get; }

        public int BufferFactoryCalls { get; private set; }

        public List<TimedAudioBuffer> Buffers { get; } = new();

        public List<TrackingSampleSource> Sources { get; } = new();

        public TrackingSampleSource Source => Sources[^1];

        /// <summary>Feeds <paramref name="chunkCount"/> chunks of silence, one chunk long each.</summary>
        public void Feed(int chunkCount)
        {
            var encoded = new byte[ChunkSamples * 2];
            for (var i = 0; i < chunkCount; i++)
            {
                Pipeline.ProcessAudioChunk(
                    new AudioChunk { EncodedData = encoded, ServerTimestamp = _nextTimestamp });
                _nextTimestamp += ChunkMs * 1000L;
            }
        }

        public ValueTask DisposeAsync() => Pipeline.DisposeAsync();
    }

    private sealed class TrackingSampleSource : IAudioSampleSource, IPlaybackLifecycleAware
    {
        private readonly ITimedAudioBuffer _buffer;

        internal TrackingSampleSource(ITimedAudioBuffer buffer) => _buffer = buffer;

        public int ResetCount { get; private set; }

        public int ReconnectCount { get; private set; }

        public AudioFormat Format => _buffer.Format;

        public int Read(float[] buffer, int offset, int count) => 0;

        public void Reset() => ResetCount++;

        public void NotifyReconnect() => ReconnectCount++;
    }

    /// <summary>A source with no state of its own, i.e. the interface's minimum.</summary>
    private sealed class PlainSampleSource : IAudioSampleSource
    {
        private readonly ITimedAudioBuffer _buffer;

        internal PlainSampleSource(ITimedAudioBuffer buffer) => _buffer = buffer;

        public AudioFormat Format => _buffer.Format;

        public int Read(float[] buffer, int offset, int count) => 0;
    }

    private sealed class StubTimer : IHighPrecisionTimer
    {
        public long GetCurrentTimeMicroseconds() => 0;

        public long GetElapsedMicroseconds(long fromTimeMicroseconds) => 0;
    }

    private sealed class StubAudioPlayer : IAudioPlayer
    {
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

        public void SetSampleSource(IAudioSampleSource source)
        {
        }

        public void Play() => State = AudioPlayerState.Playing;

        public void Pause() => State = AudioPlayerState.Paused;

        public void Stop() => State = AudioPlayerState.Stopped;

        public Task SwitchDeviceAsync(string? deviceId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
