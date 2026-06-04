using System.Text.Json.Serialization;

namespace Sendspin.SDK.Models;

/// <summary>
/// The current color palette for the group, derived from the audio and delivered via the
/// <c>color</c> role. Each color is null until the server provides it (or after it is cleared).
/// </summary>
public sealed class ColorPalette
{
    /// <summary>Server clock time in microseconds for when these colors are valid.</summary>
    [JsonPropertyName("timestamp")]
    public long? Timestamp { get; set; }

    /// <summary>Background color suitable for dark mode.</summary>
    [JsonPropertyName("background_dark")]
    public RgbColor? BackgroundDark { get; set; }

    /// <summary>Background color suitable for light mode.</summary>
    [JsonPropertyName("background_light")]
    public RgbColor? BackgroundLight { get; set; }

    /// <summary>The dominant color.</summary>
    [JsonPropertyName("primary")]
    public RgbColor? Primary { get; set; }

    /// <summary>A secondary or complementary color.</summary>
    [JsonPropertyName("accent")]
    public RgbColor? Accent { get; set; }

    /// <summary>A light color suitable for use on dark backgrounds.</summary>
    [JsonPropertyName("on_dark")]
    public RgbColor? OnDark { get; set; }

    /// <summary>A dark color suitable for use on light backgrounds.</summary>
    [JsonPropertyName("on_light")]
    public RgbColor? OnLight { get; set; }
}
