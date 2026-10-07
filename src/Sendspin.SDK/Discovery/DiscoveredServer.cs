namespace Sendspin.SDK.Discovery;

/// <summary>
/// Represents a Sendspin server discovered via mDNS.
/// </summary>
public sealed class DiscoveredServer
{
    /// <summary>
    /// A key for this advertisement, for telling discovered servers apart and remembering a
    /// choice between runs: the mDNS instance name and first IP address, joined by a hyphen
    /// (or the value of a non-spec <c>id</c> or <c>server_id</c> TXT key, if a server
    /// publishes one).
    /// </summary>
    /// <remarks>
    /// Not the server's identity. Nothing authenticates an mDNS response, so any device on the
    /// network can advertise any value here. The authenticated identity is the server's Noise
    /// static key, known only once connected (<c>SendspinClientService.ServerId</c>), and that
    /// is the id the host arbitrates and records the last-played server under.
    /// </remarks>
    required public string ServerId { get; init; }

    /// <summary>
    /// Human-readable server name.
    /// </summary>
    required public string Name { get; init; }

    /// <summary>
    /// Server hostname.
    /// </summary>
    required public string Host { get; init; }

    /// <summary>
    /// Server port number.
    /// </summary>
    required public int Port { get; init; }

    /// <summary>
    /// IP addresses for the server.
    /// </summary>
    required public IReadOnlyList<string> IpAddresses { get; init; }

    /// <summary>
    /// The value of a <c>version</c> TXT key, which the spec does not define; null from a
    /// server that publishes only the spec's keys.
    /// </summary>
    public string? ProtocolVersion { get; init; }

    /// <summary>
    /// Additional properties from TXT records.
    /// </summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Time when this server was first discovered.
    /// </summary>
    public DateTimeOffset DiscoveredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Time when this server was last seen in discovery.
    /// </summary>
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets the WebSocket URI for connecting to this server.
    /// </summary>
    public Uri GetWebSocketUri()
    {
        // Prefer IP address over hostname for reliability
        var address = IpAddresses.FirstOrDefault() ?? Host;

        // Use path from TXT record if available, otherwise default to /sendspin
        var rawPath = Properties.TryGetValue("path", out var p) ? p : "/sendspin";
        var path = MdnsServerDiscovery.SanitizePath(rawPath);

        return new Uri($"ws://{address}:{Port}{path}");
    }

    public override string ToString() => $"{Name} ({Host}:{Port})";

    public override bool Equals(object? obj)
    {
        return obj is DiscoveredServer other
               && ServerId == other.ServerId
               && Host == other.Host
               && Port == other.Port
               && IpAddresses.SequenceEqual(other.IpAddresses);
    }

    public override int GetHashCode() => HashCode.Combine(ServerId, Host, Port);
}
