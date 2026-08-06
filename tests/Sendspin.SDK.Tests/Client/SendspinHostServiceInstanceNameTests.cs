using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection.Noise;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Coverage for the implicit <see cref="AdvertiserOptions"/> seed: when the host is
/// constructed without advertiser options, the advertised instance name comes from
/// <see cref="ClientCapabilities.ClientName"/>. The host is never started —
/// <see cref="Sendspin.SDK.Discovery.MdnsServiceAdvertiser"/> only touches the network in
/// StartAsync — so no mDNS advertisement leaves the machine during the test run.
/// </summary>
[Collection("RealSockets")]
public class SendspinHostServiceInstanceNameTests
{
    [Fact]
    public async Task InstanceName_DefaultsToCapabilitiesClientName_WhenAdvertiserOptionsOmitted()
    {
        await using var host = new SendspinHostService(
            NullLoggerFactory.Instance,
            new SendspinClientOptions
            {
                Identity = SendspinIdentity.Generate(),
                Capabilities = new ClientCapabilities { ClientName = "Living Room Player" },
            });

        Assert.Equal("Living Room Player", host.InstanceName);
    }
}
