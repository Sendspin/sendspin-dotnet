using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Tests.Audio;

/// <summary>
/// A <c>stream/start</c> for a stream that is already running is a configuration update, not a
/// restart (#201): the pipeline must keep buffered audio, the running timeline and the readiness
/// gate. These tests pin what is applied in place — a re-announced format, a decode-side change
/// at an unchanged sample rate and channel count, and a sample rate or channel change, which plays
/// out what was buffered before the output moves to the new format.
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

    // A 16-bit sample value no chunk of the first format carries.
    private const short NewFormatValue = 16_000;

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
    public async Task StartAsync_ReAnnouncingFlacWhoseStreamInfoDepthDiffersFromTheAnnouncedOne_IsStillAReannounce()
    {
        // #350: the FLAC decoder takes its scaling from STREAMINFO, which may say 24 where
        // stream/start said 32. It used to write the 24 back into the format it was given — the
        // instance the pipeline compares the next stream/start against — so the same announcement
        // sent again no longer matched and the decoder was rebuilt for nothing.
        await using var harness = new Harness();
        var announced = FlacAnnouncedAs32BitWith24BitStreamInfo();
        await harness.Pipeline.StartAsync(announced);
        harness.Pipeline.ProcessAudioChunk(
            new AudioChunk { EncodedData = Flac24BitFrame(0x400000), ServerTimestamp = 1_000_000 });

        Assert.Equal(ChunkMs, harness.Buffer.BufferedMilliseconds);

        var outcome = await harness.Pipeline.StartAsync(FlacAnnouncedAs32BitWith24BitStreamInfo());

        Assert.Equal(AudioPipelineStartOutcome.FormatReannounced, outcome);
        Assert.Equal(32, announced.BitDepth);
    }

    [Fact]
    public void FlacDecoder_StreamInfoDepthDiffersFromTheAnnouncedOne_ScalesByStreamInfo()
    {
        // The control for the test above: leaving the announced format alone must not cost the
        // calibration. Half of 24-bit full scale is 0.5, and would be 1/512 scaled as 32-bit.
        using var decoder = new Sendspin.SDK.Audio.Codecs.FlacDecoder(FlacAnnouncedAs32BitWith24BitStreamInfo());
        var decoded = new float[decoder.MaxSamplesPerFrame];

        var written = decoder.Decode(Flac24BitFrame(0x400000), decoded);

        Assert.Equal(ChunkSamples, written);
        Assert.All(decoded.AsSpan(0, written).ToArray(), sample => Assert.Equal(0.5f, sample));
    }

    [Fact]
    public async Task StartAsync_SampleRateChange_KeepsBufferedAudioPlaying()
    {
        await using var harness = new Harness();
        await harness.Pipeline.StartAsync(Pcm());
        harness.Feed(5);

        var firstPlayer = harness.Player;
        var firstBuffer = harness.Buffer;
        var buffered = firstBuffer.BufferedMilliseconds;
        harness.States.Clear();

        var outcome = await harness.Pipeline.StartAsync(Pcm(sampleRate: 44_100));

        // Spec: "Clients MUST keep buffered chunks and decode each chunk in the format that was
        // in effect when it was received." The first buffer keeps its audio and its player; the
        // second takes the chunks that follow. Nothing more is coming for the first, so it starts
        // playing without waiting for a readiness gate it can no longer reach.
        Assert.Equal(AudioPipelineStartOutcome.DecoderReplaced, outcome);
        Assert.Equal(2, harness.Buffers.Count);
        Assert.Single(harness.Players);
        Assert.False(firstPlayer.Disposed);
        Assert.Equal(1, firstPlayer.PlayCalls);
        Assert.Equal(buffered, firstBuffer.BufferedMilliseconds);
        Assert.Equal(new[] { AudioPipelineState.Playing }, harness.States);
        Assert.Equal(44_100, harness.Pipeline.CurrentFormat?.SampleRate);
    }

    [Fact]
    public async Task StartAsync_ChannelChange_KeepsBufferedAudioPlaying()
    {
        await using var harness = new Harness();
        await harness.Pipeline.StartAsync(Pcm());
        harness.Feed(5);

        var firstBuffer = harness.Buffer;
        var buffered = firstBuffer.BufferedMilliseconds;

        await harness.Pipeline.StartAsync(Pcm(channels: 1));

        Assert.Equal(2, harness.Buffers.Count);
        Assert.Single(harness.Players);
        Assert.Equal(buffered, firstBuffer.BufferedMilliseconds);
    }

    [Fact]
    public async Task StartAsync_SampleRateChange_PlaysBufferedAudioInOrderBeforeTheNewFormat()
    {
        await using var harness = new Harness();
        await harness.Pipeline.StartAsync(Pcm());

        // Chunk n decodes to n/32.768, so what comes out says which chunk it came from.
        for (var chunk = 1; chunk <= ChunksToPlayback; chunk++)
        {
            harness.Feed(1, value: (short)(chunk * 1000));
        }

        await harness.Pipeline.StartAsync(Pcm(sampleRate: 44_100));
        harness.Feed(ChunksToPlayback, sampleRate: 44_100, value: NewFormatValue);

        var firstPlayer = harness.Player;
        var played = harness.PullUntilEmpty(harness.Buffers[0], harness.Sources[0]);

        Assert.Equal(Enumerable.Range(1, ChunksToPlayback), played);

        await harness.WaitForAsync(() => harness.Players.Count == 2 && harness.Player.PlayCalls == 1);

        // The second output was opened only once the first buffer had nothing left to play.
        Assert.Equal(new[] { 0.0, 0.0 }, harness.FirstBufferLeftWhenOutputOpened);
        Assert.True(firstPlayer.Disposed);
        Assert.Equal(AudioPipelineState.Playing, harness.Pipeline.State);

        played = harness.PullUntilEmpty(harness.Buffers[1], harness.Sources[1]);

        Assert.Equal(new[] { NewFormatValue / 1000 }, played);
    }

    [Fact]
    public async Task StartAsync_SampleRateChangeWithNothingBuffered_Restarts()
    {
        await using var harness = new Harness();
        await harness.Pipeline.StartAsync(Pcm());

        var outcome = await harness.Pipeline.StartAsync(Pcm(sampleRate: 44_100));

        Assert.Equal(AudioPipelineStartOutcome.Restarted, outcome);
        Assert.Equal(2, harness.Players.Count);
        Assert.Equal(AudioPipelineState.Buffering, harness.Pipeline.State);
    }

    [Fact]
    public async Task StartAsync_SampleRateChangeJustAfterTheBufferEmptied_LeavesTheOutputPlayingItsTail()
    {
        await using var harness = new Harness();
        await harness.Pipeline.StartAsync(Pcm());
        harness.Feed(ChunksToPlayback);
        harness.PullUntilEmpty(harness.Buffer, harness.Sources[0]);

        // The ring is empty because its last samples went to the device, which has yet to play
        // them: the end of any track that was not sent ahead.
        var firstPlayer = harness.Player;
        firstPlayer.OutputLatencyMs = 300;

        var outcome = await harness.Pipeline.StartAsync(Pcm(sampleRate: 44_100));

        Assert.Equal(AudioPipelineStartOutcome.DecoderReplaced, outcome);
        Assert.Single(harness.Players);
        Assert.Equal(0, firstPlayer.StopCount);
        Assert.False(firstPlayer.Disposed);

        await harness.WaitForAsync(() => harness.Players.Count == 2 && firstPlayer.Disposed);
    }

    [Fact]
    public async Task StartAsync_SecondSampleRateChangeBeforeTheFirstHasSwitched_Restarts()
    {
        await using var harness = new Harness();
        await harness.Pipeline.StartAsync(Pcm());
        harness.Feed(5);
        await harness.Pipeline.StartAsync(Pcm(sampleRate: 44_100));

        var outcome = await harness.Pipeline.StartAsync(Pcm(sampleRate: 96_000));

        Assert.Equal(AudioPipelineStartOutcome.Restarted, outcome);
        Assert.Equal(2, harness.Players.Count);
        Assert.True(harness.Players[0].Disposed);
        Assert.Equal(96_000, harness.Pipeline.CurrentFormat?.SampleRate);
    }

    [Fact]
    public async Task StartAsync_SecondSampleRateChangeBeforeTheFirstHasSwitched_StopsTheOutputBeforeDisposingItsBuffer()
    {
        await using var harness = new Harness();
        await harness.Pipeline.StartAsync(Pcm());
        harness.Feed(ChunksToPlayback);
        await harness.Pipeline.StartAsync(Pcm(sampleRate: 44_100));
        var read = harness.ReadFirstSourceWhenStopping(harness.Player);

        await harness.Pipeline.StartAsync(Pcm(sampleRate: 96_000));

        Assert.True(read.Done);
        Assert.Null(read.Thrown);
    }

    [Fact]
    public async Task StartAsync_AfterAPlayerErrorDuringASampleRateChange_StopsTheOutputBeforeDisposingItsBuffer()
    {
        await using var harness = new Harness();
        await harness.Pipeline.StartAsync(Pcm());
        harness.Feed(ChunksToPlayback);
        await harness.Pipeline.StartAsync(Pcm(sampleRate: 44_100));
        var read = harness.ReadFirstSourceWhenStopping(harness.Player);
        harness.Player.Fail();

        await harness.Pipeline.StartAsync(Pcm());

        Assert.True(read.Done);
        Assert.Null(read.Thrown);
    }

    [Fact]
    public async Task Clear_BeforeASampleRateChangeHasSwitched_DiscardsTheOldFormatAudioToo()
    {
        await using var harness = new Harness();
        await harness.Pipeline.StartAsync(Pcm());
        harness.Feed(ChunksToPlayback);
        await harness.Pipeline.StartAsync(Pcm(sampleRate: 44_100));
        harness.Feed(1, sampleRate: 44_100);

        harness.Pipeline.Clear();

        Assert.Equal(0, harness.Buffers[0].BufferedMilliseconds);
        Assert.Equal(0, harness.Buffers[1].BufferedMilliseconds);
        Assert.Equal(AudioPipelineState.Buffering, harness.Pipeline.State);

        // With nothing left to play out the output moves straight away, and waits for the
        // readiness gate like any other stream/clear.
        await harness.WaitForAsync(() => harness.Players.Count == 2 && harness.Players[0].Disposed);
        Assert.Equal(0, harness.Player.PlayCalls);
    }

    [Fact]
    public async Task StopAsync_BeforeASampleRateChangeHasSwitched_OpensNoSecondOutput()
    {
        await using var harness = new Harness();
        await harness.Pipeline.StartAsync(Pcm());
        harness.Feed(ChunksToPlayback);
        await harness.Pipeline.StartAsync(Pcm(sampleRate: 44_100));

        await harness.Pipeline.StopAsync();
        await Task.Delay(100);

        Assert.Equal(AudioPipelineState.Idle, harness.Pipeline.State);
        Assert.Single(harness.Players);
        Assert.True(harness.Player.Disposed);
    }

    [Fact]
    public async Task StartAsync_AfterAPlayerErrorDuringASampleRateChange_AbandonsThePendingSwitch()
    {
        await using var harness = new Harness();
        await harness.Pipeline.StartAsync(Pcm());
        harness.Feed(ChunksToPlayback);
        await harness.Pipeline.StartAsync(Pcm(sampleRate: 44_100));
        harness.Player.Fail();

        Assert.Equal(AudioPipelineState.Error, harness.Pipeline.State);

        await harness.Pipeline.StartAsync(Pcm());
        var player = harness.Player;

        // What would let a switch left over from the failed stream go ahead: its buffer emptying.
        harness.Pipeline.Clear();
        await Task.Delay(150);

        Assert.Same(player, harness.Player);
        Assert.False(player.Disposed);
    }

    [Fact]
    public async Task StartAsync_BufferFactoryFailingOnASampleRateChange_LeavesNoDisposedBufferForTheNextStream()
    {
        await using var harness = new Harness();
        await harness.Pipeline.StartAsync(Pcm());
        harness.Feed(5);

        harness.FailNextBuffer = true;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Pipeline.StartAsync(Pcm(sampleRate: 44_100)));

        await harness.Pipeline.StartAsync(Pcm());
        harness.Feed(5);

        Assert.Equal(5 * ChunkMs, harness.Pipeline.BufferStats?.BufferedMs);
    }

    [Fact]
    public async Task Clear_BeforeASampleRateChangeHasSwitched_LeavesStartingPlaybackToTheSwitch()
    {
        await using var harness = new Harness();
        await harness.Pipeline.StartAsync(Pcm());
        harness.Feed(ChunksToPlayback);
        await harness.Pipeline.StartAsync(Pcm(sampleRate: 44_100));
        var firstPlayer = harness.Player;

        harness.Pipeline.Clear();
        harness.Feed(ChunksToPlayback, sampleRate: 44_100);

        // The new buffer is ready, but the output it will play through does not exist yet: the
        // player in place is the one about to be closed.
        await harness.WaitForAsync(() => harness.Players.Count == 2 && harness.Player.PlayCalls == 1);

        Assert.Equal(1, firstPlayer.PlayCalls);
        Assert.Equal(AudioPipelineState.Playing, harness.Pipeline.State);
    }

    [Fact]
    public async Task StartAsync_SampleRateChangeWithAnOutputThatStopsPulling_SwitchesAnyway()
    {
        await using var harness = new Harness();
        await harness.Pipeline.StartAsync(Pcm());
        harness.Feed(ChunksToPlayback);

        await harness.Pipeline.StartAsync(Pcm(sampleRate: 44_100));

        // Nothing reads the first buffer, so it never runs dry.
        await harness.WaitForAsync(() => harness.Players.Count == 2 && harness.Player.PlayCalls == 1);

        Assert.True(harness.Players[0].Disposed);
    }

    [Fact]
    public async Task SwitchDeviceAsync_BeforeASampleRateChangeHasSwitched_UpdatesTheBufferBeingPlayed()
    {
        await using var harness = new Harness();
        await harness.Pipeline.StartAsync(Pcm());
        harness.Feed(5);
        await harness.Pipeline.StartAsync(Pcm(sampleRate: 44_100));

        harness.Player.OutputLatencyMs = 40;
        await harness.Pipeline.SwitchDeviceAsync(null);

        Assert.Equal(40_000, harness.Buffers[0].OutputLatencyMicroseconds);
    }

    [Fact]
    public async Task StartAsync_SampleRateChangeWithANegativeOutputLatency_StillSwitches()
    {
        await using var harness = new Harness();
        await harness.Pipeline.StartAsync(Pcm());
        harness.Feed(ChunksToPlayback);
        harness.Player.OutputLatencyMs = -5;

        await harness.Pipeline.StartAsync(Pcm(sampleRate: 44_100));
        harness.PullUntilEmpty(harness.Buffers[0], harness.Sources[0]);

        await harness.WaitForAsync(() => harness.Players.Count == 2 && harness.Player.PlayCalls == 1);
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

    /// <summary>
    /// 48 kHz stereo FLAC announced as 32-bit, with a <c>codec_header</c> whose STREAMINFO says 24.
    /// </summary>
    private static AudioFormat FlacAnnouncedAs32BitWith24BitStreamInfo()
    {
        var header = new byte[42];
        new byte[]
        {
            0x66, 0x4C, 0x61, 0x43, // fLaC
            0x80, 0x00, 0x00, 0x22, // last metadata block, STREAMINFO, 34 bytes
            0x00, 0x10, 0x20, 0x00, // block size 16 to 8192
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // frame sizes unknown
            0x0B, 0xB8, 0x03, 0x70, // 48000 Hz, 2 channels, 24 bits per sample
        }.CopyTo(header, 0);

        return new AudioFormat
        {
            Codec = "flac",
            SampleRate = SampleRate,
            Channels = Channels,
            BitDepth = 32,
            CodecHeader = Convert.ToBase64String(header),
        };
    }

    /// <summary>
    /// One FLAC frame of one chunk duration, every sample of both channels <paramref name="value"/>,
    /// taking its bit depth from STREAMINFO. Constant subframes; the CRCs are zero because the
    /// vendored decoder does not check them.
    /// </summary>
    private static byte[] Flac24BitFrame(int value)
    {
        var blockSize = ChunkSamples / Channels;
        var sample = new[] { (byte)(value >> 16), (byte)(value >> 8), (byte)value };
        return
        [
            0xFF, 0xF8, // sync, fixed block size
            0x70, // block size follows as 16 bits, sample rate from STREAMINFO
            0x10, // two independent channels, bit depth from STREAMINFO
            0x00, // frame number
            (byte)((blockSize - 1) >> 8), (byte)(blockSize - 1),
            0x00, // header CRC-8
            0x00, .. sample, // constant subframe, left
            0x00, .. sample, // constant subframe, right
            0x00, 0x00, // frame CRC-16
        ];
    }

    private sealed class Harness : IAsyncDisposable
    {
        private const long FirstTimestamp = 1_000_000;

        private readonly StubTimer _timer = new StubTimer();
        private long _nextTimestamp = FirstTimestamp;

        public Harness()
        {
            Pipeline = new AudioPipeline(
                NullLogger<AudioPipeline>.Instance,
                new AudioDecoderFactory(),
                new FakeClockSynchronizer { HasMinimalSync = true, IsConverged = true },
                (format, clockSync) =>
                {
                    if (FailNextBuffer)
                    {
                        FailNextBuffer = false;
                        throw new InvalidOperationException("buffer factory failed");
                    }

                    var buffer = new TimedAudioBuffer(format, clockSync, bufferCapacityMs: 2000);
                    Buffers.Add(buffer);
                    return buffer;
                },
                () =>
                {
                    var player = new StubAudioPlayer();
                    FirstBufferLeftWhenOutputOpened.Add(Buffers[0].BufferedMilliseconds);
                    Players.Add(player);
                    return player;
                },
                (buffer, time) =>
                {
                    var source = new StubSampleSource(buffer, time);
                    Sources.Add(source);
                    return source;
                },
                precisionTimer: _timer,
                useMonotonicTimer: false);

            Pipeline.StateChanged += (_, state) => States.Add(state);
        }

        public AudioPipeline Pipeline { get; }

        public bool FailNextBuffer { get; set; }

        public List<TimedAudioBuffer> Buffers { get; } = new List<TimedAudioBuffer>();

        public List<StubAudioPlayer> Players { get; } = new List<StubAudioPlayer>();

        public List<StubSampleSource> Sources { get; } = new List<StubSampleSource>();

        /// <summary>What the first buffer still held each time an output was opened.</summary>
        public List<double> FirstBufferLeftWhenOutputOpened { get; } = new List<double>();

        public List<AudioPipelineState> States { get; } = new List<AudioPipelineState>();

        public TimedAudioBuffer Buffer => Buffers[^1];

        public StubAudioPlayer Player => Players[^1];

        /// <summary>
        /// Feeds <paramref name="chunkCount"/> chunks of one chunk duration each, every sample
        /// of them <paramref name="value"/> (silence by default; 16-bit only).
        /// </summary>
        public void Feed(int chunkCount, int bitDepth = 16, int sampleRate = SampleRate, short value = 0)
        {
            var encoded = new byte[ChunkMs * sampleRate / 1000 * Channels * (bitDepth / 8)];
            if (value != 0)
            {
                for (var i = 0; i < encoded.Length; i += 2)
                {
                    BitConverter.TryWriteBytes(encoded.AsSpan(i), value);
                }
            }

            for (var i = 0; i < chunkCount; i++)
            {
                Pipeline.ProcessAudioChunk(new AudioChunk { EncodedData = encoded, ServerTimestamp = _nextTimestamp });
                _nextTimestamp += ChunkMs * 1000L;
            }
        }

        /// <summary>
        /// Plays <paramref name="buffer"/> out through <paramref name="source"/> as an output
        /// device would, 10 ms of local time per read, and returns the sample values heard in
        /// thousands, each run of one value once and silence left out.
        /// </summary>
        public List<int> PullUntilEmpty(TimedAudioBuffer buffer, StubSampleSource source)
        {
            _timer.Now = Math.Max(_timer.Now, FirstTimestamp);

            var heard = new List<int>();
            var samples = new float[source.Format.SampleRate / 100 * source.Format.Channels];
            for (var pull = 0; pull < 200 && buffer.BufferedMilliseconds > 0; pull++)
            {
                source.Read(samples, 0, samples.Length);
                _timer.Now += 10_000;

                foreach (var sample in samples)
                {
                    var value = (int)Math.Round(sample * 32_768 / 1000);
                    if (value != 0 && (heard.Count == 0 || heard[^1] != value))
                    {
                        heard.Add(value);
                    }
                }
            }

            return heard;
        }

        /// <summary>
        /// Has the output's audio callback land once more as <paramref name="player"/> is being
        /// stopped: until the stop returns, the device is free to ask for another block.
        /// </summary>
        public LastRead ReadFirstSourceWhenStopping(StubAudioPlayer player)
        {
            var read = new LastRead();
            player.Stopping = () =>
            {
                read.Thrown = Record.Exception(() => Sources[0].Read(new float[Channels], 0, Channels));
                read.Done = true;
            };
            return read;
        }

        /// <summary>Waits for something the pipeline does on its own time.</summary>
        public async Task WaitForAsync(Func<bool> condition)
        {
            for (var i = 0; i < 500 && !condition(); i++)
            {
                await Task.Delay(10);
            }

            Assert.True(condition());
        }

        public ValueTask DisposeAsync() => Pipeline.DisposeAsync();
    }

    /// <summary>What a read made by <see cref="Harness.ReadFirstSourceWhenStopping"/> came to.</summary>
    private sealed class LastRead
    {
        public bool Done { get; set; }

        public Exception? Thrown { get; set; }
    }

    private sealed class StubTimer : IHighPrecisionTimer
    {
        public long Now { get; set; }

        public long GetCurrentTimeMicroseconds() => Now;

        public long GetElapsedMicroseconds(long fromTimeMicroseconds) => Now - fromTimeMicroseconds;
    }

    private sealed class StubSampleSource : IAudioSampleSource
    {
        private readonly ITimedAudioBuffer _buffer;
        private readonly Func<long> _time;

        internal StubSampleSource(ITimedAudioBuffer buffer, Func<long> time)
        {
            _buffer = buffer;
            _time = time;
        }

        public AudioFormat Format => _buffer.Format;

        public int Read(float[] buffer, int offset, int count) =>
            _buffer.Read(buffer.AsSpan(offset, count), _time());
    }

    /// <summary>Counts the lifecycle calls a restart makes and an in-place update must not.</summary>
    private sealed class StubAudioPlayer : IAudioPlayer
    {
        public event EventHandler<AudioPlayerState>? StateChanged;

        /// <summary>Never raised: nothing under test observes the player's own signalling.</summary>
        event EventHandler<AudioPlayerError>? IAudioPlayer.ErrorOccurred
        {
            add => _ = value;
            remove => _ = value;
        }

        public AudioPlayerState State { get; private set; } = AudioPlayerState.Uninitialized;

        public float Volume { get; set; }

        public bool IsMuted { get; set; }

        public int OutputLatencyMs { get; set; }

        public int InitializeCalls { get; private set; }

        public int PlayCalls { get; private set; }

        public int StopCount { get; private set; }

        public bool Disposed { get; private set; }

        /// <summary>Run at the start of <see cref="Stop"/>, while the output is still live.</summary>
        public Action? Stopping { get; set; }

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

        /// <summary>Reports the output as failed, as a backend losing its device does.</summary>
        public void Fail()
        {
            State = AudioPlayerState.Error;
            StateChanged?.Invoke(this, State);
        }

        public void Stop()
        {
            Stopping?.Invoke();
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
