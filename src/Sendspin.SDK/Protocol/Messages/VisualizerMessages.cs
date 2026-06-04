using System.Text.Json.Serialization;

namespace Sendspin.SDK.Protocol.Messages;

/// <summary>
/// Visualizer feature type identifiers (the <c>visualizer@v1</c> role).
/// </summary>
public static class VisualizerTypes
{
    /// <summary>Overall loudness (binary type 16).</summary>
    public const string Loudness = "loudness";

    /// <summary>Dominant frequency + amplitude (binary type 18).</summary>
    public const string FPeak = "f_peak";

    /// <summary>Display-binned spectrum (binary type 19). Requires a spectrum config.</summary>
    public const string Spectrum = "spectrum";

    /// <summary>Musical beat events (binary type 17).</summary>
    public const string Beat = "beat";

    /// <summary>Energy-onset events with strength (binary type 20).</summary>
    public const string Peak = "peak";

    /// <summary>Perceived pitch (binary type 21).</summary>
    public const string Pitch = "pitch";
}

/// <summary>
/// Spectrum bin-spacing scales for the visualizer role.
/// </summary>
public static class VisualizerScales
{
    /// <summary>Linear frequency spacing.</summary>
    public const string Linear = "lin";

    /// <summary>Logarithmic frequency spacing.</summary>
    public const string Logarithmic = "log";

    /// <summary>Mel (perceptual) frequency spacing.</summary>
    public const string Mel = "mel";
}

/// <summary>
/// Spectrum display configuration for the visualizer role. Required when <c>spectrum</c> is among
/// the requested types.
/// </summary>
public sealed class VisualizerSpectrum
{
    /// <summary>Number of display bins the client wants the spectrum binned into.</summary>
    [JsonPropertyName("n_disp_bins")]
    public int NDispBins { get; init; }

    /// <summary>Bin spacing. See <see cref="VisualizerScales"/> ("lin", "log", or "mel").</summary>
    [JsonPropertyName("scale")]
    required public string Scale { get; init; }

    /// <summary>Lowest frequency (Hz) covered by the bins.</summary>
    [JsonPropertyName("f_min")]
    public int FMin { get; init; }

    /// <summary>Highest frequency (Hz) covered by the bins.</summary>
    [JsonPropertyName("f_max")]
    public int FMax { get; init; }
}

/// <summary>
/// The <c>visualizer@v1_support</c> object in <c>client/hello</c>: what audio features the client
/// can render and at what rate.
/// </summary>
public sealed class VisualizerSupport
{
    /// <summary>Max size in bytes of buffered visualization messages not yet displayed.</summary>
    [JsonPropertyName("buffer_capacity")]
    public int BufferCapacity { get; init; }

    /// <summary>Maximum frames per second the client wants to receive.</summary>
    [JsonPropertyName("rate_max")]
    public int RateMax { get; init; }

    /// <summary>
    /// Requested feature types, a subset of <see cref="VisualizerTypes"/>
    /// (loudness, f_peak, spectrum, beat, peak, pitch).
    /// </summary>
    [JsonPropertyName("types")]
    required public List<string> Types { get; init; }

    /// <summary>Spectrum configuration. Required when <c>spectrum</c> is in <see cref="Types"/>.</summary>
    [JsonPropertyName("spectrum")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VisualizerSpectrum? Spectrum { get; init; }
}

/// <summary>
/// The <c>visualizer</c> object in <c>stream/start</c>: the negotiated stream configuration. The
/// client uses <see cref="Spectrum"/>'s bin count to validate incoming spectrum frames.
/// </summary>
public sealed class StreamStartVisualizer
{
    /// <summary>Negotiated feature types the server will stream.</summary>
    [JsonPropertyName("types")]
    public List<string> Types { get; init; } = new();

    /// <summary>Negotiated maximum frame rate.</summary>
    [JsonPropertyName("rate_max")]
    public int RateMax { get; init; }

    /// <summary>Whether the server marks downbeats on beat frames (only meaningful when beat is negotiated).</summary>
    [JsonPropertyName("tracks_downbeats")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? TracksDownbeats { get; init; }

    /// <summary>Negotiated spectrum configuration (present when spectrum is negotiated).</summary>
    [JsonPropertyName("spectrum")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VisualizerSpectrum? Spectrum { get; init; }
}
