using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Behavioral coverage for the <c>set_static_delay</c> server command (spec PR #69) and the
/// optional <see cref="IStaticDelayStore"/> persistence seam (issue #23). Tests inject a real
/// <see cref="KalmanClockSynchronizer"/> so the applied delay can be read back deterministically,
/// avoiding any dependency on the fire-and-forget client/state acknowledgement.
/// </summary>
public class SendspinClientServiceStaticDelayTests
{
    private const string HelloJson = """
        { "type": "server/hello", "payload": { "server_id": "srv-1", "version": 1, "active_roles": ["player@v1"] } }
        """;

    private static string SetStaticDelayCommand(int delayMs) => $$"""
        { "type": "server/command", "payload": { "player": { "command": "set_static_delay", "static_delay_ms": {{delayMs}} } } }
        """;

    [Fact]
    public void SetStaticDelay_AppliesDelayAndPersists()
    {
        var sync = new KalmanClockSynchronizer();
        var store = new FakeStaticDelayStore();
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection,
            sync,
            new ClientCapabilities(),
            audioPipeline: null,
            staticDelayStore: store);

        connection.RaiseTextMessageReceived(SetStaticDelayCommand(250));

        Assert.Equal(250.0, sync.StaticDelayMs);
        Assert.Equal(new[] { 250.0 }, store.Saved);
    }

    [Theory]
    [InlineData(9000, 5000)] // above max clamps down
    [InlineData(-100, 0)]    // negatives are not supported; clamp to zero
    public void SetStaticDelay_ClampsToSpecRange(int requested, double expected)
    {
        var sync = new KalmanClockSynchronizer();
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection,
            sync);

        connection.RaiseTextMessageReceived(SetStaticDelayCommand(requested));

        Assert.Equal(expected, sync.StaticDelayMs);
    }

    [Fact]
    public void SetStaticDelay_IgnoredWhenCapabilityDisabled()
    {
        var sync = new KalmanClockSynchronizer();
        var store = new FakeStaticDelayStore();
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection,
            sync,
            new ClientCapabilities { SupportsSetStaticDelay = false },
            audioPipeline: null,
            staticDelayStore: store);

        connection.RaiseTextMessageReceived(SetStaticDelayCommand(250));

        Assert.Equal(0.0, sync.StaticDelayMs);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public void PersistedStaticDelay_RestoredOnHandshake()
    {
        var sync = new KalmanClockSynchronizer();
        var store = new FakeStaticDelayStore { Stored = 300.0 };
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection,
            sync,
            new ClientCapabilities(),
            audioPipeline: null,
            staticDelayStore: store);

        connection.RaiseTextMessageReceived(HelloJson);

        Assert.Equal(300.0, sync.StaticDelayMs);
    }

    [Fact]
    public void NoStore_HandshakeLeavesDelayUntouched()
    {
        var sync = new KalmanClockSynchronizer { StaticDelayMs = 42.0 };
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection,
            sync);

        connection.RaiseTextMessageReceived(HelloJson);

        // Reset() does not clear static delay and no store overrides it.
        Assert.Equal(42.0, sync.StaticDelayMs);
    }

    private sealed class FakeStaticDelayStore : IStaticDelayStore
    {
        public double? Stored { get; set; }

        public List<double> Saved { get; } = new();

        public double? Load() => Stored;

        public void Save(double staticDelayMs)
        {
            Stored = staticDelayMs;
            Saved.Add(staticDelayMs);
        }
    }
}
