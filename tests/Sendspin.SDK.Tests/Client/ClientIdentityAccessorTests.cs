using Sendspin.SDK.Client;
using Sendspin.SDK.Connection.Noise;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Coverage for <see cref="ISendspinClient.ClientId"/> and <see cref="ISendspinClient.TrustLevel"/>:
/// read-only accessors over identity and trust state the client already holds.
/// </summary>
public class ClientIdentityAccessorTests
{
    // Fixed key material so the expected client id below is a literal, not a re-computation
    // of the implementation's base64url encoding. Public key bytes are 0x00..0x1F; the
    // private half is arbitrary (never used for a real handshake in these tests).
    private static readonly byte[] FixedPrivateKey = Enumerable.Repeat((byte)0xAA, 32).ToArray();
    private static readonly byte[] FixedPublicKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private const string ExpectedClientId = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8";

    [Fact]
    public void ClientId_IsTheBase64UrlEncodedPublicKey()
    {
        var identity = SendspinIdentity.FromKeys(FixedPrivateKey, FixedPublicKey);
        var (client, _, _) = TestClient.Create(configure: o => o.Identity = identity);
        using var _c = client;

        Assert.Equal(ExpectedClientId, client.ClientId);
    }

    [Fact]
    public void TrustLevel_IsNone_BeforeAnyHandshake()
    {
        var (client, _, session) = TestClient.Create();
        using var _c = client;
        session.MatchedPsk = null;

        Assert.Equal(SendspinTrustLevel.None, client.TrustLevel);
    }

    [Fact]
    public void TrustLevel_IsUnpaired_OnASentinelKeyedSession()
    {
        var (client, _, _) = TestClient.Create(PskCategory.Sentinel);
        using var _c = client;

        Assert.Equal(SendspinTrustLevel.Unpaired, client.TrustLevel);
    }

    [Fact]
    public void TrustLevel_IsPairing_OnAPairingKeyedSession()
    {
        var (client, _, _) = TestClient.Create(PskCategory.Pairing);
        using var _c = client;

        Assert.Equal(SendspinTrustLevel.Pairing, client.TrustLevel);
    }

    [Fact]
    public void TrustLevel_IsPaired_OnALongTermKeyedSession()
    {
        var (client, _, _) = TestClient.Create(PskCategory.LongTerm);
        using var _c = client;

        Assert.Equal(SendspinTrustLevel.Paired, client.TrustLevel);
    }
}
