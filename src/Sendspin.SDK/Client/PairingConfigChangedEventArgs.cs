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
    public bool DynamicPairingCodeEnabled { get; init; }

    /// <summary>The effective <c>static_pin</c> enabled setting after the change.</summary>
    public bool StaticPairingCodeEnabled { get; init; }

    /// <summary>The effective minimum pairing code length after the change.</summary>
    public int MinPairingCodeLength { get; init; }

    /// <summary>
    /// The effective static pairing code after the change, or null if none is configured. This is a
    /// secret: the app is expected to store it securely (the same expectation as the Pairing
    /// PSK), and it must never be logged. It is carried here — unlike
    /// <c>management/get-pairing-config</c>, which never returns a configured secret — because
    /// without it the app cannot persist a pairing code the server just rotated.
    /// </summary>
    public string? StaticPairingCode { get; init; }

    /// <summary>The effective <c>record_mode.psk_id</c> after the change, or null if unset.</summary>
    public string? RecordModePskId { get; init; }

    /// <summary>
    /// The effective <c>static_pin</c> <c>locations</c> hint after the change. Becomes
    /// <c>["operator"]</c> once a server sets the pairing code, since the operator then owns the secret
    /// and any printed copy is stale.
    /// </summary>
    /// <remarks>
    /// Carried for the same reason as <see cref="StaticPairingCode"/>: the app persists the rotated
    /// secret, and without the hint beside it the next start would re-advertise the factory
    /// location for a pairing code that is no longer the factory's. Seed
    /// <see cref="ClientCapabilities.StaticPairingCodeLocations"/> from this on restart (#129).
    /// </remarks>
    public IReadOnlyList<string> StaticPairingCodeLocations { get; init; } = [];

    /// <summary>
    /// The effective <c>pairing_psk</c> <c>locations</c> hint after the change. Same rule as
    /// <see cref="StaticPairingCodeLocations"/>, and likewise only a server-supplied PSK flips it — a
    /// PSK this client minted does not.
    /// </summary>
    public IReadOnlyList<string> PairingPskLocations { get; init; } = [];
}
