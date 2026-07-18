using System.Numerics;

namespace Sendspin.SDK.Connection.Noise.Pairing;

/// <summary>
/// RFC 7748 X25519 scalar multiplication over Curve25519's Montgomery u-coordinates.
/// </summary>
/// <remarks>
/// Pure managed implementation (BigInteger Montgomery ladder). NOT constant-time — the
/// PIN pairing flows use it for one-shot interactive PAKE runs where the secrets are
/// per-session ephemerals; do not reuse it for long-lived static keys.
/// </remarks>
internal static class X25519
{
    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    private static readonly BigInteger A24 = 121665;

    internal const int KeySize = 32;

    /// <summary>Decodes a little-endian field element, masking the unused top bit.</summary>
    internal static BigInteger DecodeU(ReadOnlySpan<byte> u)
    {
        Span<byte> copy = stackalloc byte[KeySize];
        u.CopyTo(copy);
        copy[31] &= 0x7F;
        return new BigInteger(copy, isUnsigned: true, isBigEndian: false);
    }

    /// <summary>Encodes a field element little-endian into 32 bytes.</summary>
    internal static byte[] EncodeU(BigInteger x)
    {
        var bytes = x.ToByteArray(isUnsigned: true, isBigEndian: false);
        Array.Resize(ref bytes, KeySize);
        return bytes;
    }

    /// <summary>
    /// X25519(k, u): clamps the 32-byte scalar per RFC 7748 and runs the Montgomery
    /// ladder. Returns the 32-byte shared u-coordinate.
    /// </summary>
    internal static byte[] ScalarMult(ReadOnlySpan<byte> scalar, ReadOnlySpan<byte> u)
    {
        if (scalar.Length != KeySize || u.Length != KeySize)
            throw new ArgumentException("scalar and point must be 32 bytes");

        Span<byte> k = stackalloc byte[KeySize];
        scalar.CopyTo(k);
        k[0] &= 248;
        k[31] &= 127;
        k[31] |= 64;
        var kInt = new BigInteger(k, isUnsigned: true, isBigEndian: false);

        BigInteger x1 = DecodeU(u);
        BigInteger x2 = 1, z2 = 0, x3 = x1, z3 = 1;
        int swap = 0;

        for (int t = 254; t >= 0; t--)
        {
            int kt = (int)((kInt >> t) & 1);
            swap ^= kt;
            if (swap == 1)
            {
                (x2, x3) = (x3, x2);
                (z2, z3) = (z3, z2);
            }

            swap = kt;

            BigInteger a = Mod(x2 + z2);
            BigInteger aa = Mod(a * a);
            BigInteger b = Mod(x2 - z2);
            BigInteger bb = Mod(b * b);
            BigInteger e = Mod(aa - bb);
            BigInteger c = Mod(x3 + z3);
            BigInteger d = Mod(x3 - z3);
            BigInteger da = Mod(d * a);
            BigInteger cb = Mod(c * b);
            x3 = Mod((da + cb) * (da + cb));
            z3 = Mod(x1 * (da - cb) * (da - cb));
            x2 = Mod(aa * bb);
            z2 = Mod(e * (aa + A24 * e));
        }

        if (swap == 1)
        {
            (x2, _) = (x3, x2);
            (z2, _) = (z3, z2);
        }

        return EncodeU(Mod(x2 * BigInteger.ModPow(z2, P - 2, P)));
    }

    /// <summary>
    /// Scalar mult with the RFC 7748 all-zero output check: returns null when the
    /// result encodes the identity (a low-order input point).
    /// </summary>
    internal static byte[]? ScalarMultVerified(ReadOnlySpan<byte> scalar, ReadOnlySpan<byte> point)
    {
        byte[] shared = ScalarMult(scalar, point);
        return shared.All(b => b == 0) ? null : shared;
    }

    internal static BigInteger Mod(BigInteger x)
    {
        BigInteger r = x % P;
        return r.Sign < 0 ? r + P : r;
    }

    internal static BigInteger FieldPrime => P;
}
