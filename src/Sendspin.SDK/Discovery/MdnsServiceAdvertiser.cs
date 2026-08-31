using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Makaretu.Dns;
using Microsoft.Extensions.Logging;

namespace Sendspin.SDK.Discovery;

/// <summary>
/// Advertises this client as a Sendspin service via mDNS.
/// This enables server-initiated connections where Sendspin servers
/// discover and connect to this client.
/// </summary>
public sealed class MdnsServiceAdvertiser : IAsyncDisposable
{
    private readonly ILogger<MdnsServiceAdvertiser> _logger;
    private readonly AdvertiserOptions _options;
    private readonly object _announceLock = new object();
    private MulticastService? _mdns;
    private ServiceDiscovery? _serviceDiscovery;
    private ServiceProfile? _serviceProfile;
    private bool _disposed;

    /// <summary>
    /// Whether the service is currently being advertised.
    /// </summary>
    public bool IsAdvertising { get; private set; }

    /// <summary>
    /// The DNS-SD instance name being advertised.
    /// </summary>
    public string InstanceName => _options.InstanceName;

    public MdnsServiceAdvertiser(ILogger<MdnsServiceAdvertiser> logger, AdvertiserOptions? options = null)
    {
        _logger = logger;
        _options = options ?? new AdvertiserOptions();
    }

    /// <summary>
    /// Starts advertising this client as a Sendspin service.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsAdvertising)
        {
            _logger.LogWarning("Already advertising");
            return Task.CompletedTask;
        }

        try
        {
            // Create the multicast DNS service
            _mdns = new MulticastService();

            // Announce whenever the interface set changes. MulticastService raises this
            // synchronously inside Start() once the send sockets exist, which is what makes it
            // the right announce trigger twice over: at start it is the earliest moment an
            // announcement can actually leave the machine (SendAnswer before Start is a silent
            // no-op), and an interface appearing later — Wi-Fi associating after boot, resume
            // from sleep — needs a fresh announcement because the one sent at start never
            // reached it.
            _mdns.NetworkInterfaceDiscovered += (s, e) =>
            {
                foreach (var nic in e.NetworkInterfaces)
                {
                    _logger.LogDebug("mDNS using network interface: {Name} ({Id})",
                        nic.Name, nic.Id);
                }

                HandleNetworkInterfaceDiscovered();
            };

            // Log when queries are received (helps debug if mDNS is working)
            var queryCount = 0;
            _mdns.QueryReceived += (s, e) =>
            {
                foreach (var q in e.Message.Questions)
                {
                    // Log sendspin queries with high priority
                    if (q.Name.ToString().Contains("sendspin", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("*** Received mDNS query for SENDSPIN: {Name} (type={Type})",
                            q.Name, q.Type);
                    }
                    // Log first few queries to verify mDNS is working
                    else if (queryCount < 5)
                    {
                        _logger.LogDebug("Received mDNS query: {Name} (type={Type})",
                            q.Name, q.Type);
                        queryCount++;
                    }
                }
            };

            // Create service discovery for advertising
            _serviceDiscovery = new ServiceDiscovery(_mdns);

            // Get local IP addresses - filter out link-local addresses
            var addresses = GetLocalIPAddresses()
                .Where(ip => !ip.ToString().StartsWith("169.254.")) // Skip APIPA
                .ToList();

            _logger.LogInformation("Local IP addresses for mDNS: {Addresses}",
                string.Join(", ", addresses));

            if (addresses.Count == 0)
            {
                throw new InvalidOperationException("No valid network addresses found for mDNS advertising");
            }

            // Service type _sendspin._tcp.local., instance name = DNS-SD instance label.
            _serviceProfile = new ServiceProfile(
                instanceName: _options.InstanceName,
                serviceName: "_sendspin._tcp",
                port: (ushort)_options.Port,
                addresses: addresses);

            if (!string.IsNullOrEmpty(_options.PlayerName))
            {
                _serviceProfile.AddProperty("name", _options.PlayerName);
            }

            _serviceProfile.AddProperty("path", _options.Path);

            _logger.LogInformation(
                "mDNS Service Profile: FullName={FullName}, ServiceName={Service}, HostName={Host}, Port={Port}",
                _serviceProfile.FullyQualifiedName,
                _serviceProfile.ServiceName,
                _serviceProfile.HostName,
                _options.Port);

            foreach (var resource in _serviceProfile.Resources)
            {
                _logger.LogDebug("mDNS Resource: {Type} {Name}",
                    resource.GetType().Name, resource.Name);
            }

            // Advertise the service
            _serviceDiscovery.Advertise(_serviceProfile);

            // Start the multicast service
            _mdns.Start();

            IsAdvertising = true;
            _logger.LogInformation(
                "Advertising Sendspin client: {InstanceName} on port {Port} (path={Path})",
                _options.InstanceName, _options.Port, _options.Path);

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start mDNS advertising");
            throw;
        }
    }

    /// <summary>
    /// Stops advertising the service.
    /// </summary>
    public Task StopAsync()
    {
        if (!IsAdvertising)
            return Task.CompletedTask;

        _logger.LogInformation("Stopping mDNS advertisement for {InstanceName}", _options.InstanceName);

        try
        {
            if (_serviceProfile != null && _serviceDiscovery != null)
            {
                _serviceDiscovery.Unadvertise(_serviceProfile);
            }

            _mdns?.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping mDNS service");
        }
        finally
        {
            _serviceDiscovery = null;
            _serviceProfile = null;
            _mdns?.Dispose();
            _mdns = null;
            IsAdvertising = false;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Multicasts the service's records unsolicited. Advertise() alone only registers a passive
    /// query responder — it sends nothing — and a server whose mDNS browser has finished its
    /// startup queries never asks again (python-zeroconf switches to refresh-only scheduling
    /// after four), so a client that does not announce is never discovered by a server that
    /// started before it. The spec's discovery section opens with "Clients announce their
    /// presence via mDNS", and RFC 6762 §8.3 requires the unsolicited responses outright.
    /// Internal rather than private so a test can stand in for the interface-change event it is
    /// wired to, which nothing short of real NIC churn can raise.
    /// </summary>
    internal void HandleNetworkInterfaceDiscovered()
    {
        // Snapshot: StopAsync nulls the fields, and announcing races it by design.
        var serviceDiscovery = _serviceDiscovery;
        var profile = _serviceProfile;
        if (serviceDiscovery is null || profile is null)
        {
            return;
        }

        // Off-thread because Announce blocks for the RFC-mandated second between its two
        // sends; serialized so overlapping triggers cannot interleave their packets inside
        // that window.
        Task.Run(() =>
        {
            lock (_announceLock)
            {
                try
                {
                    serviceDiscovery.Announce(profile);
                    _logger.LogDebug("Announced mDNS presence for {InstanceName}", _options.InstanceName);
                }
                catch (Exception ex)
                {
                    // Best-effort: the query responder still works, and StopAsync tearing the
                    // transport down under an in-flight announce lands here too.
                    _logger.LogWarning(ex, "Failed to announce mDNS presence for {InstanceName}", _options.InstanceName);
                }
            }
        });
    }

    /// <summary>
    /// Gets local IPv4 addresses for this machine, preferring interfaces with a default gateway.
    /// This filters out virtual adapters (Hyper-V, WSL, Docker) that aren't reachable from the LAN.
    /// </summary>
    private IEnumerable<IPAddress> GetLocalIPAddresses()
    {
        var gatewayAddresses = new List<IPAddress>();
        var allAddresses = new List<IPAddress>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;

            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            var props = ni.GetIPProperties();
            var hasGateway = props.GatewayAddresses
                .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                       && !g.Address.Equals(IPAddress.Any));

            foreach (var addr in props.UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    allAddresses.Add(addr.Address);
                    if (hasGateway)
                    {
                        gatewayAddresses.Add(addr.Address);
                    }
                }
            }
        }

        // Prefer interfaces with a gateway (connected to a real network).
        // Fall back to all addresses only if no gateway interfaces exist.
        var result = gatewayAddresses.Count > 0 ? gatewayAddresses : allAddresses;
        foreach (var addr in result)
        {
            yield return addr;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await StopAsync();
    }
}

/// <summary>
/// Configuration options for mDNS service advertising.
/// </summary>
public sealed class AdvertiserOptions
{
    /// <summary>
    /// Whether to advertise via mDNS at all. Disable for environments without
    /// multicast support (containers, some CI) or when discovery is handled externally.
    /// Default: true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The DNS-SD service instance label (the first component of the advertised
    /// <c>&lt;instance&gt;._sendspin._tcp.local.</c> name). This is not a protocol identifier —
    /// the Sendspin spec's client mDNS advertisement carries no <c>client_id</c> at all, only
    /// the service type, port, and the <c>path</c>/<c>name</c> TXT records.
    /// Default: sendspin-windows-{hostname}
    /// </summary>
    public string InstanceName { get; set; } = $"sendspin-windows-{Environment.MachineName.ToLowerInvariant()}";

    /// <summary>
    /// Human-readable player name (advertised in TXT record as "name").
    /// Allows servers to display a friendly name during mDNS discovery,
    /// before the WebSocket handshake occurs.
    /// Default: machine name
    /// </summary>
    public string PlayerName { get; set; } = Environment.MachineName;

    /// <summary>
    /// Port the WebSocket server is listening on.
    /// Default: 8928
    /// </summary>
    public int Port { get; set; } = 8928;

    /// <summary>
    /// WebSocket endpoint path (advertised in TXT record).
    /// Default: /sendspin
    /// </summary>
    public string Path { get; set; } = "/sendspin";
}
