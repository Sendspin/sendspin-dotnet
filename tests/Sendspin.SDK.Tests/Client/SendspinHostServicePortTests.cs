using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Discovery;
using Sendspin.SDK.Connection;

namespace Sendspin.SDK.Tests.Client;

public class SendspinHostServicePortTests
{
    [Fact]
    public async Task ListeningPort_ReflectsOsAssignedPort_WhenBindingZero()
    {
        await using var host = new SendspinHostService(
            NullLoggerFactory.Instance,
            listenerOptions: new ListenerOptions { Port = 0 },
            advertiserOptions: new AdvertiserOptions { Enabled = false });

        await host.StartAsync();
        try
        {
            Assert.True(host.ListeningPort > 0, $"expected an OS-assigned port, got {host.ListeningPort}");
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
