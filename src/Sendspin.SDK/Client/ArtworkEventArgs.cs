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
    /// The image as the server sent it: by the spec, encoded in the format declared for the
    /// channel (JPEG or PNG) at the declared size.
    /// </summary>
    /// <remarks>
    /// The SDK reassembles these bytes and does not inspect them, so treat them as input from the
    /// peer: they may be up to 16 MiB, in another format, or not an image at all. Decode them
    /// with a decoder that fails cleanly on bad input, and bound the decoded dimensions if the
    /// declared size matters to you.
    /// </remarks>
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
/// A single artwork channel was cleared: either by an empty image (an announce with a
/// <c>total_size</c> of zero), or by a <c>stream/end</c> that ends the artwork role while the
/// channel still shows an image.
/// </summary>
public sealed class ArtworkClearedEventArgs : EventArgs
{
    /// <summary>
    /// Artwork channel (0-3) that was cleared.
    /// </summary>
    public int Channel { get; }

    /// <summary>
    /// Server clock timestamp in microseconds carried by the clearing announce. A clear raised for a
    /// <c>stream/end</c> has no message of its own, and carries the timestamp of the image it
    /// clears instead.
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
