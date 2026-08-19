using Microsoft.Extensions.Logging;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Discovery;
using Sendspin.SDK.Extensions;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Client;

/// <summary>
/// Hosts a Sendspin client service that accepts incoming server connections.
/// This is the server-initiated mode where:
/// 1. We run a WebSocket server
/// 2. We advertise via mDNS as _sendspin._tcp.local.
/// 3. Sendspin servers discover and connect to us
/// </summary>
public sealed class SendspinHostService : IAsyncDisposable
{
    private readonly ILogger<SendspinHostService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SendspinListener _listener;
    private readonly MdnsServiceAdvertiser _advertiser;
    private readonly AdvertiserOptions _advertiserOptions;
    private readonly SendspinClientOptions _options;
    private readonly ILastPlayedServerStore? _lastPlayedServerStore;

    private readonly Dictionary<string, ActiveServerConnection> _connections = new();
    private readonly object _connectionsLock = new();

    // Serializes this host's EnsurePairingPsk/RotatePairingPsk sequences. The per-connection
    // clients hold their own private locks over the same shared store, so cross-object
    // sequences can still interleave — every individual store operation is safe (the shipped
    // stores lock internally), so the worst case is nondeterminism, not corruption.
    private readonly object _pairingStoreLock = new();

    /// <summary>
    /// Whether the host is running (listening and advertising).
    /// </summary>
    public bool IsRunning => _listener.IsListening && (!_advertiserOptions.Enabled || _advertiser.IsAdvertising);

    /// <summary>
    /// Whether the service is currently being advertised via mDNS.
    /// </summary>
    public bool IsAdvertising => _advertiser.IsAdvertising;

    /// <summary>
    /// The DNS-SD instance name being advertised.
    /// </summary>
    public string InstanceName => _advertiser.InstanceName;

    /// <summary>
    /// The actual port the listener is bound to (resolves an OS-assigned port when configured as 0).
    /// </summary>
    public int ListeningPort => _listener.BoundPort;

    /// <summary>
    /// Currently connected servers.
    /// </summary>
    public IReadOnlyList<ConnectedServerInfo> ConnectedServers
    {
        get
        {
            lock (_connectionsLock)
            {
                return _connections.Values
                    .Where(c => c.Client.ConnectionState == ConnectionState.Connected)
                    .Select(c => new ConnectedServerInfo
                    {
                        ServerId = c.ServerId,
                        ServerName = c.Client.ServerName ?? c.ServerId,
                        ConnectedAt = c.ConnectedAt,
                        ClockSyncStatus = c.Client.ClockSyncStatus
                    })
                    .ToList();
            }
        }
    }

    /// <summary>
    /// Raised when a new server connects and completes handshake.
    /// </summary>
    public event EventHandler<ConnectedServerInfo>? ServerConnected;

    /// <summary>
    /// Raised when a server disconnects.
    /// </summary>
    public event EventHandler<string>? ServerDisconnected;

    /// <summary>
    /// Raised when playback state changes on any connection.
    /// </summary>
    public event EventHandler<GroupState>? GroupStateChanged;

    /// <summary>
    /// Raised when this player's volume or mute state is changed by a server command.
    /// </summary>
    public event EventHandler<PlayerState>? PlayerStateChanged;

    /// <summary>
    /// Raised when an artwork image is received on a channel (0-3).
    /// </summary>
    public event EventHandler<ArtworkReceivedEventArgs>? ArtworkReceived;

    /// <summary>
    /// Raised when a single artwork channel is cleared (empty artwork binary message from server).
    /// </summary>
    public event EventHandler<ArtworkClearedEventArgs>? ArtworkCleared;

    /// <summary>
    /// Raised when the group's color palette changes (the <c>color</c> role).
    /// </summary>
    public event EventHandler<ColorPalette>? ColorChanged;

    /// <summary>
    /// Raised for each decoded visualizer feature frame (the <c>visualizer@v1</c> role).
    /// </summary>
    public event EventHandler<VisualizerFrame>? VisualizationReceived;

    /// <summary>
    /// Raised once per server handshake (including reconnects) for any connected client,
    /// carrying that server's parsed <c>server/hello</c> payload.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Fires on each session's first <c>server/activate</c>, not on <c>server/hello</c>
    /// itself</b> — the encrypted handshake completes on the activate. See
    /// <see cref="ISendspinClient.ServerHelloReceived"/>.
    /// </para>
    /// <para>
    /// Multiple concurrent connections each raise it independently, so a consumer tracking
    /// per-server state has to distinguish them — but <b>not</b> by
    /// <see cref="ServerHelloPayload.ServerId"/>, which this doc previously recommended and
    /// which is always empty under the encrypted protocol: <c>server/hello</c> carries only
    /// <c>name</c>, and the server's real identity is its authenticated Noise static key.
    /// Track the connection instead — <see cref="ServerConnected"/> and
    /// <see cref="ServerDisconnected"/> carry the server id the host arbitrates on.
    /// </para>
    /// </remarks>
    public event EventHandler<ServerHelloPayload>? ServerHelloReceived;

    /// <summary>
    /// Raised when any connected client receives a <c>stream/start</c>.
    /// Fires once per stream/start frame (audio, artwork, or both).
    /// </summary>
    public event EventHandler<StreamStartPayload>? StreamStartReceived;

    /// <summary>
    /// Raised when any connected client receives a <c>stream/end</c>. Carries the roles whose
    /// output is ending — see <see cref="ISendspinClient.StreamEndReceived"/>.
    /// </summary>
    public event EventHandler<StreamEndPayload>? StreamEndReceived;

    /// <summary>
    /// Raised when any connected client receives a <c>stream/clear</c>. Carries the roles whose
    /// buffers are to be cleared — see <see cref="ISendspinClient.StreamClearReceived"/>.
    /// </summary>
    public event EventHandler<StreamClearPayload>? StreamClearReceived;

    /// <summary>
    /// Raised when the last-played server ID changes.
    /// Consumers should persist this value so it survives app restarts.
    /// </summary>
    public event EventHandler<string>? LastPlayedServerIdChanged;

    /// <summary>Raised when a Pairing PSK or pairing code exchange completes on any connection (arg: paired server id).</summary>
    public event EventHandler<string>? PairingCompleted;

    /// <summary>
    /// Raised when a server on any connection changes this client's pairing configuration
    /// via <c>management/set-pairing-config</c>, or removes the stored Pairing record via
    /// <c>management/remove-record</c>. Forwarded from the per-connection client — see
    /// <see cref="ISendspinClient.PairingConfigChanged"/>. Subscribe to persist the new
    /// effective configuration; without that, a server-made change reverts on restart.
    /// </summary>
    public event EventHandler<PairingConfigChangedEventArgs>? PairingConfigChanged;

    /// <summary>
    /// Raised when a pairing attempt on any connection is gesture-gated and no pairing
    /// window is open. Forwarded from the per-connection client — see
    /// <see cref="ISendspinClient.PairingGestureRequested"/>. Prompt the operator, then call
    /// <see cref="PairingWindow.Open"/> on the window supplied in
    /// <see cref="SendspinClientOptions.PairingWindow"/>. Without a subscriber, a
    /// <c>static_pin</c> attempt — which is gated every time — never proceeds.
    /// </summary>
    public event EventHandler<PairingGestureRequestedEventArgs>? PairingGestureRequested;

    /// <summary>
    /// Gets the server ID of the server that most recently had playback_state "playing".
    /// Used to break an arbitration tie between two connections that declare no activities.
    /// </summary>
    public string? LastPlayedServerId { get; private set; }

    /// <summary>
    /// Updates the last-played server ID.
    /// Call this when a server transitions to the "playing" state, regardless of connection mode.
    /// </summary>
    /// <param name="serverId">The server ID that is now playing.</param>
    public void SetLastPlayedServerId(string serverId)
    {
        if (string.IsNullOrEmpty(serverId) || serverId == LastPlayedServerId)
            return;

        LastPlayedServerId = serverId;
        TrySaveLastPlayed(serverId);
        _logger.LogInformation("Last played server updated: {ServerId}", serverId);
        LastPlayedServerIdChanged?.Invoke(this, serverId);
    }

    private string? TryLoadLastPlayed()
    {
        if (_lastPlayedServerStore is null)
        {
            return null;
        }

        try
        {
            return _lastPlayedServerStore.Load();
        }
        catch (Exception ex)
        {
            // Deliberately broad, reviewed under #109, and for the same reason as the
            // IStaticDelayStore pair in SendspinClientService: ILastPlayedServerStore is
            // implemented by the embedder over storage the SDK never sees, so no filter can
            // enumerate what it raises. Degrading is right — this runs from the constructor,
            // and the value it produces only breaks an arbitration tie between two servers that
            // declare no activities. Without it that tie falls through to the next rule; with a
            // throw, the host service could not be constructed at all.
            _logger.LogError(ex, "ILastPlayedServerStore.Load() threw; continuing without persisted last-played server");
            return null;
        }
    }

    private void TrySaveLastPlayed(string serverId)
    {
        if (_lastPlayedServerStore is null)
        {
            return;
        }

        try
        {
            _lastPlayedServerStore.Save(serverId);
        }
        catch (Exception ex)
        {
            // Deliberately broad for the same reason as TryLoadLastPlayed (#109). Degrading is
            // clearer still on the save side: the only caller is SetLastPlayedServerId, which
            // has already updated the in-memory value and still has LastPlayedServerIdChanged
            // to raise. Throwing would abandon that notification and propagate out of a
            // GroupStateChanged handler — turning a failed write into a lost playback-state
            // update for the embedder.
            _logger.LogError(ex, "ILastPlayedServerStore.Save({ServerId}) threw; last-played applied in-memory but not persisted", serverId);
        }
    }

    public SendspinHostService(
        ILoggerFactory loggerFactory,
        SendspinClientOptions options,
        ListenerOptions? listenerOptions = null,
        AdvertiserOptions? advertiserOptions = null,
        string? lastPlayedServerId = null,
        ILastPlayedServerStore? lastPlayedServerStore = null,
        ConnectionOptions? connectionOptions = null)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SendspinHostService>();
        _options = options;
        _lastPlayedServerStore = lastPlayedServerStore;

        // Explicit seed wins; otherwise fall back to the store (best-effort).
        LastPlayedServerId = lastPlayedServerId ?? TryLoadLastPlayed();

        var listenOpts = listenerOptions ?? new ListenerOptions();
        var advertiseOpts = advertiserOptions ?? new AdvertiserOptions
        {
            InstanceName = _options.Capabilities.ClientName,
            PlayerName = _options.Capabilities.ClientName,
            Port = listenOpts.Port,
            Path = listenOpts.Path
        };

        _listener = new SendspinListener(
            loggerFactory.CreateLogger<SendspinListener>(),
            listenOpts,
            connectionOptions);

        _advertiserOptions = advertiseOpts;
        _advertiser = new MdnsServiceAdvertiser(
            loggerFactory.CreateLogger<MdnsServiceAdvertiser>(),
            advertiseOpts);

        _listener.ServerConnected += OnServerConnected;
    }

    /// <summary>
    /// Starts the host service (listener + mDNS advertisement).
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting Sendspin host service");

        await _listener.StartAsync(cancellationToken);
        if (_advertiserOptions.Enabled)
        {
            await _advertiser.StartAsync(cancellationToken);
        }
        else
        {
            _logger.LogInformation("mDNS advertising disabled by options");
        }

        _logger.LogInformation("Sendspin host service started - waiting for server connections");
    }

    /// <summary>
    /// Stops the host service.
    /// </summary>
    public async Task StopAsync()
    {
        _logger.LogInformation("Stopping Sendspin host service");

        await _advertiser.StopAsync();

        List<ActiveServerConnection> connectionsToClose;
        lock (_connectionsLock)
        {
            connectionsToClose = _connections.Values.ToList();
            _connections.Clear();
        }

        foreach (var conn in connectionsToClose)
        {
            try
            {
                // Dispose, not just disconnect: DisposeAsync sends the same 'shutdown' goodbye
                // (GoodbyeReasons.Shutdown) but also reaches IncomingConnection.DisposeAsync,
                // which releases the socket/TcpClient/CTS after the goodbye goes out (#143).
                await conn.Client.DisposeAsync();
            }
            catch (Exception ex)
            {
                // Deliberately broad, reviewed under #109. DisposeAsync reaches
                // IAudioPipeline.StopAsync and the source pipeline's capture device, both
                // embedder-supplied, so the failure set is open — a driver that throws on
                // teardown is exactly the case this must survive. And this is a loop on the
                // shutdown path: an escape would strand the remaining connections undisposed
                // and skip _listener.StopAsync() below, leaving the socket accepting
                // connections after the caller was told the host had stopped.
                _logger.LogWarning(ex, "Error disconnecting from {ServerId}", conn.ServerId);
            }
        }

        await _listener.StopAsync();

        _logger.LogInformation("Sendspin host service stopped");
    }

    /// <summary>
    /// Stops mDNS advertising without stopping the listener.
    /// Call this when manually connecting to a server to prevent
    /// other servers from trying to connect to this client.
    /// </summary>
    public async Task StopAdvertisingAsync()
    {
        if (!_advertiser.IsAdvertising)
            return;

        _logger.LogInformation("Stopping mDNS advertisement (manual connection active)");
        await _advertiser.StopAsync();
    }

    /// <summary>
    /// Resumes mDNS advertising after it was stopped.
    /// Call this when disconnecting from a manually connected server
    /// to allow servers to discover this client again.
    /// </summary>
    public async Task StartAdvertisingAsync(CancellationToken cancellationToken = default)
    {
        if (_advertiser.IsAdvertising)
            return;

        if (!_listener.IsListening)
        {
            _logger.LogWarning("Cannot start advertising - listener is not running");
            return;
        }

        _logger.LogInformation("Resuming mDNS advertisement");
        if (_advertiserOptions.Enabled)
        {
            await _advertiser.StartAsync(cancellationToken);
        }
        else
        {
            _logger.LogInformation("mDNS advertising disabled by options");
        }
    }

    /// <summary>
    /// Disconnects all currently connected servers.
    /// Use when switching to a client-initiated connection to ensure
    /// only one connection is using the audio pipeline at a time.
    /// </summary>
    /// <param name="reason">
    /// The <c>client/goodbye</c> reason to send, from <see cref="GoodbyeReasons"/>. Defaults to
    /// <see cref="GoodbyeReasons.AnotherServer"/>: the documented use is leaving these servers
    /// for a connection this client is about to dial, and the spec makes that reason mandatory
    /// for a client that leaves one server for another (messaging.md:426). Pass
    /// <see cref="GoodbyeReasons.UserRequest"/> instead when the user asked to go offline rather
    /// than to move.
    /// </param>
    public async Task DisconnectAllAsync(string reason = GoodbyeReasons.AnotherServer)
    {
        List<ActiveServerConnection> connectionsToClose;
        lock (_connectionsLock)
        {
            connectionsToClose = _connections.Values.ToList();
            _connections.Clear();
        }

        foreach (var conn in connectionsToClose)
        {
            try
            {
                _logger.LogInformation("Disconnecting server {ServerId}: {Reason}", conn.ServerId, reason);
                await conn.Client.DisconnectAsync(reason);
            }
            catch (Exception ex)
            {
                // Deliberately broad, reviewed under #109. IncomingConnection.DisconnectAsync
                // already swallows the goodbye and close itself, so what actually surfaces here
                // is its SetState — which dispatches StateChanged synchronously into the
                // client's handler and on into the embedder's ConnectionStateChanged
                // subscribers. Subscriber code has no enumerable failure set. The loop argument
                // from StopAsync applies too: _connections was cleared before this ran, so an
                // escape leaves the untouched remainder connected with nothing tracking them.
                _logger.LogWarning(ex, "Error disconnecting from {ServerId}", conn.ServerId);
            }
        }
    }

    /// <summary>
    /// Returns this client's pairing token, generating and persisting a Pairing PSK if none
    /// is stored. Works before and between connections — in host mode the QR code has to be
    /// shown before a server dials in, when no per-connection client exists yet. Same
    /// contract as <see cref="ISendspinClient.EnsurePairingPsk"/>: idempotent until the PSK
    /// is replaced, and a client over the same store and identity returns the same token.
    /// </summary>
    /// <returns>The pairing token, for the UI to render as a QR code or display for pasting.</returns>
    /// <exception cref="InvalidOperationException">
    /// No pairing record store is configured, so a generated PSK could not be persisted.
    /// </exception>
    public string EnsurePairingPsk()
    {
        lock (_pairingStoreLock)
        {
            return PairingPskOperations.Ensure(_options.PairingRecordStore, _options.Identity);
        }
    }

    /// <summary>
    /// Replaces this client's Pairing PSK with a freshly generated one and returns the new
    /// token. Same contract as <see cref="ISendspinClient.RotatePairingPsk"/>: any token
    /// previously handed out stops being valid, and this exists to be called only by
    /// deliberate operator action.
    /// </summary>
    /// <returns>The new pairing token; any token previously handed out stops being valid.</returns>
    /// <exception cref="InvalidOperationException">No pairing record store is configured.</exception>
    public string RotatePairingPsk()
    {
        lock (_pairingStoreLock)
        {
            return PairingPskOperations.Rotate(_options.PairingRecordStore, _options.Identity);
        }
    }

    /// <summary>
    /// Builds the per-connection options handed to each <see cref="SendspinClientService"/>.
    /// </summary>
    /// <remarks>
    /// The only per-connection difference is the clock synchronizer: when none was
    /// configured, each connection gets its own <see cref="KalmanClockSynchronizer"/> with
    /// its own logger, which cannot be mutated onto the stored init-only instance. When one
    /// <em>was</em> configured it is shared deliberately, and the stored options are handed
    /// back untouched.
    /// </remarks>
    internal SendspinClientOptions BuildClientOptions()
    {
        return _options.ClockSynchronizer is null
            ? _options with
            {
                ClockSynchronizer = new KalmanClockSynchronizer(_loggerFactory.CreateLogger<KalmanClockSynchronizer>()),
            }
            : _options;
    }

    private void OnServerConnected(object? sender, WebSocketClientConnection webSocket)
    {
        HandleServerConnectedAsync(webSocket).SafeFireAndForget(_logger);
    }

    private async Task HandleServerConnectedAsync(WebSocketClientConnection webSocket)
    {
        string? connectionId = null;
        SendspinClientService? client = null;
        var registered = false;

        try
        {
            // Guard against connections arriving after the listener has been stopped
            if (!_listener.IsListening)
            {
                _logger.LogDebug("Ignoring connection — listener is stopping");
                return;
            }
            connectionId = Guid.NewGuid().ToString("N")[..8];
            _logger.LogInformation("New server connection: {ConnectionId}", connectionId);
            // Each incoming connection gets its own Noise framing — the per-connection
            // crypto state cannot be shared. The server (dialer) is the Noise initiator
            // per spec; our side responds.
            // The resolver reads this connection's live pairing config through `client`, which
            // is assigned a few lines down — before StartAsync, so before any handshake can
            // consult it. Until then the configured value is the one the client starts from.
            var framing = new Connection.Noise.NoiseWireFraming(
                _options.Identity,
                _options.PairingRecordStore is null
                    ? null
                    : new Connection.Noise.RecordPskResolver(
                        _options.PairingRecordStore,
                        () => client?.IsPairingPskEnabled ?? _options.Capabilities.PairingPskEnabled),
                _options.Suite);

            var connection = new IncomingConnection(
                _loggerFactory.CreateLogger<IncomingConnection>(),
                webSocket,
                framing);

            var clientOptions = BuildClientOptions();

            client = new SendspinClientService(
                _loggerFactory.CreateLogger<SendspinClientService>(),
                connection,
                framing,
                clientOptions);

            client.PairingCompleted += (s, serverId) => PairingCompleted?.Invoke(this, serverId);
            client.PairingConfigChanged += (s, e) => PairingConfigChanged?.Invoke(this, e);
            client.PairingGestureRequested += (s, e) => PairingGestureRequested?.Invoke(this, e);

            client.GroupStateChanged += (s, g) =>
            {
                // Track which server last had playback_state "playing".
                if (g.PlaybackState == PlaybackState.Playing && client.ServerId is not null)
                {
                    SetLastPlayedServerId(client.ServerId);
                }

                GroupStateChanged?.Invoke(this, g);
            };
            client.PlayerStateChanged += (s, p) => PlayerStateChanged?.Invoke(this, p);
            client.ArtworkReceived += (s, e) => ArtworkReceived?.Invoke(this, e);
            client.ArtworkCleared += (s, e) => ArtworkCleared?.Invoke(this, e);
            client.ColorChanged += (s, e) => ColorChanged?.Invoke(this, e);
            client.VisualizationReceived += (s, e) => VisualizationReceived?.Invoke(this, e);
            client.ServerHelloReceived += (s, payload) => ServerHelloReceived?.Invoke(this, payload);
            client.StreamStartReceived += (s, payload) => StreamStartReceived?.Invoke(this, payload);
            client.StreamEndReceived += (s, payload) => StreamEndReceived?.Invoke(this, payload);
            client.StreamClearReceived += (s, payload) => StreamClearReceived?.Invoke(this, payload);

            await connection.StartAsync();

            // The handshake is server-driven (client/init went out in StartAsync; the
            // hello/activate exchange is handled by the client service). The connection
            // is provisional until its first server/activate; the spec's provisional
            // window is 30 seconds, after which it is dropped.
            if (!await WaitForHandshakeAsync(client, connection, connectionId, timeoutSeconds: 30))
            {
                return;
            }

            // Handshake complete - now arbitrate whether to accept this server
            var serverId = client.ServerId ?? connectionId;

            // Perform multi-server arbitration: determine whether the new server
            // should replace the existing one or be rejected
            if (!await ArbitrateConnectionAsync(client, connection, serverId))
            {
                // New server lost arbitration - it has already been disconnected
                return;
            }

            // Subscribe to connection state AFTER handshake so we use the correct serverId
            client.ConnectionStateChanged += (s, e) => OnClientConnectionStateChanged(serverId, e);
            var activeConnection = new ActiveServerConnection
            {
                ServerId = serverId,
                Client = client,
                Connection = connection,
                ConnectedAt = DateTime.UtcNow
            };

            lock (_connectionsLock)
            {
                _connections[serverId] = activeConnection;
            }

            registered = true;

            _logger.LogInformation("Server connected: {ServerId} ({ServerName})",
                serverId, client.ServerName);

            ServerConnected?.Invoke(this, new ConnectedServerInfo
            {
                ServerId = serverId,
                ServerName = client.ServerName ?? serverId,
                ConnectedAt = activeConnection.ConnectedAt,
                ClockSyncStatus = client.ClockSyncStatus
            });
        }
        catch (Exception ex)
        {
            // Deliberately broad, reviewed under #109. This guards the whole connection-setup
            // body, not one operation: Noise framing construction, the handshake wait,
            // arbitration, and — inside the same try — ServerConnected?.Invoke and the event
            // subscriptions above it, which run embedder code. There is no filter that covers
            // that span, and a per-connection setup failure must not escape into the listener's
            // accept path and take the host down with it. The finally below is what keeps this
            // from leaking: an unregistered client is disposed whichever way we leave.
            _logger.LogError(ex, "Error handling server connection {ConnectionId}", connectionId ?? "unknown");
        }
        finally
        {
            // If the client was created but never registered in _connections,
            // dispose it to prevent leaking the WebSocket, semaphore, and CTS.
            // No catch, deliberately: dispose parses no peer input, so there is no
            // expected failure type to name — a throw here is a bug in our own teardown
            // and propagates to the fire-and-forget boundary in OnServerConnected, where
            // it is logged as the error it is rather than swallowed (#88 item 2).
            if (client is not null && !registered)
            {
                await client.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Waits for the handshake to complete with timeout.
    /// </summary>
    /// <param name="client">The client service to monitor.</param>
    /// <param name="connection">The connection to drop on timeout.</param>
    /// <param name="connectionId">Connection ID for logging.</param>
    /// <param name="timeoutSeconds">Handshake timeout in seconds (default: 10).</param>
    /// <returns>True if handshake completed successfully, false otherwise.</returns>
    internal async Task<bool> WaitForHandshakeAsync(
        SendspinClientService client,
        IncomingConnection connection,
        string connectionId,
        int timeoutSeconds = 10)
    {
        var handshakeComplete = new TaskCompletionSource<bool>();

        void OnStateChanged(object? s, ConnectionStateChangedEventArgs e)
        {
            if (e.NewState == ConnectionState.Connected)
            {
                handshakeComplete.TrySetResult(true);
            }
            else if (e.NewState == ConnectionState.Disconnected)
            {
                handshakeComplete.TrySetResult(false);
            }
        }

        client.ConnectionStateChanged += OnStateChanged;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        cts.Token.Register(() => handshakeComplete.TrySetCanceled());

        try
        {
            var success = await handshakeComplete.Task;
            if (!success)
            {
                _logger.LogWarning("Handshake failed for connection {ConnectionId}", connectionId);
            }
            return success;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Handshake timeout for connection {ConnectionId}", connectionId);

            // The spec says a provisional connection that has not activated within the window
            // "is dropped" (connection.md:40) and names no goodbye reason for it, so drop it
            // silently. The "handshake_timeout" this used to send is outside client/goodbye's
            // closed set (messaging.md:426), which a server reads as no goodbye at all — the
            // signal that invites it to reconnect immediately and loop.
            await connection.CloseWithoutGoodbyeAsync("handshake_timeout");
            return false;
        }
        finally
        {
            client.ConnectionStateChanged -= OnStateChanged;
        }
    }

    /// <summary>
    /// A connection's arbitration priority, from its declared server/activate activities.
    /// </summary>
    private static ConnectionPriority PriorityOf(SendspinClientService client)
        => client.LastServerActivate is { } activate
            ? ServerArbitration.FromActivities(activate.ActivitiesList)
            : ConnectionPriority.Empty;

    /// <summary>
    /// Arbitrates whether a newly handshaked server should become the active connection
    /// (only one server is active at a time). The priority rules live in and are documented by
    /// <see cref="ServerArbitration.Decide"/>; this method applies that decision by disconnecting
    /// the losing connection with the returned client/goodbye reason.
    /// </summary>
    /// <param name="newClient">The new client that just completed handshake.</param>
    /// <param name="newConnection">The new connection to disconnect if rejected.</param>
    /// <param name="newServerId">The server ID of the new connection.</param>
    /// <returns>True if the new server is accepted, false if rejected.</returns>
    private async Task<bool> ArbitrateConnectionAsync(
        SendspinClientService newClient,
        IncomingConnection newConnection,
        string newServerId)
    {
        ActiveServerConnection? existingConnection;
        lock (_connectionsLock)
        {
            // There is at most one active connection.
            existingConnection = _connections.Values.FirstOrDefault();
        }

        var result = ServerArbitration.Decide(
            newServerId,
            PriorityOf(newClient),
            existingConnection?.ServerId,
            existingConnection is null ? ConnectionPriority.Empty : PriorityOf(existingConnection.Client),
            LastPlayedServerId);

        _logger.LogInformation(
            "Arbitration: {Rationale}. New={NewServerId}, Existing={ExistingServerId}",
            result.Rationale,
            newServerId,
            existingConnection?.ServerId ?? "(none)");

        if (result.AcceptNew)
        {
            if (existingConnection is not null)
            {
                // LoserReason is non-null whenever there is an existing connection to drop.
                await DisconnectExistingAsync(existingConnection, result.LoserReason!, result.LoserFarewell);
            }

            return true;
        }

        // New server rejected (an existing connection always exists on this path).
        _logger.LogInformation(
            "Arbitration: Rejecting {NewServerId}, sending {Farewell} {Reason}",
            newServerId,
            result.LoserFarewell,
            result.LoserReason);
        try
        {
            // A rejected incoming pairing handshake is told pair/abort rather than
            // client/goodbye (connection.md); either way the connection closes behind it.
            await (result.LoserFarewell == ArbitrationFarewell.PairAbort
                ? newConnection.DisconnectWithPairAbortAsync(result.LoserReason!)
                : newConnection.DisconnectAsync(result.LoserReason!));
        }
        catch (Exception ex)
        {
            // Deliberately broad, reviewed under #109. Same surface as DisconnectAllAsync's
            // catch — the goodbye is already self-guarded, so what reaches here is state-change
            // dispatch into subscriber code. The decision matters more than the delivery: this
            // connection has lost arbitration and is going away regardless, and the caller
            // reads the returned false to skip registering it. An escape would lose that false
            // to HandleServerConnectedAsync's outer guard, which reports a rejected connection
            // as "Error handling server connection" — the wrong diagnosis for a working
            // arbitration whose goodbye happened not to land.
            _logger.LogWarning(ex, "Error disconnecting rejected server {ServerId}", newServerId);
        }

        return false;
    }

    /// <summary>
    /// Disconnects an existing active server connection during arbitration.
    /// Removes the connection from the tracking dictionary and sends its farewell message.
    /// </summary>
    /// <param name="existing">The existing connection to disconnect.</param>
    /// <param name="reason">The reason to send.</param>
    /// <param name="farewell">Which message carries it.</param>
    private async Task DisconnectExistingAsync(
        ActiveServerConnection existing, string reason, ArbitrationFarewell farewell)
    {
        lock (_connectionsLock)
        {
            _connections.Remove(existing.ServerId);
        }

        _logger.LogInformation(
            "Arbitration: Disconnecting existing server {ServerId} with reason {Reason}",
            existing.ServerId, reason);

        try
        {
            // Disconnect first so the arbitration-specific reason goes out on the wire — the
            // arbitration tests assert on it. Dispose afterward to actually release the
            // socket/TcpClient/receive-loop CTS (#143): by then _isOpen is already false, so
            // DisposeAsync's own DisconnectAsync(GoodbyeReasons.Shutdown) short-circuits without
            // sending a second goodbye that would overwrite this one. Disposing the connection
            // rather than the whole client keeps this to the socket only — arbitration eviction
            // happens on every server reconnect, so widening this to also tear down the audio/
            // source pipelines (as Client.DisposeAsync would) is a separate change, not asked for
            // here.
            //
            // A pairing farewell goes out on the connection rather than through the client: the
            // client's only farewell is client/goodbye. Its per-disconnect bookkeeping still
            // runs, off the state change either path raises.
            if (farewell == ArbitrationFarewell.PairAbort)
            {
                await existing.Connection.DisconnectWithPairAbortAsync(reason);
            }
            else
            {
                await existing.Client.DisconnectAsync(reason);
            }

            await existing.Connection.DisposeAsync();
        }
        catch (Exception ex)
        {
            // Deliberately broad, reviewed under #109, and the strongest case of the four
            // teardown catches. _connections.Remove already ran above, so by the time we get
            // here the eviction is a fact; the only thing left is to tell the embedder, on the
            // line below. An escape would skip ServerDisconnected entirely, leaving the
            // application still showing a server the host service has already forgotten — a
            // worse outcome than a goodbye that did not make it onto a socket that is closing
            // anyway. The surface is open regardless: DisposeAsync releases the socket, and the
            // disconnect dispatches state changes into subscriber code.
            _logger.LogWarning(ex, "Error disconnecting existing server {ServerId} during arbitration",
                existing.ServerId);
        }

        ServerDisconnected?.Invoke(this, existing.ServerId);
    }

    private void OnClientConnectionStateChanged(string connectionId, ConnectionStateChangedEventArgs e)
    {
        if (e.NewState == ConnectionState.Disconnected)
        {
            lock (_connectionsLock)
            {
                var entry = _connections.FirstOrDefault(c => c.Value.ServerId == connectionId);
                // FirstOrDefault returns default(KeyValuePair) when not found, which has Key=null.
                // This check works because dictionary keys are never null (serverId falls back to GUID).
                if (entry.Key is not null)
                {
                    _connections.Remove(entry.Key);
                    _logger.LogInformation("Server disconnected: {ServerId}", entry.Key);
                    ServerDisconnected?.Invoke(this, entry.Key);
                }
            }
        }
    }

    /// <summary>
    /// Sends a command to a specific server or all connected servers.
    /// </summary>
    public async Task SendCommandAsync(string command, Dictionary<string, object>? parameters = null, string? serverId = null)
    {
        List<SendspinClientService> clients;
        lock (_connectionsLock)
        {
            if (serverId != null)
            {
                if (_connections.TryGetValue(serverId, out var conn))
                {
                    clients = new List<SendspinClientService> { conn.Client };
                }
                else
                {
                    throw new InvalidOperationException($"Server {serverId} not connected");
                }
            }
            else
            {
                clients = _connections.Values.Select(c => c.Client).ToList();
            }
        }

        foreach (var client in clients)
        {
            await client.SendCommandAsync(command, parameters);
        }
    }

    /// <summary>
    /// Sends the current player state (volume, muted) to a specific server or all connected servers.
    /// </summary>
    /// <param name="volume">Current volume level (0-100).</param>
    /// <param name="muted">Current mute state.</param>
    /// <param name="staticDelayMs">
    /// A new static delay in milliseconds to apply, persist, and report, or null (the default)
    /// to leave the current delay untouched and simply report it. Same semantics as
    /// <see cref="ISendspinClient.SendPlayerStateAsync"/>: a supplied value is written to the
    /// clock synchronizer and to <see cref="SendspinClientOptions.StaticDelayStore"/>, so omit
    /// it for an ordinary volume or mute change.
    /// </param>
    /// <param name="serverId">Target server ID, or null for all servers.</param>
    public async Task SendPlayerStateAsync(int volume, bool muted, double? staticDelayMs = null, string? serverId = null)
    {
        List<SendspinClientService> clients;
        lock (_connectionsLock)
        {
            if (serverId != null)
            {
                if (_connections.TryGetValue(serverId, out var conn))
                {
                    clients = new List<SendspinClientService> { conn.Client };
                }
                else
                {
                    throw new InvalidOperationException($"Server {serverId} not connected");
                }
            }
            else
            {
                clients = _connections.Values.Select(c => c.Client).ToList();
            }
        }

        foreach (var client in clients)
        {
            await client.SendPlayerStateAsync(volume, muted, staticDelayMs);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _listener.DisposeAsync();
        await _advertiser.DisposeAsync();
    }

    private class ActiveServerConnection
    {
        required public string ServerId { get; init; }
        required public SendspinClientService Client { get; init; }
        required public IncomingConnection Connection { get; init; }
        public DateTime ConnectedAt { get; init; }
    }
}

/// <summary>
/// Information about a connected Sendspin server.
/// </summary>
public record ConnectedServerInfo
{
    required public string ServerId { get; init; }
    required public string ServerName { get; init; }
    public DateTime ConnectedAt { get; init; }
    public ClockSyncStatus? ClockSyncStatus { get; init; }
}
