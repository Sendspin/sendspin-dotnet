namespace Sendspin.SDK.Client;

/// <summary>
/// Optional persistence seam for the "last-playback server" — the server_id of the server that most
/// recently held the admitted connection while 'playback' was among its activities. Mirrors
/// <see cref="IOutputDelayStore"/>.
/// </summary>
/// <remarks>
/// The Sendspin spec requires clients to persist this across restarts so multi-server arbitration can
/// prefer the last-played server in a tie. Because the SDK is a library and cannot choose a storage
/// location, the embedder implements this interface (file, registry, database, etc.) and supplies it to
/// <see cref="SendspinHostService"/>. Implementations should be fast and non-throwing; calls happen on
/// the connection/arbitration path. When no store is provided, the SDK keeps its previous behavior and
/// the embedder may seed the value via the constructor and observe changes via
/// <c>LastPlayedServerIdChanged</c>.
/// </remarks>
public interface ILastPlayedServerStore
{
    /// <summary>
    /// Loads the persisted last-played server_id, or <c>null</c> if none has been stored.
    /// Called once at host-service construction.
    /// </summary>
    string? Load();

    /// <summary>
    /// Persists the last-played server_id. Called whenever the last-played server changes — when an
    /// admitted connection declares the 'playback' activity, or when the embedder calls
    /// <see cref="SendspinHostService.SetLastPlayedServerId"/> directly.
    /// </summary>
    /// <param name="serverId">The server_id to persist as the last-played server.</param>
    void Save(string serverId);
}
