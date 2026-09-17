namespace Sendspin.SDK.Protocol.Messages;

/// <summary>
/// Artwork channel source identifiers per the Sendspin spec.
/// </summary>
public static class ArtworkSources
{
    /// <summary>Album artwork.</summary>
    public const string Album = "album";

    /// <summary>Artist artwork.</summary>
    public const string Artist = "artist";

    /// <summary>
    /// No artwork. The server sends nothing for a channel declared with this source, letting a
    /// client disable a channel (and re-enable it later through a <c>client/state</c> update)
    /// without reconnecting.
    /// </summary>
    public const string None = "none";
}
