namespace Sendspin.SDK.Client;

/// <summary>
/// Pure, side-effect-free multi-server arbitration decision used by <see cref="SendspinHostService"/>.
/// Extracted so the spec's decision table can be unit-tested exhaustively without connection/socket I/O.
/// </summary>
internal static class ServerArbitration
{
    private const string Playback = "playback";
    private const string Discovery = "discovery";
    private const string AnotherServer = "another_server";
    private const string UserRequest = "user_request";

    /// <summary>
    /// Decides whether a newly-handshaked server should become the active connection.
    /// </summary>
    /// <param name="newServerId">server_id of the newly connected server.</param>
    /// <param name="newReason">connection_reason of the new server (null treated as discovery).</param>
    /// <param name="existingServerId">server_id of the current active server, or null if none.</param>
    /// <param name="existingReason">connection_reason of the existing server (null treated as discovery).</param>
    /// <param name="lastPlayedServerId">Persisted last-played server_id, or null.</param>
    internal static ArbitrationResult Decide(
        string newServerId,
        string? newReason,
        string? existingServerId,
        string? existingReason,
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

        var newIsPlayback = IsPlayback(newReason);
        var existingIsPlayback = IsPlayback(existingReason);

        if (newIsPlayback && !existingIsPlayback)
        {
            return new ArbitrationResult(true, AnotherServer, "new server has playback reason");
        }

        if (existingIsPlayback && !newIsPlayback)
        {
            return new ArbitrationResult(false, AnotherServer, "existing server has playback reason");
        }

        // Tie (both playback or both discovery): prefer the last-played server, else keep existing.
        if (lastPlayedServerId is not null
            && string.Equals(newServerId, lastPlayedServerId, StringComparison.Ordinal))
        {
            return new ArbitrationResult(true, AnotherServer, "new server matches last-played (tie-break)");
        }

        return new ArbitrationResult(
            false,
            AnotherServer,
            lastPlayedServerId is not null
                ? "existing server wins tie-break (new is not last-played)"
                : "existing server wins tie-break (no last-played set)");
    }

    private static bool IsPlayback(string? reason)
        => string.Equals(reason ?? Discovery, Playback, StringComparison.OrdinalIgnoreCase);
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
