using Sendspin.SDK.Models;

namespace Sendspin.SDK.Audio.Source;

/// <summary>
/// Captures audio from a local input (line-in, AUX, microphone, loopback) for the
/// <c>source</c> role. The application implements this per platform (WASAPI, ALSA, …),
/// mirroring <see cref="IAudioPlayer"/> on the output side.
/// </summary>
/// <remarks>
/// The device delivers interleaved PCM frames, at the bit depth its <see cref="Format"/>
/// declares (16 when that leaves it unset), with a local capture timestamp
/// (microseconds, in the same clock domain the SDK feeds the time filter). The SDK
/// timestamps, encodes, and streams them; the device only captures.
/// </remarks>
public interface IAudioCaptureDevice : IAsyncDisposable
{
    /// <summary>The capture format this device produces once started.</summary>
    AudioFormat Format { get; }

    /// <summary>
    /// Raised for each captured buffer: interleaved little-endian PCM samples at
    /// <see cref="Format"/>'s bit depth, with the local capture time (microseconds) of the
    /// first sample.
    /// </summary>
    event EventHandler<CapturedAudio>? AudioCaptured;

    /// <summary>Begins capturing. Frames flow via <see cref="AudioCaptured"/> until stopped.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops capturing.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>A captured audio buffer with its local capture timestamp.</summary>
/// <param name="Pcm">Interleaved little-endian PCM samples at the device format's bit depth.</param>
/// <param name="CaptureTimeMicroseconds">Local capture time of the first sample (µs).</param>
public readonly record struct CapturedAudio(ReadOnlyMemory<byte> Pcm, long CaptureTimeMicroseconds);
