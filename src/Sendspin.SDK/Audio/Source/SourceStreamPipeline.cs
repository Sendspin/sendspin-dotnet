using System.Buffers.Binary;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Audio.Source;

/// <summary>
/// Drives the <c>source</c> role: on the server's <c>start</c> it opens the capture
/// device, announces the format with <c>client_stream/start</c>, then encodes each
/// captured buffer and streams it as one or more binary type-12 chunks — none carrying
/// more than <see cref="MaxChunkMilliseconds"/> of audio — each timestamped in the server
/// time domain; on <c>stop</c> it ends the stream. Server-initiated only — a source
/// never streams unsolicited.
/// </summary>
/// <remarks>
/// Timestamps are the local capture time mapped to the server clock via
/// <see cref="IClockSynchronizer.ClientToServerTime"/> (offset + drift), per the spec.
/// The pipeline enforces the spec's trust rule itself, at the point the capture device
/// opens: it refuses to stream unless <c>canStream</c> reports user trust and an active
/// source role. Activate-time checking alone is insufficient, because server/command
/// reaches this pipeline without passing through server/activate.
/// Start, stop, and the per-connection reset (<see cref="ResetForConnectionLossAsync"/>)
/// run one at a time in arrival order, so commands are idempotent against the stream's
/// actual state rather than a mid-transition snapshot of it.
/// </remarks>
public sealed class SourceStreamPipeline : IAsyncDisposable
{
    /// <summary>
    /// Most captured buffers the pipeline will hold while the send path is slow. Typical
    /// capture devices deliver 10–20 ms buffers, so this is roughly 160–320 ms of backlog:
    /// enough to ride out a brief network stall without dropping, small enough that a
    /// resumed send never bursts more than a fraction of a second of stale audio. Beyond
    /// it the oldest buffer is dropped, per the spec's rule to drop backlog and resume
    /// from live capture rather than burst stale audio.
    /// </summary>
    internal const int MaxBufferedCaptures = 16;

    /// <summary>
    /// Most audio one binary chunk may carry, per the spec's MUST. The capture device chooses
    /// its buffer size and the SDK does not control it, so an oversized buffer is split to this
    /// ceiling rather than assumed never to arrive.
    /// </summary>
    internal const int MaxChunkMilliseconds = 150;

    private readonly IAudioCaptureDevice _capture;
    private readonly ISourceAudioEncoderFactory _encoderFactory;
    private readonly IClockSynchronizer _clock;
    private readonly ILogger _logger;
    private readonly Func<byte[], Task> _sendBinaryAsync;
    private readonly Func<IMessage, Task> _sendMessageAsync;
    private readonly Func<bool> _canStream;
    private readonly string? _configuredCodec;
    private readonly object _lock = new();

    private ISourceAudioEncoder? _encoder;
    private Channel<CapturedAudio>? _captureChannel;
    private Task? _consumerTask;
    private Task _commandChain = Task.CompletedTask;
    private bool _streaming;
    private bool _disposed;

    /// <summary>Whether the source is currently capturing and streaming.</summary>
    public bool IsStreaming { get { lock (_lock) { return _streaming; } } }

    /// <summary>Creates a source pipeline bound to a capture device and the connection's send paths.</summary>
    /// <param name="capture">The device captured audio is read from.</param>
    /// <param name="clock">Maps capture timestamps into the server time domain.</param>
    /// <param name="sendMessageAsync">Sends a control message (client_stream/start, client_stream/end).</param>
    /// <param name="sendBinaryAsync">Sends an encoded binary audio chunk.</param>
    /// <param name="logger">Logger for pipeline diagnostics.</param>
    /// <param name="canStream">
    /// Evaluated immediately before the capture device is opened. Must be false unless the
    /// connection is at trust 'user' and the source role is currently active. Called while the
    /// pipeline's lock is held, so it must not block, await, or take a lock of its own.
    /// </param>
    /// <param name="encoderFactory">Chooses an encoder for the capture format; PCM by default.</param>
    /// <param name="configuredCodec">
    /// Codec to encode captured audio as. Null falls back to the capture device's own format,
    /// which is the previous behaviour.
    /// </param>
    public SourceStreamPipeline(
        IAudioCaptureDevice capture,
        IClockSynchronizer clock,
        Func<IMessage, Task> sendMessageAsync,
        Func<byte[], Task> sendBinaryAsync,
        ILogger logger,
        Func<bool> canStream,
        ISourceAudioEncoderFactory? encoderFactory = null,
        string? configuredCodec = null)
    {
        _capture = capture;
        _clock = clock;
        _sendMessageAsync = sendMessageAsync;
        _sendBinaryAsync = sendBinaryAsync;
        _logger = logger;
        _canStream = canStream;
        _encoderFactory = encoderFactory ?? new DefaultSourceAudioEncoderFactory();
        _configuredCodec = configuredCodec;
    }

    /// <summary>Handles a server <c>source</c> command ('start' or 'stop').</summary>
    /// <remarks>
    /// Commands are chained so they execute one at a time, in arrival order. Without
    /// this, a stop dispatched while a start was still mid-flight saw "not streaming"
    /// and no-opped, leaving the stream up although the server's last word was stop.
    /// The returned task completes (or faults) with that command's own execution.
    /// </remarks>
    public Task HandleCommandAsync(string? command)
    {
        switch (command)
        {
            case "start":
                return EnqueueAsync(StartStreamingCoreAsync);
            case "stop":
                return EnqueueAsync(() => StopStreamingCoreAsync(sendEnd: true));
            default:
                _logger.LogWarning("Unknown source command '{Command}'", command);
                return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Appends a command to the serial chain. FIFO by construction — the chain is
    /// advanced under the lock in call order — which is what makes start/stop/reset
    /// mutually exclusive and keeps them in wire order. A predecessor's failure is
    /// swallowed here (it already faulted its own caller's task); each command's own
    /// failure still faults the task returned to its caller.
    /// </summary>
    private Task EnqueueAsync(Func<Task> command)
    {
        lock (_lock)
        {
            Task run = RunAfterAsync(_commandChain, command);
            _commandChain = run;
            return run;
        }

        static async Task RunAfterAsync(Task previous, Func<Task> command)
        {
            await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await command().ConfigureAwait(false);
        }
    }

    private async Task StartStreamingCoreAsync()
    {
        lock (_lock)
        {
            if (_streaming || _disposed)
                return;

            // Trust gate. Enforced here rather than at the command dispatch so that every
            // caller is covered — the activate path, the server/command path, and any path
            // added later. A source streams potentially sensitive audio, so the spec
            // requires a paired connection.
            if (!_canStream())
            {
                _logger.LogWarning(
                    "Refusing to stream: source@v1 requires user trust and an active source role");
                return;
            }
        }

        ISourceAudioEncoder? encoder = null;
        Channel<CapturedAudio>? channel = null;
        Task? consumer = null;
        bool announced = false;
        bool captureStarted = false;
        try
        {
            var format = _capture.Format;
            // Prefer the configured codec; fall back to the capture format when unset.
            string codec = _configuredCodec ?? format.Codec;
            encoder = _encoderFactory.Create(codec, format);

            var startMessage = new ClientStreamStartMessage
            {
                Payload = new ClientStreamStartPayload
                {
                    Source = new SourceStreamFormat
                    {
                        Codec = encoder.Codec,
                        Channels = format.Channels,
                        SampleRate = format.SampleRate,
                        BitDepth = format.BitDepth ?? 16,
                        CodecHeader = encoder.CodecHeader,
                    },
                },
            };
            await _sendMessageAsync(startMessage);
            announced = true;

            // Captures flow through this bounded channel to one consumer, which is what
            // keeps encode and framing strictly capture-ordered and single-threaded. The
            // encoder interface promises no thread safety (PCM happens to be stateless;
            // Opus will not be), and two sessions' chunks interleaving on the wire is
            // indistinguishable to the server. Note the nonce counter is NOT at risk here:
            // EncodeBinary returns a lazy sequence, so the encrypt runs inside the
            // connection's send lock. DropOldest sheds the stalest backlog when the send
            // path falls behind, so the stream resumes from live capture instead of
            // bursting stale audio — and TryWrite never blocks the capture callback.
            channel = Channel.CreateBounded<CapturedAudio>(
                new BoundedChannelOptions(MaxBufferedCaptures)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.DropOldest,
                },
                dropped => _logger.LogWarning(
                    "Source send path fell behind; dropped a captured buffer ({Bytes} bytes at {CaptureTimeUs}µs) to resume from live capture",
                    dropped.Pcm.Length,
                    dropped.CaptureTimeMicroseconds));
            consumer = ConsumeCapturesAsync(channel.Reader, encoder, format);

            lock (_lock)
            {
                _encoder = encoder;
                _captureChannel = channel;
                _consumerTask = consumer;
            }

            _capture.AudioCaptured += OnAudioCaptured;
            await _capture.StartAsync();
            captureStarted = true;

            lock (_lock)
            {
                // Disposed while starting: the capture device is about to be (or already
                // was) disposed under us, so fail the start and roll back below.
                ObjectDisposedException.ThrowIf(_disposed, this);
                _streaming = true;
            }

            _logger.LogInformation("Source streaming started ({Codec} {SampleRate}Hz x{Channels})",
                encoder.Codec, format.SampleRate, format.Channels);
        }
        catch (Exception)
        {
            // Restore the pre-start state so the failure cannot wedge the pipeline: a
            // later start must not be refused as already-streaming, and a later stop must
            // not end a stream that never began. The failure itself is rethrown, never
            // swallowed — callers (SafeFireAndForget at the dispatch sites) log it.
            _capture.AudioCaptured -= OnAudioCaptured;
            lock (_lock)
            {
                _encoder = null;
                _captureChannel = null;
                _consumerTask = null;
            }

            if (captureStarted)
            {
                try
                {
                    await _capture.StopAsync();
                }
                catch (Exception stopEx)
                {
                    _logger.LogWarning(stopEx, "Error stopping capture device while rolling back a failed start");
                }
            }

            channel?.Writer.TryComplete();
            if (consumer is not null)
            {
                try
                {
                    await consumer;
                }
                catch (Exception consumerEx)
                {
                    _logger.LogWarning(consumerEx, "Source consumer failed while rolling back a failed start");
                }
            }

            encoder?.Dispose();

            if (announced)
            {
                // client_stream/start already hit the wire, so the server believes an
                // input stream is open; close it as part of restoring the pre-start state.
                try
                {
                    await _sendMessageAsync(new ClientStreamEndMessage());
                }
                catch (Exception endEx)
                {
                    _logger.LogWarning(endEx, "Failed to end the half-open source stream while rolling back a failed start");
                }
            }

            throw;
        }
    }

    /// <summary>Stops streaming and ends the input stream. Idempotent.</summary>
    /// <remarks>Chained with any in-flight command, so a stop issued during a start
    /// takes effect once the start completes rather than being lost.</remarks>
    public Task StopStreamingAsync() => EnqueueAsync(() => StopStreamingCoreAsync(sendEnd: true));

    /// <summary>
    /// Per-connection reset (spec: streaming state does not survive reconnection). Tears
    /// down capture and the consumer exactly like a stop, but sends no
    /// <c>client_stream/end</c>: the stream it would end died with the connection, and on
    /// the next connection no input stream is open until the server sends a fresh start.
    /// </summary>
    /// <returns>A task that completes once the reset has been applied.</returns>
    public Task ResetForConnectionLossAsync() => EnqueueAsync(() => StopStreamingCoreAsync(sendEnd: false));

    private async Task StopStreamingCoreAsync(bool sendEnd)
    {
        ISourceAudioEncoder? encoder;
        Channel<CapturedAudio>? channel;
        Task? consumer;
        lock (_lock)
        {
            if (!_streaming)
                return;

            _streaming = false;
            encoder = _encoder;
            channel = _captureChannel;
            consumer = _consumerTask;
            _encoder = null;
            _captureChannel = null;
            _consumerTask = null;
        }

        _capture.AudioCaptured -= OnAudioCaptured;
        try
        {
            await _capture.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping capture device");
        }

        // Let the single consumer drain what was captured before the stop, so every chunk
        // reaches the wire ahead of the end-of-stream message.
        channel?.Writer.TryComplete();
        if (consumer is not null)
        {
            try
            {
                await consumer;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Source consumer completed with an error while stopping");
            }
        }

        if (sendEnd)
        {
            await _sendMessageAsync(new ClientStreamEndMessage());
        }

        encoder?.Dispose();
        _logger.LogInformation("Source streaming stopped");
    }

    private void OnAudioCaptured(object? sender, CapturedAudio captured)
    {
        ChannelWriter<CapturedAudio>? writer;
        lock (_lock)
        {
            writer = _captureChannel?.Writer;
        }

        if (writer is null)
            return;

        // Copy the PCM: the device may reuse its buffer once this handler returns, and
        // encoding now happens later on the consumer. TryWrite on a DropOldest channel
        // always succeeds without blocking — the capture callback must never stall the
        // audio device, so a full channel drops the oldest buffered capture instead.
        writer.TryWrite(new CapturedAudio(captured.Pcm.ToArray(), captured.CaptureTimeMicroseconds));
    }

    /// <summary>
    /// The single consumer: encodes and frames captures strictly in capture order, so the
    /// encoder (stateful for codecs like Opus) and the wire framing (not thread-safe) are
    /// only ever driven from one task at a time. A capture longer than
    /// <see cref="MaxChunkMilliseconds"/> is split across several chunks here. A chunk that
    /// fails to encode or send is logged and the rest of that capture skipped — the same
    /// per-chunk error surface the fire-and-forget path had — so one bad buffer or transient
    /// send failure does not kill the stream.
    /// </summary>
    private async Task ConsumeCapturesAsync(ChannelReader<CapturedAudio> reader, ISourceAudioEncoder encoder, AudioFormat format)
    {
        int bytesPerFrame = format.Channels * ((format.BitDepth ?? 16) / 8);
        int maxChunkBytes = format.SampleRate * MaxChunkMilliseconds / 1000 * bytesPerFrame;

        // A format that cannot express a chunk duration has no ceiling to enforce against;
        // its captures pass through whole. Also what keeps the split arithmetic below —
        // which divides by both terms — off a zero divisor.
        if (maxChunkBytes <= 0)
        {
            maxChunkBytes = int.MaxValue;
        }

        await foreach (CapturedAudio captured in reader.ReadAllAsync())
        {
            try
            {
                if (captured.Pcm.Length <= maxChunkBytes)
                {
                    await SendChunkAsync(encoder.Encode(captured.Pcm.Span), captured.CaptureTimeMicroseconds);
                    continue;
                }

                // Over the ceiling. Split on whole-frame boundaries into equal pieces rather
                // than greedy full-size ones: 400 ms becomes three 133 ms chunks, not
                // 150 + 150 + 100, so a buffer that overshoots the ceiling by a hair cannot
                // leave a sliver of a chunk behind it — the same spec line sets a 5 ms floor.
                // A partial trailing frame, from a device that under-delivers, rides along on
                // the last piece.
                int maxChunkFrames = maxChunkBytes / bytesPerFrame;
                int totalFrames = (captured.Pcm.Length + bytesPerFrame - 1) / bytesPerFrame;
                int pieces = (totalFrames + maxChunkFrames - 1) / maxChunkFrames;
                int framesPerPiece = (totalFrames + pieces - 1) / pieces;

                for (int piece = 0; piece < pieces; piece++)
                {
                    int offset = piece * framesPerPiece * bytesPerFrame;
                    int length = Math.Min(framesPerPiece * bytesPerFrame, captured.Pcm.Length - offset);
                    byte[] encoded = encoder.Encode(captured.Pcm.Span.Slice(offset, length));

                    // Each piece is timestamped at the instant it was captured, not at the
                    // whole buffer's, or the server would resample several chunks onto one
                    // point on its timeline.
                    await SendChunkAsync(
                        encoded,
                        captured.CaptureTimeMicroseconds + ((long)piece * framesPerPiece * 1_000_000 / format.SampleRate));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to encode or send a source chunk captured at {CaptureTimeUs}µs", captured.CaptureTimeMicroseconds);
            }
        }
    }

    /// <summary>
    /// Frames one encoded chunk as a binary type-12 message and sends it, timestamped in the
    /// server time domain. Called only from the single consumer, so the framing stays
    /// single-threaded.
    /// </summary>
    private async Task SendChunkAsync(byte[] encoded, long captureTimeMicroseconds)
    {
        long serverTimestamp = _clock.ClientToServerTime(captureTimeMicroseconds);

        // Binary source chunk: [type 12][int64 BE server timestamp][encoded audio].
        var frame = new byte[9 + encoded.Length];
        frame[0] = BinaryMessageTypes.SourceAudio0;
        BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(1, 8), serverTimestamp);
        encoded.CopyTo(frame.AsSpan(9));

        await _sendBinaryAsync(frame);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        await StopStreamingAsync();
        await _capture.DisposeAsync();
    }
}
