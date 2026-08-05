using Sendspin.SDK.Audio.Source;
using Sendspin.SDK.Models;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Capture-device double. <see cref="Capturing"/> is the assertion that matters for
/// the source trust gate: it records whether the device was ever actually opened.
/// </summary>
internal sealed class FakeCaptureDevice : IAudioCaptureDevice
{
    public AudioFormat Format { get; } = new() { Codec = "pcm", SampleRate = 48000, Channels = 2, BitDepth = 16 };
    public bool Capturing { get; private set; }
    public event EventHandler<CapturedAudio>? AudioCaptured;

    public Task StartAsync(CancellationToken ct = default) { Capturing = true; return Task.CompletedTask; }
    public Task StopAsync(CancellationToken ct = default) { Capturing = false; return Task.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Emit(byte[] pcm, long captureTimeUs) =>
        AudioCaptured?.Invoke(this, new CapturedAudio(pcm, captureTimeUs));
}
