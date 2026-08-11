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

    public async Task DisconnectAsync(string reason = "restart", CancellationToken cancellationToken = default)
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
                    var goodbye = ClientGoodbyeMessage.Create(reason);
                    await SendMessageAsync(goodbye, cancellationToken);

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

    private void OnClose()
    {
        // A server that predates the encrypted protocol (aiosendspin < 7.0.0) dials in, fails
        // to deserialize the client/init we sent from StartAsync, and closes without ever
        // replying. That produces no framing fatal, so this close handler is the only place
        // the condition is visible. One inbound frame proves the peer speaks the encrypted
        // protocol, so a close after that is ambiguous (restarting server, draining proxy).
        // The Handshaking guard keeps a local disconnect (e.g. the host's handshake timeout,
        // which closes the socket itself) from being reported as a legacy server.
        if (_state == ConnectionState.Handshaking
            && !_framing.IsTransportReady
            && _inboundFramesSinceReset == 0)
        {
            var failure = new SendspinHandshakeException(HandshakeFailureKind.LegacyServer);
            _logger.LogWarning("{Message}", failure.Message);
            _isOpen = false;
            SetState(ConnectionState.Disconnected, failure.Message);
            return;
        }

        _logger.LogInformation("Server closed connection");
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
