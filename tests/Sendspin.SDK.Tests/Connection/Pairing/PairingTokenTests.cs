using Sendspin.SDK.Connection.Noise.Pairing;

namespace Sendspin.SDK.Tests.Connection.Pairing;

/// <summary>
/// Verifies the spec pairing token format (spec #125) against the two published reference
/// vectors, plus the lenient-decode and rejection rules.
/// </summary>
public class PairingTokenTests
{
    // client_key = 0x00..0x1f in both KATs.
    private static readonly byte[] ClientKey = Enumerable.Range(0x00, 32).Select(b => (byte)b).ToArray();

    // KAT 1: pairing_psk = 0xe0..0xff (spec #125).
    private static readonly byte[] Kat1Psk = Enumerable.Range(0xe0, 32).Select(b => (byte)b).ToArray();
    private const string Kat1Token =
        "SP:0AAAQEAYEAUDAOCAJBIFQYDIOB4IBCEQTCQKRMFYYDENBWHA5DYP6BYPC4PSOLZXH5DU6V97M5XXO74HR6LZ7J5PW674PT6X37T6757Y";

    // KAT 2: pairing_psk = 0x20..0x3f (aiosendspin main).
    private static readonly byte[] Kat2Psk = Enumerable.Range(0x20, 32).Select(b => (byte)b).ToArray();
    private const string Kat2Token =
        "SP:0AAAQEAYEAUDAOCAJBIFQYDIOB4IBCEQTCQKRMFYYDENBWHA5DYPSAIJCEMSCKJRHFAUSUKZMFUXC6MBRGIZTINJWG44DSOR3HQ6T4PY";

    [Fact]
    public void Encode_Kat1_ProducesExactToken()
    {
        string token = PairingToken.Encode(ClientKey, Kat1Psk);

        Assert.Equal(Kat1Token, token);
        Assert.Equal(107, token.Length);
    }

    [Fact]
    public void Encode_Kat2_ProducesExactToken()
    {
        string token = PairingToken.Encode(ClientKey, Kat2Psk);

        Assert.Equal(Kat2Token, token);
        Assert.Equal(107, token.Length);
    }

    [Fact]
    public void Decode_Kat1_RoundTripsToExactPayload()
    {
        var (clientKey, pairingPsk) = PairingToken.Decode(Kat1Token);

        Assert.Equal(ClientKey, clientKey);
        Assert.Equal(Kat1Psk, pairingPsk);
    }

    [Fact]
    public void Decode_Kat2_RoundTripsToExactPayload()
    {
        var (clientKey, pairingPsk) = PairingToken.Decode(Kat2Token);

        Assert.Equal(ClientKey, clientKey);
        Assert.Equal(Kat2Psk, pairingPsk);
    }

    [Fact]
    public void Decode_VersionOneToken_DecodesToSamePayload()
    {
        string versionOneToken = "SP:1" + Kat2Token.Substring(4);

        var (clientKey, pairingPsk) = PairingToken.Decode(versionOneToken);

        Assert.Equal(ClientKey, clientKey);
        Assert.Equal(Kat2Psk, pairingPsk);
    }

    [Fact]
    public void Decode_VersionTwoToken_Throws()
    {
        string versionTwoToken = "SP:2" + Kat2Token.Substring(4);

        Assert.Throws<FormatException>(() => PairingToken.Decode(versionTwoToken));
    }

    [Fact]
    public void Decode_LowerCased_DecodesToKat2Payload()
    {
        var (clientKey, pairingPsk) = PairingToken.Decode(Kat2Token.ToLowerInvariant());

        Assert.Equal(ClientKey, clientKey);
        Assert.Equal(Kat2Psk, pairingPsk);
    }

    [Fact]
    public void Decode_SurroundedByWhitespace_DecodesToKat2Payload()
    {
        var (clientKey, pairingPsk) = PairingToken.Decode("  \t" + Kat2Token + "\n  ");

        Assert.Equal(ClientKey, clientKey);
        Assert.Equal(Kat2Psk, pairingPsk);
    }

    [Fact]
    public void Decode_WithoutPrefix_DecodesToKat2Payload()
    {
        string withoutPrefix = Kat2Token.Substring("SP:".Length);

        var (clientKey, pairingPsk) = PairingToken.Decode(withoutPrefix);

        Assert.Equal(ClientKey, clientKey);
        Assert.Equal(Kat2Psk, pairingPsk);
    }

    [Fact]
    public void Encode_NeverProducesTheDigit2()
    {
        Assert.DoesNotContain('2', PairingToken.Encode(ClientKey, Kat1Psk));
        Assert.DoesNotContain('2', PairingToken.Encode(ClientKey, Kat2Psk));
    }

    [Fact]
    public void Decode_WithLiteral2InPlaceOf9_DecodesToSamePayload()
    {
        Assert.Contains('9', Kat1Token);
        string withLiteral2 = Kat1Token.Replace('9', '2');

        var (clientKey, pairingPsk) = PairingToken.Decode(withLiteral2);

        Assert.Equal(ClientKey, clientKey);
        Assert.Equal(Kat1Psk, pairingPsk);
    }

    [Fact]
    public void Decode_BodyThatDecodesTo63Bytes_Throws()
    {
        string token = "SP:0" + Base32.Encode(new byte[63]);

        Assert.Throws<FormatException>(() => PairingToken.Decode(token));
    }

    [Fact]
    public void Decode_BodyThatDecodesTo65Bytes_Throws()
    {
        string token = "SP:0" + Base32.Encode(new byte[65]);

        Assert.Throws<FormatException>(() => PairingToken.Decode(token));
    }

    [Fact]
    public void Decode_EmptyString_Throws()
    {
        Assert.Throws<FormatException>(() => PairingToken.Decode(string.Empty));
    }

    [Fact]
    public void Decode_PrefixOnly_Throws()
    {
        Assert.Throws<FormatException>(() => PairingToken.Decode("SP:"));
    }

    [Fact]
    public void Encode_31ByteClientKey_Throws()
    {
        // Without the guard a short key zero-pads into a well-formed 107-character token
        // carrying wrong key material — it must throw instead.
        var ex = Assert.Throws<ArgumentException>(() => PairingToken.Encode(new byte[31], Kat1Psk));

        Assert.Equal("clientKey", ex.ParamName);
    }

    [Fact]
    public void Encode_31BytePairingPsk_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => PairingToken.Encode(ClientKey, new byte[31]));

        Assert.Equal("pairingPsk", ex.ParamName);
    }

    [Fact]
    public void Decode_BodyWithCharacterOutsideAlphabet_Throws()
    {
        // '1' and '8' are not in the RFC 4648 base32 alphabet (A-Z2-7).
        string bodyWith1 = "1" + Kat2Token.Substring(4).Substring(1);
        string bodyWith8 = "8" + Kat2Token.Substring(4).Substring(1);

        Assert.Throws<FormatException>(() => PairingToken.Decode("SP:0" + bodyWith1));
        Assert.Throws<FormatException>(() => PairingToken.Decode("SP:0" + bodyWith8));
    }
}
