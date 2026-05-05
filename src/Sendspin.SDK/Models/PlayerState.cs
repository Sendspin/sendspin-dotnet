namespace Sendspin.SDK.Models;

/// <summary>
/// This player's own volume and mute state, distinct from the group aggregate
/// in <see cref="GroupState"/>.
/// </summary>
public sealed class PlayerState
{
    /// <summary>This player's volume (0-100). Applied to audio output.</summary>
    public int Volume { get; set; } = 100;

    /// <summary>Whether this player is muted. Applied to audio output.</summary>
    public bool Muted { get; set; }
}
