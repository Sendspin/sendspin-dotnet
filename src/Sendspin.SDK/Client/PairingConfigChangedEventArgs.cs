namespace Sendspin.SDK.Client;

/// <summary>
/// A server changed this client's pairing configuration through
/// <c>management/set-pairing-config</c>. The SDK applies the change to its own state and
/// raises this so the app can persist it; the SDK does not write to the
/// <see cref="ClientCapabilities"/> instance the app owns.
/// </summary>
public sealed class PairingConfigChangedEventArgs : EventArgs
{
    /// <summary>The effective unpaired-access setting after the change.</summary>
    public bool UnpairedAccessEnabled { get; init; }

    /// <summary>
    /// True when the server replaced this client's Pairing PSK, so any pairing token
    /// previously obtained from <see cref="ISendspinClient.EnsurePairingPsk"/> is stale.
    /// </summary>
    public bool PairingPskReplaced { get; init; }
}
