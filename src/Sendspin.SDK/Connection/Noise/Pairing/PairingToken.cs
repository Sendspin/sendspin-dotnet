namespace Sendspin.SDK.Connection.Noise.Pairing;

/// <summary>
/// The Sendspin pairing token: a single string carrying a client's static public key and its
/// Pairing PSK together, for distribution as a QR code or a pasted string. The app renders the
/// QR; the SDK supplies the string verbatim, with no URI wrapper.
/// </summary>
public static class PairingToken
{
    /// <summary>Token version this SDK emits.</summary>
    public const int EmittedVersion = 0;

    /// <summary>
    /// Builds the token for a client key and Pairing PSK, both 32 bytes. The result is 107
    /// characters and contains only QR alphanumeric characters.
    /// </summary>
    public static string Encode(ReadOnlySpan<byte> clientKey, ReadOnlySpan<byte> pairingPsk)
    {
        if (clientKey.Length != KeySize)
            throw new ArgumentException($"clientKey must be {KeySize} bytes.", nameof(clientKey));
        if (pairingPsk.Length != KeySize)
            throw new ArgumentException($"pairingPsk must be {KeySize} bytes.", nameof(pairingPsk));

        Span<byte> payload = stackalloc byte[PayloadSize];
        clientKey.CopyTo(payload);
        pairingPsk.CopyTo(payload.Slice(KeySize));

        string body = Base32.Encode(payload).Replace('2', '9');
        return $"{Prefix}{EmittedVersion}{body}";
    }

    /// <summary>
    /// Parses a token, tolerating case, surrounding whitespace and a missing <c>SP:</c> prefix.
    /// Accepts versions 0 and 1, which carry an identical payload.
    /// </summary>
    /// <exception cref="FormatException">
    /// The token is malformed, carries an unrecognised version, or does not decode to exactly
    /// 64 bytes.
    /// </exception>
    public static (byte[] ClientKey, byte[] PairingPsk) Decode(string token)
    {
        string trimmed = token.Trim().ToUpperInvariant();

        if (trimmed.StartsWith(Prefix, StringComparison.Ordinal))
            trimmed = trimmed.Substring(Prefix.Length);

        if (trimmed.Length == 0)
            throw new FormatException("Pairing token is empty.");

        char version = trimmed[0];
        if (version != '0' && version != '1')
            throw new FormatException($"Pairing token has unrecognised version '{version}'; expected 0 or 1.");

        string body = trimmed.Substring(1).Replace('9', '2');

        byte[] payload = Base32.Decode(body);
        if (payload.Length != PayloadSize)
            throw new FormatException($"Pairing token payload is {payload.Length} bytes; expected {PayloadSize}.");

        byte[] clientKey = payload[..KeySize];
        byte[] pairingPsk = payload[KeySize..];
        return (clientKey, pairingPsk);
    }

    private const string Prefix = "SP:";
    private const int KeySize = 32;
    private const int PayloadSize = KeySize * 2;
}
