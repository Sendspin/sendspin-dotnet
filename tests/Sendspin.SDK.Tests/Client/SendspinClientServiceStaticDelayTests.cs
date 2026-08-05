using Sendspin.SDK.Client;
using Sendspin.SDK.Protocol.Messages;
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
    private static string SetStaticDelayCommand(int delayMs) => $$"""
        { "type": "server/command", "payload": { "player": { "command": "set_static_delay", "static_delay_ms": {{delayMs}} } } }
        """;

    [Fact]
    public void SetStaticDelay_AppliesDelayAndPersists()
    {
        var sync = new KalmanClockSynchronizer();
        var store = new FakeStaticDelayStore();
        var (client, connection, _) = TestClient.Create(configure: options =>
        {
            options.ClockSynchronizer = sync;
            options.StaticDelayStore = store;
        });
        using var _c = client;

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
        var (client, connection, _) = TestClient.Create(configure: options => options.ClockSynchronizer = sync);
        using var _c = client;

        connection.RaiseTextMessageReceived(SetStaticDelayCommand(requested));

        Assert.Equal(expected, sync.StaticDelayMs);
    }

    [Fact]
    public void SetStaticDelay_IgnoredWhenCapabilityDisabled()
    {
        var sync = new KalmanClockSynchronizer();
        var store = new FakeStaticDelayStore();
        var (client, connection, _) = TestClient.Create(configure: options =>
        {
            options.ClockSynchronizer = sync;
            options.Capabilities = new ClientCapabilities { SupportsSetStaticDelay = false };
            options.StaticDelayStore = store;
        });
        using var _c = client;

        connection.RaiseTextMessageReceived(SetStaticDelayCommand(250));

        Assert.Equal(0.0, sync.StaticDelayMs);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public void PersistedStaticDelay_RestoredOnHandshake()
    {
        var sync = new KalmanClockSynchronizer();
        var store = new FakeStaticDelayStore { Stored = 300.0 };
        var (client, connection, _) = TestClient.Create(configure: options =>
        {
            options.ClockSynchronizer = sync;
            options.StaticDelayStore = store;
        });
        using var _c = client;

        TestClient.CompleteHandshake(connection, "player@v1");

        Assert.Equal(300.0, sync.StaticDelayMs);
    }

    [Fact]
    public void NoStore_HandshakeLeavesDelayUntouched()
    {
        var sync = new KalmanClockSynchronizer { StaticDelayMs = 42.0 };
        var (client, connection, _) = TestClient.Create(configure: options => options.ClockSynchronizer = sync);
        using var _c = client;

        TestClient.CompleteHandshake(connection, "player@v1");

        // Reset() does not clear static delay and no store overrides it.
        Assert.Equal(42.0, sync.StaticDelayMs);
    }

    [Fact]
    public async Task InitialClientState_ReportsTimingFieldsAndSupportedCommands()
    {
        var (client, connection, _) = TestClient.Create(configure: options =>
            options.Capabilities = new ClientCapabilities
            {
                RequiredLeadTimeMs = 200,
                MinBufferMs = 150,
                SupportsSetStaticDelay = true,
            });
        using var _c = client;

        TestClient.CompleteHandshake(connection, "player@v1");

        var player = await WaitForPlayerStateAsync(connection);
        Assert.Equal(200, player.RequiredLeadTimeMs);
        Assert.Equal(150, player.MinBufferMs);
        Assert.NotNull(player.SupportedCommands);
        Assert.Contains("set_static_delay", player.SupportedCommands);
    }

    [Fact]
    public async Task InitialClientState_OmitsSupportedCommandsWhenCapabilityDisabled()
    {
        var (client, connection, _) = TestClient.Create(configure: options =>
            options.Capabilities = new ClientCapabilities { SupportsSetStaticDelay = false });
        using var _c = client;

        TestClient.CompleteHandshake(connection, "player@v1");

        var player = await WaitForPlayerStateAsync(connection);
        Assert.Null(player.SupportedCommands);
    }

    [Fact]
    public async Task UpdateTimingAsync_WhenConnected_ResendsStateWithNewValues()
    {
        var (client, connection, _) = TestClient.Create(configure: options =>
            options.Capabilities = new ClientCapabilities { RequiredLeadTimeMs = 200, MinBufferMs = 150 });
        using var _c = client;

        // UpdateTimingAsync only re-sends while connected; the handshake flips the fake to Connected.
        TestClient.CompleteHandshake(connection, "player@v1");
        await WaitForPlayerStateAsync(connection);

        await client.UpdateTimingAsync(requiredLeadTimeMs: 80, minBufferMs: 40);

        var player = connection.SentMessages.OfType<ClientStateMessage>().Last().Payload.Player;
        Assert.NotNull(player);
        Assert.Equal(80, player.RequiredLeadTimeMs);
        Assert.Equal(40, player.MinBufferMs);
    }

    [Fact]
    public async Task UpdateTimingAsync_WhenDisconnected_AppliesValuesWithoutSending()
    {
        var (client, connection, _) = TestClient.Create(
            configure: options =>
                options.Capabilities = new ClientCapabilities { RequiredLeadTimeMs = 200, MinBufferMs = 150 },
            connected: false);
        using var _c = client;

        // Never connected: the re-report is guarded on connection state, so nothing hits the wire...
        await client.UpdateTimingAsync(requiredLeadTimeMs: 70, minBufferMs: 30);
        Assert.Empty(connection.SentMessages);

        // ...but the new values were still applied: a later connect re-reports them in the initial state.
        TestClient.CompleteHandshake(connection, "player@v1");

        var player = await WaitForPlayerStateAsync(connection);
        Assert.Equal(70, player.RequiredLeadTimeMs);
        Assert.Equal(30, player.MinBufferMs);
    }

    [Fact]
    public void ThrowingStore_OnLoad_DoesNotAbortHandshake()
    {
        var sync = new KalmanClockSynchronizer { StaticDelayMs = 12.0 };
        var store = new FakeStaticDelayStore { ThrowOnLoad = true };
        var (client, connection, _) = TestClient.Create(configure: options =>
        {
            options.ClockSynchronizer = sync;
            options.StaticDelayStore = store;
        });
        using var _c = client;

        TestClient.CompleteHandshake(connection, "player@v1");

        // A throwing Load must be swallowed: the in-memory delay is untouched and the handshake
        // still reaches the point of sending the initial client/state.
        Assert.Equal(12.0, sync.StaticDelayMs);
        Assert.Contains(connection.SentMessages, m => m is ClientStateMessage);
    }

    [Fact]
    public void ThrowingStore_OnSave_StillAppliesDelay()
    {
        var sync = new KalmanClockSynchronizer();
        var store = new FakeStaticDelayStore { ThrowOnSave = true };
        var (client, connection, _) = TestClient.Create(configure: options =>
        {
            options.ClockSynchronizer = sync;
            options.StaticDelayStore = store;
        });
        using var _c = client;

        connection.RaiseTextMessageReceived(SetStaticDelayCommand(250));

        // Persistence failure must not prevent the in-memory apply.
        Assert.Equal(250.0, sync.StaticDelayMs);
    }

    private static async Task<PlayerStatePayload> WaitForPlayerStateAsync(FakeSendspinConnection connection)
    {
        // SendInitialClientStateAsync is fire-and-forget from the handshake; poll briefly for it.
        for (var i = 0; i < 50; i++)
        {
            var state = connection.SentMessages.OfType<ClientStateMessage>().LastOrDefault();
            if (state?.Payload.Player is { } player)
            {
                return player;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("No client/state with a player object was sent.");
    }

    private sealed class FakeStaticDelayStore : IStaticDelayStore
    {
        public double? Stored { get; set; }

        public List<double> Saved { get; } = new();

        public bool ThrowOnLoad { get; set; }

        public bool ThrowOnSave { get; set; }

        public double? Load()
        {
            if (ThrowOnLoad)
            {
                throw new InvalidOperationException("store load failed");
            }

            return Stored;
        }

        public void Save(double staticDelayMs)
        {
            if (ThrowOnSave)
            {
                throw new InvalidOperationException("store save failed");
            }

            Stored = staticDelayMs;
            Saved.Add(staticDelayMs);
        }
    }
}
