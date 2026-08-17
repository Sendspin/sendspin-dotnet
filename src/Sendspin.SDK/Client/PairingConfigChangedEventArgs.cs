namespace Sendspin.SDK.Client;

/// <summary>
/// A server changed this client's pairing configuration through
/// <c>management/set-pairing-config</c>, or removed the stored Pairing record through
/// <c>management/remove-record</c>. The SDK applies the change to its own state and
/// raises this so the app can persist it; the SDK does not write to the
/// <see cref="ClientCapabilities"/> instance the app owns.
/// </summary>
public sealed class PairingConfigChangedEventArgs : EventArgs
{
    /// <summary>The effective unpaired-access setting after the change.</summary>
    public bool UnpairedAccessEnabled { get; init; }

    /// <summary>
    /// True when the server replaced or removed this client's Pairing PSK, so any pairing
    /// token previously obtained from <see cref="ISendspinClient.EnsurePairingPsk"/> is stale.
    /// </summary>
    public bool PairingPskReplaced { get; init; }

    /// <summary>The effective <c>pairing_psk</c> enabled setting after the change.</summary>
    public bool PairingPskEnabled { get; init; }

    /// <summary>The effective <c>dynamic_pin</c> enabled setting after the change.</summary>
    public bool DynamicPinEnabled { get; init; }

    /// <summary>The effective <c>static_pin</c> enabled setting after the change.</summary>
    public bool StaticPinEnabled { get; init; }

    /// <summary>The effective minimum PIN length after the change.</summary>
    public int MinPinLength { get; init; }

    /// <summary>
    /// The effective static PIN after the change, or null if none is configured. This is a
    /// secret: the app is expected to store it securely (the same expectation as the Pairing
    /// PSK), and it must never be logged. It is carried here — unlike
    /// <c>management/get-pairing-config</c>, which never returns a configured secret — because
    /// without it the app cannot persist a PIN the server just rotated.
    /// </summary>
    public string? StaticPin { get; init; }

    /// <summary>The effective <c>record_mode.psk_id</c> after the change, or null if unset.</summary>
    public string? RecordModePskId { get; init; }

    /// <summary>
    /// The effective <c>static_pin</c> <c>locations</c> hint after the change. Becomes
    /// <c>["operator"]</c> once a server sets the PIN, since the operator then owns the secret
    /// and any printed copy is stale.
    /// </summary>
    /// <remarks>
    /// Carried for the same reason as <see cref="StaticPin"/>: the app persists the rotated
    /// secret, and without the hint beside it the next start would re-advertise the factory
    /// location for a PIN that is no longer the factory's. Seed
    /// <see cref="ClientCapabilities.StaticPinLocations"/> from this on restart (#129).
    /// </remarks>
    public IReadOnlyList<string> StaticPinLocations { get; init; } = [];

    /// <summary>
    /// The effective <c>pairing_psk</c> <c>locations</c> hint after the change. Same rule as
    /// <see cref="StaticPinLocations"/>, and likewise only a server-supplied PSK flips it — a
    /// PSK this client minted does not.
    /// </summary>
    public IReadOnlyList<string> PairingPskLocations { get; init; } = [];
}
