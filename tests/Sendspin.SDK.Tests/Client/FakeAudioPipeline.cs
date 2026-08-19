using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Minimal <see cref="IAudioPipeline"/> test double. Tests drive the client's error/recovery
/// signaling by calling <see cref="RaiseError"/> and <see cref="SetState"/>, and inspect what
/// the client asked of the pipeline through the recorded calls.
/// </summary>
internal sealed class FakeAudioPipeline : IAudioPipeline
{
    /// <summary>Chunks handed to <see cref="ProcessAudioChunk"/>, in arrival order.</summary>
    public List<AudioChunk> Chunks { get; } = new();

    public AudioPipelineState State { get; private set; } = AudioPipelineState.Idle;

    /// <summary>
    /// Whether the client may hand chunks straight to the pipeline; when false they queue.
    /// </summary>
    public bool IsReady { get; set; } = true;

    public AudioBufferStats? BufferStats => null;

    /// <summary>The format of the stream the pipeline reports as running.</summary>
    public AudioFormat? CurrentFormat { get; set; }

    public int DetectedOutputLatencyMs => 0;

    /// <summary>Formats the client started the pipeline with, in order.</summary>
    public List<AudioFormat> StartCalls { get; } = new List<AudioFormat>();

    /// <summary>Calls to <see cref="StopAsync"/>, for tests asserting a role-targeted stream/end left playback alone.</summary>
    public int StopCount { get; private set; }

    /// <summary>Calls to <see cref="Clear"/>, for tests asserting a role-targeted stream/clear left the buffers alone.</summary>
    public int ClearCount { get; private set; }

    public event EventHandler<AudioPipelineState>? StateChanged;
    public event EventHandler<AudioPipelineError>? ErrorOccurred;

    public void RaiseError(string message = "underrun") => ErrorOccurred?.Invoke(this, new AudioPipelineError(message));

    public void SetState(AudioPipelineState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }

    public Task StartAsync(AudioFormat format, long? targetTimestamp = null, CancellationToken cancellationToken = default)
    {
        StartCalls.Add(format);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        StopCount++;
        return Task.CompletedTask;
    }

    public void NotifyReconnect() { }
    public void Clear(long? newTargetTimestamp = null) => ClearCount++;
    public void ReanchorTiming() { }
    public void ProcessAudioChunk(AudioChunk chunk) => Chunks.Add(chunk);
    public void SetVolume(int volume) { }
    public void SetMuted(bool muted) { }
    public Task SwitchDeviceAsync(string? deviceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
