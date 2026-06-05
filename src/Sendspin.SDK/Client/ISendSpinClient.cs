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
    /// Connects to a Sendspin server.
    /// </summary>
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
    /// <param name="bufferCapacity">Requested buffer capacity in bytes, or null to leave unchanged.</param>
    /// <param name="spectrum">Requested spectrum configuration, or null to leave unchanged.</param>
    Task RequestVisualizerFormatAsync(List<string>? types = null, int? rateMax = null, int? bufferCapacity = null, VisualizerSpectrum? spectrum = null);

    /// <summary>
    /// Sends the current player state (volume, muted) to the server.
    /// This is used to report local state changes to Music Assistant.
    /// </summary>
    /// <param name="volume">Current volume level (0-100).</param>
    /// <param name="muted">Current mute state.</param>
    /// <param name="staticDelayMs">Static delay in milliseconds for group sync calibration.</param>
    Task SendPlayerStateAsync(int volume, bool muted, double staticDelayMs = 0.0);

    /// <summary>
    /// Updates the player timing parameters reported to the server and re-sends client/state.
    /// </summary>
    /// <remarks>
    /// Use this when measured conditions change (e.g. empirically measured lead time after warmup,
    /// or a link-type change). Per the Sendspin spec, callers should debounce updates locally and
    /// report only sustained changes — the SDK sends each call verbatim. No-op on the wire when the
    /// client is not currently connected; the new values are still applied to subsequent state sends.
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
    /// Leaves the <c>external_source</c> state, reporting <c>synchronized</c> so the client can
    /// resume participating in Sendspin playback. <see cref="IsExternalSource"/> only clears if the
    /// notification succeeds.
    /// </summary>
    Task ExitExternalSourceAsync();

    /// <summary>
    /// Clears the audio buffer, causing the pipeline to restart buffering.
    /// Use this when audio sync parameters change and you want immediate effect.
    /// </summary>
    void ClearAudioBuffer();

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
    /// Event raised when an artwork image is received on a channel (0-3). Carries the channel,
    /// display timestamp, and encoded image bytes.
    /// </summary>
    event EventHandler<ArtworkReceivedEventArgs>? ArtworkReceived;

    /// <summary>
    /// Event raised when a single artwork channel is cleared (an empty artwork binary message).
    /// Carries the channel that was cleared.
    /// </summary>
    event EventHandler<ArtworkClearedEventArgs>? ArtworkCleared;

    /// <summary>
    /// Event raised whenever a <c>server/state</c> carries a <c>color</c> object (the <c>color</c>
    /// role) — including updates that leave the resolved values unchanged. Carries the current
    /// merged <see cref="ColorPalette"/>, also available as <see cref="GroupState.Colors"/>.
    /// </summary>
    event EventHandler<ColorPalette>? ColorChanged;

    /// <summary>
    /// Event raised for each decoded visualizer feature frame (the <c>visualizer@v1</c> role). Each
    /// <see cref="VisualizerFrame"/> carries one feature type (loudness, f_peak, spectrum, beat,
    /// peak, or pitch). Malformed frames are dropped and do not raise the event.
    /// </summary>
    event EventHandler<VisualizerFrame>? VisualizationReceived;

    /// <summary>
    /// Event raised when the clock synchronizer first converges to a stable estimate.
    /// This indicates that the client is ready for sample-accurate synchronized playback.
    /// </summary>
    event EventHandler<ClockSyncStatus>? ClockSyncConverged;

    /// <summary>
    /// Raised when a <c>server/hello</c> message is received and parsed.
    /// Fires once per successful handshake (including reconnects). The payload is the
    /// same object cached on <see cref="LastServerHello"/>.
    /// </summary>
    event EventHandler<ServerHelloPayload>? ServerHelloReceived;

    /// <summary>
    /// Raised when a <c>stream/start</c> message is received and parsed.
    /// Fires for every <c>stream/start</c>, whether it carries audio format, artwork metadata, or both.
    /// The payload is the same object cached on <see cref="LastStreamStart"/>.
    /// </summary>
    event EventHandler<StreamStartPayload>? StreamStartReceived;
}
