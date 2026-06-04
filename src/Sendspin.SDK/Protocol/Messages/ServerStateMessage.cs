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
public sealed class ServerStatePayload
{
    /// <summary>
    /// Current track metadata and playback progress.
    /// </summary>
    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ServerMetadata? Metadata { get; init; }

    /// <summary>
    /// Controller state (volume, mute, supported commands).
    /// </summary>
    [JsonPropertyName("controller")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ControllerState? Controller { get; init; }

    /// <summary>
    /// Color palette derived from the current audio. Only sent to clients with the <c>color</c> role.
    /// </summary>
    [JsonPropertyName("color")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ColorState? Color { get; init; }
}

/// <summary>
/// Track metadata from server/state message.
/// </summary>
public sealed class ServerMetadata
{
    [JsonPropertyName("timestamp")]
    public long? Timestamp { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("artist")]
    public string? Artist { get; init; }

    [JsonPropertyName("album_artist")]
    public string? AlbumArtist { get; init; }

    [JsonPropertyName("album")]
    public string? Album { get; init; }

    [JsonPropertyName("artwork_url")]
    public string? ArtworkUrl { get; init; }

    [JsonPropertyName("year")]
    public int? Year { get; init; }

    [JsonPropertyName("track")]
    public int? Track { get; init; }

    /// <summary>
    /// Playback progress. <see cref="Optional{T}"/> distinguishes "absent"
    /// (no update; keep existing) from "present but null" (track ended; clear)
    /// and "present with value" (update).
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
}
