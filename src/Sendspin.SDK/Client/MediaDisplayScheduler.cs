using Microsoft.Extensions.Logging;
using Sendspin.SDK.Extensions;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Client;

/// <summary>
/// Holds visualizer frames and artwork until their display timestamp, translated from server
/// time to the local clock, and raises them then.
/// </summary>
/// <remarks>
/// <para>
/// Both roles carry a timestamp that is "server clock time when this data should be displayed",
/// which clients must translate to their own clock. The two roles differ in what a timestamp
/// already in the past means: a visualizer frame more than <see cref="StaleThresholdMicroseconds"/>
/// late is dropped ("stale visualization frames are never rendered"), while artwork is displayed
/// immediately and is never dropped for lateness.
/// </para>
/// <para>
/// Data that is already due on arrival is raised inline, on the caller's thread, so the common
/// case keeps the receive loop's existing threading contract (including a throwing subscriber
/// escaping into the receive loop). Only data with a future display time is deferred to this
/// class's background loop, and while that loop is raising an event, a newly arrived due item
/// queues behind it rather than racing past it — so each role's events stay in timestamp order
/// whichever thread raises them.
/// </para>
/// </remarks>
internal sealed class MediaDisplayScheduler : IDisposable
{
    /// <summary>
    /// How late a visualizer frame may be and still be rendered. Matches the C++ reference
    /// client's <c>TOO_OLD_THRESHOLD_US</c>, which likewise tolerates a small lateness rather
    /// than dropping every frame that misses its deadline by a scheduling quantum.
    /// </summary>
    internal const long StaleThresholdMicroseconds = 20_000;

    /// <summary>
    /// Bound on pending visualizer frames used when the application advertises no
    /// <c>buffer_capacity</c>. The advertised value is what the server paces its send-ahead
    /// against, so honouring it is what keeps the advertisement truthful; an application that
    /// leaves it at zero has promised nothing, and gets this rather than an unbounded queue.
    /// </summary>
    internal const int DefaultVisualizerCapacityBytes = 65_536;

    private const int ArtworkChannelCount = 4;

    private readonly object _lock = new();
    private readonly IClockSynchronizer _clockSynchronizer;
    private readonly IHighPrecisionTimer _timer;
    private readonly ILogger _logger;
    private readonly int _visualizerCapacityBytes;

    private readonly Action<VisualizerFrame> _raiseVisualization;
    private readonly Action<ArtworkReceivedEventArgs> _raiseArtworkReceived;
    private readonly Action<ArtworkClearedEventArgs> _raiseArtworkCleared;

    /// <summary>Pending frames, ordered by display time. Guarded by <see cref="_lock"/>.</summary>
    private readonly List<PendingFrame> _frames = new();

    /// <summary>
    /// One pending image per artwork channel. The spec's "latest wins" rule is per channel — a
    /// newer image for a channel supersedes whatever that channel was still holding — so the
    /// pending set is bounded by the channel count and needs no capacity of its own.
    /// Guarded by <see cref="_lock"/>.
    /// </summary>
    private readonly PendingArtwork?[] _artwork = new PendingArtwork?[ArtworkChannelCount];

    private readonly SemaphoreSlim _wakeup = new(0);
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Running total of <see cref="PendingFrame.WireBytes"/>. Guarded by <see cref="_lock"/>.</summary>
    private long _pendingFrameBytes;

    /// <summary>
    /// Set while the loop is raising events outside <see cref="_lock"/>. Guarded by
    /// <see cref="_lock"/>; see the class remarks on ordering.
    /// </summary>
    private bool _dispatching;

    private bool _loopStarted;
    private bool _disposed;

    internal MediaDisplayScheduler(
        IClockSynchronizer clockSynchronizer,
        IHighPrecisionTimer timer,
        int visualizerCapacityBytes,
        ILogger logger,
        Action<VisualizerFrame> raiseVisualization,
        Action<ArtworkReceivedEventArgs> raiseArtworkReceived,
        Action<ArtworkClearedEventArgs> raiseArtworkCleared)
    {
        _clockSynchronizer = clockSynchronizer;
        _timer = timer;
        _visualizerCapacityBytes = visualizerCapacityBytes > 0
            ? visualizerCapacityBytes
            : DefaultVisualizerCapacityBytes;
        _logger = logger;
        _raiseVisualization = raiseVisualization;
        _raiseArtworkReceived = raiseArtworkReceived;
        _raiseArtworkCleared = raiseArtworkCleared;
    }

    public void Dispose()
    {
        bool disposeResources;

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ClearPendingLocked();

            // When the loop never started, nothing else will ever run its finally, so this
            // call owns the two disposables instead.
            disposeResources = !_loopStarted;
        }

        _cts.Cancel();

        if (disposeResources)
        {
            _cts.Dispose();
            _wakeup.Dispose();
        }
    }

    /// <summary>
    /// Drops a visualizer frame that is already stale, raises it now if it is due, and otherwise
    /// holds it until its translated display time.
    /// </summary>
    /// <param name="frame">The decoded frame.</param>
    /// <param name="wireBytes">
    /// Size of the binary message the frame came from, counted against the advertised
    /// <c>buffer_capacity</c> while the frame is pending.
    /// </param>
    internal void SubmitVisualizerFrame(VisualizerFrame frame, int wireBytes)
    {
        bool raiseNow = false;

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            long now = _timer.GetCurrentTimeMicroseconds();
            long displayTime = _clockSynchronizer.ServerToClientTime(frame.Timestamp);

            if (now - displayTime > StaleThresholdMicroseconds)
            {
                _logger.LogTrace(
                    "Dropped stale visualizer frame: {Late}us late (display {Display}, now {Now})",
                    now - displayTime,
                    displayTime,
                    now);
                return;
            }

            if (displayTime <= now && _frames.Count == 0 && !_dispatching)
            {
                raiseNow = true;
            }
            else
            {
                InsertFrameLocked(new PendingFrame(displayTime, frame, wireBytes), now);
            }
        }

        if (raiseNow)
        {
            _raiseVisualization(frame);
        }
    }

    /// <summary>
    /// Raises artwork now if its display time has passed, and otherwise holds it until then.
    /// A later image for the same channel supersedes one still pending, per "latest wins".
    /// </summary>
    /// <param name="chunk">The parsed artwork message; empty image data means clear.</param>
    internal void SubmitArtwork(ArtworkChunk chunk)
    {
        PendingArtwork? raiseNow = null;

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            long now = _timer.GetCurrentTimeMicroseconds();
            long displayTime = _clockSynchronizer.ServerToClientTime(chunk.Timestamp);
            var pending = new PendingArtwork(displayTime, chunk);

            // Supersede unconditionally, even by an item that is about to be raised inline:
            // this arrival is the newest for the channel, so nothing older may still display.
            int channel = chunk.Channel < ArtworkChannelCount ? chunk.Channel : ArtworkChannelCount - 1;
            _artwork[channel] = null;

            if (displayTime <= now && !HasPendingArtworkLocked() && !_dispatching)
            {
                raiseNow = pending;
            }
            else
            {
                _artwork[channel] = pending;
                WakeLocked(displayTime, now);
            }
        }

        if (raiseNow is not null)
        {
            RaiseArtwork(raiseNow);
        }
    }

    /// <summary>
    /// Discards everything still pending, for both roles. Called where buffered media must not
    /// survive whatever the message named: loss of the connection, and a <c>stream/clear</c> or
    /// <c>stream/end</c> that omits <c>roles</c> and so ends every stream.
    /// </summary>
    internal void Flush()
    {
        lock (_lock)
        {
            ClearPendingLocked();
        }
    }

    /// <summary>
    /// Discards pending visualizer frames, leaving artwork held. For a <c>stream/clear</c> or
    /// <c>stream/end</c> that names the <c>visualizer</c> role.
    /// </summary>
    internal void FlushVisualizer()
    {
        lock (_lock)
        {
            ClearPendingFramesLocked();
        }
    }

    /// <summary>
    /// Discards pending artwork, leaving visualizer frames held. For a <c>stream/end</c> that
    /// names the <c>artwork</c> role — including the routine case of a server dropping that role
    /// alone, where artwork already sent for a coming track must not surface but the visualizer
    /// stream plays on.
    /// </summary>
    internal void FlushArtwork()
    {
        lock (_lock)
        {
            Array.Clear(_artwork);
        }
    }

    private void ClearPendingLocked()
    {
        ClearPendingFramesLocked();
        Array.Clear(_artwork);
    }

    private void ClearPendingFramesLocked()
    {
        _frames.Clear();
        _pendingFrameBytes = 0;
    }

    private bool HasPendingArtworkLocked()
    {
        foreach (var slot in _artwork)
        {
            if (slot is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Inserts a frame in display-time order, then enforces the advertised capacity by dropping
    /// from the front — the oldest pending frame, and the one whose display moment is closest.
    /// </summary>
    private void InsertFrameLocked(PendingFrame frame, long now)
    {
        // Server timestamps arrive monotonically in the ordinary case, so scanning back from
        // the end finds the insertion point immediately.
        int index = _frames.Count;
        while (index > 0 && _frames[index - 1].DisplayTime > frame.DisplayTime)
        {
            index--;
        }

        _frames.Insert(index, frame);
        _pendingFrameBytes += frame.WireBytes;

        while (_frames.Count > 1 && _pendingFrameBytes > _visualizerCapacityBytes)
        {
            _pendingFrameBytes -= _frames[0].WireBytes;
            _frames.RemoveAt(0);
            _logger.LogTrace(
                "Visualizer buffer over capacity ({Capacity} bytes); dropped oldest pending frame",
                _visualizerCapacityBytes);
        }

        WakeLocked(_frames[0].DisplayTime, now);
    }

    /// <summary>
    /// Starts the loop on first use and nudges it when <paramref name="deadline"/> is the
    /// soonest one pending, which is the only case where it may be waiting on a later time.
    /// </summary>
    private void WakeLocked(long deadline, long now)
    {
        if (!_loopStarted)
        {
            _loopStarted = true;
            Task.Run(() => RunAsync(_cts.Token), CancellationToken.None).SafeFireAndForget(_logger);
        }

        if (deadline <= NextDeadlineLocked(now))
        {
            _wakeup.Release();
        }
    }

    private long NextDeadlineLocked(long now)
    {
        long next = long.MaxValue;

        if (_frames.Count > 0)
        {
            next = _frames[0].DisplayTime;
        }

        for (int channel = 0; channel < _artwork.Length; channel++)
        {
            var slot = _artwork[channel];
            if (slot is not null && slot.DisplayTime < next)
            {
                next = slot.DisplayTime;
            }
        }

        return next == long.MaxValue ? long.MaxValue : Math.Max(next, now);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var dueFrames = new List<PendingFrame>();
        var dueArtwork = new List<PendingArtwork>();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int waitMilliseconds;
                bool hasDue;

                lock (_lock)
                {
                    long now = _timer.GetCurrentTimeMicroseconds();
                    TakeDueLocked(now, dueFrames, dueArtwork);
                    hasDue = dueFrames.Count > 0 || dueArtwork.Count > 0;
                    _dispatching = hasDue;
                    waitMilliseconds = WaitMillisecondsLocked(now);
                }

                if (hasDue)
                {
                    DispatchDue(dueFrames, dueArtwork);
                    dueFrames.Clear();
                    dueArtwork.Clear();

                    lock (_lock)
                    {
                        _dispatching = false;
                    }

                    continue;
                }

                await _wakeup.WaitAsync(waitMilliseconds, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed while waiting: the ordinary way this loop ends.
        }
        finally
        {
            lock (_lock)
            {
                _cts.Dispose();
                _wakeup.Dispose();
            }
        }
    }

    private void TakeDueLocked(long now, List<PendingFrame> dueFrames, List<PendingArtwork> dueArtwork)
    {
        while (_frames.Count > 0 && _frames[0].DisplayTime <= now)
        {
            var frame = _frames[0];
            _frames.RemoveAt(0);
            _pendingFrameBytes -= frame.WireBytes;

            // Re-checked on the way out as well as on arrival: the frame was fresh when it was
            // queued, but a loop that woke late must still not render it.
            if (now - frame.DisplayTime > StaleThresholdMicroseconds)
            {
                _logger.LogTrace(
                    "Dropped stale visualizer frame at dispatch: {Late}us late", now - frame.DisplayTime);
                continue;
            }

            dueFrames.Add(frame);
        }

        for (int channel = 0; channel < _artwork.Length; channel++)
        {
            var slot = _artwork[channel];
            if (slot is not null && slot.DisplayTime <= now)
            {
                dueArtwork.Add(slot);
                _artwork[channel] = null;
            }
        }

        if (dueArtwork.Count > 1)
        {
            dueArtwork.Sort(static (left, right) => left.DisplayTime.CompareTo(right.DisplayTime));
        }
    }

    private int WaitMillisecondsLocked(long now)
    {
        long next = NextDeadlineLocked(now);
        if (next == long.MaxValue)
        {
            return Timeout.Infinite;
        }

        long deltaMicroseconds = next - now;
        if (deltaMicroseconds <= 0)
        {
            return 0;
        }

        // Round up: waking a whole millisecond early only costs another pass through the loop,
        // whereas rounding down would busy-spin against a deadline that has not arrived.
        return (int)Math.Min(int.MaxValue, (deltaMicroseconds + 999) / 1000);
    }

    private void DispatchDue(List<PendingFrame> dueFrames, List<PendingArtwork> dueArtwork)
    {
        foreach (var frame in dueFrames)
        {
            SafeRaise(() => _raiseVisualization(frame.Frame), "visualizer frame");
        }

        foreach (var artwork in dueArtwork)
        {
            SafeRaise(() => RaiseArtwork(artwork), "artwork");
        }
    }

    private void RaiseArtwork(PendingArtwork artwork)
    {
        if (artwork.ImageData.Length == 0)
        {
            _raiseArtworkCleared(new ArtworkClearedEventArgs(artwork.Channel, artwork.ServerTimestamp));
        }
        else
        {
            _raiseArtworkReceived(
                new ArtworkReceivedEventArgs(artwork.Channel, artwork.ServerTimestamp, artwork.ImageData));
        }
    }

    /// <summary>
    /// Raises one scheduled event, logging rather than propagating what a subscriber throws.
    /// </summary>
    /// <remarks>
    /// Deliberately unlike the inline path, where a throwing subscriber escapes into the receive
    /// loop and is surfaced as a lost connection. There is no connection to lose here, and
    /// letting the exception out would end this loop — silently stopping every later frame and
    /// image for the life of the client. Mirrors the time-sync loop's reasoning.
    /// </remarks>
    private void SafeRaise(Action raise, string what)
    {
        try
        {
            raise();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Subscriber threw while a scheduled {What} was being raised", what);
        }
    }

    private sealed class PendingFrame
    {
        internal PendingFrame(long displayTime, VisualizerFrame frame, int wireBytes)
        {
            DisplayTime = displayTime;
            Frame = frame;
            WireBytes = wireBytes;
        }

        internal long DisplayTime { get; }

        internal VisualizerFrame Frame { get; }

        internal int WireBytes { get; }
    }

    private sealed class PendingArtwork
    {
        internal PendingArtwork(long displayTime, ArtworkChunk chunk)
        {
            DisplayTime = displayTime;
            Channel = chunk.Channel;
            ServerTimestamp = chunk.Timestamp;
            ImageData = chunk.ImageData;
        }

        internal long DisplayTime { get; }

        internal int Channel { get; }

        internal long ServerTimestamp { get; }

        internal byte[] ImageData { get; }
    }
}
