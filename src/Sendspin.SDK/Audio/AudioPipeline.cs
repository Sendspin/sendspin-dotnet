// <copyright file="AudioPipeline.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Audio;

/// <summary>
/// Orchestrates the complete audio pipeline from incoming chunks to output.
/// Manages decoder, buffer, and player lifecycle and coordinates their interaction.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline operates in the following states:
/// - Idle: No active stream
/// - Starting: Initializing components for a new stream
/// - Buffering: Accumulating audio before playback starts
/// - Playing: Actively playing audio
/// - Stopping: Shutting down the current stream
/// - Error: An error occurred
/// </para>
/// <para>
/// Audio flow:
/// 1. ProcessAudioChunk receives encoded audio with server timestamp
/// 2. Decoder converts to float PCM samples
/// 3. TimedAudioBuffer stores samples with playback timestamps
/// 4. NAudio reads from buffer when samples are due for playback
/// </para>
/// </remarks>
public sealed class AudioPipeline : IAudioPipeline
{
    private readonly ILogger<AudioPipeline> _logger;
    private readonly IAudioDecoderFactory _decoderFactory;
    private readonly IClockSynchronizer _clockSync;
    private readonly Func<AudioFormat, IClockSynchronizer, ITimedAudioBuffer> _bufferFactory;
    private readonly Func<IAudioPlayer> _playerFactory;
    private readonly Func<ITimedAudioBuffer, Func<long>, IAudioSampleSource> _sourceFactory;
    private readonly IHighPrecisionTimer _precisionTimer;

    /// <summary>
    /// Serializes the four calls that build or tear down the decode chain: <see cref="StartAsync"/>,
    /// <see cref="StopAsync"/>, <see cref="SwitchDeviceAsync"/> and <see cref="DisposeAsync"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All four yield — a real backend's <see cref="IAudioPlayer.InitializeAsync"/> and
    /// <see cref="IAsyncDisposable.DisposeAsync"/> open and close a device — and none of their
    /// callers take turns: <c>stream/start</c> and <c>stream/end</c> are handled off the receive
    /// loop, and an app can dispose the client from its own thread at any moment. Interleaved,
    /// the later call's teardown disposed the player, decoder and ring the earlier one was still
    /// building; the earlier one then resumed onto a null player, and its catch tore down the
    /// components the later call had just built. The pipeline ended in Error, reported
    /// <c>available: false</c>, and stayed silent until the next <c>stream/start</c> — with both
    /// exceptions swallowed at the fire-and-forget boundary.
    /// </para>
    /// <para>
    /// It also makes the keep-vs-restart decision at the top of <see cref="StartAsync"/> mean
    /// something: outside the gate that read could observe the transient Starting or Stopping of
    /// a call still in flight and take the destructive branch against a stream that was in fact
    /// about to be running.
    /// </para>
    /// <para>
    /// Nothing held under this gate may call back into a gated method — <see cref="SemaphoreSlim"/>
    /// is not reentrant — which is why the bodies live in the private <c>...CoreAsync</c> methods
    /// that <see cref="StartAsync"/> and <see cref="DisposeAsync"/> reach directly.
    /// </para>
    /// </remarks>
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    // Terminal once DisposeAsync has run. Read and written only under _lifecycleGate, which is
    // also the barrier that publishes it: a start that was queued behind the dispose must build
    // nothing rather than resurrect a decode chain nobody will ever tear down again.
    private bool _disposed;

    private IAudioDecoder? _decoder;
    private ITimedAudioBuffer? _buffer;
    private IAudioPlayer? _player;
    private IAudioSampleSource? _sampleSource;

    // The decoded ring the last stream used, kept for the next one. See TakeOrCreateBuffer.
    private ITimedAudioBuffer? _retainedBuffer;

    // Set by ClearCore when a seek invalidates the decoder's inter-frame state, taken by
    // ProcessAudioChunk before the next decode. See both for why it is a request rather than
    // a call.
    private bool _decoderResetPending;

    private float[] _decodeBuffer = Array.Empty<float>();
    private AudioFormat? _currentFormat;
    private int _volume = 100;
    private bool _muted;

    // The advertised min_buffer_ms, once the client has reported one. Null leaves whatever the
    // buffer factory configured alone — see SetMinBufferMilliseconds.
    private int? _minBufferMs;
    private long _lastSyncLogTime;
    private bool _usingAudioClock;
    private bool? _lastAudioClockAvailable; // For tracking timing source transitions

    // How often to log sync status during playback (microseconds)
    private const long SyncLogIntervalMicroseconds = 5_000_000; // 5 seconds

    // Clock sync wait configuration
    private readonly bool _waitForConvergence;
    private readonly int _convergenceTimeoutMs;
    private long _bufferReadyTime;
    private bool _loggedSyncWaiting;

    // Chunk arrival tracking for network diagnostics
    private readonly object _chunkStatsLock = new();
    private long _chunksReceived;
    private long _bytesReceived;
    private long _lastChunkArrivalMs;   // Environment.TickCount64 at last chunk arrival
    private double _avgInterArrivalMs;  // EWMA of inter-arrival time
    private double _chunkJitterMs;      // EWMA of |inter-arrival - avg|
    private readonly List<(long TickMs, double GapMs)> _recentGaps = [];
    private double _maxChunkGapMs;

    private const double ChunkEwmaAlpha = 0.1;
    private const long ChunkGapWindowMs = 10_000;

    /// <inheritdoc/>
    public AudioPipelineState State { get; private set; } = AudioPipelineState.Idle;

    /// <inheritdoc/>
    public bool IsReady => _decoder != null && _buffer != null;

    /// <inheritdoc/>
    public AudioBufferStats? BufferStats
    {
        get
        {
            var stats = _buffer?.GetStats();
            if (stats == null)
                return null;

            long chunksReceived, bytesReceived;
            double lastChunkAgeMs, maxChunkGapMs, chunkJitterMs;
            lock (_chunkStatsLock)
            {
                chunksReceived = _chunksReceived;
                bytesReceived = _bytesReceived;
                lastChunkAgeMs = _lastChunkArrivalMs != 0
                    ? Environment.TickCount64 - _lastChunkArrivalMs
                    : 0;
                maxChunkGapMs = _maxChunkGapMs;
                chunkJitterMs = _chunkJitterMs;
            }

            return stats with
            {
                ChunksReceived = chunksReceived,
                BytesReceived = bytesReceived,
                LastChunkAgeMs = lastChunkAgeMs,
                MaxChunkGapMs = maxChunkGapMs,
                ChunkJitterMs = chunkJitterMs,
            };
        }
    }

    /// <inheritdoc/>
    public AudioFormat? CurrentFormat => _currentFormat;

    /// <inheritdoc/>
    public AudioFormat? OutputFormat => _player?.OutputFormat;

    /// <inheritdoc/>
    public int DetectedOutputLatencyMs => _player?.OutputLatencyMs ?? 0;

    /// <inheritdoc/>
    public event EventHandler<AudioPipelineState>? StateChanged;

    /// <inheritdoc/>
    public event EventHandler<AudioPipelineError>? ErrorOccurred;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioPipeline"/> class.
    /// </summary>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="decoderFactory">Factory for creating audio decoders.</param>
    /// <param name="clockSync">Clock synchronizer for timestamp conversion.</param>
    /// <param name="bufferFactory">Factory for creating timed audio buffers.</param>
    /// <param name="playerFactory">Factory for creating audio players.</param>
    /// <param name="sourceFactory">Factory for creating sample sources.</param>
    /// <param name="precisionTimer">High-precision timer for accurate timing (optional, uses shared instance if null).</param>
    /// <param name="waitForConvergence">Whether to wait for clock sync convergence before starting playback (default: true).</param>
    /// <param name="convergenceTimeoutMs">Timeout in milliseconds to wait for clock sync convergence (default: 5000ms).</param>
    /// <param name="useMonotonicTimer">Whether to wrap the timer with monotonicity enforcement for VM resilience (default: true).</param>
    public AudioPipeline(
        ILogger<AudioPipeline> logger,
        IAudioDecoderFactory decoderFactory,
        IClockSynchronizer clockSync,
        Func<AudioFormat, IClockSynchronizer, ITimedAudioBuffer> bufferFactory,
        Func<IAudioPlayer> playerFactory,
        Func<ITimedAudioBuffer, Func<long>, IAudioSampleSource> sourceFactory,
        IHighPrecisionTimer? precisionTimer = null,
        bool waitForConvergence = true,
        int convergenceTimeoutMs = 5000,
        bool useMonotonicTimer = true)
    {
        _logger = logger;
        _decoderFactory = decoderFactory;
        _clockSync = clockSync;
        _bufferFactory = bufferFactory;
        _playerFactory = playerFactory;
        _sourceFactory = sourceFactory;
        _waitForConvergence = waitForConvergence;
        _convergenceTimeoutMs = convergenceTimeoutMs;

        // The MonotonicTimer wrapper is used as a fallback when the player has no audio clock.
        // The audio clock (if available) is selected at playback start.
        var baseTimer = precisionTimer ?? HighPrecisionTimer.Shared;
        if (useMonotonicTimer)
        {
            _precisionTimer = new MonotonicTimer(baseTimer, logger);
            _logger.LogDebug("MonotonicTimer wrapper enabled (will be used as fallback if audio clock unavailable)");
        }
        else
        {
            _precisionTimer = baseTimer;
        }

        if (HighPrecisionTimer.IsHighResolution)
        {
            _logger.LogDebug(
                "Using high-precision timer with {Resolution:F2}ns resolution",
                HighPrecisionTimer.GetResolutionNanoseconds());
        }
        else
        {
            _logger.LogWarning("High-resolution timing not available, sync accuracy may be reduced");
        }
    }

    /// <inheritdoc/>
    public async Task<AudioPipelineStartOutcome> StartAsync(
        AudioFormat format, long? targetTimestamp = null, CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await StartCoreAsync(format, targetTimestamp, cancellationToken);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task<AudioPipelineStartOutcome> StartCoreAsync(
        AudioFormat format, long? targetTimestamp, CancellationToken cancellationToken)
    {
        // A stream/start for a stream that is already running is an in-place configuration update,
        // not a restart: the spec has it update the configuration "without clearing buffers", and
        // the player role adds that such an update continues the existing timeline and does not
        // re-apply the startup lead. Buffered audio dropped here is audio the server never resends,
        // so against a server that transmits far ahead a teardown costs its whole transmit-ahead
        // window in silence (#201). Only Buffering and Playing count as running: the components
        // exist and the timeline is anchored. Idle/Starting/Stopping/Error all take the full path.
        var running = State is AudioPipelineState.Buffering or AudioPipelineState.Playing
            ? _currentFormat
            : null;

        if (running is not null && running.IsSameStreamConfiguration(format))
        {
            // Nothing to reconfigure. The decoder, the buffered audio, the sync anchor, the
            // readiness gate and the pipeline state are all left exactly as they are; only the
            // reported format is swapped for the instance the server just sent, so CurrentFormat
            // reflects the latest announcement (bitrate is outside the comparison).
            _currentFormat = format;
            _logger.LogInformation(
                "[Playback] stream/start re-announced the running format ({Format}); pipeline and buffered audio kept",
                format);
            return AudioPipelineStartOutcome.FormatReannounced;
        }

        // Sample rate and channel count are what the buffer and the output device are built from,
        // so when only the decode side changes (codec, bit depth or codec header) the decoder alone
        // is rebuilt and the audio already decoded ahead of it keeps playing — the .NET shape of
        // the C++ client injecting a new codec-header chunk into its running pipeline.
        //
        // A sample-rate or channel-count change deliberately falls through to the restart below,
        // which does clear the buffer: the deviation the spec allows ("does not clear buffers
        // unless its implementation requires it, and may document its specific behavior"). This
        // pipeline decodes on arrival, so what is buffered is float PCM, and TimedAudioBuffer's
        // ring, timestamps and schedule are all fixed to one rate and channel count at
        // construction — carrying that audio across such a change would mean resampling it into a
        // second buffer while the output device is re-initialized for the new format.
        var decoderOnlyChange = running is not null
            && running.SampleRate == format.SampleRate
            && running.Channels == format.Channels;

        if (!decoderOnlyChange)
        {
            if (State != AudioPipelineState.Idle && State != AudioPipelineState.Error)
            {
                // The non-gated core: this already holds the lifecycle gate, and SemaphoreSlim
                // is not reentrant.
                await StopCoreAsync();
            }

            SetState(AudioPipelineState.Starting);

            // Reset chunk timing state for the new session (monotonic counters are kept)
            lock (_chunkStatsLock)
            {
                _lastChunkArrivalMs = 0;
                _avgInterArrivalMs = 0;
                _chunkJitterMs = 0;
                _recentGaps.Clear();
                _maxChunkGapMs = 0;
            }

            // Without this, a 30 s pause leaves MonotonicTimer 30 s behind real time
            // (forward-jump clamping eats the gap), causing a 30 s delay on resume.
            if (_precisionTimer is MonotonicTimer mt)
            {
                mt.Reset();
            }
        }

        try
        {
            _currentFormat = format;

            // On a decoder-only update the running decoder is replaced, and only once its
            // replacement exists: a factory failure then leaves it in place for the catch below
            // to dispose, instead of leaving the pipeline decoderless.
            var replacedDecoder = decoderOnlyChange ? _decoder : null;
            _decoder = _decoderFactory.Create(format);
            replacedDecoder?.Dispose();
            _decodeBuffer = new float[_decoder.MaxSamplesPerFrame];

            _logger.LogDebug(
                "Decoder created for {Codec}, max frame size: {MaxSamples} samples",
                format.Codec,
                _decoder.MaxSamplesPerFrame);

            if (decoderOnlyChange)
            {
                _logger.LogInformation(
                    "[Playback] In-place stream/start: decoder rebuilt for {Format}, buffered audio and timeline kept",
                    format);
                return AudioPipelineStartOutcome.DecoderReplaced;
            }

            _buffer = TakeOrCreateBuffer(format);

            if (_buffer is TimedAudioBuffer timedBuffer)
            {
                timedBuffer.ReanchorRequired += OnReanchorRequired;
            }

            // The readiness gate must not ask for more audio than the server was told to keep
            // queued, and must not settle for less than the app said it needs.
            if (_minBufferMs.HasValue)
            {
                _buffer.MinBufferMilliseconds = _minBufferMs.Value;
            }

            _player = _playerFactory();
            await _player.InitializeAsync(format, cancellationToken);

            _buffer.OutputLatencyMicroseconds = _player.OutputLatencyMs * 1000L;

            // Used by push-model backends to compensate sync error for the calibrated startup latency.
            _buffer.CalibratedStartupLatencyMicroseconds = _player.CalibratedStartupLatencyMs * 1000L;
            if (_player.CalibratedStartupLatencyMs > 0)
            {
                _logger.LogInformation(
                    "[Playback] Startup latency: {CalibratedMs}ms (output latency: {OutputMs}ms)",
                    _player.CalibratedStartupLatencyMs,
                    _player.OutputLatencyMs);
            }
            else
            {
                _logger.LogDebug(
                    "[Playback] Output latency: {OutputMs}ms, No startup latency compensation",
                    _player.OutputLatencyMs);
            }

            _usingAudioClock = _player.GetAudioClockMicroseconds().HasValue;
            _lastAudioClockAvailable = _usingAudioClock;
            if (_usingAudioClock)
            {
                _buffer.TimingSourceName = "audio-clock";
                _logger.LogInformation("[Timing] Using audio hardware clock for sync timing (VM-immune)");
            }
            else if (_precisionTimer is MonotonicTimer)
            {
                _buffer.TimingSourceName = "monotonic";
                _logger.LogInformation("[Timing] Using MonotonicTimer for sync timing (audio clock not available)");
            }
            else
            {
                _buffer.TimingSourceName = "wall-clock";
                _logger.LogInformation("[Timing] Using wall clock for sync timing (audio clock not available)");
            }

            _sampleSource = _sourceFactory(_buffer, GetCurrentLocalTimeMicroseconds);
            _player.SetSampleSource(_sampleSource);

            // Re-read output latency now that SetSampleSource has initialized the output: some backends
            // (e.g. WASAPI) can only measure their real device latency once the audio client is started,
            // and report an estimate beforehand. The buffer subtracts this from the scheduled start so
            // playback is pre-rolled by the output latency and reaches the speaker on the server's clock.
            _buffer.OutputLatencyMicroseconds = _player.OutputLatencyMs * 1000L;
            _logger.LogDebug("[Playback] Output latency after attach: {OutputMs}ms", _player.OutputLatencyMs);

            _player.Volume = PerceivedVolumeToAmplitude(_volume);
            _player.IsMuted = _muted;

            _player.StateChanged += OnPlayerStateChanged;
            _player.ErrorOccurred += OnPlayerError;

            SetState(AudioPipelineState.Buffering);
            _logger.LogInformation("[Playback] Audio pipeline started: {Format}", format);
            return AudioPipelineStartOutcome.Restarted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start audio pipeline");
            await CleanupAsync();
            SetState(AudioPipelineState.Error);
            ErrorOccurred?.Invoke(this, new AudioPipelineError("Failed to start pipeline", ex));
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// <see cref="StopAsync"/>'s body, for callers that already hold the lifecycle gate.
    /// </summary>
    private async Task StopCoreAsync()
    {
        if (State == AudioPipelineState.Idle)
        {
            return;
        }

        SetState(AudioPipelineState.Stopping);

        await CleanupAsync();

        SetState(AudioPipelineState.Idle);
        _logger.LogInformation("[Playback] Audio pipeline stopped");
    }

    /// <inheritdoc/>
    public void NotifyReconnect()
    {
        _buffer?.NotifyReconnect();
        _player?.NotifyReconnect();

        // A source that corrects (SyncCorrectedSampleSource, or anything else keeping state of
        // its own) suppresses its corrections for the same stabilization window the buffer does.
        // Left out, it keeps correcting against an error the re-converging clock has not
        // finished re-measuring, and the two tiers fight over the same microseconds.
        (_sampleSource as IPlaybackLifecycleAware)?.NotifyReconnect();

        _logger.LogInformation("[Correction] Pipeline notified of reconnect, stabilization period active");
    }

    /// <inheritdoc/>
    public void Clear(long? newTargetTimestamp = null) => ClearCore(resetDecoder: true);

    /// <summary>
    /// Discards everything buffered and re-arms the readiness gate.
    /// </summary>
    /// <param name="resetDecoder">
    /// Whether the decoder's inter-frame state belongs to audio that is being skipped. True for a
    /// <c>stream/clear</c>, where the next packet comes from a new position; false for a
    /// re-anchor, which drops audio this decoder has already produced and then carries straight
    /// on with the next packet of the same stream — see <see cref="OnReanchorRequired"/>.
    /// </param>
    private void ClearCore(bool resetDecoder)
    {
        try
        {
            _buffer?.Clear();

            // Everything upstream of the output has just been reset, so anything the source is
            // still holding belongs to the discarded stream: a primed resampler splices pre-seek
            // audio into the new position, and a half-finished drop/insert interval corrects an
            // error that no longer exists.
            (_sampleSource as IPlaybackLifecycleAware)?.Reset();

            // Reset monotonic timer state to avoid carrying over stale time tracking
            // Only needed when MonotonicTimer is the active timing source (not when using audio clock)
            if (!_usingAudioClock && _precisionTimer is MonotonicTimer monotonicTimer)
            {
                monotonicTimer.Reset();
                _logger.LogDebug("Reset MonotonicTimer state on buffer clear");
            }

            // Requested, not performed. This method has no thread of its own — a stream/clear
            // arrives on the client's stream-lifecycle chain, a re-anchor is raised from a pool
            // thread — and the receive loop may be inside the decoder at this moment. Only Opus
            // has state to reset and Concentus documents its decoder as single-threaded, so the
            // reset is taken in ProcessAudioChunk instead, at the one point that is provably not
            // decoding. PCM and FLAC resets are no-ops either way.
            if (resetDecoder)
            {
                Volatile.Write(ref _decoderResetPending, true);
            }
        }
        finally
        {
            // In a finally so that nothing above — a buffer, a lifecycle-aware source the app
            // supplied — can leave the pipeline reporting Playing over an empty ring. That state
            // is permanent silence: the readiness gate in ProcessAudioChunk only re-starts
            // playback from Buffering, so the pipeline would never play again this stream.
            _bufferReadyTime = 0;
            _loggedSyncWaiting = false;

            if (State == AudioPipelineState.Playing)
            {
                SetState(AudioPipelineState.Buffering);
            }
        }

        _logger.LogDebug("Audio buffer cleared");
    }

    /// <inheritdoc/>
    public void ReanchorTiming()
    {
        // Soft re-anchor: reset the sync-timing anchor (so the next callback re-derives the
        // scheduled start with the current OutputDelayMs) while preserving buffered audio.
        // Same primitive the device-switch path uses — deliberately NOT Clear(), which would
        // dump the buffer and stall for the server's transmit-ahead window.
        if (_buffer is TimedAudioBuffer timedBuffer)
        {
            timedBuffer.ResetSyncTracking();
            _logger.LogDebug("Re-anchored sync timing (buffer preserved)");
        }
    }

    /// <inheritdoc/>
    public void ProcessAudioChunk(AudioChunk chunk)
    {
        if (_decoder == null || _buffer == null)
        {
            _logger.LogWarning("Received audio chunk but pipeline not started");
            return;
        }

        TrackChunkArrival(chunk.EncodedData.Length);

        try
        {
            // A seek asked for the decoder's inter-frame state to go; this is where it goes. See
            // ClearCore on why the request is deferred to here rather than taken on the thread
            // that made it.
            if (Volatile.Read(ref _decoderResetPending))
            {
                Volatile.Write(ref _decoderResetPending, false);
                _decoder.Reset();
            }

            // Decode the audio frame
            var samplesDecoded = _decoder.Decode(chunk.EncodedData, _decodeBuffer);

            if (samplesDecoded > 0)
            {
                // Add decoded samples to buffer with server timestamp
                _buffer.Write(_decodeBuffer.AsSpan(0, samplesDecoded), chunk.ServerTimestamp);

                // Periodically log sync error during playback
                if (State == AudioPipelineState.Playing)
                {
                    LogSyncStatusIfNeeded();
                }

                // Start playback when buffer is ready AND (optionally) clock is synced
                // JS client approach: wait for clock sync convergence to ensure accurate timing
                if (State == AudioPipelineState.Buffering && _buffer.IsReadyForPlayback)
                {
                    if (ShouldWaitForClockSync())
                    {
                        LogSyncWaitingIfNeeded();
                    }
                    else
                    {
                        StartPlayback();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Log but don't crash - one bad frame shouldn't stop the stream
            _logger.LogWarning(ex, "Error processing audio chunk, skipping frame");
        }
    }

    /// <inheritdoc/>
    public void SetVolume(int volume)
    {
        _volume = Math.Clamp(volume, 0, 100);
        if (_player != null)
        {
            _player.Volume = PerceivedVolumeToAmplitude(_volume);
        }

        _logger.LogDebug("Volume set to {Volume}%", _volume);
    }

    /// <summary>
    /// Converts a perceived-loudness volume (0-100) to a linear amplitude (0.0-1.0) using the
    /// perceptual curve <c>(volume/100)^1.5</c>. Per the Sendspin spec, volume values represent
    /// perceived loudness, not linear amplitude (e.g. volume 50 should sound half as loud as 100).
    /// </summary>
    internal static float PerceivedVolumeToAmplitude(int volume) =>
        (float)Math.Pow(Math.Clamp(volume, 0, 100) / 100.0, 1.5);

    /// <inheritdoc/>
    public void SetMuted(bool muted)
    {
        _muted = muted;
        if (_player != null)
        {
            _player.IsMuted = muted;
        }

        _logger.LogDebug("Mute set to {Muted}", muted);
    }

    /// <inheritdoc/>
    public void SetMinBufferMilliseconds(int minBufferMs)
    {
        _minBufferMs = Math.Max(0, minBufferMs);

        if (_buffer != null)
        {
            _buffer.MinBufferMilliseconds = _minBufferMs.Value;
        }

        _logger.LogDebug("[Playback] Readiness gate follows min_buffer_ms={MinBufferMs}ms", _minBufferMs);
    }

    /// <inheritdoc/>
    public async Task SwitchDeviceAsync(string? deviceId, CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await SwitchDeviceCoreAsync(deviceId, cancellationToken);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task SwitchDeviceCoreAsync(string? deviceId, CancellationToken cancellationToken)
    {
        if (_player == null)
        {
            _logger.LogWarning("Cannot switch audio device - pipeline not started");
            return;
        }

        var wasPlaying = State == AudioPipelineState.Playing;

        _logger.LogInformation("Switching audio device, currently {State}", State);

        try
        {
            // Switch the audio device - this stops/restarts playback internally
            await _player.SwitchDeviceAsync(deviceId, cancellationToken);

            // Update the buffer's latency values for the new device
            // The new device may have different latency characteristics
            if (_buffer != null)
            {
                _buffer.OutputLatencyMicroseconds = _player.OutputLatencyMs * 1000L;
                _buffer.CalibratedStartupLatencyMicroseconds = _player.CalibratedStartupLatencyMs * 1000L;
                _logger.LogDebug(
                    "Updated latencies after device switch: output={LatencyMs}ms, calibrated={CalibratedMs}ms",
                    _player.OutputLatencyMs,
                    _player.CalibratedStartupLatencyMs);

                // Trigger a soft re-anchor to reset sync error tracking
                // This prevents the timing discontinuity from causing false sync corrections
                if (_buffer is TimedAudioBuffer timedBuffer)
                {
                    timedBuffer.ResetSyncTracking();
                    _logger.LogDebug("Reset sync tracking after device switch");
                }
            }

            // If we were playing and the player resumed, ensure state is correct
            if (wasPlaying && _player.State == AudioPlayerState.Playing)
            {
                // Reset sync monitoring counters since timing has been reset
                _lastSyncLogTime = _precisionTimer.GetCurrentTimeMicroseconds();

                SetState(AudioPipelineState.Playing);
            }
            else if (wasPlaying)
            {
                // Player didn't resume automatically - might need buffering
                SetState(AudioPipelineState.Buffering);
            }

            _logger.LogInformation(
                "Audio device switched successfully, output latency: {LatencyMs}ms",
                _player.OutputLatencyMs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to switch audio device");
            SetState(AudioPipelineState.Error);
            ErrorOccurred?.Invoke(this, new AudioPipelineError("Failed to switch audio device", ex));
            throw;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            await StopCoreAsync();

            // The ring the last stream left behind has nothing to be reused for now.
            _retainedBuffer?.Dispose();
            _retainedBuffer = null;
        }
        finally
        {
            _lifecycleGate.Release();
        }

        // The semaphore itself is deliberately not disposed: a lifecycle call already waiting on
        // it would then fail with an ObjectDisposedException naming SemaphoreSlim instead of this
        // pipeline, and one arriving later would fail before the _disposed check above could give
        // it the same answer. SemaphoreSlim only holds an unmanaged handle once its
        // AvailableWaitHandle is read, which nothing here does.
    }

    /// <summary>
    /// Returns the buffer for a starting stream: the one the previous stream left behind when it
    /// still fits, otherwise a fresh one from the factory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A buffer's ring is sized from the sample rate and channel count alone (a 30 s default at
    /// 48 kHz stereo is about 11.5 MB of float PCM, straight onto the large object heap), and
    /// <see cref="ITimedAudioBuffer.Clear"/> exists precisely to return one to its
    /// post-construction state. So a stop/start cycle at an unchanged rate and channel count —
    /// every track change on a server that ends the stream between tracks — can keep the
    /// allocation instead of churning it. Anything else about the format is decode-side, which
    /// the buffer never sees: it holds decoded PCM, and the in-place update path above already
    /// keeps the same buffer across a codec or bit-depth change for the same reason.
    /// </para>
    /// <para>
    /// The trade is one ring's worth of memory held while the pipeline is idle, against an
    /// LOH allocation and collection per restart. The buffer is released when the format
    /// genuinely changes, and on <see cref="DisposeAsync"/>.
    /// </para>
    /// <para>
    /// Cumulative counters in <see cref="ITimedAudioBuffer.GetStats"/> (underruns, overruns,
    /// samples corrected) continue across a reuse rather than restarting at zero, matching the
    /// pipeline's own monotonic chunk counters.
    /// </para>
    /// </remarks>
    private ITimedAudioBuffer TakeOrCreateBuffer(AudioFormat format)
    {
        var retained = _retainedBuffer;
        _retainedBuffer = null;

        if (retained is not null)
        {
            if (retained.Format.SampleRate == format.SampleRate
                && retained.Format.Channels == format.Channels)
            {
                retained.Clear();
                _logger.LogDebug(
                    "[Playback] Reusing the decoded buffer for {SampleRate}Hz {Channels}ch",
                    format.SampleRate,
                    format.Channels);
                return retained;
            }

            retained.Dispose();
        }

        return _bufferFactory(format, _clockSync);
    }

    /// <summary>
    /// Gets the current local time in microseconds, preferring audio hardware clock when available.
    /// Used by the sample source to know when to release audio.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Priority: Audio hardware clock (if player provides it) → MonotonicTimer (wall clock fallback).
    /// </para>
    /// <para>
    /// Audio hardware clocks are immune to VM wall clock issues because they run on the
    /// audio device's crystal oscillator, not the hypervisor's timer.
    /// </para>
    /// </remarks>
    private void TrackChunkArrival(int encodedBytes)
    {
        var nowMs = Environment.TickCount64;
        lock (_chunkStatsLock)
        {
            _chunksReceived++;
            _bytesReceived += encodedBytes;

            if (_lastChunkArrivalMs != 0)
            {
                double interArrivalMs = nowMs - _lastChunkArrivalMs;
                if (_avgInterArrivalMs == 0)
                {
                    _avgInterArrivalMs = interArrivalMs;
                }
                else
                {
                    double delta = Math.Abs(interArrivalMs - _avgInterArrivalMs);
                    _chunkJitterMs = ChunkEwmaAlpha * delta + (1 - ChunkEwmaAlpha) * _chunkJitterMs;
                    _avgInterArrivalMs = ChunkEwmaAlpha * interArrivalMs + (1 - ChunkEwmaAlpha) * _avgInterArrivalMs;
                }

                _recentGaps.Add((nowMs, interArrivalMs));
                _recentGaps.RemoveAll(g => nowMs - g.TickMs > ChunkGapWindowMs);
                _maxChunkGapMs = _recentGaps.Count > 0 ? _recentGaps.Max(g => g.GapMs) : 0;
            }

            _lastChunkArrivalMs = nowMs;
        }
    }

    private long GetCurrentLocalTimeMicroseconds()
    {
        // Try audio hardware clock first (VM-immune)
        var audioClockTime = _player?.GetAudioClockMicroseconds();
        var audioClockAvailable = audioClockTime.HasValue;

        // Log timing source transitions (only after initial setup)
        if (_lastAudioClockAvailable.HasValue && audioClockAvailable != _lastAudioClockAvailable.Value)
        {
            var fromSource = _lastAudioClockAvailable.Value ? "audio-clock" : (_precisionTimer is MonotonicTimer ? "monotonic" : "wall-clock");
            var toSource = audioClockAvailable ? "audio-clock" : (_precisionTimer is MonotonicTimer ? "monotonic" : "wall-clock");
            _logger.LogInformation("[Timing] Source changed: {FromSource} → {ToSource}", fromSource, toSource);

            // Update buffer's timing source name
            if (_buffer != null)
            {
                _buffer.TimingSourceName = toSource;
            }
        }

        _lastAudioClockAvailable = audioClockAvailable;

        if (audioClockAvailable)
        {
            return audioClockTime!.Value;
        }

        // Fall back to MonotonicTimer (filtered wall clock)
        return _precisionTimer.GetCurrentTimeMicroseconds();
    }

    private void StartPlayback()
    {
        if (_player == null)
        {
            return;
        }

        try
        {
            var syncStatus = _clockSync.GetStatus();
            _player.Play();

            // Reset sync monitoring counter
            _lastSyncLogTime = _precisionTimer.GetCurrentTimeMicroseconds();

            SetState(AudioPipelineState.Playing);
            _logger.LogInformation(
                "[Playback] Starting playback: buffer={BufferMs:F0}ms, sync offset={OffsetMs:F2}ms (±{UncertaintyMs:F2}ms), " +
                "output latency={OutputLatencyMs}ms, timer resolution={ResolutionNs:F0}ns",
                _buffer?.BufferedMilliseconds ?? 0,
                syncStatus.OffsetMilliseconds,
                syncStatus.OffsetUncertaintyMicroseconds / 1000.0,
                DetectedOutputLatencyMs,
                HighPrecisionTimer.GetResolutionNanoseconds());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start playback");
            ErrorOccurred?.Invoke(this, new AudioPipelineError("Failed to start playback", ex));
        }
    }

    /// <summary>
    /// Determines whether we should wait for clock sync convergence before starting playback.
    /// </summary>
    /// <returns>True if we should wait, false if we can proceed with playback.</returns>
    private bool ShouldWaitForClockSync()
    {
        // If wait is disabled, always proceed
        if (!_waitForConvergence)
        {
            return false;
        }

        // If clock has minimal sync (2+ measurements), proceed
        // Full convergence happens in background, sync correction handles any estimation errors
        if (_clockSync.HasMinimalSync)
        {
            return false;
        }

        // Track when buffer first became ready (for timeout calculation)
        if (_bufferReadyTime == 0)
        {
            _bufferReadyTime = _precisionTimer.GetCurrentTimeMicroseconds();
        }

        // Check for timeout - proceed anyway if we've waited too long
        var elapsed = _precisionTimer.GetCurrentTimeMicroseconds() - _bufferReadyTime;
        if (elapsed > _convergenceTimeoutMs * 1000L)
        {
            var status = _clockSync.GetStatus();
            _logger.LogWarning(
                "[ClockSync] Timeout after {ElapsedMs}ms. Starting playback without full convergence. " +
                "Measurements: {Count}, Uncertainty: {Uncertainty:F2}ms",
                elapsed / 1000,
                status.MeasurementCount,
                status.OffsetUncertaintyMicroseconds / 1000.0);
            return false; // Timeout - proceed anyway
        }

        return true; // Still waiting for convergence
    }

    /// <summary>
    /// Logs that we're waiting for clock sync convergence (only once per wait period).
    /// </summary>
    private void LogSyncWaitingIfNeeded()
    {
        if (!_loggedSyncWaiting)
        {
            _loggedSyncWaiting = true;
            var status = _clockSync.GetStatus();
            _logger.LogInformation(
                "[ClockSync] Buffer ready ({BufferMs:F0}ms), waiting for convergence. " +
                "Measurements: {Count}, Uncertainty: {Uncertainty:F2}ms, Converged: {Converged}",
                _buffer?.BufferedMilliseconds ?? 0,
                status.MeasurementCount,
                status.OffsetUncertaintyMicroseconds / 1000.0,
                status.IsConverged);
        }
    }

    private async Task CleanupAsync()
    {
        // Unsubscribe from events
        if (_player != null)
        {
            _player.StateChanged -= OnPlayerStateChanged;
            _player.ErrorOccurred -= OnPlayerError;
            _player.Stop();
            await _player.DisposeAsync();
            _player = null;
        }

        // Unsubscribe from buffer events
        if (_buffer is TimedAudioBuffer timedBuffer)
        {
            timedBuffer.ReanchorRequired -= OnReanchorRequired;
        }

        _decoder?.Dispose();
        _decoder = null;

        // Kept rather than disposed: the next stream may be able to reuse the ring instead of
        // allocating another. See TakeOrCreateBuffer, which decides and releases this one.
        _retainedBuffer = _buffer;
        _buffer = null;

        _sampleSource = null;
        _decodeBuffer = Array.Empty<float>();
        _currentFormat = null;
    }

    /// <summary>
    /// Handles the buffer giving up on its current anchor: everything buffered is discarded and
    /// the pipeline goes back to buffering, so the next audio is scheduled from a fresh anchor.
    /// </summary>
    /// <remarks>
    /// Unlike a <c>stream/clear</c>, this leaves the decoder alone. A re-anchor discards audio the
    /// decoder has already produced and then carries on with the next packet of the same stream —
    /// nothing is skipped on the encoded side — so resetting would throw away inter-frame state
    /// that is still exactly right for the packet about to arrive, and manufacture a discontinuity
    /// at the resume where the codec had none. (Only Opus has such state; PCM and FLAC frames are
    /// self-contained.)
    /// </remarks>
    private void OnReanchorRequired(object? sender, EventArgs e)
    {
        var stats = _buffer?.GetStats();
        _logger.LogWarning(
            "[Correction] Re-anchor required: sync error {SyncErrorMs:F1}ms exceeds threshold. " +
            "Dropped={Dropped}, Inserted={Inserted}. Clearing buffer for resync.",
            stats?.SyncErrorMs ?? 0,
            stats?.SamplesDroppedForSync ?? 0,
            stats?.SamplesInsertedForSync ?? 0);

        // Clear and restart buffering
        ClearCore(resetDecoder: false);
    }

    private void OnPlayerStateChanged(object? sender, AudioPlayerState state)
    {
        if (state == AudioPlayerState.Error)
        {
            SetState(AudioPipelineState.Error);
        }
    }

    private void OnPlayerError(object? sender, AudioPlayerError error)
    {
        _logger.LogError(error.Exception, "Player error: {Message}", error.Message);
        ErrorOccurred?.Invoke(this, new AudioPipelineError(error.Message, error.Exception));
    }

    private void SetState(AudioPipelineState newState)
    {
        if (State != newState)
        {
            _logger.LogDebug("Pipeline state: {OldState} -> {NewState}", State, newState);
            State = newState;
            StateChanged?.Invoke(this, newState);
        }
    }

    /// <summary>
    /// Logs sync status periodically during playback for monitoring drift.
    /// </summary>
    private void LogSyncStatusIfNeeded()
    {
        var currentTime = _precisionTimer.GetCurrentTimeMicroseconds();

        // Only log every SyncLogIntervalMicroseconds
        if (currentTime - _lastSyncLogTime < SyncLogIntervalMicroseconds)
        {
            return;
        }

        _lastSyncLogTime = currentTime;
        var stats = _buffer?.GetStats();
        var clockStatus = _clockSync.GetStatus();

        if (stats is { IsPlaybackActive: true })
        {
            var syncErrorMs = stats.SyncErrorMs;
            var absError = Math.Abs(syncErrorMs);

            // Format correction mode for logging
            var correctionInfo = stats.CurrentCorrectionMode switch
            {
                SyncCorrectionMode.Dropping => $"DROPPING (dropped={stats.SamplesDroppedForSync})",
                SyncCorrectionMode.Inserting => $"INSERTING (inserted={stats.SamplesInsertedForSync})",
                SyncCorrectionMode.HardSync => $"HARD SYNC (snaps={stats.HardSyncCount})",
                _ => "none",
            };

            // Calculate derived values for debugging
            var samplesReadTimeMs = stats.SamplesReadSinceStart * (1000.0 / (_currentFormat!.SampleRate * _currentFormat.Channels));

            // Get drift status for enhanced diagnostics
            var driftInfo = clockStatus.IsDriftReliable
                ? $"drift={clockStatus.DriftMicrosecondsPerSecond:+0.0;-0.0}μs/s"
                : "drift=pending";

            // Get timer info for diagnostics - only include MonotonicTimer stats when it's the active source
            string timerInfo;
            if (_usingAudioClock)
            {
                timerInfo = "audio-clock";
            }
            else if (_precisionTimer is MonotonicTimer mt)
            {
                timerInfo = $"monotonic: {mt.GetStatsSummary()}";
            }
            else
            {
                timerInfo = "wall-clock";
            }

            // Use appropriate log level based on sync error magnitude
            if (absError > 50) // > 50ms - significant drift
            {
                _logger.LogWarning(
                    "[SyncError] Drift: error={SyncErrorMs:+0.00;-0.00}ms, elapsed={Elapsed:F0}ms, readTime={ReadTime:F0}ms, " +
                    "latencyComp={Latency}ms, {DriftInfo}, correction={Correction}, buffer={BufferMs:F0}ms, timing=[{TimerInfo}]",
                    syncErrorMs,
                    stats.ElapsedSinceStartMs,
                    samplesReadTimeMs,
                    _buffer?.OutputLatencyMicroseconds / 1000 ?? 0,
                    driftInfo,
                    correctionInfo,
                    stats.BufferedMs,
                    timerInfo);
            }
            else if (absError > 10) // > 10ms - noticeable
            {
                _logger.LogInformation(
                    "[SyncError] Status: error={SyncErrorMs:+0.00;-0.00}ms, elapsed={Elapsed:F0}ms, readTime={ReadTime:F0}ms, " +
                    "{DriftInfo}, correction={Correction}, buffer={BufferMs:F0}ms",
                    syncErrorMs,
                    stats.ElapsedSinceStartMs,
                    samplesReadTimeMs,
                    driftInfo,
                    correctionInfo,
                    stats.BufferedMs);
            }
            else // < 10ms - good sync
            {
                _logger.LogDebug(
                    "[SyncError] OK: error={SyncErrorMs:+0.00;-0.00}ms, {DriftInfo}, buffer={BufferMs:F0}ms",
                    syncErrorMs,
                    driftInfo,
                    stats.BufferedMs);
            }
        }
    }
}
