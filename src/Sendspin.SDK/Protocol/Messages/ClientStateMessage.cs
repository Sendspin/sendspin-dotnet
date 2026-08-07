using System.Text.Json.Serialization;

namespace Sendspin.SDK.Protocol.Messages;

/// <summary>
/// State update message sent from client to server.
/// Used to report availability and player state (volume, mute).
/// </summary>
public sealed class ClientStateMessage : IMessageWithPayload<ClientStatePayload>
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.ClientState;

    [JsonPropertyName("payload")]
    required public ClientStatePayload Payload { get; init; }

    /// <summary>
    /// Builds the initial client/state message, which per spec MUST carry every state field.
    /// Sent once per connection after server/activate — immediately for clients that need no
    /// clock sync, deferred until the first sync convergence for those that do.
    /// </summary>
    /// <param name="available">Whether the client is available to participate in Sendspin playback.</param>
    /// <param name="volume">Player volume (0-100).</param>
    /// <param name="muted">Whether the player is muted.</param>
    /// <param name="staticDelayMs">Static delay in milliseconds for group sync calibration.</param>
    /// <param name="requiredLeadTimeMs">Minimum startup lead time in milliseconds (codec init, decode warmup, backend buffering, DAC latency). Always required for players.</param>
    /// <param name="minBufferMs">Requested minimum ongoing buffer duration in milliseconds (absorbs network jitter, primarily for live streams). Always required for players.</param>
    /// <param name="supportedCommands">Optional player commands supported via server/command (subset of: 'set_static_delay'). Omitted from the wire when null.</param>
    public static ClientStateMessage CreateInitial(
        bool available,
        int volume = 100,
        bool muted = false,
        double staticDelayMs = 0.0,
        int requiredLeadTimeMs = 0,
        int minBufferMs = 0,
        List<string>? supportedCommands = null)
    {
        return new ClientStateMessage
        {
            Payload = new ClientStatePayload
            {
                Available = available,
                Player = new PlayerStatePayload
                {
                    Volume = volume,
                    Muted = muted,
                    StaticDelayMs = staticDelayMs,
                    RequiredLeadTimeMs = requiredLeadTimeMs,
                    MinBufferMs = minBufferMs,
                    SupportedCommands = supportedCommands
                }
            }
        };
    }

    /// <summary>
    /// Builds a delta that reports only a change in availability, with no role objects.
    /// </summary>
    /// <param name="available">Whether the client is available to participate in Sendspin playback.</param>
    public static ClientStateMessage CreateAvailability(bool available)
    {
        return new ClientStateMessage
        {
            Payload = new ClientStatePayload { Available = available }
        };
    }

    /// <summary>
    /// Builds a delta carrying only the player object. It deliberately omits
    /// <c>available</c>: a volume or mute change says nothing about whether the client is
    /// available, and asserting it here would overwrite the server's view.
    /// </summary>
    /// <param name="volume">Player volume (0-100).</param>
    /// <param name="muted">Whether the player is muted.</param>
    /// <param name="staticDelayMs">Static delay in milliseconds for group sync calibration.</param>
    /// <param name="requiredLeadTimeMs">Minimum startup lead time in milliseconds (codec init, decode warmup, backend buffering, DAC latency). Always required for players.</param>
    /// <param name="minBufferMs">Requested minimum ongoing buffer duration in milliseconds (absorbs network jitter, primarily for live streams). Always required for players.</param>
    /// <param name="supportedCommands">Optional player commands supported via server/command (subset of: 'set_static_delay'). Omitted from the wire when null.</param>
    public static ClientStateMessage CreatePlayerState(
        int volume,
        bool muted,
        double staticDelayMs,
        int requiredLeadTimeMs,
        int minBufferMs,
        List<string>? supportedCommands = null)
    {
        return new ClientStateMessage
        {
            Payload = new ClientStatePayload
            {
                Player = new PlayerStatePayload
                {
                    Volume = volume,
                    Muted = muted,
                    StaticDelayMs = staticDelayMs,
                    RequiredLeadTimeMs = requiredLeadTimeMs,
                    MinBufferMs = minBufferMs,
                    SupportedCommands = supportedCommands
                }
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
    /// source, <c>true</c> additionally means its clock is synchronized with the server.
    /// Null omits the field, for a delta that changes only the role objects.
    /// </summary>
    [JsonPropertyName("available")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Available { get; init; }

    /// <summary>
    /// Player-specific state (volume, mute, buffer level).
    /// Only included if client has player role.
    /// </summary>
    [JsonPropertyName("player")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PlayerStatePayload? Player { get; init; }

    /// <summary>Source-specific state (line-sense signal). Only if client has source role.</summary>
    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SourceStatePayload? Source { get; init; }
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
/// Player-specific state within client/state message.
/// </summary>
/// <remarks>
/// Per Sendspin spec, the player object contains <c>volume</c> and <c>muted</c>.
/// The <c>buffer_level</c> and <c>error</c> fields are SDK extensions for diagnostics.
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
    /// Buffer level in milliseconds.
    /// </summary>
    /// <remarks>
    /// SDK extension (not part of Sendspin spec). Used for diagnostic reporting.
    /// </remarks>
    [JsonPropertyName("buffer_level")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BufferLevel { get; init; }

    /// <summary>
    /// Error message if in error state.
    /// </summary>
    /// <remarks>
    /// SDK extension (not part of Sendspin spec). Used for error reporting.
    /// </remarks>
    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }

    /// <summary>
    /// Static delay in milliseconds configured for this player.
    /// Used by the server during GroupSync calibration to compensate for
    /// device audio output latency across the group.
    /// </summary>
    [JsonPropertyName("static_delay_ms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double StaticDelayMs { get; init; }

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
    /// volume/mute. Currently a subset of: 'set_static_delay'. Omitted from the wire when null.
    /// </summary>
    [JsonPropertyName("supported_commands")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? SupportedCommands { get; init; }
}
