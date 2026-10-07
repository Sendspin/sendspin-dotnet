using System.Text;
using System.Text.Json;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Connection.Noise.Pairing;

namespace Sendspin.SDK.Tests.Connection;

/// <summary>
/// Verifies the Sendspin pairing code constructions (pairing code derivation, commit, sid, PSK
/// wrapping) against known-answer vectors from the aiosendspin reference, plus a full
/// CPace MCF round-trip that unwraps the delivered PSK server-side.
/// </summary>
public class PairingCodeTests
{
    private static readonly JsonElement Kats = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Connection", "pin-kats.json"))).RootElement;

    private static byte[] Hex(string h) => Convert.FromHexString(h);

    [Fact]
    public void DerivePairingCode_MatchesReference()
    {
        foreach (var v in Kats.GetProperty("derive_pin").EnumerateArray())
        {
            string pin = PairingCodes.DerivePairingCode(
                Hex(v.GetProperty("h").GetString()!),
                Hex(v.GetProperty("nonce_A").GetString()!),
                Hex(v.GetProperty("nonce_B").GetString()!),
                v.GetProperty("length").GetInt32());
            Assert.Equal(v.GetProperty("pin").GetString(), pin);
        }
    }

    [Fact]
    public void CommitB_MatchesReference()
    {
        var v = Kats.GetProperty("commit_B");
        Assert.Equal(Hex(v.GetProperty("commit").GetString()!),
            PairingCodes.CommitB(Hex(v.GetProperty("nonce_B").GetString()!)));
    }

    [Fact]
    public void BuildSid_MatchesReference()
    {
        // Vectors: label || h || u32be(pairing_index) || u32be(round). Cross-checked against
        // aiosendspin tests/noise/test_pairing.py::test_pake_sid_known_answer (commit 4ed7c45,
        // spec #237 / aiosendspin #415). The SDK emits round 1 for both static and dynamic (no
        // client/pair-retry yet); the round-3 vector pins that BuildSid binds the round anyway.
        foreach (var v in Kats.GetProperty("sid").EnumerateArray())
        {
            Assert.Equal(
                Hex(v.GetProperty("sid").GetString()!),
                PairingCodes.BuildSid(
                    Hex(v.GetProperty("h").GetString()!),
                    (uint)v.GetProperty("pairing_index").GetInt32(),
                    (uint)v.GetProperty("round").GetInt32()));
        }
    }

    [Fact]
    public void WrapPsk_MatchesReference()
    {
        // Provenance: the wrap KATs use a spec-current round-aware sid whose bytes match
        // aiosendspin test_pake_sid_known_answer (index 2, round 1). The wrapped value is the
        // reference AEAD seal — AEAD(SHA-256(label || sid || ISK), 12-byte zero nonce) over the
        // 32-byte plaintext, 48-byte ciphertext+tag — from aiosendspin noise/pairing.py
        // (_wrap_key + _wrap_aead), computed by the same generator used for the sid and nonce KATs.
        var v = Kats.GetProperty("wrap_psk");
        byte[] wrapped = PairingCodes.WrapPsk(
            Hex(v.GetProperty("sid").GetString()!),
            Hex(v.GetProperty("isk").GetString()!),
            Hex(v.GetProperty("psk").GetString()!),
            NoiseCipherSuite.ChaChaPoly);
        Assert.Equal(Hex(v.GetProperty("wrapped").GetString()!), wrapped);
    }

    [Fact]
    public void WrapNonceB_MatchesReference()
    {
        // wrap_psk and wrap_nonce_B share the same round-aware sid and ISK, so this vector differs
        // from wrap_psk only by the label (sendspin-pair-nonce-wrap-v1, spec #155 / aiosendspin
        // #344) and the plaintext. Both are the reference AEAD seal; see WrapPsk_MatchesReference
        // for provenance.
        var v = Kats.GetProperty("wrap_nonce_B");
        byte[] wrapped = PairingCodes.WrapNonceB(
            Hex(v.GetProperty("sid").GetString()!),
            Hex(v.GetProperty("isk").GetString()!),
            Hex(v.GetProperty("nonce_B").GetString()!),
            NoiseCipherSuite.ChaChaPoly);
        Assert.Equal(Hex(v.GetProperty("wrapped").GetString()!), wrapped);
    }

    [Fact]
    public void Sdk_DoesNotReferenceTheBclAeads()
    {
        // The suite is chosen by probing libsodium (NoiseCipherSuite.IsSupported), so everything
        // sealed with "the suite's AEAD" has to run on libsodium too. The BCL AEADs are a
        // different backend with different gaps: ChaCha20Poly1305 throws
        // PlatformNotSupportedException on every Windows build before 20142, i.e. all of
        // Windows 10, where libsodium and therefore the probe are fine (#315). That cannot be
        // reproduced on a machine that has both, so pin the dependency itself.
        using var pe = new System.Reflection.PortableExecutable.PEReader(
            File.OpenRead(typeof(PairingCodes).Assembly.Location));
        var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);

        var aeads = md.TypeReferences
            .Select(md.GetTypeReference)
            .Where(t => md.GetString(t.Namespace) == "System.Security.Cryptography")
            .Select(t => md.GetString(t.Name))
            .Where(n => n is "ChaCha20Poly1305" or "AesGcm");

        Assert.Empty(aeads);
    }

    [Theory]
    [InlineData(NoiseCipherSuite.ChaChaPoly)]
    [InlineData(NoiseCipherSuite.AesGcm)]
    public void Wrap_IsByteIdenticalToTheBclAead(NoiseCipherSuite suite)
    {
        // Where both backends exist they must agree in both directions: the BCL seal of the same
        // input is the same 48 bytes, and the BCL opens what the SDK sealed.
        bool bcl = suite == NoiseCipherSuite.AesGcm
            ? System.Security.Cryptography.AesGcm.IsSupported
            : System.Security.Cryptography.ChaCha20Poly1305.IsSupported;
        if (!suite.IsSupported() || !bcl)
            return;

        byte[] sid = System.Security.Cryptography.RandomNumberGenerator.GetBytes(61);
        byte[] isk = System.Security.Cryptography.RandomNumberGenerator.GetBytes(64);
        byte[] value = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

        foreach (var (label, wrapped) in new[]
        {
            ("sendspin-pair-psk-wrap-v1", PairingCodes.WrapPsk(sid, isk, value, suite)),
            ("sendspin-pair-nonce-wrap-v1", PairingCodes.WrapNonceB(sid, isk, value, suite)),
        })
        {
            byte[] kWrap = System.Security.Cryptography.SHA256.HashData(
                [.. Encoding.ASCII.GetBytes(label), .. sid, .. isk]);
            byte[] ct = new byte[32]; byte[] tag = new byte[16]; byte[] opened = new byte[32];
            if (suite == NoiseCipherSuite.AesGcm)
            {
                using var aes = new System.Security.Cryptography.AesGcm(kWrap, 16);
                aes.Encrypt(new byte[12], value, ct, tag);
                aes.Decrypt(new byte[12], wrapped.AsSpan(..32), wrapped.AsSpan(32..), opened);
            }
            else
            {
                using var chacha = new System.Security.Cryptography.ChaCha20Poly1305(kWrap);
                chacha.Encrypt(new byte[12], value, ct, tag);
                chacha.Decrypt(new byte[12], wrapped.AsSpan(..32), wrapped.AsSpan(32..), opened);
            }

            Assert.Equal([.. ct, .. tag], wrapped);
            Assert.Equal(value, opened);
        }
    }

    [Fact]
    public void FullPakeRound_ServerUnwrapsClientPsk()
    {
        // Both sides derive the same pairing code from shared handshake material, run CPace, and
        // the server recovers the client's wrapped PSK — the crypto core of the pairing code flow.
        byte[] h = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        byte[] nonceA = new byte[32]; byte[] nonceB = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(nonceA);
        System.Security.Cryptography.RandomNumberGenerator.Fill(nonceB);
        const int length = 6;
        string pin = PairingCodes.DerivePairingCode(h, nonceA, nonceB, length);
        byte[] sid = PairingCodes.BuildSid(h, 1, 1);
        byte[] prs = Encoding.ASCII.GetBytes(pin);

        var server = CPace.Start(CPaceRole.Initiator, prs, sid, ad: PairingCodes.AdServer);
        var client = CPace.Start(CPaceRole.Responder, prs, sid, ad: PairingCodes.AdClient);

        client.Derive(server.PublicShare, PairingCodes.AdServer);
        server.Derive(client.PublicShare, PairingCodes.AdClient);

        Assert.True(client.Verify(server.Tag()));
        Assert.True(server.Verify(client.Tag()));

        byte[] psk = Enumerable.Repeat((byte)0xCD, 32).ToArray();
        byte[] wrapped = PairingCodes.WrapPsk(sid, client.Isk, psk, NoiseCipherSuite.ChaChaPoly);

        // Server unwraps with its own (identical) ISK.
        byte[] kWrap = System.Security.Cryptography.SHA256.HashData(
            [.. "sendspin-pair-psk-wrap-v1"u8.ToArray(), .. sid, .. server.Isk]);
        byte[] ct = wrapped[..32]; byte[] tag = wrapped[32..];
        byte[] recovered = new byte[32];
        using var chacha = new System.Security.Cryptography.ChaCha20Poly1305(kWrap);
        chacha.Decrypt(new byte[12], ct, tag, recovered);
        Assert.Equal(psk, recovered);
    }
}
