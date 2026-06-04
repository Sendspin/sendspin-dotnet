using System.Text.Json.Serialization;

namespace Sendspin.SDK.Protocol.Messages;

/// <summary>
/// Command message from server to control player state.
/// The server sends this to tell players what volume/mute to apply locally.
/// </summary>
public sealed class ServerCommandMessage : IMessageWithPayload<ServerCommandPayload>
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.ServerCommand;

    [JsonPropertyName("payload")]
    required public ServerCommandPayload Payload { get; init; }
}

/// <summary>
/// Payload for server/command message.
/// </summary>
public sealed class ServerCommandPayload
{
    /// <summary>
    /// Player command details (volume, mute).
    /// </summary>
    [JsonPropertyName("player")]
    public PlayerCommand? Player { get; init; }
}

/// <summary>
/// Player command details from server.
/// Null properties indicate the server is not requesting a change to that setting.
/// </summary>
public sealed class PlayerCommand
{
    /// <summary>
    /// The command type: "volume", "mute", or "set_static_delay".
    /// </summary>
    [JsonPropertyName("command")]
    public string? Command { get; init; }

    /// <summary>
    /// Volume level (0-100). Null if volume is not being changed.
    /// </summary>
    [JsonPropertyName("volume")]
    public int? Volume { get; init; }

    /// <summary>
    /// Mute state. Null if mute is not being changed.
    /// </summary>
    [JsonPropertyName("mute")]
    public bool? Mute { get; init; }

    /// <summary>
    /// Static delay in milliseconds (0-5000). Only set when <see cref="Command"/> is
    /// "set_static_delay". Null otherwise.
    /// </summary>
    [JsonPropertyName("static_delay_ms")]
    public int? StaticDelayMs { get; init; }
}
