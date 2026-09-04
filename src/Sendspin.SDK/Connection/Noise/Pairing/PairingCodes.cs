using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Sendspin.SDK.Connection.Noise.Pairing;

/// <summary>
/// Sendspin pairing code constructions on top of <see cref="CPace"/>, per the spec's
/// Pairing section: pairing code derivation, commit/reveal binding, the CPace session id, and
/// PSK wrapping.
/// </summary>
internal static class PairingCodes
{
    /// <summary>CPace associated data for the server (role A).</summary>
    internal static readonly byte[] AdServer = "server"u8.ToArray();

    /// <summary>CPace associated data for the client (role B).</summary>
    internal static readonly byte[] AdClient = "client"u8.ToArray();

    /// <summary>
    /// The CPace sid: <c>"sendspin-pair-pake-v1" || h || counter</c>, where h is the
    /// Noise handshake hash and counter the number of pairing server/activate messages
    /// since the last Noise handshake (big-endian uint32).
    /// </summary>
    internal static byte[] BuildSid(ReadOnlySpan<byte> handshakeHash, uint pairingCounter)
    {
        byte[] sid = new byte[21 + handshakeHash.Length + 4];
        Encoding.ASCII.GetBytes("sendspin-pair-pake-v1", sid);
        handshakeHash.CopyTo(sid.AsSpan(21));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(sid.AsSpan(21 + handshakeHash.Length), pairingCounter);
        return sid;
    }

    /// <summary>The dynamic-pairing code commitment: <c>SHA-256("sendspin-pair-commit-v1" || nonce_B)</c>.</summary>
    internal static byte[] CommitB(ReadOnlySpan<byte> nonceB)
    {
        byte[] input = new byte[23 + nonceB.Length];
        Encoding.ASCII.GetBytes("sendspin-pair-commit-v1", input);
        nonceB.CopyTo(input.AsSpan(23));
        return SHA256.HashData(input);
    }

    /// <summary>
    /// The number of decimal digits in a dynamic pairing code in the <c>digits</c> emission
    /// format. Fixed by the spec: the negotiable <c>pin_length</c> is gone, so there is no
    /// per-activation length any more.
    /// </summary>
    internal const int DynamicPairingCodeLength = 6;

    /// <summary>
    /// Derives the dynamic pairing code: <c>SHA-256("sendspin-pairing-code-derive-v1" || h ||
    /// nonce_A || nonce_B)</c> as an unsigned big-endian integer mod 10^L, zero-padded to L digits.
    /// </summary>
    internal static string DerivePairingCode(
        ReadOnlySpan<byte> handshakeHash, ReadOnlySpan<byte> nonceA, ReadOnlySpan<byte> nonceB, int length)
    {
        const int LabelLength = 31; // "sendspin-pairing-code-derive-v1"
        byte[] input = new byte[LabelLength + handshakeHash.Length + nonceA.Length + nonceB.Length];
        Encoding.ASCII.GetBytes("sendspin-pairing-code-derive-v1", input);
        handshakeHash.CopyTo(input.AsSpan(LabelLength));
        nonceA.CopyTo(input.AsSpan(LabelLength + handshakeHash.Length));
        nonceB.CopyTo(input.AsSpan(LabelLength + handshakeHash.Length + nonceA.Length));

        var digest = new BigInteger(SHA256.HashData(input), isUnsigned: true, isBigEndian: true);
        BigInteger pin = digest % BigInteger.Pow(10, length);
        return pin.ToString().PadLeft(length, '0');
    }

    /// <summary>
    /// Seals the 32-byte PSK under <c>K_wrap = SHA-256("sendspin-pair-psk-wrap-v1" ||
    /// sid || ISK)</c> with the session suite's AEAD, a 12-byte zero nonce, and empty
    /// associated data. Returns the 48-byte ciphertext-plus-tag.
    /// </summary>
    internal static byte[] WrapPsk(byte[] sid, byte[] isk, byte[] psk, NoiseCipherSuite suite)
    {
        byte[] kWrap = SHA256.HashData(
            [.. "sendspin-pair-psk-wrap-v1"u8.ToArray(), .. sid, .. isk]);
        byte[] nonce = new byte[12];
        byte[] ciphertext = new byte[psk.Length];
        byte[] tag = new byte[16];
        if (suite == NoiseCipherSuite.AesGcm)
        {
            using var aes = new AesGcm(kWrap, 16);
            aes.Encrypt(nonce, psk, ciphertext, tag);
        }
        else
        {
            using var chacha = new ChaCha20Poly1305(kWrap);
            chacha.Encrypt(nonce, psk, ciphertext, tag);
        }

        return [.. ciphertext, .. tag];
    }
}

/// <summary>
/// Persists per-method pairing code failure counters (spec: escalates to gesture-gating at 10,
/// counters survive reboots, not partitioned by server).
/// </summary>
public interface IPairingCodeLockoutStore
{
    /// <summary>The failure counter for a method ('static_pairing_code' or 'dynamic_pairing_code').</summary>
    int GetFailures(string method);

    /// <summary>Sets the failure counter for a method.</summary>
    void SetFailures(string method, int failures);
}

/// <summary>In-memory lockout store (counters do not survive restarts; supply a persistent implementation in production).</summary>
public sealed class InMemoryPairingCodeLockoutStore : IPairingCodeLockoutStore
{
    private readonly Dictionary<string, int> _failures = new();

    /// <inheritdoc/>
    public int GetFailures(string method) => _failures.GetValueOrDefault(method);

    /// <inheritdoc/>
    public void SetFailures(string method, int failures) => _failures[method] = failures;
}
