using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Discovery;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// SendspinHostService rebuilds SendspinClientOptions per connection by hand-copying fields.
/// A new option left out of that mirror never reaches any connection, and the failure is
/// silent: gating degrades to "never gated" and single-connection tests still pass. This
/// pins the propagation.
/// </summary>
public class PairingWindowOptionsTests
{
    [Fact]
    public async Task PairingWindow_SetOnHostOptions_ReachesPerConnectionOptions()
    {
        var window = new PairingWindow();
        var host = new SendspinHostService(
            NullLoggerFactory.Instance,
            new SendspinClientOptions
            {
                Identity = SendspinIdentity.Generate(),
                PairingWindow = window,
            },
            listenerOptions: new ListenerOptions { Port = 0 },
            advertiserOptions: new AdvertiserOptions { Enabled = false });

        await using (host)
        {
            Assert.Same(window, host.BuildClientOptions().PairingWindow);
        }
    }
}
