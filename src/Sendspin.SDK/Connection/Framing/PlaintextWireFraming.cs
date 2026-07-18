namespace Sendspin.SDK.Connection.Framing;

/// <summary>
/// Passthrough framing for the unencrypted Sendspin protocol: JSON messages travel as
/// WebSocket text frames and binary messages as binary frames, unchanged.
/// </summary>
public sealed class PlaintextWireFraming : IWireFraming
{
    /// <summary>Shared stateless instance.</summary>
    public static PlaintextWireFraming Instance { get; } = new();

    /// <inheritdoc/>
    public bool IsTransportReady => true;

    /// <inheritdoc/>
    public IReadOnlyList<WireFrame> Start() => Array.Empty<WireFrame>();

    /// <inheritdoc/>
    public IEnumerable<WireFrame> EncodeText(string json)
    {
        yield return WireFrame.FromText(json);
    }

    /// <inheritdoc/>
    public IEnumerable<WireFrame> EncodeBinary(ReadOnlyMemory<byte> data)
    {
        yield return WireFrame.FromBinary(data);
    }

    /// <inheritdoc/>
    public InboundFrameResult ProcessInbound(WireFrame frame) =>
        frame.Kind == WireFrameKind.Text
            ? InboundFrameResult.ForText(frame.PayloadAsText())
            : InboundFrameResult.ForBinary(frame.Payload);

    /// <inheritdoc/>
    public void Reset()
    {
    }
}
