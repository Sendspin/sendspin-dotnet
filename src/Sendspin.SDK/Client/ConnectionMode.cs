namespace Sendspin.SDK.Client;

/// <summary>
/// Records which of the two connection methods an embedder intends to use.
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
    /// Only advertise via mDNS and wait for servers to connect. The default.
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
