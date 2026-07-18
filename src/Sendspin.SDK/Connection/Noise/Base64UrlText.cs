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
#if NET9_0_OR_GREATER
        return System.Buffers.Text.Base64Url.DecodeFromChars(encoded);
#else
        string b64 = encoded.Replace('-', '+').Replace('_', '/');
        int padding = (4 - b64.Length % 4) % 4;
        return Convert.FromBase64String(b64.PadRight(b64.Length + padding, '='));
#endif
    }
}
