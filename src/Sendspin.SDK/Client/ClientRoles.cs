namespace Sendspin.SDK.Client;

/// <summary>
/// Client role identifiers used in the Sendspin protocol.
/// Roles define what capabilities a client has.
/// </summary>
public static class ClientRoles
{
    /// <summary>
    /// Player role - outputs synchronized audio.
    /// </summary>
    public const string Player = "player@v1";

    /// <summary>
    /// Controller role - can control group playback (play, pause, volume, etc.).
    /// </summary>
    public const string Controller = "controller@v1";

    /// <summary>
    /// Metadata role - receives track metadata updates.
    /// </summary>
    public const string Metadata = "metadata@v1";

    /// <summary>
    /// Artwork role - receives album artwork.
    /// </summary>
    public const string Artwork = "artwork@v1";

    /// <summary>
    /// Visualizer role - receives audio visualization data.
    /// </summary>
    public const string Visualizer = "visualizer@v1";

    /// <summary>
    /// Color role - receives the server-computed color palette derived from the playing
    /// audio, for ambient lighting and UI theming.
    /// </summary>
    public const string Color = "color@v1";

    /// <summary>
    /// Source role - captures audio from an input device and sends it to the server.
    /// </summary>
    public const string Source = "source@v1";
}

/// <summary>
/// Commands that can be sent to control playback.
/// </summary>
public enum PlayerCommand
{
    Play,
    Pause,
    Stop,
    Next,
    Previous,
    Shuffle,
    Repeat
}

/// <summary>
/// Volume adjustment modes.
/// </summary>
public enum VolumeMode
{
    /// <summary>
    /// Set absolute volume level.
    /// </summary>
    Absolute,

    /// <summary>
    /// Adjust volume by delta.
    /// </summary>
    Relative
}
