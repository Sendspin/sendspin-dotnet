namespace Sendspin.SDK.Connection.Framing;

/// <summary>
/// Outcome of feeding one received wire frame through <see cref="IWireFraming"/>:
/// at most one application frame to surface, plus any wire frames the framing layer
/// needs transmitted back immediately (e.g. handshake responses).
/// </summary>
public readonly struct InboundFrameResult
{
    /// <summary>Application JSON message to surface, if any.</summary>
    public string? Text { get; init; }

    /// <summary>Application binary message to surface, if any.</summary>
    public ReadOnlyMemory<byte>? Binary { get; init; }

    /// <summary>Wire frames to transmit in response before processing further input, if any.</summary>
    public IReadOnlyList<WireFrame>? Replies { get; init; }

    /// <summary>
    /// When true, the framing holds a reply that must not be produced on the receive
    /// path: the connection must call <see cref="IWireFraming.EncodeDeferredReply"/>
    /// on its send path and transmit the returned frames within the same send-lock
    /// acquisition. Encoding the reply and committing the framing's pending key swap
    /// happen inside that single call, so a sent reply cannot leave the swap
    /// uncommitted. Used for the Noise re-handshake reply, which must travel under
    /// the retiring keys and precede every frame encrypted under the new ones.
    /// </summary>
    public bool HasDeferredReply { get; init; }

    /// <summary>
    /// When set, the framing layer hit an unrecoverable protocol/crypto failure. The
    /// connection must close the socket without sending any application-level error
    /// (per the spec's handshake failure-handling rules). The value is a log-only reason.
    /// </summary>
    public string? FatalReason { get; init; }

    /// <summary>A result surfacing an application JSON message.</summary>
    public static InboundFrameResult ForText(string text) => new() { Text = text };

    /// <summary>A result surfacing an application binary message.</summary>
    public static InboundFrameResult ForBinary(ReadOnlyMemory<byte> data) => new() { Binary = data };

    /// <summary>A result surfacing nothing (the frame was fully consumed by the framing layer).</summary>
    public static InboundFrameResult None => default;

    /// <summary>
    /// A result whose reply must be encoded and committed on the connection's send path
    /// via <see cref="IWireFraming.EncodeDeferredReply"/>.
    /// </summary>
    public static InboundFrameResult ForDeferredReply() => new() { HasDeferredReply = true };

    /// <summary>A result signaling an unrecoverable framing failure.</summary>
    public static InboundFrameResult Fatal(string reason) => new() { FatalReason = reason };
}
