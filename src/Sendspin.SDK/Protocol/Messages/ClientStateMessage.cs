using System.Text.Json.Serialization;

namespace Sendspin.SDK.Protocol.Messages;

/// <summary>
/// State update message sent from client to server. Carries client-level availability plus the
/// state object of every role the server currently has active.
/// </summary>
/// <remarks>
/// <para>
/// Spec PR #175 removed merging from <c>client/state</c>: every message MUST carry
/// <c>available</c> and the <b>full</b> state of each role object it includes, and omitting a
/// role object leaves that role's state unchanged. There is no delta form any more — a field a
/// message leaves out of an included role object is dropped by the server rather than retained.
/// </para>
/// <para>
/// The SDK therefore never builds a partial message by hand. <c>SendSpinClient</c> composes one
/// full message from its live state and sends it through a single choke point, so the initial
/// state, an availability flip, a player or source update, a command acknowledgement, a role
/// (re)activation and the post-pairing resend all put the same complete picture on the wire.
/// </para>
/// </remarks>
public sealed class ClientStateMessage : IMessageWithPayload<ClientStatePayload>
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.ClientState;

    [JsonPropertyName("payload")]
    required public ClientStatePayload Payload { get; init; }

    /// <summary>
    /// Builds a client/state message. Pass the state object of every role the server activated
    /// and that has something to report, and null for the rest.
    /// </summary>
    /// <param name="available">Whether the client is available to participate in Sendspin playback.</param>
    /// <param name="player">The player object, or null when <c>player</c> is not an active role.</param>
    /// <param name="source">The source object, or null when <c>source</c> is not an active role.</param>
    /// <param name="artwork">The artwork object, or null when <c>artwork</c> is not an active role.</param>
    /// <param name="visualizer">The visualizer object, or null when <c>visualizer</c> is not an active role.</param>
    /// <remarks>
    /// The role objects are parameters rather than being built here because which of them belong
    /// depends on <c>active_roles</c>, which this type cannot see. A state object for an inactive
    /// role is a client deviation the reference server rejects outright when run with
    /// <c>allow_noncompliant_clients=False</c>; all objects absent is legitimate, since a client
    /// whose active roles define no state object (metadata, controller, colour) still sends a
    /// <c>client/state</c> and <c>available</c> alone unlocks the server's streams for it.
    /// </remarks>
    public static ClientStateMessage Create(
        bool available,
        PlayerStatePayload? player = null,
        SourceStatePayload? source = null,
        ArtworkStatePayload? artwork = null,
        VisualizerStatePayload? visualizer = null)
    {
        return new ClientStateMessage
        {
            Payload = new ClientStatePayload
            {
                Available = available,
                Player = player,
                Source = source,
                Artwork = artwork,
                Visualizer = visualizer,
            }
        };
    }
}

/// <summary>
/// Payload for client/state message.
/// </summary>
public sealed class ClientStatePayload
{
    /// <summary>
    /// Whether this client is available to participate in Sendspin playback. For a player or
    /// source, <c>true</c> additionally means its clock has synchronized with the server on
    /// this connection — latched at the first convergence rather than tracking the live
    /// convergence statistic, so a transient convergence dip does not withdraw the claim.
    /// </summary>
    /// <remarks>
    /// Not optional: spec PR #175 requires every <c>client/state</c> to carry it. It used to be
    /// nullable so a role-only delta could omit it; with merging gone there are no deltas.
    /// </remarks>
    [JsonPropertyName("available")]
    public bool Available { get; init; }

    /// <summary>
    /// Player-specific state (volume, mute, timing, format preference).
    /// Only included if the server activated the player role.
    /// </summary>
    [JsonPropertyName("player")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PlayerStatePayload? Player { get; init; }

    /// <summary>Source-specific state (line-sense signal). Only if source is an active role.</summary>
    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SourceStatePayload? Source { get; init; }

    /// <summary>
    /// Artwork channel configuration. Only if artwork is an active role. Spec PR #195 moved this
    /// out of <c>artwork@v1_support</c> in <c>client/hello</c>, which no longer exists.
    /// </summary>
    [JsonPropertyName("artwork")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ArtworkStatePayload? Artwork { get; init; }

    /// <summary>
    /// Visualizer feature configuration. Only if visualizer is an active role. Spec PR #195 moved
    /// <c>types</c>, <c>rate_max</c> and <c>spectrum</c> here from <c>visualizer@v1_support</c>,
    /// which keeps only <c>buffer_capacity</c>.
    /// </summary>
    [JsonPropertyName("visualizer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VisualizerStatePayload? Visualizer { get; init; }
}

/// <summary>Source-specific state within client/state.</summary>
public sealed class SourceStatePayload
{
    /// <summary>Line sensing: 'present' or 'absent'. Only if line_sense is supported.</summary>
    [JsonPropertyName("signal")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Signal { get; init; }
}

/// <summary>
/// The <c>artwork</c> object within client/state: what the client wants streamed on each of its
/// artwork channels.
/// </summary>
/// <remarks>
/// The array is positional from channel 0 and holds 1-4 entries. A channel index the array does
/// not cover counts as <c>source: 'none'</c>, so a client may truncate after its last active
/// channel; an array longer than 4 is a protocol error the server closes the connection over.
/// </remarks>
public sealed class ArtworkStatePayload
{
    /// <summary>Per-channel configuration, index = channel number (0-3).</summary>
    [JsonPropertyName("channels")]
    public List<ArtworkChannelState> Channels { get; init; } = new();
}

/// <summary>
/// Configuration of one artwork channel, as carried in the client/state <c>artwork</c> object.
/// </summary>
/// <remarks>
/// Set <see cref="Source"/> to <see cref="ArtworkSources.None"/> to disable the channel, or back
/// to <c>album</c>/<c>artist</c> to re-enable it, without reconnecting. <see cref="Format"/>,
/// <see cref="Width"/> and <see cref="Height"/> are required unless the source is <c>none</c>,
/// and the server delivers each image at exactly the declared size.
/// </remarks>
public sealed class ArtworkChannelState
{
    /// <summary>Artwork source: "album", "artist", or "none". See <see cref="ArtworkSources"/>.</summary>
    [JsonPropertyName("source")]
    public string Source { get; init; } = ArtworkSources.Album;

    /// <summary>Image format ("jpeg" or "png"). Omitted when the source is "none".</summary>
    [JsonPropertyName("format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Format { get; init; } = "jpeg";

    /// <summary>Delivered image width in pixels. Omitted when the source is "none".</summary>
    [JsonPropertyName("width")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Width { get; init; } = 512;

    /// <summary>Delivered image height in pixels. Omitted when the source is "none".</summary>
    [JsonPropertyName("height")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Height { get; init; } = 512;

    /// <summary>
    /// This channel as the wire requires it: a disabled channel carries <c>source</c> alone, so
    /// the format and size a client keeps configured for when it is re-enabled do not travel as a
    /// contradictory declaration.
    /// </summary>
    internal ArtworkChannelState ForWire()
        => string.Equals(Source, ArtworkSources.None, StringComparison.Ordinal)
            ? new ArtworkChannelState { Source = ArtworkSources.None, Format = null, Width = null, Height = null }
            : this;
}

/// <summary>
/// The <c>visualizer</c> object within client/state: the feature types, frame-rate cap and
/// spectrum layout the client currently wants.
/// </summary>
/// <remarks>
/// <c>buffer_capacity</c> deliberately has no place here. It is a constant of the client, so it
/// stays a <c>visualizer@v1_support</c> field of <c>client/hello</c>; the spec's state object is
/// exactly types/rate_max/spectrum, and aiosendspin rejects a client that sends anything else.
/// </remarks>
public sealed class VisualizerStatePayload
{
    /// <summary>Requested feature types (subset of <see cref="VisualizerTypes"/>).</summary>
    [JsonPropertyName("types")]
    public List<string> Types { get; init; } = new();

    /// <summary>Maximum periodic frames per second the client wants.</summary>
    [JsonPropertyName("rate_max")]
    public int RateMax { get; init; }

    /// <summary>Spectrum configuration. Required when <c>spectrum</c> is among the types.</summary>
    [JsonPropertyName("spectrum")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VisualizerSpectrum? Spectrum { get; init; }
}

/// <summary>
/// Player-specific state within client/state message.
/// </summary>
/// <remarks>
/// The spec closes this object at <c>volume</c>, <c>muted</c>, <c>static_delay_ms</c>,
/// <c>required_lead_time_ms</c>, <c>min_buffer_ms</c>, <c>supported_commands</c> and
/// <c>format</c>, and a client MUST NOT send a field it does not define. Diagnostics belong in an
/// <c>_</c>-prefixed application-specific role object, not here.
/// </remarks>
public sealed class PlayerStatePayload
{
    /// <summary>
    /// Player volume (0-100).
    /// </summary>
    [JsonPropertyName("volume")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Volume { get; init; }

    /// <summary>
    /// Whether the player is muted.
    /// </summary>
    [JsonPropertyName("muted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Muted { get; init; }

    /// <summary>
    /// Output delay in milliseconds (0-5000) configured for this player: additional delay
    /// beyond the device's audio port, such as external speakers or an amplifier. Always
    /// required for players, so it is serialized unconditionally even when zero.
    /// </summary>
    /// <remarks>
    /// An integer on the wire, and never negative. The scheduler's own delay is a
    /// <see cref="double"/> over a wider range — fractional from calibration, negative to
    /// schedule later — so a caller must project it onto this type rather than passing it
    /// through. Reporting the raw value emitted a float, omitted the field entirely at its
    /// default, and could send a negative that a spec-conformant server rejects outright.
    /// <para>
    /// The wire name stays <c>static_delay_ms</c> deliberately. Spec 168a677 (spec PR #164)
    /// renamed the field to <c>output_delay_ms</c> with no alias, but no server has adopted it —
    /// aiosendspin still reads only the old name — so flipping this attribute would drop the
    /// delay from every server's view. It flips when servers adopt the rename, not before; the
    /// C# name follows the spec's vocabulary in the meantime.
    /// </para>
    /// </remarks>
    [JsonPropertyName("static_delay_ms")]
    public int OutputDelayMs { get; init; }

    /// <summary>
    /// Minimum startup lead time in milliseconds: codec init, decode warmup, audio
    /// backend buffering, and DAC latency. The server schedules the first audio chunk at least
    /// this far after the start/restart trigger (stream/start or stream/clear). Always required
    /// for players, so it is serialized unconditionally even when zero.
    /// </summary>
    [JsonPropertyName("required_lead_time_ms")]
    public int RequiredLeadTimeMs { get; init; }

    /// <summary>
    /// Requested minimum ongoing buffer duration in milliseconds during playback (primarily for
    /// live streams), used to absorb network jitter and decode/playback timing variance.
    /// Always required for players, so it is serialized unconditionally even when zero.
    /// </summary>
    [JsonPropertyName("min_buffer_ms")]
    public int MinBufferMs { get; init; }

    /// <summary>
    /// Player commands this client accepts via server/command, beyond the always-available
    /// volume/mute. Currently a subset of: 'set_static_delay'.
    /// </summary>
    /// <remarks>
    /// Not optional and never omitted: spec PR #175 dropped the <c>?</c>, because absence and
    /// <c>[]</c> said the same thing and the redundant encoding silently revoked
    /// <c>set_output_delay</c> for a reader that treated a missing field as unchanged. A player
    /// that accepts no commands sends <c>[]</c>.
    /// </remarks>
    [JsonPropertyName("supported_commands")]
    public List<string> SupportedCommands { get; init; } = new();

    /// <summary>
    /// The audio format this player currently prefers, or null for no overridden preference (the
    /// server then picks per the <c>supported_formats</c> priority order). A preference MUST be
    /// one of the entries the client listed in <c>player@v1_support.supported_formats</c>.
    /// </summary>
    /// <remarks>
    /// Spec PR #195 folded <c>stream/request-format</c> into this field. When it changes while a
    /// player stream is active the server re-derives the stream format and sends a new
    /// <c>stream/start</c> if it changed; with no active stream it applies to the next one.
    /// </remarks>
    [JsonPropertyName("format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PlayerFormatPreference? Format { get; init; }
}

/// <summary>
/// A complete audio format the player prefers, as carried in the client/state <c>player</c>
/// object. Every field is required: a partial request has no defined meaning since spec PR #195,
/// and the preference must match a <c>supported_formats</c> entry exactly.
/// </summary>
public sealed class PlayerFormatPreference
{
    /// <summary>Codec: "opus", "flac", or "pcm".</summary>
    [JsonPropertyName("codec")]
    required public string Codec { get; init; }

    /// <summary>Number of channels (1 = mono, 2 = stereo).</summary>
    [JsonPropertyName("channels")]
    public int Channels { get; init; }

    /// <summary>Sample rate in Hz (e.g. 44100, 48000).</summary>
    [JsonPropertyName("sample_rate")]
    public int SampleRate { get; init; }

    /// <summary>Bit depth (e.g. 16, 24). Meaningful for pcm/flac; ignored for opus.</summary>
    [JsonPropertyName("bit_depth")]
    public int BitDepth { get; init; }
}
