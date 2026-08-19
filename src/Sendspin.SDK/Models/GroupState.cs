using System.Text.Json.Serialization;

namespace Sendspin.SDK.Models;

/// <summary>
/// Aggregate state for display purposes, populated from <c>group/update</c>
/// (identity and playback state) and <c>server/state</c> (controller and metadata).
/// </summary>
public sealed class GroupState
{
    /// <summary>Unique group identifier.</summary>
    [JsonPropertyName("group_id")]
    public string GroupId { get; set; } = string.Empty;

    /// <summary>Display name for the group.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Current playback state.</summary>
    [JsonPropertyName("playback_state")]
    public PlaybackState PlaybackState { get; set; } = PlaybackState.Idle;

    /// <summary>Group volume level (0-100).</summary>
    [JsonPropertyName("volume")]
    public int Volume { get; set; } = 100;

    /// <summary>Whether the group is muted.</summary>
    [JsonPropertyName("muted")]
    public bool Muted { get; set; }

    /// <summary>Current track metadata.</summary>
    [JsonPropertyName("metadata")]
    public TrackMetadata? Metadata { get; set; }

    /// <summary>Whether shuffle is enabled.</summary>
    [JsonPropertyName("shuffle")]
    public bool Shuffle { get; set; }

    /// <summary>Repeat mode ("off", "one", "all").</summary>
    [JsonPropertyName("repeat")]
    public string? Repeat { get; set; }

    /// <summary>
    /// Controller commands the server currently accepts for this group, from the
    /// <c>server/state</c> controller object. Lets an embedder enable/disable controls and avoid
    /// sending commands the server would ignore. Null until the server reports it.
    /// </summary>
    [JsonPropertyName("supported_commands")]
    public List<string>? SupportedCommands { get; set; }

    /// <summary>
    /// Maximum absolute position in milliseconds a <c>seek</c> command may target, from the
    /// <c>server/state</c> controller object — the upper bound for a seek bar. Null until the
    /// server reports it, and null again once it says the seekable range is unknown (a partial
    /// update that omits the field keeps the last value, an explicit null clears it). Still gate
    /// the control itself on <see cref="SupportedCommands"/> containing <c>seek</c>.
    /// </summary>
    [JsonPropertyName("seek_max_ms")]
    public int? SeekMaxMs { get; set; }

    /// <summary>
    /// Current color palette derived from the audio (the <c>color</c> role). Populated from
    /// <c>server/state</c> color updates; individual colors are null until the server provides them.
    /// </summary>
    [JsonPropertyName("color")]
    public ColorPalette Colors { get; set; } = new();
}
