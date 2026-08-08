using System.Buffers;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using Sendspin.SDK.Connection.Framing;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Connection;

/// <summary>
/// WebSocket connection to a Sendspin server.
/// Handles connection lifecycle, message sending/receiving, and automatic reconnection.
/// </summary>
public sealed class SendspinConnection : ISendspinConnection
{
    private readonly ILogger<SendspinConnection> _logger;
    private readonly ConnectionOptions _options;
    private readonly IWireFraming _framing;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private Uri? _serverUri;
    private int _state = (int)ConnectionState.Disconnected;
    private int _reconnectAttempt;
    private int _connectionLostGuard;
    private SendspinHandshakeException? _permanentFailure;
    private bool _lastLossDuringHandshake;
    private int _inboundFramesSinceReset;
    private bool _disposed;

    public ConnectionState State => (ConnectionState)Volatile.Read(ref _state);
    public Uri? ServerUri => _serverUri;

    /// <summary>The wire framing this connection was constructed with (test-only introspection).</summary>
    internal IWireFraming Framing => _framing;

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;
    public event EventHandler<string>? TextMessageReceived;
    public event EventHandler<ReadOnlyMemory<byte>>? BinaryMessageReceived;

    public SendspinConnection(
        ILogger<SendspinConnection> logger,
        ConnectionOptions? options,
        IWireFraming framing)
    {
        _logger = logger;
        _options = options ?? new ConnectionOptions();
        _framing = framing;
    }

    public async Task ConnectAsync(Uri serverUri, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (State is ConnectionState.Connected or ConnectionState.Connecting)
        {
            throw new InvalidOperationException($"Cannot connect while in state {State}");
        }

        _serverUri = serverUri;
        _reconnectAttempt = 0;

        // An explicit dial by the application is always allowed to try again, even after a
        // failure the reconnect loop refuses to retry on its own.
        _permanentFailure = null;
        _lastLossDuringHandshake = false;

        try
        {
            await ConnectInternalAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            if (_options.AutoReconnect && !cancellationToken.IsCancellationRequested)
            {
                // Initial connection failed - enter reconnection loop
                SetState(ConnectionState.Reconnecting, "Initial connection failed");
                await TryReconnectAsync(cancellationToken);
            }
            else
            {
                SetState(ConnectionState.Disconnected, "Connection failed");
                throw;
            }
        }
    }

    private async Task ConnectInternalAsync(CancellationToken cancellationToken)
    {
        if (_serverUri is null)
            throw new InvalidOperationException("Server URI not set");

        SetState(ConnectionState.Connecting);

        try
        {
            await CleanupWebSocketAsync();

            _webSocket = new ClientWebSocket();
            _webSocket.Options.KeepAliveInterval = TimeSpan.FromMilliseconds(_options.KeepAliveIntervalMs);
#if NET9_0_OR_GREATER
            // PING/PONG keep-alive: abort the socket if no PONG arrives in time, so a
            // half-open connection (frozen peer / network drop without a TCP FIN) surfaces
            // as a faulted ReceiveAsync instead of blocking forever. .NET 9+ only.
            if (_options.KeepAliveTimeoutMs > 0)
            {
                _webSocket.Options.KeepAliveTimeout = TimeSpan.FromMilliseconds(_options.KeepAliveTimeoutMs);
            }
#else
            if (_options.KeepAliveTimeoutMs > 0)
            {
                _logger.LogDebug(
                    "KeepAliveTimeoutMs is set but has no effect on this runtime (requires .NET 9+); " +
                    "half-open connections are detected only by the OS TCP timeout.");
            }
#endif

            using var timeoutCts = new CancellationTokenSource(_options.ConnectTimeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            _logger.LogInformation("Connecting to {Uri}...", _serverUri);
            await _webSocket.ConnectAsync(_serverUri, linkedCts.Token);

            _logger.LogInformation("Connected to {Uri}", _serverUri);
            _reconnectAttempt = 0;

            _framing.Reset();
            _inboundFramesSinceReset = 0;
            var startFrames = _framing.Start();
            if (startFrames.Count > 0)
            {
                await SendWireFramesAsync(startFrames, linkedCts.Token);
            }

            _receiveCts = new CancellationTokenSource();
            _receiveTask = ReceiveLoopAsync(_receiveCts.Token);

            SetState(ConnectionState.Handshaking);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetState(ConnectionState.Disconnected, "Connection cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to {Uri}", _serverUri);
            // Let caller decide how to handle failure.
            // TryReconnectAsync's loop handles retries without transitioning through Disconnected.
            // ConnectAsync handles initial connection failure.
            throw;
        }
    }

    public async Task DisconnectAsync(string reason = "restart", CancellationToken cancellationToken = default)
    {
        if (State == ConnectionState.Disconnected)
            return;

        SetState(ConnectionState.Disconnecting, reason);

        try
        {
            // Send goodbye message if connected
            if (_webSocket?.State == WebSocketState.Open)
            {
                try
                {
                    var goodbye = ClientGoodbyeMessage.Create(reason);
                    await SendMessageAsync(goodbye, cancellationToken);

                    await _webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        reason,
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error during graceful disconnect");
                }
            }
        }
        finally
        {
            await CleanupWebSocketAsync();
            SetState(ConnectionState.Disconnected, reason);
        }
    }

    public async Task SendMessageAsync<T>(T message, CancellationToken cancellationToken = default) where T : IMessage
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_webSocket?.State != WebSocketState.Open)
        {
            // Trigger connection lost handling if receive loop hasn't detected it yet.
            // This handles the race where WebSocket detected closure but ReceiveAsync is still blocking.
            if (State is ConnectionState.Connected or ConnectionState.Handshaking)
            {
                _ = Task.Run(() => HandleConnectionLostAsync());
            }

            throw new InvalidOperationException("WebSocket is not connected");
        }

        var json = MessageSerializer.Serialize(message);
        _logger.LogDebug("Sending: {Message}", json);
        await SendWireFramesAsync(_framing.EncodeText(json), cancellationToken);
    }

    public async Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_webSocket?.State != WebSocketState.Open)
        {
            if (State is ConnectionState.Connected or ConnectionState.Handshaking)
            {
                _ = Task.Run(() => HandleConnectionLostAsync());
            }

            throw new InvalidOperationException("WebSocket is not connected");
        }

        await SendWireFramesAsync(_framing.EncodeBinary(data), cancellationToken);
    }

    private async Task SendWireFramesAsync(IEnumerable<WireFrame> frames, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await SendFramesHoldingLockAsync(frames, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task SendFramesHoldingLockAsync(IEnumerable<WireFrame> frames, CancellationToken cancellationToken)
    {
        foreach (var frame in frames)
        {
            await _webSocket!.SendAsync(
                frame.Payload,
                frame.Kind == WireFrameKind.Text ? WebSocketMessageType.Text : WebSocketMessageType.Binary,
                endOfMessage: true,
                cancellationToken);
        }
    }

    private async Task SendDeferredReplyAsync(CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            // EncodeDeferredReply encodes the re-handshake reply under the retiring
            // keys and commits the pending key swap in one call. Encoding, sending, and
            // the commit all happen inside this single lock acquisition, so a concurrent
            // application send either fully precedes the reply (old keys) or queues
            // behind it and encodes under the new keys (#81).
            await SendFramesHoldingLockAsync(_framing.EncodeDeferredReply(), cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_options.ReceiveBufferSize);
        var messageBuffer = new MemoryStream();

        try
        {
            while (!cancellationToken.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                messageBuffer.SetLength(0);

                do
                {
                    result = await _webSocket.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        cancellationToken);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInformation("Server closed connection: {Status} - {Description}",
                            result.CloseStatus, result.CloseStatusDescription);

                        // The measured legacy signature is a 1000 close with *no reply at all*:
                        // aiosendspin < 7.0.0 fails to deserialize client/init and closes without
                        // answering. One inbound frame proves the peer speaks the encrypted
                        // protocol, so a close after that is ambiguous (restarting server,
                        // draining proxy) and must stay retryable.
                        if (!_framing.IsTransportReady
                            && result.CloseStatus == WebSocketCloseStatus.NormalClosure
                            && _inboundFramesSinceReset == 0)
                        {
                            await FailPermanentlyAsync(
                                new SendspinHandshakeException(HandshakeFailureKind.LegacyServer));
                            return;
                        }

                        // A server-initiated close (graceful restart/update) must flow into the
                        // reconnect path, not silently exit the loop. HandleConnectionLostAsync
                        // no-ops when State is Disconnecting or the object is disposed, so an
                        // intentional local disconnect still won't trigger a reconnect.
                        await HandleConnectionLostAsync();
                        return;
                    }

                    messageBuffer.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var messageData = messageBuffer.ToArray();

                // The peer answered, so this connection cannot be the legacy signature.
                _inboundFramesSinceReset++;

                var frame = new WireFrame(
                    result.MessageType == WebSocketMessageType.Text ? WireFrameKind.Text : WireFrameKind.Binary,
                    messageData);

                // Capture the framing's mode BEFORE processing: an encrypted framing marks
                // itself failed as part of raising a fatal (NoiseWireFraming.Fail() moves the
                // phase to Failed and drops the transport), so reading this afterwards would
                // report "not transport ready" for every fatal and lose the distinction below.
                var wasTransportReady = _framing.IsTransportReady;
                var inbound = _framing.ProcessInbound(frame);

                if (inbound.FatalReason is { } fatal)
                {
                    // Per spec: close without sending an application-level error message.
                    _logger.LogWarning("Wire framing failure: {Reason}; closing connection", fatal);

                    if (wasTransportReady)
                    {
                        // A fatal raised on an established session is a desync or a failed
                        // server-initiated re-handshake (key rotation, post-pairing promotion),
                        // not a rejected handshake. Reconnecting re-runs the Noise handshake
                        // from scratch, which is the recovery those need — and it is an
                        // established-session drop, so it uses the ordinary socket schedule.
                        await HandleConnectionLostAsync(lossDuringHandshake: false);
                        return;
                    }

                    await FailPermanentlyAsync(
                        new SendspinHandshakeException(HandshakeFailureKind.HandshakeRejected, fatal));
                    return;
                }

                if (inbound.HasDeferredReply)
                {
                    await SendDeferredReplyAsync(cancellationToken);
                }

                if (inbound.Replies is { Count: > 0 })
                {
                    await SendWireFramesAsync(inbound.Replies, cancellationToken);
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
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal cancellation - a local disconnect/cleanup cancelled our receive token.
        }
        catch (OperationCanceledException ex)
        {
            // Our token was NOT cancelled, so this is an involuntary abort - most commonly
            // the .NET 9+ keep-alive timeout firing on a half-open connection (the PONG never
            // arrived). Treat it as a lost connection so the reconnect path runs.
            _logger.LogWarning(ex, "Receive aborted without local cancellation (keep-alive timeout?)");
            await HandleConnectionLostAsync();
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            _logger.LogWarning("Connection closed unexpectedly");
            await HandleConnectionLostAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in receive loop");
            await HandleConnectionLostAsync();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            messageBuffer.Dispose();
        }
    }

    /// <summary>
    /// Ends the connection without retrying. Used for conditions a retry cannot fix —
    /// a server that does not speak the encrypted protocol, or a rejected handshake.
    /// </summary>
    private async Task FailPermanentlyAsync(SendspinHandshakeException failure)
    {
        // Record the verdict before any await, so a connection-loss handler racing us sees it
        // rather than starting a reconnect this method would then tear down.
        _permanentFailure = failure;

        // Take the same guard the reconnect path takes. Without it, a concurrent
        // HandleConnectionLostAsync (the send-failure path) could establish a fresh socket
        // while we are parked in CleanupWebSocketAsync, and our teardown would then dispose
        // that healthy socket and publish Disconnected over it. The reconnect loop re-checks
        // _permanentFailure, so the verdict still surfaces if the guard is already held.
        if (Interlocked.CompareExchange(ref _connectionLostGuard, 1, 0) == 1)
        {
            _logger.LogDebug("Connection loss already being handled; permanent failure recorded for it");
            return;
        }

        try
        {
            _logger.LogError("{Message}", failure.Message);

            await CleanupWebSocketAsync();
            SetState(ConnectionState.Disconnected, failure.Message, failure);
        }
        finally
        {
            Interlocked.Exchange(ref _connectionLostGuard, 0);
        }
    }

    /// <param name="lossDuringHandshake">
    /// Overrides how the loss is classified for backoff purposes. Callers that already know
    /// the framing's mode at the moment of loss pass it explicitly, because a framing that
    /// raised a fatal has by then marked itself failed and no longer reports transport mode.
    /// </param>
    private async Task HandleConnectionLostAsync(bool? lossDuringHandshake = null)
    {
        if (State == ConnectionState.Disconnecting || _disposed)
            return;

        // Atomic guard - only the first caller proceeds, prevents duplicate reconnection attempts
        // when both send failure and receive loop detect connection loss simultaneously
        if (Interlocked.CompareExchange(ref _connectionLostGuard, 1, 0) == 1)
        {
            _logger.LogDebug("Connection loss already being handled, skipping duplicate call");
            return;
        }

        // Read the framing's mode at the moment of loss, before any reconnect resets it:
        // a drop before transport mode is a handshake failure, not an ordinary socket drop.
        // Only the caller that won the guard records this, so a duplicate caller arriving
        // after a reconnect has already re-armed the framing cannot overwrite it.
        _lastLossDuringHandshake = lossDuringHandshake ?? !_framing.IsTransportReady;

        try
        {
            SetState(ConnectionState.Reconnecting, "Connection lost");

            if (_options.AutoReconnect)
            {
                await TryReconnectAsync(CancellationToken.None);
            }
            else
            {
                SetState(ConnectionState.Disconnected, "Connection lost");
            }
        }
        finally
        {
            Interlocked.Exchange(ref _connectionLostGuard, 0);
        }
    }

    private async Task TryReconnectAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !_disposed)
        {
            if (_permanentFailure is not null)
            {
                SetState(ConnectionState.Disconnected, _permanentFailure.Message, _permanentFailure);
                return;
            }

            if (_options.MaxReconnectAttempts >= 0 && _reconnectAttempt >= _options.MaxReconnectAttempts)
            {
                _logger.LogWarning("Max reconnection attempts ({Max}) reached", _options.MaxReconnectAttempts);
                SetState(ConnectionState.Disconnected, "Max reconnection attempts reached");
                return;
            }

            _reconnectAttempt++;
            var delay = CalculateReconnectDelay();

            _logger.LogInformation("Reconnecting in {Delay}ms (attempt {Attempt})...", delay, _reconnectAttempt);
            SetState(ConnectionState.Reconnecting, $"Attempt {_reconnectAttempt}");

            try
            {
                await Task.Delay(delay, cancellationToken);
                await ConnectInternalAsync(cancellationToken);

                if (State is ConnectionState.Handshaking or ConnectionState.Connected)
                {
                    return; // Successfully reconnected
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reconnection attempt {Attempt} failed", _reconnectAttempt);
            }
        }
    }

    private int CalculateReconnectDelay()
    {
        // An ambiguous handshake failure (the peer dropped us before transport mode, but
        // not with the legacy-server signature) is not a transient socket blip, so it backs
        // off on its own, slower schedule rather than hammering the server.
        if (_lastLossDuringHandshake)
        {
            return _options.HandshakeFailureBackoffMs;
        }

        var delay = (int)(_options.ReconnectDelayMs * Math.Pow(_options.ReconnectBackoffMultiplier, _reconnectAttempt - 1));
        return Math.Min(delay, _options.MaxReconnectDelayMs);
    }

    private async Task CleanupWebSocketAsync()
    {
        _receiveCts?.Cancel();

        if (_receiveTask is not null)
        {
            try
            {
                await _receiveTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
                _logger.LogDebug("Receive task cleanup timeout (expected during shutdown)");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unexpected error during receive task cleanup");
            }
        }

        _receiveCts?.Dispose();
        _receiveCts = null;
        _receiveTask = null;

        if (_webSocket is not null)
        {
            _webSocket.Dispose();
            _webSocket = null;
        }
    }

    private void SetState(ConnectionState newState, string? reason = null, Exception? exception = null)
    {
        var oldState = (ConnectionState)Interlocked.Exchange(ref _state, (int)newState);
        if (oldState == newState) return;
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

    /// <summary>
    /// Marks the connection as fully connected (called after handshake).
    /// </summary>
    public void MarkConnected()
    {
        if (State == ConnectionState.Handshaking)
        {
            SetState(ConnectionState.Connected);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await DisconnectAsync("disposing");
        _sendLock.Dispose();
    }
}
