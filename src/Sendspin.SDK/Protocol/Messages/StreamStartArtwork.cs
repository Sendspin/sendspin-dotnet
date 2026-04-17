// <copyright file="StreamStartArtwork.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using System.Text.Json.Serialization;

namespace Sendspin.SDK.Protocol.Messages;

/// <summary>
/// Artwork metadata carried inside a <c>stream/start</c> payload.
/// Describes which artwork channels the server is about to stream (one or more album/etc. images).
/// Distinct from <see cref="ArtworkSupport"/>, which is the client's capability advertisement in <c>client/hello</c>.
/// </summary>
public sealed class StreamStartArtwork
{
    /// <summary>
    /// Channels the server is streaming. Each channel corresponds to a binary artwork chunk
    /// delivered on its own channel index (0-3 per the Sendspin spec).
    /// </summary>
    [JsonPropertyName("channels")]
    public List<ArtworkStreamChannel> Channels { get; set; } = new();
}

/// <summary>
/// One artwork channel the server is about to stream.
/// </summary>
public sealed class ArtworkStreamChannel
{
    /// <summary>
    /// Semantic source of the artwork (e.g. <c>"album"</c>, <c>"artist"</c>).
    /// </summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Image format of the bytes that will follow on the binary channel (e.g. <c>"jpeg"</c>).
    /// </summary>
    [JsonPropertyName("format")]
    public string Format { get; set; } = string.Empty;

    /// <summary>
    /// Actual pixel width of the image the server will send. 0 if unspecified.
    /// </summary>
    [JsonPropertyName("width")]
    public int Width { get; set; }

    /// <summary>
    /// Actual pixel height of the image the server will send. 0 if unspecified.
    /// </summary>
    [JsonPropertyName("height")]
    public int Height { get; set; }
}
