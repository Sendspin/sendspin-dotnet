namespace Sendspin.SDK.Client;

/// <summary>
/// Placeholder for the device-level pairing window (a later task in the pairing-window plan
/// implements the real open/close/consume-once state machine and lifetime expiry). Declared
/// now, empty, only so <see cref="SendspinClientOptions"/> and test harnesses that anticipate
/// it can already reference the type by name; nothing reads or writes an instance of it yet.
/// </summary>
public sealed class PairingWindow
{
}
