// <copyright file="IAudioPlayer.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Sendspin.SDK.Models;

namespace Sendspin.SDK.Audio;

/// <summary>
/// Manages audio output device and playback lifecycle.
/// </summary>
public interface IAudioPlayer : IAsyncDisposable
{
    /// <summary>
    /// Gets the current playback state.
    /// </summary>
    AudioPlayerState State { get; }

    /// <summary>
    /// Gets or sets the output volume (0.0 to 1.0).
    /// </summary>
    float Volume { get; set; }

    /// <summary>
    /// Gets or sets whether output is muted.
    /// </summary>
    bool IsMuted { get; set; }

    /// <summary>
    /// Gets the detected output latency in milliseconds: the delay between handing a sample to the
    /// output and it reaching the speaker (output buffer + engine).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Since 9.0.6 this is <b>applied to the playback schedule</b> - the buffer pre-rolls the scheduled
    /// start by this amount (see <see cref="ITimedAudioBuffer.OutputLatencyMicroseconds"/>) so audio
    /// reaches the speaker on the server's clock. An implementation that reports a real latency therefore
    /// keeps multi-room alignment automatically and must NOT also compensate it via the output delay. Return
    /// 0 if the latency is unknown or already accounted for elsewhere.
    /// </para>
    /// <para>
    /// The value should be stable by the time playback starts. Some backends (e.g. WASAPI) can only
    /// measure their real latency once the audio client is started; the pipeline re-reads this after the
    /// sample source is attached, so reporting an estimate from <see cref="InitializeAsync"/> and the
    /// measured value thereafter is supported.
    /// </para>
    /// </remarks>
    int OutputLatencyMs { get; }

    /// <summary>
    /// Gets the calibrated startup latency in milliseconds for push-model audio backends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This represents the measured time from first audio write to actual playback start
    /// on push-model backends (like ALSA) that must pre-fill their output buffer.
    /// </para>
    /// <para>
    /// Pull-model backends (like WASAPI) should return 0 since they don't pre-fill.
    /// </para>
    /// <para>
    /// This value is used by <see cref="ITimedAudioBuffer"/> to compensate for the
    /// constant negative sync error caused by buffer prefill.
    /// </para>
    /// </remarks>
    int CalibratedStartupLatencyMs => 0;

    /// <summary>
    /// Gets the current output audio format, or null if not initialized.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This represents the audio format being sent to the audio output device.
    /// It is available after <see cref="InitializeAsync"/> completes.
    /// </para>
    /// <para>
    /// The output format may differ from the input format if resampling or
    /// format conversion is applied by the audio subsystem.
    /// </para>
    /// </remarks>
    AudioFormat? OutputFormat => null;

    /// <summary>
    /// Gets the current playback time from the audio hardware clock in microseconds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implementations should return time based on their audio backend's hardware clock,
    /// which is immune to VM wall clock issues:
    /// </para>
    /// <list type="bullet">
    /// <item><description>PortAudio: <c>outputBufferDacTime * 1_000_000</c></description></item>
    /// <item><description>PulseAudio: <c>pa_stream_get_time()</c></description></item>
    /// <item><description>ALSA: <c>snd_pcm_htimestamp()</c></description></item>
    /// <item><description>CoreAudio: <c>AudioTimeStamp.mHostTime</c></description></item>
    /// </list>
    /// <para>
    /// Return <c>null</c> if hardware clock is not available (e.g., WASAPI shared mode).
    /// The SDK will fall back to wall clock timing with MonotonicTimer filtering.
    /// </para>
    /// </remarks>
    /// <returns>
    /// Audio hardware clock time in microseconds, or <c>null</c> to use wall clock fallback.
    /// </returns>
    long? GetAudioClockMicroseconds() => null;

    /// <summary>
    /// Gets the delay, in microseconds, that a sample handed to the output at this instant will see
    /// before it is heard: whatever is already queued in the device ahead of it, plus the device's
    /// fixed latency after its queue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implement this on a push-mode output that keeps a fixed device buffer topped up (WASAPI
    /// through NAudio's <c>WasapiOut</c>, ALSA <c>snd_pcm_writei</c> loops, and the like). On such an
    /// output <see cref="OutputLatencyMs"/> is the depth of the device buffer, and a sample only
    /// waits that long when the buffer is full ahead of it. On the first fill of an empty device
    /// nothing is queued, so the first sample is heard at once; pre-rolling the schedule by the
    /// whole buffer there starts playback early by that much, and it stays early. Reporting what
    /// is actually queued lets the schedule use the real figure at the moment playback starts.
    /// </para>
    /// <para>
    /// Count everything that will play before the sample, including frames already produced for
    /// the device request in progress but not yet handed to the device: one request can reach the
    /// SDK as several reads, and a later read's samples play behind the earlier ones.
    /// </para>
    /// <para>
    /// Return <c>null</c> when the output's latency does not depend on how full it is - a
    /// pull-model output whose callback is followed by a fixed hardware latency.
    /// <see cref="OutputLatencyMs"/> is then used, as it is when this member is not implemented.
    /// </para>
    /// <para>
    /// Called from the audio callback, with the buffer's lock held, on each read while playback is
    /// waiting to start. It must be cheap, must not block, and must not call back into the buffer
    /// or the pipeline.
    /// </para>
    /// </remarks>
    /// <returns>
    /// The current output latency in microseconds, or <c>null</c> to use <see cref="OutputLatencyMs"/>.
    /// </returns>
    long? GetCurrentOutputLatencyMicroseconds() => null;

    /// <summary>
    /// Notifies the player that a WebSocket reconnect occurred.
    /// Implementations should forward this to their sync correction provider.
    /// </summary>
    /// <remarks>
    /// Default implementation is a no-op. Override in platform-specific players
    /// that use <see cref="ISyncCorrectionProvider"/> for external sync correction.
    /// </remarks>
    void NotifyReconnect() { }

    /// <summary>
    /// Initializes the audio output with the specified format.
    /// </summary>
    /// <param name="format">Audio format to use.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    Task InitializeAsync(AudioFormat format, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the sample provider that supplies audio data.
    /// </summary>
    /// <param name="source">The audio sample source.</param>
    void SetSampleSource(IAudioSampleSource source);

    /// <summary>
    /// Starts audio playback.
    /// </summary>
    void Play();

    /// <summary>
    /// Pauses audio playback.
    /// </summary>
    void Pause();

    /// <summary>
    /// Stops audio playback and resets.
    /// </summary>
    void Stop();

    /// <summary>
    /// Switches to a different audio output device.
    /// </summary>
    /// <param name="deviceId">The device ID to switch to, or null for system default.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    /// <remarks>
    /// This will briefly stop playback while reinitializing the audio output.
    /// The sample source is preserved, so playback resumes from the current position.
    /// </remarks>
    Task SwitchDeviceAsync(string? deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Event raised when playback state changes.
    /// </summary>
    event EventHandler<AudioPlayerState>? StateChanged;

    /// <summary>
    /// Event raised on playback errors.
    /// </summary>
    event EventHandler<AudioPlayerError>? ErrorOccurred;
}

/// <summary>
/// Audio player playback states.
/// </summary>
public enum AudioPlayerState
{
    /// <summary>
    /// Player has not been initialized.
    /// </summary>
    Uninitialized,

    /// <summary>
    /// Player is stopped.
    /// </summary>
    Stopped,

    /// <summary>
    /// Player is actively playing audio.
    /// </summary>
    Playing,

    /// <summary>
    /// Player is paused.
    /// </summary>
    Paused,

    /// <summary>
    /// Player encountered an error.
    /// </summary>
    Error,
}

/// <summary>
/// Represents an audio player error.
/// </summary>
/// <param name="Message">Error message.</param>
/// <param name="Exception">Optional exception that caused the error.</param>
public record AudioPlayerError(string Message, Exception? Exception = null);
