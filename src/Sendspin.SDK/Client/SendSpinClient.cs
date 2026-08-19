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

    // Holds visualizer frames and artwork until their display timestamps (#198, #199).
    private readonly MediaDisplayScheduler _displayScheduler;
    private readonly IAudioPipeline? _audioPipeline;
    private readonly IStaticDelayStore? _staticDelayStore;
    private readonly INoiseSessionInfo _session;
    private bool _activateReceived;

    // True from a pairing server/activate until the first non-pairing one. Gates every send
    // (see SendAsync): the pairing exchange holds the wire alone (#118). Cleared with the rest
    // of the per-connection state at handshake, so a reconnect never starts inside the window.
    private bool _pairingActivationActive;
    private readonly SourceStreamPipeline? _sourcePipeline;

    // Task of the most recently dispatched source command; see LastSourceCommandTask (#135).
    private Task _lastSourceCommandTask = Task.CompletedTask;
    private readonly IAudioCaptureDevice? _captureDevice;
    private readonly ISourceAudioEncoderFactory? _sourceEncoderFactory;
    private readonly IPairingRecordStore? _pairingStore;

    // Serializes this client's record-store accesses. IPairingRecordStore promises that
    // "the SDK serializes access"; before EnsurePairingPsk/RotatePairingPsk every mutation
    // ran on the receive path, and now app threads mutate too. Never held across an await.
    // Boundary: RecordPskResolver.Resolve also reads the store, from the framing inbound
    // path — a separate public object this client-private lock cannot reach — so an
    // app-thread call can still race an in-flight re-handshake's psk_id lookup.
    // Boundary 2: this lock is per-client, so two clients over one shared store (as
    // SendspinHostService builds) cannot serialize multi-call sequences against each other —
    // two concurrent EnsurePairingPsk calls can mint two Pairing records, and Rotate's
    // remove-then-upsert can interleave with set-pairing-config's. Every individual store
    // operation is safe after the store-level locking, so the worst case is nondeterminism
    // (which token wins), not corruption or lockout.
    private readonly object _pairingStoreLock = new();
    private readonly SendspinIdentity _identity;
    private bool _markedPskUsed;

    // Set when a management/remove-record targets the requester's own record, so the session
    // is closed once the result has been sent. Declared here with the rest of the per-client
    // state rather than beside its use site further down the file (#93).
    private bool _pendingSelfRemoval;
    private byte[]? _pendingPairingPsk;
    private readonly IPairingCodeLockoutStore? _pairingCodeLockoutStore;
    private readonly Func<PairingCodePresentation, CancellationToken, ValueTask>? _presentPairingCodeAsync;
    private readonly PairingWindow? _pairingWindow;

    private readonly TimeSpan _attemptTimeout;

    // Covers _attemptTimeoutCts and _pendingGatedMethod. Both are touched from the receive
    // loop AND from whatever thread raises PairingWindow.StateChanged — an operator gesture,
    // or another connection's management/open-pairing-window — so neither is safe to
    // read-modify-write unsynchronized: an unsynchronized arm leaks a CancellationTokenSource
    // that fires attempt_timeout minutes later on a connection with no attempt in flight, and
    // an unsynchronized clear can Cancel() a source another thread has already disposed, whose
    // ObjectDisposedException propagates into the receive loop's message dispatch.
    // Lock ordering: PairingWindow raises StateChanged outside its own lock, so this lock is
    // only ever taken before PairingWindow's, never after. Never held across a send.
    private readonly object _attemptLock = new object();

    // Bounds the in-flight pairing attempt. Armed by the attempt's first message, disposed by
    // ClearPairingCodeState when the attempt ends for any reason. Guarded by _attemptLock.
    private CancellationTokenSource? _attemptTimeoutCts;

    // Set when a gated activation is waiting on a window; cleared when the attempt starts or
    // the activation is superseded. Guarded by _attemptLock.
    private string? _pendingGatedMethod;
    private PairingCodeState? _pairingCodeState;
    private int _pairingCounter;
    private byte[]? _lastHandshakeHash;

    // pin_length from the current pairing activation, validated on receipt. 0 when the
    // activation is not dynamic_pin. The gating policy reads it before client/pair-init.
    private int _activationPairingCodeLength;

    // languages from the current pairing activation, handed to the pairing code presenter. Null when
    // the server sent none.
    private List<string>? _activationLanguages;

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

    // Whether a pipeline error is currently outstanding: one of the three inputs composed into
    // CurrentAvailability. Set by the pipeline error handlers, cleared when the pipeline returns
    // to Playing; also gates the recovery player-state ack (and the once-per-episode error log)
    // on an actual prior error.
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

    // Effective pairing-method configuration: seeded from capabilities at construction and
    // updated only by management/set-pairing-config. Held here rather than in the app-owned
    // capabilities object, which the SDK does not mutate; PairingConfigChanged tells the app
    // to persist the new values. client/hello, CanOffer, and get-pairing-config all read
    // these, so the advertisement and the management answer cannot drift apart.
    private bool _pairingPskEnabled;
    private bool _dynamicPairingCodeEnabled;
    private bool _staticPairingCodeEnabled;
    private int _effectiveMinPairingCodeLength;
    private string? _effectiveStaticPairingCode;

    // locations hints for the two methods that carry one. Held as effective state rather than
    // read straight off _capabilities because a server that sets the secret makes the app's
    // declared hint wrong, and the spec requires the client to follow the secret (#129).
    private List<string> _staticPairingCodeLocations;
    private List<string> _pairingPskLocations;

    // record_mode.psk_id: the shared-PSK record admitted as the storage-exhaustion fallback.
    // Null until a server sets one; the spec's default is a pre-provisioned shared-PSK
    // record, which for an SDK is the app's to provision.
    private string? _recordModePskId;

    // Bounds for any value written to the clock synchronizer's static delay. The GroupSync offset
    // path allows negatives (schedule later), so this is wider than the set_static_delay spec range.
    private const double MinStaticDelayMs = -5000.0;
    private const double MaxStaticDelayMs = 5000.0;

    // Last scheduler-side value ToWireStaticDelayMs warned about, so a delay that does not
    // survive the projection is reported once rather than on every client/state.
    private double? _lastWarnedStaticDelayMs;

    // Last line-sense signal the app reported, or null if it never has. Survives reconnects on
    // purpose: it describes the device's input, not the session (#114).
    private bool? _lastSourceSignal;

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

    /// <summary>
    /// The task of the most recently dispatched <c>source</c> command, completing with that
    /// command's own execution (test-only introspection). <see cref="Task.CompletedTask"/>
    /// before the first one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SourceStreamPipeline.HandleCommandAsync</c> returns exactly the signal a test needs,
    /// and <see cref="HandleServerCommand"/> discards it — correctly, since nothing in
    /// production waits on a source command. Without this a test could only sleep and hope.
    /// </para>
    /// <para>
    /// Sleeping is worst precisely where it matters most. The pipeline chains commands, and
    /// once the chain has passed through a channel-draining command — any <c>stop</c>, or the
    /// per-connection reset, both of which await the consumer task — the next command's
    /// continuation no longer resumes inline. A synchronous assertion after that seam passes
    /// whether or not the behaviour under test is correct, and a fixed sleep only widens the
    /// window it passes in: under CI load it degrades into "the bug did not finish in time"
    /// rather than failing. Polling cannot substitute, because these are assertions of an
    /// absence — you cannot poll for something never happening (#135).
    /// </para>
    /// </remarks>
    internal Task LastSourceCommandTask => Volatile.Read(ref _lastSourceCommandTask);

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

    /// <inheritdoc />
    public event EventHandler<string>? PairingCompleted;

    public event EventHandler<StreamStartPayload>? StreamStartReceived;

    /// <inheritdoc />
    public event EventHandler<PairingConfigChangedEventArgs>? PairingConfigChanged;

    /// <inheritdoc />
    public event EventHandler<PairingGestureRequestedEventArgs>? PairingGestureRequested;

    /// <summary>
    /// Constructs a client for the encrypted Sendspin protocol. Prefer
    /// <see cref="CreateForDial"/>, which wires the framing and session together for you.
    /// </summary>
    /// <param name="logger">Logger for client diagnostics.</param>
    /// <param name="connection">
    /// The transport this client speaks over. The client does not own it unless it built it
    /// itself — see <see cref="CreateForDial"/> and <see cref="Dispose"/>.
    /// </param>
    /// <param name="session">
    /// The Noise session backing this connection. In production this is the same
    /// <see cref="NoiseWireFraming"/> instance the connection uses for framing.
    /// </param>
    /// <param name="options">Identity, capabilities, and the optional stores and pipelines.</param>
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
        _pairingCodeLockoutStore = options.PairingCodeLockoutStore;
        _presentPairingCodeAsync = options.PresentPairingCodeAsync;
        _pairingWindow = options.PairingWindow;
        _attemptTimeout = options.PairingAttemptTimeout;
        _captureDevice = options.CaptureDevice;
        _sourceEncoderFactory = options.SourceEncoderFactory;
        _clockSynchronizer = options.ClockSynchronizer ?? new KalmanClockSynchronizer();

        _displayScheduler = new MediaDisplayScheduler(
            _clockSynchronizer,
            options.PrecisionTimer ?? HighPrecisionTimer.Shared,
            _capabilities.VisualizerSupport?.BufferCapacity ?? 0,
            _logger,
            frame => VisualizationReceived?.Invoke(this, frame),
            args => ArtworkReceived?.Invoke(this, args),
            args => ArtworkCleared?.Invoke(this, args));

        if (_captureDevice is not null)
        {
            _sourcePipeline = new SourceStreamPipeline(
                _captureDevice,
                _clockSynchronizer,
                msg => SendAsync(msg),
                data => SendBinaryAsync(data),
                _logger,
                IsSourceStreamingPermitted,
                _sourceEncoderFactory,
                _capabilities.SourceRoleSupport?.Codec);
        }
        _audioPipeline = options.AudioPipeline;
        _staticDelayStore = options.StaticDelayStore;

        _requiredLeadTimeMs = Math.Max(0, _capabilities.RequiredLeadTimeMs);
        _minBufferMs = Math.Max(0, _capabilities.MinBufferMs);
        _unpairedAccessEnabled = _capabilities.UnpairedAccessEnabled;

        // Implemented methods start enabled unless the app says otherwise. The three flags
        // exist so an app can reapply a server's set-pairing-config change on the next start
        // (#131); ANDing each with PairingCodeMethods keeps "not implemented" and "implemented
        // but disabled" distinct, which is what the spec keys different behaviour off, and
        // keeps a default-constructed ClientCapabilities reporting exactly what it did before
        // these members existed.
        _pairingPskEnabled = _capabilities.PairingPskEnabled;
        _dynamicPairingCodeEnabled = _capabilities.DynamicPairingCodeEnabled && _capabilities.PairingCodeMethods.Contains("dynamic_pin");
        _staticPairingCodeEnabled = _capabilities.StaticPairingCodeEnabled && _capabilities.PairingCodeMethods.Contains("static_pin");
        _effectiveMinPairingCodeLength = Math.Clamp(_capabilities.MinPairingCodeLength, 4, 12);
        if (_effectiveMinPairingCodeLength != _capabilities.MinPairingCodeLength)
        {
            _logger.LogWarning(
                "ClientCapabilities.MinPairingCodeLength {Value} is outside [4, 12]; clamped to {Clamped}",
                _capabilities.MinPairingCodeLength,
                _effectiveMinPairingCodeLength);
        }
        _effectiveStaticPairingCode = _capabilities.StaticPairingCode;

        // Copied, not aliased: these are mutated when a server rotates a secret, and the SDK
        // does not write to the ClientCapabilities instance the app owns (see
        // PairingConfigChangedEventArgs).
        _staticPairingCodeLocations = [.. _capabilities.StaticPairingCodeLocations];
        _pairingPskLocations = [.. _capabilities.PairingPskLocations];
        _recordModePskId = SeedRecordModePskId();

        // Usability of static_pin is evaluated live via HasUsableStaticPairingCode, not snapshotted
        // here, so a server that later supplies a valid pairing code via set-pairing-config makes the
        // method usable again without also having to resend enabled: true. This warning is
        // still worth logging once, at construction, so the app sees why the method it asked
        // for is not being offered.
        if (_capabilities.StaticPairingCodeEnabled
            && _capabilities.PairingCodeMethods.Contains("static_pin")
            && !IsValidStaticPairingCode(_capabilities.StaticPairingCode))
        {
            _logger.LogWarning(
                "ClientCapabilities.StaticPairingCode is not a valid 8-digit pairing code; static_pin will not be offered until a valid pairing code is configured");
        }

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

        if (_pairingWindow is not null)
        {
            _pairingWindow.StateChanged += OnPairingWindowStateChanged;
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

    /// <inheritdoc />
    /// <remarks>
    /// Carries the interface's contract, which matters here because
    /// <see cref="CreateForDial"/> hands back this concrete type: a caller holding a
    /// <c>SendspinClientService</c> rather than an <see cref="ISendspinClient"/> would
    /// otherwise see no documentation at all for the exceptions this can throw (#96).
    /// </remarks>
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

        // A new handshake is a new session, and an activate authorises the session it arrived
        // on — not the next one. Left standing, it permitted management/* in the window
        // between this handshake completing and this session's first server/activate, with no
        // admissibility check for the new session's PSK. Cleared here rather than on
        // disconnect because SendHandshakeAsync is private to the dial path (ConnectAsync and
        // the reconnect handshake): this particular clear does not reach the listen path's
        // arbitration, SendspinHostService.PriorityOf, which also reads LastServerActivate.
        // That is no longer the whole story, though — DetectSessionRekey clears the same
        // field for the in-band re-key case, and it runs from OnTextMessageReceived, which
        // both paths share, so THAT clear does reach PriorityOf. In the window between a
        // re-key and the new session's next activate, PriorityOf reads Empty, which changes
        // two ServerArbitration.Decide rules — see DetectSessionRekey's own comment.
        LastServerActivate = null;

        // HandleServerActivate mirrors active_roles into LastServerHello.ActiveRoles so
        // IsSourceStreamingPermitted has a single field to read the source-role grant from.
        // That mirror is the other half of the grant LastServerActivate carries and must not
        // survive into the next session either — left standing, a server/command arriving
        // before this session's own activate would stream captured audio on a grant the
        // previous session made.
        if (LastServerHello is not null)
        {
            LastServerHello.ActiveRoles = [];
        }

        // A pairing attempt cannot survive the session it was made on (the disconnect
        // handler's ClearPairingCodeState comment states the same principle for the pairing code half): the
        // PSK here was generated for, and delivered by, a specific handshake, and
        // HandleServerPairFinalize's only gate is "this field is not null" — no activity,
        // trust, or session check. Left standing, an abandoned attempt followed by a bare
        // server/pair-finalize on a later session — even one an anonymous Sentinel-keyed peer
        // opened — would persist a permanent LongTerm record.
        _pendingPairingPsk = null;

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

            // 'restart' rather than a bespoke "handshake_timeout": the reason is a closed set
            // (messaging.md:426) and a server cannot act on a string outside it. The client
            // will try again, so inviting the server to reconnect is the accurate signal.
            await _connection.DisconnectAsync(GoodbyeReasons.Restart);
            throw new TimeoutException("Server did not respond to handshake");
        }
    }

    /// <summary>
    /// Every outbound message from this client goes through here, so a pairing activation can
    /// hold the wire for the pairing exchange alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The spec's pairing sequence and aiosendspin's <c>_receive_pairing</c> agree that the
    /// exchange is exclusive: the reference server treats <em>any</em> non-pairing frame as a
    /// protocol error and closes the socket with no application-level message. #117 closed the
    /// three paths that reached the wire unprompted; this is the general form (#118).
    /// </para>
    /// <para>
    /// Gating by message type rather than at each call site is the point. A dozen senders can
    /// speak during the window — app-driven volume and format requests, a pipeline recovery ack,
    /// a management reply — and any new one would otherwise have to know the rule. It also
    /// subsumes the in-flight cases without cancellation plumbing: a sync burst already running
    /// when the activation lands drains into this check instead of onto the wire.
    /// </para>
    /// <para>
    /// Dropped rather than queued. Everything blocked here is either a state report, which is
    /// last-write-wins and recovered wholesale by the full client/state sent on leaving the
    /// window, or an app-initiated request the app can reissue. A queue would need a bound, an
    /// overflow policy, and an ordering rule against that re-report — and could deliver a stale
    /// volume after a newer one, which the re-report cannot get wrong.
    /// </para>
    /// </remarks>
    private Task SendAsync<T>(T message, CancellationToken cancellationToken = default)
        where T : IMessage
    {
        if (_pairingActivationActive && !IsAdmissibleDuringPairing(message))
        {
            _logger.LogDebug(
                "Pairing activation in effect; dropping {Type}", message.GetType().Name);
            return Task.CompletedTask;
        }

        return _connection.SendMessageAsync(message, cancellationToken);
    }

    /// <summary>Binary counterpart of <see cref="SendAsync{T}"/>: source audio, never a pairing message.</summary>
    private Task SendBinaryAsync(ReadOnlyMemory<byte> data)
    {
        if (_pairingActivationActive)
        {
            // A pairing activate that omits active_roles leaves the prior roles standing, so a
            // streaming source pipeline is never stopped and keeps producing chunks. The
            // reference server sends active_roles: [], which does stop it — this is
            // server-shape dependent, so the gate cannot rely on the roles going away.
            _logger.LogDebug("Pairing activation in effect; dropping a binary frame");
            return Task.CompletedTask;
        }

        return _connection.SendBinaryAsync(data);
    }

    /// <summary>
    /// Whether <paramref name="message"/> may travel during a pairing activation.
    /// </summary>
    /// <remarks>
    /// The pairing exchange itself, plus <c>client/hello</c>: a re-handshake landing inside the
    /// window answers <c>server/hello</c>, and dropping that reply would wedge the connection
    /// silently — a worse failure than the stray frame it would prevent. <c>client/goodbye</c>
    /// needs no entry: the connection layer sends it from <c>DisconnectAsync</c>, not through
    /// this client.
    /// </remarks>
    private static bool IsAdmissibleDuringPairing(IMessage message) => message
        is ClientPairInitMessage
        or ClientPairAuthMessage
        or ClientPairConfirmMessage
        or ClientPairFinalizeMessage
        or ClientPairPendingMessage
        or PairAbortMessage
        or ClientHelloMessage;

    /// <summary>
    /// Whether <see cref="ClientCapabilities.Roles"/> lists any version of a role family
    /// (<paramref name="family"/> is the bare name, e.g. "player", matched against the
    /// "player@" prefix so a future @v2 still counts).
    /// </summary>
    private bool HasRole(string family)
        => _capabilities.Roles.Any(r => r.StartsWith(family + "@", StringComparison.Ordinal));

    private bool HasSourceRole() => HasRole("source");

    /// <summary>
    /// Whether the server has activated any version of a role family. Distinct from
    /// <see cref="HasRole"/>, which reports what this client offers: the server decides what is
    /// active, and a client/state object for a role it did not activate is a deviation.
    /// </summary>
    /// <remarks>
    /// HandleServerActivate mirrors <c>active_roles</c> into
    /// <see cref="ServerHelloPayload.ActiveRoles"/>, so this reads the current grant rather than
    /// the hello's opening one.
    /// </remarks>
    private bool IsRoleActive(string family)
        => LastServerHello?.ActiveRoles.Any(r => r.StartsWith(family + "@", StringComparison.Ordinal))
           ?? false;

    /// <summary>
    /// Whether a client/state may carry <paramref name="family"/>'s state object: suppressed
    /// only on positive knowledge that the server did not activate the role.
    /// </summary>
    /// <remarks>
    /// No server/activate means no statement about active roles, and production never sends
    /// client/state in that window — the first activate is what completes the handshake and
    /// permits client/time and client/state at all (see HandleServerActivate). So the null case
    /// is unreachable outside test harnesses that drive the client without a handshake, and
    /// suppressing there would only stop those exercising the paths they exist to cover.
    /// </remarks>
    private bool MayReportRoleState(string family)
        => LastServerHello is null || IsRoleActive(family);

    /// <summary>
    /// Whether this client's clock must be synchronized with the server before it can claim
    /// availability. True for the two roles the spec names: player and source.
    /// </summary>
    private bool RequiresClockSync()
        => _capabilities.Roles.Any(r => r.StartsWith("player@", StringComparison.Ordinal)) || HasSourceRole();

    /// <inheritdoc />
    public async Task SetSourceSignalAsync(bool present)
    {
        if (!HasSourceRole() || _capabilities.SourceRoleSupport?.LineSense != true)
            return;

        // Recorded before the send gate, and deliberately not reset per connection: line sense
        // is a property of the device's input, not of a session, so a reconnect's initial state
        // reports what is still true. Without this the signal was simply discarded inside the
        // pre-initial window, and a client that reports only transitions never sent it again —
        // the server never learned there was signal until it changed (#114).
        _lastSourceSignal = present;

        if (!_initialClientStateSent)
        {
            // The initial message carries it instead. Sending a source-only delta here would
            // make it the server's "initial" client/state, which MUST carry all state fields.
            return;
        }

        var message = new ClientStateMessage
        {
            Payload = new ClientStatePayload
            {
                Source = BuildSourceState(),
            },
        };
        await SendAsync(message);
    }

    /// <summary>
    /// The <c>source</c> object for a client/state, or null when it does not belong: the role is
    /// not active, line sense is not supported, or nothing has reported a signal yet.
    /// </summary>
    /// <remarks>
    /// <c>signal</c> is the only field, and it is optional ("only if 'line_sense' is supported"),
    /// so with no reported signal there is nothing truthful to put in the object — inventing
    /// 'absent' would assert something the app never said.
    /// </remarks>
    private SourceStatePayload? BuildSourceState()
    {
        if (!MayReportRoleState("source")
            || _capabilities.SourceRoleSupport?.LineSense != true
            || _lastSourceSignal is not { } signal)
        {
            return null;
        }

        return new SourceStatePayload { Signal = signal ? "present" : "absent" };
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

            // Every support object is gated on its role appearing in supported_roles. The spec
            // ties the two together -- a support object belongs in client/hello exactly when its
            // role version is listed -- and aiosendspin flags an unlisted one as a client
            // deviation ("client/hello sent support objects for unlisted roles"), which a server
            // running allow_noncompliant_clients=False rejects outright rather than tolerating.
            // Roles is public and ClientCapabilities tells consumers to drop artwork@v1 from it
            // to opt out, so this was reachable straight from our own documented advice.
            playerSupport: HasRole("player")
                ? new PlayerSupport
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
                }
                : null,
            artworkSupport: HasRole("artwork")
                ? new ArtworkSupport
                {
                    // Spec allows 1-4 channels (array index = channel number).
                    Channels = _capabilities.ArtworkChannels.Take(4).ToList()
                }
                : null,
            deviceInfo: new DeviceInfo
            {
                ProductName = _capabilities.ProductName,
                Manufacturer = _capabilities.Manufacturer,
                SoftwareVersion = _capabilities.SoftwareVersion,
                MacAddress = _capabilities.MacAddress
            },
            visualizerSupport: HasRole("visualizer") ? _capabilities.VisualizerSupport : null,
            sourceSupport: HasSourceRole()
                ? new SourceSupport
                {
                    Features = _capabilities.SourceRoleSupport?.LineSense == true ? new SourceFeatures { LineSense = true } : null,
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
    /// Clock synchronizer is reset in FinishHandshake when the initial server/activate
    /// arrives, so we don't need to reset it here.
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
            // Deliberately broad, reviewed under #109. The set reaching here is narrow enough to
            // name — SendspinHandshakeException from a permanent failure, plus whatever the
            // inner DisconnectAsync raises — but this catch does not merely log: the disconnect
            // below IS the recovery, and the sole caller is
            // OnConnectionStateChanged's SafeFireAndForget. An escaping type would be logged
            // there and then dropped, leaving the client parked in Handshaking with nothing to
            // drive another attempt. A logged retry beats a silent wedge, so the filter stays
            // wide enough that the recovery always runs.
            _logger.LogError(ex, "Reconnect handshake failed");

            // Closed set again (messaging.md:426) — "handshake_failed" is not in it. The
            // reconnect loop keeps trying, so 'restart' describes what is actually happening.
            await _connection.DisconnectAsync(GoodbyeReasons.Restart);
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
        await SendAsync(message);
    }

    public async Task SetVolumeAsync(int volume)
    {
        var clampedVolume = Math.Clamp(volume, 0, 100);
        var message = ClientCommandMessage.Create(Commands.Volume, volume: clampedVolume);

        _logger.LogDebug("Setting volume to {Volume}", clampedVolume);
        await SendAsync(message);
    }

    /// <inheritdoc/>
    public async Task SetMuteAsync(bool muted)
    {
        var message = ClientCommandMessage.Create(Commands.Mute, mute: muted);

        _logger.LogDebug("Setting mute to {Muted}", muted);
        await SendAsync(message);
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
        await SendAsync(message);
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
        await SendAsync(message);
    }

    /// <inheritdoc/>
    public async Task RequestVisualizerFormatAsync(
        List<string>? types = null, int? rateMax = null, VisualizerSpectrum? spectrum = null)
    {
        var message = StreamRequestFormatMessage.ForVisualizer(new VisualizerRequestFormat
        {
            Types = types,
            RateMax = rateMax,
            Spectrum = spectrum
        });

        _logger.LogDebug("Requesting visualizer format change (types={Types}, rate_max={RateMax})",
            types is null ? "unchanged" : string.Join(",", types), rateMax);
        await SendAsync(message);
    }

    /// <inheritdoc/>
    public async Task SendPlayerStateAsync(int volume, bool muted, double? staticDelayMs = null)
    {
        var clampedVolume = Math.Clamp(volume, 0, 100);

        // A supplied delay is a client-initiated update, which the spec permits ("clients may
        // update static_delay_ms ... when audio output changes") and requires be persisted
        // ("clients must persist static_delay_ms locally across reboots and server
        // reconnections"). Applying it here is what makes the reported value true: this used to
        // report the caller's number while continuing to schedule with the old one, so the
        // server's group calibration and the client's playback disagreed, and a reconnect
        // silently reverted to the unpersisted value.
        if (staticDelayMs is { } requested && requested != _clockSynchronizer.StaticDelayMs)
        {
            _clockSynchronizer.StaticDelayMs = requested;
            TrySaveStaticDelay(requested);
        }

        // Persist the caller's values: SendInitialClientStateAsync reads _playerState, so
        // without this a reconnect's initial message would revert an app-set volume to the
        // last server-commanded value — and the pre-latch promotion below would put the old
        // volume on the wire with nothing scheduled to correct it.
        _playerState.Volume = clampedVolume;
        _playerState.Muted = muted;

        // Before the connection's initial client/state has gone out, a player-only delta must
        // not hit the wire: the server treats the first client/state it receives as the
        // initial one, which per spec MUST carry all state fields. Promote to the full
        // initial message — it reads the values persisted above — unless sync is not yet
        // established and nothing is genuinely wrong, in which case stay silent like
        // UpdateTimingAsync does: an initial sent now would carry exactly the spurious
        // available: false the deferral exists to prevent, and the deferred initial reads the
        // persisted values live, so nothing is lost.
        if (!_initialClientStateSent)
        {
            if (!InitialStateStillDeferredForClockSync)
            {
                await SendInitialClientStateAsync();
            }

            return;
        }

        // Same rule as the initial message: no player object without an active player role.
        // Enforcing it there and not here would leave the deviation reachable through every
        // app-driven volume or mute change.
        if (!MayReportRoleState("player"))
        {
            _logger.LogDebug(
                "Skipping player state: player is not an active role, so a player object would "
                + "be a client/state deviation");
            return;
        }

        // Always the applied delay, never the caller's parameter. The server MUST merge each
        // update into existing state, so a field that is present overwrites -- reporting a
        // defaulted 0 here wiped a delay the server had set, on the next volume change.
        var stateMessage = ClientStateMessage.CreatePlayerState(
            clampedVolume, muted, ToWireStaticDelayMs(_clockSynchronizer.StaticDelayMs),
            _requiredLeadTimeMs, _minBufferMs, GetPlayerSupportedCommands());

        _logger.LogDebug(
            "Sending player state: Volume={Volume}, Muted={Muted}, StaticDelay={StaticDelay}ms, LeadTime={LeadTime}ms, MinBuffer={MinBuffer}ms",
            clampedVolume, muted, _clockSynchronizer.StaticDelayMs, _requiredLeadTimeMs, _minBufferMs);
        await SendAsync(stateMessage);
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
        // Not before the connection's initial client/state has gone out, though: a player-only
        // delta must not become the first client/state the server sees (the initial MUST carry
        // all state fields). Nothing is lost — the deferred initial reads
        // _requiredLeadTimeMs/_minBufferMs live, so it carries the values applied above.
        if (_connection.State == ConnectionState.Connected && _initialClientStateSent)
        {
            await SendPlayerStateAsync(_playerState.Volume, _playerState.Muted, _clockSynchronizer.StaticDelayMs);
        }
    }

    /// <inheritdoc/>
    public bool IsExternalSource { get; private set; }

    /// <summary>
    /// True once this connection's clock sync is established: converged right now, or converged
    /// at least once earlier (<see cref="_hasConvergedOnce"/>). The spec ties a player/source's
    /// <c>available: true</c> to a synchronized clock; this is the form of that requirement
    /// that does not oscillate with the live convergence statistic. Before the connection's
    /// first convergence it is false on every path, so no premature <c>available: true</c> can
    /// reach the wire; afterwards it stays true, so a jitter-induced convergence dip cannot
    /// withdraw the claim and eject the client from its group.
    /// </summary>
    private bool ClockSyncEstablished => _hasConvergedOnce || IsClockSynced;

    /// <summary>
    /// The single source of truth for client/state's <c>available</c> field: composed from the
    /// three inputs the spec names rather than asserted independently at each call site, which
    /// is how <see cref="SendPlayerStateAsync"/> once came to hard-code it (see the §4 fix).
    /// The synchronization input is <see cref="ClockSyncEstablished"/> — latched at the first
    /// convergence — deliberately not the live <see cref="IsClockSynced"/>: convergence is a
    /// statistical threshold that oscillates under routine RTT jitter while playback carries on
    /// (the pipeline gates on minimal sync, not convergence), so composing the live value
    /// reported a still-playing client as not participating in playback, and the server moves
    /// an unavailable client to a solo group it MUST NOT auto-rejoin.
    /// </summary>
    private bool CurrentAvailability
        => (!RequiresClockSync() || ClockSyncEstablished) && !IsExternalSource && !_clientErrorReported;

    /// <summary>
    /// True while this connection's clock sync is not yet established and that is the only
    /// input composing availability to false (nothing else is wrong). Pre-latch senders stay
    /// silent in this state rather than promote the initial client/state: an initial carrying
    /// that spurious <c>available: false</c> would make the server move the client to a solo
    /// group it MUST NOT auto-rejoin — exactly what the deferral in
    /// <see cref="FinishHandshake"/> exists to prevent. The first convergence releases the
    /// initial state instead. When an input genuinely holds availability false, promotion goes
    /// ahead and the initial carries that false.
    /// </summary>
    private bool InitialStateStillDeferredForClockSync
        => RequiresClockSync() && !ClockSyncEstablished && !IsExternalSource && !_clientErrorReported;

    /// <summary>
    /// The last availability value actually sent to the server, used by
    /// <see cref="PublishAvailabilityAsync"/> to suppress a delta when nothing changed. Seeded
    /// from the initial client/state in <see cref="SendInitialClientStateAsync"/> so the first
    /// delta after it is not a spurious repeat.
    /// </summary>
    private bool? _lastAvailabilitySent;

    /// <summary>
    /// Guards the compare-and-claim on <see cref="_lastAvailabilitySent"/>. Held only across
    /// that decision, never across a send.
    /// </summary>
    private readonly object _availabilityLock = new();

    /// <summary>
    /// Whether the initial client/state for the current connection has gone out. Roles that
    /// need clock sync defer it until the first convergence (see <see cref="ApplyBestSample"/>),
    /// and it must be sent exactly once per connection: a later re-convergence takes the
    /// availability-delta path instead. Reset with the rest of the per-connection state in
    /// <see cref="FinishHandshake"/>, so a reconnect sends its initial state again.
    /// </summary>
    private bool _initialClientStateSent;

    /// <summary>
    /// Set at this connection's first convergence and never cleared for the connection's
    /// lifetime (see <see cref="ClockSyncEstablished"/> for why availability composes this
    /// latch rather than the live statistic). Reset with the rest of the per-connection state
    /// in <see cref="FinishHandshake"/>: a reconnect must re-establish sync before claiming
    /// availability, or the previous connection's latch would re-open the premature
    /// <c>available: true</c> hole on every reconnect.
    /// </summary>
    private bool _hasConvergedOnce;

    /// <summary>
    /// Set when the connection's first activate was a pairing one: a pairing activation
    /// admits nothing but pairing messages onto the wire, so <see cref="FinishHandshake"/>
    /// withholds the initial client/state entirely — even for roles that need no clock
    /// sync, whose initial would otherwise be sent on activate. The first non-pairing
    /// activate consumes this flag and runs the send-or-defer decision
    /// (<see cref="SendOrDeferInitialClientState"/>) that was skipped. Assigned per
    /// connection in <see cref="FinishHandshake"/> with the other per-connection latches.
    /// </summary>
    private bool _initialClientStateHeldForPairing;

    /// <summary>
    /// Publishes <see cref="CurrentAvailability"/> as a client/state delta when it differs from
    /// the last value sent, and no-ops otherwise. This is the only place that sends
    /// <see cref="ClientStateMessage.CreateAvailability"/> — <see cref="EnterExternalSourceAsync"/>,
    /// <see cref="ExitExternalSourceAsync"/>, and the pipeline error/recovery handlers all set
    /// their input and call this, so availability cannot again drift out of sync one call site at
    /// a time. Before the connection's initial client/state has gone out, the publish is
    /// promoted to the full initial message instead of a bare delta (see below).
    /// </summary>
    private async Task PublishAvailabilityAsync()
    {
        // Guard on connection state: a publish that lands mid-reconnect would hit a closed socket.
        // A publish skipped here is corrected on reconnect — SendInitialClientStateAsync reports
        // CurrentAvailability, so the next connection's initial state carries the composed value.
        // Event-driven callers (OnPipelineError, OnPipelineStateChanged) rely on this guard to
        // skip quietly; EnterExternalSourceAsync and ExitExternalSourceAsync check connection
        // state themselves and throw before this guard would ever apply, to preserve their
        // documented notify-first/flip-on-success contract.
        if (_connection.State != ConnectionState.Connected)
        {
            return;
        }

        // Read the composed value once and act on that one value throughout: deciding whether
        // to end the source stream from one read and publishing another would be the same
        // drift between a flag and the thing it describes that this publisher exists to stop.
        var current = CurrentAvailability;

        // An availability input flipped while the initial client/state is still deferred (e.g. a
        // pipeline error or external-source enter inside the converging window). A bare delta
        // must not hit the wire here: the server treats the first client/state it receives as
        // the initial one, which per spec MUST carry all state fields. Promote the publish to
        // the full initial message — it reads CurrentAvailability and every player field live —
        // and the latch then routes the eventual convergence through the delta path. Decided
        // BEFORE the compare-to-last-sent below: pre-latch, the tracker can only hold another
        // connection's stale value (or null), and comparing against that once let a stale
        // false suppress the promotion entirely, leaving a later player delta to become the
        // connection's first client/state.
        if (!_initialClientStateSent)
        {
            // ...unless sync is not yet established and nothing else is wrong (e.g. a
            // pipeline recovery landed inside the converging window). An initial promoted
            // now would carry the spurious available: false the deferral exists to prevent —
            // the server would solo-group the client and never auto-rejoin it — so stay
            // silent and let the first convergence release the initial state.
            if (InitialStateStillDeferredForClockSync)
            {
                return;
            }

            await EndSourceStreamIfUnavailableAsync(current);
            await SendInitialClientStateAsync();
            return;
        }

        // Post-latch the tracker was seeded by this connection's initial send, so this compares
        // against a value the server was actually told.
        //
        // The transition is claimed BEFORE the sends, not after. Written afterwards, a publish
        // that began while this one was in flight compared against the stale pre-flight value,
        // found no difference and suppressed itself — leaving the server holding this publish's
        // value, the client holding the other, and nothing scheduled to correct it (#114). The
        // initial send already seeds the tracker ahead of its await for exactly this reason;
        // the delta path simply never followed the same rule.
        //
        // Locked because the callers are event handlers and SafeFireAndForget continuations, so
        // two publishes can genuinely run in parallel and both clear an unsynchronized compare.
        bool? previous;
        lock (_availabilityLock)
        {
            if (_lastAvailabilitySent == current)
            {
                return;
            }

            previous = _lastAvailabilitySent;
            _lastAvailabilitySent = current;
        }

        try
        {
            await EndSourceStreamIfUnavailableAsync(current);
            await SendAsync(ClientStateMessage.CreateAvailability(current));
        }
        catch
        {
            // The claim did not make it onto the wire, so release it and let the next publish
            // retry — unless another publish has since claimed a different value, in which case
            // that one is now the truth and restoring ours would resurrect a stale claim.
            lock (_availabilityLock)
            {
                if (_lastAvailabilitySent == current)
                {
                    _lastAvailabilitySent = previous;
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Closes an open source input stream before this client reports <c>available: false</c>.
    /// The server rejects source chunks whenever the client is not available and treats
    /// <c>client_stream/end</c> as an implicit stop, so the end MUST precede the state: the
    /// other order leaves the server holding a stream open across the window in which it has
    /// already begun rejecting that stream's audio.
    /// </summary>
    /// <param name="available">The availability value about to be reported to the server.</param>
    /// <remarks>
    /// Enqueued unconditionally rather than gated on <c>IsStreaming</c>. A start still in
    /// flight — parked inside the capture device — has not set that flag yet, so the gate
    /// would skip it and let the start finish and stream on after the client had declared
    /// itself unavailable. The pipeline's command chain instead runs this stop after any such
    /// start, whatever stage it had reached; stopping a pipeline that is not streaming sends
    /// nothing, so the no-stream case costs one no-op through the chain.
    /// </remarks>
    private Task EndSourceStreamIfUnavailableAsync(bool available)
        => available || _sourcePipeline is null
            ? Task.CompletedTask
            : _sourcePipeline.StopStreamingAsync();

    /// <inheritdoc/>
    public async Task EnterExternalSourceAsync()
    {
        // Fails fast on a disconnected connection rather than routing through the publisher's
        // guard: that guard skips a publish silently (right for the event-driven callers), but
        // this method's documented contract is notify-first / flip-on-success, and a silent skip
        // would leave the flag flipped with nothing ever told to the server. Checking here, before
        // any state changes, keeps the flag from flipping at all in that case.
        if (_connection.State != ConnectionState.Connected)
        {
            throw new InvalidOperationException("WebSocket is not connected");
        }

        IsExternalSource = true;
        try
        {
            await PublishAvailabilityAsync();
        }
        catch
        {
            IsExternalSource = false;
            throw;
        }

        _logger.LogInformation("Entered external_source");
    }

    /// <inheritdoc/>
    public async Task ExitExternalSourceAsync()
    {
        if (_connection.State != ConnectionState.Connected)
        {
            throw new InvalidOperationException("WebSocket is not connected");
        }

        IsExternalSource = false;
        try
        {
            await PublishAvailabilityAsync();
        }
        catch
        {
            IsExternalSource = true;
            throw;
        }

        _logger.LogInformation("Exited external_source");
    }

    /// <summary>
    /// Builds the player <c>supported_commands</c> list reported in client/state, or null when
    /// none apply. Currently advertises 'set_static_delay' when the client accepts that command.
    /// </summary>
    private List<string>? GetPlayerSupportedCommands()
        => _capabilities.SupportsSetStaticDelay ? new List<string> { Commands.SetStaticDelay } : null;

    /// <summary>
    /// Projects a scheduler-side static delay onto the wire type: an integer millisecond value
    /// in 0-5000. Every client/state goes through here, so the internal range stays wider than
    /// the wire's without the difference leaking onto it.
    /// </summary>
    /// <remarks>
    /// The scheduler's value is a double in <see cref="MinStaticDelayMs"/>..<see cref="MaxStaticDelayMs"/> —
    /// fractional from calibration, negative to schedule later. The spec's <c>static_delay_ms</c>
    /// is an integer 0-5000 and states negatives are not supported; a conformant server rejects
    /// one outright rather than tolerating it. Clamping is therefore not optional, and a clamp
    /// that moved the value is worth saying out loud: the server is being told a delay the
    /// client is not actually applying.
    /// </remarks>
    private int ToWireStaticDelayMs(double staticDelayMs)
    {
        // A public settable double can be NaN or infinity; Math.Clamp propagates NaN and the
        // cast would then produce a garbage int rather than throwing.
        double bounded = double.IsFinite(staticDelayMs)
            ? Math.Clamp(staticDelayMs, 0.0, MaxStaticDelayMs)
            : 0.0;

        int wire = (int)Math.Round(bounded, MidpointRounding.AwayFromZero);

        // Deduplicated on the value: a volume slider can drive many state sends, and a
        // misconfigured delay would otherwise warn on every one of them.
        if (wire != staticDelayMs && _lastWarnedStaticDelayMs != staticDelayMs)
        {
            _lastWarnedStaticDelayMs = staticDelayMs;
            _logger.LogWarning(
                "static_delay_ms {Configured}ms is reported to the server as {Reported}ms: the wire "
                + "value is an integer 0-5000 and negatives are not supported. Audio is still "
                + "scheduled using {Configured}ms, so the server's group calibration will differ.",
                staticDelayMs, wire, staticDelayMs);
        }

        return wire;
    }

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
    /// not consume it, and nothing here rotates it. Only <see cref="RotatePairingPsk"/>, a
    /// server's <c>management/set-pairing-config</c>, or a server removing the record via
    /// <c>management/remove-record</c> replaces or drops the stored record.
    /// </remarks>
    public string EnsurePairingPsk()
    {
        lock (_pairingStoreLock)
        {
            return PairingPskOperations.Ensure(_pairingStore, _identity);
        }
    }

    /// <inheritdoc />
    public string RotatePairingPsk()
    {
        lock (_pairingStoreLock)
        {
            return PairingPskOperations.Rotate(_pairingStore, _identity);
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
            // hash reset with it), so release a presenter still showing the pairing code.
            ClearPairingCodeState();

            // Streaming state is per-connection (spec): a start from the old connection
            // must not survive into the next one, so tear capture down now, without a
            // client_stream/end — the stream it would end died with the connection.
            _sourcePipeline?.ResetForConnectionLossAsync().SafeFireAndForget(_logger);

            // Same reason, and additionally: the clock synchronizer resets on re-handshake,
            // so a pending item's display time was computed against an offset that no longer
            // holds and cannot be honoured on the new connection.
            _displayScheduler.Flush();
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
    /// Reads the handshake waiter's TaskCompletionSource under <see cref="_handshakeLock"/>.
    /// </summary>
    /// <remarks>
    /// The four completion sites on the message-handling path read the field unlocked, against
    /// a waiter that publishes it under the lock — the asymmetry #98 item 3 flags, and a
    /// contradiction of what the field's own comment says the lock is for. Only the read is
    /// guarded: completing outside the lock is deliberate, because the TCS runs its
    /// continuations inline on the calling thread (see <see cref="CompleteHandshakeWait"/>).
    /// </remarks>
    private TaskCompletionSource<bool>? CurrentHandshakeWaiter()
    {
        lock (_handshakeLock)
        {
            return _handshakeTcs;
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
    /// session: which record has been marked used, the CPace pairing counter (which the
    /// spec defines as the pairing activates since the last handshake), the accepted
    /// server/activate grant (including its ActiveRoles mirror on LastServerHello), any
    /// pending pairing PSK, and an in-flight pairing code attempt. Re-handshakes happen inside the
    /// framing layer, but they install a fresh handshake hash, so a change in it is our
    /// signal that the session was re-keyed.
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

        // A pairing code attempt's CPace state is bound to a sid built from _pairingCounter (see
        // HandleServerPairAuth), which was just reset above. An attempt straddling a re-key
        // would otherwise keep a CPace transcript computed against a counter value the next
        // pairing activate on this session will reuse for something unrelated — the same
        // per-session principle the disconnect handler applies via this same helper.
        ClearPairingCodeState();

        // An activate authorises the Noise session it arrived on. A re-key replaces that
        // session — including downward, since the spec has the server re-handshake to the
        // Pairing PSK before a pairing_psk flow — so the grant does not carry over. Without
        // this, a management grant from the retired session was honoured on the new one until
        // its first activate, on a PSK that could never have been granted management.
        //
        // Unlike SendHandshakeAsync's clear of the same field, this one reaches both the dial
        // and listen paths — DetectSessionRekey runs from OnTextMessageReceived, which both
        // share — so it also reaches SendspinHostService.PriorityOf's read of this field. In
        // the window between a re-key and this session's next activate, PriorityOf reports
        // ConnectionPriority.Empty, which changes two ServerArbitration.Decide rules: a
        // Management-priority holder becomes displaceable by incoming Playback, and Exception
        // 1 ("a pairing attempt is not displaced") stops applying — during a pairing.md:63
        // re-handshake, which is exactly when a pairing attempt is in flight. Whether
        // PriorityOf should tolerate this transient is filed separately; this comment records
        // that the gap exists, not that it is fine.
        LastServerActivate = null;

        // HandleServerActivate mirrors active_roles into LastServerHello.ActiveRoles (see
        // SendHandshakeAsync's comment on the same clear). The in-band case has no bounding
        // server/hello to reset that mirror on its own, so without this a source@v1 grant
        // from a retired session would carry forward indefinitely, rather than just until the
        // next reconnect.
        if (LastServerHello is not null)
        {
            LastServerHello.ActiveRoles = [];
        }

        // Same reasoning as SendHandshakeAsync's clear of this field: the PSK belongs to the
        // attempt that generated it, not to whatever session happens to be current when
        // server/pair-finalize arrives.
        _pendingPairingPsk = null;
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
                case MessageTypes.ManagementOpenPairingWindow:
                    HandleManagement(messageType, json);
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
            CurrentHandshakeWaiter()?.TrySetResult(false);
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
        await SendAsync(hello);
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
            CurrentHandshakeWaiter()?.TrySetResult(false);
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
            CurrentHandshakeWaiter()?.TrySetResult(false);
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

        bool pairing = payload.ActivitiesList.Contains(Activities.Pairing);
        if (pairing)
        {
            HandlePairingActivate(payload);
        }
        else
        {
            DiscardPendingGatedAttempt();
        }

        bool first = !_activateReceived;
        _activateReceived = true;

        if (first)
        {
            // The initial activate completes the encrypted handshake; only now may the
            // client start sending (client/time, client/state).
            FinishHandshake(pairing);
            if (LastServerHello is { } hello)
            {
                ServerHelloReceived?.Invoke(this, hello);
            }
        }

        // The time-sync loop runs only outside a pairing activation. A pairing activate
        // grants no roles, so there is nothing to synchronize a clock for — and the
        // reference server stops reading the socket while the operator enters the pairing code,
        // then treats the first buffered frame as the next pairing message, so a probe
        // sent during that window aborts the attempt as a protocol error. Stopping here
        // covers a pairing activate arriving mid-session with the loop already running.
        // Starting on every non-pairing activate (idempotent — StartTimeSyncLoop stops
        // any running loop first) is what resumes it afterwards, and what starts it at
        // all when the connection's FIRST activate was the pairing one and FinishHandshake
        // therefore could not. The clock synchronizer is deliberately NOT reset when
        // stopping: its measurements remain valid across the pairing window, so playback
        // resumes without re-converging.
        // Set before StopTimeSyncLoop so a probe racing the stop is dropped at the send choke
        // point rather than reaching a server that is about to treat it as a protocol error.
        bool leavingPairing = _pairingActivationActive && !pairing;
        _pairingActivationActive = pairing;

        if (pairing)
        {
            StopTimeSyncLoop();
        }
        else
        {
            // A pairing-first connection reaches its first non-pairing activate here:
            // release the withheld initial client/state by running the send-or-defer
            // decision FinishHandshake skipped. Guarded by _initialClientStateSent because
            // a genuine availability flip during the pairing window (pipeline error,
            // external source) may already have promoted the full initial onto the wire.
            // Exactly one release can fire: this one, or — for a sync-requiring role whose
            // clock has yet to converge — the first-convergence branch in ApplyBestSample,
            // and the latch set inside SendInitialClientStateAsync before its first await
            // keeps any race between them from double-sending.
            if (_initialClientStateHeldForPairing)
            {
                _initialClientStateHeldForPairing = false;
                if (!_initialClientStateSent)
                {
                    SendOrDeferInitialClientState();
                }
            }
            else if (leavingPairing && _initialClientStateSent)
            {
                // Recovers everything the window dropped. State is last-write-wins and the
                // server merges each update, so one full report of the current values restores
                // its view — a volume the app changed mid-window, a static delay, an
                // availability flip — without a queue. Skipped when the initial state has yet
                // to go out: the branch above owns that case, and a delta before it would
                // become the connection's "initial" message.
                ResendClientStateAfterPairingAsync().SafeFireAndForget(_logger);
            }

            StartTimeSyncLoop();
        }

        ServerActivateReceived?.Invoke(this, payload);

        if (first)
        {
            CurrentHandshakeWaiter()?.TrySetResult(true);
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
    /// Whether the app built this client with the method's implementation. Distinct from
    /// <see cref="IsMethodEnabled"/>: the spec omits a method object from
    /// get-pairing-config only when it is not implemented, while a merely disabled method
    /// still reports itself with enabled: false.
    /// </summary>
    private bool IsMethodImplemented(string method) => method switch
    {
        "pairing_psk" => true, // every client implements it (pairing.md:65)
        _ => _capabilities.PairingCodeMethods.Contains(method),
    };

    /// <summary>The method's effective enablement, as set by management/set-pairing-config.</summary>
    private bool IsMethodEnabled(string method) => method switch
    {
        "pairing_psk" => _pairingPskEnabled,
        "dynamic_pin" => _dynamicPairingCodeEnabled,
        "static_pin" => _staticPairingCodeEnabled,
        _ => false,
    };

    /// <summary>
    /// Whether <paramref name="pin"/> is a well-formed static pairing code: exactly 8 decimal digits
    /// (pairing.md:186).
    /// </summary>
    private static bool IsValidStaticPairingCode(string? pin) =>
        pin is { Length: 8 } && pin.All(char.IsAsciiDigit);

    /// <summary>
    /// Whether a static pairing code good enough to run the method is configured. The spec forbids
    /// enabling static_pin with no secret behind it (management.md:98) and set-pairing-config
    /// enforces that, but nothing validated what the app supplied at construction — so a
    /// client could advertise the method with a null pairing code and run CPace with an empty password.
    /// </summary>
    /// <remarks>
    /// Evaluated live rather than snapshotted at construction, so a server that supplies a
    /// valid pairing code through set-pairing-config makes the method usable again without also having
    /// to resend <c>enabled: true</c>.
    /// </remarks>
    private bool HasUsableStaticPairingCode => IsValidStaticPairingCode(_effectiveStaticPairingCode);

    /// <summary>
    /// Snapshots every effective pairing-config value into a <see cref="PairingConfigChangedEventArgs"/>.
    /// Every PairingConfigChanged raise site goes through this one builder, so a field added
    /// to the effective state later cannot reach one raise site and be missed by another.
    /// </summary>
    private PairingConfigChangedEventArgs CurrentPairingConfig(bool pairingPskReplaced) => new()
    {
        UnpairedAccessEnabled = _unpairedAccessEnabled,
        PairingPskReplaced = pairingPskReplaced,
        PairingPskEnabled = _pairingPskEnabled,
        DynamicPairingCodeEnabled = _dynamicPairingCodeEnabled,
        StaticPairingCodeEnabled = _staticPairingCodeEnabled,
        MinPairingCodeLength = _effectiveMinPairingCodeLength,
        StaticPairingCode = _effectiveStaticPairingCode,
        RecordModePskId = _recordModePskId,
        StaticPairingCodeLocations = [.. _staticPairingCodeLocations],
        PairingPskLocations = [.. _pairingPskLocations],
    };

    /// <summary>
    /// A shared-PSK record: a long-term record with no bound server_id, so the same PSK may
    /// authenticate any server holding it. record_mode's fallback target must be one of
    /// these (management.md:111).
    /// </summary>
    private static bool IsSharedPskRecord(PairingRecord record)
        => record.Category == PskCategory.LongTerm && record.ServerId is null;

    /// <summary>
    /// Restores the record-mode fallback target the app persisted from
    /// <see cref="PairingConfigChangedEventArgs.RecordModePskId"/>, but only while it still
    /// names a shared-PSK record — the same constraint <c>set-pairing-config</c> validates
    /// against (management.md:111). A server can remove that record with
    /// <c>management/remove-record</c> while the app is down, and reporting a
    /// <c>psk_id</c> no record backs would tell the next server a fallback exists that
    /// cannot be used.
    /// </summary>
    private string? SeedRecordModePskId()
    {
        string? seeded = _capabilities.RecordModePskId;
        if (seeded is null)
        {
            return null;
        }

        bool valid;
        lock (_pairingStoreLock)
        {
            valid = _pairingStore?.List().Any(r => r.PskId == seeded && IsSharedPskRecord(r)) == true;
        }

        if (valid)
        {
            return seeded;
        }

        _logger.LogWarning(
            "ClientCapabilities.RecordModePskId '{PskId}' names no shared-PSK record; record-mode fallback starts unset",
            seeded);
        return null;
    }

    /// <summary>
    /// Builds the supported_pair_methods list for the encrypted client/hello: every
    /// implemented method that is currently enabled, with its descriptor.
    /// </summary>
    private List<PairMethodDescriptor> BuildPairMethods()
    {
        var methods = new List<PairMethodDescriptor>();
        if (IsMethodEnabled("pairing_psk"))
        {
            methods.Add(new PairMethodDescriptor
            {
                Method = "pairing_psk",
                Locations = LocationsHint(_pairingPskLocations),
            });
        }

        if (CanRun("dynamic_pin"))
        {
            methods.Add(new PairMethodDescriptor
            {
                Method = "dynamic_pin",
                OutChannels = _capabilities.PairingCodeOutChannels,
                MinPairingCodeLength = _effectiveMinPairingCodeLength,
            });
        }

        if (CanRun("static_pin"))
        {
            methods.Add(new PairMethodDescriptor
            {
                Method = "static_pin",
                Locations = LocationsHint(_staticPairingCodeLocations),
            });
        }

        return methods;
    }

    /// <summary>
    /// The <c>locations</c> hint to advertise, or null to omit the field entirely when the app
    /// declared none. An empty array would be a positive claim that the secret can be found
    /// nowhere; absence is the spec's way of saying "no hint" (#129).
    /// </summary>
    /// <remarks>
    /// A defensive copy, because the descriptor is handed to the serializer while the source
    /// list can still be rewritten by a concurrent <c>set-pairing-config</c>.
    /// </remarks>
    private static List<string>? LocationsHint(List<string> locations) =>
        locations.Count == 0 ? null : [.. locations];

    /// <summary>
    /// Replaces a method's locations hint with <c>["operator"]</c> because a server just set
    /// that method's secret. Returns whether it changed, so the caller can fold it into the
    /// single PairingConfigChanged raise.
    /// </summary>
    /// <remarks>
    /// A fresh list rather than a mutation in place: <see cref="LocationsHint"/> hands copies
    /// to descriptors, but the app may also be holding the list it passed in through
    /// <see cref="ClientCapabilities"/>, and the SDK does not write to that.
    /// </remarks>
    private static bool SetLocationsToOperator(ref List<string> locations)
    {
        if (locations is [PairMethodLocations.Operator])
        {
            return false;
        }

        locations = [PairMethodLocations.Operator];
        return true;
    }

    /// <summary>
    /// Whether this client is configured to run <paramref name="method"/> to completion:
    /// implemented, enabled, and holding every dependency the method needs. A method that
    /// fails this must not be advertised in client/hello or reported enabled by
    /// get-pairing-config, or the server is told an offer exists that every attempt will
    /// refuse (#132).
    /// </summary>
    /// <remarks>
    /// Deliberately excludes session-scoped conditions. Which PSK keyed the current session
    /// is not a property of the client's configuration and can differ per connection, so it
    /// stays in <see cref="CanOffer"/>.
    /// </remarks>
    private bool CanRun(string method) => method switch
    {
        // A pairing code method without a lockout store cannot persist the failure counter, so the
        // method could never escalate to gesture-gating and every attempt would stay ungated.
        // Refuse rather than fail open. Dynamic pairing code additionally requires a presenter: without
        // PresentPairingCodeAsync the derived pairing code would reach nobody.
        //
        // A record store is a dependency in exactly the same sense (#158). Without one the pairing code
        // exchange runs to completion, the server writes a long-term record, and this client
        // stores nothing -- so it fails to authenticate on the very next connection while the
        // app has been told pairing succeeded. pairing_psk has required a store since the
        // trust-and-pairing work; the pairing code methods were never given the same treatment.
        "dynamic_pin" => IsMethodImplemented("dynamic_pin") && IsMethodEnabled("dynamic_pin")
                         && _pairingCodeLockoutStore is not null && _presentPairingCodeAsync is not null
                         && _pairingStore is not null,
        "static_pin" => IsMethodImplemented("static_pin") && IsMethodEnabled("static_pin")
                        && HasUsableStaticPairingCode && _pairingCodeLockoutStore is not null
                        && _pairingStore is not null,
        // pairing_psk is deliberately not covered here: it stays on BuildPairMethods's
        // own IsMethodEnabled check even though CanOffer requires _pairingStore is not
        // null too. Folding it in here would make a store-less client advertise
        // zero pair methods, since pairing_psk is mandatory. The pairing code methods are optional,
        // so withholding an unusable one costs nothing and stops the server rendering
        // pairing UX for a method every attempt would refuse.
        _ => false,
    };

    /// <summary>
    /// Whether this client can currently complete <paramref name="method"/> on this
    /// session. The spec's check is against live capability, which may have drifted
    /// from what supported_pair_methods advertised in client/hello.
    /// </summary>
    private bool CanOffer(string? method) => method switch
    {
        // pairing_psk is admissible only when the method is enabled, on a session already
        // keyed by the Pairing PSK, and only when the resulting long-term record can
        // actually be persisted.
        "pairing_psk" => _pairingPskEnabled
                         && _session.MatchedPsk?.Category == PskCategory.Pairing
                         && _pairingStore is not null,
        "dynamic_pin" => CanRun("dynamic_pin"),
        "static_pin" => CanRun("static_pin"),
        _ => false,
    };

    /// <summary>
    /// Starts the client side of a pairing attempt when server/activate declares the
    /// pairing activity, dispatching on the method the server selected: Pairing PSK
    /// generates the long-term PSK and delivers it in client/pair-finalize, and the pairing code
    /// methods begin a pairing code attempt. A method the matched PSK disallows, or that this
    /// client cannot currently complete, is refused with pair/abort reason
    /// 'method_not_supported' and the connection is left open (spec #123).
    /// </summary>
    private void HandlePairingActivate(ServerActivatePayload payload)
    {
        ClearPairingCodeState();

        // Only pairing activates count. The spec defines the CPace counter as the pairing
        // activates since the last Noise handshake; the restart on re-handshake lives in
        // DetectSessionRekey, which has already run for this message.
        _pairingCounter++;

        if (!CanOffer(payload.Pairing?.Method))
        {
            // Spec: reply method_not_supported and LEAVE THE CONNECTION OPEN. The server
            // may re-activate with another method, or re-handshake for a fresh
            // supported_pair_methods advertisement.
            _logger.LogWarning(
                "Cannot offer pair method {Method} on this session; aborting the attempt",
                payload.Pairing?.Method);
            SendAsync(new PairAbortMessage
            {
                Payload = new PairAbortPayload { Reason = "method_not_supported" },
            }).SafeFireAndForget(_logger);
            return;
        }

        _activationPairingCodeLength = 0;
        _activationLanguages = payload.Pairing?.Languages;
        if (payload.Pairing?.Method == "dynamic_pin")
        {
            // Validated here, not at server/pair-init: the spec moved pin_length into the
            // activation (pairing.md:149) precisely because the gating decision needs it
            // before client/pair-init is sent.
            int? length = payload.Pairing.PairingCodeLength;
            if (length is null || length < _effectiveMinPairingCodeLength || length > 12)
            {
                _logger.LogWarning(
                    "Activation pin_length {Length} is outside [{Min}, 12]; aborting the attempt",
                    length,
                    _effectiveMinPairingCodeLength);
                SendAsync(new PairAbortMessage
                {
                    Payload = new PairAbortPayload { Reason = "pin_length_unacceptable" },
                }).SafeFireAndForget(_logger);
                return;
            }

            _activationPairingCodeLength = length.Value;
        }

        switch (payload.Pairing?.Method)
        {
            case "pairing_psk":
                _pendingPairingPsk = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
                _logger.LogInformation("Pairing PSK flow: delivering long-term PSK to server {ServerId}", ServerId);
                SendAsync(new ClientPairFinalizeMessage
                {
                    Payload = new ClientPairFinalizePayload { LongTermPsk = Base64UrlText.Encode(_pendingPairingPsk) },
                }).SafeFireAndForget(_logger);
                ArmAttemptTimeout();
                break;

            case "dynamic_pin":
            case "static_pin":
                BeginOrDeferPairingCodeAttempt(payload.Pairing!.Method);
                break;

            default:
                // CanOffer above and this switch are two lists of the same methods. A method
                // added to one and not the other would otherwise fall through in silence,
                // leaving the server waiting for a reply that never comes.
                throw new System.Diagnostics.UnreachableException(
                    $"CanOffer admitted pair method '{payload.Pairing?.Method}' with no dispatch arm");
        }
    }

    /// <summary>
    /// Starts a pairing code attempt, or defers it until an operator gesture opens the pairing window.
    /// </summary>
    /// <remarks>
    /// Gating policy (pairing.md:227-230): static_pin gates every attempt; dynamic_pin gates
    /// only when the method is escalated or the session's pairing code is shorter than 6 digits —
    /// short codes are bought with a gesture. pairing_psk is never gated and does not reach here.
    /// </remarks>
    private void BeginOrDeferPairingCodeAttempt(string method)
    {
        bool gated = method == "static_pin"
                     || IsMethodEscalated(method)
                     || _activationPairingCodeLength < 6;

        bool deferred = false;
        if (gated)
        {
            // Claiming the opening and marking this connection pending must be one step. Split,
            // a window opened in the gap raised StateChanged while _pendingGatedMethod was still
            // null, so OnPairingWindowStateChanged found nothing pending and returned — and this
            // connection then waited for an opening that had already been and gone (#148).
            //
            // Locking across TryConsume is safe in this order: PairingWindow releases its own
            // gate before raising StateChanged (see Open/Close), so the reverse nesting —
            // window gate held while a handler takes _attemptLock — does not exist.
            lock (_attemptLock)
            {
                if (_pairingWindow?.TryConsume() != true)
                {
                    // Signals the wait without starting the attempt, so no timeout is armed.
                    _pendingGatedMethod = method;
                    deferred = true;
                }
            }
        }

        if (deferred)
        {
            // Outside the lock: this sends, logs, and raises an application event, none of which
            // should run while holding a lock a subscriber's callback could contend for.
            SendAsync(new ClientPairPendingMessage
            {
                Payload = new ClientPairPendingPayload { PairingIndex = _pairingCounter },
            }).SafeFireAndForget(_logger);

            _logger.LogInformation(
                "pairing code ({Method}): awaiting an operator gesture to open the pairing window",
                method);
            PairingGestureRequested?.Invoke(this, new PairingGestureRequestedEventArgs
            {
                Method = method,
                PairingIndex = _pairingCounter,
            });
            return;
        }

        StartPairingCodeAttempt(dynamic: method == "dynamic_pin");
    }

    /// <summary>
    /// A window opened while this connection was waiting on a gesture. Exactly one waiting
    /// connection can claim any opening; the losers stay pending and send nothing.
    /// </summary>
    private void OnPairingWindowStateChanged(object? sender, EventArgs e)
    {
        // PairingWindow swallows every subscriber's exceptions to stop one application handler
        // tearing down another connection's message dispatch — which also means a fault in this
        // handler, the SDK's own, would be completely invisible: no log, no rethrow, and a
        // symptom of a gated attempt that silently never resumes after the operator's gesture.
        // Logging here rather than giving PairingWindow a logger keeps the window free of
        // logging concerns (#147).
        try
        {
            string method;

            // The claim — "is this connection still pending, and can it take the opening?" — has
            // to be atomic, or two raises on different threads both consume for the same
            // connection. TryConsume takes the window's own lock, which is safe here: the window
            // raises this event after releasing that lock, so the two are never taken in the
            // other order.
            lock (_attemptLock)
            {
                if (_pendingGatedMethod is not { } pending)
                {
                    return;
                }

                if (_pairingWindow?.TryConsume() != true)
                {
                    return;
                }

                _pendingGatedMethod = null;
                method = pending;
            }

            StartPairingCodeAttempt(dynamic: method == "dynamic_pin");
        }
        catch (Exception ex)
        {
            // The opening may already have been consumed by the time this throws, in which case
            // the operator's gesture is spent and the attempt did not start. Nothing here can
            // recover that; the point is that it stops being silent.
            _logger.LogError(
                ex,
                "Pairing window state-changed handler failed; a gated attempt may not have resumed");
        }
    }

    /// <summary>
    /// Drops a gated attempt still waiting on a gesture, without consuming the window.
    /// </summary>
    /// <remarks>
    /// A pending attempt belongs to the activation that deferred it. An activation that does
    /// not declare the pairing activity ends that one, so the wait ends with it: left standing,
    /// the next opening would make this connection send client/pair-init outside any pairing
    /// activation — and consume the shared window while doing it, so the gesture the operator
    /// made for whichever connection is still legitimately pending would silently do nothing
    /// for them. Not consuming the window is the other half: the opening stays available.
    /// The superseded-by-a-newer-pairing-activation case is <see cref="HandlePairingActivate"/>'s
    /// own ClearPairingCodeState.
    /// </remarks>
    private void DiscardPendingGatedAttempt()
    {
        lock (_attemptLock)
        {
            if (_pendingGatedMethod is null)
            {
                return;
            }

            _pendingGatedMethod = null;
        }

        _logger.LogInformation(
            "Activation no longer declares the pairing activity; discarding the pending gated attempt");
    }

    /// <summary>
    /// Begins a pairing code attempt by sending client/pair-init. For dynamic pairing code it includes
    /// commit_B over a fresh nonce_B. Any gesture gating has already been satisfied by
    /// <see cref="BeginOrDeferPairingCodeAttempt"/>, which consumed the pairing window.
    /// </summary>
    private void StartPairingCodeAttempt(bool dynamic)
    {
        var method = dynamic ? "dynamic_pin" : "static_pin";
        var state = new PairingCodeState { Dynamic = dynamic, Method = method };
        var init = new ClientPairInitMessage
        {
            Payload = new ClientPairInitPayload { PairingIndex = _pairingCounter },
        };
        if (dynamic)
        {
            state.NonceB = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            init.Payload.CommitB = Base64UrlText.Encode(PairingCodes.CommitB(state.NonceB));
        }

        _pairingCodeState = state;
        _logger.LogInformation("pairing code ({Method}): starting attempt", method);
        SendAsync(init).SafeFireAndForget(_logger);
        ArmAttemptTimeout();
    }

    /// <summary>
    /// Starts the attempt timeout. Called from the attempt's first message — client/pair-init
    /// for the pairing code flows, client/pair-finalize for Pairing PSK.
    /// </summary>
    private void ArmAttemptTimeout()
    {
        CancellationTokenSource cts;
        lock (_attemptLock)
        {
            _attemptTimeoutCts?.Cancel();
            _attemptTimeoutCts?.Dispose();
            cts = new CancellationTokenSource();
            _attemptTimeoutCts = cts;
        }

        _ = Task.Delay(_attemptTimeout, cts.Token).ContinueWith(
            t =>
            {
                if (t.IsCanceled)
                {
                    return;
                }

                // The delay can complete just as the attempt ends on another thread, which
                // cancels and replaces this source. Identity, not cancellation, is what says
                // whether the attempt this timer bounds is still the current one.
                lock (_attemptLock)
                {
                    if (!ReferenceEquals(_attemptTimeoutCts, cts))
                    {
                        return;
                    }
                }

                _logger.LogWarning("Pairing attempt timed out; aborting");
                AbortPairingCode("attempt_timeout");
                _pairingWindow?.Close();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void HandleServerPairInit(string json)
    {
        var msg = MessageSerializer.Deserialize<ServerPairInitMessage>(json);
        if (msg is null || _pairingCodeState is not { Dynamic: true } state)
            return;

        state.NonceA = Base64UrlText.Decode(msg.Payload.NonceA);
        var h = _session.HandshakeHash!.Value.ToArray();
        string pin = PairingCodes.DerivePairingCode(h, state.NonceA, state.NonceB!, _activationPairingCodeLength);
        state.PairingCode = pin;

        // Present the pairing code through the app's out-channel. Started here (this method runs on
        // the connection's synchronous receive dispatch, which cannot await); its completion
        // gates client/pair-auth in SendPairAuthAfterPairingCodePresentedAsync, and its token is
        // cancelled by ClearPairingCodeState when the attempt or the connection is torn down.
        state.PresentPairingCodeCts = new CancellationTokenSource();
        state.PairingCodePresented = InvokePairingCodePresenterAsync(pin, state.PresentPairingCodeCts.Token);
        // The PAKE begins when server/pair-auth arrives (server has the pairing code by then).
    }

    /// <summary>
    /// Invokes the app's <see cref="SendspinClientOptions.PresentPairingCodeAsync"/> presenter.
    /// Wrapped so a synchronously-throwing presenter faults the stored task — handled where
    /// the presentation is awaited — instead of throwing into the receive dispatch, whose
    /// catch filter treats exceptions as hostile peer input.
    /// </summary>
    private async Task InvokePairingCodePresenterAsync(string pin, CancellationToken cancellationToken)
    {
        // Non-null on every path that reaches a dynamic pair-init: CanOffer refuses
        // dynamic_pin without a presenter, and without StartPairingCodeAttempt(dynamic: true)
        // there is no { Dynamic: true } state for HandleServerPairInit to act on.
        await _presentPairingCodeAsync!(new PairingCodePresentation(pin, _activationLanguages), cancellationToken);
    }

    private void HandleServerPairAuth(string json)
    {
        var msg = MessageSerializer.Deserialize<ServerPairAuthMessage>(json);
        if (msg is null || _pairingCodeState is not { } state)
            return;

        // A server/pair-auth that arrives before server/pair-init leaves the dynamic pairing code
        // underived. The spec calls a mis-sequenced pairing message a protocol error, and the
        // dispatch catch turns a JsonException into exactly that close. Left unchecked this
        // reached Encoding.ASCII.GetBytes(null) and threw ArgumentNullException, which the catch
        // filter does not name — so it escaped to the receive loop as an unexplained lost
        // connection rather than a deliberate one (#106).
        if (state.Dynamic && state.PairingCode is null)
        {
            throw new System.Text.Json.JsonException(
                "server/pair-auth arrived before server/pair-init; no dynamic pairing code has been derived");
        }

        // Static pairing code: the pairing code is device-printed and known from the start.
        string pin = state.Dynamic ? state.PairingCode! : (_effectiveStaticPairingCode ?? string.Empty);
        var h = _session.HandshakeHash!.Value.ToArray();
        byte[] sid = PairingCodes.BuildSid(h, (uint)_pairingCounter);

        var cpace = CPace.Start(
            CPaceRole.Responder,
            System.Text.Encoding.ASCII.GetBytes(pin),
            sid,
            ad: PairingCodes.AdClient);
        state.CPace = cpace;
        state.Sid = sid;

        // Derive stays on the synchronous path: a hostile pake_msg_1 raises CPaceException
        // into the dispatch catch, which closes the connection as with any malformed input.
        cpace.Derive(Base64UrlText.Decode(msg.Payload.PakeMsg1), PairingCodes.AdServer);

        SendPairAuthAfterPairingCodePresentedAsync(state, cpace.PublicShare).SafeFireAndForget(_logger);
    }

    /// <summary>
    /// Sends client/pair-auth once the pairing code presentation has completed. The share itself
    /// leaks nothing (CPace), but the reply must not leave this client before the operator
    /// could have seen the pairing code — a presenter that has not finished displaying it cannot have
    /// had its pairing code entered — so a slow presenter delays the PAKE rather than racing it. For
    /// static pairing code (no presentation) this completes synchronously, as before. The presentation
    /// itself is awaited and its failure handled here; the fire-and-forget boundary at the
    /// call site observes only the send, exactly as it did when the send was unconditional.
    /// </summary>
    private async Task SendPairAuthAfterPairingCodePresentedAsync(PairingCodeState state, byte[] publicShare)
    {
        if (state.PairingCodePresented is { } presented)
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
                // Otherwise the app could not present the pairing code, so the operator can never
                // enter it: fail closed with the reason list's client-side-gave-up value
                // rather than completing a PAKE nobody can win.
                if (ReferenceEquals(_pairingCodeState, state))
                {
                    if (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "PresentPairingCodeAsync failed; aborting the pairing code attempt");
                    }

                    AbortPairingCode("user_cancelled");
                }

                return;
            }
        }

        // The attempt may have been superseded or aborted while the presentation was
        // pending; a stale share must not be sent into whatever replaced it.
        if (!ReferenceEquals(_pairingCodeState, state))
        {
            return;
        }

        await SendAsync(new ClientPairAuthMessage
        {
            Payload = new ClientPairAuthPayload { PakeMsg2 = Base64UrlText.Encode(publicShare) },
        });
    }

    private void HandleServerPairConfirm(string json)
    {
        var msg = MessageSerializer.Deserialize<ServerPairConfirmMessage>(json);
        if (msg is null || _pairingCodeState is not { CPace: { } cpace } state)
            return;

        if (!cpace.Verify(Base64UrlText.Decode(msg.Payload.ServerKc)))
        {
            RecordPairingCodeFailure(state.Method);
            AbortPairingCode("pin_mismatch");
            return;
        }

        // Send client/pair-confirm then client/pair-finalize (wrapped PSK) back-to-back.
        var confirm = new ClientPairConfirmMessage
        {
            Payload = new ClientPairConfirmPayload { ClientKc = Base64UrlText.Encode(cpace.Tag()) },
        };
        if (state.Dynamic)
        {
            confirm.Payload.NonceB = Base64UrlText.Encode(state.NonceB!);
        }

        SendAsync(confirm).SafeFireAndForget(_logger);

        byte[] psk = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        _pendingPairingPsk = psk;
        var suite = NoiseCipherSuite.ChaChaPoly;
        byte[] wrapped = PairingCodes.WrapPsk(state.Sid!, cpace.Isk, psk, suite);
        SendAsync(new ClientPairFinalizeMessage
        {
            Payload = new ClientPairFinalizePayload { WrappedPsk = Base64UrlText.Encode(wrapped) },
        }).SafeFireAndForget(_logger);

        // Success resets the method's failure counter.
        _pairingCodeLockoutStore?.SetFailures(state.Method, 0);
    }

    private void AbortPairingCode(string reason)
    {
        ClearPairingCodeState();
        SendAsync(new PairAbortMessage
        {
            Payload = new PairAbortPayload { Reason = reason },
        }).SafeFireAndForget(_logger);
    }

    /// <summary>
    /// Whether the method's failure counter has reached the spec's escalation threshold. An
    /// escalated method stays offered and still runs; every attempt is gesture-gated until a
    /// successful server_kc verification resets the counter.
    /// </summary>
    private bool IsMethodEscalated(string method)
        => (_pairingCodeLockoutStore?.GetFailures(method) ?? 0) >= 10;

    private void RecordPairingCodeFailure(string method)
    {
        if (_pairingCodeLockoutStore is null)
            return;
        _pairingCodeLockoutStore.SetFailures(method, _pairingCodeLockoutStore.GetFailures(method) + 1);
    }

    private sealed class PairingCodeState
    {
        public bool Dynamic;
        public string Method = string.Empty;
        public byte[]? NonceA;
        public byte[]? NonceB;
        public string? PairingCode;
        public byte[]? Sid;
        public CPace? CPace;

        // Set for a dynamic attempt when server/pair-init arrives: the app's pairing code
        // presentation, awaited before client/pair-auth is sent, and the cancellation
        // fired when the attempt or connection is torn down.
        public Task? PairingCodePresented;
        public CancellationTokenSource? PresentPairingCodeCts;
    }

    /// <summary>
    /// Drops the in-flight pairing attempt, if any, and cancels its pending pairing code presentation,
    /// so a presenter still holding the pairing code (dialog, speaker) is released when the attempt
    /// is aborted, superseded, or the connection or client goes away.
    /// </summary>
    /// <remarks>
    /// This clears the whole attempt, not just its pairing code half. <see cref="_pendingPairingPsk"/>
    /// belongs here because <see cref="HandleServerPairFinalize"/>'s only gate is "that field
    /// is not null" — no activity, trust or session check — so an attempt this method ends
    /// (an abort, an attempt_timeout, a re-key) that left the PSK armed would still persist a
    /// permanent record on a later bare server/pair-finalize. That is the same reasoning
    /// <see cref="HandlePairAbort"/> already applied to the abort path alone.
    /// </remarks>
    private void ClearPairingCodeState()
    {
        var state = _pairingCodeState;
        _pairingCodeState = null;

        // Dropped, deliberately not zeroized. PairingRecord holds its Psk as a
        // ReadOnlyMemory<byte> over the caller's array rather than a copy, and
        // HandleServerPairFinalize captures this field, calls this method, and only then
        // builds the record — so clearing the array here would persist 32 zero bytes as the
        // long-term PSK. The buffer is unreachable after this either way (#102).
        _pendingPairingPsk = null;

        // Read only inside an attempt, and every attempt re-reads them from its activation —
        // but they are cleared with the rest of the attempt state so no read can ever see a
        // value from an attempt that has already ended.
        _activationPairingCodeLength = 0;
        _activationLanguages = null;

        // Clears the attempt's derived secrets (ISK, confirmation MAC key, or the unused
        // scalar if it never got that far). Every ending routes through here — success,
        // abort, attempt timeout, supersession, disconnect, disposal — which is why the
        // zeroization hangs off this method rather than the success path (#102).
        state?.CPace?.Dispose();

        if (state?.PresentPairingCodeCts is { } cts)
        {
            cts.Cancel();
            cts.Dispose();
        }

        lock (_attemptLock)
        {
            _pendingGatedMethod = null;
            _attemptTimeoutCts?.Cancel();
            _attemptTimeoutCts?.Dispose();
            _attemptTimeoutCts = null;
        }
    }

    /// <summary>
    /// The server persisted the pairing record; persist ours. The server will follow
    /// with an in-band re-handshake to the new PSK (handled by the Noise framing).
    /// </summary>
    private void HandleServerPairFinalize()
    {
        // Captured before the clear below, which ends the attempt and with it the field.
        if (_pendingPairingPsk is not { } psk)
        {
            _logger.LogWarning("server/pair-finalize with no pairing attempt in flight; ignoring");
            return;
        }

        // The attempt succeeded: disarm its timeout so a completed attempt cannot abort itself
        // afterwards, and release any pairing code presentation still held for it.
        ClearPairingCodeState();

        // One success path, and it is the one that actually persisted. Every early return
        // below leaves PairingCompleted unraised, because a client that stored nothing cannot
        // authenticate this server on the next connection -- announcing success would tell the
        // app the opposite of what is true.
        if (_pairingStore is null || ServerId is null)
        {
            // Unreachable for a null store: CanOffer gates all three methods on one (#158).
            // Kept as a guard rather than an assertion because ServerId comes from the Noise
            // session and a degenerate peer could still leave it unset here.
            _logger.LogError(
                "Pairing complete but it cannot be persisted (store configured: {HasStore}, "
                + "server id known: {HasServerId}); record NOT persisted",
                _pairingStore is not null,
                ServerId is not null);
            return;
        }

        bool stored;
        lock (_pairingStoreLock)
        {
            stored = _pairingStore.Upsert(new PairingRecord(psk, PskCategory.LongTerm, ServerId));
        }

        if (!stored)
        {
            // The store is full: nothing was persisted, so this client cannot authenticate
            // the server on a future connection.
            _logger.LogError(
                "Pairing complete but the record store is full; record NOT persisted for {ServerId}",
                ServerId);
            return;
        }

        _logger.LogInformation("Pairing complete: long-term record persisted for {ServerId}", ServerId);
        PairingCompleted?.Invoke(this, ServerId);
    }

    /// <summary>
    /// Handles a management/* request. Management is scoped to connections whose
    /// current activities include 'management'; outside that, every request answers
    /// permission_denied.
    /// </summary>
    /// <remarks>
    /// Every request is answered by exactly one management/result, with no exceptions — which
    /// management.md requires of all management/* requests, and which this handler did not do
    /// until #132: a wrong-kind field (e.g. "psk":123, or a non-boolean enabled) threw
    /// InvalidOperationException past the filter below and the dispatch catch closed the
    /// connection with no result at all. The management reads are kind-checked now and raise
    /// JsonException instead, so a malformed payload is answered 'invalid' as the spec defines.
    /// See ReadBool/ReadString/ReadInt32 for why the fix is there rather than in this filter.
    /// </remarks>
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

        SendAsync(new ManagementResultMessage { Payload = result })
            .SafeFireAndForget(_logger);

        if (result.Result == "ok" && _pendingSelfRemoval)
        {
            // Removing the requester's own record closes the management session.
            _pendingSelfRemoval = false;
            DisconnectAsync("unauthorized").SafeFireAndForget(_logger);
        }
    }

    /// <summary>
    /// Reads a management payload field of an expected JSON kind, so a peer that sends the
    /// wrong kind is answered rather than disconnected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="System.Text.Json.JsonElement"/>'s typed getters throw
    /// <see cref="InvalidOperationException"/> on a kind mismatch, and the dispatch catch
    /// treats that as a malformed message and closes the connection. That is right for the
    /// rest of the protocol and wrong here: management.md opens by saying <b>all</b>
    /// <c>management/*</c> requests are answered by a single <c>management/result</c>, and
    /// defines <c>invalid</c> as covering a malformed payload — so a wrong-kind field owes the
    /// server an answer, not a dropped socket (#132).
    /// </para>
    /// <para>
    /// These throw <see cref="System.Text.Json.JsonException"/>, which
    /// <see cref="HandleManagement"/>'s own filter already turns into that answer. Widening
    /// that filter to <see cref="InvalidOperationException"/> instead would have been one line,
    /// but it would also swallow a genuine invalid-operation bug in our own handling and report
    /// it to the server as the peer's fault. Narrowing the read keeps the two distinguishable.
    /// </para>
    /// </remarks>
    private static bool ReadBool(System.Text.Json.JsonElement element, string field) => element.ValueKind switch
    {
        System.Text.Json.JsonValueKind.True => true,
        System.Text.Json.JsonValueKind.False => false,
        _ => throw new System.Text.Json.JsonException($"{field} must be a boolean, got {element.ValueKind}"),
    };

    /// <inheritdoc cref="ReadBool"/>
    private static string ReadString(System.Text.Json.JsonElement element, string field) =>
        element.ValueKind == System.Text.Json.JsonValueKind.String
            ? element.GetString()!
            : throw new System.Text.Json.JsonException($"{field} must be a string, got {element.ValueKind}");

    /// <inheritdoc cref="ReadBool"/>
    private static int ReadInt32(System.Text.Json.JsonElement element, string field) =>
        element.ValueKind == System.Text.Json.JsonValueKind.Number && element.TryGetInt32(out int value)
            ? value
            : throw new System.Text.Json.JsonException($"{field} must be an integer, got {element.ValueKind}");

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
                        ReadString(payload.GetProperty("psk"), "psk"));
                }
                catch (FormatException ex)
                {
                    // The decoder's message is the only thing that distinguishes a malformed
                    // PSK from a malformed peer id here -- management/result carries "invalid"
                    // either way. Swallowing it left DecodePsk (#105) emitting a better message
                    // that reached nobody, and the call site unpinnable by any test (#110).
                    _logger.LogWarning("Rejecting management/add-record: {Reason}", ex.Message);
                    result.Result = "invalid";
                    break;
                }

                // An explicit JSON null keeps meaning "no server id" — a shared-PSK record —
                // exactly as an absent field does; only a wrong kind is rejected.
                string? serverId =
                    payload.TryGetProperty("server_id", out var sid)
                    && sid.ValueKind != System.Text.Json.JsonValueKind.Null
                        ? ReadString(sid, "server_id")
                        : null;
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

                // The Sentinel PSK is a published constant, and RecordPskResolver searches records
                // before falling back to it — so a record holding it would shadow Sentinel resolution
                // and admit every anonymous peer at trust 'user'. The store query below covers records
                // (including the Pairing record); this covers the candidate that is not in the store.
                if (NoiseConstants.DerivePskId(psk) == NoiseConstants.SentinelPskId)
                {
                    result.Result = "already_exists";
                    break;
                }

                lock (_pairingStoreLock)
                {
                    if (_pairingStore.List().Any(r => r.PskId == NoiseConstants.DerivePskId(psk)))
                    {
                        result.Result = "already_exists";
                    }
                    else if (!_pairingStore.Upsert(new PairingRecord(psk, PskCategory.LongTerm, serverId)))
                    {
                        result.Result = "storage_exhausted";
                    }
                }

                break;
            }

            case MessageTypes.ManagementRemoveRecord:
            {
                string pskId = ReadString(payload.GetProperty("psk_id"), "psk_id");

                // A record referenced by record_mode.psk_id cannot be removed while the
                // reference exists (management.md:111); both halves of that constraint are
                // rejected as invalid.
                if (pskId == _recordModePskId)
                {
                    result.Result = "invalid";
                    break;
                }

                bool removed = false;
                bool removedPairing = false;
                if (_pairingStore is not null)
                {
                    lock (_pairingStoreLock)
                    {
                        var record = _pairingStore.List().FirstOrDefault(r => r.PskId == pskId);
                        if (record is not null)
                        {
                            _pairingStore.Remove(pskId);
                            removed = true;
                            removedPairing = record.Category == PskCategory.Pairing;
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

                if (removedPairing)
                {
                    // The server just invalidated any token EnsurePairingPsk handed out (the
                    // next call mints a fresh PSK), which is the same staleness
                    // set-pairing-config's psk replacement causes — report it the same way.
                    // Raised outside _pairingStoreLock for the same reason as there:
                    // subscribers run arbitrary app code and must not run under the lock.
                    PairingConfigChanged?.Invoke(this, CurrentPairingConfig(pairingPskReplaced: true));
                }

                break;
            }

            case MessageTypes.ManagementGetPairingConfig:
            {
                PairingMethodState? staticPairingCodeState = IsMethodImplemented("static_pin")
                    ? new PairingMethodState(CanRun("static_pin"))
                    : null;
                DynamicPairingCodeConfigState? dynamicPairingCodeState = IsMethodImplemented("dynamic_pin")
                    ? new DynamicPairingCodeConfigState(
                        CanRun("dynamic_pin"),
                        _effectiveMinPairingCodeLength,
                        IsMethodEscalated("dynamic_pin"))
                    : null;
                result.Data = System.Text.Json.JsonSerializer.SerializeToElement(
                    new PairingConfigData(
                        new PairingMethodState(IsMethodEnabled("pairing_psk")),
                        staticPairingCodeState,
                        dynamicPairingCodeState,
                        new RecordModeState(_recordModePskId),
                        new PairingMethodState(_unpairedAccessEnabled)),
                    MessageSerializerContext.Default.PairingConfigData);
                break;
            }

            case MessageTypes.ManagementSetPairingConfig:
            {
                // Patch semantics: only fields present are applied. Setting fields on an
                // unimplemented pairing code method returns invalid.

                // Parse every field before applying any, so a request refused partway
                // (no store, undecodable psk, unimplemented method, out-of-range value)
                // changes nothing, and the single change event below always describes a
                // fully applied request.
                bool? requestedUnpairedAccess = null;
                if (payload.TryGetProperty("unpaired_access", out var ua)
                    && ua.TryGetProperty("enabled", out var uaEnabled))
                {
                    requestedUnpairedAccess = ReadBool(uaEnabled, "unpaired_access.enabled");
                }

                // dynamic_pin. Parsed here with the other fields so a request refused partway
                // changes nothing and the single change event below always describes a fully
                // applied request.
                bool? requestedDynamicPairingCodeEnabled = null;
                int? requestedMinPairingCodeLength = null;
                if (payload.TryGetProperty("dynamic_pin", out var dp))
                {
                    if (!IsMethodImplemented("dynamic_pin"))
                    {
                        result.Result = "invalid";
                        break;
                    }

                    if (dp.TryGetProperty("enabled", out var dpEnabled))
                    {
                        requestedDynamicPairingCodeEnabled = ReadBool(dpEnabled, "dynamic_pin.enabled");
                    }

                    if (dp.TryGetProperty("min_pin_length", out var minLen))
                    {
                        int value = ReadInt32(minLen, "dynamic_pin.min_pin_length");
                        if (value < 4 || value > 12)
                        {
                            result.Result = "invalid";
                            break;
                        }

                        requestedMinPairingCodeLength = value;
                    }

                    // A server can supply a missing pairing code but not a missing IPairingCodeLockoutStore or
                    // PresentPairingCodeAsync -- those are app configuration. Answering ok and then
                    // continuing to report enabled: false would leave the server unable to
                    // tell why its change did not take.
                    if (requestedDynamicPairingCodeEnabled == true
                        && (_pairingCodeLockoutStore is null || _presentPairingCodeAsync is null))
                    {
                        result.Result = "invalid";
                        break;
                    }
                }

                // static_pin. The spec fixes the static pairing code at 8 decimal digits (pairing.md:186) and
                // rejects enabling the method with no secret behind it (management.md:98).
                bool? requestedStaticPairingCodeEnabled = null;
                string? requestedStaticPairingCode = null;
                if (payload.TryGetProperty("static_pin", out var sp))
                {
                    if (!IsMethodImplemented("static_pin"))
                    {
                        result.Result = "invalid";
                        break;
                    }

                    if (sp.TryGetProperty("pin", out var pairingCodeEl))
                    {
                        string pairingCode = ReadString(pairingCodeEl, "static_pin.pin");
                        if (!IsValidStaticPairingCode(pairingCode))
                        {
                            result.Result = "invalid";
                            break;
                        }

                        requestedStaticPairingCode = pairingCode;
                    }

                    if (sp.TryGetProperty("enabled", out var spEnabled))
                    {
                        requestedStaticPairingCodeEnabled = ReadBool(spEnabled, "static_pin.enabled");
                    }

                    if (requestedStaticPairingCodeEnabled == true
                        && requestedStaticPairingCode is null
                        && !IsValidStaticPairingCode(_effectiveStaticPairingCode))
                    {
                        result.Result = "invalid";
                        break;
                    }

                    // A server can supply a missing static pairing code, but not a missing
                    // IPairingCodeLockoutStore -- that's app configuration. Answering ok and then
                    // continuing to report enabled: false would leave the server unable to
                    // tell why its change did not take.
                    if (requestedStaticPairingCodeEnabled == true && _pairingCodeLockoutStore is null)
                    {
                        result.Result = "invalid";
                        break;
                    }
                }

                byte[]? newPairingPsk = null;
                bool? requestedPairingPskEnabled = null;
                if (payload.TryGetProperty("pairing_psk", out var pp))
                {
                    if (pp.TryGetProperty("psk", out var pskEl))
                    {
                        if (_pairingStore is null)
                        {
                            result.Result = "storage_exhausted";
                            break;
                        }

                        newPairingPsk = Connection.Noise.SendspinIdentity.DecodePsk(ReadString(pskEl, "pairing_psk.psk"));

                        // A psk_id that already identifies a candidate in another category would make
                        // one id resolve to two trust levels at handshake time (management.md:98).
                        // Rotating to the value the Pairing record already holds is excluded from this
                        // check — that is a no-op re-rotation, not a conflict, so only the Sentinel PSK
                        // and stored records in a category other than Pairing count as collisions.
                        string newPskId = NoiseConstants.DerivePskId(newPairingPsk);
                        bool collides = newPskId == NoiseConstants.SentinelPskId;
                        if (!collides)
                        {
                            lock (_pairingStoreLock)
                            {
                                collides = _pairingStore.List()
                                    .Any(r => r.Category != PskCategory.Pairing && r.PskId == newPskId);
                            }
                        }

                        if (collides)
                        {
                            result.Result = "already_exists";
                            break;
                        }
                    }

                    if (pp.TryGetProperty("enabled", out var ppEnabled))
                    {
                        requestedPairingPskEnabled = ReadBool(ppEnabled, "pairing_psk.enabled");
                    }
                }

                // record_mode.psk_id names the shared-PSK record the client falls back to
                // when its stored-pubkey record space is exhausted. The spec constrains it
                // in both directions (management.md:111): it must reference a shared-PSK
                // record here, and that record is protected from removal for as long as the
                // reference exists (see ManagementRemoveRecord below).
                string? requestedRecordModePskId = null;
                if (payload.TryGetProperty("record_mode", out var rm)
                    && rm.TryGetProperty("psk_id", out var rmPskId))
                {
                    string target = ReadString(rmPskId, "record_mode.psk_id");
                    bool valid;
                    lock (_pairingStoreLock)
                    {
                        valid = _pairingStore?.List().Any(r => r.PskId == target && IsSharedPskRecord(r)) == true;
                    }

                    if (!valid)
                    {
                        result.Result = "invalid";
                        break;
                    }

                    requestedRecordModePskId = target;
                }

                // The spec permits the server to make these changes, so the SDK honours
                // them — against its own effective state, never the app's capabilities.
                //
                // The pairing_psk store write runs first, before any other field is applied:
                // it is the only fallible step in this section (a full store), and parse-before-
                // apply requires that a refusal changes nothing and raises no event. Applying it
                // after even one other field would leave that field's mutation stuck on a
                // request this handler ultimately refused.
                if (newPairingPsk is not null)
                {
                    lock (_pairingStoreLock)
                    {
                        // _pairingStore (readonly) was verified non-null when the psk parsed.
                        // Upsert before removing the old record: removing it first and then
                        // finding the store full would destroy the client's only Pairing PSK
                        // while answering storage_exhausted, and do so silently — the app keeps
                        // handing out a token for a record that no longer exists, with no
                        // PairingConfigChanged to say so. Upserting first means a refusal here
                        // leaves the old record intact.
                        //
                        // The cost: a new record has a different psk_id than the one it replaces
                        // (it is derived from the PSK), so this needs transient capacity for N+1
                        // records, not N. A legitimate rotation can therefore be refused on a
                        // store already at capacity, where remove-then-upsert would have
                        // succeeded. That trade is intentional — a full store genuinely cannot
                        // hold another record, storage_exhausted is an honest answer to that, and
                        // it is recoverable: the server can free a slot with management/
                        // remove-record and retry. See PairingPskOperations.Rotate for the
                        // opposite ordering and why it differs.
                        if (!_pairingStore!.Upsert(new PairingRecord(newPairingPsk, PskCategory.Pairing)))
                        {
                            result.Result = "storage_exhausted";
                            break;
                        }

                        string newPskId = NoiseConstants.DerivePskId(newPairingPsk);
                        foreach (var old in _pairingStore.List()
                            .Where(r => r.Category == PskCategory.Pairing && r.PskId != newPskId))
                        {
                            _pairingStore.Remove(old.PskId);
                        }
                    }
                }

                bool unpairedAccessChanged = false;
                if (requestedUnpairedAccess is { } enabled)
                {
                    unpairedAccessChanged = enabled != _unpairedAccessEnabled;
                    _unpairedAccessEnabled = enabled;
                }

                bool dynamicPairingCodeChanged = false;
                if (requestedDynamicPairingCodeEnabled is { } dpe)
                {
                    dynamicPairingCodeChanged |= dpe != _dynamicPairingCodeEnabled;
                    _dynamicPairingCodeEnabled = dpe;
                }

                if (requestedMinPairingCodeLength is { } minPairingCode)
                {
                    dynamicPairingCodeChanged |= minPairingCode != _effectiveMinPairingCodeLength;
                    _effectiveMinPairingCodeLength = minPairingCode;
                }

                bool staticPairingCodeChanged = false;
                if (requestedStaticPairingCode is not null)
                {
                    staticPairingCodeChanged |= requestedStaticPairingCode != _effectiveStaticPairingCode;
                    _effectiveStaticPairingCode = requestedStaticPairingCode;

                    // The spec's "when the secret is rotated, the client updates the hint
                    // accordingly": the operator chose this pairing code, so whatever the app declared
                    // about a printed label no longer describes where to find it, and a server
                    // still rendering "check the device" would send them to a stale number
                    // (#129). Applied on every set, not only when the value differs — a server
                    // re-sending the pairing code it already set is still the operator owning it.
                    staticPairingCodeChanged |= SetLocationsToOperator(ref _staticPairingCodeLocations);
                }

                if (requestedStaticPairingCodeEnabled is { } spe)
                {
                    staticPairingCodeChanged |= spe != _staticPairingCodeEnabled;
                    _staticPairingCodeEnabled = spe;
                }

                bool pairingPskEnabledChanged = false;
                if (requestedPairingPskEnabled is { } ppe)
                {
                    pairingPskEnabledChanged = ppe != _pairingPskEnabled;
                    _pairingPskEnabled = ppe;
                }

                // Same rule for the Pairing PSK, and only here: a PSK this client minted for
                // itself (EnsurePairingPsk/RotatePairingPsk) is still found wherever the app
                // renders it, so those paths deliberately leave the hint alone.
                if (newPairingPsk is not null)
                {
                    pairingPskEnabledChanged |= SetLocationsToOperator(ref _pairingPskLocations);
                }

                bool recordModeChanged = false;
                if (requestedRecordModePskId is not null)
                {
                    recordModeChanged = requestedRecordModePskId != _recordModePskId;
                    _recordModePskId = requestedRecordModePskId;
                }

                if (unpairedAccessChanged || dynamicPairingCodeChanged || staticPairingCodeChanged || newPairingPsk is not null
                    || pairingPskEnabledChanged || recordModeChanged)
                {
                    // One event per request, after every change is applied, and outside
                    // _pairingStoreLock: subscribers run arbitrary app code, and raising
                    // under the lock would let that code block against other threads
                    // contending for it (same-thread re-entry is safe; cross-thread
                    // waits under the lock are the deadlock hazard).
                    PairingConfigChanged?.Invoke(this, CurrentPairingConfig(pairingPskReplaced: newPairingPsk is not null));
                }

                break;
            }

            case MessageTypes.ManagementOpenPairingWindow:
            {
                // Opens the window in place of the operator gesture. Rejected when no pairing code
                // method is enabled, since there would be nothing for the window to admit.
                bool anyPairingCodeMethod = CanRun("static_pin") || CanRun("dynamic_pin");
                if (!anyPairingCodeMethod || _pairingWindow is null)
                {
                    result.Result = "invalid";
                    break;
                }

                // A no-op ok when a window is already open.
                _pairingWindow.Open();
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
        ClearPairingCodeState();
    }

    /// <summary>
    /// The connected tail of the handshake: runs when the initial server/activate arrives,
    /// which is the point the encrypted handshake completes and the client may start sending.
    /// </summary>
    /// <param name="pairing">Whether the activate completing the handshake declares the
    /// pairing activity. A pairing activation admits nothing but pairing messages onto the
    /// wire, so the initial client/state is then withheld — even for roles that need no
    /// clock sync — until the first non-pairing activate (see
    /// <see cref="_initialClientStateHeldForPairing"/>).</param>
    private void FinishHandshake(bool pairing)
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

        // Per-connection latches, reset here with the rest of the per-connection state: the
        // initial client/state must be sent again, and sync must be re-established before
        // this connection may claim availability (the synchronizer was reset above, so for a
        // clock that reports unconverged after reset the two now agree).
        _initialClientStateSent = false;
        _hasConvergedOnce = false;
        _initialClientStateHeldForPairing = pairing;

        // When the connection's first activate is the pairing one, the send-or-defer
        // decision is withheld wholesale: a non-sync role's initial client/state would
        // otherwise go out right here, into a server that admits nothing but pairing
        // messages during the attempt — poisoning the exchange exactly the way client/time
        // probes did. The first non-pairing activate runs the decision instead.
        if (!pairing)
        {
            SendOrDeferInitialClientState();
        }

        // The time-sync loop — which produces the convergence a deferred initial state
        // waits for — is started by the caller, HandleServerActivate, not here: it runs
        // only outside a pairing activation, and only the caller knows the activate's
        // activities.
    }

    /// <summary>
    /// The sending side of <see cref="FinishHandshake"/>: sends the connection's initial
    /// client/state now, or defers it to the first convergence for sync-requiring roles.
    /// Runs from <see cref="FinishHandshake"/> on a normal connection; on one whose first
    /// activate was a pairing activate it runs from the first non-pairing activate instead
    /// (see <see cref="_initialClientStateHeldForPairing"/>).
    /// </summary>
    private void SendOrDeferInitialClientState()
    {
        // The spec lets a player report available: true only once clock sync is established, so
        // sync-requiring roles defer the initial client/state until the first convergence (see
        // ApplyBestSample). Deliberately NOT sent as available: false in the meantime: the
        // server moves an unavailable client into a solo group and MUST NOT auto-rejoin it, so
        // a false during a routine reconnect would permanently drop the client from its group.
        // Roles without player/source need no clock — for them available alone unlocks the
        // server's streams, so their initial state goes out at once.
        if (RequiresClockSync() && !IsClockSynced)
        {
            _logger.LogInformation("Deferring initial client/state until clock sync converges");
        }
        else
        {
            SendInitialClientStateAsync().SafeFireAndForget(_logger);
        }
    }

    /// <summary>
    /// Sends the initial client/state message: on activate for clients that need no clock sync,
    /// on the first convergence for those that do (see <see cref="FinishHandshake"/>), or
    /// promoted from <see cref="PublishAvailabilityAsync"/> when an availability input flips
    /// inside the converging window. Reports <see cref="CurrentAvailability"/> — not an asserted
    /// <c>true</c> — so a reconnect while the output is held by an external source (or a
    /// pipeline error is outstanding) does not invite the server to stream into an occupied
    /// output. Uses the current <see cref="_playerState"/> which was initialized from
    /// ClientCapabilities. Failures propagate: the fire-and-forget call sites log them via
    /// <c>SafeFireAndForget</c>, and the promoted path must throw into
    /// <see cref="EnterExternalSourceAsync"/>/<see cref="ExitExternalSourceAsync"/> so their
    /// notify-first rollback still runs.
    /// </summary>
    private async Task SendInitialClientStateAsync()
    {
        // Latched before the send: once per connection, even if a re-convergence races a
        // send still in flight. A send that fails here is corrected by the next reconnect,
        // which resets the latch with the rest of the per-connection state.
        _initialClientStateSent = true;

        // Role objects follow active_roles, not capabilities. A player object used to go out
        // unconditionally, so a source-only or artwork-only client reported player state for a
        // role the server never activated — the deviation aiosendspin rejects when strict. The
        // source object was never built at all, which is why an in-window line-sense signal had
        // nowhere to go (#114).
        bool available = CurrentAvailability;
        var stateMessage = ClientStateMessage.CreateInitial(
            available: available,
            player: MayReportRoleState("player")
                ? new PlayerStatePayload
                {
                    Volume = _playerState.Volume,
                    Muted = _playerState.Muted,
                    StaticDelayMs = ToWireStaticDelayMs(_clockSynchronizer.StaticDelayMs),
                    RequiredLeadTimeMs = _requiredLeadTimeMs,
                    MinBufferMs = _minBufferMs,
                    SupportedCommands = GetPlayerSupportedCommands(),
                }
                : null,
            source: BuildSourceState());

        // Seed the availability publisher's "last sent" tracker from the value this message
        // carries, so the first delta PublishAvailabilityAsync sends afterward is neither a
        // spurious repeat nor a swallowed change. Seeded before the send, not after: written
        // after the await, it would overwrite — with a by-then stale value — a tracker that a
        // delta publishing while this send was in flight has already advanced, and the next
        // genuine change would be suppressed as a repeat while the server believes otherwise.
        _lastAvailabilitySent = available;

        var stateJson = MessageSerializer.Serialize(stateMessage);
        _logger.LogInformation("Sending initial client/state:\n{Json}", stateJson);
        await SendAsync(stateMessage);

        // Also apply to audio pipeline to ensure consistency
        _audioPipeline?.SetVolume(_playerState.Volume);
        _audioPipeline?.SetMuted(_playerState.Muted);
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
            // Deliberately broad, reviewed under #109, and the backstop the burst's narrowed
            // filter propagates into. Two reasons to leave it wide. GetAdaptiveTimeSyncIntervalMs
            // calls IClockSynchronizer.GetStatus(), and that is an embedder-supplied interface
            // with no closed set of failure types. And this is the outermost frame of a loop
            // started by SafeFireAndForget, so narrowing would not make anything louder — it
            // would only trade this line for the generic "fire-and-forget task failed", losing
            // the one thing worth knowing, which is that the loop is now dead and this
            // connection's clock will drift from here on.
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
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException
            or System.Net.WebSockets.WebSocketException or System.IO.IOException
            or System.Net.Sockets.SocketException or TimeoutException)
        {
            // Narrowed from catch-all (#109). Everything inside the try is our own send path —
            // SendSingleProbeAsync and Task.Delay — so unlike the loop that calls this, the set
            // here is closed: a socket dying mid-write, a disposed connection, or a send onto
            // one that is no longer Connected. Those are worth tolerating, because the loop
            // treats a returning burst as "try again next interval" and a transient write
            // failure must not cost the connection its clock sync.
            //
            // Anything else — a serialization fault, a null deref in the probe code — is a bug
            // in that path, and retrying it every interval forever buries it under a warning
            // per burst while the client silently never converges (a player deferring its
            // initial client/state on IsClockSynced then never reports available). It
            // propagates to TimeSyncLoopAsync's guard instead, which ends the loop and logs it
            // once. That guard stays broad deliberately — see its comment.
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
            await SendAsync(timeMessage, cancellationToken).ConfigureAwait(false);
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

        if (_clockSynchronizer.IsConverged)
        {
            // Establishes ClockSyncEstablished for this connection — and keeps it established
            // across later convergence dips. Set before the transition handling below, so the
            // sends it triggers compose availability with sync already established.
            _hasConvergedOnce = true;
        }

        if (!wasConverged && _clockSynchronizer.IsConverged)
        {
            _logger.LogInformation("[ClockSync] Converged after {Count} measurements", status.MeasurementCount);
            ClockSyncConverged?.Invoke(this, status);

            if (!_initialClientStateSent)
            {
                // First convergence on this connection: release the initial client/state that
                // FinishHandshake deferred. Later re-convergences take the delta path below.
                SendInitialClientStateAsync().SafeFireAndForget(_logger);
            }
            else
            {
                // The initial state went out before this connection's first convergence — a
                // genuine false (external source, pipeline error) promoted it inside the
                // converging window. If that condition has since cleared, the recovery's
                // available: true has been withheld pending sync establishment, and this is
                // where it is released. On a mid-session re-convergence the latch was already
                // set, so the compare-to-last-sent makes this a no-op.
                PublishAvailabilityAsync().SafeFireAndForget(_logger);
            }
        }
        else if (wasConverged && !_clockSynchronizer.IsConverged)
        {
            // Worth an operator's attention, but deliberately kept off the wire: convergence
            // is a statistical threshold that oscillates under routine RTT jitter, and
            // playback carries on regardless (the pipeline gates on minimal sync, not
            // convergence). Availability composes the per-connection ClockSyncEstablished
            // latch rather than this live statistic — publishing available: false here told
            // the server a still-playing client had left playback, and the server moves an
            // unavailable client to a solo group it MUST NOT auto-rejoin, so one RTT spike
            // permanently ejected a speaker from its group.
            _logger.LogWarning("[ClockSync] Convergence lost after {Count} measurements", status.MeasurementCount);
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
            // Published before the fire-and-forget so a test can await this command instead of
            // guessing a timeout; see LastSourceCommandTask (#135).
            var dispatched = _sourcePipeline.HandleCommandAsync(sourceCommand.Command);
            Volatile.Write(ref _lastSourceCommandTask, dispatched);
            dispatched.SafeFireAndForget(_logger);
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
    /// Re-reports the full client state after a pairing activation ends, restoring the server's
    /// view of anything the window dropped.
    /// </summary>
    /// <remarks>
    /// Deliberately the full state rather than a delta: the client cannot know which of its
    /// values the server last saw, because it does not track what the gate discarded. Resending
    /// unchanged fields is explicitly permitted — "A client MAY instead resend unchanged fields,
    /// up to its full state" — so the complete picture is both correct and the only thing that
    /// is knowably correct here.
    /// </remarks>
    private async Task ResendClientStateAfterPairingAsync()
    {
        if (_connection.State != ConnectionState.Connected)
        {
            return;
        }

        await SendAsync(ClientStateMessage.CreateInitial(
            available: CurrentAvailability,
            player: MayReportRoleState("player")
                ? new PlayerStatePayload
                {
                    Volume = _playerState.Volume,
                    Muted = _playerState.Muted,
                    StaticDelayMs = ToWireStaticDelayMs(_clockSynchronizer.StaticDelayMs),
                    RequiredLeadTimeMs = _requiredLeadTimeMs,
                    MinBufferMs = _minBufferMs,
                    SupportedCommands = GetPlayerSupportedCommands(),
                }
                : null,
            source: BuildSourceState()));

        // Keep the availability publisher's tracker in step with what the server was just told,
        // so the next genuine change is neither a spurious repeat nor swallowed as one.
        lock (_availabilityLock)
        {
            _lastAvailabilitySent = CurrentAvailability;
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
            // Deliberately broad, reviewed under #109. IStaticDelayStore is implemented by the
            // embedder over a store the SDK never sees — file, registry, SQLite, a cloud
            // key-value API — so there is no set of types to narrow to; a filter naming
            // IOException would let a database provider's own exception abort the handshake.
            // The interface docs ask for a non-throwing implementation, which is exactly why
            // this exists: it is the guard for the implementations that are not. Degrading is
            // right here — a delay we could not read is a lost calibration, not a lost session,
            // and the handshake behind this call still has an initial client/state to send.
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
            // Deliberately broad for the same reason as LoadPersistedStaticDelay's catch (#109):
            // an embedder-implemented store has no enumerable failure set. Degrading is the
            // stronger answer on the save side, because the callers are a server/command and a
            // GroupSync offset — the delay is already applied in memory and already
            // acknowledged, so throwing here would fail a command that in fact succeeded.
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
            //
            // That leaves a deliberate asymmetry, reviewed under #106 and kept: a throwing
            // subscriber on this path is logged by SafeFireAndForget and the connection lives,
            // while one on a synchronous handler escapes into the receive loop and drops the
            // connection. Containing faults everywhere was considered and rejected — an
            // operator notices a player that stopped, and can miss a log line, so an
            // application bug staying loud is worth the inconsistency. Peer input is
            // unaffected either way: it closes the connection on both paths.
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
        if (LastServerActivate?.ActivitiesList.Contains(Activities.Pairing) == true)
        {
            // Same rule as the time-sync loop's gate in HandleServerActivate: no
            // client/time may leave the client while a pairing activation is in effect —
            // the reference server would read the probe where it requires the next pairing
            // message and abort the attempt. This burst is not the loop (its token is
            // CancellationToken.None, so StopTimeSyncLoop cannot reach it) and it fires
            // without app action, so it is gated at the source: a stream/start crossing a
            // mid-session pairing activate on a clock without minimal sync must stay
            // silent. The loop's restart on the next non-pairing activate covers the
            // re-sync this burst would have provided.
            _logger.LogDebug("Pairing activation in effect, skipping stream-start sync burst");
        }
        else if (!_clockSynchronizer.HasMinimalSync)
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

            // Media held for a display time that belongs to the stream just ended must not
            // surface after it.
            _displayScheduler.Flush();

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

        // "Clients should clear all buffered visualization data and continue with data received
        // after this message" — the same boundary applies to artwork still held for display.
        _displayScheduler.Flush();
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
                    _logger.LogDebug("Artwork on channel {Channel}: {Length} bytes @ {Timestamp}",
                        artwork.Channel, artwork.ImageData.Length, artwork.Timestamp);

                    // Held until the timestamp's local equivalent, or raised now if that has
                    // already passed — artwork is never dropped for lateness (#199).
                    _displayScheduler.SubmitArtwork(artwork);
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

                    // Held until the timestamp's local equivalent, and dropped outright if it
                    // is already too far past to render (#198).
                    _displayScheduler.SubmitVisualizerFrame(frame, data.Length);
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
    /// Synchronous dispose — stops the time-sync loop, clears pairing state, and unsubscribes
    /// connection events to break the reference cycle that would otherwise prevent GC.
    /// </summary>
    /// <remarks>
    /// <b>Does not close the connection.</b> Only <see cref="DisposeAsync"/> disposes the
    /// underlying <see cref="ISendspinConnection"/>, stops the audio pipeline, and disposes
    /// the source pipeline's capture device — all of which need to await. Use this overload
    /// only where the connection is owned and disposed elsewhere; a client from
    /// <see cref="CreateForDial"/> owns its connection exclusively, so disposing one of those
    /// synchronously leaves the socket open and the server expecting a reconnect (#96).
    /// </remarks>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopTimeSyncLoop();
        ClearPairingCodeState();
        UnsubscribeConnectionEvents();
        _displayScheduler.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        StopTimeSyncLoop();
        ClearPairingCodeState();
        UnsubscribeConnectionEvents();
        _displayScheduler.Dispose();

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

        if (_pairingWindow is not null)
        {
            _pairingWindow.StateChanged -= OnPairingWindowStateChanged;
        }
    }

    /// <summary>
    /// Reports <c>available: false</c> when the audio pipeline raises an error (e.g. a buffer
    /// underrun or sync failure), so the server knows this player cannot keep up. Per the spec the
    /// player then buffers and recovers once it can resume playback (see
    /// <see cref="OnPipelineStateChanged"/>). The latch and the publisher call are unconditional on
    /// every occurrence — the publisher's own compare-to-last-sent is what suppresses the
    /// resulting wire duplicates, so a second error while one is already outstanding is not
    /// silently dropped before it can be composed with other inputs (e.g. external source). Only
    /// the log line is gated on the latch's prior value, to keep once-per-episode logging for a
    /// sustained error.
    /// </summary>
    private void OnPipelineError(object? sender, AudioPipelineError error)
    {
        if (!_clientErrorReported)
        {
            _logger.LogWarning("Audio pipeline error; reporting available: false ({Message})", error.Message);
        }

        _clientErrorReported = true;
        PublishAvailabilityAsync().SafeFireAndForget(_logger);
    }

    /// <summary>
    /// Tracks pipeline state to drive the error -&gt; recovered transition: once the pipeline
    /// returns to <see cref="AudioPipelineState.Playing"/> after an error, report player state.
    /// The Error state itself is also reported here for pipelines that surface underruns via
    /// state changes rather than <see cref="OnPipelineError"/>.
    /// </summary>
    private void OnPipelineStateChanged(object? sender, AudioPipelineState state)
    {
        switch (state)
        {
            case AudioPipelineState.Error:
                if (!_clientErrorReported)
                {
                    _logger.LogWarning("Audio pipeline entered Error state; reporting available: false");
                }

                _clientErrorReported = true;
                PublishAvailabilityAsync().SafeFireAndForget(_logger);
                break;

            case AudioPipelineState.Playing when _clientErrorReported:
                _clientErrorReported = false;
                PublishAvailabilityAsync().SafeFireAndForget(_logger);

                // Guard on connection state: a recovery that lands while disconnected/reconnecting
                // would otherwise hit a closed socket. Reconnect corrects a report skipped here:
                // SendInitialClientStateAsync reports CurrentAvailability, which composes
                // _clientErrorReported/IsExternalSource back in.
                if (_connection.State == ConnectionState.Connected)
                {
                    _logger.LogInformation("Audio pipeline recovered; reporting player state");
                    SendPlayerStateAckAsync().SafeFireAndForget(_logger);
                }

                break;
        }
    }

    /// <summary>
    /// Builds a client that dials a server, wiring one <see cref="NoiseWireFraming"/> as
    /// both the connection's framing and the client's Noise session so the two cannot
    /// drift apart.
    /// </summary>
    /// <remarks>
    /// <b>Dispose the returned client with <c>await using</c>, not <c>using</c>.</b> This
    /// method constructs the <see cref="SendspinConnection"/> internally and the caller never
    /// receives a handle to it, so the client is the only thing that can close it — and only
    /// <see cref="DisposeAsync"/> does. Synchronous <see cref="Dispose"/> cannot: closing the
    /// socket means sending <c>client/goodbye</c> and awaiting the close, which a synchronous
    /// dispose has no way to do. The cost of getting it wrong is not just a leaked socket: a
    /// server that sees a client vanish without a goodbye is told to assume <c>restart</c> and
    /// keep reconnecting to an application that has exited (#96).
    /// </remarks>
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
