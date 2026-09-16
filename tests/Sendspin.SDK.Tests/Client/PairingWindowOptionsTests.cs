using System.Reflection;
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
    public async Task BuildClientOptions_WithAConfiguredSynchronizer_PassesItThroughUnchanged()
    {
        // The passthrough branch: a synchronizer the app configured is shared across
        // connections on purpose, and nothing is rebuilt. The options record is still copied,
        // because every connection gets the host's live-record callback attached (#183).
        var synchronizer = new KalmanClockSynchronizer();
        var hostOptions = new SendspinClientOptions
        {
            Identity = SendspinIdentity.Generate(),
            ClockSynchronizer = synchronizer,
        };

        await using var host = CreateHost(hostOptions);
        var built = host.BuildClientOptions();

        Assert.Same(synchronizer, built.ClockSynchronizer);
        Assert.Same(hostOptions.Identity, built.Identity);
        Assert.NotNull(built.LiveRecordPskIds);
    }

    [Fact]
    public async Task BuildClientOptions_LiveRecordPskIds_IncludesTrackedOpenClients_BeforeAdmissionAndDuringTeardown()
    {
        await using var host = CreateHost(new SendspinClientOptions { Identity = SendspinIdentity.Generate() });
        var built = host.BuildClientOptions();

        var (provisional, _, provisionalSession) = TestClient.Create(connected: true);
        using var _provisional = provisional;
        var provisionalPsk = MakePsk(0x11);
        provisionalSession.MatchedPsk = new NoisePsk(provisionalPsk, PskCategory.LongTerm, "srv-provisional");
        TrackOpenClient(host, provisional);

        var (teardown, teardownConnection, teardownSession) = TestClient.Create(connected: true);
        using var _teardown = teardown;
        var teardownPsk = MakePsk(0x22);
        teardownSession.MatchedPsk = new NoisePsk(teardownPsk, PskCategory.LongTerm, "srv-teardown");
        SetConnectionState(teardownConnection, ConnectionState.Disconnecting);
        TrackOpenClient(host, teardown);

        var (closed, closedConnection, closedSession) = TestClient.Create(connected: true);
        using var _closed = closed;
        var closedPsk = MakePsk(0x33);
        closedSession.MatchedPsk = new NoisePsk(closedPsk, PskCategory.LongTerm, "srv-closed");
        closedConnection.DisconnectAsync().GetAwaiter().GetResult();
        TrackOpenClient(host, closed);

        var ids = built.LiveRecordPskIds!();

        Assert.Contains(NoiseConstants.DerivePskId(provisionalPsk), ids);
        Assert.Contains(NoiseConstants.DerivePskId(teardownPsk), ids);
        Assert.DoesNotContain(NoiseConstants.DerivePskId(closedPsk), ids);
    }

    [Fact]
    public async Task BuildClientOptions_LiveRecordPskIds_IncludesAnAdoptedOpenClient()
    {
        await using var host = CreateHost(new SendspinClientOptions { Identity = SendspinIdentity.Generate() });
        var (client, _, session) = TestClient.Create(connected: true);
        using var _c = client;
        var adoptedPsk = MakePsk(0x44);
        session.MatchedPsk = new NoisePsk(adoptedPsk, PskCategory.LongTerm, "srv-adopted");

        host.AdoptClientInitiated(client, "srv-adopted");

        Assert.Contains(
            NoiseConstants.DerivePskId(adoptedPsk),
            host.BuildClientOptions().LiveRecordPskIds!());
    }

    private static void TrackOpenClient(SendspinHostService host, SendspinClientService client) =>
        typeof(SendspinHostService)
            .GetMethod("TrackOpenClient", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(host, [client]);

    private static void SetConnectionState(FakeSendspinConnection connection, ConnectionState state) =>
        typeof(FakeSendspinConnection)
            .GetMethod("SetState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(connection, [state]);

    private static byte[] MakePsk(byte fill) => Enumerable.Repeat(fill, 32).ToArray();
}
