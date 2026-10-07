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

    // Reassembles the artwork image transfer in flight; a complete image goes to the scheduler.
    private readonly ArtworkTransfer _artworkTransfer = new();
    private readonly IAudioPipeline? _audioPipeline;
    private readonly IOutputDelayStore? _outputDelayStore;
    private readonly INoiseSessionInfo _session;
    private bool _activateReceived;

    // Whether this connection has received its server/hello. Per connection, unlike
    // LastServerHello, which keeps the previous connection's payload across a reconnect.
    private bool _serverHelloReceived;

    // True while the activation in effect declares 'pairing' without 'playback'. Gates every
    // send (see SendAsync): the pairing exchange then holds the wire alone (#118); alongside
    // playback it does not (pairing.md, "Entering and leaving pairing"). Cleared with the rest
    // of the per-connection state at handshake, so a reconnect never starts inside the window.
    private bool _pairingActivationActive;
    private readonly SourceStreamPipeline? _sourcePipeline;

    // Task of the most recently dispatched source command; see LastSourceCommandTask (#135).
    private Task _lastSourceCommandTask = Task.CompletedTask;
    private readonly IAudioCaptureDevice? _captureDevice;
    private readonly ISourceAudioEncoderFactory? _sourceEncoderFactory;
    private readonly IPairingRecordStore? _pairingStore;

    // Serializes this client's multi-step record-store sequences through the gate shared by
    // every SDK object using the same store instance. IPairingRecordStore promises that
    // "the SDK serializes access"; before EnsurePairingPsk/RotatePairingPsk every mutation
    // ran on the receive path, and now app threads mutate too. Never held across an await.
    // Boundary: RecordPskResolver.Resolve also reads the store, from the framing inbound
    // path — a separate public object this lock cannot reach — so an app-thread call can
    // still race an in-flight re-handshake's psk_id lookup. Every individual store
    // operation is safe after the store-level locking, so the worst case there is
    // nondeterminism, not corruption or lockout.
    private readonly object _pairingStoreLock;
    private readonly SendspinIdentity _identity;
    private bool _markedPskUsed;

    private byte[]? _pendingPairingPsk;
    private readonly IPairingCodeLockoutStore? _pairingCodeLockoutStore;
    private readonly Func<PairingCodePresentation, CancellationToken, ValueTask>? _presentPairingCodeAsync;
    private readonly PairingWindow? _pairingWindow;

    // Supplied by the host when this client is one of several sharing a record store, so an
    // eviction here cannot take out a record a sibling connection is authenticated by. Null
    // for a standalone client, which has no siblings.
    private readonly Func<IReadOnlyCollection<string>>? _liveRecordPskIds;

    private readonly TimeSpan _attemptTimeout;

    // Covers _attemptTimeoutCts and _pendingGatedMethod. Both are touched from the receive
    // loop AND from whatever thread raises PairingWindow.StateChanged — an operator gesture —
    // so neither is safe to
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

    // The active roles an in-band re-handshake set aside, for the server/activate that follows
    // it to persist or remove. Null outside that window.
    private List<string>? _activeRolesBeforeRekey;

    // format from the current pairing activation, validated on receipt. Null when the
    // activation is not dynamic_pairing_code.
    private string? _activationPairingCodeFormat;

    // languages advertised by the server in server/hello, handed to the pairing code presenter.
    // Null when the server sent none. Spec #178 moved this hint off the pairing activation
    // (where it was per-attempt) onto server/hello, so it is now connection-scoped.
    private List<string>? _serverLanguages;

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
    private int _lastReportedLeadTimeMs = -1;
    private int _lastReportedMinBufferMs = -1;

    // The effective unpaired-access setting, seeded from capabilities at construction. Held
    // here rather than read straight off the app-owned capabilities object so the admissibility
    // check and client/hello cannot drift apart.
    private readonly bool _unpairedAccessEnabled;

    // Effective pairing-method configuration, seeded from capabilities at construction. Pairing
    // config is local and manufacturer-defined: nothing on the wire changes it. client/hello
    // and CanOffer both read these.
    private readonly bool _pairingPskEnabled;
    private readonly bool _dynamicPairingCodeEnabled;
    private readonly bool _staticPairingCodeEnabled;
    private readonly string? _effectiveStaticPairingCode;

    // locations hints for the two methods that carry one. Copied from _capabilities rather than
    // aliased, so the SDK never writes to the list the app owns (#129).
    private readonly List<string> _staticPairingCodeLocations;
    private readonly List<string> _pairingPskLocations;

    // Guards the two pieces of role configuration that change during a connection: the artwork
    // channel declaration and the visualizer configuration. Both are written by the app calling
    // SetArtworkChannelAsync/SetVisualizerConfigurationAsync and read by the client/hello and
    // client/state builders, which run on the send and receive paths — different threads by
    // construction.
    private readonly object _roleConfigLock = new();

    // Serializes each client/state send from snapshot through enqueue. _roleConfigLock (above)
    // prevents a torn snapshot, but two concurrent Set*Async calls could still snapshot in one
    // order and hand their frames to the transport in the other, landing the older snapshot last
    // and overwriting the newer configuration on the server. SendClientStateAsync holds this from
    // the snapshot until SendAsync has been awaited, so the transport accepts frames in snapshot
    // order. Every client/state send routes through that method.
    private readonly SemaphoreSlim _clientStateSendGate = new(1, 1);

    // This client's effective artwork channel declaration, copied from the capabilities at
    // construction rather than aliased. ClientCapabilities belongs to the app, and a host shares
    // one instance across every connection it accepts: writing a connection's runtime
    // reconfiguration back through it would leak that connection's channels into its siblings
    // and race whatever state builder was reading the same list. Guarded by _roleConfigLock.
    private readonly List<ArtworkChannelState> _artworkChannels;

    // This client's effective visualizer configuration, seeded from the capabilities at
    // construction for the same reason and under the same lock. Replaced wholesale rather than
    // mutated, so a reader that has taken the reference sees a complete configuration.
    private VisualizerRoleSupport? _visualizerRoleSupport;

    // Bounds for a persisted output delay loaded from the store. The applied value is 0-5000 per
    // the spec's output_delay_ms and the clock synchronizer's setter is the single clamp site;
    // bounding a stored value here as well only keeps the logged value equal to the applied one.
    private const double MinOutputDelayMs = 0.0;
    private const double MaxOutputDelayMs = 5000.0;

    // Last line-sense signal the app reported, or null if it never has. Survives reconnects on
    // purpose: it describes the device's input, not the session (#114).
    private bool? _lastSourceSignal;

    // The player's current format preference, reported in the client/state player object since
    // spec PR #195 removed stream/request-format. Null means no override: the server selects per
    // the supported_formats priority order. Survives reconnects, so the preference an app set
    // once is re-reported by the next connection's initial state rather than silently reverting.
    private PlayerFormatPreference? _playerFormatPreference;

    // Role families whose client/state object has gone out on this connection, counted rather
    // than tracked as a set so a send that claimed the gate before its await can roll back on
    // failure without trampling the same family's claim from another successful or still
    // in-flight state send. Positive count = the role's inbound binary channel is open on this
    // connection (spec PR #204). Reset per connection in FinishHandshake. Guarded because it is
    // written from the send paths and read from the receive loop.
    private readonly Dictionary<string, int> _roleStateSent = new(StringComparer.Ordinal);
    private readonly HashSet<string> _warnedUngatedRoles = new(StringComparer.Ordinal);
    private readonly object _roleStateSentLock = new();

    // The spec's artwork channel maximum: the client/state channels array is positional from
    // channel 0 and one longer than this is a protocol error the server closes the connection over.
    private const int MaxArtworkChannels = 4;

    // One bit per undefined player-audio type (5-7) already warned about. A lost race on this
    // field costs a duplicate warning and nothing else, so it needs no synchronization.
    private int _warnedUndefinedPlayerAudioTypes;

    // True once this unavailable period has logged a dropped display frame, so the drop (spec
    // #266/#271) is reported once rather than at frame rate. Re-armed when the client becomes
    // available again and on each new connection. A lost race costs a duplicate debug line and
    // nothing else.
    private bool _loggedDisplayDropWhileUnavailable;

    // Tail of the stream-lifecycle chain: the task the next lifecycle handler waits for. See
    // DispatchStreamLifecycle. The lock covers the read-and-replace only.
    private readonly object _streamLifecycleLock = new();
    private Task _streamLifecycleChain = Task.CompletedTask;

    /// <summary>
    /// Queue for audio chunks that arrive before pipeline is ready.
    /// Prevents chunk loss during the ~50ms decoder/buffer initialization.
    /// </summary>
    /// <remarks>
    /// Each chunk is queued with the number of the player configuration it was received under:
    /// the value of <see cref="_playerConfigReceived"/> at that moment.
    /// </remarks>
    private readonly ConcurrentQueue<(AudioChunk Chunk, int Config)> _earlyChunkQueue = new();

    // Counts the lifecycle messages that change what the pipeline does with the next chunk — a
    // stream/start carrying a player object, a stream/clear or stream/end reaching the player,
    // the player role's removal — as they are received and as they take effect. A chunk is
    // "received under" the count at its arrival. The two differ only while such a message is
    // waiting on the lifecycle chain behind an earlier one, and chunks received meanwhile queue
    // instead of going to a pipeline that message has yet to reach. Both under _audioHandoffLock.
    private int _playerConfigReceived;
    private int _playerConfigApplied;

    // Whether the "discarding audio while unavailable" line has already been logged for the
    // current unavailable period (external source or unsynchronized clock; a pipeline error alone
    // does not discard). Set on the first dropped chunk and cleared when availability
    // returns to true (in PublishAvailabilityAsync), so a false->true->false sequence logs once
    // per period even when no audio arrives while available. Written from the receive loop and the
    // availability publisher; a stale read only ever costs a duplicated or skipped debug line, so
    // it needs no lock.
    private bool _audioDroppedWhileUnavailable;

    /// <summary>
    /// Serializes the two places a chunk is handed to the pipeline: the receive loop's direct
    /// hand-off, and the <c>stream/start</c> handler draining what queued while the pipeline was
    /// not ready.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Those are two threads by construction — the start is awaited, and the receive loop must not
    /// wait for it — and the pipeline reports itself ready part-way through a start, as soon as it
    /// has a decoder and a ring. So a chunk arriving mid-start went straight in while the drain was
    /// still feeding the ones that arrived before it: the pipeline saw them out of order, and both
    /// callers decoded through its single scratch buffer at the same time, one overwriting the
    /// other's samples between the decode and the write.
    /// </para>
    /// <para>
    /// Ordering alone cannot close this: the two calls come from different threads whatever
    /// dispatch the lifecycle messages use, so the drain and the hand-off have to exclude each
    /// other. The cost is one uncontended monitor per chunk — a chunk arrives every 20 ms or so,
    /// and the drain is bounded by <see cref="MaxEarlyChunks"/> and happens once per stream start.
    /// </para>
    /// </remarks>
    private readonly object _audioHandoffLock = new();

    /// <summary>
    /// Maximum chunks to queue before pipeline ready (~2 seconds of audio at typical rates).
    /// </summary>
    private const int MaxEarlyChunks = 100;

    // 8 probes lets us pick the lowest-RTT sample and still complete a burst quickly.
    // The reference burst strategy's burst_size_ (sendspin-cpp time_burst.h).
    private const int BurstSize = 8;

    /// <summary>
    /// Per-probe timeout for time sync responses: the reference's
    /// <c>DEFAULT_RESPONSE_TIMEOUT_MS</c>.
    /// </summary>
    /// <remarks>
    /// Generous on purpose. A probe that is merely slow still yields a usable — if
    /// high-<c>max_error</c> — sample, and burst-best selection discards it in favour of any
    /// quicker one, so there is nothing to gain by giving up on it early. The former 2 s
    /// timeout turned an ordinary WiFi stall into a lost sample, and because a timeout also
    /// abandoned the rest of the burst, into a lost burst (#225).
    /// </remarks>
    private const int ProbeTimeoutMs = 10000;

    /// <summary>
    /// Interval between bursts once the clock has converged: the reference's
    /// <c>DEFAULT_BURST_INTERVAL_MS</c>.
    /// </summary>
    /// <remarks>
    /// Long intervals are what make drift observable — the estimate improves with the baseline
    /// between updates, not with the number of updates — and matching the reference here is
    /// what keeps a .NET player's filter dynamics in step with the C++ and JS players sharing
    /// its group.
    /// </remarks>
    private const int SyncedTimeSyncIntervalMs = 10000;

    /// <summary>
    /// Interval between bursts before the clock has converged.
    /// </summary>
    /// <remarks>
    /// The one deliberate departure from the reference's single fixed interval. The reference
    /// reports itself time-synced after its first measurement, so it has no converging window
    /// to hurry through; this SDK withholds <c>available: true</c> — and with it the initial
    /// <c>client/state</c> — until its filter has five measurements and sub-millisecond
    /// uncertainty, which at a 10 s cadence would leave a player invisible to the server for
    /// the better part of a minute after every connect. Pacing the converging window at 500 ms
    /// instead reaches that gate in about two seconds, like the reference clients (#226).
    /// <para>
    /// It is a budget, not a mode: see <see cref="MaxConvergingBursts"/>. On a link where the
    /// convergence gate is simply out of reach the tier would otherwise never end, and "not
    /// converged yet" is exactly when a client can least afford to be the loudest thing on the
    /// network.
    /// </para>
    /// </remarks>
    private const int ConvergingTimeSyncIntervalMs = 500;

    /// <summary>
    /// Bursts the converging tier may spend before the interval widens to
    /// <see cref="SyncedTimeSyncIntervalMs"/> whether the clock has converged or not.
    /// </summary>
    /// <remarks>
    /// About 30 seconds of fast pacing, which is far more than the five bursts a healthy link
    /// needs and still enough for a slow one — at 20 ms RTT the offset uncertainty crosses the
    /// millisecond gate in roughly 25 measurements. Past that the pacing is not buying
    /// convergence, it is buying packets: measurement uncertainty falls as the square root of
    /// the sample count, so a link whose per-sample noise is tens of milliseconds needs
    /// hundreds to thousands of samples, and 8 probes every 500 ms would sustain 5-6 probes a
    /// second for the best part of an hour to get there. The reference client does not converge
    /// on such a link either — it never leaves its fixed 10 s cadence — so widening loses
    /// nothing real and stops a struggling client from making its own network worse. The client
    /// keeps probing at the steady cadence and still reports <c>available</c> the moment the
    /// gate is finally met.
    /// </remarks>
    private const int MaxConvergingBursts = 60;

    // Converging-tier budget, refilled by StartTimeSyncLoop. Touched from the time-sync loop;
    // a loop being replaced can spend one burst of its successor's fresh budget before its
    // Task.Delay observes cancellation — harmless (59 instead of 60), noted for honesty.
    private int _convergingBurstsSpent;
    private bool _convergingBudgetExhausted;

    /// <summary>
    /// Cancelled when this connection ends, and only then.
    /// </summary>
    /// <remarks>
    /// The stream-start rescue burst runs on it. That burst has to outlive
    /// <see cref="StopTimeSyncLoop"/> — a pairing activation stops the loop, and the burst is
    /// gated against pairing at its own source instead — but it must not outlive the
    /// connection. On <see cref="CancellationToken.None"/> it did: against a server that
    /// accepts <c>client/time</c> and never answers, the orphan ran the whole burst (8 probes,
    /// each waiting the full per-probe timeout) while holding the single-burst guard, so the
    /// loop restarted by a fast reconnect had its own bursts skipped until it finished.
    /// </remarks>
    private CancellationTokenSource? _connectionLifetimeCts;

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
    /// The <c>pairing_psk</c> method's effective enablement. Read live by this connection's
    /// <see cref="RecordPskResolver"/>, which excludes Pairing-category records from the
    /// handshake candidate set while the method is off (#202).
    /// </summary>
    internal bool IsPairingPskEnabled => _pairingPskEnabled;

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
    public event EventHandler<StreamEndPayload>? StreamEndReceived;

    /// <inheritdoc />
    public event EventHandler<StreamClearPayload>? StreamClearReceived;

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
        _pairingStoreLock = _pairingStore is null
            ? new object()
            : PairingRecordStoreSynchronization.For(_pairingStore);
        _identity = options.Identity;
        _pairingCodeLockoutStore = options.PairingCodeLockoutStore;
        _presentPairingCodeAsync = options.PresentPairingCodeAsync;
        _pairingWindow = options.PairingWindow;
        _liveRecordPskIds = options.LiveRecordPskIds;
        _attemptTimeout = options.PairingAttemptTimeout;
        _captureDevice = options.CaptureDevice;
        _sourceEncoderFactory = options.SourceEncoderFactory;
        _clockSynchronizer = options.ClockSynchronizer ?? new KalmanClockSynchronizer();

        // Copied, not aliased: these two are the only capability values the SDK itself changes
        // during a connection, and ClientCapabilities is app-owned — shared, in a host, by every
        // connection it accepts. See the field comments.
        _artworkChannels = CopyArtworkChannels(_capabilities.ArtworkChannels);
        _visualizerRoleSupport = CopyVisualizerRoleSupport(_capabilities.VisualizerRoleSupport);

        _displayScheduler = new MediaDisplayScheduler(
            _clockSynchronizer,
            options.PrecisionTimer ?? HighPrecisionTimer.Shared,
            _visualizerRoleSupport?.BufferCapacity ?? 0,
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
                _capabilities.SourceRoleSupport?.Codec,
                () => LastServerHello?.SourceV1Support?.SupportedCodecs);
        }
        _audioPipeline = options.AudioPipeline;
        _outputDelayStore = options.OutputDelayStore;

        _requiredLeadTimeMs = Math.Max(0, _capabilities.RequiredLeadTimeMs);
        _minBufferMs = Math.Max(0, _capabilities.MinBufferMs);
        _unpairedAccessEnabled = _capabilities.UnpairedAccessEnabled;

        // The buffer's readiness gate is the client side of the same promise min_buffer_ms makes
        // to the server, so it follows what is advertised rather than its own default.
        _audioPipeline?.SetMinBufferMilliseconds(_minBufferMs);

        // At most one pairing-code method may be offered (spec #189). Checked before anything
        // is derived from the list, so a contradictory configuration cannot reach the wire.
        _capabilities.ValidatePairingCodeMethods();

        // The runtime reconfiguration path validates spectrum-vs-spectrum-config already; the
        // initial configuration needs the same guard before the first client/state is built.
        _capabilities.ValidateVisualizerRoleSupport();

        // A player must advertise at least one supported_format (spec #257); check before the
        // first client/hello, where an empty list would otherwise go out.
        _capabilities.ValidateAudioFormats();

        // A custom (_-prefixed) role must carry an explicit @v version (spec template.md).
        _capabilities.ValidateCustomRoleVersions();

        // Values a server rejects the hello or the first client/state over, or that the spec
        // gives a fixed form: a player lists flac or pcm, artwork channels stay inside the
        // role's vocabulary, and mac_address is lowercase colon-separated.
        _capabilities.ValidatePlayerCodecs();
        _capabilities.ValidateArtworkChannels();
        _capabilities.ValidateMacAddress();

        // Implemented methods start enabled unless the app says otherwise. ANDing each with
        // PairingCodeMethods keeps "not implemented" and "implemented but disabled" distinct,
        // which is what the spec keys different behaviour off, and keeps a default-constructed
        // ClientCapabilities reporting exactly what it did before these members existed.
        _pairingPskEnabled = _capabilities.PairingPskEnabled;
        _dynamicPairingCodeEnabled = _capabilities.DynamicPairingCodeEnabled
            && _capabilities.PairingCodeMethods.Contains(PairMethods.DynamicPairingCode);
        _staticPairingCodeEnabled = _capabilities.StaticPairingCodeEnabled
            && _capabilities.PairingCodeMethods.Contains(PairMethods.StaticPairingCode);
        _effectiveStaticPairingCode = _capabilities.StaticPairingCode;

        // Copied, not aliased: the SDK does not write to the ClientCapabilities instance the
        // app owns.
        _staticPairingCodeLocations = [.. _capabilities.StaticPairingCodeLocations];
        _pairingPskLocations = [.. _capabilities.PairingPskLocations];

        // Usability of static_pairing_code is evaluated live via HasUsableStaticPairingCode, not
        // snapshotted here. This warning is still worth logging once, at construction, so the app
        // sees why the method it asked for is not being offered.
        if (_capabilities.StaticPairingCodeEnabled
            && _capabilities.PairingCodeMethods.Contains(PairMethods.StaticPairingCode)
            && !IsValidStaticPairingCode(_capabilities.StaticPairingCode))
        {
            _logger.LogWarning(
                "ClientCapabilities.StaticPairingCode is not a valid 8-digit pairing code; {Method} will not be offered until a valid pairing code is configured",
                PairMethods.StaticPairingCode);
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
            _audioPipeline.OutputLatencyChanged += OnOutputLatencyChanged;
        }

        if (_pairingWindow is not null)
        {
            _pairingWindow.StateChanged += OnPairingWindowStateChanged;
        }
    }

    /// <summary>
    /// A private copy of an app-supplied visualizer configuration, including its mutable
    /// <see cref="VisualizerRoleSupport.Types"/> list, so nothing this client reports can be
    /// changed under it by the app (or by a sibling connection sharing the same
    /// <see cref="ClientCapabilities"/>) after construction.
    /// </summary>
    private static VisualizerRoleSupport? CopyVisualizerRoleSupport(VisualizerRoleSupport? support)
        => support is null
            ? null
            : new VisualizerRoleSupport
            {
                BufferCapacity = support.BufferCapacity,
                Types = [.. support.Types],
                RateMax = support.RateMax,

                // VisualizerSpectrum is immutable (init-only scalars), so the reference is safe
                // to share.
                Spectrum = support.Spectrum,
            };

    /// <summary>
    /// Copies the app-supplied starting artwork declaration into this client's private state.
    /// </summary>
    /// <remarks>
    /// The wire requires 1-4 positional entries. An app-owned empty list therefore means "the
    /// artwork role is present but channel 0 is disabled", not "emit an invalid empty array".
    /// </remarks>
    private static List<ArtworkChannelState> CopyArtworkChannels(List<ArtworkChannelState> channels)
        => channels.Count == 0
            ? new List<ArtworkChannelState> { new() { Source = ArtworkSources.None } }
            : [.. channels];

    /// <summary>
    /// The spec's precondition for streaming captured audio: a paired ('user'-trust)
    /// connection with the source role currently active, on a client that is available ("A
    /// client MUST ignore <c>start</c> received while it is unavailable"). Evaluated per start
    /// attempt, because trust, the active-role set and availability can all change over a
    /// connection's life.
    /// </summary>
    private bool IsSourceStreamingPermitted() =>
        _session.MatchedPsk?.Category == PskCategory.LongTerm
        && (LastServerHello?.ActiveRoles.Any(r => r.StartsWith("source@", StringComparison.Ordinal)) ?? false)
        && CurrentAvailability;

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
    /// Starts over the handshake state that belongs to one connection: whether its
    /// <c>server/hello</c> and first <c>server/activate</c> have arrived, and everything the
    /// previous connection's activation granted.
    /// </summary>
    /// <remarks>
    /// Runs when the connection enters <see cref="ConnectionState.Connecting"/>, which
    /// <see cref="SendspinConnection"/> does at the start of every dial — the application's own
    /// and each automatic reconnect attempt — before the socket is opened. So it is done before
    /// the new connection can deliver anything, and an automatic reconnect, which no caller is
    /// waiting on, gets it as well. An in-band re-handshake does not pass through here; see
    /// <see cref="DetectSessionRekey"/>.
    /// </remarks>
    private void ResetHandshakeStateForNewConnection()
    {
        _activateReceived = false;
        _serverHelloReceived = false;
        _pairingActivationActive = false;

        // A new handshake means a new session, so the record this client marked used belongs
        // to the previous one. DetectSessionRekey covers the in-band case; this covers the
        // per-connection one, where the identity changes without the client observing a
        // rekey on an established session.
        _markedPskUsed = false;

        // A new handshake is a new session, and an activate authorises the session it arrived
        // on — not the next one. Cleared here rather than on
        // disconnect because this runs on the dial path only (a listen-path connection never
        // passes through Connecting): this particular clear does not reach the listen path's
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
    /// speak during the window — app-driven volume and format requests, a pipeline recovery ack
    /// — and any new one would otherwise have to know the rule. It also
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
        or ClientPairRetryMessage
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

    private static bool MayReportRoleState(string family, IReadOnlySet<string>? activeRoleFamilies)
        => activeRoleFamilies is null || activeRoleFamilies.Contains(family);

    private static HashSet<string> ToRoleFamilies(IEnumerable<string> activeRoles)
    {
        var families = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in activeRoles)
        {
            int at = role.IndexOf('@');
            families.Add(at > 0 ? role[..at] : role);
        }

        return families;
    }

    /// <summary>
    /// A stable snapshot of the families currently active for this connection's client/state
    /// builders. Built once per send so every role object decision is made from one activate.
    /// </summary>
    internal IReadOnlySet<string>? SnapshotActiveRoleFamilies()
        => LastServerHello?.ActiveRoles is { } activeRoles
            ? ToRoleFamilies(activeRoles)
            : null;

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
            // The initial message carries it instead: it reads _lastSourceSignal live.
            return;
        }

        await SendClientStateAsync();
    }

    /// <summary>
    /// The <c>source</c> object for a client/state, or null when source is not an active role.
    /// </summary>
    /// <remarks>
    /// The object is sent even when it is empty: an activation requires an update that includes
    /// it, and the server MUST NOT send a source <c>start</c> until it has received one.
    /// <c>signal</c> is the only field, and it is optional ("only if 'line_sense' is supported"),
    /// so it is left out when line sense is not supported or nothing has reported a signal yet —
    /// inventing 'absent' would assert something the app never said.
    /// </remarks>
    private SourceStatePayload? BuildSourceState(IReadOnlySet<string>? activeRoleFamilies)
    {
        if (!MayReportRoleState("source", activeRoleFamilies))
        {
            return null;
        }

        if (_capabilities.SourceRoleSupport?.LineSense != true || _lastSourceSignal is not { } signal)
        {
            return new SourceStatePayload();
        }

        return new SourceStatePayload { Signal = signal ? "present" : "absent" };
    }

    /// <summary>
    /// The <c>player</c> object for a client/state, or null when player is not an active role.
    /// Always the client's complete player state: spec PR #175 removed merging, so a field this
    /// object leaves out is dropped by the server rather than retained.
    /// </summary>
    private PlayerStatePayload? BuildPlayerState(IReadOnlySet<string>? activeRoleFamilies)
    {
        if (!MayReportRoleState("player", activeRoleFamilies))
        {
            return null;
        }

        // The configured leads plus the player's output latency (#281): the buffer pre-rolls
        // playback by that latency, so it is lead the server must give and the player cannot.
        var (leadTimeMs, minBufferMs) = ReportedLeads();

        return new PlayerStatePayload
        {
            Volume = _playerState.Volume,
            Muted = _playerState.Muted,

            // Always the applied delay, never a caller's parameter: the server takes what is on
            // the wire as the player's delay, so reporting one the client is not applying leaves
            // its group calibration working from a different number than playback.
            OutputDelayMs = ToWireOutputDelayMs(_clockSynchronizer.OutputDelayMs),
            RequiredLeadTimeMs = leadTimeMs,
            MinBufferMs = minBufferMs,
            SupportedCommands = GetPlayerSupportedCommands(),
            Format = _playerFormatPreference,
        };
    }

    /// <summary>
    /// The <c>artwork</c> object for a client/state, or null when artwork is not an active role.
    /// Carries the client's channel declaration, which spec PR #195 moved here from the
    /// (now deleted) <c>artwork@v1_support</c> object in <c>client/hello</c>.
    /// </summary>
    /// <remarks>
    /// Truncated to the spec's four channels: the array is positional from channel 0, and one
    /// longer than four is a protocol error the server SHOULD close the connection over.
    /// <para>
    /// Reads this client's own channel list (see <see cref="_artworkChannels"/>) under
    /// <see cref="_roleConfigLock"/>: <see cref="SetArtworkChannelAsync"/> may be adding a
    /// channel from an app thread while this runs on the send path, and enumerating the list
    /// through that would throw rather than merely read a stale value.
    /// </para>
    /// </remarks>
    private ArtworkStatePayload? BuildArtworkState(IReadOnlySet<string>? activeRoleFamilies)
    {
        if (!MayReportRoleState("artwork", activeRoleFamilies))
        {
            return null;
        }

        lock (_roleConfigLock)
        {
            return new ArtworkStatePayload
            {
                Channels = _artworkChannels.Take(MaxArtworkChannels).Select(c => c.ForWire()).ToList(),
            };
        }
    }

    /// <summary>
    /// The <c>visualizer</c> object for a client/state, or null when visualizer is not an active
    /// role or the app configured no visualizer support. Carries types/rate_max/spectrum, which
    /// spec PR #195 moved here from <c>visualizer@v1_support</c>.
    /// </summary>
    private VisualizerStatePayload? BuildVisualizerState(IReadOnlySet<string>? activeRoleFamilies)
    {
        if (!MayReportRoleState("visualizer", activeRoleFamilies))
        {
            return null;
        }

        // The configuration is replaced wholesale, never mutated in place, so taking the
        // reference under the lock is enough to read a consistent one.
        VisualizerRoleSupport? support;
        lock (_roleConfigLock)
        {
            support = _visualizerRoleSupport;
        }

        if (support is null)
        {
            return null;
        }

        return new VisualizerStatePayload
        {
            Types = new List<string>(support.Types),
            RateMax = support.RateMax,
            Spectrum = support.Spectrum,
        };
    }

    /// <summary>
    /// The single place a <c>client/state</c> is built and sent. Composes <c>available</c> plus
    /// the full state object of every active role from this client's live state, so the initial
    /// message, an availability flip, a player/source/artwork/visualizer update, a command
    /// acknowledgement, a role (re)activation and the post-pairing resend all put the same
    /// complete picture on the wire and cannot drift apart one call site at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Spec PR #175 removed merging: every message carries <c>available</c> and the full state of
    /// each role object it includes, and an omitted object leaves that role unchanged. Sending
    /// every active role's object every time is therefore not merely permitted but the only shape
    /// that cannot silently drop a field, and it is what spec PR #204's binary gate needs — the
    /// server may not stream a role's binary data until it has seen that role's object.
    /// </para>
    /// <para>
    /// Claims each role whose object is on this message as reported for this connection (see
    /// <see cref="_roleStateSent"/>) <em>before</em> the await, for the same reason the
    /// availability tracker is seeded before its send: a later frame on the same session must
    /// not be treated as outracing a state message that is already queued to write. If the send
    /// itself fails, the claim is rolled back.
    /// </para>
    /// </remarks>
    private async Task SendClientStateAsync(bool initial = false)
    {
        // Held from the snapshot until SendAsync has been awaited, so two concurrent Set*Async
        // calls cannot snapshot in one order and enqueue in the other (see _clientStateSendGate).
        // Not reentrant, and safe to be so: nothing between here and the await sends a client/state.
        await _clientStateSendGate.WaitAsync();
        try
        {
            bool available = CurrentAvailability;
            var activeRoleFamilies = SnapshotActiveRoleFamilies();
            var message = CreateClientStateMessage(available, activeRoleFamilies);

            if (initial)
            {
                _logger.LogInformation("Sending initial client/state:\n{Json}", MessageSerializer.Serialize(message));
            }

            var claimedFamilies = MarkRoleStateSent(message.Payload);

            // Keep the availability publisher's tracker in step with what the server is being told,
            // so the next genuine change is neither a spurious repeat nor swallowed as one.
            lock (_availabilityLock)
            {
                _lastAvailabilitySent = available;
            }

            // Keep the output-latency dedupe (OnOutputLatencyChanged) in step with the leads this
            // message just put on the wire, so a stream-start re-attach reporting an unchanged
            // latency does not re-send. Read from the built player object so it cannot disagree
            // with what the server was told.
            if (message.Payload.Player is { } reportedPlayer)
            {
                _lastReportedLeadTimeMs = reportedPlayer.RequiredLeadTimeMs;
                _lastReportedMinBufferMs = reportedPlayer.MinBufferMs;
            }

            try
            {
                await SendAsync(message);
            }
            catch
            {
                RollBackClaimedRoleState(claimedFamilies);
                throw;
            }
        }
        finally
        {
            _clientStateSendGate.Release();
        }
    }

    internal ClientStateMessage CreateClientStateMessage(bool available, IReadOnlySet<string>? activeRoleFamilies)
        => ClientStateMessage.Create(
            available: available,
            player: BuildPlayerState(activeRoleFamilies),
            source: BuildSourceState(activeRoleFamilies),
            artwork: BuildArtworkState(activeRoleFamilies),
            visualizer: BuildVisualizerState(activeRoleFamilies));

    /// <summary>
    /// Records which roles a client/state just reported an object for, which is what opens each
    /// role's inbound binary channel (spec PR #204).
    /// </summary>
    private List<string> MarkRoleStateSent(ClientStatePayload payload)
    {
        var claimedFamilies = new List<string>(capacity: 4);
        lock (_roleStateSentLock)
        {
            Claim("player", payload.Player);
            Claim("source", payload.Source);
            Claim("artwork", payload.Artwork);
            Claim("visualizer", payload.Visualizer);
        }

        return claimedFamilies;

        void Claim(string family, object? stateObject)
        {
            if (stateObject is null)
            {
                return;
            }

            claimedFamilies.Add(family);
            _roleStateSent[family] = _roleStateSent.GetValueOrDefault(family) + 1;
        }
    }

    private void RollBackClaimedRoleState(IReadOnlyList<string> claimedFamilies)
    {
        if (claimedFamilies.Count == 0)
        {
            return;
        }

        lock (_roleStateSentLock)
        {
            foreach (var family in claimedFamilies)
            {
                if (!_roleStateSent.TryGetValue(family, out int claims))
                {
                    continue;
                }

                if (claims <= 1)
                {
                    _roleStateSent.Remove(family);
                }
                else
                {
                    _roleStateSent[family] = claims - 1;
                }
            }
        }
    }

    private void RemoveRoleStateClaimsForInactiveFamilies(IReadOnlySet<string> activeRoleFamilies)
    {
        lock (_roleStateSentLock)
        {
            _roleStateSent.Keys
                .Where(family => !activeRoleFamilies.Contains(family))
                .ToList()
                .ForEach(family => _roleStateSent.Remove(family));
        }
    }

    /// <summary>
    /// Discards the inbound display state of every role dropped from <c>active_roles</c> by a
    /// <c>server/activate</c> (spec PR #275): the role's current object on the group model and any
    /// future-scheduled update it left pending, raising the same cleared events a <c>null</c> role
    /// object raises. A role still active is untouched — its state and its pending update alike.
    /// </summary>
    /// <remarks>
    /// Runs on the receive loop, like the <c>server/state</c> handler whose clears it mirrors.
    /// Does nothing before any group state exists: the server sends no <c>server/state</c> before
    /// the first activate, so a role dropped between <c>server/hello</c> and that activate held no
    /// state to clear and has nothing to announce.
    /// </remarks>
    private void DiscardDeactivatedRoleState(
        IReadOnlySet<string> previousActiveRoleFamilies,
        IReadOnlySet<string> currentActiveRoleFamilies)
    {
        if (_currentGroup is not { } group)
        {
            return;
        }

        bool Removed(string family)
            => previousActiveRoleFamilies.Contains(family) && !currentActiveRoleFamilies.Contains(family);

        var changed = false;
        var colorCleared = false;

        if (Removed("metadata"))
        {
            _displayScheduler.FlushStateUpdate(ScheduledStateRole.Metadata);
            ApplyMetadata(group, null);
            changed = true;
        }

        if (Removed("controller"))
        {
            ClearControllerState(group);
            changed = true;
        }

        if (Removed("color"))
        {
            _displayScheduler.FlushStateUpdate(ScheduledStateRole.Color);
            ApplyColor(group, null);
            changed = true;
            colorCleared = true;
        }

        if (changed)
        {
            GroupStateChanged?.Invoke(this, group);
        }

        if (colorCleared)
        {
            ColorChanged?.Invoke(this, group.Colors);
        }
    }

    /// <summary>
    /// Whether this connection has sent the <c>client/state</c> object for
    /// <paramref name="family"/>, which spec PR #204 makes the gate on that role's binary data:
    /// "The server MUST NOT send a role's binary data until it has received that object."
    /// </summary>
    /// <remarks>
    /// A conformant server never gets ahead of this, so a frame the gate drops is a server
    /// deviation — and dropping it is the safe reading: the player object is where
    /// <c>output_delay_ms</c>, <c>required_lead_time_ms</c> and <c>min_buffer_ms</c> live, so
    /// audio that arrives before it was scheduled against timings the server had to guess.
    /// </remarks>
    private bool IsRoleBinaryPermitted(string family)
    {
        lock (_roleStateSentLock)
        {
            return _roleStateSent.ContainsKey(family);
        }
    }

    /// <summary>
    /// Logs the first binary frame dropped for each role whose client/state object has yet to go
    /// out, so a server streaming ahead of the gate produces one line per role rather than one
    /// per frame.
    /// </summary>
    private void WarnOnceOnUngatedRoleBinary(string family)
    {
        lock (_roleStateSentLock)
        {
            if (!_warnedUngatedRoles.Add(family))
            {
                return;
            }
        }

        _logger.LogWarning(
            "Dropping {Role} binary data: this connection has not yet sent its {Role} client/state "
            + "object, which the server must receive before streaming the role's binary data",
            family,
            family);
    }

    /// <summary>
    /// Creates the ClientHello message from current capabilities.
    /// Extracted for reuse between initial connection and reconnection handshakes.
    /// </summary>
    private ClientHelloMessage CreateClientHelloMessage()
    {
        int artworkChannelCount;
        VisualizerRoleSupport? visualizerConfiguration;
        lock (_roleConfigLock)
        {
            artworkChannelCount = _artworkChannels.Count;
            visualizerConfiguration = _visualizerRoleSupport;
        }

        if (artworkChannelCount > MaxArtworkChannels)
        {
            _logger.LogWarning("ArtworkChannels has {Count} entries; only the first 4 are reported (spec maximum).",
                artworkChannelCount);
        }

        return ClientHelloMessage.Create(
            // Under the encrypted protocol client_id/version travel in client/init and are
            // omitted here; unpaired_access and supported_pair_methods travel here instead.
            // trust_level is gone: spec PR #158 deleted it from client/hello.
            name: _capabilities.ClientName,
            supportedRoles: _capabilities.Roles,

            // Every support object is gated on its role appearing in supported_roles. The spec
            // ties the two together -- a support object belongs in client/hello exactly when its
            // role version is listed -- and aiosendspin flags an unlisted one as a client
            // deviation ("client/hello sent support objects for unlisted roles"), which a server
            // running allow_noncompliant_clients=False rejects outright rather than tolerating.
            // Roles is public and ClientCapabilities tells consumers to drop artwork@v1 from it
            // to opt out, so this was reachable straight from our own documented advice.
            //
            // There is no artwork object at all: spec PR #195 deleted artwork@v1_support, and the
            // channel declaration now travels in client/state where it can change mid-connection.
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
                }
                : null,
            deviceInfo: new DeviceInfo
            {
                ProductName = _capabilities.ProductName,
                Manufacturer = _capabilities.Manufacturer,
                SoftwareVersion = _capabilities.SoftwareVersion,
                MacAddress = _capabilities.MacAddress
            },

            // Only buffer_capacity since spec PR #195: types, rate_max and spectrum are dynamic
            // and belong to the client/state visualizer object.
            visualizerSupport: HasRole("visualizer") && visualizerConfiguration is { } visualizer
                ? new VisualizerSupport { BufferCapacity = visualizer.BufferCapacity }
                : null,
            sourceSupport: HasSourceRole()
                ? new SourceSupport
                {
                    Features = _capabilities.SourceRoleSupport?.LineSense == true ? new SourceFeatures { LineSense = true } : null,
                }
                : null,
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

    public Task DisconnectAsync(string reason = "restart") => DisconnectAsync(reason, sendGoodbye: true);

    private async Task DisconnectAsync(string reason, bool sendGoodbye)
    {
        if (_disposed) return;

        _logger.LogInformation("Disconnecting: {Reason}", reason);

        StopTimeSyncLoop();
        EndConnectionLifetime();

        if (sendGoodbye)
        {
            await _connection.DisconnectAsync(reason);
        }
        else
        {
            await _connection.CloseWithoutGoodbyeAsync(reason);
        }

        ServerId = null;
        ServerName = null;
        _currentGroup = null;
    }

    /// <summary>
    /// Whether a controller <c>client/command</c> named <paramref name="command"/> may be put on
    /// the wire: the <c>controller@v1</c> role must be active and the command must appear in the
    /// group's latest <c>supported_commands</c>. Until a <c>server/state</c> controller object has
    /// populated that list it is treated as empty — nothing is permitted until the server says so.
    /// Drops with a warning naming the command and the reason rather than throwing, the same way
    /// the seek path drops a seek without its argument: a server ignores such a command anyway.
    /// </summary>
    private bool MaySendControllerCommand(string command)
    {
        if (!IsRoleActive("controller"))
        {
            _logger.LogWarning("Dropping controller command '{Command}': controller@v1 is not active", command);
            return false;
        }

        var supported = _currentGroup?.SupportedCommands;
        if (supported is null || !supported.Contains(command))
        {
            _logger.LogWarning(
                "Dropping controller command '{Command}': not in the group's supported_commands", command);
            return false;
        }

        return true;
    }

    public async Task SendCommandAsync(string command, Dictionary<string, object>? parameters = null)
    {
        if (!MaySendControllerCommand(command))
        {
            return;
        }

        // Extract the typed controller parameters from the loosely-typed dictionary
        int? volume = null;
        bool? mute = null;
        int? positionMs = null;
        int? offsetMs = null;

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

            if (parameters.TryGetValue("position_ms", out var posObj))
            {
                positionMs = AsMilliseconds(posObj);
            }

            if (parameters.TryGetValue("offset_ms", out var offObj))
            {
                offsetMs = AsMilliseconds(offObj);
            }
        }

        // The spec makes position_ms/offset_ms mandatory on their commands (controller/v1.md:34),
        // so a seek whose parameter is missing or unreadable has no valid wire form — sending it
        // bare would put a forbidden shape on the socket. Drop it with a warning instead, the same
        // way the rest of this class handles input it cannot use; the typed SeekAsync /
        // SeekRelativeAsync are the way to seek without this failure mode.
        if (command == Commands.Seek && positionMs is null)
        {
            _logger.LogWarning("Dropping 'seek': no usable position_ms parameter");
            return;
        }

        if (command == Commands.SeekRelative && offsetMs is null)
        {
            _logger.LogWarning("Dropping 'seek_relative': no usable offset_ms parameter");
            return;
        }

        var message = ClientCommandMessage.Create(command, volume, mute, positionMs, offsetMs);

        _logger.LogDebug("Sending command: {Command}", command);
        await SendAsync(message);
    }

    /// <summary>
    /// Reads a millisecond count out of <see cref="SendCommandAsync"/>'s loosely-typed parameter
    /// dictionary. Callers pass whatever their arithmetic produced — <see cref="TimeSpan"/>'s
    /// TotalMilliseconds is a double, a JSON round-trip lands on long — so all three numeric
    /// widths are accepted, fractional milliseconds rounded. Null when the value is not a number
    /// or does not fit an <see cref="int"/> (NaN and the infinities fail both comparisons).
    /// </summary>
    private static int? AsMilliseconds(object? value) => value switch
    {
        int i => i,
        long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
        double d when d is >= int.MinValue and <= int.MaxValue => (int)Math.Round(d),
        _ => null
    };

    public async Task SetVolumeAsync(int volume)
    {
        if (!MaySendControllerCommand(Commands.Volume))
        {
            return;
        }

        var clampedVolume = Math.Clamp(volume, 0, 100);
        var message = ClientCommandMessage.Create(Commands.Volume, volume: clampedVolume);

        _logger.LogDebug("Setting volume to {Volume}", clampedVolume);
        await SendAsync(message);
    }

    /// <inheritdoc/>
    public async Task SetMuteAsync(bool muted)
    {
        if (!MaySendControllerCommand(Commands.Mute))
        {
            return;
        }

        var message = ClientCommandMessage.Create(Commands.Mute, mute: muted);

        _logger.LogDebug("Setting mute to {Muted}", muted);
        await SendAsync(message);
    }

    /// <inheritdoc/>
    public async Task SeekAsync(int positionMs)
    {
        if (!MaySendControllerCommand(Commands.Seek))
        {
            return;
        }

        var message = ClientCommandMessage.Create(Commands.Seek, positionMs: positionMs);

        _logger.LogDebug("Seeking to {PositionMs} ms", positionMs);
        await SendAsync(message);
    }

    /// <inheritdoc/>
    public async Task SeekRelativeAsync(int offsetMs)
    {
        if (!MaySendControllerCommand(Commands.SeekRelative))
        {
            return;
        }

        var message = ClientCommandMessage.Create(Commands.SeekRelative, offsetMs: offsetMs);

        _logger.LogDebug("Seeking by {OffsetMs} ms", offsetMs);
        await SendAsync(message);
    }

    /// <inheritdoc/>
    public async Task SetPlayerFormatPreferenceAsync(AudioFormat? format)
    {
        PlayerFormatPreference? preference = null;

        if (format is not null)
        {
            // The spec closed the partial-request case: a preference is one whole
            // supported_formats entry or nothing, and a server MAY close the connection over one
            // it never advertised. Rejecting here is a configuration error the app can fix;
            // sending it would be a wire deviation it cannot see.
            int bitDepth = format.BitDepth ?? 16;
            bool supported = _capabilities.AudioFormats.Any(f =>
                string.Equals(f.Codec, format.Codec, StringComparison.Ordinal)
                && f.Channels == format.Channels
                && f.SampleRate == format.SampleRate
                && (f.BitDepth ?? 16) == bitDepth);

            if (!supported)
            {
                throw new ArgumentException(
                    $"Format {format.Codec}/{format.SampleRate}Hz/{format.Channels}ch/{bitDepth}-bit is not one of "
                    + "ClientCapabilities.AudioFormats. The spec requires a player's format preference to be one of "
                    + "the entries it advertised in supported_formats.",
                    nameof(format));
            }

            preference = new PlayerFormatPreference
            {
                Codec = format.Codec,
                Channels = format.Channels,
                SampleRate = format.SampleRate,
                BitDepth = bitDepth,
            };
        }

        _playerFormatPreference = preference;

        _logger.LogDebug("Player format preference set to {Format}",
            preference is null
                ? "none (server selects by priority)"
                : $"{preference.Codec}/{preference.SampleRate}Hz/{preference.Channels}ch/{preference.BitDepth}-bit");

        await ReportStateChangeAsync();
    }

    /// <inheritdoc/>
    public async Task SetArtworkChannelAsync(
        int channel, string? source = null, string? format = null, int? width = null, int? height = null)
    {
        if (channel is < 0 or >= MaxArtworkChannels)
        {
            throw new ArgumentOutOfRangeException(
                nameof(channel), channel, $"Artwork channel must be 0-{MaxArtworkChannels - 1}.");
        }

        ArtworkChannelState configured;
        lock (_roleConfigLock)
        {
            // The wire array is positional from channel 0, so a gap has to be filled rather than
            // skipped; an uncovered index means source 'none', which is exactly what the filler
            // says. It keeps ArtworkChannelState's own format/size defaults instead of nulling
            // them: a filler is a real channel that happens to be off, so enabling it later with
            // a source alone still declares the format/width/height the spec requires of an
            // active channel. ForWire drops them again for as long as the source is 'none'.
            while (_artworkChannels.Count <= channel)
            {
                _artworkChannels.Add(new ArtworkChannelState { Source = ArtworkSources.None });
            }

            var existing = _artworkChannels[channel];
            var newSource = source ?? existing.Source;

            // A channel re-enabled with a source alone must still declare the format and size the
            // spec requires of an active source. When the retained values are null (an app-supplied
            // disabled channel), fall back to ArtworkChannelState's own defaults — the same ones the
            // gap filler above keeps. ForWire drops them again for as long as the source is 'none'.
            var defaults = string.Equals(newSource, ArtworkSources.None, StringComparison.Ordinal)
                ? existing
                : new ArtworkChannelState();
            configured = new ArtworkChannelState
            {
                Source = newSource,
                Format = format ?? existing.Format ?? defaults.Format,
                Width = width ?? existing.Width ?? defaults.Width,
                Height = height ?? existing.Height ?? defaults.Height,
            };
            configured.Validate();
            _artworkChannels[channel] = configured;
        }

        _logger.LogDebug("Artwork channel {Channel} configured (source={Source}, format={Format}, {Width}x{Height})",
            channel, configured.Source, configured.Format, configured.Width, configured.Height);

        await ReportStateChangeAsync();
    }

    /// <inheritdoc/>
    public async Task SetVisualizerConfigurationAsync(
        List<string> types, int rateMax, VisualizerSpectrum? spectrum = null)
    {
        ArgumentNullException.ThrowIfNull(types);

        // A visualizer object listing 'spectrum' without a spectrum config is a protocol error
        // the server SHOULD close the connection over, so it never reaches the wire from here.
        if (types.Contains(VisualizerTypes.Spectrum, StringComparer.Ordinal) && spectrum is null)
        {
            throw new ArgumentException(
                "A visualizer configuration that requests 'spectrum' must also carry a spectrum object; "
                + "the server closes the connection over one that does not.",
                nameof(spectrum));
        }

        // rate_max is a positive integer in every visualizer state object, whatever the types —
        // an empty or event-only list included (roles/visualizer/v1.md, spec #257).
        if (rateMax <= 0)
        {
            throw new ArgumentException(
                "A visualizer configuration must set a positive rate_max, whatever types it requests.",
                nameof(rateMax));
        }

        lock (_roleConfigLock)
        {
            // Buffer capacity is a constant of the device advertised once in client/hello, so it
            // carries over from whatever this client was constructed with.
            _visualizerRoleSupport = new VisualizerRoleSupport
            {
                BufferCapacity = _visualizerRoleSupport?.BufferCapacity ?? 0,
                Types = [.. types],
                RateMax = rateMax,
                Spectrum = spectrum,
            };
        }

        _logger.LogDebug("Visualizer configuration set (types={Types}, rate_max={RateMax})",
            string.Join(",", types), rateMax);

        await ReportStateChangeAsync();
    }

    /// <summary>
    /// Puts a stream-configuration change onto the wire once the connection is in a state that
    /// permits it. Before the connection's initial client/state has gone out there is nothing to
    /// send yet: that message reads every field live, so it carries the change when it goes.
    /// </summary>
    private Task ReportStateChangeAsync()
        => _connection.State == ConnectionState.Connected && _initialClientStateSent
            ? SendClientStateAsync()
            : Task.CompletedTask;

    /// <inheritdoc/>
    public async Task SendPlayerStateAsync(int volume, bool muted, double? outputDelayMs = null)
    {
        var clampedVolume = Math.Clamp(volume, 0, 100);

        // A supplied delay is a client-initiated update, which the spec permits ("clients may
        // update output_delay_ms ... when audio output changes") and requires be persisted
        // ("clients must persist output_delay_ms locally across reboots and server
        // reconnections"). Applying it here is what makes the reported value true: this used to
        // report the caller's number while continuing to schedule with the old one, so the
        // server's group calibration and the client's playback disagreed, and a reconnect
        // silently reverted to the unpersisted value.
        if (outputDelayMs is { } requested && requested != _clockSynchronizer.OutputDelayMs)
        {
            _clockSynchronizer.OutputDelayMs = requested;

            // Persist what the setter actually applied (clamped to 0-5000), not the raw request,
            // so a reload restores the same value rather than re-clamping a stored out-of-range one.
            TrySaveOutputDelay(_clockSynchronizer.OutputDelayMs);
        }

        // Persist the caller's values: SendInitialClientStateAsync reads _playerState, so
        // without this a reconnect's initial message would revert an app-set volume to the
        // last server-commanded value — and the pre-latch promotion below would put the old
        // volume on the wire with nothing scheduled to correct it.
        _playerState.Volume = clampedVolume;
        _playerState.Muted = muted;

        // Before the connection's initial client/state has gone out, nothing may hit the wire
        // yet: the server treats the first client/state it receives as the initial one. Send the
        // full state — it reads the values persisted above — unless sync is not yet established
        // and nothing is genuinely wrong, in which case stay silent like UpdateTimingAsync does:
        // a message sent now would carry exactly the spurious available: false the deferral
        // exists to prevent, and the deferred initial reads the persisted values live, so nothing
        // is lost. The same holds before the connection's initial server/activate, which is
        // what makes it Connected: the client "MUST NOT send other Sendspin messages until it
        // receives that activation", and the initial it then sends carries these values.
        if (!_initialClientStateSent)
        {
            if (_connection.State == ConnectionState.Connected && !InitialStateStillDeferredForClockSync)
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

        var (leadTimeMs, minBufferMs) = ReportedLeads();
        _logger.LogDebug(
            "Sending player state: Volume={Volume}, Muted={Muted}, OutputDelay={OutputDelay}ms, LeadTime={LeadTime}ms, MinBuffer={MinBuffer}ms",
            clampedVolume, muted, _clockSynchronizer.OutputDelayMs, leadTimeMs, minBufferMs);

        // The full state of every active role, not a player-only fragment: spec PR #175 removed
        // merging, so a message's included role objects are read as that role's whole state.
        await SendClientStateAsync();
    }

    /// <summary>
    /// The lead values to report: the configured ones plus the output latency the player will
    /// spend before a chunk's timestamp. The buffer pre-rolls playback by that latency, so it is
    /// lead the server has to give and the player cannot. The measured latency wins once a player
    /// has reported one; until then the host's expectation stands in. The configured
    /// <see cref="ClientCapabilities.MinBufferMs"/> alone still bounds the readiness gate.
    /// </summary>
    private (int LeadTimeMs, int MinBufferMs) ReportedLeads()
    {
        var measured = _audioPipeline?.DetectedOutputLatencyMs ?? 0;
        var outputLatencyMs = measured > 0 ? measured : Math.Max(0, _capabilities.ExpectedOutputLatencyMs);
        return (_requiredLeadTimeMs + outputLatencyMs, _minBufferMs + outputLatencyMs);
    }

    /// <summary>
    /// A player was attached or switched and reports a different output latency. The leads the
    /// server holds include the old one, so re-report — but only when the numbers actually move:
    /// every stream start re-attaches a player, and the spec asks for debounced updates.
    /// </summary>
    private void OnOutputLatencyChanged(object? sender, int latencyMs)
    {
        var (leadTimeMs, minBufferMs) = ReportedLeads();
        if (leadTimeMs == _lastReportedLeadTimeMs && minBufferMs == _lastReportedMinBufferMs)
        {
            return;
        }

        if (_connection.State != ConnectionState.Connected)
        {
            return; // The next initial client/state carries the new values.
        }

        _logger.LogInformation(
            "Output latency now {LatencyMs}ms; re-reporting player timing: LeadTime={LeadTime}ms, MinBuffer={MinBuffer}ms",
            latencyMs, leadTimeMs, minBufferMs);
        SendPlayerStateAsync(_playerState.Volume, _playerState.Muted).SafeFireAndForget(_logger);
    }

    /// <inheritdoc/>
    public async Task UpdateTimingAsync(int requiredLeadTimeMs, int minBufferMs)
    {
        _requiredLeadTimeMs = Math.Max(0, requiredLeadTimeMs);
        _minBufferMs = Math.Max(0, minBufferMs);

        // Same reason as at construction: the readiness gate follows what the server is told.
        _audioPipeline?.SetMinBufferMilliseconds(_minBufferMs);

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
            await SendPlayerStateAsync(_playerState.Volume, _playerState.Muted, _clockSynchronizer.OutputDelayMs);
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
    /// connection in <see cref="FinishHandshake"/> with the other per-connection latches,
    /// which also sets it, whatever the activation, for as long as it takes to publish
    /// Connected and reset the per-connection state.
    /// </summary>
    private bool _initialClientStateHeldForPairing;

    /// <summary>
    /// Publishes <see cref="CurrentAvailability"/> as a client/state message when it differs from
    /// the last value sent, and no-ops otherwise. This is the only place availability changes
    /// reach the wire — <see cref="EnterExternalSourceAsync"/>,
    /// <see cref="ExitExternalSourceAsync"/>, and the pipeline error/recovery handlers all set
    /// their input and call this, so availability cannot again drift out of sync one call site at
    /// a time. Before the connection's initial client/state has gone out, the publish becomes
    /// that initial message instead (see below).
    /// </summary>
    /// <remarks>
    /// The message it sends is a full one: spec PR #175 removed merging from client/state, so
    /// there is no availability-only delta any more — every message carries <c>available</c> plus
    /// the complete state of each active role, which <see cref="SendClientStateAsync"/> composes.
    /// </remarks>
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

        // Clear the audio-drop log latch when the client is available again, so the next
        // unavailable period logs its first dropped chunk even if none arrived while available.
        if (current)
        {
            _audioDroppedWhileUnavailable = false;
        }

        // Re-arm the display-drop log when the client is available again, so the next unavailable
        // period logs its first dropped frame even if none arrived while available.
        if (current)
        {
            _loggedDisplayDropWhileUnavailable = false;
        }

        // An availability input flipped while the initial client/state is still deferred (e.g. a
        // pipeline error or external-source enter inside the converging window). Send the
        // connection's initial message instead — it reads CurrentAvailability and every role's
        // fields live — and the latch then routes the eventual convergence through the ordinary
        // path. Decided BEFORE the compare-to-last-sent below: pre-latch, the tracker can only
        // hold another connection's stale value (or null), and comparing against that once let a
        // stale false suppress the send entirely, leaving a later update to become the
        // connection's first client/state.
        if (!_initialClientStateSent)
        {
            // ...unless sync is not yet established and nothing else is wrong (e.g. a
            // pipeline recovery landed inside the converging window). An initial sent
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
            await SendClientStateAsync();
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
    /// <c>client-stream/end</c> as an implicit stop, so the end MUST precede the state: the
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
    /// Builds the player <c>supported_commands</c> list reported in client/state:
    /// <c>volume</c> and <c>mute</c> always — the client applies both unconditionally — plus
    /// <c>set_output_delay</c> when the client accepts that command. The reference server derives
    /// controller group volume/mute from this list, so omitting them reads as volume-incapable.
    /// </summary>
    /// <remarks>
    /// Never absent: spec PR #175 made the field required, and the list is the explicit set of
    /// commands the server MAY send. Omitting it once merging was removed would have left the
    /// server unable to tell "no commands" from "unchanged".
    /// </remarks>
    private List<string> GetPlayerSupportedCommands()
    {
        var commands = new List<string> { "volume", "mute" };
        if (_capabilities.SupportsSetOutputDelay)
        {
            commands.Add(Commands.SetOutputDelay);
        }

        return commands;
    }

    /// <summary>
    /// Projects the scheduler-side output delay onto the wire type: the spec's
    /// <c>output_delay_ms</c> is an integer and the applied value a double, so this rounds to the
    /// nearest millisecond.
    /// </summary>
    /// <remarks>
    /// The applied value is already in 0-5000 — <see cref="IClockSynchronizer.OutputDelayMs"/>'s
    /// setter is the single clamp site — so this only rounds a fractional delay to the integer the
    /// wire carries, and the reported value can no longer differ from the applied one by more than
    /// that rounding.
    /// </remarks>
    private static int ToWireOutputDelayMs(double outputDelayMs)
        => (int)Math.Round(outputDelayMs, MidpointRounding.AwayFromZero);

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
    /// not consume it, and nothing here rotates it. Only <see cref="RotatePairingPsk"/>
    /// replaces the stored record.
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

            // The loop's own token does not reach the stream-start rescue burst, which runs on
            // the connection's lifetime precisely so a pairing activation cannot stop it. This
            // is the disconnect that must.
            EndConnectionLifetime();

            // A pairing attempt cannot survive the session (the CPace counter and handshake
            // hash reset with it), so release a presenter still showing the pairing code.
            ClearPairingCodeState();

            // The window closes on "drop of that connection" — the one carrying its attempts.
            _pairingWindow?.CloseFor(this);

            // Streaming state is per-connection (spec): a start from the old connection
            // must not survive into the next one, so tear capture down now, without a
            // client-stream/end — the stream it would end died with the connection.
            _sourcePipeline?.ResetForConnectionLossAsync().SafeFireAndForget(_logger);

            // Same reason, and additionally: the clock synchronizer resets on re-handshake,
            // so a pending item's display time was computed against an offset that no longer
            // holds and cannot be honoured on the new connection. That covers the scheduled
            // metadata and color updates too — the first server/state of the next connection
            // has to carry each role's full state anyway.
            _displayScheduler.Flush();

            // A transfer the old connection left partly received will never get its remaining
            // parts, and would make the next connection's first announce a protocol error.
            _artworkTransfer.Reset();
        }

        // Clean up client state on full disconnection
        if (e.NewState == ConnectionState.Disconnected)
        {
            CompleteHandshakeWait(e.Exception as SendspinHandshakeException);
            ServerId = null;
            ServerName = null;
        }

        // Every dial starts here, the application's and each automatic reconnect attempt alike,
        // and nothing of the new connection has been received yet.
        if (e.NewState == ConnectionState.Connecting)
        {
            ResetHandshakeStateForNewConnection();
        }

        // Re-handshake when WebSocket reconnects successfully
        // Use e.OldState instead of a separate field to avoid race conditions
        // Not reached on a SendspinConnection, whose reconnect goes Reconnecting -> Connecting
        // -> Handshaking: a reconnect completes without this bounded wait, on the reset above.
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
        // this, a grant from the retired session was honoured on the new one until
        // its first activate, on a PSK that could never have been granted it.
        //
        // Unlike ResetHandshakeStateForNewConnection's clear of the same field, this one reaches
        // both the dial
        // and listen paths — DetectSessionRekey runs from OnTextMessageReceived, which both
        // share — so it also reaches SendspinHostService.PriorityOf's read of this field. In
        // the window between a re-key and this session's next activate, PriorityOf reports
        // ConnectionPriority.Empty, which stops Exception
        // 1 ("a pairing attempt is not displaced") applying — during a pairing.md:63
        // re-handshake, which is exactly when a pairing attempt is in flight. Whether
        // PriorityOf should tolerate this transient is filed separately; this comment records
        // that the gap exists, not that it is fine.
        LastServerActivate = null;

        // HandleServerActivate mirrors active_roles into LastServerHello.ActiveRoles (see
        // ResetHandshakeStateForNewConnection's comment on the same clear). The in-band case has
        // no bounding
        // server/hello to reset that mirror on its own, so without this a source@v1 grant
        // from a retired session would carry forward indefinitely, rather than just until the
        // next reconnect.
        //
        // The roles are set aside rather than dropped: the activate that follows a
        // re-handshake "is a subsequent one on the same connection", so it persists them when
        // it omits active_roles and removes the ones it leaves out (spec PR #287). Nothing is
        // granted from them until that activate has been admitted under the new PSK.
        if (LastServerHello is not null)
        {
            _activeRolesBeforeRekey = LastServerHello.ActiveRoles;
            LastServerHello.ActiveRoles = [];
        }

        // Same reasoning as ResetHandshakeStateForNewConnection's clear of this field: the PSK
        // belongs to the
        // attempt that generated it, not to whatever session happens to be current when
        // server/pair-finalize arrives.
        _pendingPairingPsk = null;
    }

    /// <summary>
    /// Stamps the session's matched PSK as used, once per session. Called on the first decrypted
    /// application message, which is the first proof the AEAD verified — the record
    /// must not be stamped on a merely attempted connection.
    /// </summary>
    /// <remarks>
    /// The timestamp is what makes least-recently-used eviction possible when a pairing
    /// arrives at a full store (spec #183). It is local bookkeeping: nothing on the wire
    /// carries it, and a store is free to ignore it and evict by some other policy.
    /// </remarks>
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
                if (record.PskId == pskId)
                {
                    _pairingStore.Upsert(record with { LastUsedUtc = DateTimeOffset.UtcNow });
                    break;
                }
            }
        }

        _markedPskUsed = true;
    }

    /// <summary>
    /// The <c>psk_id</c> of the pairing record this connection's Noise session matched, or null
    /// when the session is unkeyed (Sentinel) or not yet handshaken.
    /// </summary>
    /// <remarks>
    /// Read by the host to protect live records from eviction (spec #183). Internal: the record
    /// identifier is an implementation detail of the store, not part of the app-facing surface.
    /// </remarks>
    internal string? MatchedRecordPskId =>
        _session.MatchedPsk is { } matched ? NoiseConstants.DerivePskId(matched.Key.Span) : null;

    /// <summary>
    /// Whether this Noise session has yet to carry an admitted <c>server/activate</c>: before
    /// the connection's first one, and again between an in-band re-handshake and the activate
    /// that follows it.
    /// </summary>
    /// <remarks>
    /// The server may send nothing else in either window (messaging.md: "The server MUST NOT
    /// send other Sendspin messages until it sends the initial server/activate"; connection.md,
    /// Re-handshake: "Once the new keys are in place, the server MUST send server/activate as
    /// its first message under the new keys"), and the activate is where this client checks
    /// what the peer is allowed to do, so until it has been admitted nothing else the peer
    /// sends takes effect. Such a message is dropped rather than closed over: the spec defines
    /// no close for it, and a peer that never sends its first activate is already bounded by
    /// the handshake timeout on the dial path and the provisional-connection timeout on the
    /// listen path.
    /// <para>
    /// <c>server/time</c> is let through. It is applied only as the answer to a probe this
    /// client has in flight (see <see cref="HandleServerTime"/>), so it cannot take effect
    /// unasked, and the client sends no probe before its first activate.
    /// </para>
    /// <para>
    /// A re-handshake is noticed on the text path (<see cref="DetectSessionRekey"/>), so the
    /// second window opens with the first text message under the new keys.
    /// </para>
    /// </remarks>
    private bool AwaitingActivate => LastServerActivate is null;

    private void OnTextMessageReceived(object? sender, TextMessageReceivedEventArgs e)
    {
        var json = e.Json;

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

        string? messageType = null;
        try
        {
            messageType = MessageSerializer.GetMessageType(json);
            _logger.LogTrace("Received: {Type}", messageType);

            // server/activate follows server/hello (messaging.md, Communication, steps 6-8).
            // Without one there is nothing to record the activated roles against, so every
            // check that reads them would be answering for a peer that never said who it is.
            if (messageType is MessageTypes.ServerActivate && !_serverHelloReceived)
            {
                _logger.LogDebug("Dropping server/activate received before server/hello");
                return;
            }

            // server/hello is "Sent once per connection", and a re-handshake re-sends neither
            // hello (connection.md, Re-handshake), so any later one is a repeat. It is dropped
            // like the other out-of-sequence messages here: handled, it would be answered with a
            // second client/hello and would replace the payload the active roles are recorded on.
            if (messageType is MessageTypes.ServerHello && _serverHelloReceived)
            {
                _logger.LogDebug("Dropping repeated server/hello");
                return;
            }

            if (AwaitingActivate
                && messageType is not (MessageTypes.ServerHello or MessageTypes.ServerActivate
                    or MessageTypes.ServerTime))
            {
                _logger.LogDebug("Dropping {Type} received before server/activate", messageType);
                return;
            }

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

                case MessageTypes.ServerUnpair:
                    HandleServerUnpair();
                    break;

                case MessageTypes.ServerTime:
                    HandleServerTime(json, e.ReceivedAtMicroseconds);
                    break;

                case MessageTypes.GroupUpdate:
                    HandleGroupUpdate(json);
                    break;

                // The player grant is read here, on the receive loop, for all three: that is
                // the grant the server had declared when it sent the message. Their handlers run
                // later, behind whatever the pipeline is still doing, and a server/activate
                // handled in between must not decide for a message that preceded it.
                case MessageTypes.StreamStart:
                {
                    bool playerActive = IsRoleActive("player");
                    DispatchStreamLifecycle(
                        config => HandleStreamStartAsync(json, playerActive, config),
                        changesPlayer: playerActive && HasPlayerObject(json));
                    break;
                }

                case MessageTypes.StreamEnd:
                {
                    bool playerActive = IsRoleActive("player");
                    DispatchStreamLifecycle(
                        config => HandleStreamEndAsync(json, playerActive, config),
                        changesPlayer: playerActive && EndNamesPlayer(json));
                    break;
                }

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
            // wrong-kind element (type routing), and
            // CPaceException from a hostile or mis-sequenced PAKE share. Anything not
            // named here is a bug in our own handling and propagates so the receive loop
            // surfaces it as a lost connection.
            if (messageType is MessageTypes.ServerPairInit or MessageTypes.ServerPairAuth
                or MessageTypes.ServerPairConfirm or MessageTypes.ServerPairFinalize
                or MessageTypes.PairAbort)
            {
                CloseOnPairingProtocolError(ex);
                return;
            }

            // The spec gives no other malformed message a silent close, and the goodbye
            // reason list is closed with no protocol-error value, so this close reuses
            // 'unauthorized' — the reason this client already sends for peer-violation
            // closes — rather than inventing a wire value. It is also the one that stops a
            // server redialling a playback connection only to send the same message again.
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

        // server/hello defines no active_roles; the property is only where HandleServerActivate
        // records the grant. One sent here anyway must not become roles a first activate that
        // omits the field then persists.
        payload.ActiveRoles = [];
        LastServerHello = payload;
        _serverHelloReceived = true;
        ServerName = payload.Name;

        // A server/hello opens a new connection, and no role persists into one.
        _activeRolesBeforeRekey = null;

        // Connection-scoped since spec #178 moved the hint here from the pairing activation.
        // Copied so a later hello cannot mutate a list already handed to a presenter.
        _serverLanguages = payload.Languages is { Count: > 0 } languages ? [.. languages] : null;

        // Encrypted flow: server/hello carries only the name. The server identity
        // came from server/init, and roles arrive in the initial server/activate,
        // which completes the handshake. Per spec, no other messages (including
        // client/time and client/state) may be sent before that activate, so the
        // connected tail runs in HandleServerActivate.
        ServerId = _session.ServerId;
        _logger.LogInformation("Server hello received (encrypted): {ServerId} ({ServerName})",
            ServerId, ServerName);
        WarnOnCredentialMismatch();
        SendEncryptedClientHelloAsync().SafeFireAndForget(_logger);
    }

    /// <summary>
    /// Surfaces the spec's credential-mismatch signal to the operator: a Sentinel-keyed session
    /// with a stored long-term record for this very server means the server referenced a
    /// credential this client could not use, and the client answered with the published Sentinel
    /// PSK (connection.md § Sentinel Fallback). The connection works, but at trust level 'none'
    /// — no playback until someone re-pairs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reports and nothing else. The spec is explicit that the signal alone MUST NOT cause
    /// either side to remove or replace a record; records change only through pairing or
    /// <c>server/unpair</c>.
    /// </para>
    /// <para>
    /// Only a stored-pubkey record can answer "am I paired with the server I am talking to".
    /// A shared-PSK record carries no server id by design, so it is not evidence of a mismatch
    /// — treating one as evidence would warn on every connection to an unrelated server.
    /// Raised from server/hello rather than the handshake because that is where the client
    /// learns the session's PSK category, and it runs once per connection.
    /// </para>
    /// </remarks>
    private void WarnOnCredentialMismatch()
    {
        if (_pairingStore is null
            || ServerId is null
            || _session.MatchedPsk?.Category != PskCategory.Sentinel)
        {
            return;
        }

        bool holdsARecordForThisServer;
        lock (_pairingStoreLock)
        {
            holdsARecordForThisServer = _pairingStore.List()
                .Any(r => r.Category == PskCategory.LongTerm && r.ServerId == ServerId);
        }

        if (holdsARecordForThisServer)
        {
            _logger.LogWarning(
                "Server {ServerId} referenced a credential this client no longer holds; the "
                + "session is running unpaired on the Sentinel PSK. Re-pair to restore service.",
                ServerId);
        }
    }

    /// <summary>
    /// Answers an encrypted-flow server/hello with the encrypted-shape client/hello
    /// (client_id/version omitted; unpaired_access and supported_pair_methods included).
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

        // active_roles persists across activates that omit it. That includes the first one
        // after a re-handshake, whose roles DetectSessionRekey set aside (spec PR #287: "The
        // activation rules, including those for omitted active_roles, apply under the newly
        // matched PSK") — and one of those rules is that persisted roles are treated as empty
        // once the connection is no longer playback-capable.
        var previousActiveRoles = _activeRolesBeforeRekey ?? LastServerHello?.ActiveRoles ?? [];
        bool playbackCapable = _session.MatchedPsk is { } matchedPsk
            && IsAdmissible(matchedPsk.Category, payload.ActivitiesList, hasRoles: true, _unpairedAccessEnabled);
        var activeRoles = payload.ActiveRoles ?? (playbackCapable ? previousActiveRoles : []);

        // Source role is trust-gated: it streams potentially sensitive captured audio,
        // so it MUST only run on a paired ('user'-trust) connection. If a server
        // activates source@v1 without user trust, refuse and close (spec).
        if (activeRoles.Any(r => r.StartsWith("source@", StringComparison.Ordinal))
            && _session.MatchedPsk?.Category != PskCategory.LongTerm)
        {
            _logger.LogWarning("server/activate activated source@v1 without user trust; closing");
            CurrentHandshakeWaiter()?.TrySetResult(false);
            DisconnectAsync("unauthorized").SafeFireAndForget(_logger);
            return;
        }

        // "A server MUST NOT activate a role or version the client did not list in
        // supported_roles." The spec names no rejection for one that does, so the activation
        // stands and the unlisted roles are simply not part of the grant: nothing behind them
        // exists here to act on what the server would send.
        if (activeRoles.Any(r => !_capabilities.Roles.Contains(r)))
        {
            _logger.LogWarning("server/activate activated roles this client did not list; ignoring them: [{Roles}]",
                string.Join(", ", activeRoles.Where(r => !_capabilities.Roles.Contains(r))));
            activeRoles = [.. activeRoles.Where(_capabilities.Roles.Contains)];
        }

        // Recorded only once the activation is admitted: this property is what the
        // host's arbitration reads, so a refused activate must
        // not leave its activities behind. 'Last accepted activation' is the only
        // defensible meaning for a value other code grants permission from.
        LastServerActivate = payload;

        // Mirror roles where legacy consumers look.
        bool activeRolesChanged = false;
        if (LastServerHello is not null)
        {
            _activeRolesBeforeRekey = null;

            var previousActiveRoleFamilies = ToRoleFamilies(previousActiveRoles);
            var currentActiveRoleFamilies = ToRoleFamilies(activeRoles);

            // When the source role is dropped from active_roles, stop streaming (spec:
            // the client ends its input stream on deactivation).
            bool wasSourceActive = previousActiveRoleFamilies.Contains("source");
            bool isSourceActive = currentActiveRoleFamilies.Contains("source");
            if (wasSourceActive && !isSourceActive && _sourcePipeline is not null)
            {
                _sourcePipeline.StopStreamingAsync().SafeFireAndForget(_logger);
            }

            // Noted before the overwrite: an activation that changes the active set changes
            // which role objects belong in client/state, and the server may not stream a newly
            // activated role's binary data until it has received that role's object (spec PR
            // #204). The reaction is deferred to the non-pairing branch below, after the
            // handshake and pairing decisions have run.
            activeRolesChanged = !previousActiveRoleFamilies.SetEquals(currentActiveRoleFamilies);

            LastServerHello.ActiveRoles = [.. activeRoles];

            if (activeRolesChanged)
            {
                RemoveRoleStateClaimsForInactiveFamilies(currentActiveRoleFamilies);
                DiscardDeactivatedRoleState(previousActiveRoleFamilies, currentActiveRoleFamilies);

                // A removed stream role stops its remaining output and clears its buffers
                // whether or not a stream/end came first (spec PR #289). On the lifecycle
                // chain, so it cannot overtake a stream/start still starting the pipeline.
                var removedStreamRoles = previousActiveRoleFamilies
                    .Except(currentActiveRoleFamilies)
                    .Where(family => family is "player" or "artwork" or "visualizer")
                    .ToList();
                if (removedStreamRoles.Count > 0)
                {
                    bool playerRemoved = removedStreamRoles.Contains("player");
                    DispatchStreamLifecycle(
                        config => StopStreamRolesAsync(removedStreamRoles, playerRemoved, config),
                        changesPlayer: playerRemoved);
                }
            }
        }

        _logger.LogInformation("Server activate: activities [{Activities}], roles [{Roles}]",
            string.Join(", ", payload.ActivitiesList),
            string.Join(", ", activeRoles));

        // Pairing can run alongside playback (pairing.md, "Entering and leaving pairing"): the
        // attempt starts on any activation declaring 'pairing', but the wire is held for the
        // pairing exchange alone only when the activation does not also declare 'playback'.
        bool pairing = payload.ActivitiesList.Contains(Activities.Pairing);
        bool pairingOnly = pairing && !payload.ActivitiesList.Contains(Activities.Playback);
        if (pairing)
        {
            HandlePairingActivate(payload);
        }
        else
        {
            // An activation without 'pairing' ends any attempt in progress: the client "abandons
            // the attempt, discarding all pairing state", and "persists nothing". Nothing is sent
            // — the server ended it — and the pairing window is left alone: an abandoned
            // attempt "does not count against a pairing window". A gated attempt still waiting on
            // a gesture goes with it, or the next opening would send client/pair-init outside
            // any pairing activation and bind the shared window to this connection.
            bool attemptInFlight = _pairingCodeState is not null || _pendingPairingPsk is not null;
            lock (_attemptLock)
            {
                attemptInFlight |= _pendingGatedMethod is not null;
            }

            ClearPairingCodeState();
            if (attemptInFlight)
            {
                _logger.LogInformation(
                    "Activation no longer declares the pairing activity; the pairing attempt is abandoned");
            }
        }

        bool first = !_activateReceived;
        _activateReceived = true;

        if (first)
        {
            // The initial activate completes the encrypted handshake; only now may the
            // client start sending (client/time, client/state).
            if (!FinishHandshake(pairingOnly))
            {
                // The connection was closed from inside its own promotion to Connected, so the
                // rest of this activate — the hello notification, the time-sync loop, the
                // activate event — would all be raised for a session that has already gone. A
                // waiting ConnectAsync is not stranded by returning here: the Disconnected
                // transition that closed the connection completes its handshake wait.
                return;
            }

            if (LastServerHello is { } hello)
            {
                ServerHelloReceived?.Invoke(this, hello);
            }
        }

        // The time-sync loop runs only outside a pairing-only activation. Such an activate
        // declares no playback, so there is nothing to synchronize a clock for — and the
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
        bool leavingPairing = _pairingActivationActive && !pairingOnly;
        _pairingActivationActive = pairingOnly;

        if (pairingOnly)
        {
            StopTimeSyncLoop();
        }
        else
        {
            // A pairing-first connection reaches its first non-pairing activate here:
            // release the withheld initial client/state by running the send-or-defer
            // decision FinishHandshake skipped. Nothing promotes the initial while it is
            // withheld (see SendInitialClientStateAsync), so the _initialClientStateSent guard
            // only covers a sender racing the line above it.
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
                // Recovers everything the window dropped. State is last-write-wins, so one full
                // report of the current values restores the server's view — a volume the app
                // changed mid-window, an output delay, an availability flip — without a queue.
                // Skipped when the initial state has yet to go out: the branch above owns that
                // case, and a state message before it would become the connection's "initial".
                ResendClientStateAfterPairingAsync().SafeFireAndForget(_logger);
            }
            else if (activeRolesChanged && !first && _initialClientStateSent)
            {
                // The active set changed, so the set of role objects that belongs in
                // client/state changed with it. Report it now, before the server starts the
                // streams the activation just authorized: a newly activated role's binary data
                // may not flow until the server has received that role's state object (spec PR
                // #204), and a newly deactivated role's object must stop being reported.
                // Excluded on the connection's first activate — the initial client/state, sent
                // or deferred by FinishHandshake above, already carries the activated set, and
                // sending here too would simply double it.
                SendClientStateAsync().SafeFireAndForget(_logger);
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

        // Spec rule ordering: prefer 'pairing_required' when the session is unpaired and
        // enabling unpaired access would make the activation admissible.
        if (psk.Category != PskCategory.LongTerm
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
            // A paired session never carries a pairing activity: pairing runs on the Pairing
            // PSK (or, unpaired, on the Sentinel PSK), so a server declaring it on a long-term
            // session is asking this client to re-pair over a credential it already holds.
            PskCategory.LongTerm => set.All(a => a is Activities.Playback),

            // The two unpaired rows are the same row: pairing always, playback only with
            // unpaired access enabled.
            PskCategory.Pairing or PskCategory.Sentinel => set.All(
                a => a is Activities.Pairing || (a is Activities.Playback && unpairedAccessEnabled)),
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
    /// <see cref="IsMethodEnabled"/>: a method the app never listed is not implemented, while
    /// a listed method the app switched off is implemented but disabled.
    /// </summary>
    private bool IsMethodImplemented(string method) => method switch
    {
        PairMethods.PairingPsk => true, // every client implements it (pairing.md:65)
        _ => _capabilities.PairingCodeMethods.Contains(method),
    };

    /// <summary>The method's effective enablement, as the app configured it.</summary>
    private bool IsMethodEnabled(string method) => method switch
    {
        PairMethods.PairingPsk => _pairingPskEnabled,
        PairMethods.DynamicPairingCode => _dynamicPairingCodeEnabled,
        PairMethods.StaticPairingCode => _staticPairingCodeEnabled,
        _ => false,
    };

    /// <summary>
    /// Whether <paramref name="pin"/> is a well-formed static pairing code: exactly 8 decimal digits
    /// (pairing.md:186).
    /// </summary>
    private static bool IsValidStaticPairingCode(string? pin) =>
        pin is { Length: 8 } && pin.All(char.IsAsciiDigit);

    /// <summary>
    /// Whether a static pairing code good enough to run the method is configured. Nothing
    /// validates what the app supplies at construction, so without this a client could
    /// advertise the method with a null pairing code and run CPace with an empty password.
    /// </summary>
    private bool HasUsableStaticPairingCode => IsValidStaticPairingCode(_effectiveStaticPairingCode);

    /// <summary>
    /// Builds the supported_pair_methods object for the encrypted client/hello: every
    /// implemented method that is currently enabled, keyed by its method identifier (spec #179).
    /// </summary>
    private Dictionary<string, PairMethodDescriptor> BuildPairMethods()
    {
        var methods = new Dictionary<string, PairMethodDescriptor>(StringComparer.Ordinal);
        if (IsMethodEnabled(PairMethods.PairingPsk))
        {
            methods[PairMethods.PairingPsk] = new PairMethodDescriptor
            {
                Locations = LocationsHint(_pairingPskLocations),
            };
        }

        if (CanRun(PairMethods.DynamicPairingCode))
        {
            // 'speaker' is filtered out rather than passed through: a client advertising it
            // must also advertise a digit_audio object and be able to play the server's digit
            // audio pack, neither of which this SDK implements. Advertising it would invite a
            // server to pick a flow that reaches nobody. If that leaves no channel at all, the
            // method is withheld entirely — an empty out_channels is not a usable offer.
            var outChannels = _capabilities.PairingCodeOutChannels
                .Where(c => !string.Equals(c, "speaker", StringComparison.Ordinal))
                .ToList();
            if (outChannels.Count != _capabilities.PairingCodeOutChannels.Count)
            {
                _logger.LogWarning(
                    "ClientCapabilities.PairingCodeOutChannels lists 'speaker', which requires the "
                    + "digit-audio flow this SDK does not implement; it is omitted from the "
                    + "{Method} descriptor",
                    PairMethods.DynamicPairingCode);
            }

            if (outChannels.Count == 0)
            {
                _logger.LogWarning(
                    "{Method} will not be offered: no usable entry remains in "
                    + "ClientCapabilities.PairingCodeOutChannels",
                    PairMethods.DynamicPairingCode);
            }
            else
            {
                methods[PairMethods.DynamicPairingCode] = new PairMethodDescriptor
                {
                    OutChannels = outChannels,

                    // Only 'digits' is emitted. A descriptor whose formats name nothing the
                    // server recognizes is treated as though the method were not offered, so
                    // this list is what makes the offer real — and qr_code would need the
                    // per-session pairing token this SDK does not produce.
                    Formats = [PairingCodeFormats.Digits],
                };
            }
        }

        if (CanRun(PairMethods.StaticPairingCode))
        {
            methods[PairMethods.StaticPairingCode] = new PairMethodDescriptor
            {
                Locations = LocationsHint(_staticPairingCodeLocations),
            };
        }

        return methods;
    }

    /// <summary>
    /// The <c>locations</c> hint to advertise, or null to omit the field entirely when the app
    /// declared none. An empty array would be a positive claim that the secret can be found
    /// nowhere; absence is the spec's way of saying "no hint" (#129).
    /// </summary>
    /// <remarks>
    /// A defensive copy, because the descriptor is handed to the serializer while the app may
    /// still hold the list it passed in through <see cref="ClientCapabilities"/>.
    /// </remarks>
    private static List<string>? LocationsHint(List<string> locations) =>
        locations.Count == 0 ? null : [.. locations];

    /// <summary>
    /// Whether this client is configured to run <paramref name="method"/> to completion:
    /// implemented, enabled, and holding every dependency the method needs. A method that
    /// fails this must not be advertised in client/hello, or the server is told an offer
    /// exists that every attempt will refuse (#132).
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
        PairMethods.DynamicPairingCode => IsMethodImplemented(PairMethods.DynamicPairingCode)
                         && IsMethodEnabled(PairMethods.DynamicPairingCode)
                         && _pairingCodeLockoutStore is not null && _presentPairingCodeAsync is not null
                         && _pairingStore is not null,
        PairMethods.StaticPairingCode => IsMethodImplemented(PairMethods.StaticPairingCode)
                        && IsMethodEnabled(PairMethods.StaticPairingCode)
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
        PairMethods.PairingPsk => _pairingPskEnabled
                         && _session.MatchedPsk?.Category == PskCategory.Pairing
                         && _pairingStore is not null,

        // The other direction of the same rule: the method is pairing_psk if and only if the
        // matched PSK is the Pairing PSK, so a code method is refused on that session.
        PairMethods.DynamicPairingCode => CanRun(PairMethods.DynamicPairingCode)
                         && _session.MatchedPsk?.Category != PskCategory.Pairing,
        PairMethods.StaticPairingCode => CanRun(PairMethods.StaticPairingCode)
                         && _session.MatchedPsk?.Category != PskCategory.Pairing,
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
                Payload = new PairAbortPayload { Reason = PairAbortReasons.MethodNotSupported },
            }).SafeFireAndForget(_logger);
            return;
        }

        _activationPairingCodeFormat = null;
        string? format = payload.Pairing?.Format;
        switch (payload.Pairing?.Method)
        {
            case PairMethods.DynamicPairingCode:
                // The activation names the emission format the server picked from the descriptor's
                // formats (spec #178). It is required for this method and must be one this client
                // advertised; anything else is a method this client cannot run, which is exactly
                // what method_not_supported says. pin_length is gone — a digits code is 6 digits.
                if (!string.Equals(format, PairingCodeFormats.Digits, StringComparison.Ordinal))
                {
                    _logger.LogWarning(
                        "Activation format {Format} is not one this client offers for {Method}; aborting the attempt",
                        format ?? "(none)",
                        PairMethods.DynamicPairingCode);
                    SendAsync(new PairAbortMessage
                    {
                        Payload = new PairAbortPayload { Reason = PairAbortReasons.MethodNotSupported },
                    }).SafeFireAndForget(_logger);
                    return;
                }

                _activationPairingCodeFormat = format;
                break;

            case PairMethods.StaticPairingCode:
            case PairMethods.PairingPsk:
                if (format is not null)
                {
                    _logger.LogWarning(
                        "Activation format {Format} is invalid for {Method}; aborting the attempt",
                        format,
                        payload.Pairing!.Method);
                    SendAsync(new PairAbortMessage
                    {
                        Payload = new PairAbortPayload { Reason = PairAbortReasons.MethodNotSupported },
                    }).SafeFireAndForget(_logger);
                    return;
                }

                break;
        }

        switch (payload.Pairing?.Method)
        {
            case PairMethods.PairingPsk:
                _pendingPairingPsk = PairingRecords.GenerateUniquePsk(_pairingStore!);
                _logger.LogInformation("Pairing PSK flow: delivering long-term PSK to server {ServerId}", ServerId);

                // Spec #247: the attempt starts with client/pair-init, then client/pair-finalize,
                // so a delayed finalize from a cancelled attempt can no longer finalize a later
                // one. The two are one awaited flow — the finalize send is issued only after the
                // init send completes — so the order holds even when another writer contends for
                // the send lock, which SemaphoreSlim does not serve FIFO.
                SendPairingPskInitThenFinalizeAsync(_pairingCounter, _pendingPairingPsk)
                    .SafeFireAndForget(_logger);
                ArmAttemptTimeout();
                break;

            case PairMethods.DynamicPairingCode:
            case PairMethods.StaticPairingCode:
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
    /// Sends the Pairing PSK attempt's <c>client/pair-init</c> then <c>client/pair-finalize</c> in
    /// order (spec #247): the finalize send is issued only after the init send completes, so the
    /// order holds even under send-lock contention, which <see cref="System.Threading.SemaphoreSlim"/>
    /// does not serve FIFO. Both values are captured at dispatch so a later attempt cannot change
    /// them across the awaits.
    /// </summary>
    private async Task SendPairingPskInitThenFinalizeAsync(int pairingIndex, byte[] pendingPairingPsk)
    {
        // No commit_B — that is dynamic pairing code only.
        await SendAsync(new ClientPairInitMessage
        {
            Payload = new ClientPairInitPayload { PairingIndex = pairingIndex },
        });
        await SendAsync(new ClientPairFinalizeMessage
        {
            Payload = new ClientPairFinalizePayload { LongTermPsk = Base64UrlText.Encode(pendingPairingPsk) },
        });
    }

    /// <summary>
    /// Starts a pairing code attempt, or defers it until an operator gesture opens the pairing window.
    /// </summary>
    /// <remarks>
    /// Gating policy: static_pairing_code gates every attempt; dynamic_pairing_code gates only
    /// when the method is escalated. The old "or the session's pairing code is shorter than 6
    /// digits" clause is gone with pin_length — a digits code is always 6 digits.
    /// pairing_psk is never gated and does not reach here.
    /// </remarks>
    private void BeginOrDeferPairingCodeAttempt(string method)
    {
        bool gated = method == PairMethods.StaticPairingCode
                     || IsMethodEscalated(method);

        bool deferred = false;
        if (gated)
        {
            // Asking for admission and marking this connection pending must be one step. Split,
            // a window opened in the gap raised StateChanged while _pendingGatedMethod was still
            // null, so OnPairingWindowStateChanged found nothing pending and returned — and this
            // connection then waited for an opening that had already been and gone (#148).
            //
            // Locking across TryAdmit is safe in this order: PairingWindow releases its own
            // gate before raising StateChanged (see Open/Close), so the reverse nesting —
            // window gate held while a handler takes _attemptLock — does not exist.
            lock (_attemptLock)
            {
                if (_pairingWindow?.TryAdmit(this) != true)
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

        StartPairingCodeAttempt(dynamic: method == PairMethods.DynamicPairingCode);
    }

    /// <summary>
    /// A window opened while this connection was waiting on a gesture. Exactly one waiting
    /// connection is admitted by any opening; the losers stay pending and send nothing.
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
            // to be atomic, or two raises on different threads both start an attempt for the
            // same connection. TryAdmit takes the window's own lock, which is safe here: the window
            // raises this event after releasing that lock, so the two are never taken in the
            // other order.
            lock (_attemptLock)
            {
                if (_pendingGatedMethod is not { } pending)
                {
                    return;
                }

                if (_pairingWindow?.TryAdmit(this) != true)
                {
                    return;
                }

                _pendingGatedMethod = null;
                method = pending;
            }

            StartPairingCodeAttempt(dynamic: method == PairMethods.DynamicPairingCode);
        }
        catch (Exception ex)
        {
            // The opening may already be bound to this connection by the time this throws, in
            // which case the attempt did not start. Nothing here can recover that; the point is
            // that it stops being silent.
            _logger.LogError(
                ex,
                "Pairing window state-changed handler failed; a gated attempt may not have resumed");
        }
    }

    /// <summary>
    /// Begins a pairing code attempt by sending client/pair-init. For dynamic pairing code it includes
    /// commit_B over a fresh nonce_B. Any gesture gating has already been satisfied by
    /// <see cref="BeginOrDeferPairingCodeAttempt"/>, which had the pairing window admit it.
    /// </summary>
    private void StartPairingCodeAttempt(bool dynamic)
    {
        var method = dynamic ? PairMethods.DynamicPairingCode : PairMethods.StaticPairingCode;
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
                AbortPairingCode(PairAbortReasons.AttemptTimeout);
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

        // "A round begins with server/pair-init and ends with the client's verification of
        // server_kc": one that arrives inside a round is out of sequence, a protocol error.
        // It is refused before it is counted, or a peer holding no credential could run the
        // persisted count up to the hold-back with back-to-back messages (#320).
        if (state.RoundBegun || state.CPace is not null)
        {
            throw new System.Text.Json.JsonException(
                "server/pair-init arrived inside a round that has not ended");
        }

        // nonce_A is "present in the first round only": that round derives the pairing code,
        // and the binding values, and so the code, are unchanged across the rounds after it.
        if (state.PairingCode is null)
        {
            if (msg.Payload.NonceA is null)
            {
                throw new System.Text.Json.JsonException(
                    "the first round's server/pair-init is missing nonce_A");
            }

            state.NonceA = Base64UrlText.Decode(msg.Payload.NonceA);
            if (state.NonceA.Length != 32)
            {
                throw new System.Text.Json.JsonException("nonce_A is not 32 bytes");
            }

            var h = _session.HandshakeHash!.Value.ToArray();
            state.PairingCode = PairingCodes.DerivePairingCode(
                h, state.NonceA, state.NonceB!, PairingCodes.DynamicPairingCodeLength);
        }

        // The round counts toward the hold-back from here, where its code is emitted, so a
        // round abandoned afterwards still counts.
        RecordPairingCodeFailure(state.Method);
        state.RoundBegun = true;

        // Present the pairing code through the app's out-channel, again in each round. Started
        // here (this method runs on the connection's synchronous receive dispatch, which cannot
        // await); its completion gates client/pair-auth in
        // SendPairAuthAfterPairingCodePresentedAsync, and its token is cancelled by
        // ClearPairingCodeState when the attempt or the connection is torn down.
        state.PresentPairingCodeCts ??= new CancellationTokenSource();
        state.PairingCodePresented = InvokePairingCodePresenterAsync(
            state.PairingCode, state.PresentPairingCodeCts.Token);
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
        // dynamic_pairing_code without a presenter, and without StartPairingCodeAttempt(dynamic: true)
        // there is no { Dynamic: true } state for HandleServerPairInit to act on.
        await _presentPairingCodeAsync!(
            new PairingCodePresentation(pin, _serverLanguages)
            {
                Format = _activationPairingCodeFormat,
            },
            cancellationToken);
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
        //
        // The same holds in every later round, where the code is already derived: each CPace
        // run is one guess at the pairing code, and server/pair-init is where the round is
        // counted toward the hold-back. A server/pair-auth accepted without one would be a
        // guess that is never counted.
        if (state.Dynamic && !state.RoundBegun)
        {
            throw new System.Text.Json.JsonException(
                "server/pair-auth arrived without the server/pair-init that begins its round");
        }

        // The static flow is a single round, so its one CPace run admits one server/pair-auth.
        if (!state.Dynamic && state.CPace is not null)
        {
            throw new System.Text.Json.JsonException(
                "a second server/pair-auth arrived in the static pairing code flow");
        }

        state.RoundBegun = false;

        // Static pairing code: the pairing code is device-printed and known from the start.
        string pin = state.Dynamic ? state.PairingCode! : (_effectiveStaticPairingCode ?? string.Empty);
        var h = _session.HandshakeHash!.Value.ToArray();

        // Each round is a separate CPace run under its own sid. The static flow is always
        // round 1; the dynamic flow advances the round with each client/pair-retry.
        byte[] sid = PairingCodes.BuildSid(h, (uint)_pairingCounter, state.Round);

        var cpace = CPace.Start(
            CPaceRole.Responder,
            System.Text.Encoding.ASCII.GetBytes(pin),
            sid,
            ad: PairingCodes.AdClient);
        state.CPace = cpace;
        state.Sid = sid;

        // Derive stays on the synchronous path: a hostile pake_msg_1 raises CPaceException
        // into the dispatch catch, which closes the connection as a pairing protocol error.
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

                    AbortPairingCode(PairAbortReasons.UserCancelled);
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
            if (state.Dynamic && !IsMethodEscalated(state.Method))
            {
                // "The client SHOULD retry": another round against the same pairing code. The
                // attempt, its code and its running timeout stay in place; the server begins
                // the next round with a new server/pair-init.
                cpace.Dispose();
                state.CPace = null;
                state.Round++;
                SendAsync(new ClientPairRetryMessage()).SafeFireAndForget(_logger);
                return;
            }

            // A dynamic round was already counted when its code was emitted.
            if (!state.Dynamic)
            {
                RecordPairingCodeFailure(state.Method);
            }

            _pairingWindow?.RecordFailedAttempt(this);
            AbortPairingCode(PairAbortReasons.PairingCodeMismatch);
            return;
        }

        // Send client/pair-confirm then client/pair-finalize (wrapped PSK) back-to-back.
        var confirm = new ClientPairConfirmMessage
        {
            Payload = new ClientPairConfirmPayload { ClientKc = Base64UrlText.Encode(cpace.Tag()) },
        };
        if (state.Dynamic)
        {
            // Spec #155: nonce_B is revealed wrapped, not raw, sealed under the round's sid + ISK.
            confirm.Payload.WrappedNonceB = Base64UrlText.Encode(
                PairingCodes.WrapNonceB(state.Sid!, cpace.Isk, state.NonceB!, _session.Suite));
        }

        SendAsync(confirm).SafeFireAndForget(_logger);

        byte[] psk = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        _pendingPairingPsk = psk;

        // The AEAD of the connection's *negotiated* suite, not a fixed one: the server unwraps
        // with whatever client/init announced, so hardcoding ChaCha20-Poly1305 broke every
        // code-based pairing on an AES-GCM session — including the automatic fallback on a
        // platform without ChaCha20-Poly1305 (#192).
        byte[] wrapped = PairingCodes.WrapPsk(state.Sid!, cpace.Isk, psk, _session.Suite);
        SendAsync(new ClientPairFinalizeMessage
        {
            Payload = new ClientPairFinalizePayload { WrappedPsk = Base64UrlText.Encode(wrapped) },
        }).SafeFireAndForget(_logger);

        // Success resets the method's failure counter.
        if (_pairingCodeLockoutStore is not null)
        {
            lock (PairingCodeLockoutStoreSynchronization.For(_pairingCodeLockoutStore))
                _pairingCodeLockoutStore.SetFailures(state.Method, 0);
        }
    }

    /// <summary>
    /// Closes the connection over a pairing protocol error: "the detecting side closes the
    /// WebSocket without sending any application-level error message, and persists nothing"
    /// (pairing.md, Protocol Errors). So no client/goodbye and no pair/abort — 'unauthorized'
    /// in particular would tell the server its activation had been refused. Every pairing
    /// protocol error ends here: the dispatch catch routes whatever a pairing message's
    /// handler throws.
    /// </summary>
    private void CloseOnPairingProtocolError(Exception? ex)
    {
        _logger.LogError(ex, "Pairing protocol error; closing the connection without client/goodbye");

        // Dropped now rather than when the close completes, the wrapped PSK awaiting
        // server/pair-finalize included.
        ClearPairingCodeState();
        DisconnectAsync("pairing protocol error", sendGoodbye: false).SafeFireAndForget(_logger);
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
    /// Whether the method's failure counter has reached the escalation threshold. An escalated
    /// method stays offered and still runs; every attempt is gesture-gated, and a failed round
    /// aborts rather than retries, until a successful server_kc verification resets the counter.
    /// </summary>
    /// <remarks>
    /// The counter holds failed server_kc verifications for static_pairing_code and rounds since
    /// the last verified server_kc for dynamic_pairing_code. The spec's round limit is 20 and
    /// lets a client hold attempts back earlier; this one does so at 10.
    /// </remarks>
    private bool IsMethodEscalated(string method)
        => (_pairingCodeLockoutStore?.GetFailures(method) ?? 0) >= 10;

    private void RecordPairingCodeFailure(string method)
    {
        if (_pairingCodeLockoutStore is null)
            return;

        // Every connection of a host counts into the one store. Unserialized, two of them
        // counting at once both read the same value and one count is lost.
        lock (PairingCodeLockoutStoreSynchronization.For(_pairingCodeLockoutStore))
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

        // The round within the attempt, 1 for the first; advanced by each client/pair-retry.
        public uint Round = 1;

        // Set by the server/pair-init that begins a dynamic round and spent by that round's
        // server/pair-auth, so each counted round admits exactly one CPace run.
        public bool RoundBegun;

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

        // Read only inside an attempt, and every attempt re-reads it from its activation —
        // but it is cleared with the rest of the attempt state so no read can ever see a
        // value from an attempt that has already ended. (The presenter's language hint is
        // connection-scoped since spec #178 and deliberately survives an attempt.)
        _activationPairingCodeFormat = null;

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

        // A pairing never fails for lack of record storage (spec #183): if the store is at
        // capacity, a non-live long-term record is evicted to make room. Replacing this
        // server's own previous record — the one-record-per-server rule — happens in here too,
        // so a re-pair does not consume a second slot.
        lock (_pairingStoreLock)
        {
            PairingRecords.PersistLongTerm(
                _pairingStore,
                psk,
                ServerId,
                LiveRecordPskIds(),
                _logger);
        }

        _logger.LogInformation("Pairing complete: long-term record persisted for {ServerId}", ServerId);
        _pairingWindow?.CloseFor(this);
        PairingCompleted?.Invoke(this, ServerId);
    }

    /// <summary>
    /// The <c>psk_id</c>s that back a currently-open connection and therefore must never be
    /// evicted to make room for a new pairing.
    /// </summary>
    /// <remarks>
    /// Always includes this connection's own matched record, whose entry in the host's registry
    /// is not guaranteed to be visible yet on a connection still being admitted. The rest comes
    /// from the host, which is the only object that knows about sibling connections; a
    /// standalone client has no siblings and contributes nothing.
    /// </remarks>
    private IReadOnlyCollection<string> LiveRecordPskIds()
    {
        var live = new HashSet<string>(StringComparer.Ordinal);
        if (_session.MatchedPsk is { } matched)
        {
            live.Add(NoiseConstants.DerivePskId(matched.Key.Span));
        }

        if (_liveRecordPskIds is not null)
        {
            foreach (string id in _liveRecordPskIds())
            {
                live.Add(id);
            }
        }

        return live;
    }

    /// <summary>
    /// A paired server dropped its own pairing record: remove this server's long-term record —
    /// there is exactly one per server (spec #183) — say goodbye with reason 'unpaired', and
    /// close. Ignored on a session not keyed by a long-term record.
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
                // A long-term record is bound to the server the pairing created it for, so
                // only that server may retire it — one record per server (spec #183), and a
                // server cannot unpair another's. A record migrated from the pre-#183 on-disk
                // format has no server_id to check against; the matched psk_id is then the
                // only binding there is, and the message still means what it says.
                var record = _pairingStore.List().FirstOrDefault(r => r.PskId == pskId);
                if (record is not null
                    && (record.ServerId is null
                        || string.Equals(record.ServerId, _session.ServerId, StringComparison.Ordinal)))
                {
                    _pairingStore.Remove(pskId);
                    removed = true;
                }
                else if (record is not null)
                {
                    _logger.LogWarning(
                        "server/unpair from {ServerId} names a record bound to {RecordServerId}; "
                        + "ignoring the removal.", _session.ServerId, record.ServerId);
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
    /// pairing activity without playback. Such an activation admits nothing but pairing
    /// messages onto the wire, so the initial client/state is then withheld — even for roles
    /// that need no clock sync — until the first activate that is not pairing-only (see
    /// <see cref="_initialClientStateHeldForPairing"/>).</param>
    /// <returns>
    /// True when the connection survived its own promotion to Connected, so the rest of the
    /// activate may run. False when it was closed from inside <c>MarkConnected</c>'s state
    /// dispatch, in which case nothing this activate implies belongs to it any more.
    /// </returns>
    private bool FinishHandshake(bool pairing)
    {
        // MarkConnected publishes Connected synchronously into the app's handlers, and a
        // handler may send: restoring a saved volume there is the obvious thing to write. So
        // both send gates are set before it. The pairing gate is set as this activate declares
        // it, or the handler's message lands on a pairing-only wire. The initial client/state
        // is withheld whatever the activation, or the handler's call becomes an "initial" sent
        // ahead of the resets below and this method then sends a second one. The call still
        // stores its values, and the initial sent from here reads them. All three fields are
        // this client's own, so setting them for a connection that turns out to have closed
        // touches nothing shared.
        _pairingActivationActive = pairing;
        _initialClientStateSent = false;
        _initialClientStateHeldForPairing = true;

        // Mark connection as fully connected
        if (_connection is SendspinConnection conn)
        {
            conn.MarkConnected();
        }
        else if (_connection is IncomingConnection incoming)
        {
            incoming.MarkConnected();
        }

        // MarkConnected publishes Connected synchronously — through this client's own
        // ConnectionStateChanged and on into whatever the embedder subscribed, which in host
        // mode is the handshake waiter and, behind it, the ServerConnected handler that decides
        // whether to keep this server at all (#253). An embedder that refuses it there has
        // already closed this connection by the time MarkConnected returns: both transports
        // publish Disconnecting before their first await, so the close is visible here even when
        // the refusal was fire-and-forget.
        //
        // Everything below is connection-scoped work, but the state it touches need not be:
        // the clock synchronizer and the audio pipeline may be shared across every connection
        // the embedder runs, so resetting them for a connection that no longer exists corrupts
        // the session that is still playing. Same predicate OnTextMessageReceived applies to
        // inbound frames, and for the same reason: once we have decided to close, nothing the
        // peer sent may still take effect.
        if (_connection.State is ConnectionState.Disconnected or ConnectionState.Disconnecting)
        {
            _logger.LogInformation(
                "Connection closed inside its own handshake completion ({State}); abandoning the rest of the activate",
                _connection.State);
            return false;
        }

        // Reset clock synchronizer for new connection
        _clockSynchronizer.Reset();

        // Notify audio pipeline of reconnect to suppress sync corrections
        // while the Kalman filter re-converges (~2 seconds).
        // Safe to call even on initial connection: _audioPipeline is null before first stream/start,
        // and NotifyReconnect on null buffer/player is a no-op.
        _audioPipeline?.NotifyReconnect();

        // Restore any persisted output_delay_ms before reporting initial state, so the server
        // sees the calibrated delay immediately on (re)connect. No-op when no store is configured.
        LoadPersistedOutputDelay();

        // Per-connection latches, reset here with the rest of the per-connection state: the
        // initial client/state must be sent again (its latch was cleared above, ahead of
        // MarkConnected, and the hold kept it clear), and sync must be re-established before
        // this connection may claim availability (the synchronizer was reset above, so for a
        // clock that reports unconverged after reset the two now agree).
        _hasConvergedOnce = false;
        _initialClientStateHeldForPairing = pairing;
        _loggedDisplayDropWhileUnavailable = false;

        // Role-state readiness is per connection too (spec PR #204): the new server has received
        // nothing yet, so every role's binary channel starts closed until this connection sends
        // that role's client/state object. The one-shot warnings reset with it, so a server that
        // streams ahead of the gate says so once per connection rather than once per process.
        lock (_roleStateSentLock)
        {
            _roleStateSent.Clear();
            _warnedUngatedRoles.Clear();
        }

        // The availability tracker belongs to the connection that seeded it: left set, a
        // reconnect whose composed availability happens to match the old value would find
        // "already sent" and never tell the new server anything.
        lock (_availabilityLock)
        {
            _lastAvailabilitySent = null;
        }

        // Connection-scoped work started from here on belongs to this connection and dies with
        // it — see the field's remarks for the orphan this replaced.
        BeginConnectionLifetime();

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
        return true;
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
        // Not while the connection's first activation is still the pairing-only one. SendAsync
        // would drop the message without an error, and latching for a message that never went
        // out leaves the activate after pairing believing there is nothing to send. That
        // activate sends it instead, reading every value live. FinishHandshake holds it the
        // same way while it publishes Connected, and sends it itself afterwards.
        if (_initialClientStateHeldForPairing)
        {
            return;
        }

        // Latched before the send: once per connection, even if a re-convergence races a
        // send still in flight. A send that fails here is corrected by the next reconnect,
        // which resets the latch with the rest of the per-connection state.
        _initialClientStateSent = true;

        // Role objects follow active_roles, not capabilities, and every active role's object is
        // included — SendClientStateAsync composes both rules. A client whose active_roles are
        // non-empty still sends this message when none of its roles defines a state object
        // (spec PR #181): available alone is what opens the server's streams for those roles.
        await SendClientStateAsync(initial: true);

        // Also apply to audio pipeline to ensure consistency
        _audioPipeline?.SetVolume(_playerState.Volume);
        _audioPipeline?.SetMuted(_playerState.Muted);
    }

    private void StartTimeSyncLoop()
    {
        StopTimeSyncLoop();

        // A fresh converging window for the new loop: this runs on (re)connect and on the
        // return from a pairing window, both of which are moments where reaching the
        // convergence gate promptly is worth the extra probes again.
        _convergingBurstsSpent = 0;
        _convergingBudgetExhausted = false;

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
    /// Arms a fresh <see cref="_connectionLifetimeCts"/> for a connection that has just
    /// completed its handshake, cancelling any left over from the previous one.
    /// </summary>
    private void BeginConnectionLifetime()
        => CancelConnectionLifetime(Interlocked.Exchange(ref _connectionLifetimeCts, new CancellationTokenSource()));

    /// <summary>
    /// Cancels connection-scoped work — currently the stream-start rescue burst — because the
    /// connection has ended.
    /// </summary>
    private void EndConnectionLifetime()
        => CancelConnectionLifetime(Interlocked.Exchange(ref _connectionLifetimeCts, null));

    private static void CancelConnectionLifetime(CancellationTokenSource? cts)
    {
        if (cts is null)
        {
            return;
        }

        // Cancel before Dispose, and Cancel runs its registrations synchronously, so anything
        // awaiting this token has already been released by the time the source goes away.
        cts.Cancel();
        cts.Dispose();
    }

    /// <summary>
    /// Interval to wait before the next time-sync burst: fast while the clock is still
    /// converging, the reference's steady-state cadence once it has.
    /// </summary>
    /// <remarks>
    /// Two tiers, not a ladder. The ladder this replaced switched to slow pacing at three
    /// measurements while convergence needs five, so on a good network — where uncertainty
    /// drops under a millisecond after the third burst — measurements four and five each
    /// arrived 10 s late and the player took over twenty seconds to appear on the server,
    /// slower on a good network than on a poor one (#226). Keying the tier on convergence
    /// itself, rather than on a proxy for it, is what makes that impossible to reintroduce.
    /// <para>
    /// The fast tier is a per-loop budget of <see cref="MaxConvergingBursts"/> bursts, not a
    /// mode that lasts until convergence: a link that cannot reach the gate would otherwise
    /// hold the loop at 500 ms indefinitely. Spending the budget is not fatal — the client
    /// keeps probing at the steady cadence and reports <c>available</c> whenever the gate is
    /// finally met — and the budget is refilled by <see cref="StartTimeSyncLoop"/>, so a
    /// reconnect or a return from a pairing window gets a fresh converging window.
    /// </para>
    /// <para>
    /// Marked <c>internal</c> so the cadence tiers can be asserted directly; the only
    /// production caller is <see cref="TimeSyncLoopAsync"/>.
    /// </para>
    /// </remarks>
    internal int GetAdaptiveTimeSyncIntervalMs()
    {
        var status = _clockSynchronizer.GetStatus();

        if (status.IsConverged)
        {
            return SyncedTimeSyncIntervalMs;
        }

        if (_convergingBurstsSpent < MaxConvergingBursts)
        {
            _convergingBurstsSpent++;
            return ConvergingTimeSyncIntervalMs;
        }

        if (!_convergingBudgetExhausted)
        {
            _convergingBudgetExhausted = true;
            _logger.LogWarning(
                "Clock sync has not converged after {Bursts} bursts (offset uncertainty " +
                "{Uncertainty:F0}μs over {Count} measurements); falling back to the {Interval}ms " +
                "steady-state cadence. Probing faster than this cannot fix a link this noisy, and " +
                "this client will not report available until the filter converges.",
                MaxConvergingBursts,
                status.OffsetUncertaintyMicroseconds,
                status.MeasurementCount,
                SyncedTimeSyncIntervalMs);
        }

        return SyncedTimeSyncIntervalMs;
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
    /// The stream-start rescue burst: a one-off burst on the connection's lifetime rather than
    /// on the time-sync loop's, for a stream starting before the clock has minimal sync.
    /// </summary>
    /// <remarks>
    /// The token is the whole point — see <see cref="_connectionLifetimeCts"/>. It has to
    /// survive <see cref="StopTimeSyncLoop"/>, because a pairing activation stops the loop and
    /// this burst answers to a pairing gate of its own, but it has to die with the connection.
    /// A null source means the connection ended (or never finished its handshake), so there is
    /// nothing to rescue.
    /// </remarks>
    private Task SendRescueSyncBurstAsync()
    {
        if (Volatile.Read(ref _connectionLifetimeCts) is not { } lifetime)
        {
            return Task.CompletedTask;
        }

        try
        {
            return SendTimeSyncBurstAsync(lifetime.Token);
        }
        catch (ObjectDisposedException)
        {
            // A disconnect can cancel-and-dispose the source between the read above and the
            // Token property evaluating. The connection is dying either way — same as null.
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Sends a burst of NTP-style time-sync probes sequentially and feeds the lowest-RTT
    /// sample into the clock synchronizer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reference burst strategy, followed here: each probe is sent as soon as the previous
    /// one is answered — no fixed spacing, since the reply is the pacing signal — and a probe
    /// that times out advances to the next one rather than abandoning the burst. Both matter
    /// for the same reason: the burst exists to collect candidates so the cleanest can be
    /// chosen, and one slow reply is exactly the condition under which the remaining seven are
    /// worth having. The previous shape (50 ms between probes, abort on the first timeout)
    /// turned a single stalled reply into a one-sample burst (#225).
    /// </para>
    /// <para>
    /// Marked <c>internal</c> for direct invocation from concurrent-burst regression tests;
    /// production callers reach this via <see cref="StartTimeSyncLoop"/> or
    /// <see cref="HandleStreamStartAsync"/>'s smart-sync trigger.
    /// </para>
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
                    continue; // probe timed out; the next one still gets its chance

                // A round trip of zero or less is a corrupt exchange, not a fast one — the
                // server clock stepped between T2 and T3, its two stamps came from different
                // sources, or a counter jumped. Because burst-best selection prefers the
                // LOWEST round trip, such a sample would always win and would then enter the
                // filter with a near-zero variance that drives the Kalman gain to 1. The
                // reference drops it as it arrives, before it can be a candidate at all (#224).
                if (sample.Value.Rtt <= 0)
                {
                    _logger.LogWarning(
                        "Dropping time response {Index}/{Total} with non-positive round trip: {Rtt:F0}μs",
                        i + 1, BurstSize, sample.Value.Rtt);
                    continue;
                }

                samples.Add(sample.Value);
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
    /// Returns null if the probe was dropped or the reply doesn't arrive within ProbeTimeoutMs.
    /// </summary>
    /// <remarks>
    /// T1 is not stamped here. The transport stamps it at the send point and hands it back
    /// through <see cref="ISendspinConnection.SendTimeMessageAsync"/>'s callback, which is what
    /// keeps serialization, encryption and send-queue latency out of the measured round trip
    /// (#227). The callback runs before the frame reaches the socket, so the reply-matching
    /// slot below is always populated ahead of any answer to it.
    /// </remarks>
    private async Task<TimeSyncSample?> SendSingleProbeAsync(int index, CancellationToken cancellationToken)
    {
        // The gate SendAsync applies to every other message. Probes bypass SendAsync now that
        // the transport builds them, so the rule is restated rather than inherited: the
        // reference server stops reading the socket during a pairing attempt and treats the
        // first frame it reads afterwards as the next pairing message, so a probe sent into
        // that window aborts the attempt as a protocol error.
        if (_pairingActivationActive)
        {
            _logger.LogDebug("Pairing activation in effect; dropping ClientTimeMessage");
            return null;
        }

        var tcs = new TaskCompletionSource<TimeSyncSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        long t1 = 0;

        try
        {
            await _connection.SendTimeMessageAsync(
                transmitted =>
                {
                    t1 = transmitted;
                    lock (_burstLock)
                    {
                        _burstInFlight = tcs;
                        _burstInFlightT1 = transmitted;
                    }
                },
                cancellationToken).ConfigureAwait(false);
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

        // Media and state held for a display moment translate their server timestamps through
        // this offset every time the scheduler looks at them, so this correction reaches them on
        // its own — but not the sleep the scheduler is already in, which was sized against the
        // previous offset. Before the first measurement that sleep runs until the server's
        // uptime has elapsed on the local clock, so the very first correction is the one that
        // most needs the loop woken to notice it.
        _displayScheduler.NotifyClockAdjusted();

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

    /// <summary>
    /// Completes the in-flight probe this <c>server/time</c> answers, turning the four
    /// timestamps into a burst sample.
    /// </summary>
    /// <param name="json">The received <c>server/time</c> payload.</param>
    /// <param name="t4">
    /// The client receive time the transport captured before this frame was decrypted and
    /// parsed. Passed in rather than read here: a T4 taken after deserialization charges
    /// decrypt and parse time to the round trip, inflating <c>max_error</c> and biasing the
    /// offset by half the send/receive asymmetry (#227).
    /// </param>
    private void HandleServerTime(string json, long t4)
    {
        var message = MessageSerializer.Deserialize<ServerTimeMessage>(json);
        if (message is null) return;

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
            // supported_commands belongs to the previous group; drop it until the new group's server/state.
            _currentGroup.SupportedCommands = null;

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

        // Each role object is itself Optional: absent = no change, present-null = clear all of
        // that role's state (sent when the role leaves active_roles, and on pairing quiesce),
        // present-with-value = the role's full state (spec #175). Clearing one role leaves the
        // others alone. Every branch below is announced by the GroupStateChanged at the end of
        // this method, which is how a UI learns to drop the deactivated role's data (#196). What
        // that announcement carries is the state as it stands: a scheduled metadata or color
        // update has not been applied yet and announces itself when it is (spec #135, pending merge).

        // Each object counts "only if the ... role is active" (messaging.md). One for a role that
        // is not is ignored, a null one included: the activate that removed the role already
        // cleared its state, and applying a late object would bring it back.
        bool metadataPresent = payload.Metadata.IsPresent && IsRoleActive("metadata");
        bool controllerPresent = payload.Controller.IsPresent && IsRoleActive("controller");
        bool colorPresent = payload.Color.IsPresent && IsRoleActive("color");
        if (!metadataPresent && !controllerPresent && !colorPresent)
        {
            _logger.LogDebug("server/state [{Player}]: no object for an active role; ignored",
                _capabilities.ClientName);
            return;
        }

        // Apply the metadata role. Full state per spec #175: the object is the role's complete
        // metadata, so a leaf it omits is unset — ApplyMetadata builds from it alone rather than
        // merging against what is held.
        if (metadataPresent)
        {
            var meta = payload.Metadata.Value;

            // A future timestamp defers the apply to that moment; anything else — a past or
            // present timestamp, no timestamp, or the null role object — applies now and
            // discards whatever update was being held.
            long? takesEffectAt = meta?.Timestamp.GetValueOrDefault();

            if (!_displayScheduler.TryScheduleStateUpdate(
                    ScheduledStateRole.Metadata,
                    takesEffectAt,
                    () => ApplyScheduledMetadata(meta)))
            {
                ApplyMetadata(_currentGroup, meta);
            }
        }

        // Update controller state for UI display only.
        // Do NOT apply volume to the audio pipeline - server/state contains GROUP volume.
        // The server sends server/command with player-specific volume when it wants
        // to change THIS player's output.
        // Per the Sendspin spec, repeat/shuffle live in the controller object (not metadata).
        if (controllerPresent)
        {
            if (payload.Controller.Value is not { } controller)
            {
                ClearControllerState(_currentGroup);
            }
            else
            {
                if (controller.Volume.HasValue)
                    _currentGroup.Volume = controller.Volume.Value;
                if (controller.Muted.HasValue)
                    _currentGroup.Muted = controller.Muted.Value;
                if (controller.Repeat is not null)
                    _currentGroup.Repeat = controller.Repeat;
                if (controller.Shuffle.HasValue)
                    _currentGroup.Shuffle = controller.Shuffle.Value;

                // Full state per spec #175: supported_commands and seek_max_ms are unset when the
                // controller object omits them, not kept from the last one. This matters for the
                // client/command gate (MaySendControllerCommand), which authorises a command only
                // while it is in the current supported_commands — a stale list would let it send a
                // command the latest state no longer advertises. Absence and an explicit null both
                // read as unset. The always-reported siblings above stay keep-on-absent: a
                // conformant server never omits them, and Volume/Muted are non-nullable with no
                // "unset" to clear to.
                _currentGroup.SupportedCommands = controller.SupportedCommands;
                _currentGroup.SeekMaxMs = controller.SeekMaxMs.GetValueOrDefault();
            }
        }

        // Apply the color role. Full state per spec #175: the object is the complete palette, so a
        // color it omits is unset — ApplyColor takes each from it alone. Scheduled as metadata is.
        var colorChanged = false;
        if (colorPresent)
        {
            var color = payload.Color.Value;

            if (!_displayScheduler.TryScheduleStateUpdate(
                    ScheduledStateRole.Color,
                    color?.Timestamp,
                    () => ApplyScheduledColor(color)))
            {
                ApplyColor(_currentGroup, color);
                colorChanged = true;
            }
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
    /// Applies a <c>metadata</c> role object to the current state: a null object clears the role,
    /// anything else replaces the metadata with the object's full state (spec #175). A leaf the
    /// object omits is unset — nothing carries forward from the previous metadata.
    /// </summary>
    /// <param name="group">
    /// The group state to write, resolved by the caller. Passed rather than read from
    /// <see cref="_currentGroup"/> here, because the scheduled callers run on the scheduler loop
    /// and must not create a group state the disconnect that raced them has already dropped.
    /// </param>
    /// <param name="meta">The role object, or null to clear the role.</param>
    private static void ApplyMetadata(GroupState group, ServerMetadata? meta)
    {
        if (meta is null)
        {
            group.Metadata = null;
            return;
        }

        // Built from the object alone, never merged against what is held: spec #175 makes the
        // object the role's complete state, so an omitted leaf is unset (absent and explicit-null
        // both read as null through GetValueOrDefault) rather than a value kept from before.
        group.Metadata = new TrackMetadata
        {
            Timestamp = meta.Timestamp.GetValueOrDefault(),
            Title = meta.Title.GetValueOrDefault(),
            Artist = meta.Artist.GetValueOrDefault(),
            AlbumArtist = meta.AlbumArtist.GetValueOrDefault(),
            Album = meta.Album.GetValueOrDefault(),
            ArtworkUrl = meta.ArtworkUrl.GetValueOrDefault(),
            Year = meta.Year.GetValueOrDefault(),
            Track = meta.Track.GetValueOrDefault(),
            Progress = meta.Progress.GetValueOrDefault()
        };
    }

    /// <summary>
    /// Returns every field the controller role owns to the value a group carries before the
    /// server has reported any of them — read off a fresh <see cref="GroupState"/> rather than
    /// repeating its literals, so the two cannot drift. Used for an explicit <c>null</c>
    /// controller object and when the role leaves <c>active_roles</c> (spec PR #275).
    /// </summary>
    private static void ClearControllerState(GroupState group)
    {
        var unreported = new GroupState();
        group.Volume = unreported.Volume;
        group.Muted = unreported.Muted;
        group.Repeat = unreported.Repeat;
        group.Shuffle = unreported.Shuffle;
        group.SupportedCommands = unreported.SupportedCommands;
        group.SeekMaxMs = unreported.SeekMaxMs;
    }

    /// <summary>
    /// Applies a <c>color</c> role object to the current palette, in place rather than replacing
    /// it, so a consumer holding the <see cref="ColorPalette"/> it was handed by an earlier
    /// <see cref="ColorChanged"/> sees the update — including a clear. Full state per spec #175:
    /// the object is the complete palette, so a color it omits is unset (a null object clears
    /// every color), taken from the object alone rather than merged against what is held.
    /// </summary>
    /// <param name="group">
    /// The group state whose palette is written — see <see cref="ApplyMetadata"/> on why it is
    /// the caller that resolves it.
    /// </param>
    /// <param name="color">The role object, or null to clear the role.</param>
    private static void ApplyColor(GroupState group, ColorState? color)
    {
        var colors = group.Colors;

        if (color is null)
        {
            colors.Timestamp = null;
            colors.BackgroundDark = null;
            colors.BackgroundLight = null;
            colors.Primary = null;
            colors.Accent = null;
            colors.OnDark = null;
            colors.OnLight = null;
            return;
        }

        colors.Timestamp = color.Timestamp;
        colors.BackgroundDark = color.BackgroundDark.GetValueOrDefault();
        colors.BackgroundLight = color.BackgroundLight.GetValueOrDefault();
        colors.Primary = color.Primary.GetValueOrDefault();
        colors.Accent = color.Accent.GetValueOrDefault();
        colors.OnDark = color.OnDark.GetValueOrDefault();
        colors.OnLight = color.OnLight.GetValueOrDefault();
    }

    /// <summary>
    /// Applies a held <c>metadata</c> update at its scheduled moment and announces it. The
    /// <c>server/state</c> that carried it announced only the state as it stood then, so the
    /// merge needs an announcement of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs on the scheduler's loop, not the receive loop — see
    /// <see cref="ISendspinClient.GroupStateChanged"/> on threading.
    /// </para>
    /// <para>
    /// Which is also why the group is resolved once and the whole apply abandoned when there is
    /// none: <see cref="DisconnectAsync"/> drops <see cref="_currentGroup"/> after flushing the
    /// scheduler, and the flush only empties the pending slots — an update the loop had already
    /// lifted out of its slot is past that point and lands here with the connection gone. Every
    /// read after this line goes through the local, so nothing can create a group state for a
    /// connection that has ended, and nothing dereferences a field that may have just been
    /// nulled. The next connection's first <c>server/state</c> carries each role in full anyway.
    /// </para>
    /// </remarks>
    private void ApplyScheduledMetadata(ServerMetadata? meta)
    {
        if (_currentGroup is not { } group)
        {
            return;
        }

        ApplyMetadata(group, meta);
        GroupStateChanged?.Invoke(this, group);
    }

    /// <summary>
    /// Applies a held <c>color</c> update at its scheduled moment and announces it, on the
    /// scheduler's loop. <see cref="ColorChanged"/> marks the moment the palette takes effect,
    /// which is here and not when the update was merely scheduled.
    /// </summary>
    /// <remarks>
    /// Resolves the group once and abandons the apply when there is none, for the reason
    /// <see cref="ApplyScheduledMetadata"/> gives.
    /// </remarks>
    private void ApplyScheduledColor(ColorState? color)
    {
        if (_currentGroup is not { } group)
        {
            return;
        }

        ApplyColor(group, color);
        GroupStateChanged?.Invoke(this, group);
        ColorChanged?.Invoke(this, group.Colors);
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

        // The player object is valid "only if the player role is active": a server that left
        // player out of active_roles has no say over this client's output.
        if (!IsRoleActive("player"))
        {
            _logger.LogDebug("server/command: ignoring player command '{Command}', player is not an active role",
                player.Command);
            return;
        }

        _logger.LogDebug("server/command: {Command}", player.Command);

        // One command per message, named by 'command'; each applies only its own parameter, so a
        // parameter belonging to another command is not acted on. Anything else — an unknown
        // command, one missing its parameter, or one absent from supported_commands — is ignored.
        switch (player.Command)
        {
            // Updates _playerState (this player's volume), not _currentGroup (group average).
            // Clamped to the spec's 0-100 here, as SendPlayerStateAsync does for an app-set
            // volume, so the app and the acknowledgement see the same value.
            case Commands.Volume when player.Volume is { } requestedVolume:
                var volume = Math.Clamp(requestedVolume, 0, 100);
                _playerState.Volume = volume;
                _audioPipeline?.SetVolume(volume);
                _logger.LogInformation("server/command [{Player}]: Applied volume {Volume}",
                    _capabilities.ClientName, volume);
                break;

            case Commands.Mute when player.Mute is { } mute:
                _playerState.Muted = mute;
                _audioPipeline?.SetMuted(mute);
                _logger.LogInformation("server/command [{Player}]: Applied mute {Muted}",
                    _capabilities.ClientName, mute);
                break;

            // Apply set_output_delay only when advertised as supported and a value is present. Per spec
            // the value is 0-5000 ms (negatives are not supported); the clock synchronizer's setter is
            // the single clamp site, so the requested value is handed to it and the applied result read
            // back for persistence and the log.
            // Spec 168a677 (spec PR #164) renamed the command from 'set_static_delay' and the field
            // from 'static_delay_ms' with no alias; the 10.x line accepts only the new names.
            case Commands.SetOutputDelay
                when _capabilities.SupportsSetOutputDelay && player.OutputDelayMs is { } requestedDelayMs:
                _clockSynchronizer.OutputDelayMs = requestedDelayMs;
                var applied = _clockSynchronizer.OutputDelayMs;
                TrySaveOutputDelay(applied);
                _logger.LogInformation("server/command [{Player}]: Applied output delay {Delay}ms",
                    _capabilities.ClientName, applied);
                break;

            default:
                return;
        }

        PlayerStateChanged?.Invoke(this, _playerState);

        // Per spec: send client/state to confirm the applied state back to the server.
        SendPlayerStateAckAsync().SafeFireAndForget(_logger);
    }

    /// <summary>
    /// Re-reports the full client state after a pairing activation ends, restoring the server's
    /// view of anything the window dropped.
    /// </summary>
    /// <remarks>
    /// Deliberately the full state rather than a fragment: the client cannot know which of its
    /// values the server last saw, because it does not track what the gate discarded. Since spec
    /// PR #175 every client/state is full state anyway, so this is simply the ordinary send —
    /// the only thing pairing-specific left here is when it happens.
    /// </remarks>
    private async Task ResendClientStateAfterPairingAsync()
    {
        if (_connection.State != ConnectionState.Connected)
        {
            return;
        }

        await SendClientStateAsync();
    }

    /// <summary>
    /// Sends a client/state acknowledgement after applying a server/command.
    /// Reports current player volume and mute state back to the server.
    /// </summary>
    private async Task SendPlayerStateAckAsync()
    {
        await SendPlayerStateAsync(_playerState.Volume, _playerState.Muted, _clockSynchronizer.OutputDelayMs);
    }


    /// <summary>
    /// Restores the persisted output delay (if a store is configured and a value exists) into the
    /// clock synchronizer. Called on each handshake before the initial client/state is reported.
    /// </summary>
    /// <remarks>
    /// Best-effort: a throwing or out-of-range store must not abort the handshake (the initial
    /// client/state and time-sync loop run after this). On failure we log and continue without the
    /// persisted delay. The synchronizer's setter is what keeps the applied delay in the spec's
    /// 0-5000 range; the bound applied here is redundant with it and only makes the debug line
    /// report the value that was applied.
    /// </remarks>
    private void LoadPersistedOutputDelay()
    {
        if (_outputDelayStore is null)
        {
            return;
        }

        double? stored;
        try
        {
            stored = _outputDelayStore.Load();
        }
        catch (Exception ex)
        {
            // Deliberately broad, reviewed under #109. IOutputDelayStore is implemented by the
            // embedder over a store the SDK never sees — file, registry, SQLite, a cloud
            // key-value API — so there is no set of types to narrow to; a filter naming
            // IOException would let a database provider's own exception abort the handshake.
            // The interface docs ask for a non-throwing implementation, which is exactly why
            // this exists: it is the guard for the implementations that are not. Degrading is
            // right here — a delay we could not read is a lost calibration, not a lost session,
            // and the handshake behind this call still has an initial client/state to send.
            _logger.LogError(ex, "IOutputDelayStore.Load() threw; continuing without persisted output delay");
            return;
        }

        if (!stored.HasValue)
        {
            return;
        }

        if (!double.IsFinite(stored.Value))
        {
            _logger.LogWarning("Persisted output delay was not finite ({Delay}); ignoring", stored.Value);
            return;
        }

        var clamped = Math.Clamp(stored.Value, MinOutputDelayMs, MaxOutputDelayMs);
        _clockSynchronizer.OutputDelayMs = clamped;
        _logger.LogDebug("Restored persisted output delay: {Delay:+0.0;-0.0}ms", clamped);
    }

    /// <summary>
    /// Best-effort persistence of the output delay. A throwing store must never break command
    /// or sync-offset handling — log and continue so the in-memory delay, state event, and ack still flow.
    /// </summary>
    private void TrySaveOutputDelay(double outputDelayMs)
    {
        if (_outputDelayStore is null)
        {
            return;
        }

        try
        {
            _outputDelayStore.Save(outputDelayMs);
        }
        catch (Exception ex)
        {
            // Deliberately broad for the same reason as LoadPersistedOutputDelay's catch (#109):
            // an embedder-implemented store has no enumerable failure set. Degrading is the
            // stronger answer on the save side, because the callers are a server/command and a
            // GroupSync offset — the delay is already applied in memory and already
            // acknowledged, so throwing here would fail a command that in fact succeeded.
            _logger.LogError(ex, "IOutputDelayStore.Save({Delay}ms) threw; output delay applied in-memory but not persisted", outputDelayMs);
        }
    }

    /// <summary>
    /// Runs a stream-lifecycle handler after every lifecycle handler dispatched before it, without
    /// making the receive loop wait for any of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>stream/start</c>, <c>stream/end</c> and <c>stream/clear</c> all reach into the audio
    /// pipeline, whose start and stop open and close an output device — which is why they are
    /// handled off the receive loop in the first place. Dispatched independently they can also
    /// <i>land</i> independently: a track boundary sends <c>stream/end</c> then
    /// <c>stream/start</c> back to back, and the end's teardown finishing after the start's build
    /// leaves the pipeline stopped for a stream the server has started — silence until the next
    /// track. Chaining each handler onto the one before restores the wire order at the point the
    /// handlers take effect.
    /// </para>
    /// <para>
    /// The chain is only a queue, not a thread: a handler dispatched while the chain is idle still
    /// runs inline on the receive loop up to its first real await, exactly as the bare
    /// fire-and-forget did. The receive loop never waits for a handler already in flight.
    /// </para>
    /// <para>
    /// Only the lifecycle messages take this path. The binary audio chunks, which arrive at chunk
    /// rate, stay on the receive loop — so a message that has to wait here would be overtaken by
    /// the chunks sent after it, and they would be decoded, or cleared, under the configuration
    /// it replaces. Such a message holds them back instead: they queue from the moment it is
    /// received and are handed over, in order, once it has run. A message that runs at once
    /// holds nothing back, and neither does one that leaves the player alone.
    /// </para>
    /// </remarks>
    /// <param name="handler">
    /// The handler to run once the chain reaches it, given the number of the player configuration
    /// its message was received under — its own, when <paramref name="changesPlayer"/>.
    /// </param>
    /// <param name="changesPlayer">
    /// Whether the message changes what the pipeline does with the chunks that follow it.
    /// </param>
    private void DispatchStreamLifecycle(Func<int, Task> handler, bool changesPlayer)
    {
        Task predecessor;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Held for two field accesses only. Handlers run outside it, so nothing this class does
        // under the lock can reach an application event handler.
        lock (_streamLifecycleLock)
        {
            predecessor = _streamLifecycleChain;
            _streamLifecycleChain = completion.Task;
        }

        int config;
        lock (_audioHandoffLock)
        {
            config = changesPlayer ? ++_playerConfigReceived : _playerConfigReceived;

            // Nothing ahead of it: the handler runs below, on this thread, before the receive
            // loop reads another frame. The pipeline's own readiness covers the rest of it.
            if (changesPlayer && predecessor.IsCompleted)
            {
                _playerConfigApplied = config;
            }
        }

        RunStreamLifecycleAsync(predecessor, handler, changesPlayer, config, completion).SafeFireAndForget(_logger);
    }

    private async Task RunStreamLifecycleAsync(
        Task predecessor, Func<int, Task> handler, bool changesPlayer, int config, TaskCompletionSource completion)
    {
        try
        {
            if (!predecessor.IsCompleted)
            {
                // Never faults: every link completes its own slot in the finally below, so a
                // handler that throws stops at its own SafeFireAndForget and the next one still
                // runs. A lifecycle message must not be skipped because the one before it failed.
                await predecessor.ConfigureAwait(false);
            }

            await handler(config).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                // Before the slot completes, so a chain that reads as idle has nothing held
                // back — and here, so a handler that throws does not leave audio queueing.
                if (changesPlayer)
                {
                    ReleaseEarlyChunks(config);
                }
            }
            finally
            {
                completion.SetResult();
            }
        }
    }

    /// <summary>
    /// Whether a <c>stream/start</c> carries a <c>player</c> object. Asked on the receive loop,
    /// where the message is otherwise only dispatched: the display roles' streams are started by
    /// messages of the same type, and one of those waiting behind the player's device open must
    /// not hold the opening burst back.
    /// </summary>
    private static bool HasPlayerObject(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        return doc.RootElement.TryGetProperty("payload", out var payload)
            && payload.ValueKind == System.Text.Json.JsonValueKind.Object
            && payload.TryGetProperty("player", out var player)
            && player.ValueKind != System.Text.Json.JsonValueKind.Null;
    }

    /// <summary>
    /// Whether a <c>stream/end</c> names the <c>player</c> role, or names none and so ends every
    /// stream. Asked on the receive loop for the same reason as <see cref="HasPlayerObject"/>: a
    /// server ends the visualizer's stream in the same breath as the player's, and that end
    /// waiting behind the player's device close must not hold audio back.
    /// </summary>
    private static bool EndNamesPlayer(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("payload", out var payload)
            || payload.ValueKind != System.Text.Json.JsonValueKind.Object
            || !payload.TryGetProperty("roles", out var roles)
            || roles.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return true;
        }

        foreach (var role in roles.EnumerateArray())
        {
            if (role.ValueKind == System.Text.Json.JsonValueKind.String && role.ValueEquals("player"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Marks the player configuration numbered <paramref name="config"/> as in effect and hands
    /// the pipeline the chunks that were held back for it, stopping at the first one received
    /// under a later configuration.
    /// </summary>
    private void ReleaseEarlyChunks(int config)
    {
        lock (_audioHandoffLock)
        {
            _playerConfigApplied = config;

            // After a stream/end there is no pipeline to take them: they stay queued, and the
            // next stream/start drops them as the previous stream's.
            if (_audioPipeline is not { IsReady: true } pipeline)
            {
                return;
            }

            while (_earlyChunkQueue.TryPeek(out var queued) && queued.Config <= config)
            {
                _earlyChunkQueue.TryDequeue(out _);
                pipeline.ProcessAudioChunk(queued.Chunk);
            }
        }
    }

    /// <summary>
    /// Drops the queued chunks received before the message that brought the player configuration
    /// numbered <paramref name="config"/>.
    /// </summary>
    private void DropEarlyChunksReceivedBefore(int config)
    {
        lock (_audioHandoffLock)
        {
            while (_earlyChunkQueue.TryPeek(out var queued) && queued.Config < config)
            {
                _earlyChunkQueue.TryDequeue(out _);
            }
        }
    }

    private async Task HandleStreamStartAsync(string json, bool playerActive, int config)
    {
        try
        {
            await HandleStreamStartCoreAsync(json, playerActive, config);
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

    private async Task HandleStreamStartCoreAsync(string json, bool playerActive, int config)
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
        DiscardArtworkForReconfiguredChannels(LastStreamStart?.Artwork, payload.Artwork);
        LastStreamStart = payload;
        StreamStartReceived?.Invoke(this, payload);

        // stream/start with no "player" key is artwork-only — skip pipeline start
        if (payload.Format is null)
        {
            _logger.LogDebug("Stream start is artwork-only (no player key), skipping pipeline start");
            return;
        }

        // The player object is valid "only if the player role is active": a connection the
        // server left player out of may not open this client's output. Keyed on the grant and
        // not on IsRoleBinaryPermitted — the reference server starts a held player without its
        // state object once its wait for one times out, and a start dropped then is never sent
        // again, so the stream would stay closed after the object did go out.
        if (!playerActive)
        {
            _logger.LogDebug("Stream start: ignoring player object, player is not an active role");
            return;
        }

        // "The format MUST be one the client listed in its supported_formats." The decoder, the
        // ring and the output device are all sized from this object, and an unpaired session's
        // peer is unauthenticated, so one this client never offered opens nothing. The spec
        // names no close for it, so the connection stays up — but the server now sends that
        // format, and a stream left running would put those chunks through the previous
        // format's decoder. The player stream ends as on a stream/end; chunks arriving after it
        // queue up to MaxEarlyChunks and are dropped by the next start as the previous stream's.
        if (!IsListedPlayerFormat(payload.Format))
        {
            _logger.LogWarning(
                "Stream start: player format {Format} is not one of this client's supported_formats; stopping the player stream",
                payload.Format);
            await StopStreamRolesAsync(new List<string> { "player" }, stopPlayer: true, config);
            return;
        }

        _logger.LogInformation("Stream starting: {Format}", payload.Format);

        // Smart sync burst: only trigger if clock isn't already synced
        // If we've been connected for a while, the continuous sync loop has already converged
        if (_pairingActivationActive)
        {
            // Same rule as the time-sync loop's gate in HandleServerActivate: no
            // client/time may leave the client while a pairing activation is in effect —
            // the reference server would read the probe where it requires the next pairing
            // message and abort the attempt. This burst is not the loop (it runs on the
            // connection's lifetime, so StopTimeSyncLoop cannot reach it) and it fires
            // without app action, so it is gated at the source: a stream/start crossing a
            // mid-session pairing activate on a clock without minimal sync must stay
            // silent. The loop's restart on the next non-pairing activate covers the
            // re-sync this burst would have provided.
            _logger.LogDebug("Pairing activation in effect, skipping stream-start sync burst");
        }
        else if (!_clockSynchronizer.HasMinimalSync)
        {
            _logger.LogDebug("Clock not synced, triggering re-sync burst (fire-and-forget)");
            SendRescueSyncBurstAsync().SafeFireAndForget(_logger);
        }
        else
        {
            _logger.LogDebug("Clock already synced ({MeasurementCount} measurements), skipping burst",
                _clockSynchronizer.GetStatus()?.MeasurementCount ?? 0);
        }

        // Start pipeline immediately - don't block on sync burst
        // The continuous sync loop + sync correction will handle any residual drift
        if (_audioPipeline == null)
        {
            // Nothing will ever drain the queue, so a client configured without a pipeline
            // would otherwise accumulate chunks until it hit MaxEarlyChunks and stayed there.
            while (_earlyChunkQueue.TryDequeue(out _))
            {
            }

            return;
        }

        // The group this start reports Playing on, resolved before the start rather than after
        // it. Creating one is what a stream/start means for a server that sends no group/update,
        // but it must not happen on the far side of the await: DisconnectAsync drops
        // _currentGroup, and `??=` down there republished a default Playing state for a
        // connection that had ended — the fault #247 fixed in the scheduled metadata and colour
        // applies. Resolving here and re-checking below keeps the create with the code that
        // still knows a stream is being started, and makes the announcement conditional on that
        // same group still being the client's.
        var group = _currentGroup ??= new GroupState();

        // A pipeline-start failure is a local fault, not peer input: the pipeline
        // reports it to the server itself (ErrorOccurred -> client/state: 'error'),
        // and it propagates from here so a real bug surfaces instead of being
        // collapsed into a log line (#88 item 2).
        var outcome = await _audioPipeline.StartAsync(payload.Format);

        // Held across the drop and the drain, and taken by the receive loop's hand-off, so a
        // chunk arriving mid-start cannot overtake the queue or decode through the pipeline's
        // scratch buffer at the same time as one being drained (see _audioHandoffLock).
        int drainedCount;
        lock (_audioHandoffLock)
        {
            // Only a re-announced format keeps them: that stream is still running and the queued
            // chunks are its own, so they are drained in below rather than dropped, per the spec's
            // "without clearing buffers" (#201). Every other outcome rebuilt the decoder, which
            // leaves anything encoded for the previous stream unreadable. Which of the two happened
            // is the pipeline's to decide and to report — deriving it here from its state and format
            // meant a second copy of the rule, free to drift from the one that matters.
            //
            // "Previous" goes by when a chunk was received, not by when this handler got to run:
            // behind a stream/end still closing the device, the new stream's opening chunks are
            // queued before this point, and they are this start's.
            if (outcome != AudioPipelineStartOutcome.FormatReannounced)
            {
                DropEarlyChunksReceivedBefore(config);
            }

            // Drain what is left: the chunks kept above, plus any that arrived during
            // initialization. Not the ones behind a later stream/start or stream/clear still
            // waiting its turn — that message hands them over when it has run.
            drainedCount = 0;
            while (_earlyChunkQueue.TryPeek(out var queued) && queued.Config <= config)
            {
                _earlyChunkQueue.TryDequeue(out _);
                _audioPipeline.ProcessAudioChunk(queued.Chunk);
                drainedCount++;
            }
        }

        if (drainedCount > 0)
        {
            _logger.LogDebug("Drained {Count} early chunks into pipeline", drainedCount);
        }

        // Infer Playing state from stream/start for servers that don't send group/update — unless
        // a disconnect took the group while the pipeline was starting, in which case there is no
        // longer anything for this stream to be Playing on.
        if (!ReferenceEquals(_currentGroup, group))
        {
            return;
        }

        group.PlaybackState = PlaybackState.Playing;
        GroupStateChanged?.Invoke(this, group);
    }

    /// <summary>
    /// Whether <paramref name="format"/> is an entry of the <c>supported_formats</c> this client
    /// sends in <c>client/hello</c>.
    /// </summary>
    /// <remarks>
    /// Codec, channels and sample rate always; bit depth for <c>pcm</c> only, where it is the
    /// decoder's sample width. The spec has it "ignored" for <c>opus</c>. For <c>flac</c> this is
    /// deliberately looser than the spec's "meaningful for pcm and flac": servers have announced
    /// 32 for 24-bit content (PyAV's s32 container), the decoder takes its scaling from
    /// STREAMINFO, and nothing on the FLAC path is sized from the announced depth — so matching
    /// it would only silence a stream that plays. An absent bit depth is 16 on both sides, which
    /// is what <c>client/hello</c> sends for an entry listed without one and what the PCM decoder
    /// assumes. <c>codec_header</c> is not part of an entry.
    /// </remarks>
    private bool IsListedPlayerFormat(AudioFormat format)
    {
        bool pcm = string.Equals(format.Codec, AudioCodecs.Pcm, StringComparison.OrdinalIgnoreCase);

        return _capabilities.AudioFormats.Any(f =>
            string.Equals(f.Codec, format.Codec, StringComparison.OrdinalIgnoreCase)
            && f.Channels == format.Channels
            && f.SampleRate == format.SampleRate
            && (!pcm || (f.BitDepth ?? 16) == (format.BitDepth ?? 16)));
    }

    /// <summary>
    /// Drops the image a channel is still holding when a <c>stream/start</c> changes that
    /// channel's configuration, per spec #135 (pending merge): the held image was encoded for a
    /// configuration that no longer applies, and the server re-sends it if it still does.
    /// </summary>
    /// <param name="previous">The artwork object of the last <c>stream/start</c>, if any.</param>
    /// <param name="current">The artwork object of the one being handled.</param>
    /// <remarks>
    /// A <c>stream/start</c> with no <c>artwork</c> object reconfigures nothing — it is a
    /// player-only or visualizer-only start — so it leaves every channel's pending image alone.
    /// A channel that disappears from the array is treated as changed: the server has stopped
    /// describing it, so nothing it sent for it may still surface.
    /// </remarks>
    private void DiscardArtworkForReconfiguredChannels(
        StreamStartArtwork? previous, StreamStartArtwork? current)
    {
        if (current is null)
        {
            return;
        }

        var before = previous?.Channels;
        var after = current.Channels;
        int channels = Math.Max(before?.Count ?? 0, after.Count);

        for (int channel = 0; channel < channels; channel++)
        {
            var wasConfigured = channel < (before?.Count ?? 0) ? before![channel] : null;
            var isConfigured = channel < after.Count ? after[channel] : null;

            if (!SameArtworkChannelConfiguration(wasConfigured, isConfigured))
            {
                // The channel's pending image is the transfer in flight until it completes,
                // and the one the scheduler holds after.
                _artworkTransfer.Cancel((byte)channel);
                _displayScheduler.FlushArtworkChannel(channel);
            }
        }
    }

    private static bool SameArtworkChannelConfiguration(
        ArtworkStreamChannel? left, ArtworkStreamChannel? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.Source == right.Source
               && left.Format == right.Format
               && left.Width == right.Width
               && left.Height == right.Height;
    }

    private async Task HandleStreamEndAsync(string json, bool playerActive, int config)
    {
        try
        {
            var message = MessageSerializer.Deserialize<StreamEndMessage>(json);
            if (message is null)
            {
                return;
            }

            // As in HandleStreamStartCoreAsync: the serializer does not enforce the
            // model's non-nullable Payload, and the role gate below dereferences it, so
            // a null payload must be reported as the JsonException this catch handles
            // before that dereference throws NullReferenceException past it.
            if (message.Payload is null)
            {
                throw new System.Text.Json.JsonException("stream/end payload is null");
            }

            var payload = message.Payload;
            _logger.LogInformation(
                "Stream ended for roles: {Roles}",
                payload.Roles is null ? "all" : string.Join(", ", payload.Roles));

            StreamEndReceived?.Invoke(this, payload);

            await StopStreamRolesAsync(payload.Roles, ReachesPlayerRole(payload.Roles, playerActive), config);
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

    /// <summary>
    /// Stops the output and clears the buffers of the stream roles a <c>stream/end</c> names —
    /// every one when it names none — or that a <c>server/activate</c> removed.
    /// </summary>
    /// <param name="roles">The roles named, or null for every stream role.</param>
    /// <param name="stopPlayer">
    /// Whether the player is among them. The caller's to say: a <c>stream/end</c> reaches the
    /// player only while the role is active, a removal exactly when it no longer is.
    /// </param>
    /// <param name="config">
    /// The number of the player configuration the message was received under. Chunks queued
    /// before it are the ended stream's.
    /// </param>
    private async Task StopStreamRolesAsync(List<string>? roles, bool stopPlayer, int config)
    {
        // Media held for a display time that belongs to the stream just ended must not
        // surface after it, and the artwork on display is cleared: both a stream/end and a
        // role's removal are playback termination (spec #266), unlike a stream/clear seek.
        FlushDisplayRoles(roles, endingStream: true);

        if (!stopPlayer)
        {
            return;
        }

        DropEarlyChunksReceivedBefore(config);

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

    private void HandleStreamClear(string json)
    {
        // No null-payload guard of its own, unlike its two siblings: "payload": null never gets
        // past the Deserialize below, because PeerMessageValidation's stream/clear arm rejects
        // it there — and this handler is synchronous, so that JsonException lands in
        // OnTextMessageReceived's malformed-payload close rather than in a fire-and-forget
        // swallow. NullStreamClearPayload_ClosesTheConnection pins that end to end.
        var message = MessageSerializer.Deserialize<StreamClearMessage>(json);
        if (message is null)
        {
            return;
        }

        var payload = message.Payload;
        _logger.LogDebug(
            "Stream clear (seek) for roles: {Roles}",
            payload.Roles is null ? "all" : string.Join(", ", payload.Roles));

        StreamClearReceived?.Invoke(this, payload);

        // "Clients should clear all buffered visualization data and continue with data received
        // after this message" — the same boundary applies to artwork still held for display. A
        // seek keeps the image already on screen, so the flush drops only what is pending.
        FlushDisplayRoles(payload.Roles, endingStream: false);

        if (ReachesPlayerRole(payload.Roles, IsRoleActive("player")) && _audioPipeline is { } pipeline)
        {
            // Deserialization stays on the receive loop above — that is what routes a malformed
            // payload into OnTextMessageReceived's close — but the pipeline call joins the
            // lifecycle chain, so a seek cannot clear buffers ahead of the stream/start that
            // creates them. Nor, waiting there, can it clear the chunks received after it: those
            // are held back until it has run, and the ones still queued from before it go too.
            DispatchStreamLifecycle(
                config =>
                {
                    DropEarlyChunksReceivedBefore(config);
                    pipeline.Clear();
                    return Task.CompletedTask;
                },
                changesPlayer: true);
        }
    }

    /// <summary>
    /// Whether a stream/end or stream/clear reaches the <c>player</c> role, and so the audio
    /// pipeline. An omitted <c>roles</c> means every active stream, which is the case that
    /// makes an absent array and an empty one behave differently — and no stream is active for
    /// a player the server did not activate, so on such a connection neither message reaches it.
    /// </summary>
    /// <remarks>
    /// Role-targeted teardown is routine, not exotic: whenever a <c>server/activate</c> drops a
    /// stream role, the server ends that role's output first, so a <c>stream/end</c> naming
    /// <c>artwork</c> arrives mid-playback and must not touch audio (#193). Other names —
    /// <c>artwork</c>, <c>visualizer</c>, and the application-specific roles starting with
    /// <c>_</c> — are simply not <c>player</c>; the two media roles are torn down separately by
    /// <see cref="FlushDisplayRoles"/>, and the rest reach subscribers through
    /// <see cref="StreamEndReceived"/> / <see cref="StreamClearReceived"/> rather than being
    /// validated here, since only the consumer of a role knows its names.
    /// </remarks>
    private static bool ReachesPlayerRole(List<string>? roles, bool playerActive)
        => playerActive && (roles is null || roles.Contains("player"));

    /// <summary>
    /// Discards the media a <c>stream/end</c> or <c>stream/clear</c> ends the display of: the
    /// roles it names, or both media roles when it names none.
    /// </summary>
    /// <remarks>
    /// Runs outside <see cref="ReachesPlayerRole"/>'s gate, because the roles holding data here
    /// are exactly the ones that gate turns away — a <c>stream/end</c> for <c>visualizer</c>
    /// alone must still drop the frames waiting for their display moment. <c>artwork</c> is in
    /// <c>stream/end</c>'s role vocabulary but not <c>stream/clear</c>'s; it is honoured in both
    /// anyway, as the C++ reference client switches on the same three names for either message.
    /// A present-but-empty array names no role and so ends nothing, as everywhere else.
    /// Dropping the artwork still held is what spec #135 (pending merge) means by "on
    /// <c>stream/end</c>, clearing buffers includes discarding pending images".
    /// <para>
    /// <paramref name="endingStream"/> separates the two messages for the artwork already on
    /// display: a <c>stream/end</c> is playback termination and additionally clears it (spec #266),
    /// while a <c>stream/clear</c> is a seek or track jump that keeps it and only drops the pending
    /// image.
    /// </para>
    /// </remarks>
    private void FlushDisplayRoles(List<string>? roles, bool endingStream)
    {
        if (roles is null)
        {
            // Every stream, which for this scheduler is the two media roles. The state roles
            // hold no stream, and spec #135 (pending merge) ties a pending metadata or color
            // update to nothing a stream teardown says.
            _displayScheduler.FlushVisualizer();
            FlushArtwork(endingStream);
            return;
        }

        if (roles.Contains("visualizer"))
        {
            _displayScheduler.FlushVisualizer();
        }

        if (roles.Contains("artwork"))
        {
            FlushArtwork(endingStream);
        }
    }

    /// <summary>
    /// Discards the pending artwork a <c>stream/end</c> or <c>stream/clear</c> reaches, and for a
    /// <c>stream/end</c> also clears what is on display and ends the transfer in flight.
    /// </summary>
    /// <remarks>
    /// "On <c>stream/end</c> for the artwork role, clients MUST clear the current image and
    /// discard any pending image" — and a channel's pending image runs from its announce, so one
    /// still arriving is discarded with the rest; left alone it would complete and go on display
    /// after the application was told to clear. A <c>stream/clear</c> leaves the transfer be:
    /// artwork is not in that message's role vocabulary, so the server goes on sending the
    /// image's parts, and forgetting the transfer would turn each into a protocol error.
    /// </remarks>
    private void FlushArtwork(bool endingStream)
    {
        if (endingStream)
        {
            _artworkTransfer.Reset();
        }

        _displayScheduler.FlushArtwork(raiseCleared: endingStream);
    }

    private void OnBinaryMessageReceived(object? sender, ReadOnlyMemory<byte> data)
    {
        // The same rule, for the same reason, as the check at the top of
        // OnTextMessageReceived: frames keep arriving while a close is in flight. Here they
        // would be audio still fed to the pipeline, and an artwork sequence error would start
        // a second close whose 'unauthorized' goodbye competes with the first one's reason.
        if (_connection.State is ConnectionState.Disconnected or ConnectionState.Disconnecting)
        {
            return;
        }

        if (AwaitingActivate)
        {
            _logger.LogDebug("Dropping binary message received before server/activate");
            return;
        }

        // Artwork is routed on its type byte alone: it does not share the timestamped header
        // TryParse reads — a cancel is two bytes — and a length that header would reject is,
        // for artwork, a protocol error to close over rather than a frame to drop.
        if (data.Length > 0 && BinaryMessageTypes.IsArtwork(data.Span[0]))
        {
            DispatchBinaryMessage(BinaryMessageCategory.Artwork, data.Span[0], 0, default, data);
            return;
        }

        if (!BinaryMessageParser.TryParse(data.Span, out var type, out var timestamp, out var payload))
        {
            _logger.LogWarning("Failed to parse binary message");
            return;
        }

        var category = BinaryMessageParser.GetCategory(type);

        // No catch here, deliberately: every binary parser is Try-style (a malformed frame
        // parses to null and is dropped above or inside DispatchBinaryMessage, or for artwork
        // closes the connection there), so nothing a hostile payload produces can throw.
        // Anything that does throw — a buggy event subscriber or pipeline — is a bug in our own
        // handling and must propagate so the receive loop surfaces it as a lost connection, not
        // be collapsed into a log line (#88 item 2).
        DispatchBinaryMessage(category, type, timestamp, payload, data);
    }

    private void DispatchBinaryMessage(
        BinaryMessageCategory category, byte type, long timestamp, ReadOnlySpan<byte> payload, ReadOnlyMemory<byte> data)
    {
        // Spec PR #204: a role's binary data is only in play once the server has received that
        // role's client/state object. Enforcing it on the receive side keeps a server that
        // streams ahead of the gate from being treated as authoritative — its frames were
        // scheduled against timings and a channel configuration this client never sent.
        if (RoleFamilyForBinary(category) is { } family && !IsRoleBinaryPermitted(family))
        {
            WarnOnceOnUngatedRoleBinary(family);
            return;
        }

        // Spec #266/#271 (SHOULD): while this client reports available: false the server should not
        // stream it display data, so a visualizer frame that arrives anyway is dropped before it
        // is scheduled — its timings belong to a state this client is not in. The connection
        // stays open, and the player-audio arm keeps its own handling. So does the artwork arm:
        // an image is a transfer of several messages, which has to be followed even while its
        // data is being discarded (see HandleArtworkMessage).
        if (category is BinaryMessageCategory.Visualizer && !CurrentAvailability)
        {
            DropDisplayBinaryWhileUnavailable();
            return;
        }

        switch (category)
        {
            case BinaryMessageCategory.PlayerAudio:
                // Spec #270: while this client reports available: false its pipeline is not
                // consuming, so discard inbound audio rather than decode it — the connection stays
                // open (the spec says discard, MUST NOT close). Logged once per unavailable period,
                // not per chunk, since a live stream would otherwise flood the log.
                // The pipeline's own reported error is deliberately not a reason to discard: a
                // failed playback start is retried from ProcessAudioChunk, so audio is the only
                // thing that returns the pipeline to Playing and clears that error.
                if (IsExternalSource || (RequiresClockSync() && !ClockSyncEstablished))
                {
                    if (!_audioDroppedWhileUnavailable)
                    {
                        _audioDroppedWhileUnavailable = true;
                        _logger.LogDebug(
                            "Discarding player audio while unavailable; chunks are dropped until this client reports available again");
                    }

                    break;
                }

                if (type != BinaryMessageTypes.PlayerAudio0)
                {
                    // player@v1 defines one audio slot; 5-7 are allocated to the role but carry no
                    // defined payload. Feeding them to the pipeline would interleave unknown bytes
                    // with the real stream, so they are dropped as the C++ reference client does.
                    WarnOnceOnUndefinedPlayerAudioType(type);
                    break;
                }

                var audioChunk = BinaryMessageParser.ParseAudioChunk(data.Span);
                if (audioChunk != null)
                {
                    // Excludes the stream/start handler's drain, which feeds the same pipeline
                    // from another thread — see _audioHandoffLock. The queue must be empty as
                    // well as the pipeline ready: a chunk handed over while earlier ones are
                    // still queued would overtake them. And no lifecycle message received before
                    // this chunk may still be waiting to run: the pipeline is then ready for the
                    // stream that message ends, clears or reconfigures.
                    lock (_audioHandoffLock)
                    {
                        if (_playerConfigApplied == _playerConfigReceived
                            && _audioPipeline?.IsReady == true
                            && _earlyChunkQueue.IsEmpty)
                        {
                            // Pipeline ready - process immediately
                            _audioPipeline.ProcessAudioChunk(audioChunk);
                        }
                        else if (_earlyChunkQueue.Count < MaxEarlyChunks)
                        {
                            // Pipeline not ready yet - queue for later processing
                            // This prevents chunk loss during decoder/buffer initialization
                            _earlyChunkQueue.Enqueue((audioChunk, _playerConfigReceived));
                            _logger.LogTrace("Queued early chunk ({QueueSize} in queue)", _earlyChunkQueue.Count);
                        }

                        // else: queue full, drop chunk (should rarely happen)
                    }
                }

                _logger.LogTrace("Audio chunk: {Length} bytes @ {Timestamp}", payload.Length, timestamp);
                break;

            case BinaryMessageCategory.Artwork:
                HandleArtworkMessage(data.Span);
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
    /// Applies one artwork binary message — an announce, a part, or a cancel — and hands the
    /// display scheduler the image a transfer completes (spec roles/artwork/v1.md).
    /// </summary>
    /// <remarks>
    /// The scheduler holds each channel's pending image once it is complete; until then the
    /// pending image is the transfer in flight. So the two things the spec says discard a
    /// channel's pending image — an announce and a cancel — are applied to both.
    /// </remarks>
    private void HandleArtworkMessage(ReadOnlySpan<byte> data)
    {
        if (!BinaryMessageParser.TryParseArtwork(data, out var message, out var partData))
        {
            CloseOnArtworkProtocolError("malformed message", data);
            return;
        }

        // "Unavailable clients SHOULD discard otherwise valid image data", but "MUST still
        // process announces and cancels and count each part's data bytes toward total_size":
        // dropping the message whole would leave the next one out of sequence, which is a
        // protocol error this client closes the connection over.
        bool discard = !CurrentAvailability;
        if (discard)
        {
            DropDisplayBinaryWhileUnavailable();
        }

        ArtworkChunk? complete;

        switch (message.Kind)
        {
            case ArtworkMessageKind.Cancel:
                _artworkTransfer.Cancel(message.Channel);
                _displayScheduler.FlushArtworkChannel(message.Channel);
                return;

            case ArtworkMessageKind.Announce:
                if (!_artworkTransfer.TryBegin(message, discard, out complete))
                {
                    CloseOnArtworkProtocolError("announce while a transfer is in flight", data);
                    return;
                }

                if (message.TotalSize > ArtworkTransfer.MaxImageBytes)
                {
                    _logger.LogWarning(
                        "Refusing artwork image of {Size} bytes on channel {Channel}: over the {Max} byte limit",
                        message.TotalSize, message.Channel, ArtworkTransfer.MaxImageBytes);
                }

                _displayScheduler.FlushArtworkChannel(message.Channel);
                break;

            default:
                if (!_artworkTransfer.TryAppend(message.Channel, partData, discard, out complete))
                {
                    CloseOnArtworkProtocolError(
                        "part with no transfer in flight on its channel, or extending past total_size", data);
                    return;
                }

                break;
        }

        if (complete is not null)
        {
            _logger.LogDebug("Artwork on channel {Channel}: {Length} bytes @ {Timestamp}",
                complete.Channel, complete.ImageData.Length, complete.Timestamp);

            // Held until the timestamp's local equivalent, or raised now if that has
            // already passed — artwork is never dropped for lateness (#199).
            _displayScheduler.SubmitArtwork(complete);
        }
    }

    /// <summary>
    /// Closes the connection over a malformed artwork message or sequence, as the spec requires
    /// ("the client MUST close the connection"). The goodbye reason list has no protocol-error
    /// value, so this reuses 'unauthorized' as the malformed-text-message close does.
    /// </summary>
    private void CloseOnArtworkProtocolError(string what, ReadOnlySpan<byte> data)
    {
        _logger.LogError(
            "Artwork protocol error ({What}): type {Type}, {Length} bytes; closing connection",
            what, data[0], data.Length);
        DisconnectAsync("unauthorized").SafeFireAndForget(_logger);
    }

    /// <summary>
    /// The role family whose client/state object gates a binary category, or null for a category
    /// no role owns.
    /// </summary>
    private static string? RoleFamilyForBinary(BinaryMessageCategory category) => category switch
    {
        BinaryMessageCategory.PlayerAudio => "player",
        BinaryMessageCategory.Artwork => "artwork",
        BinaryMessageCategory.Visualizer => "visualizer",
        _ => null,
    };

    /// <summary>
    /// Logs the first chunk seen on each undefined player-audio type, so a server emitting them at
    /// chunk rate produces one line per type rather than one per chunk.
    /// </summary>
    private void WarnOnceOnUndefinedPlayerAudioType(byte type)
    {
        int bit = 1 << (type - BinaryMessageTypes.PlayerAudio0);
        if ((_warnedUndefinedPlayerAudioTypes & bit) != 0)
        {
            return;
        }

        _warnedUndefinedPlayerAudioTypes |= bit;
        _logger.LogWarning(
            "Dropping binary type {Type}: player@v1 defines only audio type {DefinedType}",
            type,
            BinaryMessageTypes.PlayerAudio0);
    }

    /// <summary>
    /// Logs the first artwork/visualizer frame dropped in each unavailable period, so a server
    /// that keeps streaming display data to an unavailable client says so once rather than at
    /// frame rate. Re-armed in <see cref="PublishAvailabilityAsync"/> when the client becomes
    /// available again, and on each new connection.
    /// </summary>
    private void DropDisplayBinaryWhileUnavailable()
    {
        if (_loggedDisplayDropWhileUnavailable)
        {
            return;
        }

        _loggedDisplayDropWhileUnavailable = true;
        _logger.LogDebug(
            "Dropping artwork/visualizer binary data while unavailable (available: false); the "
            + "server should not stream display data to an unavailable client");
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
        EndConnectionLifetime();
        ClearPairingCodeState();
        UnsubscribeConnectionEvents();
        _displayScheduler.Dispose();
        _clientStateSendGate.Dispose();
    }

    public ValueTask DisposeAsync() => DisposeAsync(ownsPipelines: true);

    /// <summary>
    /// Disposes this client, leaving the audio pipeline and the capture device alone when
    /// <paramref name="ownsPipelines"/> is false.
    /// </summary>
    /// <remarks>
    /// <see cref="SendspinHostService"/> builds every connection it accepts over the same
    /// <see cref="IAudioPipeline"/> and <see cref="IAudioCaptureDevice"/>, and only the one it
    /// admitted is playing through them. Disposing any other — one that never activated, lost
    /// arbitration, or was displaced — must not stop that playback or close that device (#311).
    /// </remarks>
    internal async ValueTask DisposeAsync(bool ownsPipelines)
    {
        if (_disposed) return;
        _disposed = true;

        StopTimeSyncLoop();
        EndConnectionLifetime();
        ClearPairingCodeState();
        UnsubscribeConnectionEvents();
        _displayScheduler.Dispose();
        _clientStateSendGate.Dispose();

        // NOTE: We do NOT dispose _audioPipeline here - it's a shared singleton
        // managed by the DI container. We only stop playback if active.
        if (_audioPipeline != null && ownsPipelines)
        {
            await _audioPipeline.StopAsync();
        }

        // The source pipeline owns its capture device, so dispose it here — unless a host
        // shares that device across its connections, when only the streaming stops.
        if (_sourcePipeline is not null)
        {
            await _sourcePipeline.DisposeAsync(disposeCapture: ownsPipelines);
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
            _audioPipeline.OutputLatencyChanged -= OnOutputLatencyChanged;
        }

        if (_pairingWindow is not null)
        {
            _pairingWindow.StateChanged -= OnPairingWindowStateChanged;

            // Disposal is a drop of the connection too, and no state change reports it once
            // the handlers above are gone.
            _pairingWindow.CloseFor(this);
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
        // Assigned below, before anything can dial and so before the resolver can be asked
        // anything. The closure is how the resolver reaches this connection's live pairing
        // config, which only exists once the client does; until then the configured value is
        // the same one the client will start from.
        SendspinClientService? client = null;

        var framing = new NoiseWireFraming(
            options.Identity,
            options.PairingRecordStore is null
                ? null
                : new RecordPskResolver(
                    options.PairingRecordStore,
                    () => client?.IsPairingPskEnabled ?? options.Capabilities.PairingPskEnabled),
            options.Suite);

        var connection = new SendspinConnection(
            loggerFactory.CreateLogger<SendspinConnection>(),
            connectionOptions,
            framing);

        client = new SendspinClientService(
            loggerFactory.CreateLogger<SendspinClientService>(),
            connection,
            framing,
            options);

        return client;
    }
}
