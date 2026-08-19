using Sendspin.SDK.Connection;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Client;

/// <summary>
/// Main client interface for interacting with a Sendspin server.
/// </summary>
public interface ISendspinClient : IAsyncDisposable
{
    /// <summary>
    /// Current connection state.
    /// </summary>
    ConnectionState ConnectionState { get; }

    /// <summary>
    /// Server ID after successful connection.
    /// </summary>
    string? ServerId { get; }

    /// <summary>
    /// Server name after successful connection.
    /// </summary>
    string? ServerName { get; }

    /// <summary>
    /// The most recent <c>server/hello</c> payload received from the server,
    /// or <c>null</c> if the handshake has not yet completed.
    /// </summary>
    /// <remarks>
    /// Exposes fields that the scalar <see cref="ServerId"/>/<see cref="ServerName"/> properties
    /// don't surface, notably <see cref="ServerHelloPayload.ActiveRoles"/> and
    /// <see cref="ServerHelloPayload.Version"/>. Re-set on every reconnect handshake.
    /// </remarks>
    ServerHelloPayload? LastServerHello { get; }

    /// <summary>
    /// The most recent <c>stream/start</c> payload received from the server,
    /// or <c>null</c> if no stream has started on this connection yet.
    /// </summary>
    /// <remarks>
    /// Includes both <see cref="StreamStartPayload.Format"/> (player audio format) and
    /// <see cref="StreamStartPayload.Artwork"/>. Either may be null depending on the stream type.
    /// Replaced on every <c>stream/start</c>, including artwork-only updates.
    /// </remarks>
    StreamStartPayload? LastStreamStart { get; }

    /// <summary>
    /// Current group state (volume/mute represent group averages for display).
    /// </summary>
    GroupState? CurrentGroup { get; }

    /// <summary>
    /// This player's own volume and mute state (applied to audio output).
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="CurrentGroup"/>, which contains the group average,
    /// this represents THIS player's actual volume as set by <c>server/command</c>
    /// messages or local user input.
    /// </remarks>
    PlayerState CurrentPlayerState { get; }

    /// <summary>
    /// Current clock synchronization status.
    /// </summary>
    ClockSyncStatus? ClockSyncStatus { get; }

    /// <summary>
    /// Whether the clock synchronizer has converged to a stable estimate.
    /// </summary>
    bool IsClockSynced { get; }

    /// <summary>
    /// Connects to a Sendspin server and completes the encrypted handshake.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns only once the handshake has completed — which, under the encrypted protocol,
    /// means the server's first <c>server/activate</c> has arrived, not merely its
    /// <c>server/hello</c>.
    /// </para>
    /// <para>
    /// <b>A permanent handshake failure is thrown, not merely reported.</b> Handle it here:
    /// an application that only subscribes to <see cref="ConnectionStateChanged"/> — or to
    /// nothing, as the Quick Start once showed — would otherwise see this call return
    /// normally against a server it had just permanently rejected, and discover the problem
    /// when its first command threw "WebSocket is not connected".
    /// </para>
    /// <para>
    /// A transport-level failure to reach the server (for example
    /// <see cref="System.Net.WebSockets.WebSocketException"/>) propagates only when
    /// <c>ConnectionOptions.AutoReconnect</c> is false; with it enabled the connection enters
    /// its reconnect loop instead and this call returns.
    /// </para>
    /// </remarks>
    /// <exception cref="Connection.SendspinHandshakeException">
    /// The handshake failed permanently, so retrying cannot help.
    /// <see cref="Connection.SendspinHandshakeException.Kind"/> distinguishes the two cases:
    /// <see cref="Connection.HandshakeFailureKind.LegacyServer"/> — the server predates the
    /// encrypted protocol (aiosendspin &lt; 7.0.0); upgrade it, or use the 9.x SDK line.
    /// <see cref="Connection.HandshakeFailureKind.HandshakeRejected"/> — the server speaks the
    /// encrypted protocol but refused this handshake: no usable pairing record, an unsupported
    /// cipher suite, a version mismatch, or malformed input. Pair again, or check the suite.
    /// </exception>
    /// <exception cref="TimeoutException">
    /// The server accepted the socket but did not complete the hello exchange within the
    /// spec's recommended 30 s handshake window. Unlike the above this is not permanent —
    /// retrying is reasonable.
    /// </exception>
    /// <exception cref="InvalidOperationException">Already connected or connecting.</exception>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled.
    /// </exception>
    Task ConnectAsync(Uri serverUri, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnects from the server.
    /// </summary>
    Task DisconnectAsync(string reason = "restart");

    /// <summary>
    /// Sends a playback command.
    /// </summary>
    Task SendCommandAsync(string command, Dictionary<string, object>? parameters = null);

    /// <summary>
    /// Sets the volume level (0-100).
    /// </summary>
    Task SetVolumeAsync(int volume);

    /// <summary>
    /// Sets the group mute state via a controller <c>mute</c> command.
    /// </summary>
    /// <param name="muted">True to mute, false to unmute.</param>
    Task SetMuteAsync(bool muted);

    /// <summary>
    /// Requests a different player audio format via <c>stream/request-format</c> — use this to adapt
    /// to changing network or CPU conditions (e.g. downgrade codec/sample rate). Omitted parameters
    /// are left to the server, which responds with a <c>stream/start</c> for the player role.
    /// </summary>
    /// <param name="codec">Requested codec ("opus", "flac", "pcm"), or null to leave unchanged.</param>
    /// <param name="sampleRate">Requested sample rate in Hz, or null to leave unchanged.</param>
    /// <param name="channels">Requested channel count, or null to leave unchanged.</param>
    /// <param name="bitDepth">Requested bit depth, or null to leave unchanged.</param>
    Task RequestPlayerFormatAsync(string? codec = null, int? sampleRate = null, int? channels = null, int? bitDepth = null);

    /// <summary>
    /// Requests a format/source change for a single artwork channel via <c>stream/request-format</c>.
    /// Omitted parameters are left unchanged by the server. Set <paramref name="source"/> to
    /// <c>"none"</c> to disable the channel, or back to <c>"album"</c>/<c>"artist"</c> to re-enable it,
    /// without reconnecting. The server responds with a <c>stream/start</c> for the artwork role.
    /// </summary>
    /// <param name="channel">Artwork channel number (0-3).</param>
    /// <param name="source">Artwork source ("album", "artist", "none"), or null to leave unchanged.</param>
    /// <param name="format">Image format ("jpeg", "png", "bmp"), or null to leave unchanged.</param>
    /// <param name="mediaWidth">Maximum width in pixels, or null to leave unchanged.</param>
    /// <param name="mediaHeight">Maximum height in pixels, or null to leave unchanged.</param>
    Task RequestArtworkFormatAsync(int channel, string? source = null, string? format = null, int? mediaWidth = null, int? mediaHeight = null);

    /// <summary>
    /// Renegotiates the visualizer stream via <c>stream/request-format</c> (the <c>visualizer@v1</c>
    /// role). Omitted parameters keep their prior value. The server responds with a
    /// <c>stream/start</c> carrying the new visualizer config.
    /// </summary>
    /// <param name="types">Requested feature types (subset of loudness/f_peak/spectrum/beat/peak/pitch), or null to leave unchanged.</param>
    /// <param name="rateMax">Requested maximum frame rate, or null to leave unchanged.</param>
    /// <param name="spectrum">Requested spectrum configuration, or null to leave unchanged.</param>
    /// <remarks>
    /// Buffer capacity is not renegotiable: it is a <c>visualizer@v1_support</c> field of
    /// <c>client/hello</c>, and the spec's <c>stream/request-format</c> visualizer object carries
    /// only types, rate_max and spectrum. Set it via
    /// <see cref="ClientCapabilities.VisualizerSupport"/> before connecting.
    /// </remarks>
    Task RequestVisualizerFormatAsync(List<string>? types = null, int? rateMax = null, VisualizerSpectrum? spectrum = null);

    /// <summary>
    /// Sends the current player state (volume, muted) to the server.
    /// This is used to report local state changes to Music Assistant.
    /// </summary>
    /// <remarks>
    /// The reported volume and mute also become the client's persisted player state, so later
    /// full-state sends (e.g. a reconnect's initial client/state) carry them. While the
    /// connection's initial client/state is still deferred pending clock sync, the call sends
    /// nothing yet — the deferred initial reports the persisted values once sync converges —
    /// unless something genuinely holds availability false, in which case it sends the full
    /// initial message instead of a player-only delta.
    /// </remarks>
    /// <param name="volume">Current volume level (0-100).</param>
    /// <param name="muted">Current mute state.</param>
    /// <param name="staticDelayMs">
    /// A new static delay in milliseconds to apply, persist, and report, or null (the default)
    /// to leave the current delay untouched and simply report it.
    /// </param>
    /// <remarks>
    /// <para>
    /// Supplying a value is a client-initiated static-delay update — the spec permits one "when
    /// audio output changes" and requires clients to persist <c>static_delay_ms</c> across
    /// reboots and reconnections — so the value is written to
    /// <see cref="Synchronization.IClockSynchronizer.StaticDelayMs"/> and to
    /// <see cref="SendspinClientOptions.StaticDelayStore"/>, not merely reported.
    /// </para>
    /// <para>
    /// Omit it for an ordinary volume or mute change. The reported delay is always the one
    /// actually applied: the server MUST merge each client/state into existing state, so a
    /// value present on the wire overwrites, and reporting a delay you are not applying leaves
    /// the server's group calibration working from a different number than your playback.
    /// </para>
    /// </remarks>
    Task SendPlayerStateAsync(int volume, bool muted, double? staticDelayMs = null);

    /// <summary>
    /// Updates the player timing parameters reported to the server and re-sends client/state.
    /// </summary>
    /// <remarks>
    /// Use this when measured conditions change (e.g. empirically measured lead time after warmup,
    /// or a link-type change). Per the Sendspin spec, callers should debounce updates locally and
    /// report only sustained changes — the SDK sends each call verbatim. No-op on the wire when the
    /// client is not currently connected, and also while the connection's initial client/state is
    /// still deferred pending clock sync; the new values are still applied and the next state
    /// send (including that deferred initial) carries them.
    /// </remarks>
    /// <param name="requiredLeadTimeMs">Minimum startup lead time in milliseconds.</param>
    /// <param name="minBufferMs">Requested minimum ongoing buffer duration in milliseconds.</param>
    Task UpdateTimingAsync(int requiredLeadTimeMs, int minBufferMs);

    /// <summary>
    /// Whether the client has entered the <c>external_source</c> state (its output is in use by an
    /// external system and it is not currently participating in Sendspin playback).
    /// </summary>
    bool IsExternalSource { get; }

    /// <summary>
    /// Enters the <c>external_source</c> state: tells the server this client's output is in use by an
    /// external system (HDMI input, local media, a different audio source) and is not participating
    /// in Sendspin playback. The server moves the client to a solo, stopped group and ends its
    /// streams. Notifies the server first; <see cref="IsExternalSource"/> only flips if the
    /// notification succeeds (rollback on failure), so a throw leaves the client in its prior state.
    /// </summary>
    Task EnterExternalSourceAsync();

    /// <summary>
    /// Leaves the <c>external_source</c> state, reporting <c>available: true</c> so the client can
    /// resume participating in Sendspin playback. <see cref="IsExternalSource"/> only clears if the
    /// notification succeeds.
    /// </summary>
    Task ExitExternalSourceAsync();

    /// <summary>
    /// Reports line-sense signal presence to the server via client/state (source role).
    /// No-op unless the source role is configured with line sensing, and skipped while the
    /// connection's initial client/state is still deferred pending clock sync — a source-only
    /// delta must not become the first client/state the server sees. The initial message does
    /// not carry the signal, so a change made inside that window is reported by the app's next
    /// call after sync converges.
    /// </summary>
    Task SetSourceSignalAsync(bool present);

    /// <summary>
    /// Clears the audio buffer, causing the pipeline to restart buffering.
    /// Use this when audio sync parameters change and you want immediate effect.
    /// </summary>
    void ClearAudioBuffer();

    /// <summary>
    /// This client's <c>client_id</c>: the base64url-encoded Curve25519 public key of the
    /// identity this client was constructed with. Stable across reconnects and restarts as
    /// long as the same identity is reused.
    /// </summary>
    string ClientId { get; }

    /// <summary>
    /// How far the current session's peer is trusted. See <see cref="SendspinTrustLevel"/> —
    /// in particular, <see cref="SendspinTrustLevel.Unpaired"/> describes trust, not whether
    /// the connection is encrypted.
    /// </summary>
    SendspinTrustLevel TrustLevel { get; }

    /// <summary>
    /// Returns this client's pairing token, generating and persisting a Pairing PSK if none is
    /// stored. Idempotent: repeated calls return the same token until the PSK is replaced by
    /// <see cref="RotatePairingPsk"/>, by a server's <c>management/set-pairing-config</c>, or
    /// by a server removing the Pairing record via <c>management/remove-record</c>. Each of
    /// those raises <see cref="PairingConfigChanged"/> with
    /// <see cref="PairingConfigChangedEventArgs.PairingPskReplaced"/> set.
    /// Hand the string to your UI to render as a QR code or to display for pasting.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No pairing record store is configured, so a generated PSK could not be persisted.
    /// </exception>
    string EnsurePairingPsk();

    /// <summary>
    /// Replaces this client's Pairing PSK with a freshly generated one and returns the new
    /// token. Any token previously handed out stops being valid. The spec forbids the client
    /// rotating on its own, so this exists to be called only by deliberate operator action.
    /// </summary>
    /// <exception cref="InvalidOperationException">No pairing record store is configured.</exception>
    string RotatePairingPsk();

    /// <summary>
    /// Event raised when connection state changes.
    /// </summary>
    event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    /// <summary>
    /// Event raised when group state updates (playback, metadata, volume).
    /// </summary>
    event EventHandler<GroupState>? GroupStateChanged;

    /// <summary>
    /// Event raised when THIS player's volume or mute state changes.
    /// </summary>
    /// <remarks>
    /// This event fires when <c>server/command</c> messages change the player's
    /// volume or mute state. Subscribe to this for audio-affecting changes.
    /// </remarks>
    event EventHandler<PlayerState>? PlayerStateChanged;

    /// <summary>
    /// Event raised when an artwork image is due for display on a channel (0-3). Carries the
    /// channel, display timestamp, and encoded image bytes.
    /// </summary>
    /// <remarks>
    /// The SDK translates the message's server timestamp to the local clock and holds the image
    /// until then, so the event marks the display moment rather than the arrival moment — an
    /// image pre-sent for the next track is raised when that track starts. A timestamp already
    /// past on arrival raises immediately; artwork is never dropped for lateness, and a newer
    /// image for a channel supersedes one still held for it. See
    /// <see cref="VisualizationReceived"/> for which thread raises the event.
    /// </remarks>
    event EventHandler<ArtworkReceivedEventArgs>? ArtworkReceived;

    /// <summary>
    /// Event raised when a single artwork channel is cleared (an empty artwork binary message).
    /// Carries the channel that was cleared.
    /// </summary>
    /// <remarks>
    /// Scheduled against its display timestamp exactly as <see cref="ArtworkReceived"/> is.
    /// </remarks>
    event EventHandler<ArtworkClearedEventArgs>? ArtworkCleared;

    /// <summary>
    /// Event raised whenever a <c>server/state</c> carries a <c>color</c> object (the <c>color</c>
    /// role) — including updates that leave the resolved values unchanged. Carries the current
    /// merged <see cref="ColorPalette"/>, also available as <see cref="GroupState.Colors"/>.
    /// </summary>
    event EventHandler<ColorPalette>? ColorChanged;

    /// <summary>
    /// Event raised when a decoded visualizer feature frame is due for display (the
    /// <c>visualizer@v1</c> role). Each <see cref="VisualizerFrame"/> carries one feature type
    /// (loudness, f_peak, spectrum, beat, peak, or pitch). Malformed frames are dropped and do
    /// not raise the event.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Servers send frames ahead of when they should be shown — that is what the advertised
    /// <c>buffer_capacity</c> is for — so the SDK translates each frame's server timestamp to the
    /// local clock and raises this event at that moment, keeping the visualization aligned with
    /// the audio. Frames that are already more than 20 ms late on arrival are dropped rather than
    /// rendered stale, as are frames still pending when the stream is cleared or ends, or when
    /// the connection drops. Frames beyond the advertised <c>buffer_capacity</c> are dropped
    /// oldest-first.
    /// </para>
    /// <para>
    /// <b>Threading:</b> a frame already due on arrival is raised on the receive loop, as before;
    /// a frame held for a future display time is raised on an SDK background thread instead. Both
    /// orderings are preserved, but a subscriber must be safe to call from either thread, and
    /// must marshal to a UI thread itself. An exception from a subscriber still faults the
    /// connection when the event was raised inline; on the scheduled path it is logged and the
    /// remaining frames continue.
    /// </para>
    /// </remarks>
    event EventHandler<VisualizerFrame>? VisualizationReceived;

    /// <summary>
    /// Event raised when the clock synchronizer first converges to a stable estimate.
    /// This indicates that the client is ready for sample-accurate synchronized playback.
    /// </summary>
    event EventHandler<ClockSyncStatus>? ClockSyncConverged;

    /// <summary>
    /// Raised once per successful handshake (including reconnects), carrying the parsed
    /// <c>server/hello</c> payload — the same object cached on <see cref="LastServerHello"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Fires on the session's first <c>server/activate</c>, not on <c>server/hello</c>
    /// itself.</b> Under the encrypted protocol the handshake completes on that activate, and
    /// this event marks the completed handshake rather than the arrival of the message it
    /// carries. Anything written against the pre-encryption ordering — which raised it as
    /// soon as the hello was parsed, before any activate — observes different state here: by
    /// the time it fires the role grant has been applied and the client may already send.
    /// </para>
    /// <para>
    /// The payload's <see cref="ServerHelloPayload.Name"/> is the only field an encrypted
    /// server populates. In particular <see cref="ServerHelloPayload.ServerId"/> is empty:
    /// the server's identity is its authenticated Noise static key, exposed as
    /// <see cref="ISendspinClient.ServerId"/>. Key per-server state off that, never off the
    /// payload's copy. See the remarks on <see cref="ServerHelloPayload"/>.
    /// </para>
    /// </remarks>
    event EventHandler<ServerHelloPayload>? ServerHelloReceived;

    /// <summary>
    /// Raised when a <c>stream/start</c> message is received and parsed.
    /// Fires for every <c>stream/start</c>, whether it carries audio format, artwork metadata, or both.
    /// The payload is the same object cached on <see cref="LastStreamStart"/>.
    /// </summary>
    event EventHandler<StreamStartPayload>? StreamStartReceived;

    /// <summary>
    /// Raised when a server changes this client's pairing configuration via
    /// <c>management/set-pairing-config</c> — any pairing method enabled, disabled, or
    /// reconfigured (min pairing code length, static pairing code, record-mode fallback), unpaired access
    /// changed, the stored Pairing PSK replaced, or any combination of these — or removes
    /// the stored Pairing record via <c>management/remove-record</c>. The SDK applies the
    /// change to its own effective state — never to the <see cref="ClientCapabilities"/>
    /// instance the app owns — so this state lives in memory only. Every setting the event
    /// reports has a <see cref="ClientCapabilities"/> property to reapply on the next startup:
    /// <see cref="ClientCapabilities.UnpairedAccessEnabled"/>,
    /// <see cref="ClientCapabilities.MinPairingCodeLength"/>, <see cref="ClientCapabilities.StaticPairingCode"/>,
    /// <see cref="ClientCapabilities.PairingPskEnabled"/>,
    /// <see cref="ClientCapabilities.DynamicPairingCodeEnabled"/>,
    /// <see cref="ClientCapabilities.StaticPairingCodeEnabled"/> and
    /// <see cref="ClientCapabilities.RecordModePskId"/>. Persist them and reapply them at
    /// construction, and the server's change survives a restart.
    /// <see cref="PairingConfigChangedEventArgs.PairingPskReplaced"/> is not one of those
    /// settings — it is a staleness signal, not a value to reapply. When it is true, any
    /// token previously returned by <see cref="EnsurePairingPsk"/> has stopped being
    /// current; the replaced Pairing PSK itself round-trips through
    /// <see cref="Sendspin.SDK.Connection.Noise.IPairingRecordStore"/>, not through
    /// <see cref="ClientCapabilities"/>.
    /// </summary>
    event EventHandler<PairingConfigChangedEventArgs>? PairingConfigChanged;

    /// <summary>
    /// Raised when a Pairing PSK or pairing code exchange completes, with the paired
    /// server id. Fires once per completed attempt.
    /// </summary>
    event EventHandler<string>? PairingCompleted;

    /// <summary>
    /// Raised when a pairing attempt is gesture-gated and no pairing window is open. Prompt
    /// the operator, then call <see cref="PairingWindow.Open"/> on the window supplied in
    /// <see cref="SendspinClientOptions.PairingWindow"/>.
    /// </summary>
    event EventHandler<PairingGestureRequestedEventArgs>? PairingGestureRequested;
}
