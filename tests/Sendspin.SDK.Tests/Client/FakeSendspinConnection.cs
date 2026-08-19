using Sendspin.SDK.Connection;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// In-memory <see cref="ISendspinConnection"/> test double.
/// Tests drive the <c>SendspinClientService</c> by calling <see cref="RaiseTextMessageReceived"/>
/// instead of running a real WebSocket.
/// </summary>
internal sealed class FakeSendspinConnection : ISendspinConnection
{
    private bool _respondToTimeSync;
    private long _unansweredProbeT1;
    private int _probesSent;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public Uri? ServerUri { get; private set; }
    public List<IMessage> SentMessages { get; } = new();

    /// <summary>
    /// When true, <see cref="SendMessageAsync"/> throws <see cref="InvalidOperationException"/> like the
    /// real <see cref="SendspinConnection"/> when <see cref="State"/> is not
    /// <see cref="ConnectionState.Connected"/>. Off by default so the many tests that drive the client
    /// without connecting keep recording sent messages.
    /// </summary>
    public bool EnforceConnectionState { get; set; }

    /// <summary>
    /// When true, every probe sent through <see cref="SendTimeMessageAsync"/> is answered
    /// synchronously with a matching server/time reply, so the client's time-sync bursts
    /// complete and feed measurements into its clock synchronizer. This is how a fixture drives
    /// clock-sync convergence over the wire without a real server. Off by default: most tests
    /// want no unsolicited inbound traffic.
    /// <para>
    /// Switching it on also answers the probe left unanswered while it was off, the way a
    /// server coming back answers what it had been sitting on. Without that, a fixture that
    /// flips it mid-test would have to wait out the client's full per-probe timeout (10 s)
    /// before the next probe is even sent.
    /// </para>
    /// </summary>
    public bool RespondToTimeSync
    {
        get => _respondToTimeSync;
        set
        {
            _respondToTimeSync = value;
            if (value && Interlocked.Exchange(ref _unansweredProbeT1, 0) is var t1 and not 0)
            {
                RaiseTimeSyncReply(t1);
            }
        }
    }

    /// <summary>
    /// Round trip the scripted server/time replies imply, in microseconds. The whole exchange
    /// is synthetic (see <see cref="RaiseTimeSyncReply"/>), so this is exactly what the client
    /// computes — which matters most for its sign, since non-positive round trips are dropped
    /// outright (#224).
    /// </summary>
    public long TimeSyncRttMicroseconds { get; set; } = 2000;

    /// <summary>
    /// Overrides the clock this transport stamps T1 from. A test that pins a distinctive value
    /// here can tell a T1 taken at the send point from one the caller captured beforehand,
    /// which is otherwise invisible: both are "some recent microsecond".
    /// </summary>
    public Func<long>? TimeSyncTransmitClock { get; set; }

    /// <summary>
    /// Replaces the synthetic reply for a probe, given its ordinal within the connection and
    /// its T1. Returns the two server stamps and the T4 the transport reports, or null for a
    /// probe the server never answers. For tests that need one probe of a burst to differ
    /// from the rest.
    /// </summary>
    public Func<int, long, (long ServerReceived, long ServerTransmitted, long ReceivedAt)?>? TimeSyncReplyOverride
    {
        get;
        set;
    }

    /// <summary>
    /// When set, the next message passed to <see cref="SendMessageAsync"/> is recorded (it hit
    /// the wire) but the send does not complete until the given source is resolved. Consumed by
    /// that one send — cleared before awaiting — so anything sent meanwhile passes straight
    /// through. Lets a test interleave another send while one is mid-flight, which a fake whose
    /// sends complete synchronously could never produce.
    /// </summary>
    public TaskCompletionSource? HoldNextSend { get; set; }

    /// <summary>
    /// When true, the next <see cref="SendMessageAsync"/> call throws
    /// <see cref="InvalidOperationException"/> without recording the message, then clears
    /// itself. Unlike <see cref="EnforceConnectionState"/> — which can only throw while not
    /// Connected and therefore never reaches code behind an up-front connection-state guard —
    /// this simulates a send failing while <see cref="State"/> IS Connected (the socket dying
    /// mid-write), which is what catch-based rollback paths need to be exercised at all.
    /// </summary>
    public bool ThrowOnNextSend { get; set; }

    /// <summary>
    /// The exception <see cref="ThrowOnNextSend"/> raises. Defaults to the
    /// <see cref="InvalidOperationException"/> a socket dying mid-write produces, so existing
    /// callers are unaffected. Set it to reach a catch filter that deliberately does not name
    /// that type — the time-sync burst tolerates transport failures and propagates everything
    /// else (#109), and only a non-transport type can exercise the second half.
    /// </summary>
    public Exception NextSendFailure { get; set; } = new InvalidOperationException("Simulated send failure");

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;
    public event EventHandler<TextMessageReceivedEventArgs>? TextMessageReceived;
    public event EventHandler<ReadOnlyMemory<byte>>? BinaryMessageReceived;

    public Task ConnectAsync(Uri serverUri, CancellationToken cancellationToken = default)
    {
        ServerUri = serverUri;
        SetState(ConnectionState.Connected);
        return Task.CompletedTask;
    }

    /// <summary>The reason passed to the most recent <see cref="DisconnectAsync"/> call.</summary>
    public string? LastDisconnectReason { get; private set; }

    public Task DisconnectAsync(string reason = "user_request", CancellationToken cancellationToken = default)
    {
        LastDisconnectReason = reason;
        SetState(ConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    public async Task SendMessageAsync<T>(T message, CancellationToken cancellationToken = default)
        where T : IMessage
    {
        if (EnforceConnectionState && State != ConnectionState.Connected)
        {
            throw new InvalidOperationException("WebSocket is not connected");
        }

        if (ThrowOnNextSend)
        {
            ThrowOnNextSend = false;
            throw NextSendFailure;
        }

        // Locked so tests polling for fire-and-forget sends (see SnapshotSentMessages) can
        // enumerate safely while the client's time-sync loop appends from another thread.
        lock (SentMessages)
        {
            SentMessages.Add(message);
        }

        if (HoldNextSend is { } hold)
        {
            HoldNextSend = null;
            await hold.Task;
        }
    }

    /// <summary>
    /// Stamps T1 the way a real transport does — at the send point, handed back through the
    /// callback before anything can answer it — records the probe among
    /// <see cref="SentMessages"/> so probe-counting tests keep working, and answers it when
    /// <see cref="RespondToTimeSync"/> is set.
    /// </summary>
    public async Task SendTimeMessageAsync(Action<long> onTransmitted, CancellationToken cancellationToken = default)
    {
        if (EnforceConnectionState && State != ConnectionState.Connected)
        {
            throw new InvalidOperationException("WebSocket is not connected");
        }

        if (ThrowOnNextSend)
        {
            ThrowOnNextSend = false;
            throw NextSendFailure;
        }

        long t1 = TimeSyncTransmitClock?.Invoke() ?? HighPrecisionTimer.Shared.GetCurrentTimeMicroseconds();
        onTransmitted(t1);

        lock (SentMessages)
        {
            SentMessages.Add(ClientTimeMessage.Create(t1));
        }

        if (HoldNextSend is { } hold)
        {
            HoldNextSend = null;
            await hold.Task;
        }

        if (RespondToTimeSync)
        {
            RaiseTimeSyncReply(t1);
        }
        else
        {
            // Remembered so switching RespondToTimeSync on can answer it retroactively.
            Interlocked.Exchange(ref _unansweredProbeT1, t1);
        }
    }

    /// <summary>
    /// Answers one probe with a complete synthetic exchange: server stamps a half-RTT out and
    /// back, and the T4 the transport reports for the reply.
    /// </summary>
    /// <remarks>
    /// T4 is supplied rather than read from the clock so the exchange is exact — the client
    /// computes a round trip of precisely <see cref="TimeSyncRttMicroseconds"/> and an offset
    /// of precisely zero, whatever the machine is doing. It has to be exact in the RTT's sign
    /// above all: a reply whose server interval assumed more processing time than this
    /// in-memory fake actually takes would compute a negative round trip, and the client drops
    /// those samples outright (#224), leaving every burst empty.
    /// </remarks>
    public void RaiseTimeSyncReply(long t1)
    {
        const long serverProcessing = 100;
        long half = TimeSyncRttMicroseconds / 2;
        long t2 = t1 + half;
        long t3 = t2 + serverProcessing;
        long t4 = t3 + half;

        if (TimeSyncReplyOverride is { } custom)
        {
            if (custom(Interlocked.Increment(ref _probesSent), t1) is not { } stamps)
            {
                return; // a probe the server never answers; the client sits out its timeout
            }

            (t2, t3, t4) = stamps;
        }

        RaiseTextMessageReceived(
            $$$"""
            {"type":"server/time","payload":{"client_transmitted":{{{t1}}},"server_received":{{{t2}}},"server_transmitted":{{{t3}}} }}
            """,
            t4);
    }

    /// <summary>
    /// Copy of <see cref="SentMessages"/> taken under the same lock <see cref="SendMessageAsync"/>
    /// appends under. Use from tests that poll while the client is still sending in the
    /// background (e.g. the time-sync loop); plain enumeration can throw mid-append.
    /// </summary>
    public IReadOnlyList<IMessage> SnapshotSentMessages()
    {
        lock (SentMessages)
        {
            return SentMessages.ToList();
        }
    }

    /// <summary>Binary frames sent via <see cref="SendBinaryAsync"/>, in order.</summary>
    public List<byte[]> SentBinary { get; } = new();

    public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        // Locked for the same reason as SentMessages: the source pipeline's chunk
        // consumer appends from a background task while tests poll.
        lock (SentBinary)
        {
            SentBinary.Add(data.ToArray());
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Copy of <see cref="SentBinary"/> taken under the same lock <see cref="SendBinaryAsync"/>
    /// appends under, for tests that poll while chunks are still being sent in the background.
    /// </summary>
    public IReadOnlyList<byte[]> SnapshotSentBinary()
    {
        lock (SentBinary)
        {
            return SentBinary.ToList();
        }
    }

    /// <summary>
    /// Whether the client disposed this connection. PairingCodes the documented split between
    /// <c>Dispose</c> and <c>DisposeAsync</c> — see
    /// <c>ClientDisposal_OnlyTheAsyncOverloadDisposesTheConnection</c> (#96).
    /// </summary>
    public bool WasDisposed { get; private set; }

    public ValueTask DisposeAsync()
    {
        WasDisposed = true;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Simulates the socket dropping with auto-reconnect: the connection moves to
    /// <see cref="ConnectionState.Reconnecting"/>, as <see cref="SendspinConnection"/>
    /// does when the WebSocket dies.
    /// </summary>
    public void SimulateConnectionLoss() => SetState(ConnectionState.Reconnecting);

    /// <summary>
    /// Simulates the redial succeeding after <see cref="SimulateConnectionLoss"/>:
    /// Reconnecting → Handshaking, the transition the client's reconnect handshake
    /// listens for.
    /// </summary>
    public void SimulateReconnected() => SetState(ConnectionState.Handshaking);

    public void RaiseTextMessageReceived(string json)
        => RaiseTextMessageReceived(json, HighPrecisionTimer.Shared.GetCurrentTimeMicroseconds());

    /// <summary>
    /// Delivers a frame with an explicit transport receive timestamp, the way a real
    /// connection stamps one before decrypting and parsing. Only the clock-sync exchange reads
    /// it, so everything else uses the overload above and lets it default to now.
    /// </summary>
    public void RaiseTextMessageReceived(string json, long receivedAtMicroseconds)
        => TextMessageReceived?.Invoke(this, new TextMessageReceivedEventArgs(json, receivedAtMicroseconds));

    public void RaiseBinaryMessageReceived(ReadOnlyMemory<byte> data)
        => BinaryMessageReceived?.Invoke(this, data);

    private void SetState(ConnectionState newState)
    {
        var old = State;
        if (old == newState)
        {
            // Matches SendspinConnection.SetState, which does not publish a no-op transition.
            return;
        }

        State = newState;
        StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs { OldState = old, NewState = newState });
    }
}
