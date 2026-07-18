using System.Text;

namespace Sendspin.SDK.Connection.Framing;

/// <summary>
/// The WebSocket frame kind a wire frame travels in.
/// </summary>
public enum WireFrameKind
{
    /// <summary>WebSocket text frame (UTF-8 payload).</summary>
    Text,

    /// <summary>WebSocket binary frame.</summary>
    Binary,
}

/// <summary>
/// A single WebSocket frame as it appears on the wire, independent of the
/// application-level meaning of its payload.
/// </summary>
public readonly struct WireFrame
{
    private readonly string? _text;

    /// <summary>The WebSocket frame kind this frame travels in.</summary>
    public WireFrameKind Kind { get; }

    /// <summary>The raw frame payload bytes.</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>Creates a wire frame from raw payload bytes.</summary>
    public WireFrame(WireFrameKind kind, ReadOnlyMemory<byte> payload)
    {
        Kind = kind;
        Payload = payload;
        _text = null;
    }

    private WireFrame(string text)
    {
        Kind = WireFrameKind.Text;
        Payload = Encoding.UTF8.GetBytes(text);
        _text = text;
    }

    /// <summary>Creates a text wire frame from a string, caching it for <see cref="PayloadAsText"/>.</summary>
    public static WireFrame FromText(string text) => new(text);

    /// <summary>Creates a binary wire frame.</summary>
    public static WireFrame FromBinary(ReadOnlyMemory<byte> payload) => new(WireFrameKind.Binary, payload);

    /// <summary>The payload decoded as UTF-8 text.</summary>
    public string PayloadAsText() => _text ?? Encoding.UTF8.GetString(Payload.Span);
}
