using Sendspin.SDK.Connection;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// In-memory <see cref="ISendspinConnection"/> test double.
/// Tests drive the <c>SendspinClientService</c> by calling <see cref="RaiseTextMessageReceived"/>
/// instead of running a real WebSocket.
/// </summary>
internal sealed class FakeSendspinConnection : ISendspinConnection, ITimeProbeTransport
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
    /// When set, <c>ITimeProbeTransport.SendTimeMessageAsync</c> uses this instead of the real
    /// clock, so a test can pin the exact T1 that reaches the wire and the pending slot.
    /// </summary>
    public Func<long>? TimeSyncTransmitClock { get; set; }

    /// <summary>
    /// When true, every probe is answered with a synthetic <c>server/time</c> before
    /// <c>SendTimeMessageAsync</c> returns — the fixture equivalent of a server that replies
    /// instantly.
    /// </summary>
    public bool RespondToTimeSync { get; set; }

    /// <summary>
    /// Round trip the default synthetic reply reports, in microseconds. Realized through the
    /// receive stamp, so a client that took T4 after parsing could not produce it.
    /// </summary>
    public long TimeSyncRttMicroseconds { get; set; } = 2000;

    /// <summary>
    /// Per-probe control over the synthetic reply: given the 1-based probe index and its T1,
    /// return the three remaining stamps, or null to leave that probe unanswered.
    /// </summary>
    public Func<int, long, (long ServerReceived, long ServerTransmitted, long ReceivedAt)?>? TimeSyncReplyOverride { get; set; }

    /// <summary>
    /// The T4 the next <see cref="RaiseTextMessageReceived"/> reports as the transport's
    /// receive stamp. Zero means "behave like a transport without the seam".
    /// </summary>
    public long NextReceivedAtMicroseconds { get; set; }

    /// <inheritdoc/>
    public long LastTextReceivedAtMicroseconds { get; private set; }

    /// <summary>A copy of <see cref="SentMessages"/>, safe to read while the client sends.</summary>
    public IReadOnlyList<IMessage> SnapshotSentMessages()
    {
        lock (SentMessages)
        {
            return SentMessages.ToList();
        }
    }

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;
    public event EventHandler<string>? TextMessageReceived;
    public event EventHandler<ReadOnlyMemory<byte>>? BinaryMessageReceived;

    public Task ConnectAsync(Uri serverUri, CancellationToken cancellationToken = default)
    {
        ServerUri = serverUri;
        SetState(ConnectionState.Connected);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(string reason = "user_request", CancellationToken cancellationToken = default)
    {
        SetState(ConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    public Task SendMessageAsync<T>(T message, CancellationToken cancellationToken = default)
        where T : IMessage
    {
        if (EnforceConnectionState && State != ConnectionState.Connected)
        {
            throw new InvalidOperationException("WebSocket is not connected");
        }

        lock (SentMessages)
        {
            SentMessages.Add(message);
        }

        return Task.CompletedTask;
    }

    public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc/>
    public Task SendTimeMessageAsync(Action<long> onTransmitted, CancellationToken cancellationToken)
    {
        if (EnforceConnectionState && State != ConnectionState.Connected)
        {
            throw new InvalidOperationException("WebSocket is not connected");
        }

        // Stamped and reported before the message is recorded, exactly as the real transports
        // do it inside the send lock: the caller's pending slot must be armed before anything
        // that could carry a reply.
        var t1 = TimeSyncTransmitClock?.Invoke() ?? HighPrecisionTimer.Shared.GetCurrentTimeMicroseconds();
        onTransmitted(t1);

        int index;
        lock (SentMessages)
        {
            SentMessages.Add(ClientTimeMessage.Create(t1));
            index = SentMessages.Count(m => m is ClientTimeMessage);
        }

        if (RespondToTimeSync)
        {
            RespondToProbe(index, t1);
        }

        return Task.CompletedTask;
    }

    private void RespondToProbe(int index, long t1)
    {
        var reply = TimeSyncReplyOverride is { } over
            ? over(index, t1)
            : (t1 + (TimeSyncRttMicroseconds / 2),
               t1 + (TimeSyncRttMicroseconds / 2) + 100,
               t1 + TimeSyncRttMicroseconds + 100);

        if (reply is not { } r)
        {
            return;
        }

        NextReceivedAtMicroseconds = r.ReceivedAt;
        RaiseTextMessageReceived(
            "{ \"type\": \"server/time\", \"payload\": { \"client_transmitted\": " + t1 +
            ", \"server_received\": " + r.ServerReceived +
            ", \"server_transmitted\": " + r.ServerTransmitted + " } }");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void RaiseTextMessageReceived(string json)
    {
        LastTextReceivedAtMicroseconds = NextReceivedAtMicroseconds;
        TextMessageReceived?.Invoke(this, json);
    }

    public void RaiseBinaryMessageReceived(ReadOnlyMemory<byte> data)
        => BinaryMessageReceived?.Invoke(this, data);

    private void SetState(ConnectionState newState)
    {
        var old = State;
        State = newState;
        StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs { OldState = old, NewState = newState });
    }
}
