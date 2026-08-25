using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Tests.Audio;

/// <summary>
/// A <c>stream/start</c> for a stream that is already running is a configuration update, not a
/// restart (#201): the pipeline must keep buffered audio, the running timeline and the readiness
/// gate. These tests pin what is applied in place — a re-announced format and a decode-side change
/// at an unchanged sample rate and channel count — and the one case that still restarts, a sample
/// rate or channel change, which is the documented deviation.
/// </summary>
public class AudioPipelineStreamStartTests
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int ChunkMs = 20;

    // 48 kHz stereo: 96 interleaved samples per millisecond.
    private const int ChunkSamples = ChunkMs * SampleRate / 1000 * Channels;

    // TimedAudioBuffer reports ready at the lesser of 80% of its 250ms target and its 150ms
    // negotiated minimum buffer (#233), so 8 chunks (160ms) start playback.
    private const int ChunksToPlayback = 8;

    private static AudioFormat Pcm(int bitDepth = 16, int sampleRate = SampleRate, int channels = Channels) =>
        new AudioFormat { Codec = "pcm", SampleRate = sampleRate, Channels = channels, BitDepth = bitDepth };

    [Fact]
    public async Task StartAsync_FromIdle_StartsCold()
    {
        await using var harness = new Harness();

        await harness.Pipeline.StartAsync(Pcm());

        Assert.Equal(
            new[] { AudioPipelineState.Starting, AudioPipelineState.Buffering },
            harness.States);
        Assert.Single(harness.Players);
        Assert.Single(harness.Buffers);
        Assert.Equal(1, harness.Player.InitializeCalls);
        Assert.True(harness.Pipeline.IsReady);
    }

    [Fact]
    public async Task StartAsync_ReAnnouncingRunningFormat_KeepsBufferedAudio()
    {
        await using var harness = new Harness();
        await harness.Pipeline.StartAsync(Pcm());
        harness.Feed(5);

        var buffered = harness.Buffer.BufferedMilliseconds;
        var buffer = harness.Buffer;
        harness.States.Clear();

        // Same configuration, different instance — what a re-sent stream/start deserializes to.
        await harness.Pipeline.StartAsync(Pcm());

        Assert.Same(buffer, harness.Buffer);
        Assert.Single(harness.Buffers);
        Assert.Single(harness.Players);
        Assert.Equal(1, harness.Player.InitializeCalls);
        Assert.Equal(buffered, harness.Buffer.BufferedMilliseconds);
        Assert.Empty(harness.States);
    }

    [Fact]
    public async Task StartAsync_ReAnnouncingRunningFormatWhilePlaying_DoesNotReapplyStartupLead()
    {
        await using var harness = new Harness();
        await harness.Pipeline.StartAsync(Pcm());
        harness.Feed(ChunksToPlayback);

        Assert.Equal(AudioPipelineState.Playing, harness.Pipeline.State);
        var buffered = harness.Buffer.BufferedMilliseconds;
        harness.States.Clear();

        await harness.Pipeline.StartAsync(Pcm());

        // Back to Buffering would mean re-buffering to the readiness gate before audio flows
        // again — the startup lead the server does not re-apply for an in-place update.
        Assert.Equal(AudioPipelineState.Playing, harness.Pipeline.State);
        Assert.Empty(harness.States);
        Assert.Equal(1, harness.Player.PlayCalls);
        Assert.Equal(0, harness.Player.StopCount);
        Assert.Equal(buffered, harness.Buffer.BufferedMilliseconds);
    }

    [Fact]
    public async Task StartAsync_ReAnnouncingRunningFormat_ReportsTheNewlyAnnouncedFormat()
    {
        await using var harness = new Harness();
        await harness.Pipeline.StartAsync(Pcm());

        var announced = Pcm();
        announced.Bitrate = 320; // not part of the decode configuration, so still the same stream
        await harness.Pipeline.StartAsync(announced);

        Assert.Same(announced, harness.Pipeline.CurrentFormat);
        Assert.Single(harness.Buffers);
    }

    [Fact]
    public async Task StartAsync_BitDepthChangeAtSameRateAndChannels_RebuildsDecoderAndKeepsBuffer()
    {
        await using var harness = new Harness();
        await harness.Pipeline.StartAsync(Pcm());
        harness.Feed(5);

        var buffer = harness.Buffer;
        var buffered = harness.Buffer.BufferedMilliseconds;
        harness.States.Clear();

        await harness.Pipeline.StartAsync(Pcm(bitDepth: 24));

        Assert.Same(buffer, harness.Buffer);
        Assert.Single(harness.Buffers);
        Assert.Single(harness.Players);
        Assert.Equal(1, harness.Player.InitializeCalls);
        Assert.Equal(buffered, harness.Buffer.BufferedMilliseconds);
        Assert.Empty(harness.States);
        Assert.Equal(24, harness.Pipeline.CurrentFormat?.BitDepth);

        // One 24-bit chunk must add exactly one chunk of audio: the retired 16-bit decoder would
        // have read the same bytes as 1.5 chunks of samples.
        harness.Feed(1, bitDepth: 24);
        Assert.Equal(buffered + ChunkMs, harness.Buffer.BufferedMilliseconds);
    }

    [Fact]
    public async Task StartAsync_CodecHeaderChangeAtSameRateAndChannels_KeepsPipelineAndBuffer()
    {
        await using var harness = new Harness();
        var flac = new AudioFormat
        {
            Codec = "flac",
            SampleRate = SampleRate,
            Channels = Channels,
            BitDepth = 16,
            CodecHeader = Convert.ToBase64String(new byte[] { 1, 2, 3 }),
        };

        await harness.Pipeline.StartAsync(flac);

        var buffer = harness.Buffer;
        harness.States.Clear();

        var next = new AudioFormat
        {
            Codec = "flac",
            SampleRate = SampleRate,
            Channels = Channels,
            BitDepth = 16,
            CodecHeader = Convert.ToBase64String(new byte[] { 4, 5, 6 }),
        };

        await harness.Pipeline.StartAsync(next);

        Assert.Same(buffer, harness.Buffer);
        Assert.Single(harness.Players);
        Assert.Empty(harness.States);
        Assert.Same(next, harness.Pipeline.CurrentFormat);
    }

    [Fact]
    public async Task StartAsync_SampleRateChange_RestartsPipeline()
    {
        await using var harness = new Harness();
        await harness.Pipeline.StartAsync(Pcm());
        harness.Feed(5);

        var firstPlayer = harness.Player;
        harness.States.Clear();

        await harness.Pipeline.StartAsync(Pcm(sampleRate: 44_100));

        // The documented deviation: buffered audio here is already-decoded PCM at the old rate,
        // so the restart drops it rather than resampling it into the new buffer.
        Assert.Equal(2, harness.Buffers.Count);
        Assert.Equal(2, harness.Players.Count);
        Assert.Equal(
            new[]
            {
                AudioPipelineState.Stopping,
                AudioPipelineState.Idle,
                AudioPipelineState.Starting,
                AudioPipelineState.Buffering,
            },
            harness.States);
        Assert.Equal(0, harness.Buffer.BufferedMilliseconds);
        Assert.True(firstPlayer.Disposed);
        Assert.Equal(44_100, harness.Pipeline.CurrentFormat?.SampleRate);
    }

    [Fact]
    public async Task StartAsync_ChannelChange_RestartsPipeline()
    {
        await using var harness = new Harness();
        await harness.Pipeline.StartAsync(Pcm());
        harness.Feed(5);

        await harness.Pipeline.StartAsync(Pcm(channels: 1));

        Assert.Equal(2, harness.Buffers.Count);
        Assert.Equal(0, harness.Buffer.BufferedMilliseconds);
    }

    [Fact]
    public async Task StartAsync_ReportsWhichOfTheThreePathsItTook()
    {
        // The decision is the pipeline's, and the caller acts on the answer rather than
        // re-deriving it from State and CurrentFormat: a client holding chunks still encoded for
        // the previous stream can keep them only for the first of these three.
        await using var harness = new Harness();

        Assert.Equal(AudioPipelineStartOutcome.Restarted, await harness.Pipeline.StartAsync(Pcm()));
        Assert.Equal(
            AudioPipelineStartOutcome.FormatReannounced, await harness.Pipeline.StartAsync(Pcm()));
        Assert.Equal(
            AudioPipelineStartOutcome.DecoderReplaced,
            await harness.Pipeline.StartAsync(Pcm(bitDepth: 24)));
        Assert.Equal(
            AudioPipelineStartOutcome.Restarted,
            await harness.Pipeline.StartAsync(Pcm(bitDepth: 24, sampleRate: 44_100)));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private long _nextTimestamp = 1_000_000;

        public Harness()
        {
            Pipeline = new AudioPipeline(
                NullLogger<AudioPipeline>.Instance,
                new AudioDecoderFactory(),
                new FakeClockSynchronizer { HasMinimalSync = true, IsConverged = true },
                (format, clockSync) =>
                {
                    var buffer = new TimedAudioBuffer(format, clockSync, bufferCapacityMs: 2000);
                    Buffers.Add(buffer);
                    return buffer;
                },
                () =>
                {
                    var player = new StubAudioPlayer();
                    Players.Add(player);
                    return player;
                },
                (buffer, _) => new StubSampleSource(buffer),
                precisionTimer: new StubTimer(),
                useMonotonicTimer: false);

            Pipeline.StateChanged += (_, state) => States.Add(state);
        }

        public AudioPipeline Pipeline { get; }

        public List<TimedAudioBuffer> Buffers { get; } = new List<TimedAudioBuffer>();

        public List<StubAudioPlayer> Players { get; } = new List<StubAudioPlayer>();

        public List<AudioPipelineState> States { get; } = new List<AudioPipelineState>();

        public TimedAudioBuffer Buffer => Buffers[^1];

        public StubAudioPlayer Player => Players[^1];

        /// <summary>Feeds <paramref name="chunkCount"/> chunks of silence of one chunk duration each.</summary>
        public void Feed(int chunkCount, int bitDepth = 16)
        {
            var encoded = new byte[ChunkSamples * (bitDepth / 8)];
            for (var i = 0; i < chunkCount; i++)
            {
                Pipeline.ProcessAudioChunk(new AudioChunk { EncodedData = encoded, ServerTimestamp = _nextTimestamp });
                _nextTimestamp += ChunkMs * 1000L;
            }
        }

        public ValueTask DisposeAsync() => Pipeline.DisposeAsync();
    }

    private sealed class StubTimer : IHighPrecisionTimer
    {
        public long GetCurrentTimeMicroseconds() => 0;

        public long GetElapsedMicroseconds(long fromTimeMicroseconds) => 0;
    }

    private sealed class StubSampleSource : IAudioSampleSource
    {
        private readonly ITimedAudioBuffer _buffer;

        internal StubSampleSource(ITimedAudioBuffer buffer) => _buffer = buffer;

        public AudioFormat Format => _buffer.Format;

        public int Read(float[] buffer, int offset, int count) => 0;
    }

    /// <summary>Counts the lifecycle calls a restart makes and an in-place update must not.</summary>
    private sealed class StubAudioPlayer : IAudioPlayer
    {
        /// <summary>Never raised: nothing under test observes the player's own signalling.</summary>
        event EventHandler<AudioPlayerState>? IAudioPlayer.StateChanged
        {
            add => _ = value;
            remove => _ = value;
        }

        /// <summary>Never raised: nothing under test observes the player's own signalling.</summary>
        event EventHandler<AudioPlayerError>? IAudioPlayer.ErrorOccurred
        {
            add => _ = value;
            remove => _ = value;
        }

        public AudioPlayerState State { get; private set; } = AudioPlayerState.Uninitialized;

        public float Volume { get; set; }

        public bool IsMuted { get; set; }

        public int OutputLatencyMs => 0;

        public int InitializeCalls { get; private set; }

        public int PlayCalls { get; private set; }

        public int StopCount { get; private set; }

        public bool Disposed { get; private set; }

        public Task InitializeAsync(AudioFormat format, CancellationToken cancellationToken = default)
        {
            InitializeCalls++;
            State = AudioPlayerState.Stopped;
            return Task.CompletedTask;
        }

        public void SetSampleSource(IAudioSampleSource source)
        {
        }

        public void Play()
        {
            PlayCalls++;
            State = AudioPlayerState.Playing;
        }

        public void Pause() => State = AudioPlayerState.Paused;

        public void Stop()
        {
            StopCount++;
            State = AudioPlayerState.Stopped;
        }

        public Task SwitchDeviceAsync(string? deviceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
