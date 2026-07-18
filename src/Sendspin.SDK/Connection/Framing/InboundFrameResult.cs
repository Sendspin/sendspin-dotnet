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

    /// <summary>A result signaling an unrecoverable framing failure.</summary>
    public static InboundFrameResult Fatal(string reason) => new() { FatalReason = reason };
}
