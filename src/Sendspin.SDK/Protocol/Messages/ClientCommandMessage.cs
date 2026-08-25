using System.Text.Json.Serialization;

namespace Sendspin.SDK.Protocol.Messages;

/// <summary>
/// Command message sent from client to control playback.
/// Uses the envelope format: { "type": "client/command", "payload": { "controller": { ... } } }
/// </summary>
public sealed class ClientCommandMessage : IMessageWithPayload<ClientCommandPayload>
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.ClientCommand;

    [JsonPropertyName("payload")]
    required public ClientCommandPayload Payload { get; init; }

    /// <summary>
    /// Creates a command message with the specified command.
    /// </summary>
    /// <param name="command">Controller command to send (see <see cref="Commands"/>).</param>
    /// <param name="volume">Volume level (0-100), only for the 'volume' command.</param>
    /// <param name="mute">Mute state, only for the 'mute' command.</param>
    /// <param name="positionMs">Absolute position in milliseconds, only for the 'seek' command.</param>
    /// <param name="offsetMs">Signed offset in milliseconds, only for the 'seek_relative' command.</param>
    public static ClientCommandMessage Create(
        string command, int? volume = null, bool? mute = null, int? positionMs = null, int? offsetMs = null)
    {
        return new ClientCommandMessage
        {
            Payload = new ClientCommandPayload
            {
                Controller = new ControllerCommand
                {
                    Command = command,
                    Volume = volume,
                    Mute = mute,
                    PositionMs = positionMs,
                    OffsetMs = offsetMs
                }
            }
        };
    }
}

/// <summary>
/// Payload for client/command message.
/// </summary>
public sealed class ClientCommandPayload
{
    /// <summary>
    /// Controller commands for playback control.
    /// </summary>
    [JsonPropertyName("controller")]
    required public ControllerCommand Controller { get; init; }
}

/// <summary>
/// Controller command details.
/// </summary>
public sealed class ControllerCommand
{
    /// <summary>
    /// Command to execute (e.g., "play", "pause", "next", "previous").
    /// </summary>
    [JsonPropertyName("command")]
    required public string Command { get; init; }

    /// <summary>
    /// Volume level (0-100), only used when command is "volume".
    /// </summary>
    [JsonPropertyName("volume")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Volume { get; init; }

    /// <summary>
    /// Mute state, only used when command is "mute".
    /// </summary>
    [JsonPropertyName("mute")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Mute { get; init; }

    /// <summary>
    /// Absolute playback position in milliseconds, only used when command is "seek".
    /// The server ignores the command when this falls outside 0 to
    /// <see cref="ControllerState.SeekMaxMs"/>.
    /// </summary>
    [JsonPropertyName("position_ms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PositionMs { get; init; }

    /// <summary>
    /// Signed offset in milliseconds from the current position (positive forward, negative
    /// backward), only used when command is "seek_relative". The server clamps the result to the
    /// seekable range.
    /// </summary>
    [JsonPropertyName("offset_ms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OffsetMs { get; init; }
}

/// <summary>
/// Common command identifiers per the Sendspin spec.
/// </summary>
public static class Commands
{
    public const string Play = "play";
    public const string Pause = "pause";
    public const string Stop = "stop";
    public const string Next = "next";
    public const string Previous = "previous";
    public const string Volume = "volume";
    public const string Mute = "mute";
    public const string Shuffle = "shuffle";
    public const string Unshuffle = "unshuffle";
    public const string RepeatOff = "repeat_off";
    public const string RepeatOne = "repeat_one";
    public const string RepeatAll = "repeat_all";
    public const string Switch = "switch";
    public const string Seek = "seek";
    public const string SeekRelative = "seek_relative";

    /// <summary>
    /// Player command: set the player's static delay (server/command player object).
    /// </summary>
    public const string SetStaticDelay = "set_static_delay";

    /// <summary>
    /// Post-rename spelling of <see cref="SetStaticDelay"/>: spec 168a677 (spec PR #164) renamed
    /// the command to <c>set_output_delay</c> and its payload field to <c>output_delay_ms</c>,
    /// with no alias for the old names.
    /// </summary>
    /// <remarks>
    /// Accepted inbound only. No server has adopted the rename yet — aiosendspin still sends
    /// <c>set_static_delay</c> — so what this SDK puts on the wire is unchanged: the player's
    /// advertised <c>supported_commands</c> entry stays <see cref="SetStaticDelay"/> and
    /// client/state still reports <c>static_delay_ms</c>. Switch the outbound names only once
    /// servers have adopted spec PR #164.
    /// </remarks>
    public const string SetOutputDelay = "set_output_delay";
}
