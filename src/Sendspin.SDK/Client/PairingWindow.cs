namespace Sendspin.SDK.Client;

/// <summary>
/// The state in which this client has decided to accept one pairing attempt.
/// </summary>
/// <remarks>
/// <para>
/// The window is a property of the device, not of a connection: the spec closes it on "drop of
/// the connection carrying its attempt", which only makes sense if it outlives any single
/// connection, and it admits exactly one attempt no matter how many servers are connected.
/// Share one instance across every connection a host runs by passing it in
/// <c>SendspinClientOptions.PairingWindow</c>.
/// </para>
/// <para>
/// Opened by a deliberate operator gesture on the device — a button press, a reset pinhole, a
/// power-cycle pattern. Gestures should be hard to induce remotely.
/// </para>
/// <para>All members are safe to call concurrently.</para>
/// </remarks>
public sealed class PairingWindow
{
    /// <summary>The spec's recommended window lifetime.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(5);

    private readonly TimeSpan _lifetime;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();

    private DateTimeOffset? _openedAt;

    /// <summary>Initializes a new instance of the <see cref="PairingWindow"/> class, initially closed.</summary>
    /// <param name="lifetime">
    /// How long an opening lasts before it closes silently. Defaults to
    /// <see cref="DefaultLifetime"/>. Measured from opening until the attempt starts; once
    /// <c>client/pair-init</c> has been sent the attempt timeout governs instead.
    /// </param>
    /// <param name="timeProvider">Clock; defaults to <see cref="TimeProvider.System"/>.</param>
    public PairingWindow(TimeSpan? lifetime = null, TimeProvider? timeProvider = null)
    {
        _lifetime = lifetime ?? DefaultLifetime;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Raised when the window opens or closes. Not raised on silent expiry. Subscribers are
    /// invoked independently: an exception from one neither reaches the other subscribers nor
    /// the caller of <see cref="Open"/>/<see cref="Close"/>.
    /// </summary>
    public event EventHandler? StateChanged;

    /// <summary>Whether an unexpired opening is available.</summary>
    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return !IsExpiredLocked();
            }
        }
    }

    /// <summary>
    /// Opens the window, or restarts the lifetime of an opening already in progress.
    /// </summary>
    public void Open()
    {
        lock (_gate)
        {
            _openedAt = _timeProvider.GetUtcNow();
        }

        RaiseStateChanged();
    }

    /// <summary>
    /// Closes the window. Does not abort an attempt already in progress: once
    /// <c>client/pair-init</c> has been sent the opening is spent and the attempt is bounded by
    /// its own timeout.
    /// </summary>
    public void Close()
    {
        lock (_gate)
        {
            _openedAt = null;
        }

        RaiseStateChanged();
    }

    /// <summary>
    /// Claims the current opening for one attempt, closing the window. Returns false when no
    /// unexpired opening is available. At most one caller can succeed per opening, which is
    /// what makes concurrent connections resolve to a single winner.
    /// </summary>
    internal bool TryConsume()
    {
        lock (_gate)
        {
            if (IsExpiredLocked())
            {
                return false;
            }

            _openedAt = null;
            return true;
        }
    }

    /// <summary>
    /// Raises <see cref="StateChanged"/> so that one subscriber cannot affect another.
    /// </summary>
    /// <remarks>
    /// Every connection sharing this window subscribes, and the raise is synchronous on the
    /// caller's thread — typically the thread that observed the operator gesture. A subscriber
    /// that throws would otherwise propagate out of
    /// <see cref="Open"/> into a connection's message dispatch and tear it down over
    /// another subscriber's fault, and would stop the remaining subscribers from ever seeing
    /// the opening. The window itself is already updated before this runs, so a swallowed
    /// handler fault costs that handler's reaction and nothing else.
    /// </remarks>
    private void RaiseStateChanged()
    {
        var handlers = StateChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler)handler)(this, EventArgs.Empty);
            }
#pragma warning disable RCS1075 // Empty catch of Exception: deliberate, see the comment inside.
            catch (Exception)
            {
                // Deliberately swallowed and deliberately not rethrown anywhere: this type is
                // constructed by the application (new PairingWindow()) and has no logger, and
                // containing the fault is the entire purpose of this loop.
            }
#pragma warning restore RCS1075
        }
    }

    /// <summary>
    /// Whether there is no usable opening. Expiry is evaluated on read rather than by a timer:
    /// it is only ever observable when something tries to consume the window, so a timer would
    /// buy a background thread and a disposal contract for no behavioural difference.
    /// </summary>
    private bool IsExpiredLocked()
        => _openedAt is not { } openedAt
           || _timeProvider.GetUtcNow() - openedAt > _lifetime;
}
