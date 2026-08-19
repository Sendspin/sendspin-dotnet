// <copyright file="StreamClearMessage.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using System.Text.Json.Serialization;

namespace Sendspin.SDK.Protocol.Messages;

/// <summary>
/// Message from server indicating audio buffers should be cleared.
/// Uses envelope format: { "type": "stream/clear", "payload": { ... } }.
/// </summary>
public sealed class StreamClearMessage : IMessageWithPayload<StreamClearPayload>
{
    /// <inheritdoc/>
    [JsonPropertyName("type")]
    public string Type => MessageTypes.StreamClear;

    /// <inheritdoc/>
    [JsonPropertyName("payload")]
    public StreamClearPayload Payload { get; set; } = new();

    // Convenience accessor
    [JsonIgnore]
    public List<string>? Roles => Payload.Roles;
}

/// <summary>
/// Payload for stream/clear message.
/// </summary>
public sealed class StreamClearPayload
{
    /// <summary>
    /// Gets or sets the server's monotonic clock timestamp, in microseconds, at which it
    /// transmitted this message.
    /// </summary>
    [JsonPropertyName("server_transmitted")]
    public long ServerTransmitted { get; set; }

    /// <summary>
    /// Gets or sets the roles whose buffers are to be cleared — <c>player</c>,
    /// <c>visualizer</c>, or an application-specific role (a name starting with <c>_</c>).
    /// Null when the server omitted the member, which clears both stream roles.
    /// </summary>
    [JsonPropertyName("roles")]
    public List<string>? Roles { get; set; }
}
