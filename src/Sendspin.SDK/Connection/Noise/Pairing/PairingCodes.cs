using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
    /// The CPace sid: <c>"sendspin-pair-pake-v1" || h || u32be(pairing_index) || u32be(round)</c>,
    /// where h is the Noise handshake hash, pairing_index the number of pairing server/activate
    /// messages since the last Noise handshake, and round the pairing round — 1 for the static flow
    /// and for the dynamic flow's only round until <c>client/pair-retry</c> lands. Both counters are
    /// big-endian uint32.
    /// </summary>
    internal static byte[] BuildSid(ReadOnlySpan<byte> handshakeHash, uint pairingIndex, uint round)
    {
        byte[] sid = new byte[21 + handshakeHash.Length + 4 + 4];
        Encoding.ASCII.GetBytes("sendspin-pair-pake-v1", sid);
        handshakeHash.CopyTo(sid.AsSpan(21));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(sid.AsSpan(21 + handshakeHash.Length), pairingIndex);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(sid.AsSpan(21 + handshakeHash.Length + 4), round);
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
        return Seal(kWrap, psk, suite);
    }

    /// <summary>
    /// Seals the 32-byte nonce_B under <c>K_wrap = SHA-256("sendspin-pair-nonce-wrap-v1" ||
    /// sid || ISK)</c> with the session suite's AEAD, a 12-byte zero nonce, and empty
    /// associated data. Returns the 48-byte ciphertext-plus-tag carried as
    /// <c>wrapped_nonce_B</c> in <c>client/pair-confirm</c> (dynamic pairing code only).
    /// </summary>
    internal static byte[] WrapNonceB(byte[] sid, byte[] isk, byte[] nonceB, NoiseCipherSuite suite)
    {
        byte[] kWrap = SHA256.HashData(
            [.. "sendspin-pair-nonce-wrap-v1"u8.ToArray(), .. sid, .. isk]);
        return Seal(kWrap, nonceB, suite);
    }

    /// <summary>
    /// The suite's AEAD over <paramref name="plaintext"/> with a 12-byte zero nonce and empty
    /// associated data, returning ciphertext-plus-tag.
    /// </summary>
    /// <remarks>
    /// Sealed with libsodium, the backend the session itself runs on and the one
    /// <see cref="NoiseCipherSuiteExtensions.IsSupported"/> probes, so a suite that got as far as
    /// a session can always wrap. The BCL AEADs cannot promise that:
    /// <c>System.Security.Cryptography.ChaCha20Poly1305</c> does not exist on Windows before
    /// build 20142, which is every Windows 10, where libsodium selects ChaChaPoly happily (#315).
    /// </remarks>
    private static byte[] Seal(byte[] key, byte[] plaintext, NoiseCipherSuite suite)
    {
        // Idempotent. A session will already have run it through Noise.NET, but nothing here
        // should depend on that ordering: AES-GCM is unusable until it has run.
        if (Sodium.sodium_init() < 0)
            throw new CryptographicException("Failed to initialize libsodium.");

        byte[] nonce = new byte[12];
        byte[] sealedBytes = new byte[plaintext.Length + 16];
        int rc = suite == NoiseCipherSuite.AesGcm
            ? Sodium.crypto_aead_aes256gcm_encrypt(
                sealedBytes, out _, plaintext, (ulong)plaintext.Length, null, 0, IntPtr.Zero, nonce, key)
            : Sodium.crypto_aead_chacha20poly1305_ietf_encrypt(
                sealedBytes, out _, plaintext, (ulong)plaintext.Length, null, 0, IntPtr.Zero, nonce, key);
        if (rc != 0)
            throw new CryptographicException("Pairing wrap failed.");

        return sealedBytes;
    }

    /// <summary>
    /// The libsodium entry points the wrap needs. Noise.NET binds the same functions but
    /// keeps them internal; the native library is the one the SDK already ships for it.
    /// </summary>
    private static class Sodium
    {
        [DllImport("libsodium", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sodium_init();

        [DllImport("libsodium", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int crypto_aead_chacha20poly1305_ietf_encrypt(
            byte[] c, out ulong clen, byte[] m, ulong mlen, byte[]? ad, ulong adlen, IntPtr nsec, byte[] npub, byte[] k);

        [DllImport("libsodium", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int crypto_aead_aes256gcm_encrypt(
            byte[] c, out ulong clen, byte[] m, ulong mlen, byte[]? ad, ulong adlen, IntPtr nsec, byte[] npub, byte[] k);
    }
}

/// <summary>
/// Persists per-method pairing code failure counters (escalates to gesture-gating at 10,
/// counters survive reboots, not partitioned by server). For 'dynamic_pairing_code' the counter
/// holds rounds since the last verified one, counted from the moment a round's code is emitted.
/// A host shares one store across all of its connections, each counting on its own receive
/// thread, so an implementation must be safe for concurrent use. Both implementations shipped
/// in this package are.
/// </summary>
public interface IPairingCodeLockoutStore
{
    /// <summary>The failure counter for a method ('static_pairing_code' or 'dynamic_pairing_code').</summary>
    int GetFailures(string method);

    /// <summary>Sets the failure counter for a method.</summary>
    void SetFailures(string method, int failures);
}

internal static class PairingCodeLockoutStoreSynchronization
{
    private static readonly ConditionalWeakTable<IPairingCodeLockoutStore, object> Gates = new();

    /// <summary>The gate every client sharing <paramref name="store"/> counts a failure under.</summary>
    internal static object For(IPairingCodeLockoutStore store) =>
        Gates.GetValue(store, static _ => new object());
}

/// <summary>In-memory lockout store (counters do not survive restarts; supply a persistent implementation in production).</summary>
public sealed class InMemoryPairingCodeLockoutStore : IPairingCodeLockoutStore
{
    private readonly Dictionary<string, int> _failures = new();
    private readonly object _lock = new();

    /// <inheritdoc/>
    public int GetFailures(string method)
    {
        lock (_lock)
            return _failures.GetValueOrDefault(method);
    }

    /// <inheritdoc/>
    public void SetFailures(string method, int failures)
    {
        lock (_lock)
            _failures[method] = failures;
    }
}
