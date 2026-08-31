using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Client;

/// <summary>
/// The priority class of a server connection for multi-server arbitration, per the
/// spec's "Multiple servers" section: connections rank by their highest declared
/// activity — management &gt; playback &gt; pairing &gt; empty.
/// </summary>
public enum ConnectionPriority
{
    /// <summary>Empty activity set: no recognized activity declared.</summary>
    Empty = 0,

    /// <summary>A pairing attempt.</summary>
    Pairing = 1,

    /// <summary>Playback.</summary>
    Playback = 2,

    /// <summary>Management.</summary>
    Management = 3,
}

/// <summary>
/// How an arbitration loss is delivered to the losing connection.
/// </summary>
internal enum ArbitrationFarewell
{
    /// <summary>client/goodbye carrying the result's reason.</summary>
    Goodbye,

    /// <summary>pair/abort, for a losing connection that is a pairing handshake.</summary>
    PairAbort,
}

/// <summary>
/// Pure, side-effect-free multi-server arbitration decision used by
/// <see cref="SendspinHostService"/>. Implements the spec's decision table:
/// the incoming connection is accepted when its priority is higher than or equal to
/// the current holder's, with two exceptions — a pairing attempt is not displaced by
/// incoming playback or pairing, and an empty-vs-empty tie admits the incoming
/// connection only when it is the persisted last-playback server. Outside that table,
/// a client-initiated holder is never displaced: connection.md specifies multi-server
/// behaviour for server-initiated connections only and leaves the client-initiated side
/// implementation-defined. A displaced holder
/// receives goodbye 'another_server'; a rejected incoming receives
/// 'concurrent_attempt'; a loser that is a pairing handshake receives pair/abort
/// 'concurrent_attempt' instead of either.
/// </summary>
internal static class ServerArbitration
{
    private const string AnotherServer = "another_server";
    private const string UserRequest = "user_request";
    private const string ConcurrentAttempt = "concurrent_attempt";

    /// <summary>
    /// Maps a server/activate activities set to its priority class: the highest-ranked
    /// declared activity, or empty for an empty set.
    /// </summary>
    internal static ConnectionPriority FromActivities(IReadOnlyCollection<string>? activities)
    {
        if (activities is null || activities.Count == 0)
        {
            return ConnectionPriority.Empty;
        }

        // The Activities constants rather than literals: this is the same wire vocabulary the
        // rest of the SDK matches against, and a literal here would not follow a change to it
        // (#93).
        if (activities.Contains(Activities.Management))
        {
            return ConnectionPriority.Management;
        }

        if (activities.Contains(Activities.Playback))
        {
            return ConnectionPriority.Playback;
        }

        return activities.Contains(Activities.Pairing) ? ConnectionPriority.Pairing : ConnectionPriority.Empty;
    }

    /// <summary>
    /// Decides whether a newly-handshaked server should become the active connection.
    /// </summary>
    /// <param name="newServerId">server_id of the newly connected server.</param>
    /// <param name="newPriority">The incoming connection's priority class.</param>
    /// <param name="existingServerId">server_id of the current holder, or null if none.</param>
    /// <param name="existingPriority">The current holder's priority class.</param>
    /// <param name="lastPlayedServerId">Persisted last-playback server_id, or null.</param>
    /// <param name="existingIsClientInitiated">
    /// True when the current holder is a session this client dialled rather than one that
    /// dialled in — see <see cref="SendspinHostService.AdoptClientInitiated"/>. Such a holder is
    /// never displaced. Last, with a default, so the server-initiated callers that make up the
    /// spec's own table read unchanged.
    /// </param>
    internal static ArbitrationResult Decide(
        string newServerId,
        ConnectionPriority newPriority,
        string? existingServerId,
        ConnectionPriority existingPriority,
        string? lastPlayedServerId,
        bool existingIsClientInitiated = false)
    {
        var result = Rank(
            newServerId, newPriority, existingServerId, existingPriority, lastPlayedServerId, existingIsClientInitiated);

        // connection.md: a displaced or rejected connection that is a pairing handshake is told
        // pair/abort 'concurrent_attempt' rather than client/goodbye. A goodbye is not something
        // a pairing state machine processes as an attempt teardown, and pairing.md gives this
        // reason the close-after-send semantics arbitration needs (#203). The ranking above is
        // untouched: only how the loss is delivered, and under which reason, changes here.
        //
        // This covers the same-server-reconnect branch too, where the stale socket is displaced
        // under 'user_request'. That reason exists to avoid telling the SAME server
        // 'another_server', a distinction only client/goodbye draws — pair/abort has no such
        // reason, and a displaced pairing handshake is a displaced pairing handshake however the
        // successor identifies itself.
        bool loserIsPairing = result.LoserReason is not null
            && (result.AcceptNew ? existingPriority : newPriority) == ConnectionPriority.Pairing;

        return loserIsPairing
            ? result with { LoserReason = ConcurrentAttempt, LoserFarewell = ArbitrationFarewell.PairAbort }
            : result;
    }

    /// <summary>
    /// The priority ranking itself: the spec's decision table and its two exceptions, in
    /// client/goodbye terms. <see cref="Decide"/> re-routes a pairing loser afterwards.
    /// </summary>
    private static ArbitrationResult Rank(
        string newServerId,
        ConnectionPriority newPriority,
        string? existingServerId,
        ConnectionPriority existingPriority,
        string? lastPlayedServerId,
        bool existingIsClientInitiated)
    {
        // No existing connection — accept unconditionally.
        if (existingServerId is null)
        {
            return new ArbitrationResult(true, null, "no existing connection");
        }

        // A session this client dialled is not displaced by a server dialling in, whatever the
        // two declare. The spec's ranking exists so servers can arbitrate among themselves —
        // "servers cannot reclaim clients by reconnecting", so how a client handles server
        // selection is left to the client — and letting an incoming server outrank the session
        // an operator explicitly chose would silently override that choice, with nothing to
        // restore it. This sits ABOVE the same-server branch deliberately: a dialled session is
        // not a stale socket for its own server to reclaim by dialling in.
        if (existingIsClientInitiated)
        {
            return new ArbitrationResult(false, ConcurrentAttempt, "client-initiated holder is not displaced");
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
/// <param name="LoserReason">
/// Reason for the losing connection (the existing one when <paramref name="AcceptNew"/>
/// is true and one exists; the new one when false), or null when there is no loser. Carried
/// by whichever message <see cref="ArbitrationResult.LoserFarewell"/> names.
/// </param>
/// <param name="Rationale">Human-readable decision summary for logging.</param>
internal readonly record struct ArbitrationResult(bool AcceptNew, string? LoserReason, string Rationale)
{
    /// <summary>Which message delivers the loss; client/goodbye unless set otherwise.</summary>
    internal ArbitrationFarewell LoserFarewell { get; init; }
}
