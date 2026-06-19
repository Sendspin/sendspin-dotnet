using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;

namespace Sendspin.SDK.Tests.Client;

public class SendspinHostServicePortTests
{
    [Fact]
    public async Task ListeningPort_ReflectsOsAssignedPort_WhenBindingZero()
    {
        await using var host = new SendspinHostService(
            NullLoggerFactory.Instance,
            listenerOptions: new ListenerOptions { Port = 0 });

        await host.StartAsync();
        try
        {
            // Don't advertise during the test, so real servers can't race in.
            await host.StopAdvertisingAsync();
            Assert.True(host.ListeningPort > 0, $"expected an OS-assigned port, got {host.ListeningPort}");
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
