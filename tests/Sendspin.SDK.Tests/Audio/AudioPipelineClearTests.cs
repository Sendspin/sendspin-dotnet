using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Tests.Audio;

/// <summary>
/// What clearing does to the decoder, and when. <see cref="AudioPipeline.Clear"/> has no thread of
/// its own — a <c>stream/clear</c> arrives on the client's stream-lifecycle chain and a re-anchor
/// is raised from a pool thread — while the receive loop may be inside the decoder at that moment.
/// Only Opus carries state across frames, and Concentus documents its decoder as single-threaded.
/// </summary>
public class AudioPipelineClearTests
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int SamplesPerMs = SampleRate * Channels / 1000;
    private const int ChunkMs = 20;
    private const long ServerT0 = 1_000_000;

    // Local time runs well past the buffer's 5 s re-anchor cooldown, which is measured from zero:
    // a timeline starting near it suppresses the first re-anchor outright.
    private const long LocalT0 = 9_000_000_000_000;

    private static AudioFormat Pcm() =>
        new AudioFormat { Codec = "pcm", SampleRate = SampleRate, Channels = Channels, BitDepth = 16 };

    [Fact]
    public async Task Clear_DoesNotResetTheDecoderOnTheCallingThread()
    {
        // The call that asks for the reset can be running on any thread. Taking it there raced
        // the receive loop's Decode inside a decoder documented as single-threaded.
        await using var harness = new Harness();
        await harness.Pipeline.StartAsync(Pcm());
        harness.Feed(1);

        harness.Pipeline.Clear();

        Assert.Equal(0, harness.Decoder.ResetCount);
    }

    [Fact]
    public async Task Clear_ResetsTheDecoderBeforeTheNextFrameIsDecoded()
    {
        // Deferred, not dropped: the seek's next packet comes from a new position, so the
        // decoder's inter-frame state must be gone before it is decoded.
        await using var harness = new Harness();
        await harness.Pipeline.StartAsync(Pcm());
        harness.Feed(1);

        harness.Pipeline.Clear();
        harness.Feed(1);

        Assert.Equal(1, harness.Decoder.ResetCount);
        Assert.Equal(new[] { "decode", "reset", "decode" }, harness.Decoder.CallLog);
    }

    [Fact]
    public async Task Clear_ResetsTheDecoderOnceHoweverManyFramesFollow()
    {
        await using var harness = new Harness();
        await harness.Pipeline.StartAsync(Pcm());

        harness.Pipeline.Clear();
        harness.Feed(3);

        Assert.Equal(1, harness.Decoder.ResetCount);
    }

    [Fact]
    public async Task AReanchor_LeavesTheDecoderAlone()
    {
        // A re-anchor discards audio this decoder has already produced and then carries on with
        // the next packet of the same stream — nothing is skipped on the encoded side. Resetting
        // would throw away inter-frame state that is still exactly right for the packet about to
        // arrive, and manufacture a discontinuity at the resume where the codec had none.
        await using var harness = new Harness();
        await harness.Pipeline.StartAsync(Pcm());
        harness.Feed(10);

        await harness.ForceReanchorAsync();

        Assert.Equal(0, harness.Decoder.ResetCount);

        // ...and the next packet is decoded straight on, with no reset waiting for it either.
        harness.Feed(1);
        Assert.Equal(0, harness.Decoder.ResetCount);
    }

    [Fact]
    public async Task Clear_ArmsTheReadinessGateEvenWhenTheBufferThrows()
    {
        // The pipeline left reporting Playing over an emptied ring is permanent silence: the
        // readiness gate in ProcessAudioChunk only restarts playback from Buffering. Before the
        // gate re-arm moved into a finally, a throw anywhere above it — an OpusDecoder.Reset on
        // a decoder a concurrent teardown had disposed being the real one — left exactly that.
        await using var harness = new Harness(throwingBufferClear: true);
        await harness.Pipeline.StartAsync(Pcm());
        harness.Feed(10);

        Assert.Equal(AudioPipelineState.Playing, harness.Pipeline.State);

        Assert.Throws<InvalidOperationException>(() => harness.Pipeline.Clear());

        Assert.Equal(AudioPipelineState.Buffering, harness.Pipeline.State);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly TaskCompletionSource _reanchorCleared =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private long _nextTimestamp = ServerT0;
        private bool _wasPlaying;

        public Harness(bool throwingBufferClear = false)
        {
            Pipeline = new AudioPipeline(
                NullLogger<AudioPipeline>.Instance,
                new CountingDecoderFactory(Decoder),
                new FakeClockSynchronizer
                {
                    HasMinimalSync = true,
                    IsConverged = true,
                    OffsetMicroseconds = ServerT0 - LocalT0,
                },
                (format, clockSync) =>
                {
                    Buffer = new TimedAudioBuffer(format, clockSync, bufferCapacityMs: 5_000);
                    return throwingBufferClear ? new ThrowingClearBuffer(Buffer) : Buffer;
                },
                () => new StubAudioPlayer(),
                (buffer, _) => new PlainSampleSource(buffer),
                precisionTimer: new StubTimer(),
                useMonotonicTimer: false);

            // The re-anchor's clear runs on a pool thread, so the wait below is on the state
            // transition it produces rather than on a delay.
            Pipeline.StateChanged += (_, state) =>
            {
                if (state == AudioPipelineState.Playing)
                {
                    _wasPlaying = true;
                }
                else if (state == AudioPipelineState.Buffering && _wasPlaying)
                {
                    _reanchorCleared.TrySetResult();
                }
            };
        }

        public AudioPipeline Pipeline { get; }

        public CountingDecoder Decoder { get; } = new();

        public TimedAudioBuffer? Buffer { get; private set; }

        /// <summary>Feeds <paramref name="chunkCount"/> chunks of silence, one chunk long each.</summary>
        public void Feed(int chunkCount)
        {
            var encoded = new byte[ChunkMs * SamplesPerMs * 2];
            for (var i = 0; i < chunkCount; i++)
            {
                Pipeline.ProcessAudioChunk(
                    new AudioChunk { EncodedData = encoded, ServerTimestamp = _nextTimestamp });
                _nextTimestamp += ChunkMs * 1000L;
            }
        }

        /// <summary>
        /// Reads far enough past the buffered audio's schedule that discarding every stale segment
        /// still leaves an error past the re-anchor threshold, which is what makes the buffer raise
        /// ReanchorRequired.
        /// </summary>
        public async Task ForceReanchorAsync()
        {
            var output = new float[SamplesPerMs * 10];

            // The first read requests the re-anchor; the one after it emits silence and raises
            // ReanchorRequired, which is the call the pipeline is subscribed to.
            Buffer!.Read(output, LocalT0 + 1_500_000);
            Buffer.Read(output, LocalT0 + 1_510_000);

            await _reanchorCleared.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }

        public ValueTask DisposeAsync() => Pipeline.DisposeAsync();
    }

    private sealed class CountingDecoderFactory : IAudioDecoderFactory
    {
        private readonly CountingDecoder _decoder;

        internal CountingDecoderFactory(CountingDecoder decoder) => _decoder = decoder;

        public IAudioDecoder Create(AudioFormat format)
        {
            _decoder.Format = format;
            return _decoder;
        }

        public bool IsSupported(string codec) => true;
    }

    /// <summary>
    /// Decodes silence and records what it was asked to do, in order — which is the whole point:
    /// a reset that lands after the frame it was meant to precede is not a reset at all.
    /// </summary>
    private sealed class CountingDecoder : IAudioDecoder
    {
        private readonly List<string> _callLog = new();

        public AudioFormat Format { get; set; } = new AudioFormat { Codec = "pcm", SampleRate = SampleRate, Channels = Channels };

        public int MaxSamplesPerFrame => ChunkMs * SamplesPerMs;

        public int ResetCount { get; private set; }

        public IReadOnlyList<string> CallLog => _callLog.ToList();

        public int Decode(ReadOnlySpan<byte> encodedData, Span<float> decodedSamples)
        {
            _callLog.Add("decode");
            var count = Math.Min(MaxSamplesPerFrame, decodedSamples.Length);
            decodedSamples[..count].Clear();
            return count;
        }

        public void Reset()
        {
            ResetCount++;
            _callLog.Add("reset");
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Stands in for anything above the gate re-arm that can throw. On 9.x the real one is
    /// <c>OpusDecoder.Reset</c> raising <see cref="ObjectDisposedException"/> against a decoder a
    /// concurrent teardown disposed; the decoder reset now happens elsewhere, so the buffer's own
    /// Clear plays that part here.
    /// </summary>
    private sealed class ThrowingClearBuffer : ITimedAudioBuffer
    {
        private readonly ITimedAudioBuffer _inner;

        internal ThrowingClearBuffer(ITimedAudioBuffer inner) => _inner = inner;

        public AudioFormat Format => _inner.Format;

        public SyncCorrectionOptions SyncOptions => _inner.SyncOptions;

        public double BufferedMilliseconds => _inner.BufferedMilliseconds;

        public bool IsReadyForPlayback => _inner.IsReadyForPlayback;

        public double TargetBufferMilliseconds
        {
            get => _inner.TargetBufferMilliseconds;
            set => _inner.TargetBufferMilliseconds = value;
        }

        public long OutputLatencyMicroseconds
        {
            get => _inner.OutputLatencyMicroseconds;
            set => _inner.OutputLatencyMicroseconds = value;
        }

        public long CalibratedStartupLatencyMicroseconds
        {
            get => _inner.CalibratedStartupLatencyMicroseconds;
            set => _inner.CalibratedStartupLatencyMicroseconds = value;
        }

        public string? TimingSourceName
        {
            get => _inner.TimingSourceName;
            set => _inner.TimingSourceName = value;
        }

        public long SyncErrorMicroseconds => _inner.SyncErrorMicroseconds;

        public double SmoothedSyncErrorMicroseconds => _inner.SmoothedSyncErrorMicroseconds;

#pragma warning disable CS0618 // exercising the obsolete surface the pipeline still drives
        public double TargetPlaybackRate => _inner.TargetPlaybackRate;

        public event Action<double>? TargetPlaybackRateChanged
        {
            add => _inner.TargetPlaybackRateChanged += value;
            remove => _inner.TargetPlaybackRateChanged -= value;
        }

        public int Read(Span<float> buffer, long currentLocalTime) => _inner.Read(buffer, currentLocalTime);
#pragma warning restore CS0618

        public void Write(ReadOnlySpan<float> samples, long serverTimestamp) => _inner.Write(samples, serverTimestamp);

        public int ReadRaw(Span<float> buffer, long currentLocalTime) => _inner.ReadRaw(buffer, currentLocalTime);

        public void NotifyExternalCorrection(int samplesDropped, int samplesInserted) =>
            _inner.NotifyExternalCorrection(samplesDropped, samplesInserted);

        public void ReportExternalPlaybackRate(double rate) => _inner.ReportExternalPlaybackRate(rate);

        public void NotifyReconnect() => _inner.NotifyReconnect();

        public void Clear() => throw new InvalidOperationException("buffer clear failed");

        public AudioBufferStats GetStats() => _inner.GetStats();

        public void Dispose() => _inner.Dispose();
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
