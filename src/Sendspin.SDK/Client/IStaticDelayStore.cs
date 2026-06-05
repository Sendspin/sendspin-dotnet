namespace Sendspin.SDK.Client;

/// <summary>
/// Optional persistence seam for a player's <c>static_delay_ms</c>.
/// </summary>
/// <remarks>
/// The Sendspin spec requires clients to persist <c>static_delay_ms</c> locally across reboots and
/// server reconnections. Because the SDK is a library and cannot choose a storage location, the
/// embedder implements this interface (file, registry, database, etc.) and supplies it to
/// <see cref="SendspinClientService"/>. When no store is provided, the SDK keeps its previous
/// behavior and the embedder is responsible for re-supplying the delay on each connection.
/// <para>
/// Implementations should be fast and non-throwing; calls happen on the connection/handshake path.
/// The SDK persists a single output's delay. Embedders that vary delay per audio output should key
/// their store by the active output and hand the SDK a view for the current one.
/// </para>
/// </remarks>
public interface IStaticDelayStore
{
    /// <summary>
    /// Loads the persisted static delay in milliseconds, or <c>null</c> if none has been stored.
    /// Called when a connection is established, before the initial client/state is sent.
    /// </summary>
    double? Load();

    /// <summary>
    /// Persists the static delay in milliseconds. Called whenever the delay changes (e.g. an
    /// inbound <c>set_static_delay</c> command or a GroupSync calibration offset).
    /// </summary>
    /// <param name="staticDelayMs">
    /// The static delay to persist, in milliseconds. May be negative when sourced from a GroupSync
    /// calibration offset (which schedules audio later); the <c>set_static_delay</c> command path is
    /// always non-negative. Store and round-trip the value as-is.
    /// </param>
    void Save(double staticDelayMs);
}
