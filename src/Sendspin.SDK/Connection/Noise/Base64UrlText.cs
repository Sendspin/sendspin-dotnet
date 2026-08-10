namespace Sendspin.SDK.Connection.Noise;

/// <summary>
/// Base64url (RFC 4648 §5, unpadded) helpers that work on every target framework.
/// </summary>
internal static class Base64UrlText
{
    public static string Encode(ReadOnlySpan<byte> data)
    {
#if NET9_0_OR_GREATER
        return System.Buffers.Text.Base64Url.EncodeToString(data);
#else
        return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
#endif
    }

    public static byte[] Decode(string encoded)
    {
        // Deliberately above the #if, on the path both frameworks run. The net8.0 fallback
        // below translates base64url into standard base64 and hands it to
        // Convert.FromBase64String, which also accepts '+' and '/'; net10.0's
        // Base64Url.DecodeFromChars rejects them. Those two characters are the whole
        // disagreement — both frameworks accept '=' padding and skip whitespace — so
        // rejecting exactly them here makes the answers identical without changing what
        // either framework already accepts.
        //
        // Do not move this into the #else and do not widen it into a full alphabet check.
        // The test project targets net10.0 only, so no test can catch either mistake (#108).
        if (encoded.AsSpan().IndexOfAny('+', '/') >= 0)
        {
            throw new FormatException("Input is not a valid base64url string.");
        }

#if NET9_0_OR_GREATER
        return System.Buffers.Text.Base64Url.DecodeFromChars(encoded);
#else
        string b64 = encoded.Replace('-', '+').Replace('_', '/');
        int padding = (4 - b64.Length % 4) % 4;
        return Convert.FromBase64String(b64.PadRight(b64.Length + padding, '='));
#endif
    }
}
