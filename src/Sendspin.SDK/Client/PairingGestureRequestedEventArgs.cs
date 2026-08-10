namespace Sendspin.SDK.Client;

/// <summary>
/// Raised when a pairing attempt is gesture-gated and no <see cref="PairingWindow"/> is open,
/// so the application can prompt its operator to perform the gesture.
/// </summary>
/// <remarks>
/// Raised once per gated activation, at the moment <c>client/pair-pending</c> is sent. It is
/// not re-raised when a window opens and is claimed by another connection, so a prompt maps
/// one-to-one with a pending activation.
/// </remarks>
public sealed class PairingGestureRequestedEventArgs : EventArgs
{
    /// <summary>The pairing method awaiting the gesture: static_pin or dynamic_pin.</summary>
    required public string Method { get; init; }

    /// <summary>The activation's pairing index.</summary>
    required public int PairingIndex { get; init; }
}
