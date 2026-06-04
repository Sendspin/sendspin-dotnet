namespace Sendspin.SDK.Client;

/// <summary>
/// Artwork image received on a specific channel (binary message types 8-11 for channels 0-3).
/// </summary>
public sealed class ArtworkReceivedEventArgs : EventArgs
{
    /// <summary>
    /// Artwork channel (0-3) this image is for, per the binary message type.
    /// </summary>
    public int Channel { get; }

    /// <summary>
    /// Server clock timestamp in microseconds for when this artwork should be displayed.
    /// </summary>
    public long Timestamp { get; }

    /// <summary>
    /// Encoded image bytes (JPEG/PNG/BMP).
    /// </summary>
    public byte[] ImageData { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ArtworkReceivedEventArgs"/> class.
    /// </summary>
    public ArtworkReceivedEventArgs(int channel, long timestamp, byte[] imageData)
    {
        Channel = channel;
        Timestamp = timestamp;
        ImageData = imageData;
    }
}

/// <summary>
/// A single artwork channel was cleared (an empty binary artwork message: type byte + timestamp,
/// no image data).
/// </summary>
public sealed class ArtworkClearedEventArgs : EventArgs
{
    /// <summary>
    /// Artwork channel (0-3) that was cleared.
    /// </summary>
    public int Channel { get; }

    /// <summary>
    /// Server clock timestamp in microseconds carried by the clear message.
    /// </summary>
    public long Timestamp { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ArtworkClearedEventArgs"/> class.
    /// </summary>
    public ArtworkClearedEventArgs(int channel, long timestamp)
    {
        Channel = channel;
        Timestamp = timestamp;
    }
}
