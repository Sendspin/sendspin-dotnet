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
    private readonly ClientCapabilities _capabilities;
    private readonly IAudioPipeline? _audioPipeline;
    private readonly IClockSynchronizer? _clockSynchronizer;
    private readonly ILastPlayedServerStore? _lastPlayedServerStore;

    private readonly Dictionary<string, ActiveServerConnection> _connections = new();
    private readonly object _connectionsLock = new();

    /// <summary>
    /// Whether the host is running (listening and advertising).
    /// </summary>
    public bool IsRunning => _listener.IsListening && _advertiser.IsAdvertising;

    /// <summary>
    /// Whether the service is currently being advertised via mDNS.
    /// </summary>
    public bool IsAdvertising => _advertiser.IsAdvertising;

    /// <summary>
    /// The client ID being advertised.
    /// </summary>
    public string ClientId => _advertiser.ClientId;

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
    /// Raised when any connected client receives a <c>server/hello</c>.
    /// Fires once per server handshake (including reconnects). Multiple concurrent
    /// connections will each raise this event independently — consumers that care
    /// about per-server state should key off <see cref="ServerHelloPayload.ServerId"/>.
    /// </summary>
    public event EventHandler<ServerHelloPayload>? ServerHelloReceived;

    /// <summary>
    /// Raised when any connected client receives a <c>stream/start</c>.
    /// Fires once per stream/start frame (audio, artwork, or both).
    /// </summary>
    public event EventHandler<StreamStartPayload>? StreamStartReceived;

    /// <summary>
    /// Raised when the last-played server ID changes.
    /// Consumers should persist this value so it survives app restarts.
    /// </summary>
    public event EventHandler<string>? LastPlayedServerIdChanged;

    /// <summary>
    /// Gets the server ID of the server that most recently had playback_state "playing".
    /// Used for tie-breaking when multiple servers with the same connection_reason try to connect.
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
            _logger.LogError(ex, "ILastPlayedServerStore.Save({ServerId}) threw; last-played applied in-memory but not persisted", serverId);
        }
    }

    public SendspinHostService(
        ILoggerFactory loggerFactory,
        ClientCapabilities? capabilities = null,
        ListenerOptions? listenerOptions = null,
        AdvertiserOptions? advertiserOptions = null,
        IAudioPipeline? audioPipeline = null,
        IClockSynchronizer? clockSynchronizer = null,
        string? lastPlayedServerId = null,
        ILastPlayedServerStore? lastPlayedServerStore = null)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SendspinHostService>();
        _capabilities = capabilities ?? new ClientCapabilities();
        _audioPipeline = audioPipeline;
        _clockSynchronizer = clockSynchronizer;
        _lastPlayedServerStore = lastPlayedServerStore;

        // Explicit seed wins; otherwise fall back to the store (best-effort).
        LastPlayedServerId = lastPlayedServerId ?? TryLoadLastPlayed();

        var listenOpts = listenerOptions ?? new ListenerOptions();
        var advertiseOpts = advertiserOptions ?? new AdvertiserOptions
        {
            ClientId = _capabilities.ClientId,
            PlayerName = _capabilities.ClientName,
            Port = listenOpts.Port,
            Path = listenOpts.Path
        };

        _listener = new SendspinListener(
            loggerFactory.CreateLogger<SendspinListener>(),
            listenOpts);

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
        await _advertiser.StartAsync(cancellationToken);

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
                await conn.Client.DisconnectAsync("host_stopping");
            }
            catch (Exception ex)
            {
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
        await _advertiser.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Disconnects all currently connected servers.
    /// Use when switching to a client-initiated connection to ensure
    /// only one connection is using the audio pipeline at a time.
    /// </summary>
    public async Task DisconnectAllAsync(string reason = "switching_connection_mode")
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
                _logger.LogWarning(ex, "Error disconnecting from {ServerId}", conn.ServerId);
            }
        }
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
            var connection = new IncomingConnection(
                _loggerFactory.CreateLogger<IncomingConnection>(),
                webSocket);

            // Use the shared clock synchronizer if provided, otherwise create a per-connection one.
            var clockSync = _clockSynchronizer
                ?? new KalmanClockSynchronizer(_loggerFactory.CreateLogger<KalmanClockSynchronizer>());
            client = new SendspinClientService(
                _loggerFactory.CreateLogger<SendspinClientService>(),
                connection,
                clockSync,
                _capabilities,
                _audioPipeline);

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

            await connection.StartAsync();

            // client/hello is always sent first per the protocol.
            await SendClientHelloAsync(client, connection);

            if (!await WaitForHandshakeAsync(client, connection, connectionId))
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
            _logger.LogError(ex, "Error handling server connection {ConnectionId}", connectionId ?? "unknown");
        }
        finally
        {
            // If the client was created but never registered in _connections,
            // dispose it to prevent leaking the WebSocket, semaphore, and CTS.
            if (client is not null && !registered)
            {
                try
                {
                    await client.DisposeAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error disposing unregistered client {ConnectionId}", connectionId);
                }
            }
        }
    }

    private async Task SendClientHelloAsync(SendspinClientService client, IncomingConnection connection)
    {
        if (_capabilities.ArtworkChannels.Count > 4)
        {
            _logger.LogWarning("ArtworkChannels has {Count} entries; only the first 4 are advertised (spec maximum).",
                _capabilities.ArtworkChannels.Count);
        }

        // Use audio formats from capabilities (order matters - server picks first supported)
        var hello = ClientHelloMessage.Create(
            clientId: _capabilities.ClientId,
            name: _capabilities.ClientName,
            supportedRoles: _capabilities.Roles,
            playerSupport: new PlayerSupport
            {
                SupportedFormats = _capabilities.AudioFormats
                    .Select(f => new AudioFormatSpec
                    {
                        Codec = f.Codec,
                        Channels = f.Channels,
                        SampleRate = f.SampleRate,
                        BitDepth = f.BitDepth ?? 16,
                    })
                    .ToList(),
                BufferCapacity = _capabilities.BufferCapacity,
                SupportedCommands = new List<string> { "volume", "mute" }
            },
            artworkSupport: new ArtworkSupport
            {
                // Spec allows 1-4 channels (array index = channel number).
                Channels = _capabilities.ArtworkChannels.Take(4).ToList()
            },
            deviceInfo: new DeviceInfo
            {
                ProductName = _capabilities.ProductName,
                Manufacturer = _capabilities.Manufacturer,
                SoftwareVersion = _capabilities.SoftwareVersion,
                MacAddress = _capabilities.MacAddress
            },
            visualizerSupport: _capabilities.VisualizerSupport
        );

        var helloJson = MessageSerializer.Serialize(hello);
        _logger.LogInformation("Sending client/hello:\n{Json}", helloJson);
        await connection.SendMessageAsync(hello);
    }

    /// <summary>
    /// Waits for the handshake to complete with timeout.
    /// </summary>
    /// <param name="client">The client service to monitor.</param>
    /// <param name="connection">The connection to disconnect on timeout.</param>
    /// <param name="connectionId">Connection ID for logging.</param>
    /// <param name="timeoutSeconds">Handshake timeout in seconds (default: 10).</param>
    /// <returns>True if handshake completed successfully, false otherwise.</returns>
    private async Task<bool> WaitForHandshakeAsync(
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
            await connection.DisconnectAsync("handshake_timeout");
            return false;
        }
        finally
        {
            client.ConnectionStateChanged -= OnStateChanged;
        }
    }

    /// <summary>
    /// Arbitrates whether a newly handshaked server should become the active connection.
    /// Only one server can be active at a time. Priority rules:
    /// 1. "playback" connection_reason beats "discovery"
    /// 2. If tied, the last-played server wins
    /// 3. If still tied (or LastPlayedServerId is null), the existing server wins
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
            newClient.ConnectionReason,
            existingConnection?.ServerId,
            existingConnection?.Client.ConnectionReason,
            LastPlayedServerId);

        _logger.LogInformation(
            "Arbitration: {Rationale}. New={NewServerId} (reason={NewReason}), Existing={ExistingServerId}",
            result.Rationale,
            newServerId,
            newClient.ConnectionReason ?? "discovery",
            existingConnection?.ServerId ?? "(none)");

        if (result.AcceptNew)
        {
            if (existingConnection is not null)
            {
                // LoserGoodbyeReason is non-null whenever there is an existing connection to drop.
                await DisconnectExistingAsync(existingConnection, result.LoserGoodbyeReason!);
            }

            return true;
        }

        // New server rejected (an existing connection always exists on this path).
        _logger.LogInformation("Arbitration: Rejecting {NewServerId}, sending goodbye", newServerId);
        try
        {
            await newConnection.DisconnectAsync(result.LoserGoodbyeReason!);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disconnecting rejected server {ServerId}", newServerId);
        }

        return false;
    }

    /// <summary>
    /// Disconnects an existing active server connection during arbitration.
    /// Removes the connection from the tracking dictionary and sends a goodbye message.
    /// </summary>
    /// <param name="existing">The existing connection to disconnect.</param>
    /// <param name="reason">The goodbye reason to send.</param>
    private async Task DisconnectExistingAsync(ActiveServerConnection existing, string reason)
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
            await existing.Client.DisconnectAsync(reason);
        }
        catch (Exception ex)
        {
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
    /// <param name="staticDelayMs">Static delay in milliseconds for group sync calibration.</param>
    /// <param name="serverId">Target server ID, or null for all servers.</param>
    public async Task SendPlayerStateAsync(int volume, bool muted, double staticDelayMs = 0.0, string? serverId = null)
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
