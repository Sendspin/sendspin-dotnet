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
        var v = Kats.GetProperty("sid");
        Assert.Equal(Hex(v.GetProperty("sid").GetString()!),
            PairingCodes.BuildSid(Hex(v.GetProperty("h").GetString()!), (uint)v.GetProperty("counter").GetInt32()));
    }

    [Fact]
    public void WrapPsk_MatchesReference()
    {
        var v = Kats.GetProperty("wrap_psk");
        byte[] wrapped = PairingCodes.WrapPsk(
            Hex(v.GetProperty("sid").GetString()!),
            Hex(v.GetProperty("isk").GetString()!),
            Hex(v.GetProperty("psk").GetString()!),
            NoiseCipherSuite.ChaChaPoly);
        Assert.Equal(Hex(v.GetProperty("wrapped").GetString()!), wrapped);
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
        byte[] sid = PairingCodes.BuildSid(h, 1);
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
