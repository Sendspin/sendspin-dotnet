using System.Text;

namespace Sendspin.SDK.Connection.Noise.Pairing;

/// <summary>
/// RFC 4648 base32 (alphabet <c>A-Z2-7</c>) helpers. Exists for <see cref="PairingToken"/> and
/// nothing else; kept internal so it is not SDK API surface.
/// </summary>
internal static class Base32
{
    /// <summary>Encodes bytes as base32 with no <c>=</c> padding.</summary>
    public static string Encode(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            return string.Empty;

        var result = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0;
        int bitsInBuffer = 0;

        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bitsInBuffer += 8;

            while (bitsInBuffer >= 5)
            {
                bitsInBuffer -= 5;
                result.Append(Alphabet[(buffer >> bitsInBuffer) & 0x1F]);
            }
        }

        if (bitsInBuffer > 0)
            result.Append(Alphabet[(buffer << (5 - bitsInBuffer)) & 0x1F]);

        return result.ToString();
    }

    /// <summary>
    /// Decodes a base32 string, case-insensitively and without requiring <c>=</c> padding.
    /// </summary>
    /// <exception cref="FormatException">The string contains a character outside the base32 alphabet.</exception>
    public static byte[] Decode(string encoded)
    {
        string upper = encoded.ToUpperInvariant().TrimEnd('=');

        var result = new List<byte>((upper.Length * 5) / 8);
        int buffer = 0;
        int bitsInBuffer = 0;

        foreach (char c in upper)
        {
            int value = Alphabet.IndexOf(c);
            if (value < 0)
                throw new FormatException($"Character '{c}' is not part of the base32 alphabet.");

            buffer = (buffer << 5) | value;
            bitsInBuffer += 5;

            if (bitsInBuffer >= 8)
            {
                bitsInBuffer -= 8;
                result.Add((byte)((buffer >> bitsInBuffer) & 0xFF));
            }
        }

        return result.ToArray();
    }

    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
}
