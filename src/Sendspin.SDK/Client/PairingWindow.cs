namespace Sendspin.SDK.Client;

/// <summary>
/// The state in which this client has decided to accept pairing attempts.
/// </summary>
/// <remarks>
/// <para>
/// The window is a property of the device, not of a connection: the spec admits attempts
/// "only on the connection that carries its first", which only makes sense if every connection
/// a host runs sees the same window. Share one instance across them by passing it in
/// <c>SendspinClientOptions.PairingWindow</c>.
/// </para>
/// <para>
/// An opening admits several attempts. It closes on a completed pairing, on its fifth failed
/// attempt, when the connection carrying its attempts drops, on <see cref="Close"/>, or when
/// its lifetime expires. An attempt that times out or is cancelled does not close it.
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

    // The spec closes the window on "its fifth failed attempt".
    private const int MaxFailedAttempts = 5;

    private readonly TimeSpan _lifetime;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();

    private DateTimeOffset? _openedAt;

    // The connection carrying this opening's attempts, bound by its first; null until then.
    private object? _connection;
    private int _failedAttempts;

    /// <summary>Initializes a new instance of the <see cref="PairingWindow"/> class, initially closed.</summary>
    /// <param name="lifetime">
    /// How long an opening lasts before it closes silently. Defaults to
    /// <see cref="DefaultLifetime"/>. Measured from opening, without pausing during attempts:
    /// an attempt already in progress runs to its own end, but starting another needs a new
    /// opening.
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
    /// Opens the window, or replaces an opening already in progress with a fresh one.
    /// </summary>
    public void Open()
    {
        lock (_gate)
        {
            _openedAt = _timeProvider.GetUtcNow();
            _connection = null;
            _failedAttempts = 0;
        }

        RaiseStateChanged();
    }

    /// <summary>
    /// Closes the window. Does not abort an attempt already in progress, which is bounded by
    /// its own timeout.
    /// </summary>
    public void Close()
    {
        lock (_gate)
        {
            _openedAt = null;
            _connection = null;
        }

        RaiseStateChanged();
    }

    /// <summary>
    /// Admits an attempt on <paramref name="connection"/>, leaving the window open. Returns
    /// false when no unexpired opening is available, or when the opening already carries
    /// another connection's attempts. The first connection admitted is the only one admitted
    /// for the rest of the opening, which is what makes concurrent connections resolve to a
    /// single winner.
    /// </summary>
    internal bool TryAdmit(object connection)
    {
        lock (_gate)
        {
            if (IsExpiredLocked())
            {
                return false;
            }

            _connection ??= connection;
            return ReferenceEquals(_connection, connection);
        }
    }

    /// <summary>
    /// Counts a failed attempt — the client's verification of <c>server_kc</c> failed — on the
    /// connection carrying this opening's attempts, closing the window on the fifth.
    /// </summary>
    internal void RecordFailedAttempt(object connection)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_connection, connection) || ++_failedAttempts < MaxFailedAttempts)
            {
                return;
            }

            _openedAt = null;
            _connection = null;
        }

        RaiseStateChanged();
    }

    /// <summary>
    /// Closes the window if <paramref name="connection"/> is the one carrying its attempts:
    /// that connection completed a pairing, or dropped.
    /// </summary>
    internal void CloseFor(object connection)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_connection, connection))
            {
                return;
            }

            _openedAt = null;
            _connection = null;
        }

        RaiseStateChanged();
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
    /// it is only ever observable when something asks the window to admit an attempt, so a
    /// timer would buy a background thread and a disposal contract for no behavioural
    /// difference.
    /// </summary>
    private bool IsExpiredLocked()
        => _openedAt is not { } openedAt
           || _timeProvider.GetUtcNow() - openedAt > _lifetime;
}
