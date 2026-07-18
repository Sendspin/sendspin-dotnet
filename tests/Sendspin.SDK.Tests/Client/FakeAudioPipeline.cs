using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Minimal <see cref="IAudioPipeline"/> test double. Tests drive the client's error/recovery
/// signaling by calling <see cref="RaiseError"/> and <see cref="SetState"/>.
/// </summary>
internal sealed class FakeAudioPipeline : IAudioPipeline
{
    public AudioPipelineState State { get; private set; } = AudioPipelineState.Idle;
    public bool IsReady => true;
    public AudioBufferStats? BufferStats => null;
    public AudioFormat? CurrentFormat => null;
    public int DetectedOutputLatencyMs => 0;

    public event EventHandler<AudioPipelineState>? StateChanged;
    public event EventHandler<AudioPipelineError>? ErrorOccurred;

    public void RaiseError(string message = "underrun") => ErrorOccurred?.Invoke(this, new AudioPipelineError(message));

    public void SetState(AudioPipelineState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }

    public Task StartAsync(AudioFormat format, long? targetTimestamp = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
    public void NotifyReconnect() { }
    public void Clear(long? newTargetTimestamp = null) { }
    public void ReanchorTiming() { }
    public void ProcessAudioChunk(AudioChunk chunk) { }
    public void SetVolume(int volume) { }
    public void SetMuted(bool muted) { }
    public Task SwitchDeviceAsync(string? deviceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
