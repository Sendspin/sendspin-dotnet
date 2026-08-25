using Sendspin.SDK.Client;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Behavioral coverage for the server's output-delay command (spec PR #69) and the optional
/// <see cref="IOutputDelayStore"/> persistence seam (issue #23). Tests inject a real
/// <see cref="KalmanClockSynchronizer"/> so the applied delay can be read back deterministically,
/// avoiding any dependency on the fire-and-forget client/state acknowledgement.
/// </summary>
/// <remarks>
/// Test names beginning <c>SetStaticDelay</c> or <c>SetOutputDelay</c> name the wire command
/// spelling under test, not the concept: spec 168a677 renamed <c>set_static_delay</c> to
/// <c>set_output_delay</c> with no alias, and both are accepted inbound.
/// </remarks>
public class SendspinClientServiceOutputDelayTests
{
    private static string SetStaticDelayCommand(int delayMs) => $$"""
        { "type": "server/command", "payload": { "player": { "command": "set_static_delay", "static_delay_ms": {{delayMs}} } } }
        """;

    private static string SetOutputDelayCommand(int delayMs) => $$"""
        { "type": "server/command", "payload": { "player": { "command": "set_output_delay", "output_delay_ms": {{delayMs}} } } }
        """;

    [Fact]
    public void SetStaticDelay_AppliesDelayAndPersists()
    {
        var sync = new KalmanClockSynchronizer();
        var store = new FakeOutputDelayStore();
        var (client, connection, _) = TestClient.Create(configure: options => options with
        {
            ClockSynchronizer = sync,
            OutputDelayStore = store,
        });
        using var _c = client;

        connection.RaiseTextMessageReceived(SetStaticDelayCommand(250));

        Assert.Equal(250.0, sync.OutputDelayMs);
        Assert.Equal(new[] { 250.0 }, store.Saved);
    }

    [Theory]
    [InlineData(9000, 5000)] // above max clamps down
    [InlineData(-100, 0)]    // negatives are not supported; clamp to zero
    public void SetStaticDelay_ClampsToSpecRange(int requested, double expected)
    {
        var sync = new KalmanClockSynchronizer();
        var (client, connection, _) = TestClient.Create(configure: options => options with { ClockSynchronizer = sync });
        using var _c = client;

        connection.RaiseTextMessageReceived(SetStaticDelayCommand(requested));

        Assert.Equal(expected, sync.OutputDelayMs);
    }

    [Fact]
    public void SetStaticDelay_IgnoredWhenCapabilityDisabled()
    {
        var sync = new KalmanClockSynchronizer();
        var store = new FakeOutputDelayStore();
        var (client, connection, _) = TestClient.Create(configure: options => options with
        {
            ClockSynchronizer = sync,
            Capabilities = new ClientCapabilities { SupportsSetOutputDelay = false },
            OutputDelayStore = store,
        });
        using var _c = client;

        connection.RaiseTextMessageReceived(SetStaticDelayCommand(250));

        Assert.Equal(0.0, sync.OutputDelayMs);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public void SetOutputDelay_AppliesDelayAndPersists()
    {
        // Spec 168a677 renamed the command and its field with no alias; a server that has
        // adopted the rename must land on the same delay as the pre-rename shape does.
        var sync = new KalmanClockSynchronizer();
        var store = new FakeOutputDelayStore();
        var (client, connection, _) = TestClient.Create(configure: options => options with
        {
            ClockSynchronizer = sync,
            OutputDelayStore = store,
        });
        using var _c = client;

        connection.RaiseTextMessageReceived(SetOutputDelayCommand(120));

        Assert.Equal(120.0, sync.OutputDelayMs);
        Assert.Equal(new[] { 120.0 }, store.Saved);
    }

    [Fact]
    public void SetOutputDelay_WithBothFields_PrefersOutputDelayMs()
    {
        // A transitional server may send both names. The post-rename field is the authoritative
        // one, so it wins rather than the legacy field it replaced.
        var sync = new KalmanClockSynchronizer();
        var (client, connection, _) = TestClient.Create(configure: options => options with { ClockSynchronizer = sync });
        using var _c = client;

        connection.RaiseTextMessageReceived("""
            { "type": "server/command", "payload": { "player": { "command": "set_output_delay", "output_delay_ms": 120, "static_delay_ms": 250 } } }
            """);

        Assert.Equal(120.0, sync.OutputDelayMs);
    }

    [Fact]
    public void SetOutputDelay_IgnoredWhenCapabilityDisabled()
    {
        var sync = new KalmanClockSynchronizer();
        var store = new FakeOutputDelayStore();
        var (client, connection, _) = TestClient.Create(configure: options => options with
        {
            ClockSynchronizer = sync,
            Capabilities = new ClientCapabilities { SupportsSetOutputDelay = false },
            OutputDelayStore = store,
        });
        using var _c = client;

        connection.RaiseTextMessageReceived(SetOutputDelayCommand(120));

        Assert.Equal(0.0, sync.OutputDelayMs);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public void PersistedOutputDelay_RestoredOnHandshake()
    {
        var sync = new KalmanClockSynchronizer();
        var store = new FakeOutputDelayStore { Stored = 300.0 };
        var (client, connection, _) = TestClient.Create(configure: options => options with
        {
            ClockSynchronizer = sync,
            OutputDelayStore = store,
        });
        using var _c = client;

        TestClient.CompleteHandshake(connection, "player@v1");

        Assert.Equal(300.0, sync.OutputDelayMs);
    }

    [Fact]
    public void NoStore_HandshakeLeavesDelayUntouched()
    {
        var sync = new KalmanClockSynchronizer { OutputDelayMs = 42.0 };
        var (client, connection, _) = TestClient.Create(configure: options => options with { ClockSynchronizer = sync });
        using var _c = client;

        TestClient.CompleteHandshake(connection, "player@v1");

        // Reset() does not clear output delay and no store overrides it.
        Assert.Equal(42.0, sync.OutputDelayMs);
    }

    [Fact]
    public async Task InitialClientState_ReportsTimingFieldsAndSupportedCommands()
    {
        // Clock already converged: the initial client/state these tests inspect is otherwise
        // deferred until sync convergence (see InitialClientStateGatingTests).
        var (client, connection, _) = TestClient.Create(configure: options => options with
        {
            ClockSynchronizer = new ConvergedClockSynchronizer(),
            Capabilities = new ClientCapabilities
            {
                RequiredLeadTimeMs = 200,
                MinBufferMs = 150,
                SupportsSetOutputDelay = true,
            },
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
        var (client, connection, _) = TestClient.Create(configure: options => options with
        {
            ClockSynchronizer = new ConvergedClockSynchronizer(),
            Capabilities = new ClientCapabilities { SupportsSetOutputDelay = false },
        });
        using var _c = client;

        TestClient.CompleteHandshake(connection, "player@v1");

        var player = await WaitForPlayerStateAsync(connection);
        Assert.Null(player.SupportedCommands);
    }

    [Fact]
    public async Task UpdateTimingAsync_WhenConnected_ResendsStateWithNewValues()
    {
        var (client, connection, _) = TestClient.Create(configure: options => options with
        {
            ClockSynchronizer = new ConvergedClockSynchronizer(),
            Capabilities = new ClientCapabilities { RequiredLeadTimeMs = 200, MinBufferMs = 150 },
        });
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
            configure: options => options with
            {
                ClockSynchronizer = new ConvergedClockSynchronizer(),
                Capabilities = new ClientCapabilities { RequiredLeadTimeMs = 200, MinBufferMs = 150 },
            },
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
        // Converged fake rather than the Kalman used elsewhere in this file: the assertion
        // needs the initial client/state actually sent, which a player defers until sync.
        var sync = new ConvergedClockSynchronizer { OutputDelayMs = 12.0 };
        var store = new FakeOutputDelayStore { ThrowOnLoad = true };
        var (client, connection, _) = TestClient.Create(configure: options => options with
        {
            ClockSynchronizer = sync,
            OutputDelayStore = store,
        });
        using var _c = client;

        TestClient.CompleteHandshake(connection, "player@v1");

        // A throwing Load must be swallowed: the in-memory delay is untouched and the handshake
        // still reaches the point of sending the initial client/state.
        Assert.Equal(12.0, sync.OutputDelayMs);
        Assert.Contains(connection.SentMessages, m => m is ClientStateMessage);
    }

    [Fact]
    public void ThrowingStore_OnSave_StillAppliesDelay()
    {
        var sync = new KalmanClockSynchronizer();
        var store = new FakeOutputDelayStore { ThrowOnSave = true };
        var (client, connection, _) = TestClient.Create(configure: options => options with
        {
            ClockSynchronizer = sync,
            OutputDelayStore = store,
        });
        using var _c = client;

        connection.RaiseTextMessageReceived(SetStaticDelayCommand(250));

        // Persistence failure must not prevent the in-memory apply.
        Assert.Equal(250.0, sync.OutputDelayMs);
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

    private sealed class FakeOutputDelayStore : IOutputDelayStore
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

        public void Save(double outputDelayMs)
        {
            if (ThrowOnSave)
            {
                throw new InvalidOperationException("store save failed");
            }

            Stored = outputDelayMs;
            Saved.Add(outputDelayMs);
        }
    }
}
