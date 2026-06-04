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
    /// No artwork. The server sends nothing for a channel advertised with this source, letting a
    /// client disable a channel (and re-enable it later via <c>stream/request-format</c>) without
    /// reconnecting.
    /// </summary>
    public const string None = "none";
}
