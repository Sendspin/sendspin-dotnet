namespace Sendspin.SDK.Connection.Framing;

/// <summary>
/// Translates between application frames (JSON protocol messages, protocol binary
/// messages) and the WebSocket wire frames that carry them.
/// </summary>
/// <remarks>
/// This is the seam where the Sendspin encrypted transport slots in: an encrypted
/// implementation owns the cryptographic handshake (emitting its opening frames via
/// <see cref="Start"/> and responding through <see cref="InboundFrameResult.Replies"/>),
/// encrypts outbound application frames, decrypts inbound ones, and splits/reassembles
/// fragmented messages. The plaintext implementation
/// (<see cref="PlaintextWireFraming"/>) is a 1:1 passthrough.
/// Implementations need not be thread-safe: connections serialize encode calls under
/// their send lock and process inbound frames on a single receive path.
/// </remarks>
public interface IWireFraming
{
    /// <summary>
    /// Whether application frames may currently flow. Always true for plaintext framing;
    /// false for an encrypted framing until its handshake completes.
    /// </summary>
    bool IsTransportReady { get; }

    /// <summary>
    /// Wire frames to transmit immediately after the underlying socket opens
    /// (e.g. an encrypted framing's first handshake message). Empty for plaintext framing.
    /// </summary>
    IReadOnlyList<WireFrame> Start();

    /// <summary>Encodes an application JSON message into one or more wire frames.</summary>
    IEnumerable<WireFrame> EncodeText(string json);

    /// <summary>Encodes an application binary message into one or more wire frames.</summary>
    IEnumerable<WireFrame> EncodeBinary(ReadOnlyMemory<byte> data);

    /// <summary>Processes one received wire frame.</summary>
    InboundFrameResult ProcessInbound(WireFrame frame);

    /// <summary>Resets all per-connection state. Called before each (re)connect.</summary>
    void Reset();
}
