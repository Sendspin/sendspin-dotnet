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
    /// <exception cref="InvalidOperationException">
    /// No record store is configured, or no PSK with an unused <c>psk_id</c> could be
    /// generated — this call is from an app thread, so an exception is the channel; a token
    /// for a PSK that was never stored could never authenticate.
    /// </exception>
    internal static string Ensure(IPairingRecordStore? store, SendspinIdentity identity)
    {
        var records = RequireStore(store);
        var record = records.List().FirstOrDefault(r => r.Category == PskCategory.Pairing);
        if (record is null)
        {
            record = new PairingRecord(PairingRecords.GenerateUniquePsk(records), PskCategory.Pairing);
            records.Upsert(record);
        }

        return PairingToken.Encode(identity.PublicKey.Span, record.Psk.Span);
    }

    /// <summary>
    /// Replaces the stored Pairing PSK with a freshly generated one and returns the new token.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No record store is configured, or the rotated PSK could not be persisted
    /// (see <see cref="Ensure"/>).
    /// </exception>
    internal static string Rotate(IPairingRecordStore? store, SendspinIdentity identity)
    {
        var records = RequireStore(store);

        // Remove every Pairing record first. This call has no protocol round-trip — it
        // must return a token or throw — and the new record's psk_id differs from the old one's
        // (derived from the PSK), so upserting first would need transient capacity for N+1
        // records. On a store already at its limit, that would make rotation impossible even
        // though it is a like-for-like replacement. Removing first frees the slot, so a
        // rotation can succeed at all on a capacity-bounded store; the cost is the asymmetric
        // one — a failure between Remove and Upsert (an IO fault) leaves the client with no
        // Pairing PSK, surfaced by the exception rather than masked. A leftover second record
        // would also make Ensure non-deterministic about which token it returns, which this
        // loop rules out regardless.
        foreach (var old in records.List().Where(r => r.Category == PskCategory.Pairing))
        {
            records.Remove(old.PskId);
        }

        byte[] fresh = PairingRecords.GenerateUniquePsk(records);
        records.Upsert(new PairingRecord(fresh, PskCategory.Pairing));

        return PairingToken.Encode(identity.PublicKey.Span, fresh);
    }

    private static IPairingRecordStore RequireStore(IPairingRecordStore? store) =>
        store ?? throw new InvalidOperationException(
            "No pairing record store is configured, so a generated Pairing PSK could not " +
            "be persisted. Set SendspinClientOptions.PairingRecordStore.");
}
