using Sendspin.SDK.Discovery;
using Zeroconf;

namespace Sendspin.SDK.Tests.Discovery;

/// <summary>
/// #346: what a responder says reaches the log and the application only after it has been
/// cleaned. Any device on the LAN can answer an mDNS query, so a name such as
/// <c>"Living Room\n[ERROR] disk full"</c> was logged verbatim, as two lines, and shown as is.
/// </summary>
public class MdnsHostParsingTests
{
    private const string HostileName = "Living Room\n[ERROR] disk full";

    [Fact]
    public void NothingLoggedForAHost_CarriesItsControlCharacters()
    {
        var logger = new CapturingLogger<MdnsServerDiscovery>();
        var discovery = new MdnsServerDiscovery(logger);

        discovery.ParseHost(new FakeHost(
            "ma\r\nserver",
            new Dictionary<string, string>
            {
                ["name"] = HostileName,
                ["path"] = "/sendspin\n[ERROR] forged",
                ["x\ny"] = "a\rb\u001b[31m",
            }));

        Assert.NotEmpty(logger.Entries);
        Assert.All(logger.Entries, entry => Assert.DoesNotContain(entry.Message, char.IsControl));
    }

    [Fact]
    public void TheDiscoveredServer_CarriesNoControlCharacters()
    {
        var discovery = new MdnsServerDiscovery(new CapturingLogger<MdnsServerDiscovery>());

        var server = discovery.ParseHost(new FakeHost(
            "ma\r\nserver",
            new Dictionary<string, string> { ["name"] = HostileName, ["x\ny"] = "a\rb" }));

        Assert.NotNull(server);
        Assert.Equal("Living Room[ERROR] disk full", server.Name);
        Assert.Equal("maserver", server.Host);
        Assert.Equal("Living Room[ERROR] disk full (maserver:8927)", server.ToString());
        Assert.Equal("ab", server.Properties["xy"]);
    }

    [Fact]
    public void TheHostNameFallback_IsCleanedToo()
    {
        var discovery = new MdnsServerDiscovery(new CapturingLogger<MdnsServerDiscovery>());

        var server = discovery.ParseHost(new FakeHost("living\nroom.local", new Dictionary<string, string>()));

        Assert.NotNull(server);
        Assert.Equal("Livingroom", server.Name);
    }

    /// <summary>
    /// The key the Windows app stores as its auto-connect choice. No reference server publishes
    /// an <c>id</c> TXT key, so this instance-name-and-address form is what is saved in the
    /// field; changing how it is built would silently drop that setting.
    /// </summary>
    [Fact]
    public void ServerId_IsTheInstanceNameAndFirstAddress_WhenNoIdKeyIsPublished()
    {
        var discovery = new MdnsServerDiscovery(new CapturingLogger<MdnsServerDiscovery>());

        var server = discovery.ParseHost(new FakeHost(
            "abc123",
            new Dictionary<string, string> { ["name"] = "Music Assistant", ["path"] = "/sendspin" }));

        Assert.NotNull(server);
        Assert.Equal("abc123-192.168.1.10", server.ServerId);
    }

    private sealed class FakeHost : IZeroconfHost
    {
        public FakeHost(string displayName, Dictionary<string, string> txt)
        {
            DisplayName = displayName;
            Services = new Dictionary<string, IService>
            {
                [MdnsServerDiscovery.ServiceType] = new FakeService { Properties = [txt] },
            };
        }

        public string DisplayName { get; }

        public string Id => "192.168.1.10";

        public string IPAddress => "192.168.1.10";

        public IReadOnlyList<string> IPAddresses => ["192.168.1.10"];

        public IReadOnlyDictionary<string, IService> Services { get; }
    }

    private sealed class FakeService : IService
    {
        public string Name => "fake";

        public string ServiceName => MdnsServerDiscovery.ServiceType;

        public int Port => 8927;

        public int Ttl => 120;

        public IReadOnlyList<IReadOnlyDictionary<string, string>> Properties { get; init; } = [];
    }
}
