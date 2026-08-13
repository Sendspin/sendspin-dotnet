namespace Sendspin.SDK.Tests;

/// <summary>
/// base64url for the server-simulating test doubles, on APIs both shipped target frameworks
/// have.
/// </summary>
/// <remarks>
/// <para>
/// <c>System.Buffers.Text.Base64Url</c> is net9+, and the doubles used it — which is what kept
/// the test project on <c>net10.0</c> and left the <c>net8.0</c> half of every <c>#if</c> in the
/// library unexecutable (#155).
/// </para>
/// <para>
/// Deliberately not the SDK's <c>Base64UrlText</c>. These doubles stand in for the server, so
/// encoding the wire with the same code under test would let a bug agree with itself: #108 was
/// three separate encoding disagreements, and a shared implementation would have hidden all of
/// them. Same reasoning as the independent implementations already in the pairing tests (#93).
/// </para>
/// </remarks>
internal static class TestBase64Url
{
    internal static string EncodeToString(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[] DecodeFromChars(ReadOnlySpan<char> chars)
    {
        string padded = new string(chars).Replace('-', '+').Replace('_', '/');

        // Restore the padding Convert.FromBase64String requires and base64url omits.
        padded = padded.PadRight((padded.Length + 3) / 4 * 4, '=');

        return Convert.FromBase64String(padded);
    }
}
