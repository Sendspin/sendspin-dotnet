using System.Security.Cryptography;
using System.Text;

namespace Sendspin.SDK.Connection.Noise;

/// <summary>
/// Wire constants for the Sendspin Noise transport, per the protocol spec's
/// Encryption and Pre-Shared Key sections.
/// </summary>
internal static class NoiseConstants
{
    /// <summary>Core protocol version carried in <c>client/init</c>.</summary>
    public const int ProtocolVersion = 1;

    /// <summary>X25519 key size in bytes.</summary>
    public const int KeySize = 32;

    /// <summary>PSK size in bytes.</summary>
    public const int PskSize = 32;

    /// <summary>
    /// Max plaintext bytes per Noise transport message: the Noise 65535-byte ceiling
    /// minus the 16-byte AEAD tag. Includes the message type byte.
    /// </summary>
    public const int MaxTransportPlaintext = 65535 - 16;

    /// <summary>Binary message type for a JSON message body (UTF-8).</summary>
    public const byte MessageTypeJsonBody = 0;

    /// <summary>
    /// Binary message ID for a fragment frame (spec messaging.md). A fragmented message is a run
    /// of these: the first frame is <c>[1][flags][orig_type][data]</c> and each later frame
    /// <c>[1][flags][data]</c>. IDs 2 and 3 (the pre-1.0 fragment-more / fragment-end types) are
    /// reserved and no longer sent or accepted.
    /// </summary>
    public const byte MessageTypeFragment = 1;

    /// <summary>Fragment flags bit 1: set on the first fragment of a message.</summary>
    public const byte FragmentFlagFirst = 0b10;

    /// <summary>Fragment flags bit 0: set on the last fragment of a message.</summary>
    public const byte FragmentFlagLast = 0b01;

    /// <summary>Fragment flags bits 2-7: reserved and MUST be zero.</summary>
    public const byte FragmentFlagsReserved = 0b1111_1100;

    /// <summary>
    /// Bound on a reassembled fragmented message, protecting against a peer
    /// streaming endless fragments.
    /// </summary>
    public const int MaxReassembledMessageBytes = 64 * 1024 * 1024;

    /// <summary>
    /// Tighter bound on reassembly before the framing layer has surfaced its first
    /// application message. The only legitimate content that early is <c>server/hello</c>,
    /// so this is generous while limiting what an unauthorised peer can make us buffer.
    /// The spec sets no maximum; this is local hardening.
    /// </summary>
    public const int MaxReassembledMessageBytesBeforeFirstMessage = 128 * 1024;

    /// <summary>
    /// The published Sentinel PSK: <c>SHA-256("sendspin-sentinel-psk-v1")</c>.
    /// Used whenever no pairing record applies; public, so it authenticates nothing.
    /// </summary>
    public static ReadOnlySpan<byte> SentinelPsk => SentinelPskBytes;

    private static readonly byte[] SentinelPskBytes =
        SHA256.HashData(Encoding.ASCII.GetBytes("sendspin-sentinel-psk-v1"));

    /// <summary>The Sentinel PSK's psk_id (a published constant).</summary>
    public static string SentinelPskId { get; } = DerivePskId(SentinelPskBytes);

    /// <summary>
    /// Derives a psk_id per spec: <c>base64url(SHA-256("sendspin-psk-id-v1" || PSK))</c>.
    /// </summary>
    public static string DerivePskId(ReadOnlySpan<byte> psk)
    {
        Span<byte> input = stackalloc byte[18 + PskSize];
        Encoding.ASCII.GetBytes("sendspin-psk-id-v1", input);
        psk.CopyTo(input[18..]);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        return Base64UrlText.Encode(hash);
    }
}
