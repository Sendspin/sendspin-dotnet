using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Tests.Audio;

/// <summary>
/// The buffer asks the output how long a sample handed over now will wait, through
/// <see cref="TimedAudioBuffer.CurrentOutputLatency"/>. These pin that the pipeline connects that
/// to whichever player is attached.
/// </summary>
public class AudioPipelineOutputLatencyMeasurementTests
{
    private static readonly AudioFormat Format = new()
    {
        Codec = "pcm", SampleRate = 48_000, Channels = 2, BitDepth = 16,
    };

    [Fact]
    public async Task Buffer_AsksTheAttachedPlayer()
    {
        await using var harness = new Harness();
        await harness.Pipeline.StartAsync(Format);

        var measure = harness.Buffers[^1].CurrentOutputLatency;
        Assert.NotNull(measure);

        harness.Players[^1].CurrentLatencyMicroseconds = 37_000;
        Assert.Equal(37_000, measure());

        harness.Players[^1].CurrentLatencyMicroseconds = 91_000;
        Assert.Equal(91_000, measure());
    }

    [Fact]
    public async Task PlayerThatDoesNotMeasure_LeavesTheReportedLatencyInForce()
    {
        await using var harness = new Harness();
        await harness.Pipeline.StartAsync(Format);

        harness.Players[^1].CurrentLatencyMicroseconds = null;

        Assert.Null(harness.Buffers[^1].CurrentOutputLatency!());
        Assert.Equal(100_000, harness.Buffers[^1].OutputLatencyMicroseconds);
    }

    [Fact]
    public async Task Measurement_FollowsADeviceSwitch()
    {
        await using var harness = new Harness();
        await harness.Pipeline.StartAsync(Format);
        var buffer = harness.Buffers[^1];
        harness.Players[^1].CurrentLatencyMicroseconds = 90_000;

        // The new device opens empty.
        harness.Players[^1].LatencyAfterSwitchMicroseconds = 0;
        await harness.Pipeline.SwitchDeviceAsync("other");

        Assert.Equal(0, buffer.CurrentOutputLatency!());
    }

    [Fact]
    public async Task Measurement_FollowsAReplacedPlayer()
    {
        await using var harness = new Harness();
        await harness.Pipeline.StartAsync(Format);
        harness.Players[^1].CurrentLatencyMicroseconds = 90_000;

        await harness.Pipeline.StopAsync();
        await harness.Pipeline.StartAsync(Format);

        Assert.Equal(2, harness.Players.Count);
        harness.Players[^1].CurrentLatencyMicroseconds = 12_000;
        Assert.Equal(12_000, harness.Buffers[^1].CurrentOutputLatency!());
    }

    private sealed class Harness : IAsyncDisposable
    {
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
                    var player = new MeasuringPlayer();
                    Players.Add(player);
                    return player;
                },
                (buffer, _) => new SilentSampleSource(buffer),
                precisionTimer: new ZeroTimer(),
                useMonotonicTimer: false);
        }

        public AudioPipeline Pipeline { get; }

        public List<TimedAudioBuffer> Buffers { get; } = new();

        public List<MeasuringPlayer> Players { get; } = new();

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

    private sealed class MeasuringPlayer : IAudioPlayer
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

        public int OutputLatencyMs => 100;

        public long? CurrentLatencyMicroseconds { get; set; } = 0;

        public long? LatencyAfterSwitchMicroseconds { get; set; }

        public long? GetCurrentOutputLatencyMicroseconds() => CurrentLatencyMicroseconds;

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

        public Task SwitchDeviceAsync(string? deviceId, CancellationToken cancellationToken = default)
        {
            CurrentLatencyMicroseconds = LatencyAfterSwitchMicroseconds;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
