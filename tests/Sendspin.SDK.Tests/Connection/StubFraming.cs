using Sendspin.SDK.Connection.Framing;

namespace Sendspin.SDK.Tests.Connection;

/// <summary>
/// Minimal passthrough <see cref="IWireFraming"/> test double with a settable
/// <see cref="IsTransportReady"/>. Reconnect tests that need <c>EncodeText</c>/
/// <c>EncodeBinary</c> to actually succeed (rather than throw, as a real
/// <c>NoiseWireFraming</c> would against a loopback server that never completes a
/// handshake) use this instead.
/// </summary>
internal sealed class StubFraming : IWireFraming
{
    public bool IsTransportReady { get; set; } = true;

    /// <summary>
    /// When set, every inbound frame trips the framing's fatal path with this reason —
    /// standing in for a rejected handshake (bad PSK, unsupported suite, malformed input).
    /// </summary>
    public string? FatalOnInbound { get; set; }

    public IReadOnlyList<WireFrame> Start() => Array.Empty<WireFrame>();

    public IEnumerable<WireFrame> EncodeText(string json)
    {
        yield return WireFrame.FromText(json);
    }

    public IEnumerable<WireFrame> EncodeBinary(ReadOnlyMemory<byte> data)
    {
        yield return WireFrame.FromBinary(data);
    }

    public InboundFrameResult ProcessInbound(WireFrame frame)
    {
        if (FatalOnInbound is { } fatal)
            return InboundFrameResult.Fatal(fatal);

        return frame.Kind == WireFrameKind.Text
            ? InboundFrameResult.ForText(frame.PayloadAsText())
            : InboundFrameResult.ForBinary(frame.Payload);
    }

    public void Reset()
    {
    }
}
