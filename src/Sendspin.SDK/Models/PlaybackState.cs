using System.Text.Json.Serialization;

namespace Sendspin.SDK.Models;

/// <summary>
/// Represents the current playback state of a group.
/// </summary>
/// <remarks>
/// Read from <c>group/update</c>'s <c>playback_state</c>, for which the spec defines
/// <c>'playing'</c> and <c>'stopped'</c>. The converter matches the wire string against the
/// member names without regard to case; System.Text.Json does not consult
/// <c>JsonPropertyName</c> on enum members, so those attributes have no effect, and written
/// out a value would be the member name (<c>"Playing"</c>). The SDK only ever reads this enum
/// from the wire. The converter also accepts the other member names and an integer, none of
/// which a conformant server sends.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<PlaybackState>))]
public enum PlaybackState
{
    /// <summary>
    /// No media loaded or stopped. The value before the first <c>group/update</c>, and the one
    /// the SDK sets itself when a stream ends or the connection drops. Not a wire value.
    /// </summary>
    [JsonPropertyName("idle")]
    Idle,

    /// <summary>
    /// The spec's <c>'stopped'</c>.
    /// </summary>
    [JsonPropertyName("stopped")]
    Stopped,

    /// <summary>
    /// The spec's <c>'playing'</c>.
    /// </summary>
    [JsonPropertyName("playing")]
    Playing,

    /// <summary>
    /// Playback paused. Not defined by the spec.
    /// </summary>
    [JsonPropertyName("paused")]
    Paused,

    /// <summary>
    /// Error state (e.g., buffer underrun, codec error). Not defined by the spec.
    /// </summary>
    [JsonPropertyName("error")]
    Error
}
