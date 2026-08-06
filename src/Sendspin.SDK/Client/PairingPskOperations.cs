using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Connection.Noise.Pairing;

namespace Sendspin.SDK.Client;

/// <summary>
/// The ensure/rotate Pairing PSK operations, shared by <see cref="SendspinClientService"/>
/// and <see cref="SendspinHostService"/> so both surfaces mint identical tokens over the
/// same store and identity. Each store operation here is individually thread-safe (the
/// shipped stores lock internally); callers serialize multi-step sequences with their own
/// lock.
/// </summary>
internal static class PairingPskOperations
{
    /// <summary>
    /// Returns the pairing token for the stored Pairing PSK, generating and persisting one
    /// if none is stored.
    /// </summary>
    /// <exception cref="InvalidOperationException">No record store is configured.</exception>
    internal static string Ensure(IPairingRecordStore? store, SendspinIdentity identity)
    {
        var records = RequireStore(store);
        var record = records.List().FirstOrDefault(r => r.Category == PskCategory.Pairing);
        if (record is null)
        {
            byte[] psk = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            record = new PairingRecord(psk, PskCategory.Pairing);
            records.Upsert(record);
        }

        return PairingToken.Encode(identity.PublicKey.Span, record.Psk.Span);
    }

    /// <summary>
    /// Replaces the stored Pairing PSK with a freshly generated one and returns the new token.
    /// </summary>
    /// <exception cref="InvalidOperationException">No record store is configured.</exception>
    internal static string Rotate(IPairingRecordStore? store, SendspinIdentity identity)
    {
        var records = RequireStore(store);

        // Remove every Pairing record, exactly as the management/set-pairing-config handler
        // does: a leftover second record would make Ensure non-deterministic about which
        // token it returns.
        foreach (var old in records.List().Where(r => r.Category == PskCategory.Pairing))
        {
            records.Remove(old.PskId);
        }

        byte[] fresh = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        records.Upsert(new PairingRecord(fresh, PskCategory.Pairing));
        return PairingToken.Encode(identity.PublicKey.Span, fresh);
    }

    private static IPairingRecordStore RequireStore(IPairingRecordStore? store) =>
        store ?? throw new InvalidOperationException(
            "No pairing record store is configured, so a generated Pairing PSK could not " +
            "be persisted. Set SendspinClientOptions.PairingRecordStore.");
}
