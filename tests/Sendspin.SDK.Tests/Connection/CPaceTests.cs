using System.Numerics;
using System.Text.Json;
using Sendspin.SDK.Connection.Noise.Pairing;

namespace Sendspin.SDK.Tests.Connection;

/// <summary>
/// Verifies the CPACE-X25519-SHA512 port against known-answer vectors generated from
/// the aiosendspin reference implementation (the `cpace` PyPI package), plus RFC 7748
/// X25519 vectors and protocol-level failure behavior.
/// </summary>
public class CPaceTests
{
    private static readonly JsonElement Kats = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Connection", "cpace-kats.json"))).RootElement;

    private static byte[] Hex(JsonElement e, string name) => Convert.FromHexString(e.GetProperty(name).GetString()!);

    [Fact]
    public void X25519_Rfc7748Vector1()
    {
        // RFC 7748 §5.2 test vector 1
        byte[] scalar = Convert.FromHexString("a546e36bf0527c9d3b16154b82465edd62144c0ac1fc5a18506a2244ba449ac4");
        byte[] u = Convert.FromHexString("e6db6867583030db3594c1a424b15f7c726624ec26b3353b10a903a6d0ab1c4c");
        byte[] expected = Convert.FromHexString("c3da55379de9c6908e94ea4df28d084f32eccf03491c71f754b4075577a28552");
        Assert.Equal(expected, X25519.ScalarMult(scalar, u));
    }

    [Fact]
    public void X25519_Rfc7748Vector2()
    {
        byte[] scalar = Convert.FromHexString("4b66e9d4d1b4673c5ad22691957d6af5c11b6421e0ea01d42ca4169e7918ba0d");
        byte[] u = Convert.FromHexString("e5210f12786811d3f4b7959d0538ae2c31dbe7106fc03c3efc4cd549c715a493");
        byte[] expected = Convert.FromHexString("95cbde9476e8907d7aade45cb4b873f88b595a68799fa152e6f8f7647aac7957");
        Assert.Equal(expected, X25519.ScalarMult(scalar, u));
    }

    [Fact]
    public void Elligator2_MatchesReferenceVectors()
    {
        foreach (var v in Kats.GetProperty("elligator2").EnumerateArray())
        {
            var r = new BigInteger(Hex(v, "r"), isUnsigned: true, isBigEndian: false);
            Assert.Equal(Hex(v, "x"), CPace.Elligator2(r));
        }
    }

    [Fact]
    public void CPace_FullRuns_MatchReferenceVectors()
    {
        foreach (var v in Kats.GetProperty("cpace").EnumerateArray())
        {
            byte[] prs = Hex(v, "prs"), ci = Hex(v, "ci"), sid = Hex(v, "sid");
            byte[] ada = Hex(v, "ada"), adb = Hex(v, "adb");

            Assert.Equal(Hex(v, "generator"), CPace.CalculateGenerator(prs, ci, sid));

            var a = CPace.StartWithScalar(CPaceRole.Initiator, prs, sid, Hex(v, "scalarA"), ci, ada);
            var b = CPace.StartWithScalar(CPaceRole.Responder, prs, sid, Hex(v, "scalarB"), ci, adb);
            Assert.Equal(Hex(v, "Ya"), a.PublicShare);
            Assert.Equal(Hex(v, "Yb"), b.PublicShare);

            a.Derive(b.PublicShare, adb);
            b.Derive(a.PublicShare, ada);
            Assert.Equal(Hex(v, "isk"), a.Isk);
            Assert.Equal(Hex(v, "isk"), b.Isk);
            Assert.Equal(Hex(v, "Ta"), a.Tag());
            Assert.Equal(Hex(v, "Tb"), b.Tag());
            Assert.True(a.Verify(b.Tag()));
            Assert.True(b.Verify(a.Tag()));
        }
    }

    [Fact]
    public void CPace_WrongPin_TagsFailVerification()
    {
        byte[] sid = new byte[16];
        var a = CPace.Start(CPaceRole.Initiator, "1234"u8.ToArray(), sid, ad: "server"u8.ToArray());
        var b = CPace.Start(CPaceRole.Responder, "9999"u8.ToArray(), sid, ad: "client"u8.ToArray());

        a.Derive(b.PublicShare, "client"u8.ToArray());
        b.Derive(a.PublicShare, "server"u8.ToArray());

        Assert.False(a.Verify(b.Tag()));
        Assert.False(b.Verify(a.Tag()));
    }

    [Fact]
    public void CPace_LowOrderPeerShare_Throws()
    {
        var b = CPace.Start(CPaceRole.Responder, "1234"u8.ToArray(), new byte[8]);
        Assert.Throws<CPaceException>(() => b.Derive(new byte[32])); // u=0 is low-order
    }

    [Fact]
    public void CPace_DeriveTwice_Throws()
    {
        var a = CPace.Start(CPaceRole.Initiator, "1234"u8.ToArray(), new byte[8]);
        var b = CPace.Start(CPaceRole.Responder, "1234"u8.ToArray(), new byte[8]);
        a.Derive(b.PublicShare);
        Assert.Throws<CPaceException>(() => a.Derive(b.PublicShare));
    }
}
