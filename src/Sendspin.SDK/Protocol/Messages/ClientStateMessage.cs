using System.Text.Json.Serialization;

namespace Sendspin.SDK.Protocol.Messages;

/// <summary>
/// State update message sent from client to server.
/// Used to report client state (synchronized, error, external_source)
/// and player state (volume, mute).
/// </summary>
public sealed class ClientStateMessage : IMessageWithPayload<ClientStatePayload>
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.ClientState;

    [JsonPropertyName("payload")]
    required public ClientStatePayload Payload { get; init; }

    /// <summary>
    /// Creates a synchronized state message with player volume/mute and timing parameters.
    /// This should be sent immediately after receiving server/hello.
    /// </summary>
    /// <param name="volume">Player volume (0-100).</param>
    /// <param name="muted">Whether the player is muted.</param>
    /// <param name="staticDelayMs">Static delay in milliseconds for group sync calibration.</param>
    /// <param name="requiredLeadTimeMs">Minimum startup lead time in milliseconds (codec init, decode warmup, backend buffering, DAC latency). Always required for players.</param>
    /// <param name="minBufferMs">Requested minimum ongoing buffer duration in milliseconds (absorbs network jitter, primarily for live streams). Always required for players.</param>
    /// <param name="supportedCommands">Optional player commands supported via server/command (subset of: 'set_static_delay'). Omitted from the wire when null.</param>
    public static ClientStateMessage CreateSynchronized(
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
                State = "synchronized",
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
    /// Creates a client/state message carrying only the operational <paramref name="state"/>, with
    /// no player object. Per the spec, subsequent client/state updates should include only the
    /// fields that changed — so a pure operational-state change ("error", "synchronized",
    /// "external_source") is sent without re-sending the (full) player object.
    /// </summary>
    /// <param name="state">Operational state: "synchronized", "error", or "external_source".</param>
    public static ClientStateMessage CreateState(string state)
    {
        return new ClientStateMessage
        {
            Payload = new ClientStatePayload { State = state }
        };
    }

    /// <summary>
    /// Creates an error state message (<c>{ "state": "error" }</c>), with no player object.
    /// </summary>
    /// <param name="errorMessage">
    /// Optional error detail for the caller's own logging. It is NOT sent on the wire — the spec
    /// defines no error-detail field, and a state-only delta must not carry the player object.
    /// </param>
    public static ClientStateMessage CreateError(string? errorMessage = null) => CreateState("error");
}

/// <summary>
/// Payload for client/state message.
/// </summary>
public sealed class ClientStatePayload
{
    /// <summary>
    /// Client state: "synchronized", "error", or "external_source".
    /// </summary>
    [JsonPropertyName("state")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? State { get; init; }

    /// <summary>
    /// Player-specific state (volume, mute, buffer level).
    /// Only included if client has player role.
    /// </summary>
    [JsonPropertyName("player")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PlayerStatePayload? Player { get; init; }
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
