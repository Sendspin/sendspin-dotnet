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
    private readonly List<string> _callLog = new();
    private readonly List<(int Count, TaskCompletionSource Source)> _callWaiters = new();

    private readonly TaskCompletionSource _startEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AudioPipelineState State { get; private set; } = AudioPipelineState.Idle;

    /// <summary>
    /// Whether the pipeline reports itself ready for chunks. Settable because a real pipeline
    /// flips it part-way through a start, as soon as it has a decoder and a ring — which is the
    /// window a chunk can arrive in mid-start.
    /// </summary>
    public bool IsReady { get; set; } = true;

    public AudioBufferStats? BufferStats => null;
    public AudioFormat? CurrentFormat => null;
    public int DetectedOutputLatencyMs => 0;

    /// <summary>Chunks handed to <see cref="ProcessAudioChunk"/>, in arrival order.</summary>
    public List<AudioChunk> Chunks { get; } = new();

    /// <summary>Formats passed to <see cref="StartAsync"/>, in order.</summary>
    public List<AudioFormat> StartCalls { get; } = new();

    /// <summary>Number of <see cref="StopAsync"/> calls.</summary>
    public int StopCount { get; private set; }

    /// <summary>Number of <see cref="Clear"/> calls.</summary>
    public int ClearCount { get; private set; }

    /// <summary>
    /// Names of the lifecycle calls the client made — <c>start</c>, <c>stop</c>, <c>clear</c> — in
    /// the order they <b>finished</b>, which is the order the pipeline actually saw them in.
    /// Entry order says nothing: a handler can be entered first and land last.
    /// </summary>
    public IReadOnlyList<string> CallLog
    {
        get
        {
            lock (_callLog)
            {
                return _callLog.ToList();
            }
        }
    }

    /// <summary>
    /// When set, the next <see cref="StartAsync"/> parks until it is resolved. This is the seam a
    /// synchronously-completing double cannot offer: a real pipeline start opens an output device,
    /// so a message handled while one is in flight is the ordinary case, not an exotic one.
    /// Consumed by that one call.
    /// </summary>
    public TaskCompletionSource? HoldNextStart { get; set; }

    /// <summary>As <see cref="HoldNextStart"/>, for <see cref="StopAsync"/>.</summary>
    public TaskCompletionSource? HoldNextStop { get; set; }

    /// <summary>Completes when the first <see cref="StartAsync"/> has been entered.</summary>
    public Task StartEntered => _startEntered.Task;

    /// <summary>Completes when the first <see cref="StopAsync"/> has been entered.</summary>
    public Task StopEntered => _stopEntered.Task;

    /// <summary>
    /// Completes once <paramref name="count"/> lifecycle calls have finished, so a test can wait
    /// for the client's handlers to land rather than poll for them.
    /// </summary>
    public Task CallsCompleted(int count)
    {
        lock (_callLog)
        {
            if (_callLog.Count >= count)
            {
                return Task.CompletedTask;
            }

            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _callWaiters.Add((count, waiter));
            return waiter.Task;
        }
    }

    public event EventHandler<AudioPipelineState>? StateChanged;
    public event EventHandler<AudioPipelineError>? ErrorOccurred;

    public void RaiseError(string message = "underrun") => ErrorOccurred?.Invoke(this, new AudioPipelineError(message));

    public void SetState(AudioPipelineState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }

    public async Task StartAsync(AudioFormat format, long? targetTimestamp = null, CancellationToken cancellationToken = default)
    {
        StartCalls.Add(format);
        _startEntered.TrySetResult();

        if (HoldNextStart is { } hold)
        {
            HoldNextStart = null;
            await hold.Task;
        }

        Record("start");
    }

    public async Task StopAsync()
    {
        StopCount++;
        _stopEntered.TrySetResult();

        if (HoldNextStop is { } hold)
        {
            HoldNextStop = null;
            await hold.Task;
        }

        Record("stop");
    }

    public void NotifyReconnect() { }

    public void Clear(long? newTargetTimestamp = null)
    {
        ClearCount++;
        Record("clear");
    }

    public void ReanchorTiming() { }

    public void ProcessAudioChunk(AudioChunk chunk) => Chunks.Add(chunk);

    public void SetVolume(int volume) { }

    public void SetMuted(bool muted) { }

    public Task SwitchDeviceAsync(string? deviceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void Record(string call)
    {
        List<TaskCompletionSource>? ready = null;

        lock (_callLog)
        {
            _callLog.Add(call);

            for (int i = _callWaiters.Count - 1; i >= 0; i--)
            {
                if (_callLog.Count >= _callWaiters[i].Count)
                {
                    (ready ??= new List<TaskCompletionSource>()).Add(_callWaiters[i].Source);
                    _callWaiters.RemoveAt(i);
                }
            }
        }

        // Completed outside the lock: the waiter's continuation is a test thread, and it reads
        // CallLog under the same lock.
        ready?.ForEach(waiter => waiter.TrySetResult());
    }
}
