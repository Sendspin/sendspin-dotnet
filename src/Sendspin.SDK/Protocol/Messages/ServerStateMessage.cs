using System.Text.Json.Serialization;
using Sendspin.SDK.Protocol;

namespace Sendspin.SDK.Protocol.Messages;

/// <summary>
/// State update message from server containing metadata and controller state.
/// This is the primary way Music Assistant sends track metadata to clients.
/// </summary>
public sealed class ServerStateMessage : IMessageWithPayload<ServerStatePayload>
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.ServerState;

    [JsonPropertyName("payload")]
    required public ServerStatePayload Payload { get; init; }
}

/// <summary>
/// Payload for server/state message.
/// </summary>
/// <remarks>
/// The role objects use <see cref="Optional{T}"/> for the same reason their leaf fields do, one
/// level up. The spec gives an explicit null role object its own meaning — "a whole role object
/// set to <c>null</c> clears all of that role's state" — and the server sends exactly that when a
/// state role leaves <c>active_roles</c>, and when pairing quiesces. Plain nullables collapsed
/// that into "absent", so the clear deserialized into a no-op and the client kept exposing the
/// deactivated role's last values indefinitely (#196).
/// </remarks>
public sealed class ServerStatePayload
{
    /// <summary>
    /// Current track metadata and playback progress. Absent = no change, present-null = clear all
    /// metadata state, present-with-value = merge the delta.
    /// </summary>
    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<ServerMetadata?> Metadata { get; init; } = Optional<ServerMetadata?>.Absent();

    /// <summary>
    /// Controller state (volume, mute, supported commands). Absent = no change, present-null =
    /// clear all controller state, present-with-value = merge the delta.
    /// </summary>
    [JsonPropertyName("controller")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<ControllerState?> Controller { get; init; } = Optional<ControllerState?>.Absent();

    /// <summary>
    /// Color palette derived from the current audio. Only sent to clients with the <c>color</c>
    /// role. Absent = no change, present-null = clear the whole palette, present-with-value =
    /// merge the delta.
    /// </summary>
    [JsonPropertyName("color")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<ColorState?> Color { get; init; } = Optional<ColorState?>.Absent();
}

/// <summary>
/// Track metadata from server/state message.
/// </summary>
/// <remarks>
/// All fields use <see cref="Optional{T}"/> to distinguish "absent" (partial update; keep existing)
/// from "present but null" (explicit clear; e.g. artless track or <c>cleared_update()</c> on stop)
/// and "present with value" (update). This matches <c>UndefinedField</c> in aiosendspin.
/// </remarks>
public sealed class ServerMetadata
{
    [JsonPropertyName("timestamp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<long?> Timestamp { get; init; } = Optional<long?>.Absent();

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<string?> Title { get; init; } = Optional<string?>.Absent();

    [JsonPropertyName("artist")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<string?> Artist { get; init; } = Optional<string?>.Absent();

    [JsonPropertyName("album_artist")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<string?> AlbumArtist { get; init; } = Optional<string?>.Absent();

    [JsonPropertyName("album")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<string?> Album { get; init; } = Optional<string?>.Absent();

    [JsonPropertyName("artwork_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<string?> ArtworkUrl { get; init; } = Optional<string?>.Absent();

    [JsonPropertyName("year")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<int?> Year { get; init; } = Optional<int?>.Absent();

    [JsonPropertyName("track")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<int?> Track { get; init; } = Optional<int?>.Absent();

    /// <summary>
    /// Playback progress. Absent = keep existing, present-null = track ended (clear),
    /// present-with-value = update.
    /// </summary>
    [JsonPropertyName("progress")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<PlaybackProgress?> Progress { get; init; } = Optional<PlaybackProgress?>.Absent();
}

/// <summary>
/// Playback progress information.
/// </summary>
public sealed class PlaybackProgress
{
    /// <summary>
    /// Current position in milliseconds.
    /// Using double to handle servers that send numeric values as floats.
    /// </summary>
    [JsonPropertyName("track_progress")]
    public double? TrackProgress { get; init; }

    /// <summary>
    /// Total duration in milliseconds.
    /// Using double to handle servers that send numeric values as floats.
    /// Nullable for streams with unknown duration.
    /// </summary>
    [JsonPropertyName("track_duration")]
    public double? TrackDuration { get; init; }

    /// <summary>
    /// Playback speed (1000 = normal speed).
    /// Using double to handle servers that send numeric values as floats.
    /// </summary>
    [JsonPropertyName("playback_speed")]
    public double? PlaybackSpeed { get; init; }
}

/// <summary>
/// Controller state from server/state message.
/// </summary>
public sealed class ControllerState
{
    [JsonPropertyName("supported_commands")]
    public List<string>? SupportedCommands { get; init; }

    [JsonPropertyName("volume")]
    public int? Volume { get; init; }

    [JsonPropertyName("muted")]
    public bool? Muted { get; init; }

    /// <summary>
    /// Repeat mode: "off", "one", or "all". Per the Sendspin spec, repeat state is carried in the
    /// controller object (moved here from the metadata object).
    /// </summary>
    [JsonPropertyName("repeat")]
    public string? Repeat { get; init; }

    /// <summary>
    /// Whether shuffle is enabled. Per the Sendspin spec, shuffle state is carried in the controller
    /// object (moved here from the metadata object).
    /// </summary>
    [JsonPropertyName("shuffle")]
    public bool? Shuffle { get; init; }

    /// <summary>
    /// Maximum absolute position in milliseconds a <c>seek</c> may target (e.g. the end of the
    /// current track). Per the Sendspin spec the server includes this whenever <c>seek</c> is in
    /// <see cref="SupportedCommands"/>, and omits <c>seek</c> when the seekable range is unknown
    /// (e.g. live streams) — <c>seek_relative</c> may still be offered in that case.
    /// </summary>
    [JsonPropertyName("seek_max_ms")]
    public int? SeekMaxMs { get; init; }
}
