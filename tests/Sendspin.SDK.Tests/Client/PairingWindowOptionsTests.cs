using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Discovery;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// SendspinHostService builds each connection's options from the stored ones. Since #95
/// that is a `with` expression, so a forgotten property is no longer possible and the
/// reflection-based mirror check this file used to carry is gone. What remains is the one
/// behaviour the method still has: substitute a per-connection clock synchronizer when none
/// was configured, and otherwise hand the stored options back.
/// </summary>
public class PairingWindowOptionsTests
{
    private static SendspinHostService CreateHost(SendspinClientOptions options) =>
        new(
            NullLoggerFactory.Instance,
            options,
            listenerOptions: new ListenerOptions { Port = 0 },
            advertiserOptions: new AdvertiserOptions { Enabled = false });

    [Fact]
    public async Task BuildClientOptions_WithNoSynchronizer_SubstitutesOneAndKeepsEverythingElse()
    {
        var window = new PairingWindow();
        var hostOptions = new SendspinClientOptions
        {
            Identity = SendspinIdentity.Generate(),
            PairingWindow = window,
            PairingAttemptTimeout = TimeSpan.FromSeconds(7),
        };

        await using var host = CreateHost(hostOptions);
        var built = host.BuildClientOptions();

        Assert.NotNull(built.ClockSynchronizer);
        Assert.Null(hostOptions.ClockSynchronizer);
        Assert.Same(window, built.PairingWindow);
        Assert.Equal(TimeSpan.FromSeconds(7), built.PairingAttemptTimeout);
        Assert.Same(hostOptions.Identity, built.Identity);
    }

    [Fact]
    public async Task BuildClientOptions_WithAConfiguredSynchronizer_HandsBackTheStoredOptions()
    {
        // The passthrough branch: a synchronizer the app configured is shared across
        // connections on purpose, and nothing is rebuilt.
        var hostOptions = new SendspinClientOptions
        {
            Identity = SendspinIdentity.Generate(),
            ClockSynchronizer = new KalmanClockSynchronizer(),
        };

        await using var host = CreateHost(hostOptions);

        Assert.Same(hostOptions, host.BuildClientOptions());
    }
}
