namespace Sendspin.SDK.Client;

/// <summary>
/// Event args for sync offset applied from GroupSync calibration.
/// </summary>
public sealed class SyncOffsetEventArgs : EventArgs
{
    /// <summary>
    /// The player ID that the offset was applied to.
    /// </summary>
    public string PlayerId { get; }

    /// <summary>
    /// The offset in milliseconds that was applied to the player's
    /// <see cref="Sendspin.SDK.Synchronization.IClockSynchronizer.StaticDelayMs"/>. Per the Sendspin
    /// protocol spec the value is subtracted from server timestamps when scheduling playback:
    /// positive values advance playback (compensating for downstream hardware delay), negative
    /// values delay it.
    /// </summary>
    public double OffsetMs { get; }

    /// <summary>
    /// The source of the calibration (e.g., "groupsync", "manual").
    /// </summary>
    public string? Source { get; }

    public SyncOffsetEventArgs(string playerId, double offsetMs, string? source = null)
    {
        PlayerId = playerId;
        OffsetMs = offsetMs;
        Source = source;
    }
}
