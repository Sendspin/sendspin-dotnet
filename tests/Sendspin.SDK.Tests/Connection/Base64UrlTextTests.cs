using Sendspin.SDK.Connection.Noise;

namespace Sendspin.SDK.Tests.Connection;

/// <summary>
/// Base64UrlText.Decode must give the same answer on net8.0 and net10.0. The two frameworks
/// run different bodies — Convert.FromBase64String after an alphabet translation versus
/// System.Buffers.Text.Base64Url — and the net8.0 fallback accepts the standard-base64
/// characters '+' and '/' that base64url does not have (#108).
/// </summary>
/// <remarks>
/// This project targets net10.0 only, so these tests can never execute the net8.0 body.
/// They cover the divergence because the rule they pin lives on the shared path above the
/// #if. Moving that check into either branch makes these tests stop covering net8.0 while
/// still passing.
/// </remarks>
public class Base64UrlTextTests
{
    [Fact]
    public void Decode_RoundTripsEncode()
    {
        byte[] data = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

        Assert.Equal(data, Base64UrlText.Decode(Base64UrlText.Encode(data)));
    }

    [Theory]
    [InlineData('+')]
    [InlineData('/')]
    public void Decode_RejectsStandardBase64Characters(char character)
    {
        // 43 characters: what a 32-byte server_id or psk encodes to, which is where the
        // divergence actually bites.
        string encoded = Base64UrlText.Encode(new byte[32]);
        string mutated = character + encoded[1..];

        Assert.Throws<FormatException>(() => Base64UrlText.Decode(mutated));
    }

    [Theory]
    [InlineData('-')]
    [InlineData('_')]
    public void Decode_AcceptsBase64UrlCharacters(char character)
    {
        // The guard rejects two characters, not "everything unusual": '-' and '_' are the
        // base64url alphabet's own substitutions and must keep decoding.
        string encoded = Base64UrlText.Encode(new byte[32]);
        string mutated = character + encoded[1..];

        Assert.Equal(32, Base64UrlText.Decode(mutated).Length);
    }

    [Fact]
    public void Decode_AcceptsPadding()
    {
        // net10.0's Base64Url.DecodeFromChars tolerates '=' padding. Pinned so the guard is
        // never widened into a full-alphabet check, which would reject it and create a fresh
        // divergence in the opposite direction.
        string encoded = Base64UrlText.Encode(new byte[32]);

        Assert.Equal(32, Base64UrlText.Decode(encoded + "=").Length);
    }
}
