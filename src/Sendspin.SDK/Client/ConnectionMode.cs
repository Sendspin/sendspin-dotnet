namespace Sendspin.SDK.Client;

/// <summary>
/// Records which connection method an embedder intends to use.
/// </summary>
/// <remarks>
/// The SDK does not read this enum. The method is selected structurally, by the service
/// you run: <see cref="SendspinHostService"/> advertises and accepts server-initiated
/// connections, while <see cref="SendspinClientService"/> dials a server that discovery
/// found. This type only records that intent, for an embedder's own configuration and
/// logging; nothing in the SDK enforces it.
/// </remarks>
public enum ConnectionMode
{
    /// <summary>
    /// Both discover servers and advertise as a player. This describes a spec violation:
    /// connection.md requires a client to use exactly one connection method at a time, so
    /// no client should run in this mode. Use <see cref="AdvertiseOnly"/> or
    /// <see cref="DiscoverOnly"/> instead.
    /// <para>
    /// It remains the zero value on this release line, so an unset field still resolves to
    /// it — set the mode explicitly. It is removed in 10.0.0, where
    /// <see cref="AdvertiseOnly"/> becomes the zero value.
    /// </para>
    /// </summary>
    [Obsolete("Auto describes a spec violation: connection.md requires a client to use exactly one connection method at a time. Use AdvertiseOnly or DiscoverOnly. Removed in 10.0.0.")]
    Auto,

    /// <summary>
    /// Only advertise via mDNS and wait for servers to connect.
    /// connection.md requires a client to use exactly one connection method at a time,
    /// so a client in this mode must not also discover and dial servers.
    /// Equivalent to the Python CLI's daemon mode.
    /// </summary>
    AdvertiseOnly,

    /// <summary>
    /// Only discover servers via mDNS and connect to them.
    /// connection.md requires a client to use exactly one connection method at a time,
    /// so a client in this mode must not also advertise or listen for incoming
    /// connections.
    /// </summary>
    DiscoverOnly
}
