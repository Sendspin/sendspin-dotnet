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

    public ConnectionState State => _state;
    public Uri? ServerUri { get; private set; }

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;
    public event EventHandler<string>? TextMessageReceived;
    public event EventHandler<ReadOnlyMemory<byte>>? BinaryMessageReceived;

    public IncomingConnection(
        ILogger<IncomingConnection> logger,
        WebSocketClientConnection socket,
        IWireFraming? framing = null)
    {
        _logger = logger;
        _socket = socket;
        _framing = framing ?? PlaintextWireFraming.Instance;

        // Get server address from connection info
        var clientIp = socket.ClientIpAddress;
        var clientPort = socket.ClientPort;
        ServerUri = new Uri($"ws://{clientIp}:{clientPort}");

        // Wire up events
        _socket.OnMessage = OnTextMessage;
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
        finally
        {
            _sendLock.Release();
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

    private void OnTextMessage(string message) => DispatchInbound(WireFrame.FromText(message));

    private void OnBinaryMessage(byte[] data) => DispatchInbound(new WireFrame(WireFrameKind.Binary, data));

    private void DispatchInbound(WireFrame frame)
    {
        var inbound = _framing.ProcessInbound(frame);

        if (inbound.FatalReason is { } fatal)
        {
            // Per spec: close without sending an application-level error message.
            _logger.LogWarning("Wire framing failure: {Reason}; closing connection", fatal);
            _isOpen = false;
            _ = CloseSocketSafeAsync();
            SetState(ConnectionState.Disconnected, fatal);
            return;
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

    private void OnClose()
    {
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
        _disposed = true;

        await DisconnectAsync("disposing");
        _sendLock.Dispose();
    }
}
