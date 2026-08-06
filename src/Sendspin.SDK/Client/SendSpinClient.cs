using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Connection.Noise.Pairing;
using Sendspin.SDK.Audio.Source;
using Sendspin.SDK.Extensions;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Client;

/// <summary>
/// Main Sendspin client that orchestrates connection, handshake, and message handling.
/// </summary>
public sealed class SendspinClientService : ISendspinClient, IDisposable
{
    private readonly ILogger<SendspinClientService> _logger;
    private readonly ISendspinConnection _connection;
    private readonly ClientCapabilities _capabilities;
    private readonly IClockSynchronizer _clockSynchronizer;
    private readonly IAudioPipeline? _audioPipeline;
    private readonly IStaticDelayStore? _staticDelayStore;
    private readonly INoiseSessionInfo _session;
    private bool _activateReceived;
    private readonly SourceStreamPipeline? _sourcePipeline;
    private readonly IAudioCaptureDevice? _captureDevice;
    private readonly ISourceAudioEncoderFactory? _sourceEncoderFactory;
    private readonly IPairingRecordStore? _pairingStore;

    // Serializes this client's record-store accesses. IPairingRecordStore promises that
    // "the SDK serializes access"; before EnsurePairingPsk/RotatePairingPsk every mutation
    // ran on the receive path, and now app threads mutate too. Never held across an await.
    // Boundary: RecordPskResolver.Resolve also reads the store, from the framing inbound
    // path — a separate public object this client-private lock cannot reach — so an
    // app-thread call can still race an in-flight re-handshake's psk_id lookup.
    private readonly object _pairingStoreLock = new();
    private readonly SendspinIdentity _identity;
    private bool _markedPskUsed;
    private byte[]? _pendingPairingPsk;
    private readonly IPinLockoutStore? _pinLockoutStore;
    private readonly Func<string, CancellationToken, ValueTask>? _presentPinAsync;
    private PinPairingState? _pinState;
    private int _pairingCounter;
    private byte[]? _lastHandshakeHash;

    // _handshakeTcs is published by the handshake waiter and completed by the connection's
    // state-changed handler, which runs on the receive loop's thread. _handshakeLock covers
    // both so a permanent failure that lands before the waiter publishes its TCS is still
    // seen by it — see SendHandshakeAsync and CompleteHandshakeWait.
    private readonly object _handshakeLock = new();
    private TaskCompletionSource<bool>? _handshakeTcs;
    private SendspinHandshakeException? _handshakeFailure;
    private GroupState? _currentGroup;
    private PlayerState _playerState;
    private CancellationTokenSource? _timeSyncCts;
    private bool _disposed;

    // Whether we have reported client/state: 'error' to the server and are awaiting recovery.
    // Guards against duplicate error reports and gates the synchronized report on actual recovery.
    private bool _clientErrorReported;

    // Player timing parameters reported in client/state. Seeded from capabilities and updatable
    // at runtime via UpdateTimingAsync (e.g. after measuring lead time or a link-type change).
    private int _requiredLeadTimeMs;
    private int _minBufferMs;

    // The effective unpaired-access setting: seeded from capabilities at construction and
    // updated when a server changes it via management/set-pairing-config. Held here rather
    // than written into the app-owned capabilities object, which the SDK does not mutate.
    // PairingConfigChanged tells the app to persist the new value.
    private bool _unpairedAccessEnabled;

    // Bounds for any value written to the clock synchronizer's static delay. The GroupSync offset
    // path allows negatives (schedule later), so this is wider than the set_static_delay spec range.
    private const double MinStaticDelayMs = -5000.0;
    private const double MaxStaticDelayMs = 5000.0;

    /// <summary>
    /// Queue for audio chunks that arrive before pipeline is ready.
    /// Prevents chunk loss during the ~50ms decoder/buffer initialization.
    /// </summary>
    private readonly ConcurrentQueue<AudioChunk> _earlyChunkQueue = new();

    /// <summary>
    /// Maximum chunks to queue before pipeline ready (~2 seconds of audio at typical rates).
    /// </summary>
    private const int MaxEarlyChunks = 100;

    // 8 probes lets us pick the lowest-RTT sample and still complete a burst quickly.
    private const int BurstSize = 8;

    // 50 ms between probes — short enough for fast bursts, long enough to avoid TCP queuing.
    private const int BurstIntervalMs = 50;

    /// <summary>
    /// Per-probe timeout for time sync responses.
    /// Matches the JS reference player and aborts a burst if any probe stalls.
    /// </summary>
    private const int ProbeTimeoutMs = 2000;

    // Sequential burst tracking: at most one probe is in flight at any time.
    // _burstInFlight is the awaiter for that probe's reply; _burstInFlightT1
    // is the T1 used to match the incoming server/time response.
    private readonly object _burstLock = new();
    private TaskCompletionSource<TimeSyncSample>? _burstInFlight;
    private long _burstInFlightT1;

    // Guards the burst loop against concurrent invocation. The continuous time-sync
    // loop and the smart-sync trigger in HandleStreamStart both call
    // SendTimeSyncBurstAsync; without this flag, two overlapping bursts would
    // overwrite each other's _burstInFlight slot and both abort.
    // Matches the timeSyncBurstActive guard in the JS reference player.
    private int _burstRunning;

    private readonly record struct TimeSyncSample(long T1, long T2, long T3, long T4, double Rtt);

    public ConnectionState ConnectionState => _connection.State;
    public string? ServerId { get; private set; }
    public string? ServerName { get; private set; }

    /// <summary>The Noise session this client was constructed with (test-only introspection).</summary>
    internal INoiseSessionInfo Session => _session;

    /// <summary>
    /// The connection this client was constructed with (test-only introspection). Named to
    /// avoid shadowing the <c>Sendspin.SDK.Connection</c> namespace used elsewhere in this
    /// file (e.g. <c>Connection.Noise.SendspinIdentity</c>).
    /// </summary>
    internal ISendspinConnection ClientConnection => _connection;

    /// <inheritdoc />
    public ServerHelloPayload? LastServerHello { get; private set; }

    /// <summary>
    /// The most recent <em>accepted</em> server/activate payload (encrypted protocol), or
    /// null before the initial activation. An activate the admissibility table refused is
    /// never recorded here, because the activities it declared must not grant anything.
    /// Roles in <see cref="ServerActivatePayload.ActiveRoles"/> are also mirrored into
    /// <see cref="LastServerHello"/> for legacy consumers.
    /// </summary>
    public ServerActivatePayload? LastServerActivate { get; private set; }

    /// <inheritdoc />
    public StreamStartPayload? LastStreamStart { get; private set; }

    public GroupState? CurrentGroup => _currentGroup;
    public PlayerState CurrentPlayerState => _playerState;
    public ClockSyncStatus? ClockSyncStatus => _clockSynchronizer.GetStatus();
    public bool IsClockSynced => _clockSynchronizer.IsConverged;

    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
    public event EventHandler<GroupState>? GroupStateChanged;
    public event EventHandler<PlayerState>? PlayerStateChanged;
    public event EventHandler<ArtworkReceivedEventArgs>? ArtworkReceived;
    public event EventHandler<ArtworkClearedEventArgs>? ArtworkCleared;
    public event EventHandler<ColorPalette>? ColorChanged;
    public event EventHandler<VisualizerFrame>? VisualizationReceived;
    public event EventHandler<ClockSyncStatus>? ClockSyncConverged;
    public event EventHandler<ServerHelloPayload>? ServerHelloReceived;

    /// <summary>
    /// Raised for every server/activate on an encrypted connection, including
    /// re-activations that change the activity set or roles.
    /// </summary>
    public event EventHandler<ServerActivatePayload>? ServerActivateReceived;

    /// <summary>
    /// Raised when a Pairing PSK exchange completes and the long-term record has been
    /// persisted (argument: the paired server id).
    /// </summary>
    public event EventHandler<string>? PairingCompleted;

    public event EventHandler<StreamStartPayload>? StreamStartReceived;

    /// <inheritdoc />
    public event EventHandler<PairingConfigChangedEventArgs>? PairingConfigChanged;

    /// <summary>
    /// Constructs a client for the encrypted Sendspin protocol.
    /// </summary>
    /// <param name="session">
    /// The Noise session backing this connection. In production this is the same
    /// <see cref="NoiseWireFraming"/> instance the connection uses for framing.
    /// </param>
    internal SendspinClientService(
        ILogger<SendspinClientService> logger,
        ISendspinConnection connection,
        INoiseSessionInfo session,
        SendspinClientOptions options)
    {
        _logger = logger;
        _connection = connection;
        _session = session;
        _capabilities = options.Capabilities;
        _pairingStore = options.PairingRecordStore;
        _identity = options.Identity;
        _pinLockoutStore = options.PinLockoutStore;
        _presentPinAsync = options.PresentPinAsync;
        _captureDevice = options.CaptureDevice;
        _sourceEncoderFactory = options.SourceEncoderFactory;
        _clockSynchronizer = options.ClockSynchronizer ?? new KalmanClockSynchronizer();

        if (_captureDevice is not null)
        {
            _sourcePipeline = new SourceStreamPipeline(
                _captureDevice,
                _clockSynchronizer,
                msg => _connection.SendMessageAsync(msg),
                data => _connection.SendBinaryAsync(data),
                _logger,
                IsSourceStreamingPermitted,
                _sourceEncoderFactory,
                _capabilities.SourceSupport?.Codec);
        }
        _audioPipeline = options.AudioPipeline;
        _staticDelayStore = options.StaticDelayStore;

        _requiredLeadTimeMs = Math.Max(0, _capabilities.RequiredLeadTimeMs);
        _minBufferMs = Math.Max(0, _capabilities.MinBufferMs);
        _unpairedAccessEnabled = _capabilities.UnpairedAccessEnabled;

        _playerState = new PlayerState
        {
            Volume = Math.Clamp(_capabilities.InitialVolume, 0, 100),
            Muted = _capabilities.InitialMuted
        };

        _connection.StateChanged += OnConnectionStateChanged;
        _connection.TextMessageReceived += OnTextMessageReceived;
        _connection.BinaryMessageReceived += OnBinaryMessageReceived;

        if (_audioPipeline is not null)
        {
            _audioPipeline.ErrorOccurred += OnPipelineError;
            _audioPipeline.StateChanged += OnPipelineStateChanged;
        }
    }

    /// <summary>
    /// The spec's precondition for streaming captured audio: a paired ('user'-trust)
    /// connection with the source role currently active. Evaluated per start attempt,
    /// because both trust and the active-role set can change over a connection's life.
    /// </summary>
    private bool IsSourceStreamingPermitted() =>
        _session.MatchedPsk?.Category == PskCategory.LongTerm
        && (LastServerHello?.ActiveRoles.Any(r => r.StartsWith("source@", StringComparison.Ordinal)) ?? false);

    public async Task ConnectAsync(Uri serverUri, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _logger.LogInformation("Connecting to {Uri}", serverUri);

        // Cleared here rather than in SendHandshakeAsync: a failure raised between the dial
        // and the handshake wait belongs to this attempt and must survive to reach the caller.
        lock (_handshakeLock)
        {
            _handshakeFailure = null;
        }

        await _connection.ConnectAsync(serverUri, cancellationToken);
        await SendHandshakeAsync(cancellationToken);
    }

    /// <summary>
    /// Sends the ClientHello message and waits for the ServerHello response.
    /// Used for both initial connection and reconnection handshakes.
    /// </summary>
    private async Task SendHandshakeAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<bool> handshakeTcs;
        SendspinHandshakeException? alreadyFailed;
        lock (_handshakeLock)
        {
            handshakeTcs = _handshakeTcs = new TaskCompletionSource<bool>();
            alreadyFailed = _handshakeFailure;
        }

        _activateReceived = false;

        // A new handshake means a new session, so the record this client marked used belongs
        // to the previous one. DetectSessionRekey covers the in-band case; this covers the
        // per-connection one, where the identity changes without the client observing a
        // rekey on an established session.
        _markedPskUsed = false;

        // The connection's receive loop is already running when we get here, so a permanent
        // failure can be raised before there is a TCS to fail — the continuation that resumes
        // ConnectAsync may sit queued behind a busy UI thread while the peer is already
        // closing. Publishing the TCS and reading the failure under the same lock the handler
        // takes makes both interleavings equivalent: whichever side runs first, the caller
        // still gets the diagnostic rather than a 30 s wait ending in TimeoutException.
        if (alreadyFailed is not null)
        {
            throw alreadyFailed;
        }

        // 30 s per the spec's recommended handshake-phase timeout.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await using var registration = linkedCts.Token.Register(() => handshakeTcs.TrySetCanceled());
            var success = await handshakeTcs.Task;

            if (success)
            {
                _logger.LogInformation("Handshake complete with server {ServerId} ({ServerName})", ServerId, ServerName);
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            _logger.LogError("Handshake timeout - server did not complete the hello exchange");
            await _connection.DisconnectAsync("handshake_timeout");
            throw new TimeoutException("Server did not respond to handshake");
        }
    }

    private bool HasSourceRole()
        => _capabilities.Roles.Any(r => r.StartsWith("source@", StringComparison.Ordinal));

    /// <summary>
    /// Reports line-sense signal presence to the server via client/state (source role).
    /// No-op unless the source role is configured with line sensing.
    /// </summary>
    public async Task SetSourceSignalAsync(bool present)
    {
        if (!HasSourceRole() || _capabilities.SourceSupport?.LineSense != true)
            return;

        var message = new ClientStateMessage
        {
            Payload = new ClientStatePayload
            {
                Source = new SourceStatePayload { Signal = present ? "present" : "absent" },
            },
        };
        await _connection.SendMessageAsync(message);
    }

    /// <summary>
    /// Creates the ClientHello message from current capabilities.
    /// Extracted for reuse between initial connection and reconnection handshakes.
    /// Unpaired access is advertised from the effective value rather than the app's
    /// capabilities, since a server may have changed it via
    /// <c>management/set-pairing-config</c>: the hello reports what this client will
    /// actually do.
    /// </summary>
    private ClientHelloMessage CreateClientHelloMessage()
    {
        if (_capabilities.ArtworkChannels.Count > 4)
        {
            _logger.LogWarning("ArtworkChannels has {Count} entries; only the first 4 are advertised (spec maximum).",
                _capabilities.ArtworkChannels.Count);
        }

        return ClientHelloMessage.Create(
            // Under the encrypted protocol client_id/version travel in client/init and
            // are omitted here; trust_level and unpaired_access are required instead.
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
            visualizerSupport: _capabilities.VisualizerSupport,
            sourceSupport: HasSourceRole()
                ? new SourceSupport
                {
                    Features = _capabilities.SourceSupport?.LineSense == true ? new SourceFeatures { LineSense = true } : null,
                }
                : null,
            trustLevel: _session.MatchedPsk?.Category == PskCategory.LongTerm ? "user" : "none",
            supportedPairMethods: BuildPairMethods(),
            unpairedAccess: new UnpairedAccess { Enabled = _unpairedAccessEnabled }
        );
    }

    /// <summary>
    /// Performs handshake after the connection layer has successfully reconnected the WebSocket.
    /// Called from OnConnectionStateChanged when entering Handshaking state during reconnection.
    /// </summary>
    /// <remarks>
    /// Clock synchronizer is reset in HandleServerHello when the handshake completes,
    /// so we don't need to reset it here.
    /// </remarks>
    private async Task PerformReconnectHandshakeAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("WebSocket reconnected, performing handshake...");

        try
        {
            await SendHandshakeAsync(cancellationToken);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Reconnect handshake timed out");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Reconnect handshake cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reconnect handshake failed");
            await _connection.DisconnectAsync("handshake_failed");
        }
    }

    public async Task DisconnectAsync(string reason = "restart")
    {
        if (_disposed) return;

        _logger.LogInformation("Disconnecting: {Reason}", reason);

        StopTimeSyncLoop();

        await _connection.DisconnectAsync(reason);

        ServerId = null;
        ServerName = null;
        _currentGroup = null;
    }

    public async Task SendCommandAsync(string command, Dictionary<string, object>? parameters = null)
    {
        // Extract volume and mute from parameters if present
        int? volume = null;
        bool? mute = null;

        if (parameters != null)
        {
            if (parameters.TryGetValue("volume", out var volObj) && volObj is int vol)
            {
                volume = vol;
            }

            // Accept "mute" (matches the wire/command name) or legacy "muted".
            if ((parameters.TryGetValue("mute", out var muteObj) || parameters.TryGetValue("muted", out muteObj))
                && muteObj is bool m)
            {
                mute = m;
            }
        }

        var message = ClientCommandMessage.Create(command, volume, mute);

        _logger.LogDebug("Sending command: {Command}", command);
        await _connection.SendMessageAsync(message);
    }

    public async Task SetVolumeAsync(int volume)
    {
        var clampedVolume = Math.Clamp(volume, 0, 100);
        var message = ClientCommandMessage.Create(Commands.Volume, volume: clampedVolume);

        _logger.LogDebug("Setting volume to {Volume}", clampedVolume);
        await _connection.SendMessageAsync(message);
    }

    /// <inheritdoc/>
    public async Task SetMuteAsync(bool muted)
    {
        var message = ClientCommandMessage.Create(Commands.Mute, mute: muted);

        _logger.LogDebug("Setting mute to {Muted}", muted);
        await _connection.SendMessageAsync(message);
    }

    /// <inheritdoc/>
    public async Task RequestPlayerFormatAsync(
        string? codec = null, int? sampleRate = null, int? channels = null, int? bitDepth = null)
    {
        var message = StreamRequestFormatMessage.ForPlayer(new PlayerRequestFormat
        {
            Codec = codec,
            SampleRate = sampleRate,
            Channels = channels,
            BitDepth = bitDepth
        });

        _logger.LogDebug("Requesting player format change (codec={Codec}, sample_rate={SampleRate}, channels={Channels}, bit_depth={BitDepth})",
            codec ?? "unchanged", sampleRate, channels, bitDepth);
        await _connection.SendMessageAsync(message);
    }

    /// <inheritdoc/>
    public async Task RequestArtworkFormatAsync(
        int channel, string? source = null, string? format = null, int? mediaWidth = null, int? mediaHeight = null)
    {
        var message = StreamRequestFormatMessage.ForArtwork(new ArtworkRequestFormat
        {
            Channel = channel,
            Source = source,
            Format = format,
            MediaWidth = mediaWidth,
            MediaHeight = mediaHeight
        });

        _logger.LogDebug("Requesting artwork format for channel {Channel} (source={Source}, format={Format})",
            channel, source ?? "unchanged", format ?? "unchanged");
        await _connection.SendMessageAsync(message);
    }

    /// <inheritdoc/>
    public async Task RequestVisualizerFormatAsync(
        List<string>? types = null, int? rateMax = null, int? bufferCapacity = null, VisualizerSpectrum? spectrum = null)
    {
        var message = StreamRequestFormatMessage.ForVisualizer(new VisualizerRequestFormat
        {
            Types = types,
            RateMax = rateMax,
            BufferCapacity = bufferCapacity,
            Spectrum = spectrum
        });

        _logger.LogDebug("Requesting visualizer format change (types={Types}, rate_max={RateMax})",
            types is null ? "unchanged" : string.Join(",", types), rateMax);
        await _connection.SendMessageAsync(message);
    }

    /// <inheritdoc/>
    public async Task SendPlayerStateAsync(int volume, bool muted, double staticDelayMs = 0.0)
    {
        var clampedVolume = Math.Clamp(volume, 0, 100);
        var stateMessage = ClientStateMessage.CreateSynchronized(
            clampedVolume, muted, staticDelayMs,
            _requiredLeadTimeMs, _minBufferMs, GetPlayerSupportedCommands());

        _logger.LogDebug(
            "Sending player state: Volume={Volume}, Muted={Muted}, StaticDelay={StaticDelay}ms, LeadTime={LeadTime}ms, MinBuffer={MinBuffer}ms",
            clampedVolume, muted, staticDelayMs, _requiredLeadTimeMs, _minBufferMs);
        await _connection.SendMessageAsync(stateMessage);
    }

    /// <inheritdoc/>
    public async Task UpdateTimingAsync(int requiredLeadTimeMs, int minBufferMs)
    {
        _requiredLeadTimeMs = Math.Max(0, requiredLeadTimeMs);
        _minBufferMs = Math.Max(0, minBufferMs);

        _logger.LogDebug("Updating player timing: LeadTime={LeadTime}ms, MinBuffer={MinBuffer}ms",
            _requiredLeadTimeMs, _minBufferMs);

        // Re-report the player state so the server picks up the new timing for subsequent playback.
        // Callers should debounce updates locally per spec; the SDK reports each call verbatim.
        if (_connection.State == ConnectionState.Connected)
        {
            await SendPlayerStateAsync(_playerState.Volume, _playerState.Muted, _clockSynchronizer.StaticDelayMs);
        }
    }

    /// <inheritdoc/>
    public bool IsExternalSource { get; private set; }

    /// <inheritdoc/>
    public async Task EnterExternalSourceAsync()
    {
        // Notify the server first; only flip local state if it succeeds (rollback on failure).
        await _connection.SendMessageAsync(ClientStateMessage.CreateState("external_source"));
        IsExternalSource = true;
        _logger.LogInformation("Entered external_source");
    }

    /// <inheritdoc/>
    public async Task ExitExternalSourceAsync()
    {
        await _connection.SendMessageAsync(ClientStateMessage.CreateState("synchronized"));
        IsExternalSource = false;
        _logger.LogInformation("Exited external_source (synchronized)");
    }

    /// <summary>
    /// Builds the player <c>supported_commands</c> list reported in client/state, or null when
    /// none apply. Currently advertises 'set_static_delay' when the client accepts that command.
    /// </summary>
    private List<string>? GetPlayerSupportedCommands()
        => _capabilities.SupportsSetStaticDelay ? new List<string> { Commands.SetStaticDelay } : null;

    /// <inheritdoc/>
    public void ClearAudioBuffer()
    {
        _logger.LogDebug("Clearing audio buffer for immediate sync parameter effect");
        _audioPipeline?.Clear();
    }

    /// <inheritdoc />
    public string ClientId => _identity.PeerId;

    /// <inheritdoc />
    public SendspinTrustLevel TrustLevel
    {
        get
        {
            var category = _session.MatchedPsk?.Category;
            return category switch
            {
                null => SendspinTrustLevel.None,
                PskCategory.Sentinel => SendspinTrustLevel.Unpaired,
                PskCategory.Pairing => SendspinTrustLevel.Pairing,
                PskCategory.LongTerm => SendspinTrustLevel.Paired,
                // No default: an unrecognised category must never silently read as
                // "untrusted" — that is the wrong-security-indicator failure mode this
                // property exists to avoid. Throw and name the value instead.
                _ => throw new InvalidOperationException($"Unhandled PSK category: {category}"),
            };
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Per spec #122 the Pairing PSK is per-client and long-lived: a successful pairing does
    /// not consume it, and nothing here rotates it. Only <see cref="RotatePairingPsk"/> or a
    /// server's <c>management/set-pairing-config</c> replaces the stored record.
    /// </remarks>
    public string EnsurePairingPsk()
    {
        if (_pairingStore is null)
        {
            throw new InvalidOperationException(
                "No pairing record store is configured, so a generated Pairing PSK could not " +
                "be persisted. Set SendspinClientOptions.PairingRecordStore.");
        }

        lock (_pairingStoreLock)
        {
            var record = _pairingStore.List().FirstOrDefault(r => r.Category == PskCategory.Pairing);
            if (record is null)
            {
                byte[] psk = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
                record = new PairingRecord(psk, PskCategory.Pairing);
                _pairingStore.Upsert(record);
            }

            return PairingToken.Encode(_identity.PublicKey.Span, record.Psk.Span);
        }
    }

    /// <inheritdoc />
    public string RotatePairingPsk()
    {
        if (_pairingStore is null)
        {
            throw new InvalidOperationException(
                "No pairing record store is configured, so a generated Pairing PSK could not " +
                "be persisted. Set SendspinClientOptions.PairingRecordStore.");
        }

        lock (_pairingStoreLock)
        {
            // Remove every Pairing record, exactly as the management/set-pairing-config handler
            // does: a leftover second record would make EnsurePairingPsk non-deterministic about
            // which token it returns.
            foreach (var old in _pairingStore.List().Where(r => r.Category == PskCategory.Pairing))
            {
                _pairingStore.Remove(old.PskId);
            }

            byte[] fresh = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            _pairingStore.Upsert(new PairingRecord(fresh, PskCategory.Pairing));
            return PairingToken.Encode(_identity.PublicKey.Span, fresh);
        }
    }

    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        _logger.LogDebug("Connection state: {OldState} -> {NewState}", e.OldState, e.NewState);

        // Forward the event
        ConnectionStateChanged?.Invoke(this, e);

        // Stop time sync on any disconnection-related state to prevent
        // "WebSocket is not connected" spam from the time sync loop
        if (e.NewState is ConnectionState.Disconnected or ConnectionState.Reconnecting)
        {
            StopTimeSyncLoop();

            // A pairing attempt cannot survive the session (the CPace counter and handshake
            // hash reset with it), so release a presenter still showing the PIN.
            ClearPinState();
        }

        // Clean up client state on full disconnection
        if (e.NewState == ConnectionState.Disconnected)
        {
            CompleteHandshakeWait(e.Exception as SendspinHandshakeException);
            ServerId = null;
            ServerName = null;
        }

        // Re-handshake when WebSocket reconnects successfully
        // Use e.OldState instead of a separate field to avoid race conditions
        if (e.NewState == ConnectionState.Handshaking && e.OldState == ConnectionState.Reconnecting)
        {
            PerformReconnectHandshakeAsync().SafeFireAndForget(_logger);
        }
    }

    /// <summary>
    /// Ends a pending handshake wait on disconnect. A permanent handshake failure is
    /// propagated as an exception rather than a false result, so it reaches the
    /// <see cref="ConnectAsync"/> caller: an app that never subscribed to
    /// <see cref="ConnectionStateChanged"/> would otherwise see the connect succeed and
    /// only find out when its first command threw "WebSocket is not connected".
    /// </summary>
    private void CompleteHandshakeWait(SendspinHandshakeException? failure)
    {
        TaskCompletionSource<bool>? tcs;
        lock (_handshakeLock)
        {
            // Recorded before the TCS is read, and read by the waiter after it publishes one,
            // so the failure cannot fall between the two.
            if (failure is not null)
            {
                _handshakeFailure = failure;
            }

            tcs = _handshakeTcs;
        }

        // Completed outside the lock: the TCS runs its continuations inline, on this thread.
        if (failure is not null)
        {
            tcs?.TrySetException(failure);
        }
        else
        {
            tcs?.TrySetResult(false);
        }
    }

    /// <summary>
    /// Notices an in-band re-handshake and restarts the state that is scoped to one Noise
    /// session: which record has been marked used, and the CPace pairing counter (which the
    /// spec defines as the pairing activates since the last handshake). Re-handshakes happen
    /// inside the framing layer, but they install a fresh handshake hash, so a change in it
    /// is our signal that the session was re-keyed.
    /// </summary>
    /// <remarks>
    /// Called for every decrypted message rather than from the pairing path, because the
    /// re-key is not followed by a pairing activate in the flow that matters most: after a
    /// successful pairing the server rotates onto the new long-term PSK and then activates
    /// playback, and that record must still be marked used in its turn.
    /// </remarks>
    private void DetectSessionRekey()
    {
        var currentHash = _session.HandshakeHash?.ToArray();
        if (currentHash is null)
            return;
        if (_lastHandshakeHash is not null && currentHash.AsSpan().SequenceEqual(_lastHandshakeHash))
            return;

        _lastHandshakeHash = currentHash;
        _pairingCounter = 0;
        _markedPskUsed = false;
    }

    /// <summary>
    /// Marks the session's matched PSK used, once per session. Called on the first decrypted
    /// application message, which is the first proof the AEAD verified — the record
    /// must not be marked on a merely attempted connection.
    /// </summary>
    private void MarkMatchedPskUsed()
    {
        if (_markedPskUsed || _pairingStore is null)
            return;
        if (_session.MatchedPsk is not { } matched)
            return;

        string pskId = NoiseConstants.DerivePskId(matched.Key.Span);
        lock (_pairingStoreLock)
        {
            foreach (var record in _pairingStore.List())
            {
                if (record.PskId == pskId && !record.Used)
                {
                    _pairingStore.Upsert(record with { Used = true });
                    break;
                }
            }
        }

        _markedPskUsed = true;
    }

    private void OnTextMessageReceived(object? sender, string json)
    {
        // Once we have decided to close, nothing the peer sends may still take effect.
        // Neither receive path stops on its own: SendspinConnection's loop keeps reading
        // while the goodbye and the socket close are in flight, and IncomingConnection
        // delivers frames from a synchronous socket callback. Every close this client
        // initiates is fire-and-forget, so without this the frames that arrive during the
        // teardown window are handled as if the connection were still live.
        if (_connection.State is ConnectionState.Disconnected or ConnectionState.Disconnecting)
        {
            _logger.LogDebug("Dropping message received while {State}", _connection.State);
            return;
        }

        // Reaching here means the framing layer decrypted and authenticated a frame. Check
        // for a re-key first: after one, this frame belongs to a new session, so the
        // used-marking below applies to whichever record the session rotated onto.
        DetectSessionRekey();
        MarkMatchedPskUsed();

        try
        {
            var messageType = MessageSerializer.GetMessageType(json);
            _logger.LogTrace("Received: {Type}", messageType);

            switch (messageType)
            {
                case MessageTypes.ServerHello:
                    HandleServerHello(json);
                    break;

                case MessageTypes.ServerActivate:
                    HandleServerActivate(json);
                    break;

                case MessageTypes.ServerPairFinalize:
                    HandleServerPairFinalize();
                    break;

                case MessageTypes.PairAbort:
                    HandlePairAbort(json);
                    break;

                case MessageTypes.ServerPairInit:
                    HandleServerPairInit(json);
                    break;

                case MessageTypes.ServerPairAuth:
                    HandleServerPairAuth(json);
                    break;

                case MessageTypes.ServerPairConfirm:
                    HandleServerPairConfirm(json);
                    break;

                case MessageTypes.ManagementListRecords:
                case MessageTypes.ManagementAddRecord:
                case MessageTypes.ManagementRemoveRecord:
                case MessageTypes.ManagementGetPairingConfig:
                case MessageTypes.ManagementSetPairingConfig:
                    HandleManagement(messageType!, json);
                    break;

                case MessageTypes.ServerUnpair:
                    HandleServerUnpair();
                    break;

                case MessageTypes.ServerTime:
                    HandleServerTime(json);
                    break;

                case MessageTypes.GroupUpdate:
                    HandleGroupUpdate(json);
                    break;

                case MessageTypes.StreamStart:
                    HandleStreamStartAsync(json).SafeFireAndForget(_logger);
                    break;

                case MessageTypes.StreamEnd:
                    HandleStreamEndAsync(json).SafeFireAndForget(_logger);
                    break;

                case MessageTypes.StreamClear:
                    HandleStreamClear(json);
                    break;

                case MessageTypes.ServerState:
                    HandleServerState(json);
                    break;

                case MessageTypes.ServerCommand:
                    HandleServerCommand(json);
                    break;

                default:
                    _logger.LogDebug("Unhandled message type: {Type}", messageType);
                    break;
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException
            or InvalidOperationException or CPaceException)
        {
            // This message was AEAD-authenticated by the framing layer, so a payload that
            // fails to parse means the peer is broken or hostile; continuing would leave
            // the connection in an undefined state. The filter names the failures a
            // malformed payload produces: JsonException from typed deserialization,
            // FormatException from base64url fields (pairing nonces/shares/tags),
            // InvalidOperationException from JsonElement.GetString()/GetBoolean() on a
            // wrong-kind element (type routing and the management fields), and
            // CPaceException from a hostile or mis-sequenced PAKE share. The goodbye
            // reason list is closed with no protocol-error value, so the close reuses
            // 'unauthorized' — the reason this client already sends for peer-violation
            // closes — rather than inventing a wire value. Anything not named here is a
            // bug in our own handling and propagates so the receive loop surfaces it as
            // a lost connection.
            _logger.LogError(ex, "Malformed message from authenticated peer; closing connection");
            DisconnectAsync("unauthorized").SafeFireAndForget(_logger);
        }
    }

    private void HandleServerHello(string json)
    {
        var message = MessageSerializer.Deserialize<ServerHelloMessage>(json);
        if (message is null)
        {
            _logger.LogWarning("Failed to deserialize server/hello");
            _handshakeTcs?.TrySetResult(false);
            return;
        }

        var payload = message.Payload;
        LastServerHello = payload;
        ServerName = payload.Name;

        // Encrypted flow: server/hello carries only the name. The server identity
        // came from server/init, and roles arrive in the initial server/activate,
        // which completes the handshake. Per spec, no other messages (including
        // client/time and client/state) may be sent before that activate, so the
        // connected tail runs in HandleServerActivate.
        ServerId = _session.ServerId;
        _logger.LogInformation("Server hello received (encrypted): {ServerId} ({ServerName})",
            ServerId, ServerName);
        SendEncryptedClientHelloAsync().SafeFireAndForget(_logger);
    }

    /// <summary>
    /// Answers an encrypted-flow server/hello with the encrypted-shape client/hello
    /// (client_id/version omitted; trust_level and unpaired_access included).
    /// </summary>
    private async Task SendEncryptedClientHelloAsync()
    {
        var hello = CreateClientHelloMessage();
        var helloJson = MessageSerializer.Serialize(hello);
        _logger.LogInformation("Sending client/hello (encrypted):\n{Json}", helloJson);
        await _connection.SendMessageAsync(hello);
    }

    private void HandleServerActivate(string json)
    {
        var message = MessageSerializer.Deserialize<ServerActivateMessage>(json);
        if (message is null)
        {
            _logger.LogWarning("Failed to deserialize server/activate");
            return;
        }

        var payload = message.Payload;

        if (!ValidateActivateAdmissibility(payload, out var goodbyeReason))
        {
            _logger.LogWarning("Inadmissible server/activate (activities: {Activities}); closing with {Reason}",
                string.Join(", ", payload.ActivitiesList), goodbyeReason);
            _handshakeTcs?.TrySetResult(false);
            DisconnectAsync(goodbyeReason).SafeFireAndForget(_logger);
            return;
        }

        // Source role is trust-gated: it streams potentially sensitive captured audio,
        // so it MUST only run on a paired ('user'-trust) connection. If a server
        // activates source@v1 without user trust, refuse and close (spec).
        if (payload.ActiveRoles is not null
            && payload.ActiveRoles.Any(r => r.StartsWith("source@", StringComparison.Ordinal))
            && _session.MatchedPsk?.Category != PskCategory.LongTerm)
        {
            _logger.LogWarning("server/activate activated source@v1 without user trust; closing");
            _handshakeTcs?.TrySetResult(false);
            DisconnectAsync("unauthorized").SafeFireAndForget(_logger);
            return;
        }

        // Recorded only once the activation is admitted: this property is what the
        // management gate and the host's arbitration read, so a refused activate must
        // not leave its activities behind. 'Last accepted activation' is the only
        // defensible meaning for a value other code grants permission from.
        LastServerActivate = payload;

        // Mirror roles where legacy consumers look. active_roles persists across
        // activates that omit it, so only overwrite when present.
        if (payload.ActiveRoles is not null && LastServerHello is not null)
        {
            // When the source role is dropped from active_roles, stop streaming (spec:
            // the client ends its input stream on deactivation).
            bool wasSourceActive = LastServerHello.ActiveRoles.Any(r => r.StartsWith("source@", StringComparison.Ordinal));
            bool isSourceActive = payload.ActiveRoles.Any(r => r.StartsWith("source@", StringComparison.Ordinal));
            if (wasSourceActive && !isSourceActive && _sourcePipeline is not null)
            {
                _sourcePipeline.StopStreamingAsync().SafeFireAndForget(_logger);
            }

            LastServerHello.ActiveRoles = payload.ActiveRoles;
        }

        _logger.LogInformation("Server activate: activities [{Activities}], roles [{Roles}]",
            string.Join(", ", payload.ActivitiesList),
            string.Join(", ", payload.ActiveRoles ?? LastServerHello?.ActiveRoles ?? []));

        if (payload.ActivitiesList.Contains(Activities.Pairing))
        {
            HandlePairingActivate(payload);
        }

        bool first = !_activateReceived;
        _activateReceived = true;

        if (first)
        {
            // The initial activate completes the encrypted handshake; only now may the
            // client start sending (client/time, client/state).
            FinishHandshake();
            if (LastServerHello is { } hello)
            {
                ServerHelloReceived?.Invoke(this, hello);
            }
        }

        ServerActivateReceived?.Invoke(this, payload);

        if (first)
        {
            _handshakeTcs?.TrySetResult(true);
        }
    }

    /// <summary>
    /// Applies the spec's server/activate admissibility table for the matched PSK
    /// category. Returns false with the client/goodbye reason to close with.
    /// </summary>
    private bool ValidateActivateAdmissibility(ServerActivatePayload payload, out string goodbyeReason)
    {
        goodbyeReason = string.Empty;
        var psk = _session.MatchedPsk;
        if (psk is null)
        {
            // A session always has a matched PSK once the handshake completes; reaching
            // here means the framing surfaced an activate before transport mode.
            goodbyeReason = "unauthorized";
            return false;
        }

        var activities = payload.ActivitiesList ?? [];
        bool hasRoles = payload.ActiveRoles is { Count: > 0 };

        if (IsAdmissible(psk.Category, activities, hasRoles, _unpairedAccessEnabled))
        {
            return true;
        }

        // Spec rule ordering: prefer 'pairing_required' when enabling unpaired access
        // would make the activation admissible on a Sentinel-keyed session.
        if (psk.Category == PskCategory.Sentinel
            && !_unpairedAccessEnabled
            && IsAdmissible(psk.Category, activities, hasRoles, unpairedAccessEnabled: true))
        {
            goodbyeReason = "pairing_required";
            return false;
        }

        goodbyeReason = "unauthorized";
        return false;
    }

    private static bool IsAdmissible(PskCategory category, List<string> activities, bool hasRoles, bool unpairedAccessEnabled)
    {
        bool AllowedSet(IReadOnlyCollection<string> set) => category switch
        {
            PskCategory.Pairing => set.Count == 1 && set.Contains(Activities.Pairing),
            PskCategory.LongTerm => (set.Count == 1 && set.Contains(Activities.Pairing))
                || set.All(a => a is Activities.Playback or Activities.Management),
            PskCategory.Sentinel => set.Count == 0
                || (set.Count == 1 && set.Contains(Activities.Pairing))
                || (set.Count == 1 && set.Contains(Activities.Playback) && unpairedAccessEnabled),
            _ => false,
        };

        if (!AllowedSet(activities))
        {
            return false;
        }

        if (!hasRoles)
        {
            return true;
        }

        // Non-empty active_roles requires a playback-capable connection: activities
        // extended with 'playback' must still be an allowed set.
        var withPlayback = activities.Contains(Activities.Playback)
            ? activities
            : [.. activities, Activities.Playback];
        return AllowedSet(withPlayback);
    }

    /// <summary>
    /// Builds the supported_pair_methods list for the encrypted client/hello: the
    /// mandatory Pairing PSK plus any configured PIN methods with their descriptors,
    /// including current lockout state.
    /// </summary>
    private List<PairMethodDescriptor> BuildPairMethods()
    {
        var methods = new List<PairMethodDescriptor> { new() { Method = "pairing_psk" } };
        if (_capabilities.PinPairingMethods.Contains("dynamic_pin"))
        {
            methods.Add(new PairMethodDescriptor
            {
                Method = "dynamic_pin",
                OutChannels = _capabilities.PinOutChannels,
                MinPinLength = _capabilities.MinPinLength,
                LockedOut = IsPinMethodLockedOut("dynamic_pin"),
            });
        }

        if (_capabilities.PinPairingMethods.Contains("static_pin"))
        {
            methods.Add(new PairMethodDescriptor
            {
                Method = "static_pin",
                LockedOut = IsPinMethodLockedOut("static_pin"),
            });
        }

        return methods;
    }

    /// <summary>
    /// Whether this client can currently complete <paramref name="method"/> on this
    /// session. The spec's check is against live capability, which may have drifted
    /// from what supported_pair_methods advertised in client/hello.
    /// </summary>
    private bool CanOffer(string? method) => method switch
    {
        // pairing_psk is admissible only on a session already keyed by the Pairing PSK,
        // and only when the resulting long-term record can actually be persisted.
        "pairing_psk" => _session.MatchedPsk?.Category == PskCategory.Pairing
                         && _pairingStore is not null,
        // A PIN method without a lockout store cannot enforce the spec's terminal lockout at
        // 10 failures — IsPinMethodLockedOut would always report false — so offering it would
        // grant unlimited attempts. Refuse rather than fail open. Dynamic PIN additionally
        // requires a presenter: without PresentPinAsync the derived PIN would reach nobody,
        // so an attempt could only proceed with a PIN the operator never saw.
        "dynamic_pin" => _capabilities.PinPairingMethods.Contains("dynamic_pin")
                         && _pinLockoutStore is not null
                         && _presentPinAsync is not null,
        "static_pin" => _capabilities.PinPairingMethods.Contains("static_pin")
                        && _pinLockoutStore is not null,
        _ => false,
    };

    /// <summary>
    /// Starts the client side of a pairing attempt when server/activate declares the
    /// pairing activity, dispatching on the method the server selected: Pairing PSK
    /// generates the long-term PSK and delivers it in client/pair-finalize, and the PIN
    /// methods begin a PIN attempt. A method the matched PSK disallows, or that this
    /// client cannot currently complete, is refused with pair/abort reason
    /// 'method_not_supported' and the connection is left open (spec #123).
    /// </summary>
    private void HandlePairingActivate(ServerActivatePayload payload)
    {
        ClearPinState();

        // Only pairing activates count. The spec defines the CPace counter as the pairing
        // activates since the last Noise handshake; the restart on re-handshake lives in
        // DetectSessionRekey, which has already run for this message.
        _pairingCounter++;

        if (!CanOffer(payload.SelectedPairMethod))
        {
            // Spec: reply method_not_supported and LEAVE THE CONNECTION OPEN. The server
            // may re-activate with another method, or re-handshake for a fresh
            // supported_pair_methods advertisement.
            _logger.LogWarning(
                "Cannot offer pair method {Method} on this session; aborting the attempt",
                payload.SelectedPairMethod);
            _connection.SendMessageAsync(new PairAbortMessage
            {
                Payload = new PairAbortPayload { Reason = "method_not_supported" },
            }).SafeFireAndForget(_logger);
            return;
        }

        switch (payload.SelectedPairMethod)
        {
            case "pairing_psk":
                _pendingPairingPsk = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
                _logger.LogInformation("Pairing PSK flow: delivering long-term PSK to server {ServerId}", ServerId);
                _connection.SendMessageAsync(new ClientPairFinalizeMessage
                {
                    Payload = new ClientPairFinalizePayload { LongTermPsk = B64Url(_pendingPairingPsk) },
                }).SafeFireAndForget(_logger);
                break;

            case "dynamic_pin":
                StartPinAttempt(dynamic: true);
                break;

            case "static_pin":
                StartPinAttempt(dynamic: false);
                break;

            default:
                // CanOffer above and this switch are two lists of the same methods. A method
                // added to one and not the other would otherwise fall through in silence,
                // leaving the server waiting for a reply that never comes.
                throw new System.Diagnostics.UnreachableException(
                    $"CanOffer admitted pair method '{payload.SelectedPairMethod}' with no dispatch arm");
        }
    }

    /// <summary>
    /// Begins a PIN-pairing attempt by sending client/pair-init. For dynamic PIN it
    /// includes commit_B over a fresh nonce_B; for static PIN the operator gesture that
    /// opens the pairing window is assumed to have occurred (the SDK sends init
    /// immediately — hosts gate this via their own gesture handling if required).
    /// </summary>
    private void StartPinAttempt(bool dynamic)
    {
        var method = dynamic ? "dynamic_pin" : "static_pin";
        if (IsPinMethodLockedOut(method))
        {
            _connection.SendMessageAsync(new PairAbortMessage
            {
                Payload = new PairAbortPayload { Reason = "locked_out" },
            }).SafeFireAndForget(_logger);
            return;
        }

        var state = new PinPairingState { Dynamic = dynamic, Method = method };
        var init = new ClientPairInitMessage
        {
            Payload = new ClientPairInitPayload { PairingIndex = _pairingCounter },
        };
        if (dynamic)
        {
            state.NonceB = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            init.Payload.CommitB = B64Url(PinPairing.CommitB(state.NonceB));
        }

        _pinState = state;
        _logger.LogInformation("PIN pairing ({Method}): starting attempt", method);
        _connection.SendMessageAsync(init).SafeFireAndForget(_logger);
    }

    private void HandleServerPairInit(string json)
    {
        var msg = MessageSerializer.Deserialize<ServerPairInitMessage>(json);
        if (msg is null || _pinState is not { Dynamic: true } state)
            return;

        int pinLength = msg.Payload.PinLength;
        if (pinLength < _capabilities.MinPinLength || pinLength > 12)
        {
            AbortPin("pin_length_unacceptable");
            return;
        }

        state.NonceA = PinPairing.DecodeB64Url(msg.Payload.NonceA);
        var h = _session.HandshakeHash!.Value.ToArray();
        string pin = PinPairing.DerivePin(h, state.NonceA, state.NonceB!, pinLength);
        state.Pin = pin;

        // Present the PIN through the app's out-channel. Started here (this method runs on
        // the connection's synchronous receive dispatch, which cannot await); its completion
        // gates client/pair-auth in SendPairAuthAfterPinPresentedAsync, and its token is
        // cancelled by ClearPinState when the attempt or the connection is torn down.
        state.PresentPinCts = new CancellationTokenSource();
        state.PinPresented = InvokePinPresenterAsync(pin, state.PresentPinCts.Token);
        // The PAKE begins when server/pair-auth arrives (server has the PIN by then).
    }

    /// <summary>
    /// Invokes the app's <see cref="SendspinClientOptions.PresentPinAsync"/> presenter.
    /// Wrapped so a synchronously-throwing presenter faults the stored task — handled where
    /// the presentation is awaited — instead of throwing into the receive dispatch, whose
    /// catch filter treats exceptions as hostile peer input.
    /// </summary>
    private async Task InvokePinPresenterAsync(string pin, CancellationToken cancellationToken)
    {
        // Non-null on every path that reaches a dynamic pair-init: CanOffer refuses
        // dynamic_pin without a presenter, and without StartPinAttempt(dynamic: true)
        // there is no { Dynamic: true } state for HandleServerPairInit to act on.
        await _presentPinAsync!(pin, cancellationToken);
    }

    private void HandleServerPairAuth(string json)
    {
        var msg = MessageSerializer.Deserialize<ServerPairAuthMessage>(json);
        if (msg is null || _pinState is not { } state)
            return;

        // Static PIN: the PIN is device-printed and known from the start.
        string pin = state.Dynamic ? state.Pin! : (_capabilities.StaticPin ?? string.Empty);
        var h = _session.HandshakeHash!.Value.ToArray();
        byte[] sid = PinPairing.BuildSid(h, (uint)_pairingCounter);

        var cpace = CPace.Start(
            CPaceRole.Responder,
            System.Text.Encoding.ASCII.GetBytes(pin),
            sid,
            ad: PinPairing.AdClient);
        state.CPace = cpace;
        state.Sid = sid;

        // Derive stays on the synchronous path: a hostile pake_msg_1 raises CPaceException
        // into the dispatch catch, which closes the connection as with any malformed input.
        cpace.Derive(PinPairing.DecodeB64Url(msg.Payload.PakeMsg1), PinPairing.AdServer);

        SendPairAuthAfterPinPresentedAsync(state, cpace.PublicShare).SafeFireAndForget(_logger);
    }

    /// <summary>
    /// Sends client/pair-auth once the PIN presentation has completed. The share itself
    /// leaks nothing (CPace), but the reply must not leave this client before the operator
    /// could have seen the PIN — a presenter that has not finished displaying it cannot have
    /// had its PIN entered — so a slow presenter delays the PAKE rather than racing it. For
    /// static PIN (no presentation) this completes synchronously, as before. The presentation
    /// itself is awaited and its failure handled here; the fire-and-forget boundary at the
    /// call site observes only the send, exactly as it did when the send was unconditional.
    /// </summary>
    private async Task SendPairAuthAfterPinPresentedAsync(PinPairingState state, byte[] publicShare)
    {
        if (state.PinPresented is { } presented)
        {
            try
            {
                await presented;
            }
            catch (Exception ex)
            {
                // Cancelled or failed. If the attempt was already torn down (abort,
                // supersession, disconnect — the paths that cancel the presentation), its
                // outcome is settled and a successor attempt's state must not be clobbered.
                // Otherwise the app could not present the PIN, so the operator can never
                // enter it: fail closed with the reason list's client-side-gave-up value
                // rather than completing a PAKE nobody can win.
                if (ReferenceEquals(_pinState, state))
                {
                    if (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "PresentPinAsync failed; aborting the PIN attempt");
                    }

                    AbortPin("user_cancelled");
                }

                return;
            }
        }

        // The attempt may have been superseded or aborted while the presentation was
        // pending; a stale share must not be sent into whatever replaced it.
        if (!ReferenceEquals(_pinState, state))
        {
            return;
        }

        await _connection.SendMessageAsync(new ClientPairAuthMessage
        {
            Payload = new ClientPairAuthPayload { PakeMsg2 = B64Url(publicShare) },
        });
    }

    private void HandleServerPairConfirm(string json)
    {
        var msg = MessageSerializer.Deserialize<ServerPairConfirmMessage>(json);
        if (msg is null || _pinState is not { CPace: { } cpace } state)
            return;

        if (!cpace.Verify(PinPairing.DecodeB64Url(msg.Payload.ServerKc)))
        {
            RecordPinFailure(state.Method);
            AbortPin("pin_mismatch");
            return;
        }

        // Send client/pair-confirm then client/pair-finalize (wrapped PSK) back-to-back.
        var confirm = new ClientPairConfirmMessage
        {
            Payload = new ClientPairConfirmPayload { ClientKc = B64Url(cpace.Tag()) },
        };
        if (state.Dynamic)
        {
            confirm.Payload.NonceB = B64Url(state.NonceB!);
        }

        _connection.SendMessageAsync(confirm).SafeFireAndForget(_logger);

        byte[] psk = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        _pendingPairingPsk = psk;
        var suite = NoiseCipherSuite.ChaChaPoly;
        byte[] wrapped = PinPairing.WrapPsk(state.Sid!, cpace.Isk, psk, suite);
        _connection.SendMessageAsync(new ClientPairFinalizeMessage
        {
            Payload = new ClientPairFinalizePayload { WrappedPsk = B64Url(wrapped) },
        }).SafeFireAndForget(_logger);

        // Success resets the method's failure counter.
        _pinLockoutStore?.SetFailures(state.Method, 0);
    }

    private void AbortPin(string reason)
    {
        ClearPinState();
        _connection.SendMessageAsync(new PairAbortMessage
        {
            Payload = new PairAbortPayload { Reason = reason },
        }).SafeFireAndForget(_logger);
    }

    private bool IsPinMethodLockedOut(string method)
        => (_pinLockoutStore?.GetFailures(method) ?? 0) >= 10;

    private void RecordPinFailure(string method)
    {
        if (_pinLockoutStore is null)
            return;
        _pinLockoutStore.SetFailures(method, _pinLockoutStore.GetFailures(method) + 1);
    }

    private static string B64Url(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class PinPairingState
    {
        public bool Dynamic;
        public string Method = string.Empty;
        public byte[]? NonceA;
        public byte[]? NonceB;
        public string? Pin;
        public byte[]? Sid;
        public CPace? CPace;

        // Set for a dynamic attempt when server/pair-init arrives: the app's PIN
        // presentation, awaited before client/pair-auth is sent, and the cancellation
        // fired when the attempt or connection is torn down.
        public Task? PinPresented;
        public CancellationTokenSource? PresentPinCts;
    }

    /// <summary>
    /// Drops the in-flight PIN attempt, if any, and cancels its pending PIN presentation,
    /// so a presenter still holding the PIN (dialog, speaker) is released when the attempt
    /// is aborted, superseded, or the connection or client goes away.
    /// </summary>
    private void ClearPinState()
    {
        var state = _pinState;
        _pinState = null;
        if (state?.PresentPinCts is { } cts)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    /// <summary>
    /// The server persisted the pairing record; persist ours. The server will follow
    /// with an in-band re-handshake to the new PSK (handled by the Noise framing).
    /// </summary>
    private void HandleServerPairFinalize()
    {
        if (_pendingPairingPsk is null)
        {
            _logger.LogWarning("server/pair-finalize with no pairing attempt in flight; ignoring");
            return;
        }

        if (_pairingStore is not null && ServerId is not null)
        {
            lock (_pairingStoreLock)
            {
                _pairingStore.Upsert(new PairingRecord(
                    _pendingPairingPsk, PskCategory.LongTerm, ServerId));
            }

            _logger.LogInformation("Pairing complete: long-term record persisted for {ServerId}", ServerId);
        }
        else
        {
            _logger.LogWarning("Pairing completed but no record store configured; record NOT persisted");
        }

        _pendingPairingPsk = null;
        PairingCompleted?.Invoke(this, ServerId ?? string.Empty);
    }

    /// <summary>
    /// Handles a management/* request. Management is scoped to connections whose
    /// current activities include 'management'; outside that, every request answers
    /// permission_denied. Every request is answered by exactly one management/result,
    /// except one carrying a wrong-kind field (e.g. "psk":123 or a non-boolean
    /// enabled): that throws InvalidOperationException past the local filter below,
    /// so the dispatch catch closes the connection and no result is sent.
    /// </summary>
    private void HandleManagement(string type, string json)
    {
        var result = new ManagementResultPayload();

        if (LastServerActivate?.ActivitiesList.Contains(Activities.Management) != true)
        {
            result.Result = "permission_denied";
        }
        else
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var payload = doc.RootElement.GetProperty("payload");
                result = ExecuteManagementOperation(type, payload);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or FormatException)
            {
                result.Result = "invalid";
            }
        }

        _connection.SendMessageAsync(new ManagementResultMessage { Payload = result })
            .SafeFireAndForget(_logger);

        if (result.Result == "ok" && _pendingSelfRemoval)
        {
            // Removing the requester's own record closes the management session.
            _pendingSelfRemoval = false;
            DisconnectAsync("unauthorized").SafeFireAndForget(_logger);
        }
    }

    private bool _pendingSelfRemoval;

    private ManagementResultPayload ExecuteManagementOperation(
        string type, System.Text.Json.JsonElement payload)
    {
        var result = new ManagementResultPayload();
        switch (type)
        {
            case MessageTypes.ManagementListRecords:
            {
                List<ManagementRecordEntry> records;
                lock (_pairingStoreLock)
                {
                    records = (_pairingStore?.List() ?? [])
                        .Select(r => new ManagementRecordEntry(r.PskId, r.ServerId, r.Used))
                        .ToList();
                }
                result.Data = System.Text.Json.JsonSerializer.SerializeToElement(
                    new ManagementRecordsData(records),
                    MessageSerializerContext.Default.ManagementRecordsData);
                break;
            }

            case MessageTypes.ManagementAddRecord:
            {
                byte[] psk;
                try
                {
                    psk = Connection.Noise.SendspinIdentity.DecodePsk(
                        payload.GetProperty("psk").GetString() ?? string.Empty);
                }
                catch (FormatException)
                {
                    result.Result = "invalid";
                    break;
                }

                string? serverId = payload.TryGetProperty("server_id", out var sid) ? sid.GetString() : null;
                if (serverId is not null && !IsValidServerId(serverId))
                {
                    result.Result = "invalid";
                    break;
                }

                if (_pairingStore is null)
                {
                    result.Result = "storage_exhausted";
                    break;
                }

                lock (_pairingStoreLock)
                {
                    if (_pairingStore.List().Any(r => r.PskId == NoiseConstants.DerivePskId(psk)))
                    {
                        result.Result = "already_exists";
                    }
                    else
                    {
                        _pairingStore.Upsert(new PairingRecord(psk, PskCategory.LongTerm, serverId));
                    }
                }

                break;
            }

            case MessageTypes.ManagementRemoveRecord:
            {
                string pskId = payload.GetProperty("psk_id").GetString()
                    ?? throw new FormatException("psk_id missing");
                bool removed = false;
                if (_pairingStore is not null)
                {
                    lock (_pairingStoreLock)
                    {
                        if (_pairingStore.List().Any(r => r.PskId == pskId))
                        {
                            _pairingStore.Remove(pskId);
                            removed = true;
                        }
                    }
                }

                if (!removed)
                {
                    result.Result = "not_found";
                    break;
                }

                if (_session.MatchedPsk is { } current && NoiseConstants.DerivePskId(current.Key.Span) == pskId)
                {
                    _pendingSelfRemoval = true;
                }

                break;
            }

            case MessageTypes.ManagementGetPairingConfig:
            {
                // PIN methods are not implemented, so their objects are absent per spec.
                result.Data = System.Text.Json.JsonSerializer.SerializeToElement(
                    new PairingConfigData(
                        new PairingMethodState(Enabled: true),
                        new PairingMethodState(_unpairedAccessEnabled)),
                    MessageSerializerContext.Default.PairingConfigData);
                break;
            }

            case MessageTypes.ManagementSetPairingConfig:
            {
                // Patch semantics: only fields present are applied. Setting fields on an
                // unimplemented PIN method returns invalid.
                if (payload.TryGetProperty("static_pin", out _) || payload.TryGetProperty("dynamic_pin", out _))
                {
                    result.Result = "invalid";
                    break;
                }

                // Parse both fields before applying either, so a request refused partway
                // (no store, undecodable psk) changes nothing, and the single change
                // event below always describes a fully applied request.
                bool? requestedUnpairedAccess = null;
                if (payload.TryGetProperty("unpaired_access", out var ua)
                    && ua.TryGetProperty("enabled", out var uaEnabled))
                {
                    requestedUnpairedAccess = uaEnabled.GetBoolean();
                }

                byte[]? newPairingPsk = null;
                if (payload.TryGetProperty("pairing_psk", out var pp)
                    && pp.TryGetProperty("psk", out var pskEl))
                {
                    if (_pairingStore is null)
                    {
                        result.Result = "storage_exhausted";
                        break;
                    }

                    newPairingPsk = Connection.Noise.SendspinIdentity.DecodePsk(pskEl.GetString() ?? string.Empty);
                }

                // The spec permits the server to make these changes, so the SDK honours
                // them — against its own effective state, never the app's capabilities.
                bool unpairedAccessChanged = false;
                if (requestedUnpairedAccess is { } enabled)
                {
                    unpairedAccessChanged = enabled != _unpairedAccessEnabled;
                    _unpairedAccessEnabled = enabled;
                }

                if (newPairingPsk is not null)
                {
                    lock (_pairingStoreLock)
                    {
                        // _pairingStore (readonly) was verified non-null when the psk parsed.
                        foreach (var old in _pairingStore!.List().Where(r => r.Category == PskCategory.Pairing))
                        {
                            _pairingStore.Remove(old.PskId);
                        }

                        _pairingStore.Upsert(new PairingRecord(newPairingPsk, PskCategory.Pairing));
                    }
                }

                if (unpairedAccessChanged || newPairingPsk is not null)
                {
                    // One event per request, after every change is applied, and outside
                    // _pairingStoreLock: subscribers run arbitrary app code, and raising
                    // under the lock would let that code block against other threads
                    // contending for it (same-thread re-entry is safe; cross-thread
                    // waits under the lock are the deadlock hazard).
                    PairingConfigChanged?.Invoke(this, new PairingConfigChangedEventArgs
                    {
                        UnpairedAccessEnabled = _unpairedAccessEnabled,
                        PairingPskReplaced = newPairingPsk is not null,
                    });
                }

                break;
            }
        }

        return result;
    }

    /// <summary>
    /// A server_id is a base64url Curve25519 public key: exactly 43 characters, no padding,
    /// decoding to <see cref="NoiseConstants.KeySize"/> bytes. Anything else is rejected on
    /// ingest so the record store never holds a value that is not a server id.
    /// </summary>
    private static bool IsValidServerId(string serverId)
    {
        if (serverId.Length != 43)
            return false;

        try
        {
            return Base64UrlText.Decode(serverId).Length == NoiseConstants.KeySize;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// A paired server dropped its own pairing record: remove the matched stored-pubkey
    /// record (shared records are kept per spec), say goodbye with reason 'unpaired',
    /// and close. Ignored at trust level 'none'.
    /// </summary>
    private void HandleServerUnpair()
    {
        var current = _session.MatchedPsk;
        if (current is null || current.Category != PskCategory.LongTerm)
        {
            _logger.LogDebug("server/unpair on a non-user-trust connection; ignoring");
            return;
        }

        string pskId = NoiseConstants.DerivePskId(current.Key.Span);
        bool removed = false;
        if (_pairingStore is not null)
        {
            lock (_pairingStoreLock)
            {
                var record = _pairingStore.List().FirstOrDefault(r => r.PskId == pskId);
                if (record is not null && record.ServerId is not null)
                {
                    _pairingStore.Remove(pskId);
                    removed = true;
                }
            }
        }

        if (removed)
        {
            _logger.LogInformation("server/unpair: removed pairing record for {ServerId}", ServerId);
        }

        DisconnectAsync("unpaired").SafeFireAndForget(_logger);
    }

    private void HandlePairAbort(string json)
    {
        var message = MessageSerializer.Deserialize<PairAbortMessage>(json);
        _logger.LogWarning("Pairing aborted: {Reason}", message?.Payload.Reason ?? "unknown");
        _pendingPairingPsk = null;
        ClearPinState();
    }

    /// <summary>
    /// The connected tail of the handshake: runs when the initial server/activate arrives,
    /// which is the point the encrypted handshake completes and the client may start sending.
    /// </summary>
    private void FinishHandshake()
    {
        // Mark connection as fully connected
        if (_connection is SendspinConnection conn)
        {
            conn.MarkConnected();
        }
        else if (_connection is IncomingConnection incoming)
        {
            incoming.MarkConnected();
        }

        // Reset clock synchronizer for new connection
        _clockSynchronizer.Reset();

        // Notify audio pipeline of reconnect to suppress sync corrections
        // while the Kalman filter re-converges (~2 seconds).
        // Safe to call even on initial connection: _audioPipeline is null before first stream/start,
        // and NotifyReconnect on null buffer/player is a no-op.
        _audioPipeline?.NotifyReconnect();

        // Restore any persisted static_delay_ms before reporting initial state, so the server
        // sees the calibrated delay immediately on (re)connect. No-op when no store is configured.
        LoadPersistedStaticDelay();

        // Send initial client state (required by protocol after the handshake completes)
        // This tells the server we're synchronized and ready
        SendInitialClientStateAsync().SafeFireAndForget(_logger);

        // Start time synchronization loop with adaptive intervals
        StartTimeSyncLoop();
    }

    /// <summary>
    /// Sends the initial client/state message after handshake.
    /// Per the protocol, clients with player role must send their state immediately.
    /// Uses the current <see cref="_playerState"/> which was initialized from ClientCapabilities.
    /// </summary>
    private async Task SendInitialClientStateAsync()
    {
        try
        {
            // Send the current player state (initialized from capabilities)
            var stateMessage = ClientStateMessage.CreateSynchronized(
                volume: _playerState.Volume,
                muted: _playerState.Muted,
                staticDelayMs: _clockSynchronizer.StaticDelayMs,
                requiredLeadTimeMs: _requiredLeadTimeMs,
                minBufferMs: _minBufferMs,
                supportedCommands: GetPlayerSupportedCommands());
            var stateJson = MessageSerializer.Serialize(stateMessage);
            _logger.LogInformation("Sending initial client/state:\n{Json}", stateJson);
            await _connection.SendMessageAsync(stateMessage);

            // Also apply to audio pipeline to ensure consistency
            _audioPipeline?.SetVolume(_playerState.Volume);
            _audioPipeline?.SetMuted(_playerState.Muted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send initial client state");
        }
    }

    private void StartTimeSyncLoop()
    {
        StopTimeSyncLoop();
        _timeSyncCts = new CancellationTokenSource();
        TimeSyncLoopAsync(_timeSyncCts.Token).SafeFireAndForget(_logger);
        _logger.LogDebug("Time sync loop started (adaptive intervals)");
    }

    private void StopTimeSyncLoop()
    {
        _timeSyncCts?.Cancel();
        _timeSyncCts?.Dispose();
        _timeSyncCts = null;
        _logger.LogDebug("Time sync loop stopped");
    }

    /// <summary>
    /// Calculates the next time sync interval based on synchronization quality.
    /// Uses longer intervals when well-synced to improve drift measurement signal-to-noise ratio.
    /// </summary>
    private int GetAdaptiveTimeSyncIntervalMs()
    {
        var status = _clockSynchronizer.GetStatus();

        // If not enough measurements yet, sync rapidly (but after burst, so this is inter-burst interval)
        if (status.MeasurementCount < 3)
            return 500; // 500ms between initial bursts

        // Uncertainty in milliseconds
        var uncertaintyMs = status.OffsetUncertaintyMicroseconds / 1000.0;

        // Adaptive intervals based on sync quality
        // Longer intervals when synced = better drift signal detection over time
        if (uncertaintyMs < 1.0)
            return 10000; // Well synchronized: 10s (allows drift to accumulate measurably)
        else if (uncertaintyMs < 2.0)
            return 5000;  // Good sync: 5s
        else if (uncertaintyMs < 5.0)
            return 2000;  // Moderate sync: 2s
        else
            return 1000;  // Poor sync: 1s
    }

    private async Task TimeSyncLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _connection.State == ConnectionState.Connected)
            {
                // Send burst of time sync messages
                await SendTimeSyncBurstAsync(cancellationToken);

                // Calculate adaptive interval based on current sync quality
                var intervalMs = GetAdaptiveTimeSyncIntervalMs();

                _logger.LogTrace("Next time sync burst in {Interval}ms (uncertainty: {Uncertainty:F2}ms)",
                    intervalMs,
                    _clockSynchronizer.GetStatus().OffsetUncertaintyMicroseconds / 1000.0);

                await Task.Delay(intervalMs, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Time sync loop ended unexpectedly");
        }
    }

    /// <summary>
    /// Sends a burst of NTP-style time-sync probes sequentially and feeds the
    /// lowest-RTT sample into the clock synchronizer. Each probe is awaited with
    /// a per-probe timeout; if any probe times out the remainder of the burst
    /// is abandoned (matches the JS reference player, since TCP head-of-line
    /// blocking means later probes likely face the same delay).
    /// </summary>
    /// <remarks>
    /// Marked <c>internal</c> for direct invocation from concurrent-burst regression tests;
    /// production callers reach this via <see cref="StartTimeSyncLoop"/> or
    /// <see cref="HandleStreamStartAsync"/>'s smart-sync trigger.
    /// </remarks>
    internal async Task SendTimeSyncBurstAsync(CancellationToken cancellationToken)
    {
        if (_connection.State != ConnectionState.Connected)
            return;

        // Skip if another burst is already in flight (e.g., the continuous loop is mid-burst
        // and the smart-sync trigger fires). The single-slot TCS design can't safely interleave.
        if (Interlocked.CompareExchange(ref _burstRunning, 1, 0) != 0)
        {
            _logger.LogTrace("Time sync burst already in flight; skipping concurrent request");
            return;
        }

        var samples = new List<TimeSyncSample>(BurstSize);

        try
        {
            for (int i = 0; i < BurstSize; i++)
            {
                if (cancellationToken.IsCancellationRequested || _connection.State != ConnectionState.Connected)
                    break;

                var sample = await SendSingleProbeAsync(i + 1, cancellationToken).ConfigureAwait(false);
                if (sample is null)
                    break; // probe timed out or aborted; stop the burst

                samples.Add(sample.Value);

                // Pace probes so a fast localhost burst doesn't saturate the wire.
                if (i < BurstSize - 1)
                    await Task.Delay(BurstIntervalMs, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation is expected on disconnect; just exit.
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Time sync burst aborted");
        }
        finally
        {
            lock (_burstLock)
            {
                _burstInFlight = null;
                _burstInFlightT1 = 0;
            }
            Interlocked.Exchange(ref _burstRunning, 0);
        }

        if (samples.Count > 0)
            ApplyBestSample(samples);
    }

    /// <summary>
    /// Sends one client/time message and awaits its server/time reply.
    /// Returns null if the reply doesn't arrive within ProbeTimeoutMs.
    /// </summary>
    private async Task<TimeSyncSample?> SendSingleProbeAsync(int index, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<TimeSyncSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeMessage = ClientTimeMessage.CreateNow();
        var t1 = timeMessage.ClientTransmitted;

        lock (_burstLock)
        {
            _burstInFlight = tcs;
            _burstInFlightT1 = t1;
        }

        try
        {
            await _connection.SendMessageAsync(timeMessage, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_burstLock)
            {
                if (ReferenceEquals(_burstInFlight, tcs))
                    _burstInFlight = null;
            }
            throw;
        }

        _logger.LogTrace("Sent probe {Index}/{Total}: T1={T1}", index, BurstSize, t1);

        try
        {
            return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(ProbeTimeoutMs), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Time sync probe {Index}/{Total} timed out (T1={T1})", index, BurstSize, t1);
            return null;
        }
        finally
        {
            lock (_burstLock)
            {
                if (ReferenceEquals(_burstInFlight, tcs))
                    _burstInFlight = null;
            }
        }
    }

    /// <summary>
    /// Picks the lowest-RTT sample from a completed burst and feeds it to the synchronizer.
    /// </summary>
    private void ApplyBestSample(IReadOnlyList<TimeSyncSample> samples)
    {
        var best = samples[0];
        for (int i = 1; i < samples.Count; i++)
        {
            if (samples[i].Rtt < best.Rtt)
                best = samples[i];
        }

        _logger.LogDebug("Processing best of {Count} burst results: RTT={RTT:F0}μs", samples.Count, best.Rtt);

        bool wasConverged = _clockSynchronizer.IsConverged;
        _clockSynchronizer.ProcessMeasurement(best.T1, best.T2, best.T3, best.T4);

        var status = _clockSynchronizer.GetStatus();
        if (status.MeasurementCount <= 10 || status.MeasurementCount % 10 == 0)
        {
            _logger.LogDebug(
                "Clock sync: offset={Offset:F2}ms (±{Uncertainty:F2}ms), drift={Drift:F2}μs/s, converged={Converged}, driftReliable={DriftReliable}",
                status.OffsetMilliseconds,
                status.OffsetUncertaintyMicroseconds / 1000.0,
                status.DriftMicrosecondsPerSecond,
                status.IsConverged,
                status.IsDriftReliable);
        }

        if (!wasConverged && _clockSynchronizer.IsConverged)
        {
            _logger.LogInformation("[ClockSync] Converged after {Count} measurements", status.MeasurementCount);
            ClockSyncConverged?.Invoke(this, status);
        }
    }

    private void HandleServerTime(string json)
    {
        var message = MessageSerializer.Deserialize<ServerTimeMessage>(json);
        if (message is null) return;

        var t4 = ClientTimeMessage.GetCurrentTimestampMicroseconds();
        var t1 = message.ClientTransmitted;
        var t2 = message.ServerReceived;
        var t3 = message.ServerTransmitted;
        double rtt = (t4 - t1) - (t3 - t2);

        TaskCompletionSource<TimeSyncSample>? tcs = null;
        lock (_burstLock)
        {
            if (_burstInFlight is not null && _burstInFlightT1 == t1)
            {
                tcs = _burstInFlight;
                _burstInFlight = null;
                _burstInFlightT1 = 0;
            }
        }

        if (tcs is not null)
        {
            tcs.TrySetResult(new TimeSyncSample(t1, t2, t3, t4, rtt));
            return;
        }

        // Unmatched response. Could be a duplicate, a reply for a probe that already
        // timed out, or a server-initiated message. We deliberately do NOT fall back to
        // ProcessMeasurement — that would feed an unselected sample to the filter and
        // bypass burst-best selection. JS and cpp reference players also discard.
        _logger.LogTrace("Discarding unmatched server/time response (T1={T1}, RTT={RTT:F0}μs)", t1, rtt);
    }

    private void HandleGroupUpdate(string json)
    {
        var message = MessageSerializer.Deserialize<GroupUpdateMessage>(json);
        if (message is null) return;

        _currentGroup ??= new GroupState();

        var previousGroupId = _currentGroup.GroupId;
        var previousName = _currentGroup.Name;

        // group/update contains: group_id, group_name, playback_state
        // Volume, mute, metadata come via server/state (handled in HandleServerState)
        if (!string.IsNullOrEmpty(message.GroupId))
            _currentGroup.GroupId = message.GroupId;
        if (!string.IsNullOrEmpty(message.GroupName))
            _currentGroup.Name = message.GroupName;
        if (message.PlaybackState.HasValue)
            _currentGroup.PlaybackState = message.PlaybackState.Value;

        // Log group ID changes (helps diagnose grouping issues)
        if (previousGroupId != _currentGroup.GroupId && !string.IsNullOrEmpty(previousGroupId))
        {
            _logger.LogInformation("group/update [{Player}]: Group ID changed {OldId} -> {NewId}",
                _capabilities.ClientName, previousGroupId, _currentGroup.GroupId);
        }

        // Log group name changes
        if (previousName != _currentGroup.Name && _currentGroup.Name is not null)
        {
            _logger.LogInformation("group/update [{Player}]: Group name changed '{OldName}' -> '{NewName}'",
                _capabilities.ClientName, previousName ?? "(none)", _currentGroup.Name);
        }

        _logger.LogDebug("group/update [{Player}]: GroupId={GroupId}, Name={Name}, State={State}",
            _capabilities.ClientName,
            _currentGroup.GroupId,
            _currentGroup.Name ?? "(none)",
            _currentGroup.PlaybackState);

        GroupStateChanged?.Invoke(this, _currentGroup);
    }

    private void HandleServerState(string json)
    {
        var message = MessageSerializer.Deserialize<ServerStateMessage>(json);
        if (message is null) return;

        var payload = message.Payload;
        _currentGroup ??= new GroupState();

        // Update metadata from server/state (merge with existing to preserve data across partial updates)
        if (payload.Metadata is not null)
        {
            var meta = payload.Metadata;
            var existing = _currentGroup.Metadata ?? new TrackMetadata();

            // All fields use Optional<T>: absent = keep existing, present-null = clear, present-with-value = update.
            _currentGroup.Metadata = new TrackMetadata
            {
                Timestamp = meta.Timestamp.IsPresent ? meta.Timestamp.Value : existing.Timestamp,
                Title = meta.Title.IsPresent ? meta.Title.Value : existing.Title,
                Artist = meta.Artist.IsPresent ? meta.Artist.Value : existing.Artist,
                AlbumArtist = meta.AlbumArtist.IsPresent ? meta.AlbumArtist.Value : existing.AlbumArtist,
                Album = meta.Album.IsPresent ? meta.Album.Value : existing.Album,
                ArtworkUrl = meta.ArtworkUrl.IsPresent ? meta.ArtworkUrl.Value : existing.ArtworkUrl,
                Year = meta.Year.IsPresent ? meta.Year.Value : existing.Year,
                Track = meta.Track.IsPresent ? meta.Track.Value : existing.Track,
                Progress = meta.Progress.IsPresent ? meta.Progress.Value : existing.Progress
            };
        }

        // Update controller state for UI display only.
        // Do NOT apply volume to the audio pipeline - server/state contains GROUP volume.
        // The server sends server/command with player-specific volume when it wants
        // to change THIS player's output.
        // Per the Sendspin spec, repeat/shuffle live in the controller object (not metadata).
        if (payload.Controller is not null)
        {
            if (payload.Controller.Volume.HasValue)
                _currentGroup.Volume = payload.Controller.Volume.Value;
            if (payload.Controller.Muted.HasValue)
                _currentGroup.Muted = payload.Controller.Muted.Value;
            if (payload.Controller.Repeat is not null)
                _currentGroup.Repeat = payload.Controller.Repeat;
            if (payload.Controller.Shuffle.HasValue)
                _currentGroup.Shuffle = payload.Controller.Shuffle.Value;
            if (payload.Controller.SupportedCommands is not null)
                _currentGroup.SupportedCommands = payload.Controller.SupportedCommands;
        }

        // Merge color deltas (color role). Each field is Optional: absent keeps the existing color,
        // present-null clears it, present-with-value updates it.
        var colorChanged = false;
        if (payload.Color is not null)
        {
            var c = payload.Color;
            var colors = _currentGroup.Colors;

            colors.Timestamp = c.Timestamp ?? colors.Timestamp;
            if (c.BackgroundDark.IsPresent) colors.BackgroundDark = c.BackgroundDark.Value;
            if (c.BackgroundLight.IsPresent) colors.BackgroundLight = c.BackgroundLight.Value;
            if (c.Primary.IsPresent) colors.Primary = c.Primary.Value;
            if (c.Accent.IsPresent) colors.Accent = c.Accent.Value;
            if (c.OnDark.IsPresent) colors.OnDark = c.OnDark.Value;
            if (c.OnLight.IsPresent) colors.OnLight = c.OnLight.Value;

            colorChanged = true;
        }

        _logger.LogDebug("server/state [{Player}]: Volume={Volume}, Muted={Muted}, Track={Track} by {Artist}",
            _capabilities.ClientName,
            _currentGroup.Volume,
            _currentGroup.Muted,
            _currentGroup.Metadata?.Title ?? "unknown",
            _currentGroup.Metadata?.Artist ?? "unknown");

        GroupStateChanged?.Invoke(this, _currentGroup);

        if (colorChanged)
        {
            ColorChanged?.Invoke(this, _currentGroup.Colors);
        }
    }

    /// <summary>
    /// Handles server/command messages that instruct the player to apply volume or mute changes.
    /// These commands originate from controller clients and are relayed by the server to all players.
    /// </summary>
    /// <remarks>
    /// Per the Sendspin spec, after applying a server/command, the player MUST send a client/state
    /// message back to acknowledge the change. This allows the server to:
    /// 1. Confirm the player received and applied the command
    /// 2. Recalculate the group average from actual player states
    /// 3. Broadcast updated group state to controllers
    /// </remarks>
    private void HandleServerCommand(string json)
    {
        var message = MessageSerializer.Deserialize<ServerCommandMessage>(json);
        if (message?.Payload is null)
        {
            _logger.LogDebug("server/command: empty payload");
            return;
        }

        if (message.Payload.Source is { } sourceCommand && _sourcePipeline is not null)
        {
            _sourcePipeline.HandleCommandAsync(sourceCommand.Command).SafeFireAndForget(_logger);
        }

        if (message.Payload.Player is null)
        {
            return;
        }

        var player = message.Payload.Player;
        var changed = false;

        _logger.LogDebug("server/command: {Command}", player.Command);

        // Updates _playerState (this player's volume), not _currentGroup (group average).
        if (player.Volume.HasValue)
        {
            _playerState.Volume = player.Volume.Value;
            _audioPipeline?.SetVolume(player.Volume.Value);
            changed = true;
            _logger.LogInformation("server/command [{Player}]: Applied volume {Volume}",
                _capabilities.ClientName, player.Volume.Value);
        }

        if (player.Mute.HasValue)
        {
            _playerState.Muted = player.Mute.Value;
            _audioPipeline?.SetMuted(player.Mute.Value);
            changed = true;
            _logger.LogInformation("server/command [{Player}]: Applied mute {Muted}",
                _capabilities.ClientName, player.Mute.Value);
        }

        // Apply set_static_delay only when advertised as supported and a value is present.
        // Per spec the value is 0-5000 ms (negatives are not supported), so we clamp to that range.
        if (player.Command == Commands.SetStaticDelay
            && _capabilities.SupportsSetStaticDelay
            && player.StaticDelayMs.HasValue)
        {
            var clamped = Math.Clamp(player.StaticDelayMs.Value, 0, 5000);
            if (clamped != player.StaticDelayMs.Value)
            {
                _logger.LogWarning("server/command [{Player}]: static_delay_ms clamped from {Requested}ms to {Clamped}ms",
                    _capabilities.ClientName, player.StaticDelayMs.Value, clamped);
            }

            _clockSynchronizer.StaticDelayMs = clamped;
            TrySaveStaticDelay(clamped);
            changed = true;
            _logger.LogInformation("server/command [{Player}]: Applied static_delay {Delay}ms",
                _capabilities.ClientName, clamped);
        }

        if (changed)
        {
            PlayerStateChanged?.Invoke(this, _playerState);

            // Per spec: send client/state to confirm the applied state back to the server.
            SendPlayerStateAckAsync().SafeFireAndForget(_logger);
        }
    }

    /// <summary>
    /// Sends a client/state acknowledgement after applying a server/command.
    /// Reports current player volume and mute state back to the server.
    /// </summary>
    private async Task SendPlayerStateAckAsync()
    {
        await SendPlayerStateAsync(_playerState.Volume, _playerState.Muted, _clockSynchronizer.StaticDelayMs);
    }


    /// <summary>
    /// Restores the persisted static delay (if a store is configured and a value exists) into the
    /// clock synchronizer. Called on each handshake before the initial client/state is reported.
    /// </summary>
    /// <remarks>
    /// Best-effort: a throwing or out-of-range store must not abort the handshake (the initial
    /// client/state and time-sync loop run after this). On failure we log and continue without the
    /// persisted delay. The loaded value is clamped to the same range as the GroupSync offset path,
    /// since that is the broadest legitimate source of a persisted delay (negatives allowed).
    /// </remarks>
    private void LoadPersistedStaticDelay()
    {
        if (_staticDelayStore is null)
        {
            return;
        }

        double? stored;
        try
        {
            stored = _staticDelayStore.Load();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IStaticDelayStore.Load() threw; continuing without persisted static delay");
            return;
        }

        if (!stored.HasValue)
        {
            return;
        }

        if (!double.IsFinite(stored.Value))
        {
            _logger.LogWarning("Persisted static delay was not finite ({Delay}); ignoring", stored.Value);
            return;
        }

        var clamped = Math.Clamp(stored.Value, MinStaticDelayMs, MaxStaticDelayMs);
        _clockSynchronizer.StaticDelayMs = clamped;
        _logger.LogDebug("Restored persisted static delay: {Delay:+0.0;-0.0}ms", clamped);
    }

    /// <summary>
    /// Best-effort persistence of <c>static_delay_ms</c>. A throwing store must never break command
    /// or sync-offset handling — log and continue so the in-memory delay, state event, and ack still flow.
    /// </summary>
    private void TrySaveStaticDelay(double staticDelayMs)
    {
        if (_staticDelayStore is null)
        {
            return;
        }

        try
        {
            _staticDelayStore.Save(staticDelayMs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IStaticDelayStore.Save({Delay}ms) threw; static delay applied in-memory but not persisted", staticDelayMs);
        }
    }

    private async Task HandleStreamStartAsync(string json)
    {
        try
        {
            await HandleStreamStartCoreAsync(json);
        }
        catch (System.Text.Json.JsonException ex)
        {
            // An authenticated stream/start whose payload does not parse is a protocol
            // error: close, mirroring OnTextMessageReceived's malformed-payload handling.
            // This handler runs on the fire-and-forget path, so the dispatch catch never
            // sees its failures — the close must happen here. Anything else (pipeline
            // start, event subscribers) is a local fault, not peer input, and propagates
            // to the fire-and-forget boundary instead of being swallowed.
            _logger.LogError(ex, "Malformed stream/start from authenticated peer; closing connection");
            await DisconnectAsync("unauthorized");
        }
    }

    private async Task HandleStreamStartCoreAsync(string json)
    {
        var message = MessageSerializer.Deserialize<StreamStartMessage>(json);
        if (message is null)
        {
            return;
        }

        // System.Text.Json does not enforce non-nullable reference annotations, so an
        // authenticated peer can send "payload": null — or a "player" whose required
        // "codec" is null — and typed deserialization still succeeds with a null where
        // the model promises a value. Detect the hole before the first dereference and
        // signal it as the JsonException the caller's catch already routes to the
        // close; the NullReferenceException a dereference would produce instead is not
        // named there and would die in the fire-and-forget swallow.
        if (message.Payload is null || message.Payload.Format is { Codec: null })
        {
            throw new System.Text.Json.JsonException(
                "stream/start payload is null or its player has a null codec");
        }

        var payload = message.Payload;
        LastStreamStart = payload;
        StreamStartReceived?.Invoke(this, payload);

        // stream/start with no "player" key is artwork-only — skip pipeline start
        if (payload.Format is null)
        {
            _logger.LogDebug("Stream start is artwork-only (no player key), skipping pipeline start");
            return;
        }

        _logger.LogInformation("Stream starting: {Format}", payload.Format);

        while (_earlyChunkQueue.TryDequeue(out _))
        {
        }

        // Smart sync burst: only trigger if clock isn't already synced
        // If we've been connected for a while, the continuous sync loop has already converged
        if (!_clockSynchronizer.HasMinimalSync)
        {
            _logger.LogDebug("Clock not synced, triggering re-sync burst (fire-and-forget)");
            _ = SendTimeSyncBurstAsync(CancellationToken.None);
        }
        else
        {
            _logger.LogDebug("Clock already synced ({MeasurementCount} measurements), skipping burst",
                _clockSynchronizer.GetStatus()?.MeasurementCount ?? 0);
        }

        // Start pipeline immediately - don't block on sync burst
        // The continuous sync loop + sync correction will handle any residual drift
        if (_audioPipeline != null)
        {
            // A pipeline-start failure is a local fault, not peer input: the pipeline
            // reports it to the server itself (ErrorOccurred -> client/state: 'error'),
            // and it propagates from here so a real bug surfaces instead of being
            // collapsed into a log line (#88 item 2).
            await _audioPipeline.StartAsync(payload.Format);

            // Drain any chunks that arrived during initialization
            var drainedCount = 0;
            while (_earlyChunkQueue.TryDequeue(out var chunk))
            {
                _audioPipeline.ProcessAudioChunk(chunk);
                drainedCount++;
            }

            if (drainedCount > 0)
            {
                _logger.LogDebug("Drained {Count} early chunks into pipeline", drainedCount);
            }

            // Infer Playing state from stream/start for servers that don't send group/update
            _currentGroup ??= new GroupState();
            _currentGroup.PlaybackState = PlaybackState.Playing;
            GroupStateChanged?.Invoke(this, _currentGroup);
        }
    }

    private async Task HandleStreamEndAsync(string json)
    {
        try
        {
            var message = MessageSerializer.Deserialize<StreamEndMessage>(json);

            // As in HandleStreamStartCoreAsync: the serializer does not enforce the
            // model's non-nullable Payload, and the Reason accessor below dereferences
            // it, so a null payload must be reported as the JsonException this catch
            // handles before that dereference throws NullReferenceException past it.
            if (message is { Payload: null })
            {
                throw new System.Text.Json.JsonException("stream/end payload is null");
            }

            _logger.LogInformation("Stream ended: {Reason}", message?.Reason ?? "unknown");

            while (_earlyChunkQueue.TryDequeue(out _))
            {
            }

            if (_audioPipeline != null)
            {
                // A pipeline-stop failure is a local fault, not peer input; it propagates
                // to the fire-and-forget boundary so a real bug surfaces (#88 item 2).
                await _audioPipeline.StopAsync();
            }

            if (_currentGroup != null)
            {
                _currentGroup.PlaybackState = PlaybackState.Idle;
                GroupStateChanged?.Invoke(this, _currentGroup);
            }
        }
        catch (System.Text.Json.JsonException ex)
        {
            // An authenticated stream/end whose payload does not parse is a protocol
            // error: close, mirroring OnTextMessageReceived's malformed-payload handling.
            // This handler runs on the fire-and-forget path, so the dispatch catch never
            // sees its failures — the close must happen here.
            _logger.LogError(ex, "Malformed stream/end from authenticated peer; closing connection");
            await DisconnectAsync("unauthorized");
        }
    }

    private void HandleStreamClear(string json)
    {
        var message = MessageSerializer.Deserialize<StreamClearMessage>(json);
        _logger.LogDebug("Stream clear (seek)");

        _audioPipeline?.Clear();
    }

    private void OnBinaryMessageReceived(object? sender, ReadOnlyMemory<byte> data)
    {
        if (!BinaryMessageParser.TryParse(data.Span, out var type, out var timestamp, out var payload))
        {
            _logger.LogWarning("Failed to parse binary message");
            return;
        }

        var category = BinaryMessageParser.GetCategory(type);

        // No catch here, deliberately: every binary parser is Try-style (a malformed frame
        // parses to null and is dropped above or inside DispatchBinaryMessage), so nothing
        // a hostile payload produces can throw. Anything that does throw — a buggy event
        // subscriber or pipeline — is a bug in our own handling and must propagate so the
        // receive loop surfaces it as a lost connection, not be collapsed into a log line
        // (#88 item 2).
        DispatchBinaryMessage(category, type, timestamp, payload, data);
    }

    private void DispatchBinaryMessage(
        BinaryMessageCategory category, byte type, long timestamp, ReadOnlySpan<byte> payload, ReadOnlyMemory<byte> data)
    {
        switch (category)
        {
            case BinaryMessageCategory.PlayerAudio:
                var audioChunk = BinaryMessageParser.ParseAudioChunk(data.Span);
                if (audioChunk != null)
                {
                    if (_audioPipeline?.IsReady == true)
                    {
                        // Pipeline ready - process immediately
                        _audioPipeline.ProcessAudioChunk(audioChunk);
                    }
                    else if (_earlyChunkQueue.Count < MaxEarlyChunks)
                    {
                        // Pipeline not ready yet - queue for later processing
                        // This prevents chunk loss during decoder/buffer initialization
                        _earlyChunkQueue.Enqueue(audioChunk);
                        _logger.LogTrace("Queued early chunk ({QueueSize} in queue)", _earlyChunkQueue.Count);
                    }
                    // else: queue full, drop chunk (should rarely happen)
                }

                _logger.LogTrace("Audio chunk: {Length} bytes @ {Timestamp}", payload.Length, timestamp);
                break;

            case BinaryMessageCategory.Artwork:
                var artwork = BinaryMessageParser.ParseArtworkChunk(data.Span);
                if (artwork is not null)
                {
                    if (artwork.ImageData.Length == 0)
                    {
                        _logger.LogDebug("Artwork cleared on channel {Channel}", artwork.Channel);
                        ArtworkCleared?.Invoke(this, new ArtworkClearedEventArgs(artwork.Channel, artwork.Timestamp));
                    }
                    else
                    {
                        _logger.LogDebug("Artwork received on channel {Channel}: {Length} bytes",
                            artwork.Channel, artwork.ImageData.Length);
                        ArtworkReceived?.Invoke(this, new ArtworkReceivedEventArgs(artwork.Channel, artwork.Timestamp, artwork.ImageData));
                    }
                }
                break;

            case BinaryMessageCategory.Visualizer:
                // Spectrum frames are validated against the negotiated bin count from the last
                // stream/start. A malformed frame parses to null and is dropped.
                var frame = BinaryMessageParser.ParseVisualizerFrame(
                    data.Span, LastStreamStart?.Visualizer?.Spectrum?.NDispBins);
                if (frame is not null)
                {
                    _logger.LogTrace("Visualizer frame: type {Type} @ {Timestamp}", type, timestamp);
                    VisualizationReceived?.Invoke(this, frame);
                }
                else
                {
                    // Trace (not warn): at up to rate_max/sec this would spam, but it makes a dead
                    // visualizer diagnosable — e.g. a spectrum frame before any negotiated bin count.
                    _logger.LogTrace(
                        "Dropped visualizer frame: type {Type}, {Length} payload bytes, negotiated bins {Bins}",
                        type, payload.Length, LastStreamStart?.Visualizer?.Spectrum?.NDispBins);
                }
                break;
        }
    }

    /// <summary>
    /// Synchronous dispose — unsubscribes connection events to break the reference
    /// cycle that prevents GC when <see cref="DisposeAsync"/> is not called.
    /// Prefer <see cref="DisposeAsync"/> for full cleanup including async operations.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopTimeSyncLoop();
        ClearPinState();
        UnsubscribeConnectionEvents();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        StopTimeSyncLoop();
        ClearPinState();
        UnsubscribeConnectionEvents();

        // NOTE: We do NOT dispose _audioPipeline here - it's a shared singleton
        // managed by the DI container. We only stop playback if active.
        if (_audioPipeline != null)
        {
            await _audioPipeline.StopAsync();
        }

        // The source pipeline owns its capture device, so dispose it here.
        if (_sourcePipeline is not null)
        {
            await _sourcePipeline.DisposeAsync();
        }

        await _connection.DisposeAsync();
    }

    private void UnsubscribeConnectionEvents()
    {
        _connection.StateChanged -= OnConnectionStateChanged;
        _connection.TextMessageReceived -= OnTextMessageReceived;
        _connection.BinaryMessageReceived -= OnBinaryMessageReceived;

        if (_audioPipeline is not null)
        {
            _audioPipeline.ErrorOccurred -= OnPipelineError;
            _audioPipeline.StateChanged -= OnPipelineStateChanged;
        }
    }

    /// <summary>
    /// Reports <c>client/state: 'error'</c> when the audio pipeline raises an error (e.g. a buffer
    /// underrun or sync failure), so the server knows this player cannot keep up. Per the spec the
    /// player then buffers and reports <c>'synchronized'</c> once it recovers (see
    /// <see cref="OnPipelineStateChanged"/>).
    /// </summary>
    private void OnPipelineError(object? sender, AudioPipelineError error)
    {
        if (_clientErrorReported)
        {
            return;
        }

        _clientErrorReported = true;
        _logger.LogWarning("Audio pipeline error; reporting client/state: error ({Message})", error.Message);
        ReportClientErrorAsync(error.Message).SafeFireAndForget(_logger);
    }

    /// <summary>
    /// Tracks pipeline state to drive the error -&gt; synchronized recovery transition: once the
    /// pipeline returns to <see cref="AudioPipelineState.Playing"/> after an error, report
    /// <c>client/state: 'synchronized'</c>. The Error state itself is also reported here for
    /// pipelines that surface underruns via state changes rather than <see cref="OnPipelineError"/>.
    /// </summary>
    private void OnPipelineStateChanged(object? sender, AudioPipelineState state)
    {
        switch (state)
        {
            case AudioPipelineState.Error when !_clientErrorReported:
                _clientErrorReported = true;
                _logger.LogWarning("Audio pipeline entered Error state; reporting client/state: error");
                ReportClientErrorAsync(null).SafeFireAndForget(_logger);
                break;

            case AudioPipelineState.Playing when _clientErrorReported:
                _clientErrorReported = false;

                // Guard on connection state symmetrically with ReportClientErrorAsync: a recovery
                // that lands while disconnected/reconnecting would otherwise hit a closed socket.
                // The reconnect handshake re-reports synchronized via SendInitialClientStateAsync.
                if (_connection.State == ConnectionState.Connected)
                {
                    _logger.LogInformation("Audio pipeline recovered; reporting client/state: synchronized");
                    SendPlayerStateAckAsync().SafeFireAndForget(_logger);
                }

                break;
        }
    }

    private async Task ReportClientErrorAsync(string? message)
    {
        if (_connection.State != ConnectionState.Connected)
        {
            return;
        }

        await _connection.SendMessageAsync(ClientStateMessage.CreateError(message));
    }

    /// <summary>
    /// Builds a client that dials a server, wiring one <see cref="NoiseWireFraming"/> as
    /// both the connection's framing and the client's Noise session so the two cannot
    /// drift apart.
    /// </summary>
    public static SendspinClientService CreateForDial(
        ILoggerFactory loggerFactory,
        SendspinClientOptions options,
        ConnectionOptions? connectionOptions = null)
    {
        var framing = new NoiseWireFraming(
            options.Identity,
            options.PairingRecordStore is null ? null : new RecordPskResolver(options.PairingRecordStore),
            options.Suite);

        var connection = new SendspinConnection(
            loggerFactory.CreateLogger<SendspinConnection>(),
            connectionOptions,
            framing);

        return new SendspinClientService(
            loggerFactory.CreateLogger<SendspinClientService>(),
            connection,
            framing,
            options);
    }
}
