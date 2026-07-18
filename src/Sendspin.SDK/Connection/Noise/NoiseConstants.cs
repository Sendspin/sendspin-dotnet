using System.Security.Cryptography;
using System.Text;

namespace Sendspin.SDK.Connection.Noise;

/// <summary>
/// Wire constants for the Sendspin Noise transport, per the protocol spec's
/// Encryption and Pre-Shared Key sections.
/// </summary>
public static class NoiseConstants
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

    /// <summary>Binary message type for a non-final fragment frame.</summary>
    public const byte MessageTypeFragmentMore = 2;

    /// <summary>Binary message type for the final fragment frame.</summary>
    public const byte MessageTypeFragmentEnd = 3;

    /// <summary>
    /// Bound on a reassembled fragmented message, protecting against a peer
    /// streaming endless fragments.
    /// </summary>
    public const int MaxReassembledMessageBytes = 64 * 1024 * 1024;

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
