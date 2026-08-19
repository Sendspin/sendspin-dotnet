// <copyright file="StreamEndMessage.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using System.Text.Json.Serialization;

namespace Sendspin.SDK.Protocol.Messages;

/// <summary>
/// Message from server indicating audio stream has ended.
/// Uses envelope format: { "type": "stream/end", "payload": { ... } }.
/// </summary>
public sealed class StreamEndMessage : IMessageWithPayload<StreamEndPayload>
{
    /// <inheritdoc/>
    [JsonPropertyName("type")]
    public string Type => MessageTypes.StreamEnd;

    /// <inheritdoc/>
    [JsonPropertyName("payload")]
    public StreamEndPayload Payload { get; set; } = new();

    // Convenience accessor
    [JsonIgnore]
    public List<string>? Roles => Payload.Roles;
}

/// <summary>
/// Payload for stream/end message.
/// </summary>
public sealed class StreamEndPayload
{
    /// <summary>
    /// Gets or sets the server's monotonic clock timestamp, in microseconds, at which it
    /// transmitted this message.
    /// </summary>
    [JsonPropertyName("server_transmitted")]
    public long ServerTransmitted { get; set; }

    /// <summary>
    /// Gets or sets the roles whose streams are ending — <c>player</c>, <c>artwork</c>,
    /// <c>visualizer</c>, or an application-specific role (a name starting with <c>_</c>).
    /// Null when the server omitted the member, which ends every active stream.
    /// </summary>
    [JsonPropertyName("roles")]
    public List<string>? Roles { get; set; }
}
