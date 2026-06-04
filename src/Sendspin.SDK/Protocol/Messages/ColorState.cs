using System.Text.Json.Serialization;
using Sendspin.SDK.Models;

namespace Sendspin.SDK.Protocol.Messages;

/// <summary>
/// The <c>color</c> object in <c>server/state</c>: a palette derived from the current audio.
/// </summary>
/// <remarks>
/// Each color is an <see cref="Optional{T}"/> of a nullable <see cref="RgbColor"/> so the three
/// <c>server/state</c> delta states are distinguishable: absent (no change), present-and-null
/// (clear), and present-with-value (update). The server guarantees WCAG contrast ratios between
/// these colors; clients consume them as-is.
/// </remarks>
public sealed class ColorState
{
    /// <summary>Server clock time in microseconds for when these colors are valid.</summary>
    [JsonPropertyName("timestamp")]
    public long? Timestamp { get; init; }

    /// <summary>Background color suitable for dark mode.</summary>
    [JsonPropertyName("background_dark")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<RgbColor?> BackgroundDark { get; init; } = Optional<RgbColor?>.Absent();

    /// <summary>Background color suitable for light mode.</summary>
    [JsonPropertyName("background_light")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<RgbColor?> BackgroundLight { get; init; } = Optional<RgbColor?>.Absent();

    /// <summary>The dominant color. Not adjusted for contrast.</summary>
    [JsonPropertyName("primary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<RgbColor?> Primary { get; init; } = Optional<RgbColor?>.Absent();

    /// <summary>A secondary or complementary color. Not adjusted for contrast.</summary>
    [JsonPropertyName("accent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<RgbColor?> Accent { get; init; } = Optional<RgbColor?>.Absent();

    /// <summary>A light color suitable for use on dark backgrounds.</summary>
    [JsonPropertyName("on_dark")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<RgbColor?> OnDark { get; init; } = Optional<RgbColor?>.Absent();

    /// <summary>A dark color suitable for use on light backgrounds.</summary>
    [JsonPropertyName("on_light")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<RgbColor?> OnLight { get; init; } = Optional<RgbColor?>.Absent();
}
