using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Tests.Audio;

/// <summary>
/// Two lifecycle calls overlapping. The pipeline's start, stop, device switch and dispose all
/// yield — a real device backend's <see cref="IAudioPlayer.InitializeAsync"/> and
/// <see cref="IAsyncDisposable.DisposeAsync"/> take milliseconds — and the callers do not take
/// turns: <c>stream/start</c> and <c>stream/end</c> are handled off the receive loop, and an app
/// can dispose the client from its own thread at any moment.
/// </summary>
/// <remarks>
/// <para>
/// The shipped fakes elsewhere in the suite complete every player call synchronously, which
/// leaves that whole class of interleaving unreachable: a start could never be observed
/// mid-flight, so nothing could be observed racing it. <see cref="GatedAudioPlayer"/> exists to
/// open that seam — its <c>InitializeAsync</c> and <c>DisposeAsync</c> park on a
/// <see cref="TaskCompletionSource"/> the test completes — so these tests pin the interleavings
/// deterministically rather than by timing.
/// </para>
/// </remarks>
public class AudioPipelineConcurrencyTests
{
    private const int SampleRate = 48_000;

    private static AudioFormat Pcm(int sampleRate = SampleRate, int channels = 2) =>
        new AudioFormat { Codec = "pcm", SampleRate = sampleRate, Channels = channels, BitDepth = 16 };

    /// <summary>
    /// A disposed buffer rejects reads and nothing else about it is observable, so this is how a
    /// test asks whether the pipeline let go of one.
    /// </summary>
    private static bool IsDisposed(TimedAudioBuffer buffer)
    {
        try
        {
            buffer.Read(new float[2], 0);
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    [Fact]
    public async Task ASecondStart_WaitsForTheFirst_AndItsStreamIsTheOneLeftRunning()
    {
        // The interleaving a track boundary produces: stream/end + stream/start, both handled off
        // the receive loop. Ungated, the second call's teardown disposed the components the first
        // was still building — the first then resumed onto a null player, took the catch, and its
        // cleanup destroyed the second call's freshly built chain instead.
        var harness = new Harness();

        var first = harness.Pipeline.StartAsync(Pcm());
        var player1 = await harness.NextPlayerAsync();
        await player1.Entered;

        var second = harness.Pipeline.StartAsync(Pcm(sampleRate: 44_100));

        // Nothing of the second start has run: it is behind the first, not interleaved with it.
        Assert.False(second.IsCompleted);
        Assert.Equal(1, harness.PlayersCreated);

        player1.ReleaseInitialize();
        Assert.Equal(AudioPipelineStartOutcome.Restarted, await first);

        var player2 = await harness.NextPlayerAsync();
        await player2.Entered;
        player2.ReleaseInitialize();
        Assert.Equal(AudioPipelineStartOutcome.Restarted, await second);

        Assert.Equal(AudioPipelineState.Buffering, harness.Pipeline.State);
        Assert.Equal(44_100, harness.Pipeline.CurrentFormat!.SampleRate);
        Assert.True(player1.Disposed);
        Assert.False(player2.Disposed);

        // The decisive one: the first start's tail must not have attached its own sample source
        // to the second start's player, which is what it did when the two ran interleaved.
        Assert.Equal(1, player2.SampleSourcesAttached);
        Assert.Equal(2, harness.PlayersCreated);

        await harness.DisposeAsync();
    }

    [Fact]
    public async Task AStopIssuedDuringAStart_TakesEffectAfterIt()
    {
        // stream/end arriving while the stream/start before it is still initializing the device.
        // Ungated, the stop tore the half-built chain down and the start resumed onto a null
        // player: NullReferenceException, cleanup, Error state — and a silent player until the
        // next stream/start, with both exceptions swallowed by the fire-and-forget boundary.
        var harness = new Harness();

        var start = harness.Pipeline.StartAsync(Pcm());
        var player = await harness.NextPlayerAsync();
        await player.Entered;

        var stop = harness.Pipeline.StopAsync();
        Assert.False(stop.IsCompleted);

        player.ReleaseInitialize();
        Assert.Equal(AudioPipelineStartOutcome.Restarted, await start);
        await stop;

        Assert.Equal(AudioPipelineState.Idle, harness.Pipeline.State);
        Assert.True(player.Disposed);

        await harness.DisposeAsync();
    }

    [Fact]
    public async Task AStartIssuedDuringAStop_SeesTheStopFinished()
    {
        // The other order, and the one that reaches the teardown's own yield: CleanupAsync awaits
        // the player's DisposeAsync, which on a real backend closes a device.
        var harness = new Harness();

        var first = harness.Pipeline.StartAsync(Pcm());
        var player1 = await harness.NextPlayerAsync();
        await player1.Entered;
        player1.ReleaseInitialize();
        await first;

        player1.HoldDispose();
        var stop = harness.Pipeline.StopAsync();
        await player1.DisposeEntered;

        var restart = harness.Pipeline.StartAsync(Pcm());
        Assert.False(restart.IsCompleted);
        Assert.Equal(1, harness.PlayersCreated);

        player1.ReleaseDispose();
        await stop;

        var player2 = await harness.NextPlayerAsync();
        await player2.Entered;
        player2.ReleaseInitialize();
        await restart;

        Assert.Equal(AudioPipelineState.Buffering, harness.Pipeline.State);
        Assert.True(player1.Disposed);
        Assert.False(player2.Disposed);

        await harness.DisposeAsync();
    }

    [Fact]
    public async Task DisposeIssuedDuringAStart_TearsDownWhatTheStartBuilt()
    {
        // An app disposing the client while a stream/start is in flight. The dispose must not
        // overtake the start and leave its player and ring behind, alive and unreachable.
        var harness = new Harness();

        var start = harness.Pipeline.StartAsync(Pcm());
        var player = await harness.NextPlayerAsync();
        await player.Entered;

        var dispose = harness.Pipeline.DisposeAsync();
        Assert.False(dispose.IsCompleted);

        player.ReleaseInitialize();
        Assert.Equal(AudioPipelineStartOutcome.Restarted, await start);
        await dispose;

        Assert.True(player.Disposed);
        Assert.Equal(AudioPipelineState.Idle, harness.Pipeline.State);
        Assert.True(IsDisposed(harness.Buffers[0]));
    }

    [Fact]
    public async Task AStartAfterDispose_BuildsNothing()
    {
        // The continuation side of the same race: a stream/start that reaches the pipeline after
        // DisposeAsync has run must not build a decoder, a ring and a device behind its back.
        var harness = new Harness();
        await harness.Pipeline.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => harness.Pipeline.StartAsync(Pcm()));

        Assert.Equal(0, harness.PlayersCreated);
        Assert.Equal(AudioPipelineState.Idle, harness.Pipeline.State);
    }

    [Fact]
    public async Task DisposeIsIdempotent()
    {
        var harness = new Harness();
        var first = harness.Pipeline.StartAsync(Pcm());
        var player = await harness.NextPlayerAsync();
        await player.Entered;
        player.ReleaseInitialize();
        await first;

        await harness.Pipeline.DisposeAsync();
        await harness.Pipeline.DisposeAsync();

        Assert.True(player.Disposed);
        Assert.Equal(1, player.DisposeCount);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly Channel<GatedAudioPlayer> _players = Channel.CreateUnbounded<GatedAudioPlayer>();
        private int _playersCreated;

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
                    var player = new GatedAudioPlayer();
                    Interlocked.Increment(ref _playersCreated);
                    _players.Writer.TryWrite(player);
                    return player;
                },
                (buffer, _) => new PlainSampleSource(buffer),
                precisionTimer: new StubTimer(),
                useMonotonicTimer: false);
        }

        public AudioPipeline Pipeline { get; }

        public List<TimedAudioBuffer> Buffers { get; } = new();

        public int PlayersCreated => Volatile.Read(ref _playersCreated);

        /// <summary>
        /// The next player the pipeline built, waited for rather than polled. The timeout turns a
        /// player the pipeline never builds into a failure instead of a hung run.
        /// </summary>
        public async Task<GatedAudioPlayer> NextPlayerAsync() =>
            await _players.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        public ValueTask DisposeAsync() => Pipeline.DisposeAsync();
    }

    /// <summary>
    /// A player whose two yielding calls park until the test lets them through, which is the seam
    /// every interleaving here is built on.
    /// </summary>
    private sealed class GatedAudioPlayer : IAudioPlayer
    {
        private readonly TaskCompletionSource _initializeEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _initializeReleased =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _disposeEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource? _disposeReleased;

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

        /// <summary>Completes when <see cref="InitializeAsync"/> has been entered and is parked.</summary>
        public Task Entered => _initializeEntered.Task;

        /// <summary>Completes when <see cref="DisposeAsync"/> has been entered and is parked.</summary>
        public Task DisposeEntered => _disposeEntered.Task;

        public bool Disposed { get; private set; }

        public int DisposeCount { get; private set; }

        public int SampleSourcesAttached { get; private set; }

        public AudioPlayerState State { get; private set; } = AudioPlayerState.Uninitialized;

        public float Volume { get; set; }

        public bool IsMuted { get; set; }

        public int OutputLatencyMs => 0;

        public async Task InitializeAsync(AudioFormat format, CancellationToken cancellationToken = default)
        {
            _initializeEntered.TrySetResult();
            await _initializeReleased.Task;
            State = AudioPlayerState.Stopped;
        }

        public void ReleaseInitialize() => _initializeReleased.TrySetResult();

        /// <summary>Parks the next <see cref="DisposeAsync"/> until <see cref="ReleaseDispose"/>.</summary>
        public void HoldDispose() =>
            _disposeReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseDispose() => _disposeReleased?.TrySetResult();

        public void SetSampleSource(IAudioSampleSource source) => SampleSourcesAttached++;

        public void Play() => State = AudioPlayerState.Playing;

        public void Pause() => State = AudioPlayerState.Paused;

        public void Stop() => State = AudioPlayerState.Stopped;

        public Task SwitchDeviceAsync(string? deviceId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public async ValueTask DisposeAsync()
        {
            _disposeEntered.TrySetResult();
            if (_disposeReleased is { } held)
            {
                await held.Task;
            }

            DisposeCount++;
            Disposed = true;
        }
    }

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
}
