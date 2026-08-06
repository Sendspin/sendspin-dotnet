namespace Sendspin.SDK.Client;

/// <summary>
/// How this client's <c>source@v1</c> role behaves. Only meaningful when the role is
/// advertised and a capture device is configured.
/// </summary>
public sealed class SourceSupport
{
    /// <summary>
    /// Whether this client reports line-sense signal presence, advertised in
    /// <c>source@v1_support.features</c> and reported through <c>client/state</c>.
    /// </summary>
    public bool LineSense { get; init; }

    /// <summary>
    /// Codec to encode captured audio as. Null keeps the capture device's own codec, which is
    /// the previous behaviour. Set this when the device captures PCM but the stream should
    /// carry a compressed codec.
    /// </summary>
    public string? Codec { get; init; }
}
