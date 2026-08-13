using Sendspin.SDK.Connection;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// In-memory <see cref="ISendspinConnection"/> test double.
/// Tests drive the <c>SendspinClientService</c> by calling <see cref="RaiseTextMessageReceived"/>
/// instead of running a real WebSocket.
/// </summary>
internal sealed class FakeSendspinConnection : ISendspinConnection
{
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
    /// When true, every <see cref="ClientTimeMessage"/> probe passed to
    /// <see cref="SendMessageAsync"/> is answered synchronously with a matching server/time
    /// reply, so the client's time-sync bursts complete and feed measurements into its clock
    /// synchronizer. This is how a fixture drives clock-sync convergence over the wire without
    /// a real server. Off by default: most tests want no unsolicited inbound traffic.
    /// </summary>
    public bool RespondToTimeSync { get; set; }

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
    public event EventHandler<string>? TextMessageReceived;
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

        if (RespondToTimeSync && message is ClientTimeMessage probe)
        {
            long t1 = probe.ClientTransmitted;
            RaiseTextMessageReceived(
                $$$"""
                {"type":"server/time","payload":{"client_transmitted":{{{t1}}},"server_received":{{{t1 + 10}}},"server_transmitted":{{{t1 + 20}}} }}
                """);
        }
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

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

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
        => TextMessageReceived?.Invoke(this, json);

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
