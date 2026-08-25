using Microsoft.Extensions.Logging;
using Sendspin.SDK.Extensions;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Client;

/// <summary>
/// A <c>server/state</c> role that carries scheduled updates: one whose object has a timestamp at
/// which it takes effect, so a future-stamped update is held rather than merged on receipt.
/// </summary>
/// <remarks>
/// The two roles spec #135 (pending merge) gives the current-plus-pending model to, alongside
/// artwork. Used as an index into <see cref="MediaDisplayScheduler"/>'s pending slots, so each
/// role holds at most one update and neither can displace the other's.
/// </remarks>
internal enum ScheduledStateRole
{
    /// <summary>The <c>metadata</c> role object.</summary>
    Metadata = 0,

    /// <summary>The <c>color</c> role object.</summary>
    Color = 1,
}

/// <summary>
/// Holds visualizer frames, artwork, and scheduled <c>metadata</c>/<c>color</c> updates until
/// their timestamp, translated from server time to the local clock, and raises or applies them
/// then.
/// </summary>
/// <remarks>
/// <para>
/// Every role here carries a timestamp that is "server clock time when this takes effect", which
/// clients must translate to their own clock. They differ in what a timestamp already in the past
/// means: a visualizer frame more than <see cref="StaleThresholdMicroseconds"/> late is dropped
/// ("stale visualization frames are never rendered"), while artwork and state updates take effect
/// immediately and are never dropped for lateness.
/// </para>
/// <para>
/// Artwork (per channel) and the two <see cref="ScheduledStateRole"/>s follow one model, spec
/// #135 (pending merge): each slot keeps <em>at most one</em> pending item, a future-stamped
/// message replaces whatever the slot held, and a past-or-present one takes effect at once and
/// discards what the slot held. Timestamps are never compared between messages — the newest
/// message always wins its slot, even when it is due sooner than the item it displaces — because
/// only the server knows which of the two it meant to stand.
/// </para>
/// <para>
/// The translation is <see cref="IClockSynchronizer.ServerToClientTimeUncompensated"/>, the clock
/// offset alone: the role specs say to translate "using the offset computed from clock
/// synchronization", and only the player role goes on to subtract <c>static_delay_ms</c>. That
/// delay compensates for hardware past the audio port, so applying it here would show every
/// visual ahead of the sound it belongs to by up to the 5 s the setting allows.
/// </para>
/// <para>
/// Data that is already due on arrival is raised inline, on the caller's thread, so the common
/// case keeps the receive loop's existing threading contract (including a throwing subscriber
/// escaping into the receive loop). Only data with a future display time is deferred to this
/// class's background loop, and while that loop is raising an event, a newly arrived due item
/// queues behind it rather than racing past it — so each role's events stay in timestamp order
/// whichever thread raises them. Within one dispatch pass the state roles are applied before the
/// media of that pass, so a subscriber handling the artwork of a track change already sees the
/// metadata and colors it belongs to.
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

    private const int StateRoleCount = 2;

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

    /// <summary>
    /// One pending update per <see cref="ScheduledStateRole"/>, indexed by the enum. The roles
    /// are independent: a scheduled <c>color</c> update never displaces a scheduled
    /// <c>metadata</c> one, and neither is ordered against the other.
    /// Guarded by <see cref="_lock"/>.
    /// </summary>
    private readonly PendingStateUpdate?[] _stateUpdates = new PendingStateUpdate?[StateRoleCount];

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
            long displayTime = _clockSynchronizer.ServerToClientTimeUncompensated(frame.Timestamp);

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
    /// The newest image for a channel supersedes one still pending for it, per "latest wins" —
    /// arrival order, not timestamp order (spec #135, pending merge).
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
            long displayTime = _clockSynchronizer.ServerToClientTimeUncompensated(chunk.Timestamp);
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
    /// Offers a <c>server/state</c> role update to its pending slot, and reports whether the slot
    /// took it. Whatever the slot was holding is discarded either way — the newest message wins
    /// it, with no comparison of timestamps.
    /// </summary>
    /// <param name="role">The role whose slot this update belongs to.</param>
    /// <param name="serverTimestamp">
    /// The role object's <c>timestamp</c>, in server clock time. Null when the message carried
    /// none — nothing to schedule against, so it takes effect now, as a past-or-present one does.
    /// </param>
    /// <param name="apply">
    /// Applies the update to the client's state and announces it. Invoked from the scheduler
    /// loop when its moment arrives, and only then: a caller told the slot did not take the
    /// update applies it itself, in the place the message it arrived in already announces.
    /// </param>
    /// <returns>
    /// True when the update is now held for a future moment (or queued behind a dispatch already
    /// in flight) and <paramref name="apply"/> will run later; false when it is due and the
    /// caller must apply it.
    /// </returns>
    internal bool TryScheduleStateUpdate(ScheduledStateRole role, long? serverTimestamp, Action apply)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                // Nothing will ever run the action, and the client is going away with it.
                return true;
            }

            long now = _timer.GetCurrentTimeMicroseconds();
            long effectiveTime = serverTimestamp is { } timestamp
                ? _clockSynchronizer.ServerToClientTimeUncompensated(timestamp)
                : now;

            // Cleared before the decision, not after it: this message supersedes whatever the
            // slot held whether it schedules or applies now.
            _stateUpdates[(int)role] = null;

            // A due update still has to queue when the loop is mid-dispatch, or it would race
            // past an update for the same role that the loop is applying right now.
            if (effectiveTime <= now && !_dispatching)
            {
                return false;
            }

            _stateUpdates[(int)role] = new PendingStateUpdate(effectiveTime, apply);
            WakeLocked(effectiveTime, now);
            return true;
        }
    }

    /// <summary>
    /// Discards everything still pending, for every role. Called where nothing buffered may
    /// survive: loss of the connection, whose re-handshake resets the clock offset every pending
    /// item's local time was computed against.
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

    /// <summary>
    /// Discards the image one artwork channel is holding, leaving every other channel and role
    /// alone. For a <c>stream/start</c> that reconfigures the channel: the held image was encoded
    /// for a configuration that no longer applies, and the server re-sends it if it still does.
    /// </summary>
    /// <param name="channel">Channel index, 0-3.</param>
    internal void FlushArtworkChannel(int channel)
    {
        if (channel < 0 || channel >= ArtworkChannelCount)
        {
            return;
        }

        lock (_lock)
        {
            _artwork[channel] = null;
        }
    }

    private void ClearPendingLocked()
    {
        ClearPendingFramesLocked();
        Array.Clear(_artwork);
        Array.Clear(_stateUpdates);
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

        for (int role = 0; role < _stateUpdates.Length; role++)
        {
            var slot = _stateUpdates[role];
            if (slot is not null && slot.EffectiveTime < next)
            {
                next = slot.EffectiveTime;
            }
        }

        return next == long.MaxValue ? long.MaxValue : Math.Max(next, now);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var dueFrames = new List<PendingFrame>();
        var dueArtwork = new List<PendingArtwork>();
        var dueState = new List<PendingStateUpdate>();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int waitMilliseconds;
                bool hasDue;

                lock (_lock)
                {
                    long now = _timer.GetCurrentTimeMicroseconds();
                    TakeDueLocked(now, dueFrames, dueArtwork, dueState);
                    hasDue = dueFrames.Count > 0 || dueArtwork.Count > 0 || dueState.Count > 0;
                    _dispatching = hasDue;
                    waitMilliseconds = WaitMillisecondsLocked(now);
                }

                if (hasDue)
                {
                    DispatchDue(dueFrames, dueArtwork, dueState);
                    dueFrames.Clear();
                    dueArtwork.Clear();
                    dueState.Clear();

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

    private void TakeDueLocked(
        long now,
        List<PendingFrame> dueFrames,
        List<PendingArtwork> dueArtwork,
        List<PendingStateUpdate> dueState)
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

        // Taken in role order rather than by time: the two roles carry no ordering against each
        // other, and each can only ever contribute the one update its slot holds.
        for (int role = 0; role < _stateUpdates.Length; role++)
        {
            var slot = _stateUpdates[role];
            if (slot is not null && slot.EffectiveTime <= now)
            {
                dueState.Add(slot);
                _stateUpdates[role] = null;
            }
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

    private void DispatchDue(
        List<PendingFrame> dueFrames,
        List<PendingArtwork> dueArtwork,
        List<PendingStateUpdate> dueState)
    {
        // State first, so a subscriber reacting to the artwork of a track change already sees
        // the metadata and colors that image belongs to. See the class remarks.
        foreach (var update in dueState)
        {
            SafeRaise(update.Apply, "state update");
        }

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

    private sealed class PendingStateUpdate
    {
        internal PendingStateUpdate(long effectiveTime, Action apply)
        {
            EffectiveTime = effectiveTime;
            Apply = apply;
        }

        /// <summary>Local-clock time at which the update takes effect.</summary>
        internal long EffectiveTime { get; }

        /// <summary>Merges the update into the client's state and announces it.</summary>
        internal Action Apply { get; }
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
