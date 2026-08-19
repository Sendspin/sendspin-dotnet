using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using Sendspin.SDK.Connection.Framing;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Connection;

/// <summary>
/// Wraps an incoming WebSocket connection from a Sendspin server.
/// Used for server-initiated connections where the server connects to us.
/// </summary>
public sealed class IncomingConnection : ISendspinConnection
{
    private readonly ILogger<IncomingConnection> _logger;
    private readonly WebSocketClientConnection _socket;
    private readonly IWireFraming _framing;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private ConnectionState _state = ConnectionState.Disconnected;
    private bool _disposed;
    private bool _isOpen;
    private int _inboundFramesSinceReset;

    public ConnectionState State => _state;
    public Uri? ServerUri { get; private set; }

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;
    public event EventHandler<string>? TextMessageReceived;
    public event EventHandler<ReadOnlyMemory<byte>>? BinaryMessageReceived;

    public IncomingConnection(
        ILogger<IncomingConnection> logger,
        WebSocketClientConnection socket,
        IWireFraming framing)
    {
        _logger = logger;
        _socket = socket;
        _framing = framing;

        // Get server address from connection info
        var clientIp = socket.ClientIpAddress;
        var clientPort = socket.ClientPort;
        ServerUri = new Uri($"ws://{clientIp}:{clientPort}");

        // Wire up events
        _socket.OnText = OnTextMessage;
        _socket.OnBinary = OnBinaryMessage;
        _socket.OnClose = OnClose;
        _socket.OnError = OnError;
    }

    /// <summary>
    /// Starts processing messages on this connection.
    /// For incoming connections, this just marks the connection as ready.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_state != ConnectionState.Disconnected)
        {
            throw new InvalidOperationException($"Cannot start while in state {_state}");
        }

        _isOpen = true;

        _framing.Reset();
        _inboundFramesSinceReset = 0;
        var startFrames = _framing.Start();
        if (startFrames.Count > 0)
        {
            await SendWireFramesAsync(startFrames, cancellationToken);
        }

        SetState(ConnectionState.Handshaking);
    }

    /// <summary>
    /// Not used for incoming connections - throws InvalidOperationException.
    /// </summary>
    public Task ConnectAsync(Uri serverUri, CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(
            "IncomingConnection does not support outgoing connections. " +
            "Use SendspinConnection for client-initiated connections.");
    }

    public Task DisconnectAsync(string reason = "restart", CancellationToken cancellationToken = default)
        => CloseAfterSendingAsync(ClientGoodbyeMessage.Create(reason), reason, cancellationToken);

    /// <summary>
    /// Closes the connection without sending <c>client/goodbye</c> — the spec's literal
    /// "dropped".
    /// </summary>
    /// <remarks>
    /// For the ends the spec defines no goodbye reason for, such as a provisional connection
    /// that never activates (connection.md:40). <c>client/goodbye.reason</c> is a closed set
    /// (messaging.md:426), so inventing a reason is worse than silence: a server cannot parse
    /// it, reads it as no goodbye at all, and may immediately reconnect — while the invented
    /// string still tells an unauthenticated peer exactly why it was dropped. Mirrors how a
    /// framing failure already closes here, with no application-level message.
    /// </remarks>
    /// <param name="reason">Local reason, for the state change and the log only. Never sent.</param>
    public async Task CloseWithoutGoodbyeAsync(string reason)
    {
        if (_state == ConnectionState.Disconnected || !_isOpen)
            return;

        // State before the close, as the framing-failure path does: the socket close can bring
        // the peer's own Close frame back through OnClose, and a state still reading
        // Handshaking there would be misclassified as the legacy-server signature (#97).
        _isOpen = false;
        SetState(ConnectionState.Disconnected, reason);
        await CloseSocketSafeAsync();
    }

    public async Task SendMessageAsync<T>(T message, CancellationToken cancellationToken = default) where T : IMessage
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_isOpen)
        {
            throw new InvalidOperationException("WebSocket is not connected");
        }

        var json = MessageSerializer.Serialize(message);
        _logger.LogDebug("Sending: {Message}", json);
        await SendWireFramesAsync(_framing.EncodeText(json), cancellationToken);
    }

    public async Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_isOpen)
        {
            throw new InvalidOperationException("WebSocket is not connected");
        }

        await SendWireFramesAsync(_framing.EncodeBinary(data), cancellationToken);
    }

    /// <summary>
    /// Closes after sending <c>pair/abort</c> instead of <c>client/goodbye</c>. connection.md
    /// routes an arbitration loss on a pairing handshake through pair/abort reason
    /// <c>concurrent_attempt</c>, and pairing.md gives that reason close-after-send semantics —
    /// so this is the same close as <see cref="DisconnectAsync"/> with a different farewell on
    /// the way out, not an extra message before one (#203).
    /// </summary>
    internal Task DisconnectWithPairAbortAsync(string reason, CancellationToken cancellationToken = default)
        => CloseAfterSendingAsync(
            new PairAbortMessage { Payload = new PairAbortPayload { Reason = reason } },
            reason,
            cancellationToken);

    /// <summary>
    /// Sends one farewell message and closes. Generic in the message type rather than taking an
    /// <see cref="IMessage"/>: serialization is source-generated per concrete type, which an
    /// interface-typed argument would defeat under PublishAot.
    /// </summary>
    private async Task CloseAfterSendingAsync<T>(T farewell, string reason, CancellationToken cancellationToken)
        where T : IMessage
    {
        if (_state == ConnectionState.Disconnected || !_isOpen)
            return;

        SetState(ConnectionState.Disconnecting, reason);

        try
        {
            if (_isOpen)
            {
                try
                {
                    await SendMessageAsync(farewell, cancellationToken);

                    await _socket.CloseAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error during graceful disconnect");
                }
            }
        }
        finally
        {
            _isOpen = false;
            SetState(ConnectionState.Disconnected, reason);
        }
    }

    private async Task SendWireFramesAsync(IEnumerable<WireFrame> frames, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SendFramesHoldingLockAsync(frames).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task SendFramesHoldingLockAsync(IEnumerable<WireFrame> frames)
    {
        foreach (var frame in frames)
        {
            if (frame.Kind == WireFrameKind.Text)
            {
                await _socket.SendAsync(frame.PayloadAsText()).ConfigureAwait(false);
            }
            else
            {
                await _socket.SendAsync(frame.Payload.ToArray()).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Marks the connection as fully connected (called after handshake).
    /// </summary>
    public void MarkConnected()
    {
        if (_state == ConnectionState.Handshaking)
        {
            SetState(ConnectionState.Connected);
        }
    }

    // Built from the received bytes, not from a decoded string: the Noise prologue binds the
    // exact wire bytes of both init messages, and WireFrame.FromText would re-encode them
    // (#124). This mirrors what SendspinConnection already does on the dial path.
    private void OnTextMessage(byte[] data) => DispatchInbound(new WireFrame(WireFrameKind.Text, data));

    private void OnBinaryMessage(byte[] data) => DispatchInbound(new WireFrame(WireFrameKind.Binary, data));

    private void DispatchInbound(WireFrame frame)
    {
        // The peer answered, so this connection cannot be the legacy signature (see OnClose).
        _inboundFramesSinceReset++;

        // Capture before ProcessInbound: NoiseWireFraming.Fail() moves the phase to Failed
        // as part of producing a fatal result, which drops IsTransportReady to false. Reading
        // it after the call would collapse both cases below into the handshake-time one.
        var wasTransportReady = _framing.IsTransportReady;
        var inbound = _framing.ProcessInbound(frame);

        if (inbound.FatalReason is { } fatal)
        {
            // Mirrors the dial path's classification. A fatal raised before transport mode is
            // a rejected handshake (no matching PSK, PSK bound to another server, unsupported
            // version) — never the legacy-server signature, which produces no fatal at all
            // because the peer never sends anything. A fatal on an established session is a
            // desync or a failed server-initiated re-handshake. Neither is retried here: the
            // listen path has no reconnect loop, so we close and let the server redial.
            _logger.LogWarning("{Message}", wasTransportReady
                ? $"Wire framing failure on an established session: {fatal}; closing connection"
                : new SendspinHandshakeException(HandshakeFailureKind.HandshakeRejected, fatal).Message);

            // Per spec: close without sending an application-level error message.
            _isOpen = false;
            _ = CloseSocketSafeAsync();
            SetState(ConnectionState.Disconnected, fatal);
            return;
        }

        if (inbound.HasDeferredReply)
        {
            // Re-handshake reply: encoded and committed on the send path under the send
            // lock, but dispatched without blocking the receive path, same as Replies.
            _ = SendDeferredReplySafeAsync();
        }

        if (inbound.Replies is { Count: > 0 } replies)
        {
            // Replies only occur for handshaking framings; the socket callbacks are
            // synchronous, so dispatch without blocking the receive path.
            _ = SendRepliesSafeAsync(replies);
        }

        if (inbound.Text is { } text)
        {
            _logger.LogDebug("Received text: {Message}", text.Length > 500 ? text[..500] + "..." : text);
            TextMessageReceived?.Invoke(this, text);
        }

        if (inbound.Binary is { } binary)
        {
            _logger.LogTrace("Received binary: {Length} bytes", binary.Length);
            BinaryMessageReceived?.Invoke(this, binary);
        }
    }

    private async Task CloseSocketSafeAsync()
    {
        try
        {
            await _socket.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error closing socket after framing failure");
        }
    }

    private async Task SendRepliesSafeAsync(IReadOnlyList<WireFrame> replies)
    {
        try
        {
            await SendWireFramesAsync(replies, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send framing reply frames");
        }
    }

    private async Task SendDeferredReplySafeAsync()
    {
        try
        {
            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // EncodeDeferredReply encodes the re-handshake reply under the retiring
                // keys and commits the pending key swap in one call. Encoding, sending,
                // and the commit all happen inside this single lock acquisition, so a
                // concurrent application send either fully precedes the reply (old keys)
                // or queues behind it and encodes under the new keys (#81).
                await SendFramesHoldingLockAsync(_framing.EncodeDeferredReply()).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send deferred framing reply");
        }
    }

    private void OnClose(WebSocketCloseStatus? closeStatus)
    {
        // A server that predates the encrypted protocol (aiosendspin < 7.0.0) dials in, fails
        // to deserialize the client/init we sent from StartAsync, and closes without ever
        // replying. That produces no framing fatal, so this close handler is the only place
        // the condition is visible. One inbound frame proves the peer speaks the encrypted
        // protocol, so a close after that is ambiguous (restarting server, draining proxy).
        // The Handshaking guard keeps a local disconnect (e.g. the host's handshake timeout,
        // which routes through DisconnectAsync and so is Disconnecting by now) from being
        // reported as a legacy server.
        if (_state == ConnectionState.Handshaking
            && !_framing.IsTransportReady
            && _inboundFramesSinceReset == 0)
        {
            // The measured legacy signature is specifically a *1000* close with no reply,
            // which is why the dial path requires NormalClosure too (see SendspinConnection's
            // receive loop). Requiring it here as well is the whole point of #97: every
            // abnormal mid-handshake end — a TCP drop, a keep-alive abort, which
            // WebSocketClientConnection routes through this same callback with no status —
            // was otherwise reported as a server too old to speak encryption, sending
            // operators to upgrade a server that was never the problem.
            if (closeStatus != WebSocketCloseStatus.NormalClosure)
            {
                // Still a warning, not the ordinary close below: a handshake that never
                // completed is a failure, and this path has no reconnect loop to retry it.
                // The message stays agnostic because the close alone cannot say why.
                _logger.LogWarning(
                    "Connection closed during the handshake before the server replied ({Status}); "
                    + "the handshake did not complete",
                    closeStatus?.ToString() ?? "no close status");
                _isOpen = false;
                SetState(ConnectionState.Disconnected, "Handshake incomplete: connection closed");
                return;
            }

            var failure = new SendspinHandshakeException(HandshakeFailureKind.LegacyServer);
            _logger.LogWarning("{Message}", failure.Message);
            _isOpen = false;
            SetState(ConnectionState.Disconnected, failure.Message);
            return;
        }

        _logger.LogInformation("Server closed connection ({Status})",
            closeStatus?.ToString() ?? "no close status");
        _isOpen = false;
        SetState(ConnectionState.Disconnected, "Connection closed by server");
    }

    private void OnError(Exception ex)
    {
        _logger.LogError(ex, "WebSocket error");
        _isOpen = false;
        SetState(ConnectionState.Disconnected, ex.Message, ex);
    }

    private void SetState(ConnectionState newState, string? reason = null, Exception? exception = null)
    {
        var oldState = _state;
        if (oldState == newState) return;

        _state = newState;
        _logger.LogDebug("Connection state: {OldState} -> {NewState} ({Reason})",
            oldState, newState, reason ?? "N/A");

        StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs
        {
            OldState = oldState,
            NewState = newState,
            Reason = reason,
            Exception = exception
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        // Same ordering rule as SendspinConnection.DisposeAsync: the send guards below key off
        // _disposed, so setting it before the goodbye threw ObjectDisposedException into
        // DisconnectAsync's catch and the connection closed without a word. See that method
        // for why an unparseable or absent reason makes a conformant server auto-reconnect.
        await DisconnectAsync(GoodbyeReasons.Shutdown);

        // DisconnectAsync only sends our Close frame (#143) — it never drove the socket to
        // Closed, so nothing released the WebSocket, TcpClient, or receive-loop CTS. Disposing
        // here cancels that CTS before awaiting the receive loop, so a cancelled ReceiveAsync
        // aborts the socket instead of waiting on the peer, even one that never answers close.
        await _socket.DisposeAsync();

        _disposed = true;
        _sendLock.Dispose();
    }
}
