using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Extensions;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Audio.Source;

/// <summary>
/// Drives the <c>source</c> role: on the server's <c>start</c> it opens the capture
/// device, announces the format with <c>client_stream/start</c>, then encodes each
/// captured buffer and streams it as a binary type-12 chunk timestamped in the server
/// time domain; on <c>stop</c> it ends the stream. Server-initiated only — a source
/// never streams unsolicited.
/// </summary>
/// <remarks>
/// Timestamps are the local capture time mapped to the server clock via
/// <see cref="IClockSynchronizer.ClientToServerTime"/> (offset + drift), per the spec.
/// The pipeline does not itself enforce trust; the client service refuses to activate
/// the source role at trust level <c>none</c>.
/// </remarks>
public sealed class SourceStreamPipeline : IAsyncDisposable
{
    private readonly IAudioCaptureDevice _capture;
    private readonly ISourceAudioEncoderFactory _encoderFactory;
    private readonly IClockSynchronizer _clock;
    private readonly ILogger _logger;
    private readonly Func<byte[], Task> _sendBinaryAsync;
    private readonly Func<IMessage, Task> _sendMessageAsync;
    private readonly object _lock = new();

    private ISourceAudioEncoder? _encoder;
    private bool _streaming;
    private bool _disposed;

    /// <summary>Whether the source is currently capturing and streaming.</summary>
    public bool IsStreaming { get { lock (_lock) { return _streaming; } } }

    /// <summary>Creates a source pipeline bound to a capture device and the connection's send paths.</summary>
    public SourceStreamPipeline(
        IAudioCaptureDevice capture,
        IClockSynchronizer clock,
        Func<IMessage, Task> sendMessageAsync,
        Func<byte[], Task> sendBinaryAsync,
        ILogger logger,
        ISourceAudioEncoderFactory? encoderFactory = null)
    {
        _capture = capture;
        _clock = clock;
        _sendMessageAsync = sendMessageAsync;
        _sendBinaryAsync = sendBinaryAsync;
        _logger = logger;
        _encoderFactory = encoderFactory ?? new DefaultSourceAudioEncoderFactory();
    }

    /// <summary>Handles a server <c>source</c> command ('start' or 'stop').</summary>
    public async Task HandleCommandAsync(string? command)
    {
        switch (command)
        {
            case "start":
                await StartStreamingAsync();
                break;
            case "stop":
                await StopStreamingAsync();
                break;
            default:
                _logger.LogWarning("Unknown source command '{Command}'", command);
                break;
        }
    }

    private async Task StartStreamingAsync()
    {
        lock (_lock)
        {
            if (_streaming || _disposed)
                return;
            _streaming = true;
        }

        var format = _capture.Format;
        // Choose an encoder for the capture format (PCM by default).
        _encoder = _encoderFactory.Create(format.Codec, format);

        var startMessage = new ClientStreamStartMessage
        {
            Payload = new ClientStreamStartPayload
            {
                Source = new SourceStreamFormat
                {
                    Codec = _encoder.Codec,
                    Channels = format.Channels,
                    SampleRate = format.SampleRate,
                    BitDepth = format.BitDepth ?? 16,
                    CodecHeader = _encoder.CodecHeader,
                },
            },
        };
        await _sendMessageAsync(startMessage);

        _capture.AudioCaptured += OnAudioCaptured;
        await _capture.StartAsync();
        _logger.LogInformation("Source streaming started ({Codec} {SampleRate}Hz x{Channels})",
            _encoder.Codec, format.SampleRate, format.Channels);
    }

    /// <summary>Stops streaming and ends the input stream. Idempotent.</summary>
    public async Task StopStreamingAsync()
    {
        bool wasStreaming;
        lock (_lock)
        {
            wasStreaming = _streaming;
            _streaming = false;
        }

        if (!wasStreaming)
            return;

        _capture.AudioCaptured -= OnAudioCaptured;
        try
        {
            await _capture.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping capture device");
        }

        await _sendMessageAsync(new ClientStreamEndMessage());
        _encoder?.Dispose();
        _encoder = null;
        _logger.LogInformation("Source streaming stopped");
    }

    private void OnAudioCaptured(object? sender, CapturedAudio captured)
    {
        SourceChunkAsync(captured).SafeFireAndForget(_logger);
    }

    private async Task SourceChunkAsync(CapturedAudio captured)
    {
        ISourceAudioEncoder? encoder;
        lock (_lock)
        {
            if (!_streaming)
                return;
            encoder = _encoder;
        }

        if (encoder is null)
            return;

        byte[] encoded = encoder.Encode(captured.Pcm.Span);
        long serverTimestamp = _clock.ClientToServerTime(captured.CaptureTimeMicroseconds);

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
