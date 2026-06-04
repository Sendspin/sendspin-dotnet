using System.Text.Json.Serialization;

namespace Sendspin.SDK.Protocol.Messages;

/// <summary>
/// Client request to change a stream's format on the fly (per the Sendspin spec). Uses the
/// envelope format <c>{ "type": "stream/request-format", "payload": { "artwork": { ... } } }</c>.
/// The server responds with a <c>stream/start</c> for the requested role.
/// </summary>
public sealed class StreamRequestFormatMessage : IMessageWithPayload<StreamRequestFormatPayload>
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.StreamRequestFormat;

    [JsonPropertyName("payload")]
    required public StreamRequestFormatPayload Payload { get; init; }

    /// <summary>
    /// Creates a stream/request-format message carrying an artwork channel change.
    /// </summary>
    public static StreamRequestFormatMessage ForArtwork(ArtworkRequestFormat artwork) =>
        new() { Payload = new StreamRequestFormatPayload { Artwork = artwork } };

    /// <summary>
    /// Creates a stream/request-format message carrying a visualizer renegotiation.
    /// </summary>
    public static StreamRequestFormatMessage ForVisualizer(VisualizerRequestFormat visualizer) =>
        new() { Payload = new StreamRequestFormatPayload { Visualizer = visualizer } };
}

/// <summary>
/// Payload for the stream/request-format message. Each role object is optional.
/// </summary>
public sealed class StreamRequestFormatPayload
{
    /// <summary>
    /// Artwork channel format change. Only for clients with the artwork role.
    /// </summary>
    [JsonPropertyName("artwork")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ArtworkRequestFormat? Artwork { get; init; }

    /// <summary>
    /// Visualizer renegotiation. Only for clients with the visualizer role.
    /// </summary>
    [JsonPropertyName("visualizer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VisualizerRequestFormat? Visualizer { get; init; }
}

/// <summary>
/// Renegotiates the visualizer stream. All fields are optional; omitted fields keep their prior
/// value. The server responds with a <c>stream/start</c> carrying the new visualizer config.
/// </summary>
public sealed class VisualizerRequestFormat
{
    /// <summary>Requested feature types (subset of <see cref="VisualizerTypes"/>).</summary>
    [JsonPropertyName("types")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Types { get; init; }

    /// <summary>Requested maximum frame rate.</summary>
    [JsonPropertyName("rate_max")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RateMax { get; init; }

    /// <summary>Requested buffer capacity in bytes.</summary>
    [JsonPropertyName("buffer_capacity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BufferCapacity { get; init; }

    /// <summary>Requested spectrum configuration.</summary>
    [JsonPropertyName("spectrum")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VisualizerSpectrum? Spectrum { get; init; }
}

/// <summary>
/// Requests a format/source change for a single artwork channel. Omitted fields are left unchanged
/// by the server. Set <see cref="Source"/> to <c>"none"</c> to disable the channel, or back to
/// <c>"album"</c>/<c>"artist"</c> to re-enable it, without reconnecting.
/// </summary>
public sealed class ArtworkRequestFormat
{
    /// <summary>
    /// Channel number (0-3) corresponding to the channel index declared in client/hello.
    /// </summary>
    [JsonPropertyName("channel")]
    public int Channel { get; init; }

    /// <summary>
    /// Requested artwork source: "album", "artist", or "none". See <see cref="ArtworkSources"/>.
    /// </summary>
    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; init; }

    /// <summary>
    /// Requested image format: "jpeg", "png", or "bmp".
    /// </summary>
    [JsonPropertyName("format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Format { get; init; }

    /// <summary>
    /// Requested maximum width in pixels.
    /// </summary>
    [JsonPropertyName("media_width")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MediaWidth { get; init; }

    /// <summary>
    /// Requested maximum height in pixels.
    /// </summary>
    [JsonPropertyName("media_height")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MediaHeight { get; init; }
}
