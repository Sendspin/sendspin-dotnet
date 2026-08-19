using System.Buffers;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using Sendspin.SDK.Connection.Framing;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Synchronization;

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

    /// <summary>
    /// True inside the receive loop's async flow. Cleanup reached from that loop must not wait
    /// for the loop to finish — it would be waiting on itself (#98 item 4).
    /// </summary>
    /// <remarks>
    /// An <see cref="AsyncLocal{T}"/> rather than a parameter threaded through
    /// <see cref="HandleConnectionLostAsync"/>, <see cref="TryReconnectAsync"/> and
    /// <see cref="ConnectInternalAsync"/>, all of which sit between the loop and the cleanup.
    /// This is safe only because the loop is started with <see cref="Task.Run(Func{Task})"/>,
    /// which forks the execution context: started directly, the assignment below would run in
    /// the caller's context and leak this flag back into the connect path.
    /// </remarks>
    private readonly AsyncLocal<bool> _onReceiveLoop = new();

    /// <summary>
    /// Cancelled by disposal. Linked into the reconnect loop so a parked reconnect delay
    /// unwinds when the connection goes away, instead of waking minutes later to dial a dead
    /// port (#98 item 5).
    /// </summary>
    private readonly CancellationTokenSource _lifetime = new();

    public ConnectionState State => (ConnectionState)Volatile.Read(ref _state);
    public Uri? ServerUri => _serverUri;

    /// <summary>The wire framing this connection was constructed with (test-only introspection).</summary>
    internal IWireFraming Framing => _framing;

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;
    public event EventHandler<TextMessageReceivedEventArgs>? TextMessageReceived;
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
        Volatile.Write(ref _permanentFailure, null);
        Volatile.Write(ref _lastLossDuringHandshake, false);

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
            Volatile.Write(ref _inboundFramesSinceReset, 0);
            var startFrames = _framing.Start();
            if (startFrames.Count > 0)
            {
                await SendWireFramesAsync(startFrames, linkedCts.Token);
            }

            _receiveCts = new CancellationTokenSource();

            // Task.Run, not a direct call: it forks the execution context so the loop's
            // _onReceiveLoop marker cannot leak back into this method. Matches how
            // WebSocketClientConnection starts its own loop. Do not "simplify" this away.
            _receiveTask = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token));

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

                    // CloseOutputAsync, not CloseAsync: the latter performs the full closing
                    // handshake and waits for the peer's Close frame, which a crashed, hung or
                    // non-conformant peer never sends. The listen path had the identical call
                    // and did hang (#143); this is the same shape on the dial path (#160).
                    //
                    // The finally below does not bound it. A finally runs when the try exits,
                    // and an await that never returns never exits -- so CleanupWebSocketAsync
                    // is unreachable in exactly the case it looks like it covers. Nor can the
                    // token save it: DisposeAsync reaches here via DisconnectAsync(shutdown)
                    // with no token at all, and ISendSpinClient.DisconnectAsync has no
                    // CancellationToken parameter, so no caller can supply a cancellable one.
                    //
                    // CloseOutputAsync sends our Close frame and returns without waiting, which
                    // still gives the peer a clean close. The socket is left in CloseSent until
                    // the peer replies or CleanupWebSocketAsync disposes it.
                    await _webSocket.CloseOutputAsync(
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

    public async Task SendTimeMessageAsync(Action<long> onTransmitted, CancellationToken cancellationToken = default)
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

        string json;

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            // T1 is stamped here and not by the caller: after the wait on the send lock, so a
            // probe queued behind another message does not carry a timestamp from before that
            // message was even written, and inside the lock, so nothing can be written between
            // the stamp and this frame. Serialization and Noise encryption still fall between
            // the two, exactly as they do in the reference transport, which likewise captures
            // client_transmitted and then formats the JSON inline (#227).
            var clientTransmitted = HighPrecisionTimer.Shared.GetCurrentTimeMicroseconds();
            onTransmitted(clientTransmitted);

            json = MessageSerializer.Serialize(ClientTimeMessage.Create(clientTransmitted));
            await SendFramesHoldingLockAsync(_framing.EncodeText(json), cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }

        // Logged after the write rather than before it, so the logging sink's cost cannot land
        // between the T1 stamp and the socket.
        _logger.LogDebug("Sending: {Message}", json);
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
        // Every terminal path below reaches CleanupWebSocketAsync, which would otherwise wait
        // two seconds for this very task to finish before disposing the socket (#98 item 4).
        _onReceiveLoop.Value = true;

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
                            && Volatile.Read(ref _inboundFramesSinceReset) == 0)
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

                // T4 for any clock-sync exchange this frame carries. Taken here — the frame is
                // complete, nothing has been decrypted or parsed yet — because the spec defines
                // it as the receive time "captured locally when the response arrives". Stamping
                // it after deserialization charged decrypt and parse time to the round trip.
                var receivedAtMicroseconds = HighPrecisionTimer.Shared.GetCurrentTimeMicroseconds();

                var messageData = messageBuffer.ToArray();

                // The peer answered, so this connection cannot be the legacy signature.
                Interlocked.Increment(ref _inboundFramesSinceReset);

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
                    TextMessageReceived?.Invoke(this, new TextMessageReceivedEventArgs(text, receivedAtMicroseconds));
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
        Volatile.Write(ref _permanentFailure, failure);

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
        Volatile.Write(ref _lastLossDuringHandshake, lossDuringHandshake ?? !_framing.IsTransportReady);

        try
        {
            SetState(ConnectionState.Reconnecting, "Connection lost");

            if (_options.AutoReconnect)
            {
                await TryReconnectAsync(CancellationToken.None);
            }
            else if (!await PublishPermanentFailureIfRecordedAsync())
            {
                // Only when no verdict is pending. With AutoReconnect off this is the sole
                // exit, and publishing a bare "Connection lost" unconditionally discarded the
                // typed exception FailPermanentlyAsync had recorded for us to publish — the
                // application saw a plain drop where the SDK knew the cause (#98 item 2a).
                SetState(ConnectionState.Disconnected, "Connection lost");
            }
        }
        finally
        {
            Interlocked.Exchange(ref _connectionLostGuard, 0);
        }
    }

    /// <summary>
    /// Publishes a verdict <see cref="FailPermanentlyAsync"/> recorded but could not publish,
    /// tearing the socket down with it. Returns whether one was pending.
    /// </summary>
    /// <remarks>
    /// FailPermanentlyAsync records the verdict and then bails when it finds the
    /// connection-lost guard already held, relying on the holder to publish it. So every exit
    /// the holder can take has to consult it. Two did not: a reconnect that succeeded returned
    /// as though healthy, and the AutoReconnect-off path published a bare "Connection lost"
    /// (#98 items 1, 2a and 2b). The cleanup matters as much as the state change — in the
    /// succeeded-reconnect case there is a live socket to take down, and at the loop top a dead
    /// one that nothing else disposes.
    /// </remarks>
    private async Task<bool> PublishPermanentFailureIfRecordedAsync()
    {
        if (Volatile.Read(ref _permanentFailure) is not { } failure)
            return false;

        await CleanupWebSocketAsync();
        SetState(ConnectionState.Disconnected, failure.Message, failure);
        return true;
    }

    private async Task TryReconnectAsync(CancellationToken cancellationToken)
    {
        // Disposal has to be able to interrupt the delay below. Without this the loop parked on
        // an uncancellable Task.Delay — up to the full handshake backoff — and woke long after
        // the connection was gone to dial a dead port (#98 item 5).
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetime.Token);
        var token = linkedCts.Token;

        while (!token.IsCancellationRequested && !_disposed)
        {
            if (await PublishPermanentFailureIfRecordedAsync())
            {
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
                await Task.Delay(delay, token);
                await ConnectInternalAsync(token);

                if (State is ConnectionState.Handshaking or ConnectionState.Connected)
                {
                    // A permanence recorded while this attempt was in flight — inside the delay
                    // or the dial — was not visible at the loop top, and FailPermanentlyAsync
                    // could not publish it because this loop holds the guard. Returning here
                    // without re-reading left the verdict unpublished and the connection sitting
                    // in Handshaking for good, and left the field set on a healthy connection so
                    // the next ordinary drop was refused a retry (#98 items 1 and 2b).
                    await PublishPermanentFailureIfRecordedAsync();
                    return;
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
        if (Volatile.Read(ref _lastLossDuringHandshake))
        {
            return _options.HandshakeFailureBackoffMs;
        }

        var delay = (int)(_options.ReconnectDelayMs * Math.Pow(_options.ReconnectBackoffMultiplier, _reconnectAttempt - 1));
        return Math.Min(delay, _options.MaxReconnectDelayMs);
    }

    private async Task CleanupWebSocketAsync()
    {
        _receiveCts?.Cancel();

        if (_receiveTask is not null && _onReceiveLoop.Value)
        {
            // Reached from the receive loop itself, so the wait below would be the loop
            // awaiting its own completion: it can only end by timing out, and it burned the
            // full two seconds on every legacy-server diagnostic and every receive-loop-driven
            // reconnect (#98 item 4). Skipping it costs nothing — this loop is on its way out
            // and does not touch the socket again, which is the only thing the wait protected.
            _logger.LogDebug("Cleanup reached from the receive loop; not waiting for it to end");
        }
        else if (_receiveTask is not null)
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

        // Before the goodbye, not after: a reconnect loop parked in its delay has to unwind now.
        // Nothing on the shutdown path below runs on this token, so cancelling it early cannot
        // cut the goodbye short (#98 item 5).
        await _lifetime.CancelAsync();

        // Say goodbye BEFORE marking disposed. SendMessageAsync refuses to send on a disposed
        // connection, so setting the flag first made the goodbye throw ObjectDisposedException
        // straight into DisconnectAsync's catch, where it was swallowed at Debug level — the
        // connection closed silently every time. A server that sees a client vanish without a
        // goodbye is told to assume 'restart' and auto-reconnect (messaging.md:442), so an app
        // that had exited kept being reconnected to.
        //
        // 'shutdown' is the spec's reason for a client that is not coming back
        // (messaging.md:436). An app that IS coming back — restarting to self-update, say —
        // should call DisconnectAsync(GoodbyeReasons.Restart) itself before disposing.
        await DisconnectAsync(GoodbyeReasons.Shutdown);

        _disposed = true;
        _sendLock.Dispose();
        _lifetime.Dispose();
    }
}
