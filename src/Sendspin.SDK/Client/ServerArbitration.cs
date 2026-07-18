namespace Sendspin.SDK.Client;

/// <summary>
/// The priority class of a server connection for multi-server arbitration, per the
/// spec's "Multiple servers" section: connections rank by their highest declared
/// activity — management &gt; playback &gt; pairing &gt; empty.
/// </summary>
public enum ConnectionPriority
{
    /// <summary>Empty activity set (legacy connection_reason 'discovery' or absent).</summary>
    Empty = 0,

    /// <summary>A pairing attempt.</summary>
    Pairing = 1,

    /// <summary>Playback (legacy connection_reason 'playback').</summary>
    Playback = 2,

    /// <summary>Management.</summary>
    Management = 3,
}

/// <summary>
/// Pure, side-effect-free multi-server arbitration decision used by
/// <see cref="SendspinHostService"/>. Implements the spec's decision table:
/// the incoming connection is accepted when its priority is higher than or equal to
/// the current holder's, with two exceptions — a pairing attempt is not displaced by
/// incoming playback or pairing, and an empty-vs-empty tie admits the incoming
/// connection only when it is the persisted last-playback server. A displaced holder
/// receives goodbye 'another_server'; a rejected incoming receives
/// 'concurrent_attempt'.
/// </summary>
internal static class ServerArbitration
{
    private const string AnotherServer = "another_server";
    private const string UserRequest = "user_request";
    private const string ConcurrentAttempt = "concurrent_attempt";

    /// <summary>
    /// Maps a legacy connection_reason to its priority class: 'playback' ranks as
    /// playback; anything else (including 'discovery' and absent) as empty.
    /// </summary>
    internal static ConnectionPriority FromConnectionReason(string? reason)
        => string.Equals(reason ?? "discovery", "playback", StringComparison.OrdinalIgnoreCase)
            ? ConnectionPriority.Playback
            : ConnectionPriority.Empty;

    /// <summary>
    /// Decides whether a newly-handshaked server should become the active connection.
    /// </summary>
    /// <param name="newServerId">server_id of the newly connected server.</param>
    /// <param name="newPriority">The incoming connection's priority class.</param>
    /// <param name="existingServerId">server_id of the current holder, or null if none.</param>
    /// <param name="existingPriority">The current holder's priority class.</param>
    /// <param name="lastPlayedServerId">Persisted last-playback server_id, or null.</param>
    internal static ArbitrationResult Decide(
        string newServerId,
        ConnectionPriority newPriority,
        string? existingServerId,
        ConnectionPriority existingPriority,
        string? lastPlayedServerId)
    {
        // No existing connection — accept unconditionally.
        if (existingServerId is null)
        {
            return new ArbitrationResult(true, null, "no existing connection");
        }

        // Same server reconnecting — accept and drop the stale socket. user_request is the
        // spec-valid "do not auto-reconnect" reason: the peer is the SAME server (not
        // another_server) and the client is alive (not shutdown).
        if (string.Equals(newServerId, existingServerId, StringComparison.Ordinal))
        {
            return new ArbitrationResult(true, UserRequest, "same server reconnecting");
        }

        // Exception 1: a pairing attempt is not displaced by incoming playback or pairing.
        if (existingPriority == ConnectionPriority.Pairing
            && newPriority is ConnectionPriority.Playback or ConnectionPriority.Pairing)
        {
            return new ArbitrationResult(false, ConcurrentAttempt, "pairing attempt is not displaced");
        }

        // Exception 2: empty-vs-empty tie admits the incoming connection only when it is
        // the persisted last-playback server.
        if (newPriority == ConnectionPriority.Empty && existingPriority == ConnectionPriority.Empty)
        {
            if (lastPlayedServerId is not null
                && string.Equals(newServerId, lastPlayedServerId, StringComparison.Ordinal))
            {
                return new ArbitrationResult(true, AnotherServer, "new server matches last-playback (empty tie)");
            }

            return new ArbitrationResult(false, ConcurrentAttempt, "existing holder kept (empty tie)");
        }

        // General rule: higher or equal priority is accepted, lower is rejected.
        if (newPriority >= existingPriority)
        {
            return new ArbitrationResult(true, AnotherServer,
                $"incoming priority {newPriority} >= holder {existingPriority}");
        }

        return new ArbitrationResult(false, ConcurrentAttempt,
            $"incoming priority {newPriority} < holder {existingPriority}");
    }
}

/// <summary>
/// Outcome of <see cref="ServerArbitration.Decide"/>.
/// </summary>
/// <param name="AcceptNew">True to accept the new server; false to reject it.</param>
/// <param name="LoserGoodbyeReason">
/// client/goodbye reason for the losing connection (the existing one when <paramref name="AcceptNew"/>
/// is true and one exists; the new one when false), or null when there is no loser.
/// </param>
/// <param name="Rationale">Human-readable decision summary for logging.</param>
internal readonly record struct ArbitrationResult(bool AcceptNew, string? LoserGoodbyeReason, string Rationale);
