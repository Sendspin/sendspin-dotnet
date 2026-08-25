using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Client;
using Sendspin.SDK.Models;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Tests.Audio;

/// <summary>
/// <c>min_buffer_ms</c> is one number with two jobs: what the server is asked to keep queued,
/// and what the buffer waits for before starting. These pin that the buffer's readiness gate
/// actually follows the advertised value instead of its own default, and that the two defaults
/// are one constant rather than two that have to be kept in step by hand.
/// </summary>
public class MinBufferWiringTests
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int SamplesPerMs = SampleRate * Channels / 1000;

    private static readonly AudioFormat Format = new()
    {
        Codec = "pcm", SampleRate = SampleRate, Channels = Channels, BitDepth = 16,
    };

    [Fact]
    public void TheTwoDefaults_AreTheSameConstant()
    {
        // They used to be two hand-written 150s. Anyone changing one had no way to know the
        // other existed, and the readiness gate silently stopped matching the advertisement.
        using var buffer = new TimedAudioBuffer(Format, new FakeClockSynchronizer());

        Assert.Equal(PlayerBufferCapacity.DefaultMinBufferMilliseconds, buffer.MinBufferMilliseconds);
        Assert.Equal(PlayerBufferCapacity.DefaultMinBufferMilliseconds, new ClientCapabilities().MinBufferMs);
        Assert.Equal(150, PlayerBufferCapacity.DefaultMinBufferMilliseconds);
    }

    [Fact]
    public void ReadinessGate_TakesTheLesserOfTheTargetDepthAndTheAdvertisedMinimum()
    {
        // Advertising 500 ms asks the server to keep half a second queued, so the gate must not
        // release at the 200 ms that 80% of the target depth allows — the client would start
        // before the audio it told the server it needs has arrived.
        using var buffer = new TimedAudioBuffer(Format, new FakeClockSynchronizer())
        {
            TargetBufferMilliseconds = 250,
            MinBufferMilliseconds = 500,
        };

        var chunk = new float[10 * SamplesPerMs];
        for (var i = 0; i < 19; i++)
        {
            buffer.Write(chunk, i * 10_000L);
        }

        Assert.Equal(190, buffer.BufferedMilliseconds);
        Assert.False(buffer.IsReadyForPlayback, "below 80% of the 250 ms target depth");

        buffer.Write(chunk, 19 * 10_000L);

        Assert.Equal(200, buffer.BufferedMilliseconds);
        Assert.True(buffer.IsReadyForPlayback, "80% of the target depth is the binding gate here");
    }

    [Fact]
    public async Task Pipeline_AppliesTheAdvertisedMinimumToEveryBufferItStarts()
    {
        await using var harness = new Harness();
        harness.Pipeline.SetMinBufferMilliseconds(500);

        await harness.Pipeline.StartAsync(Format);

        Assert.Equal(500, harness.Buffers[^1].MinBufferMilliseconds);
    }

    [Fact]
    public async Task Pipeline_AppliesAMidStreamChangeToTheRunningBuffer()
    {
        // The spec lets a client update its timing parameters at any time
        // (roles/player/v1.md:68), and ISendspinClient.UpdateTimingAsync is that path.
        await using var harness = new Harness();
        await harness.Pipeline.StartAsync(Format);

        harness.Pipeline.SetMinBufferMilliseconds(400);

        Assert.Equal(400, harness.Buffers[^1].MinBufferMilliseconds);
    }

    [Fact]
    public async Task Pipeline_LeavesTheFactorysValueAloneUntilToldOtherwise()
    {
        // An app that configures its own buffer must not have the SDK's default written over
        // the top of it just because nothing advertised a value.
        await using var harness = new Harness(minBufferMs: 320);

        await harness.Pipeline.StartAsync(Format);

        Assert.Equal(320, harness.Buffers[^1].MinBufferMilliseconds);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(double? minBufferMs = null)
        {
            Pipeline = new AudioPipeline(
                NullLogger<AudioPipeline>.Instance,
                new AudioDecoderFactory(),
                new FakeClockSynchronizer { HasMinimalSync = true, IsConverged = true },
                (format, clockSync) =>
                {
                    var buffer = new TimedAudioBuffer(format, clockSync, bufferCapacityMs: 2000);
                    if (minBufferMs.HasValue)
                    {
                        buffer.MinBufferMilliseconds = minBufferMs.Value;
                    }

                    Buffers.Add(buffer);
                    return buffer;
                },
                () => new SilentAudioPlayer(),
                (buffer, _) => new SilentSampleSource(buffer),
                precisionTimer: new ZeroTimer(),
                useMonotonicTimer: false);
        }

        public AudioPipeline Pipeline { get; }

        public List<TimedAudioBuffer> Buffers { get; } = new();

        public ValueTask DisposeAsync() => Pipeline.DisposeAsync();
    }

    private sealed class ZeroTimer : IHighPrecisionTimer
    {
        public long GetCurrentTimeMicroseconds() => 0;

        public long GetElapsedMicroseconds(long fromTimeMicroseconds) => 0;
    }

    private sealed class SilentSampleSource : IAudioSampleSource
    {
        private readonly ITimedAudioBuffer _buffer;

        internal SilentSampleSource(ITimedAudioBuffer buffer) => _buffer = buffer;

        public AudioFormat Format => _buffer.Format;

        public int Read(float[] buffer, int offset, int count) => 0;
    }

    private sealed class SilentAudioPlayer : IAudioPlayer
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
