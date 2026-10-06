namespace Sendspin.SDK.Connection.Noise;

/// <summary>The category a stored PSK belongs to, determining post-handshake routing.</summary>
public enum PskCategory
{
    /// <summary>A long-term Sendspin PSK established by pairing.</summary>
    LongTerm,

    /// <summary>A Sendspin Pairing PSK (bootstrap secret for the Pairing PSK flow).</summary>
    Pairing,

    /// <summary>The published Sentinel PSK (no authentication).</summary>
    Sentinel,
}

/// <summary>A PSK resolved for a Noise handshake by <see cref="INoisePskResolver"/>.</summary>
/// <param name="Key">The 32-byte PSK.</param>
/// <param name="Category">The PSK's category.</param>
/// <param name="ServerId">
/// For stored-pubkey records, the server id the PSK is bound to; the handshake fails if
/// it does not match the connected server. Null for shared-PSK records and the sentinel.
/// </param>
internal sealed record NoisePsk(ReadOnlyMemory<byte> Key, PskCategory Category, string? ServerId = null);

/// <summary>
/// Resolves the psk_id and psk_category received in Noise handshake message 1 to a PSK candidate.
/// </summary>
internal interface INoisePskResolver
{
    /// <summary>
    /// Returns the PSK of the declared category whose derived psk_id matches, or null for a
    /// lookup miss. A psk_id held only under a different category is a miss (connection.md
    /// § Pre-Shared Key).
    /// </summary>
    NoisePsk? Resolve(string pskId, PskCategory category);
}

/// <summary>
/// A resolver that knows only the published Sentinel PSK - the pre-pairing default.
/// </summary>
internal sealed class SentinelPskResolver : INoisePskResolver
{
    /// <summary>Shared stateless instance.</summary>
    public static SentinelPskResolver Instance { get; } = new();

    /// <inheritdoc/>
    public NoisePsk? Resolve(string pskId, PskCategory category) =>
        category == PskCategory.Sentinel && pskId == NoiseConstants.SentinelPskId
            ? new NoisePsk(NoiseConstants.SentinelPsk.ToArray(), PskCategory.Sentinel)
            : null;
}
