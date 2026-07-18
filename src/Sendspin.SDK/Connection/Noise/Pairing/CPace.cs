using System.Numerics;
using System.Security.Cryptography;

namespace Sendspin.SDK.Connection.Noise.Pairing;

/// <summary>The CPace role in initiator-responder mode: the server is A, the client is B.</summary>
internal enum CPaceRole
{
    /// <summary>Role A (the Sendspin server).</summary>
    Initiator,

    /// <summary>Role B (the Sendspin client).</summary>
    Responder,
}

/// <summary>A CPace step failed: bad peer share, tag mismatch, or out-of-order use.</summary>
internal sealed class CPaceException(string message) : Exception(message);

/// <summary>
/// One side of a CPACE-X25519-SHA512 run (draft-irtf-cfrg-cpace) with explicit mutual
/// confirmation, as used by the Sendspin PIN pairing flows. Construction verified
/// against the aiosendspin reference implementation via known-answer vectors.
/// </summary>
/// <remarks>
/// Not constant-time (see <see cref="X25519"/>); acceptable for the interactive
/// one-shot PIN flows this backs, where all secrets are per-session.
/// </remarks>
internal sealed class CPace
{
    private const int FieldBytes = 32;
    private const int Sha512BlockBytes = 128;
    private static readonly byte[] Dsi = "CPace255"u8.ToArray();
    private static readonly byte[] DsiIsk = "CPace255_ISK"u8.ToArray();
    private static readonly byte[] MacLabel = "CPaceMac"u8.ToArray();

    private static readonly BigInteger CurveA = 486662;
    private static readonly BigInteger Z = 2;
    private static readonly BigInteger Inv2 =
        BigInteger.ModPow(2, X25519.FieldPrime - 2, X25519.FieldPrime);
    private static readonly BigInteger LegendrePower = (X25519.FieldPrime - 1) / 2;

    private readonly CPaceRole _role;
    private readonly byte[] _sid;
    private readonly byte[] _ad;
    private byte[]? _scalar;
    private bool _derived;
    private byte[] _isk = [];
    private byte[] _macKey = [];
    private (byte[] Share, byte[] Ad) _sideA;
    private (byte[] Share, byte[] Ad) _sideB;

    /// <summary>This side's public share (Ya for A, Yb for B), 32 bytes.</summary>
    public byte[] PublicShare { get; }

    private CPace(CPaceRole role, byte[] sid, byte[] ad, byte[] scalar, byte[] publicShare)
    {
        _role = role;
        _sid = sid;
        _ad = ad;
        _scalar = scalar;
        PublicShare = publicShare;
    }

    /// <summary>
    /// Begins a CPace run: samples a scalar and computes the public share from the
    /// PRS-derived generator.
    /// </summary>
    public static CPace Start(CPaceRole role, byte[] prs, byte[] sid, byte[]? ci = null, byte[]? ad = null)
        => StartWithScalar(role, prs, sid, RandomNumberGenerator.GetBytes(FieldBytes), ci, ad);

    /// <summary>Deterministic variant for known-answer tests.</summary>
    internal static CPace StartWithScalar(
        CPaceRole role, byte[] prs, byte[] sid, byte[] scalar, byte[]? ci = null, byte[]? ad = null)
    {
        byte[] generator = CalculateGenerator(prs, ci ?? [], sid);
        byte[] share = X25519.ScalarMultVerified(scalar, generator)
            ?? throw new CPaceException("generator encodes a low-order point");
        return new CPace(role, sid, ad ?? [], scalar, share);
    }

    /// <summary>Ingests the peer's share, deriving the ISK and confirmation MAC key.</summary>
    public void Derive(byte[] peerShare, byte[]? peerAd = null)
    {
        byte[] scalar = _scalar ?? throw new CPaceException("Derive() may only be called once");
        _scalar = null;
        if (peerShare.Length != FieldBytes)
            throw new CPaceException($"peer share must be {FieldBytes} bytes");

        byte[] shared = X25519.ScalarMultVerified(scalar, peerShare)
            ?? throw new CPaceException("peer share encodes a low-order point");

        peerAd ??= [];
        (_sideA, _sideB) = _role == CPaceRole.Initiator
            ? ((PublicShare, _ad), (peerShare, peerAd))
            : ((peerShare, peerAd), (PublicShare, _ad));

        byte[] transcript = [.. LvCat(_sideA.Share, _sideA.Ad), .. LvCat(_sideB.Share, _sideB.Ad)];
        _isk = SHA512.HashData([.. LvCat(DsiIsk, _sid, shared), .. transcript]);
        _macKey = SHA512.HashData([.. MacLabel, .. _sid, .. _isk]);
        _derived = true;
    }

    /// <summary>The 64-byte intermediate session key.</summary>
    public byte[] Isk => _derived ? _isk : throw new CPaceException("Derive() must be called first");

    /// <summary>This side's 64-byte confirmation tag (Ta for A, Tb for B).</summary>
    public byte[] Tag() => Mac(own: true);

    /// <summary>Whether the peer's tag proves knowledge of the PRS.</summary>
    public bool Verify(byte[] peerTag)
    {
        if (_sideA.Share.AsSpan().SequenceEqual(_sideB.Share) && _sideA.Ad.AsSpan().SequenceEqual(_sideB.Ad))
            return false; // reflection: the expected peer tag would equal our own
        return CryptographicOperations.FixedTimeEquals(peerTag, Mac(own: false));
    }

    private byte[] Mac(bool own)
    {
        if (!_derived)
            throw new CPaceException("Derive() must be called first");
        var (share, ad) = own == (_role == CPaceRole.Initiator) ? _sideA : _sideB;
        return HMACSHA512.HashData(_macKey, LvCat(share, ad));
    }

    // --- Construction helpers (mirroring the reference implementation exactly) ---

    private static byte[] PrependLen(byte[] data)
    {
        int length = data.Length;
        var prefix = new List<byte>();
        while (true)
        {
            prefix.Add((byte)(length & 0x7F));
            length >>= 7;
            if (length == 0)
                break;
            prefix[^1] |= 0x80;
        }

        return [.. prefix, .. data];
    }

    private static byte[] LvCat(params byte[][] parts) => parts.SelectMany(PrependLen).ToArray();

    internal static byte[] CalculateGenerator(byte[] prs, byte[] ci, byte[] sid)
    {
        int zpadLen = Math.Max(
            0, Sha512BlockBytes - 1 - PrependLen(prs).Length - PrependLen(Dsi).Length);
        byte[] genString = LvCat(Dsi, prs, new byte[zpadLen], ci, sid);
        byte[] hash = SHA512.HashData(genString)[..FieldBytes];
        return Elligator2(X25519.DecodeU(hash));
    }

    internal static byte[] Elligator2(BigInteger r)
    {
        BigInteger p = X25519.FieldPrime;
        r = X25519.Mod(r);
        BigInteger denom = X25519.Mod(1 + Z * r * r);
        BigInteger v = X25519.Mod(-CurveA * Inv0(denom));
        BigInteger eps = BigInteger.ModPow(
            X25519.Mod(v * v * v + CurveA * v * v + v), LegendrePower, p);
        BigInteger x = X25519.Mod(eps * v - (1 - eps) * CurveA * Inv2);
        return X25519.EncodeU(x);
    }

    private static BigInteger Inv0(BigInteger x)
        => BigInteger.ModPow(x, X25519.FieldPrime - 2, X25519.FieldPrime);
}
