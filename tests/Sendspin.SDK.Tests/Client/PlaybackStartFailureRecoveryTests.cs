using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Client;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Synchronization;
using Sendspin.SDK.Tests.Audio;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// A failed playback start leaves the pipeline in Buffering with an error reported, and its only
/// retry is the readiness gate in <see cref="AudioPipeline.ProcessAudioChunk"/>. The client's
/// discard of audio while unavailable (spec #270) must therefore not apply when the pipeline's own
/// error is the only reason it is unavailable, or the chunk that would recover it never arrives.
/// Uses the real <see cref="AudioPipeline"/>: a fake forced to Playing cannot show the deadlock.
/// </summary>
public class PlaybackStartFailureRecoveryTests
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int ChunkMs = 20;

    // 16-bit PCM: two bytes per sample, per channel.
    private const int ChunkBytes = ChunkMs * SampleRate / 1000 * Channels * 2;

    private static byte[] Chunk(long ts)
    {
        var buf = new byte[13 + ChunkBytes];
        buf[0] = BinaryMessageTypes.PlayerAudio0;
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(1, 8), ts);
        return buf;
    }

    [Fact]
    public async Task AudioAfterAFailedPlaybackStart_ReachesThePipeline_AndRecoversToPlayingAndAvailable()
    {
        var clock = new FakeClockSynchronizer { HasMinimalSync = true, IsConverged = true };
        var player = new FailsFirstPlayAudioPlayer();
        await using var pipeline = new AudioPipeline(
            NullLogger<AudioPipeline>.Instance,
            new AudioDecoderFactory(),
            clock,
            (format, clockSync) => new TimedAudioBuffer(format, clockSync, bufferCapacityMs: 5_000),
            () => player,
            (buffer, _) => new SilentSampleSource(buffer),
            precisionTimer: new StubTimer(),
            useMonotonicTimer: false);
        var (client, connection, _) = TestClient.Create(activated: true, configure: options => options with
        {
            AudioPipeline = pipeline,
            ClockSynchronizer = clock,
        });
        using var _c = client;

        // The activate reset the clock for its connection; this client has synced since.
        clock.HasMinimalSync = true;
        clock.IsConverged = true;
        await pipeline.StartAsync(
            new AudioFormat { Codec = "pcm", SampleRate = SampleRate, Channels = Channels, BitDepth = 16 });

        // Feed until the readiness gate tries to start playback and the device refuses.
        long ts = 1_000_000;
        while (player.PlayCalls == 0)
        {
            connection.RaiseBinaryMessageReceived(Chunk(ts));
            ts += ChunkMs * 1000L;
        }

        Assert.Equal(AudioPipelineState.Buffering, pipeline.State);
        Assert.Equal(false, connection.SentMessages.OfType<ClientStateMessage>().Last().Payload.Available);

        // The stream carries on; the next chunk is what retries the start.
        connection.RaiseBinaryMessageReceived(Chunk(ts));

        Assert.Equal(2, player.PlayCalls);
        Assert.Equal(AudioPipelineState.Playing, pipeline.State);
        Assert.Equal(true, connection.SentMessages.OfType<ClientStateMessage>().Last().Payload.Available);
    }

    private sealed class SilentSampleSource : IAudioSampleSource
    {
        private readonly ITimedAudioBuffer _buffer;

        internal SilentSampleSource(ITimedAudioBuffer buffer) => _buffer = buffer;

        public AudioFormat Format => _buffer.Format;

        public int Read(float[] buffer, int offset, int count) => 0;
    }

    private sealed class StubTimer : IHighPrecisionTimer
    {
        public long GetCurrentTimeMicroseconds() => 0;

        public long GetElapsedMicroseconds(long fromTimeMicroseconds) => 0;
    }

    /// <summary>A device whose first start fails and whose second succeeds.</summary>
    private sealed class FailsFirstPlayAudioPlayer : IAudioPlayer
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

        public int PlayCalls { get; private set; }

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

        public void Play()
        {
            if (++PlayCalls == 1)
            {
                throw new InvalidOperationException("device busy");
            }

            State = AudioPlayerState.Playing;
        }

        public void Pause() => State = AudioPlayerState.Paused;

        public void Stop() => State = AudioPlayerState.Stopped;

        public Task SwitchDeviceAsync(string? deviceId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
