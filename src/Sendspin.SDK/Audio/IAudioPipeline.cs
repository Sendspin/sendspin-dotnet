// <copyright file="IAudioPipeline.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol;

namespace Sendspin.SDK.Audio;

/// <summary>
/// Orchestrates the complete audio pipeline from incoming chunks to output.
/// </summary>
public interface IAudioPipeline : IAsyncDisposable
{
    /// <summary>
    /// Gets the current pipeline state.
    /// </summary>
    AudioPipelineState State { get; }

    /// <summary>
    /// Gets whether the pipeline is ready to accept audio chunks.
    /// </summary>
    /// <remarks>
    /// Returns true when the decoder and buffer have been initialized and can process chunks.
    /// Use this to check before calling <see cref="ProcessAudioChunk"/> to avoid chunk loss.
    /// </remarks>
    bool IsReady { get; }

    /// <summary>
    /// Gets the current buffer statistics, or null if not started.
    /// </summary>
    AudioBufferStats? BufferStats { get; }

    /// <summary>
    /// Gets the current audio format being decoded (incoming format), or null if not streaming.
    /// </summary>
    /// <remarks>
    /// This represents the format of the audio stream as received from the server,
    /// before any processing or conversion by the audio pipeline.
    /// </remarks>
    AudioFormat? CurrentFormat { get; }

    /// <summary>
    /// Gets the audio format being sent to the output device, or null if not playing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This represents the format of audio data being written to the audio output device.
    /// In most cases, this matches <see cref="CurrentFormat"/> but with PCM encoding,
    /// as all codecs are decoded to PCM before output.
    /// </para>
    /// <para>
    /// This value is available after the pipeline has started playing.
    /// </para>
    /// </remarks>
    AudioFormat? OutputFormat => null;

    /// <summary>
    /// Gets the detected audio output latency in milliseconds.
    /// This value is available after the pipeline has started.
    /// </summary>
    /// <remarks>
    /// This latency represents the buffer delay between when audio is submitted
    /// to the audio output and when it is actually played through the speakers.
    /// It can be used to automatically compensate for audio output delay.
    /// </remarks>
    int DetectedOutputLatencyMs { get; }

    /// <summary>
    /// Starts the pipeline with the specified stream format.
    /// Called when stream/start is received.
    /// </summary>
    /// <remarks>
    /// A <c>stream/start</c> for a stream that is already running is a configuration update, not a
    /// restart: implementations must apply it without clearing buffered audio, and must continue
    /// the existing timeline rather than re-anchoring it or re-applying the startup lead, since the
    /// server does neither. Buffers may be cleared only where the change genuinely requires it —
    /// see <see cref="AudioPipeline"/> for what the shipped pipeline updates in place and what it
    /// restarts for.
    /// </remarks>
    /// <param name="format">Audio format for the stream.</param>
    /// <param name="targetTimestamp">Optional target timestamp for playback alignment.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// What the call did to the running stream. The implementation decides this — how much of the
    /// decode chain a given format change forces it to rebuild is its own business — and reports
    /// it so a caller holding audio for the stream can tell whether that audio survived, without
    /// re-deriving the decision from the pipeline's state and format.
    /// </returns>
    Task<AudioPipelineStartOutcome> StartAsync(
        AudioFormat format, long? targetTimestamp = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the pipeline.
    /// Called when stream/end is received.
    /// </summary>
    /// <returns>A task representing the async operation.</returns>
    Task StopAsync();

    /// <summary>
    /// Notifies the pipeline that a WebSocket reconnect occurred.
    /// Suppresses sync corrections during the reconnect stabilization period.
    /// </summary>
    /// <remarks>
    /// Call this after the clock synchronizer is reset on reconnect. The buffer,
    /// the player and a sample source implementing <see cref="IPlaybackLifecycleAware"/>
    /// will suppress corrections until the Kalman filter has had time to re-converge
    /// (~2 seconds by default).
    /// </remarks>
    void NotifyReconnect();

    /// <summary>
    /// Clears the buffer (for seek).
    /// Called when stream/clear is received.
    /// </summary>
    /// <remarks>
    /// Resets the decoder and a sample source implementing
    /// <see cref="IPlaybackLifecycleAware"/> along with the buffer: everything holding audio or
    /// correction state from the discarded stream, so none of it is spliced into the new one.
    /// </remarks>
    /// <param name="newTargetTimestamp">Optional new target timestamp.</param>
    void Clear(long? newTargetTimestamp = null);

    /// <summary>
    /// Re-anchors playback timing without discarding buffered audio.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Resets the sync-timing anchor so the next callback re-derives the scheduled start from the
    /// current clock state — picking up a changed <see cref="Synchronization.IClockSynchronizer.OutputDelayMs"/> —
    /// while keeping all buffered audio.
    /// </para>
    /// <para>
    /// Use this to apply a static-delay change mid-playback. Unlike <see cref="Clear"/>, it does not
    /// dump the buffer, so playback continues from the already-buffered audio (shifted by the new
    /// delay) instead of stalling to refill. That matters with servers that transmit far ahead of
    /// playback, where <see cref="Clear"/> leaves the buffer waiting the full transmit-ahead window
    /// (tens of seconds) for re-received, future-timestamped audio.
    /// </para>
    /// </remarks>
    void ReanchorTiming();

    /// <summary>
    /// Processes an incoming audio chunk.
    /// </summary>
    /// <param name="chunk">The audio chunk to process.</param>
    void ProcessAudioChunk(AudioChunk chunk);

    /// <summary>
    /// Sets volume (0-100).
    /// </summary>
    /// <param name="volume">Volume level.</param>
    void SetVolume(int volume);

    /// <summary>
    /// Sets mute state.
    /// </summary>
    /// <param name="muted">Whether to mute.</param>
    void SetMuted(bool muted);

    /// <summary>
    /// Applies the <c>min_buffer_ms</c> the client advertises to the buffer's readiness gate,
    /// now and for every stream started afterwards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The SDK calls this from the client with <c>ClientCapabilities.MinBufferMs</c>, and again
    /// whenever <c>ISendspinClient.UpdateTimingAsync</c> changes it — the spec lets a client
    /// update its timing parameters at any time (roles/player/v1.md:68). Without it an app
    /// advertising 500 ms would still start at the 150 ms default, before the audio it told the
    /// server it needs has arrived.
    /// </para>
    /// <para>
    /// Until it is called, the buffer keeps whatever
    /// <see cref="ITimedAudioBuffer.MinBufferMilliseconds"/> its factory gave it.
    /// </para>
    /// </remarks>
    /// <param name="minBufferMs">Advertised minimum ongoing buffer depth, in milliseconds.</param>
    void SetMinBufferMilliseconds(int minBufferMs);

    /// <summary>
    /// Switches to a different audio output device.
    /// </summary>
    /// <param name="deviceId">The device ID to switch to, or null for system default.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    /// <remarks>
    /// <para>
    /// This will briefly interrupt playback while reinitializing the audio output.
    /// The audio buffer is preserved, so playback resumes from approximately the same position.
    /// </para>
    /// <para>
    /// After switching, the sync timing is re-anchored to account for any timing
    /// discontinuity during the device switch.
    /// </para>
    /// </remarks>
    Task SwitchDeviceAsync(string? deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Event raised when pipeline state changes.
    /// </summary>
    event EventHandler<AudioPipelineState>? StateChanged;

    /// <summary>
    /// Event raised on pipeline errors.
    /// </summary>
    event EventHandler<AudioPipelineError>? ErrorOccurred;
}

/// <summary>
/// What an <see cref="IAudioPipeline.StartAsync"/> call did to the stream that was running, which
/// is what decides whether audio already held for that stream is still playable.
/// </summary>
public enum AudioPipelineStartOutcome
{
    /// <summary>
    /// The decode chain was built from scratch — the ordinary cold start, and any format change
    /// the implementation could not apply in place. Audio encoded for the previous stream cannot
    /// be fed to it. An implementation may still recycle the decoded ring's allocation across
    /// such a start, as <see cref="AudioPipeline"/> does; it is cleared on the way, so nothing
    /// buffered survives and this remains the outcome that discards.
    /// </summary>
    Restarted = 0,

    /// <summary>
    /// Only the decoder was rebuilt: audio decoded before the change, the timeline and the output
    /// device were all kept. Audio still encoded for the previous stream cannot be fed to the new
    /// decoder, so a caller holding any must drop it, as for <see cref="Restarted"/>.
    /// </summary>
    DecoderReplaced = 1,

    /// <summary>
    /// The running format was re-announced and nothing was rebuilt (spec: a configuration update
    /// "without clearing buffers"). The stream continues, so audio held for it is still its own.
    /// </summary>
    FormatReannounced = 2,
}

/// <summary>
/// Audio pipeline states.
/// </summary>
public enum AudioPipelineState
{
    /// <summary>
    /// Pipeline is idle, not processing audio.
    /// </summary>
    Idle,

    /// <summary>
    /// Pipeline is starting up.
    /// </summary>
    Starting,

    /// <summary>
    /// Pipeline is buffering audio before playback.
    /// </summary>
    Buffering,

    /// <summary>
    /// Pipeline is actively playing audio.
    /// </summary>
    Playing,

    /// <summary>
    /// Pipeline is stopping.
    /// </summary>
    Stopping,

    /// <summary>
    /// Pipeline encountered an error.
    /// </summary>
    Error,
}

/// <summary>
/// Represents an audio pipeline error.
/// </summary>
/// <param name="Message">Error message.</param>
/// <param name="Exception">Optional exception that caused the error.</param>
public record AudioPipelineError(string Message, Exception? Exception = null);
