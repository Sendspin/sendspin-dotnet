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
        ArgumentNullException.ThrowIfNull(encoded);

        // Deliberately above the #if, on the path both frameworks run. The net8.0 fallback
        // below translates base64url into standard base64 and hands it to
        // Convert.FromBase64String, which also accepts '+' and '/'; net10.0's
        // Base64Url.DecodeFromChars rejects them. Rejecting exactly those two characters
        // here makes the answers identical without changing what either framework already
        // accepts. Whitespace is stripped in the net8.0 branch below, not here, because the
        // problem there isn't acceptance — Convert.FromBase64String already skips it — it's
        // that the padding arithmetic counts it; see that branch for why.
        //
        // Do not move this into the #else and do not widen it into a full alphabet check.
        // Both mistakes are now caught: the test project runs on net8.0 as well as net10.0, so
        // Base64UrlTextTests executes this method's other body too (#155). Moving the guard into
        // the #if fails Decode_RejectsStandardBase64Characters on net8.0 while net10.0 stays
        // green — measured, and the reason that suite is worth its weight.
        if (encoded.AsSpan().IndexOfAny('+', '/') >= 0)
        {
            throw new FormatException("Input is not a valid base64url string.");
        }

#if NET9_0_OR_GREATER
        return System.Buffers.Text.Base64Url.DecodeFromChars(encoded);
#else
        // Strip whitespace before computing padding: Convert.FromBase64String skips it, but
        // if we compute padding from the raw length, whitespace gets counted as significant
        // characters. That miscounts padding whenever the whitespace count isn't a multiple
        // of 4, and Convert then sees a non-multiple-of-4 significant-character count and
        // throws — a divergence from net10.0, which tolerates whitespace anywhere.
        //
        // The set stripped here is exactly ' ', '\t', '\r', '\n' — not char.IsWhiteSpace.
        // Measured on net10.0.302: Base64Url.DecodeFromChars and Convert.FromBase64String
        // both skip only those four ASCII characters and throw FormatException on every other
        // char.IsWhiteSpace member tried (including '\v', '\f', U+0085, U+00A0, U+2000,
        // U+3000). Stripping the full Unicode White_Space set here would make net8.0 accept
        // input that net10.0 rejects — the same #108 divergence this file exists to close.
        string b64 = string.Concat(encoded.Where(c => c is not (' ' or '\t' or '\r' or '\n')))
            .Replace('-', '+').Replace('_', '/');
        int padding = (4 - b64.Length % 4) % 4;
        return Convert.FromBase64String(b64.PadRight(b64.Length + padding, '='));
#endif
    }
}
