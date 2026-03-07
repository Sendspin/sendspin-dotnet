using Sendspin.SDK.Discovery;

namespace Sendspin.SDK.Tests.Discovery;

public class DiscoveredServerTests
{
    private static DiscoveredServer CreateServer(
        string id = "server-1",
        string host = "test.local",
        int port = 8927,
        IReadOnlyList<string>? ips = null)
    {
        return new DiscoveredServer
        {
            ServerId = id,
            Name = "Test Server",
            Host = host,
            Port = port,
            IpAddresses = ips ?? new List<string> { "192.168.1.100" },
        };
    }

    [Fact]
    public void Equals_SameServerIdAndDetails_ReturnsTrue()
    {
        var a = CreateServer();
        var b = CreateServer();
        Assert.Equal(a, b);
    }

    [Fact]
    public void Equals_DifferentServerId_ReturnsFalse()
    {
        var a = CreateServer(id: "server-1");
        var b = CreateServer(id: "server-2");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Equals_SameIdDifferentPort_ReturnsFalse()
    {
        var a = CreateServer(port: 8927);
        var b = CreateServer(port: 9000);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Equals_SameIdDifferentHost_ReturnsFalse()
    {
        var a = CreateServer(host: "old.local");
        var b = CreateServer(host: "new.local");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Equals_SameIdDifferentIps_ReturnsFalse()
    {
        var a = CreateServer(ips: new List<string> { "192.168.1.100" });
        var b = CreateServer(ips: new List<string> { "10.0.0.50" });
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void GetHashCode_SameIdDifferentPort_DifferentHash()
    {
        var a = CreateServer(port: 8927);
        var b = CreateServer(port: 9000);
        Assert.NotEqual(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void GetHashCode_IdenticalServers_SameHash()
    {
        var a = CreateServer();
        var b = CreateServer();
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }
}
