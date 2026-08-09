using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Audio.Source;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Tests.Client;

namespace Sendspin.SDK.Tests.Audio;

/// <summary>
/// Direct coverage for <see cref="SourceStreamPipeline"/>'s two correctness rules (#82):
/// captured buffers are encoded and framed by a single consumer, in capture order, through
/// a bounded channel whose overflow drops the oldest backlog; and a failure while starting
/// restores the pre-start state instead of wedging the pipeline against every later start.
/// </summary>
public class SourceStreamPipelineTests
{
    /// <summary>Binary chunk layout is [type 12][int64 BE server timestamp][payload].</summary>
    private const int ChunkHeaderBytes = 9;

    // FakeCaptureDevice's format: 48 kHz stereo 16-bit, so 4 bytes per interleaved frame
    // and 192 bytes per millisecond of audio.
    private const int SampleRate = 48000;
    private const int BytesPerFrame = 2 * (16 / 8);
    private const int BytesPerMillisecond = SampleRate * BytesPerFrame / 1000;

    /// <summary>The spec's 150 ms chunk ceiling, in bytes of this format's PCM.</summary>
    private const int CeilingBytes = 150 * BytesPerMillisecond;

    [Fact]
    public async Task OverlappingCaptures_EncodeSequentially_AndReachTheFramingInCaptureOrder()
    {
        var capture = new FakeCaptureDevice();
        var encoder = new GatedEncoder();
        var sent = new List<IMessage>();
        var frames = new List<byte[]>();
        var pipeline = CreatePipeline(capture, Collect(sent), Collect(frames), new FixedEncoderFactory(encoder));
        await pipeline.HandleCommandAsync("start");

        // Park the first capture inside Encode, then deliver a second capture while the
        // first is provably still being encoded — the overlapping-capture shape from the
        // defect. A stateless PCM encoder would not crash under concurrent entry, so the
        // assertions are on observed entry count and observed wire order, not on survival.
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        encoder.HoldNextEncode(hold);
        var emit1 = Task.Run(() => capture.Emit([1, 1, 1, 1], 1000));
        try
        {
            await encoder.EncodeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Must return immediately: the capture callback only enqueues. If it could
            // block on the busy consumer, this call would deadlock the test right here.
            capture.Emit([2, 2, 2, 2], 2000);

            // The second capture must wait behind the first, not overtake it to the framing.
            lock (frames)
            {
                Assert.Empty(frames);
            }
        }
        finally
        {
            hold.TrySetResult();
        }

        await emit1.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(
            () =>
            {
                lock (frames)
                {
                    return frames.Count == 2;
                }
            },
            "both chunks to be framed and sent");

        Assert.Equal(1, encoder.MaxObservedConcurrency); // the encoder is never entered concurrently
        Assert.Equal(new byte[] { 1, 2 }, encoder.EnteredMarkersSnapshot());
        lock (frames)
        {
            // Binary chunk layout is [type 12][int64 BE timestamp][payload]: the payload
            // marker sits at offset 9, and the wire must carry capture order.
            Assert.Equal(new byte[] { 1, 2 }, frames.Select(f => f[9]).ToArray());
        }
    }

    [Fact]
    public async Task StalledSend_BoundsTheBacklog_DropsTheOldest_AndNeverBlocksTheCaptureCallback()
    {
        var capture = new FakeCaptureDevice();
        var wire = new List<string>();
        var firstSendEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int sends = 0;
        Func<byte[], Task> sendBinary = async data =>
        {
            if (Interlocked.Increment(ref sends) == 1)
            {
                firstSendEntered.TrySetResult();
                await gate.Task;
            }

            lock (wire)
            {
                wire.Add($"chunk:{data[9]}");
            }
        };
        Func<IMessage, Task> sendMessage = m =>
        {
            lock (wire)
            {
                wire.Add(m switch
                {
                    ClientStreamStartMessage => "start",
                    ClientStreamEndMessage => "end",
                    _ => m.GetType().Name,
                });
            }

            return Task.CompletedTask;
        };
        var pipeline = CreatePipeline(capture, sendMessage, sendBinary);
        await pipeline.HandleCommandAsync("start");

        const int overflow = 4;
        try
        {
            // Chunk 1 is dequeued and parked mid-send: a stalled network write with the
            // device still capturing. Everything after it piles up behind the stall.
            capture.Emit([1], 1_000);
            await firstSendEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Fill the channel to capacity, then keep going. Every Emit must return
            // immediately even against a full channel — a capture callback that blocks
            // would stall the audio device, so this loop completing while the send is
            // still parked is itself part of the proof.
            for (int i = 2; i <= 1 + SourceStreamPipeline.MaxBufferedCaptures + overflow; i++)
            {
                capture.Emit([(byte)i], i * 1_000);
            }
        }
        finally
        {
            gate.TrySetResult();
        }

        // Stop completes the channel and waits for the consumer, so once it returns,
        // everything that will ever reach the wire has reached it — no timing window for
        // an unbounded implementation to sneak its extra chunks in after the assertion.
        await pipeline.StopStreamingAsync();

        // The in-flight chunk survives, the oldest `overflow` captures are dropped, the
        // newest MaxBufferedCaptures resume in capture order, and the end follows the
        // drained chunks. An unbounded queue would deliver all of 2..21 here instead.
        var expected = new List<string> { "start", "chunk:1" };
        expected.AddRange(Enumerable.Range(2 + overflow, SourceStreamPipeline.MaxBufferedCaptures)
            .Select(i => $"chunk:{i}"));
        expected.Add("end");
        lock (wire)
        {
            Assert.Equal(expected, wire);
        }
    }

    [Fact]
    public async Task FailedEncoderCreation_SurfacesTheFailure_AndLeavesThePipelineRestartable()
    {
        var capture = new FakeCaptureDevice();
        var factory = new FlakyEncoderFactory { ThrowOnCreate = true };
        var sent = new List<IMessage>();
        var frames = new List<byte[]>();
        var pipeline = CreatePipeline(capture, Collect(sent), Collect(frames), factory);

        // Surfaced, not swallowed: the caller sees the encoder failure.
        await Assert.ThrowsAsync<NotSupportedException>(() => pipeline.HandleCommandAsync("start"));

        // The wedge clause, deliberately first and with no intervening stop (a stop would
        // reset a stuck flag and mask the wedge): once the encoder can be created, a later
        // start must genuinely start.
        factory.ThrowOnCreate = false;
        await pipeline.HandleCommandAsync("start");

        Assert.Single(sent.OfType<ClientStreamStartMessage>());
        Assert.True(pipeline.IsStreaming);
        Assert.True(capture.Capturing);

        // Positive control: the restarted stream actually streams.
        capture.Emit([7], 1000);
        await WaitUntilAsync(
            () =>
            {
                lock (frames)
                {
                    return frames.Count == 1;
                }
            },
            "a chunk after the successful restart");
        lock (frames)
        {
            Assert.Equal(7, frames[0][9]);
        }
    }

    [Fact]
    public async Task FailedEncoderCreation_LeavesNoStreamState_AndStopStaysSilent()
    {
        var capture = new FakeCaptureDevice();
        var factory = new FlakyEncoderFactory { ThrowOnCreate = true };
        var sent = new List<IMessage>();
        var frames = new List<byte[]>();
        var pipeline = CreatePipeline(capture, Collect(sent), Collect(frames), factory);

        await Assert.ThrowsAsync<NotSupportedException>(() => pipeline.HandleCommandAsync("start"));

        Assert.False(pipeline.IsStreaming);
        Assert.False(capture.Capturing);
        Assert.DoesNotContain(sent, m => m is ClientStreamStartMessage); // Create precedes the announce
        Assert.DoesNotContain(sent, m => m is ClientStreamEndMessage);

        // A capture arriving now goes nowhere: no session is open.
        capture.Emit([1], 1000);
        lock (frames)
        {
            Assert.Empty(frames);
        }

        // And a stop must not end a stream that never began.
        await pipeline.HandleCommandAsync("stop");
        Assert.DoesNotContain(sent, m => m is ClientStreamEndMessage);
    }

    [Fact]
    public async Task FailedCaptureStart_AfterAnnounce_EndsTheHalfOpenStream_AndStaysRestartable()
    {
        // client_stream/start hits the wire before the capture device opens, so a device
        // failure leaves a half-open stream the server believes exists. The rollback must
        // close it — and still leave the pipeline restartable.
        var capture = new FailingStartCaptureDevice { FailNextStart = true };
        var sent = new List<IMessage>();
        var frames = new List<byte[]>();
        var pipeline = CreatePipeline(capture, Collect(sent), Collect(frames));

        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.HandleCommandAsync("start"));

        Assert.False(pipeline.IsStreaming);
        Assert.False(capture.Capturing);
        Assert.Equal(
            new[] { typeof(ClientStreamStartMessage), typeof(ClientStreamEndMessage) },
            sent.Select(m => m.GetType()).ToArray());

        // The device recovers; the pipeline must too.
        await pipeline.HandleCommandAsync("start");

        Assert.True(pipeline.IsStreaming);
        Assert.True(capture.Capturing);
        Assert.Equal(2, sent.OfType<ClientStreamStartMessage>().Count());
    }

    [Fact]
    public async Task StartWhileStreaming_DoesNotRestartTheStream()
    {
        var capture = new FakeCaptureDevice();
        var sent = new List<IMessage>();
        var frames = new List<byte[]>();
        var pipeline = CreatePipeline(capture, Collect(sent), Collect(frames));

        await pipeline.HandleCommandAsync("start");
        await pipeline.HandleCommandAsync("start");

        // Spec: a start received while the input stream is open MUST NOT restart it —
        // one announce, no end, and the stream stays up.
        Assert.True(pipeline.IsStreaming);
        Assert.True(capture.Capturing);
        Assert.Single(sent.OfType<ClientStreamStartMessage>());
        Assert.DoesNotContain(sent, m => m is ClientStreamEndMessage);
    }

    [Fact]
    public async Task StopWhileStopped_IsIgnored()
    {
        var capture = new FakeCaptureDevice();
        var sent = new List<IMessage>();
        var frames = new List<byte[]>();
        var pipeline = CreatePipeline(capture, Collect(sent), Collect(frames));

        // Never started: a stop must not end a stream that never opened.
        await pipeline.HandleCommandAsync("stop");
        Assert.DoesNotContain(sent, m => m is ClientStreamEndMessage);

        // Started then stopped twice: the second stop must not send a second end.
        await pipeline.HandleCommandAsync("start");
        await pipeline.HandleCommandAsync("stop");
        await pipeline.HandleCommandAsync("stop");
        Assert.Single(sent.OfType<ClientStreamEndMessage>());
    }

    [Fact]
    public async Task StopArrivingDuringAnInFlightStart_StopsTheStream()
    {
        var capture = new FakeCaptureDevice();
        var wire = new List<string>();
        var frames = new List<byte[]>();
        var startSendEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<IMessage, Task> sendMessage = async m =>
        {
            bool park;
            lock (wire)
            {
                park = m is ClientStreamStartMessage && !wire.Contains("client_stream/start");
                wire.Add(m switch
                {
                    ClientStreamStartMessage => "client_stream/start",
                    ClientStreamEndMessage => "client_stream/end",
                    _ => m.GetType().Name,
                });
            }

            if (park)
            {
                startSendEntered.TrySetResult();
                await gate.Task;
            }
        };
        var pipeline = CreatePipeline(capture, sendMessage, Collect(frames));

        Task startTask = pipeline.HandleCommandAsync("start");
        await startSendEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The stop lands while client_stream/start is still in flight. The server's
        // last word is stop, so it must not be lost against the not-yet-open stream:
        // once the start completes, the stream comes straight back down.
        Task stopTask = pipeline.HandleCommandAsync("stop");
        gate.TrySetResult();

        await startTask.WaitAsync(TimeSpan.FromSeconds(5));
        await stopTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(pipeline.IsStreaming);
        Assert.False(capture.Capturing);
        lock (wire)
        {
            Assert.Equal(new[] { "client_stream/start", "client_stream/end" }, wire);
        }
    }

    [Fact]
    public async Task StartWhileAStopIsStillDraining_DoesNotOverlapSessions_AndEndPrecedesTheNextStart()
    {
        // A stop's core does real async work after flipping the flags: it drains the
        // consumer before sending client_stream/end. A start arriving in that window must
        // not build a second encoder/consumer or announce a second session before the
        // first session's end has gone out — sessions must never overlap on the wire.
        var capture = new FakeCaptureDevice();
        var factory = new CountingEncoderFactory();
        var wire = new List<string>();
        var firstSendEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int sends = 0;
        Func<byte[], Task> sendBinary = async data =>
        {
            if (Interlocked.Increment(ref sends) == 1)
            {
                firstSendEntered.TrySetResult();
                await gate.Task;
            }

            lock (wire)
            {
                wire.Add($"chunk:{data[9]}");
            }
        };
        Func<IMessage, Task> sendMessage = m =>
        {
            lock (wire)
            {
                wire.Add(m switch
                {
                    ClientStreamStartMessage => "start",
                    ClientStreamEndMessage => "end",
                    _ => m.GetType().Name,
                });
            }

            return Task.CompletedTask;
        };
        var pipeline = CreatePipeline(capture, sendMessage, sendBinary, factory);
        await pipeline.HandleCommandAsync("start");
        Assert.Equal(1, factory.CreateCalls);

        Task stopTask;
        Task startTask;
        try
        {
            // Park the first session's consumer mid-send, then stop: the stop's core runs
            // to its drain await and is provably mid-drain when the next start arrives.
            capture.Emit([1], 1_000);
            await firstSendEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stopTask = pipeline.HandleCommandAsync("stop");

            startTask = pipeline.HandleCommandAsync("start");

            // While the drain gate is held nothing can legally advance, so these are
            // deterministic: the racing start must not have built a second session.
            Assert.Equal(1, factory.CreateCalls);
            lock (wire)
            {
                Assert.Equal(1, wire.Count(op => op == "start"));
            }
        }
        finally
        {
            gate.TrySetResult();
        }

        await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
        await startTask.WaitAsync(TimeSpan.FromSeconds(5));

        // Positive control: the queued start genuinely opened a second session that streams.
        Assert.True(pipeline.IsStreaming);
        Assert.True(capture.Capturing);
        Assert.Equal(2, factory.CreateCalls);
        capture.Emit([9], 9_000);
        await WaitUntilAsync(
            () =>
            {
                lock (wire)
                {
                    return wire.Count == 5;
                }
            },
            "the second session's chunk to be sent");
        lock (wire)
        {
            // The first session closes completely — its chunk, then its end — before the
            // second session's start announces, and the new session's chunks follow it.
            Assert.Equal(new[] { "start", "chunk:1", "end", "start", "chunk:9" }, wire);
        }
    }

    [Fact]
    public async Task DisposeRacingAnInFlightStart_DoesNotDisposeTheCaptureDeviceUnderIt()
    {
        var capture = new GatedStartCaptureDevice();
        var sent = new List<IMessage>();
        var frames = new List<byte[]>();
        var pipeline = CreatePipeline(capture, Collect(sent), Collect(frames));

        // Hold the start inside the capture device's own StartAsync, then dispose.
        Task startTask = pipeline.HandleCommandAsync("start");
        await capture.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task disposeTask = pipeline.DisposeAsync().AsTask();

        // Deterministic: nothing can legally advance until the start gate is released, so
        // a dispose that does not wait for the in-flight start has already disposed the
        // device by now — from under its still-executing StartAsync.
        Assert.False(
            capture.Disposed,
            "the capture device must not be disposed while its StartAsync is still in flight");

        capture.ReleaseStart.TrySetResult();

        // The start observes the disposal once its device call returns, rolls back, and
        // surfaces why it did not start.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => startTask.WaitAsync(TimeSpan.FromSeconds(5)));
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(pipeline.IsStreaming);
        Assert.Equal(
            new[] { "start-entered", "start-returned", "stopped", "disposed" },
            capture.OperationsSnapshot());
    }

    [Fact]
    public async Task ACaptureLongerThanTheChunkCeiling_IsSplitSoNoChunkExceedsIt()
    {
        // The buffer size is the capture device's choice, so a capture can arrive longer
        // than the spec's 150 ms chunk ceiling (roles/source/v1.md:96, a MUST). It has to
        // be split rather than assumed away — and the split must lose no audio and
        // timestamp each piece at the instant that piece was captured.
        var capture = new FakeCaptureDevice();
        var frames = new List<byte[]>();
        var pipeline = CreatePipeline(capture, Collect(new List<IMessage>()), Collect(frames));
        await pipeline.HandleCommandAsync("start");

        // 400 ms in one buffer: over the ceiling, and not a whole multiple of it.
        var pcm = new byte[400 * BytesPerMillisecond];
        for (int i = 0; i < pcm.Length; i++)
        {
            pcm[i] = (byte)(i % 251);
        }

        capture.Emit(pcm, 1_000_000);

        // The stop drains the consumer before it returns, so every chunk this capture
        // produced is on the wire by the time it completes — no polling needed.
        await pipeline.StopStreamingAsync().WaitAsync(TimeSpan.FromSeconds(5));

        byte[][] sent;
        lock (frames)
        {
            sent = frames.ToArray();
        }

        Assert.All(sent, frame => Assert.True(
            frame.Length - ChunkHeaderBytes <= CeilingBytes,
            $"a chunk of {(frame.Length - ChunkHeaderBytes) / (double)BytesPerMillisecond:F2} ms exceeds the 150 ms ceiling"));

        // Nothing dropped, duplicated, or reordered by the split.
        Assert.Equal(pcm, sent.SelectMany(frame => frame.Skip(ChunkHeaderBytes)).ToArray());

        // Each piece carries its own capture instant, not the whole buffer's.
        long framesEmitted = 0;
        foreach (byte[] chunk in sent)
        {
            Assert.Equal(
                1_000_000 + (framesEmitted * 1_000_000 / SampleRate),
                BinaryPrimitives.ReadInt64BigEndian(chunk.AsSpan(1, 8)));
            framesEmitted += (chunk.Length - ChunkHeaderBytes) / BytesPerFrame;
        }
    }

    [Fact]
    public async Task ACaptureAtTheChunkCeiling_IsSentWhole()
    {
        // The boundary, and the positive control for the split: a capture that already
        // fits must reach the wire as one chunk at its own timestamp. Without this a
        // pipeline that split every capture would pass the ceiling test above.
        var capture = new FakeCaptureDevice();
        var frames = new List<byte[]>();
        var pipeline = CreatePipeline(capture, Collect(new List<IMessage>()), Collect(frames));
        await pipeline.HandleCommandAsync("start");

        var pcm = new byte[CeilingBytes];
        capture.Emit(pcm, 7_000);
        await pipeline.StopStreamingAsync().WaitAsync(TimeSpan.FromSeconds(5));

        byte[] chunk = Assert.Single(frames);
        Assert.Equal(CeilingBytes, chunk.Length - ChunkHeaderBytes);
        Assert.Equal(7_000, BinaryPrimitives.ReadInt64BigEndian(chunk.AsSpan(1, 8)));
    }

    private static SourceStreamPipeline CreatePipeline(
        IAudioCaptureDevice capture,
        Func<IMessage, Task> sendMessage,
        Func<byte[], Task> sendBinary,
        ISourceAudioEncoderFactory? factory = null)
        => new SourceStreamPipeline(
            capture,
            new ConvergedClockSynchronizer(),
            sendMessage,
            sendBinary,
            NullLogger.Instance,
            canStream: () => true,
            factory);

    private static Func<IMessage, Task> Collect(List<IMessage> into) =>
        m =>
        {
            lock (into)
            {
                into.Add(m);
            }

            return Task.CompletedTask;
        };

    private static Func<byte[], Task> Collect(List<byte[]> into) =>
        data =>
        {
            lock (into)
            {
                into.Add(data);
            }

            return Task.CompletedTask;
        };

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {because}");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Encoder double that makes concurrent entry observable rather than inferred: it
    /// tracks the live entry count under a lock (so overlap cannot hide), records the
    /// first payload byte of each buffer in entry order, and can park one Encode call on
    /// a gate so a test can hold the consumer provably inside the encoder.
    /// </summary>
    private sealed class GatedEncoder : ISourceAudioEncoder
    {
        private readonly object _sync = new object();
        private readonly List<byte> _enteredMarkers = new List<byte>();
        private TaskCompletionSource? _holdNextEncode;
        private int _active;
        private int _maxActive;

        public string Codec => "pcm";

        public string? CodecHeader => null;

        /// <summary>Completed the moment any Encode call is entered.</summary>
        public TaskCompletionSource EncodeEntered { get; } =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Highest number of threads ever observed inside Encode at once.</summary>
        public int MaxObservedConcurrency
        {
            get
            {
                lock (_sync)
                {
                    return _maxActive;
                }
            }
        }

        /// <summary>Parks the next Encode call on <paramref name="hold"/> after it is entered.</summary>
        public void HoldNextEncode(TaskCompletionSource hold)
        {
            lock (_sync)
            {
                _holdNextEncode = hold;
            }
        }

        public byte[] EnteredMarkersSnapshot()
        {
            lock (_sync)
            {
                return _enteredMarkers.ToArray();
            }
        }

        public byte[] Encode(ReadOnlySpan<byte> pcm)
        {
            TaskCompletionSource? hold;
            lock (_sync)
            {
                _active++;
                _maxActive = Math.Max(_maxActive, _active);
                _enteredMarkers.Add(pcm[0]);
                hold = _holdNextEncode;
                _holdNextEncode = null;
            }

            EncodeEntered.TrySetResult();
            hold?.Task.Wait();

            byte[] result = pcm.ToArray();
            lock (_sync)
            {
                _active--;
            }

            return result;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Hands back one fixed encoder instance regardless of codec.</summary>
    private sealed class FixedEncoderFactory : ISourceAudioEncoderFactory
    {
        private readonly ISourceAudioEncoder _encoder;

        public FixedEncoderFactory(ISourceAudioEncoder encoder) => _encoder = encoder;

        public ISourceAudioEncoder Create(string codec, AudioFormat format) => _encoder;
    }

    /// <summary>Factory that fails on demand, so a test can fail one start and let the next succeed.</summary>
    private sealed class FlakyEncoderFactory : ISourceAudioEncoderFactory
    {
        public bool ThrowOnCreate { get; set; }

        public ISourceAudioEncoder Create(string codec, AudioFormat format) =>
            ThrowOnCreate
                ? throw new NotSupportedException("Simulated encoder creation failure")
                : new PcmSourceEncoder();
    }

    /// <summary>
    /// Counts encoder creations. Each streaming session builds exactly one encoder, so the
    /// count is how a test observes a second session being built while the first is still
    /// draining — without inferring it from wire output.
    /// </summary>
    private sealed class CountingEncoderFactory : ISourceAudioEncoderFactory
    {
        private int _createCalls;

        public int CreateCalls => Volatile.Read(ref _createCalls);

        public ISourceAudioEncoder Create(string codec, AudioFormat format)
        {
            Interlocked.Increment(ref _createCalls);
            return new PcmSourceEncoder();
        }
    }

    /// <summary>
    /// Capture device that parks inside StartAsync on a gate and records every lifecycle
    /// call in order, so a test can hold a start provably inside the device and assert
    /// nothing disposes it from under that call. Never emits.
    /// </summary>
    private sealed class GatedStartCaptureDevice : IAudioCaptureDevice
    {
        private readonly object _sync = new object();
        private readonly List<string> _operations = new List<string>();

        // Explicit no-op accessors: subscribe/unsubscribe are accepted and ignored,
        // since these tests never deliver a capture through it.
        public event EventHandler<CapturedAudio>? AudioCaptured
        {
            add { /* ignored */ }
            remove { /* ignored */ }
        }

        public AudioFormat Format { get; } = new AudioFormat { Codec = "pcm", SampleRate = 48000, Channels = 2, BitDepth = 16 };

        /// <summary>Completed the moment StartAsync is entered.</summary>
        public TaskCompletionSource StartEntered { get; } =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Releases the StartAsync call parked on it.</summary>
        public TaskCompletionSource ReleaseStart { get; } =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Disposed
        {
            get
            {
                lock (_sync)
                {
                    return _operations.Contains("disposed");
                }
            }
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            Record("start-entered");
            StartEntered.TrySetResult();
            await ReleaseStart.Task;
            Record("start-returned");
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Record("stopped");
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Record("disposed");
            return ValueTask.CompletedTask;
        }

        public string[] OperationsSnapshot()
        {
            lock (_sync)
            {
                return _operations.ToArray();
            }
        }

        private void Record(string operation)
        {
            lock (_sync)
            {
                _operations.Add(operation);
            }
        }
    }

    /// <summary>Capture device whose StartAsync fails once, then works. Never emits.</summary>
    private sealed class FailingStartCaptureDevice : IAudioCaptureDevice
    {
        // Explicit no-op accessors: the pipeline's subscribe/unsubscribe calls are
        // accepted and ignored, since these tests never deliver a capture through it.
        public event EventHandler<CapturedAudio>? AudioCaptured
        {
            add { /* ignored */ }
            remove { /* ignored */ }
        }

        public AudioFormat Format { get; } = new AudioFormat { Codec = "pcm", SampleRate = 48000, Channels = 2, BitDepth = 16 };

        public bool FailNextStart { get; set; }

        public bool Capturing { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (FailNextStart)
            {
                FailNextStart = false;
                throw new InvalidOperationException("Simulated capture-device failure");
            }

            Capturing = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Capturing = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
