# Multi-Server Arbitration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the existing host-mode multi-server arbitration provably spec-conformant with exhaustive + end-to-end tests, formalize last-played persistence behind an `ILastPlayedServerStore` seam, and normalize the same-server-reconnect goodbye to the spec-valid `user_request` reason.

**Architecture:** Extract the arbitration decision table from `SendspinHostService.ArbitrateConnectionAsync` into a pure, side-effect-free `ServerArbitration.Decide(...)` so it can be unit-tested exhaustively without sockets. Add an additive `ILastPlayedServerStore` (mirroring `IStaticDelayStore`). Cover the wire behavior with loopback end-to-end tests driven by a minimal `FakeServer` WebSocket helper. The only intentional behavior change is the same-server-reconnect goodbye reason (`reconnecting` → `user_request`).

**Tech Stack:** C# / .NET 10, xUnit, `System.Net.WebSockets.ClientWebSocket`, the SDK's own `MessageSerializer`. The test assembly already has `InternalsVisibleTo`, so decision logic stays `internal`.

**Design doc:** `docs/superpowers/specs/2026-06-04-multi-server-arbitration-design.md`

---

## Task 1: Pure arbitration decision (`ServerArbitration.Decide`)

**Files:**
- Create: `src/Sendspin.SDK/Client/ServerArbitration.cs`
- Test: `tests/Sendspin.SDK.Tests/Client/ServerArbitrationTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/Sendspin.SDK.Tests/Client/ServerArbitrationTests.cs`:

```csharp
using Sendspin.SDK.Client;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Exhaustive coverage of the pure multi-server arbitration decision table (spec §multi-server).
/// </summary>
public class ServerArbitrationTests
{
    [Fact]
    public void NoExistingConnection_AcceptsNewWithNoLoser()
    {
        var r = ServerArbitration.Decide("srv-new", "discovery", null, null, null);
        Assert.True(r.AcceptNew);
        Assert.Null(r.LoserGoodbyeReason);
    }

    [Fact]
    public void SameServerReconnect_AcceptsAndDropsStaleWithUserRequest()
    {
        var r = ServerArbitration.Decide("srv-1", "discovery", "srv-1", "discovery", null);
        Assert.True(r.AcceptNew);
        Assert.Equal("user_request", r.LoserGoodbyeReason);
    }

    [Theory]
    // newId, newReason, existingId, existingReason, lastPlayed, expectAccept, expectLoserReason
    [InlineData("b", "playback", "a", "discovery", null, true, "another_server")]   // new playback wins
    [InlineData("b", "discovery", "a", "playback", null, false, "another_server")]  // existing playback wins
    [InlineData("b", "discovery", "a", "discovery", "b", true, "another_server")]   // tie, last-played = new
    [InlineData("b", "discovery", "a", "discovery", "a", false, "another_server")]  // tie, last-played = existing
    [InlineData("b", "discovery", "a", "discovery", null, false, "another_server")] // tie, no last-played
    [InlineData("b", "playback", "a", "playback", "b", true, "another_server")]     // E1 both playback, lp = new
    [InlineData("b", "playback", "a", "playback", "a", false, "another_server")]    // E1 both playback, lp = existing
    [InlineData("b", null, "a", "playback", null, false, "another_server")]         // null reason = discovery
    [InlineData("b", "PLAYBACK", "a", "discovery", null, true, "another_server")]   // case-insensitive
    public void DecisionTable(
        string newId, string? newReason, string existingId, string? existingReason,
        string? lastPlayed, bool expectAccept, string expectLoserReason)
    {
        var r = ServerArbitration.Decide(newId, newReason, existingId, existingReason, lastPlayed);
        Assert.Equal(expectAccept, r.AcceptNew);
        Assert.Equal(expectLoserReason, r.LoserGoodbyeReason);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj --filter "FullyQualifiedName~ServerArbitrationTests"`
Expected: **Build FAILED** — `CS0103 The name 'ServerArbitration' does not exist` (type not yet defined).

- [ ] **Step 3: Write the minimal implementation**

Create `src/Sendspin.SDK/Client/ServerArbitration.cs`:

```csharp
namespace Sendspin.SDK.Client;

/// <summary>
/// Pure, side-effect-free multi-server arbitration decision used by <see cref="SendspinHostService"/>.
/// Extracted so the spec's decision table can be unit-tested exhaustively without connection/socket I/O.
/// </summary>
internal static class ServerArbitration
{
    private const string Playback = "playback";
    private const string Discovery = "discovery";
    private const string AnotherServer = "another_server";
    private const string UserRequest = "user_request";

    /// <summary>
    /// Decides whether a newly-handshaked server should become the active connection.
    /// </summary>
    /// <param name="newServerId">server_id of the newly connected server.</param>
    /// <param name="newReason">connection_reason of the new server (null treated as discovery).</param>
    /// <param name="existingServerId">server_id of the current active server, or null if none.</param>
    /// <param name="existingReason">connection_reason of the existing server (null treated as discovery).</param>
    /// <param name="lastPlayedServerId">Persisted last-played server_id, or null.</param>
    internal static ArbitrationResult Decide(
        string newServerId,
        string? newReason,
        string? existingServerId,
        string? existingReason,
        string? lastPlayedServerId)
    {
        // No existing connection — accept unconditionally.
        if (existingServerId is null)
        {
            return new ArbitrationResult(true, null, "no existing connection");
        }

        // Same server reconnecting — accept and drop the stale socket. user_request is the
        // spec-valid "do not auto-reconnect" reason: the peer is the SAME server (not
        // another_server) and the client is alive (not shutdown).
        if (string.Equals(newServerId, existingServerId, StringComparison.Ordinal))
        {
            return new ArbitrationResult(true, UserRequest, "same server reconnecting");
        }

        var newIsPlayback = IsPlayback(newReason);
        var existingIsPlayback = IsPlayback(existingReason);

        if (newIsPlayback && !existingIsPlayback)
        {
            return new ArbitrationResult(true, AnotherServer, "new server has playback reason");
        }

        if (existingIsPlayback && !newIsPlayback)
        {
            return new ArbitrationResult(false, AnotherServer, "existing server has playback reason");
        }

        // Tie (both playback or both discovery): prefer the last-played server, else keep existing.
        if (lastPlayedServerId is not null
            && string.Equals(newServerId, lastPlayedServerId, StringComparison.Ordinal))
        {
            return new ArbitrationResult(true, AnotherServer, "new server matches last-played (tie-break)");
        }

        return new ArbitrationResult(
            false,
            AnotherServer,
            lastPlayedServerId is not null
                ? "existing server wins tie-break (new is not last-played)"
                : "existing server wins tie-break (no last-played set)");
    }

    private static bool IsPlayback(string? reason)
        => string.Equals(reason ?? Discovery, Playback, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Outcome of <see cref="ServerArbitration.Decide"/>.
/// </summary>
/// <param name="AcceptNew">True to accept the new server; false to reject it.</param>
/// <param name="LoserGoodbyeReason">
/// client/goodbye reason for the losing connection (the existing one when <paramref name="AcceptNew"/>
/// is true and one exists; the new one when false), or null when there is no loser.
/// </param>
/// <param name="Rationale">Human-readable decision summary for logging.</param>
internal readonly record struct ArbitrationResult(bool AcceptNew, string? LoserGoodbyeReason, string Rationale);
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj --filter "FullyQualifiedName~ServerArbitrationTests"`
Expected: **PASS** — 11 tests (2 facts + 9 theory cases), 0 failed.

- [ ] **Step 5: Commit**

```bash
git add src/Sendspin.SDK/Client/ServerArbitration.cs tests/Sendspin.SDK.Tests/Client/ServerArbitrationTests.cs
git commit -m "feat: extract pure ServerArbitration.Decide with exhaustive tests"
```

---

## Task 2: `ILastPlayedServerStore` persistence seam (additive)

**Files:**
- Create: `src/Sendspin.SDK/Client/ILastPlayedServerStore.cs`
- Modify: `src/Sendspin.SDK/Client/SendSpinHostService.cs` (add field, ctor param, load on construct, save in `SetLastPlayedServerId`)
- Test: `tests/Sendspin.SDK.Tests/Client/SendspinHostServicePersistenceTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/Sendspin.SDK.Tests/Client/SendspinHostServicePersistenceTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Coverage for the additive ILastPlayedServerStore persistence seam on the host service.
/// The host is constructed but never started, so these tests touch no network.
/// </summary>
public class SendspinHostServicePersistenceTests
{
    private static SendspinHostService NewHost(ILastPlayedServerStore? store = null, string? seed = null) =>
        new(NullLoggerFactory.Instance, lastPlayedServerId: seed, lastPlayedServerStore: store);

    [Fact]
    public async Task Construction_SeedsLastPlayedFromStore()
    {
        var store = new FakeLastPlayedServerStore { Stored = "srv-x" };
        await using var host = NewHost(store: store);
        Assert.Equal("srv-x", host.LastPlayedServerId);
    }

    [Fact]
    public async Task SeedParam_WinsOverStore()
    {
        var store = new FakeLastPlayedServerStore { Stored = "srv-store" };
        await using var host = NewHost(store: store, seed: "srv-seed");
        Assert.Equal("srv-seed", host.LastPlayedServerId);
    }

    [Fact]
    public async Task SetLastPlayedServerId_PersistsToStore()
    {
        var store = new FakeLastPlayedServerStore();
        await using var host = NewHost(store: store);
        host.SetLastPlayedServerId("srv-y");
        Assert.Contains("srv-y", store.Saved);
    }

    [Fact]
    public async Task ThrowingStore_OnLoad_DoesNotBreakConstruction()
    {
        var store = new FakeLastPlayedServerStore { ThrowOnLoad = true };
        await using var host = NewHost(store: store);
        Assert.Null(host.LastPlayedServerId);
    }

    [Fact]
    public async Task ThrowingStore_OnSave_DoesNotThrow()
    {
        var store = new FakeLastPlayedServerStore { ThrowOnSave = true };
        await using var host = NewHost(store: store);
        host.SetLastPlayedServerId("srv-z"); // must not throw
        Assert.Equal("srv-z", host.LastPlayedServerId);
    }

    private sealed class FakeLastPlayedServerStore : ILastPlayedServerStore
    {
        public string? Stored { get; set; }
        public List<string> Saved { get; } = new();
        public bool ThrowOnLoad { get; set; }
        public bool ThrowOnSave { get; set; }

        public string? Load()
        {
            if (ThrowOnLoad) throw new InvalidOperationException("load failed");
            return Stored;
        }

        public void Save(string serverId)
        {
            if (ThrowOnSave) throw new InvalidOperationException("save failed");
            Saved.Add(serverId);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj --filter "FullyQualifiedName~SendspinHostServicePersistenceTests"`
Expected: **Build FAILED** — `ILastPlayedServerStore` does not exist and `SendspinHostService` has no `lastPlayedServerStore` parameter.

- [ ] **Step 3a: Create the interface**

Create `src/Sendspin.SDK/Client/ILastPlayedServerStore.cs`:

```csharp
namespace Sendspin.SDK.Client;

/// <summary>
/// Optional persistence seam for the "last played server" — the server_id of the server that most
/// recently had playback_state 'playing'. Mirrors <see cref="IStaticDelayStore"/>.
/// </summary>
/// <remarks>
/// The Sendspin spec requires clients to persist this across restarts so multi-server arbitration can
/// prefer the last-played server in a tie. Because the SDK is a library and cannot choose a storage
/// location, the embedder implements this interface (file, registry, database, etc.) and supplies it to
/// <see cref="SendspinHostService"/>. Implementations should be fast and non-throwing; calls happen on
/// the connection/arbitration path. When no store is provided, the SDK keeps its previous behavior and
/// the embedder may seed the value via the constructor and observe changes via
/// <c>LastPlayedServerIdChanged</c>.
/// </remarks>
public interface ILastPlayedServerStore
{
    /// <summary>
    /// Loads the persisted last-played server_id, or <c>null</c> if none has been stored.
    /// Called once at host-service construction.
    /// </summary>
    string? Load();

    /// <summary>
    /// Persists the last-played server_id. Called when a server transitions to playback_state 'playing'.
    /// </summary>
    void Save(string serverId);
}
```

- [ ] **Step 3b: Wire the store into the host service**

In `src/Sendspin.SDK/Client/SendSpinHostService.cs`:

Add a field next to the other readonly fields (after `private readonly IClockSynchronizer? _clockSynchronizer;`):

```csharp
    private readonly ILastPlayedServerStore? _lastPlayedServerStore;
```

Add the constructor parameter as the **last** parameter (preserving positional back-compat) — change the signature:

```csharp
    public SendspinHostService(
        ILoggerFactory loggerFactory,
        ClientCapabilities? capabilities = null,
        ListenerOptions? listenerOptions = null,
        AdvertiserOptions? advertiserOptions = null,
        IAudioPipeline? audioPipeline = null,
        IClockSynchronizer? clockSynchronizer = null,
        string? lastPlayedServerId = null,
        ILastPlayedServerStore? lastPlayedServerStore = null)
```

Replace the line `LastPlayedServerId = lastPlayedServerId;` with:

```csharp
        _lastPlayedServerStore = lastPlayedServerStore;
        // Explicit seed wins; otherwise fall back to the store (best-effort).
        LastPlayedServerId = lastPlayedServerId ?? TryLoadLastPlayed();
```

In `SetLastPlayedServerId`, add a save call. Change the body from:

```csharp
        LastPlayedServerId = serverId;
        _logger.LogInformation("Last played server updated: {ServerId}", serverId);
        LastPlayedServerIdChanged?.Invoke(this, serverId);
```

to:

```csharp
        LastPlayedServerId = serverId;
        TrySaveLastPlayed(serverId);
        _logger.LogInformation("Last played server updated: {ServerId}", serverId);
        LastPlayedServerIdChanged?.Invoke(this, serverId);
```

Add these two best-effort helpers (place them directly after `SetLastPlayedServerId`):

```csharp
    private string? TryLoadLastPlayed()
    {
        if (_lastPlayedServerStore is null)
        {
            return null;
        }

        try
        {
            return _lastPlayedServerStore.Load();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ILastPlayedServerStore.Load() threw; continuing without persisted last-played server");
            return null;
        }
    }

    private void TrySaveLastPlayed(string serverId)
    {
        if (_lastPlayedServerStore is null)
        {
            return;
        }

        try
        {
            _lastPlayedServerStore.Save(serverId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ILastPlayedServerStore.Save({ServerId}) threw; last-played applied in-memory but not persisted", serverId);
        }
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj --filter "FullyQualifiedName~SendspinHostServicePersistenceTests"`
Expected: **PASS** — 5 tests, 0 failed.

- [ ] **Step 5: Commit**

```bash
git add src/Sendspin.SDK/Client/ILastPlayedServerStore.cs src/Sendspin.SDK/Client/SendSpinHostService.cs tests/Sendspin.SDK.Tests/Client/SendspinHostServicePersistenceTests.cs
git commit -m "feat: add ILastPlayedServerStore persistence seam to host service"
```

---

## Task 3: Surface the actual bound port (enables loopback tests)

**Files:**
- Modify: `src/Sendspin.SDK/Connection/SendSpinListener.cs` (add `BoundPort`)
- Modify: `src/Sendspin.SDK/Client/SendSpinHostService.cs` (add `ListeningPort`)
- Test: `tests/Sendspin.SDK.Tests/Client/SendspinHostServicePortTests.cs`

`SimpleWebSocketServer.Port` already reports the OS-assigned port after `Start` (it reads `LocalEndpoint`). `SendspinListener` and `SendspinHostService` currently expose only the *configured* port; this task threads the real one up.

- [ ] **Step 1: Write the failing test**

Create `tests/Sendspin.SDK.Tests/Client/SendspinHostServicePortTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj --filter "FullyQualifiedName~SendspinHostServicePortTests"`
Expected: **Build FAILED** — `SendspinHostService` has no `ListeningPort`.

- [ ] **Step 3a: Add `BoundPort` to the listener**

In `src/Sendspin.SDK/Connection/SendSpinListener.cs`, add after the existing `Port` property:

```csharp
    /// <summary>
    /// The actual bound port. Differs from <see cref="Port"/> when the configured port is 0
    /// (OS-assigned). Falls back to the configured port before the server has started.
    /// </summary>
    public int BoundPort => _server?.Port ?? _options.Port;
```

- [ ] **Step 3b: Expose it on the host service**

In `src/Sendspin.SDK/Client/SendSpinHostService.cs`, add near the other public accessors (e.g. after the `ClientId` property):

```csharp
    /// <summary>
    /// The actual port the listener is bound to (resolves an OS-assigned port when configured as 0).
    /// </summary>
    public int ListeningPort => _listener.BoundPort;
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj --filter "FullyQualifiedName~SendspinHostServicePortTests"`
Expected: **PASS** — 1 test.

- [ ] **Step 5: Commit**

```bash
git add src/Sendspin.SDK/Connection/SendSpinListener.cs src/Sendspin.SDK/Client/SendSpinHostService.cs tests/Sendspin.SDK.Tests/Client/SendspinHostServicePortTests.cs
git commit -m "feat: surface the listener's actual bound port for tests"
```

---

## Task 4: Loopback `FakeServer` harness + arbitration characterization e2e

These scenarios all pass against the **current** host service (it already does switch/reject/tie correctly). They build the harness and lock in existing wire behavior before Task 5 changes the reconnect reason.

**Files:**
- Create: `tests/Sendspin.SDK.Tests/Client/FakeServer.cs`
- Create: `tests/Sendspin.SDK.Tests/Client/SendspinHostServiceArbitrationTests.cs`

- [ ] **Step 1: Write the `FakeServer` helper**

Create `tests/Sendspin.SDK.Tests/Client/FakeServer.cs`:

```csharp
using System.Net.WebSockets;
using System.Text;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Minimal in-test Sendspin "server": a WebSocket client that connects to the host's listener,
/// completes the handshake by replying to client/hello with a server/hello carrying a chosen
/// server_id and connection_reason, and captures any client/goodbye reason the host sends.
/// </summary>
internal sealed class FakeServer : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly string _serverId;
    private readonly string _connectionReason;
    private readonly TaskCompletionSource<string> _goodbye =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _receiveLoop;

    internal FakeServer(string serverId, string connectionReason)
    {
        _serverId = serverId;
        _connectionReason = connectionReason;
    }

    internal async Task ConnectAsync(int port)
    {
        await _ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/sendspin"), CancellationToken.None);
        _receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    /// <summary>Returns the client/goodbye reason, or null if none arrives before the timeout.</summary>
    internal async Task<string?> WaitForGoodbyeAsync(TimeSpan timeout)
    {
        var completed = await Task.WhenAny(_goodbye.Task, Task.Delay(timeout));
        return completed == _goodbye.Task ? await _goodbye.Task : null;
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[8192];
        try
        {
            while (_ws.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                var type = MessageSerializer.GetMessageType(json);

                if (type == MessageTypes.ClientHello)
                {
                    await SendServerHelloAsync();
                }
                else if (type == MessageTypes.ClientGoodbye)
                {
                    var goodbye = MessageSerializer.Deserialize<ClientGoodbyeMessage>(json);
                    _goodbye.TrySetResult(goodbye?.Payload.Reason ?? string.Empty);
                }
            }
        }
        catch (WebSocketException)
        {
            // Socket torn down (host closed without a goodbye) — leave _goodbye unset.
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SendServerHelloAsync()
    {
        var hello = new ServerHelloMessage
        {
            Payload = new ServerHelloPayload
            {
                ServerId = _serverId,
                Name = _serverId,
                Version = 1,
                ActiveRoles = new List<string> { "player@v1" },
                ConnectionReason = _connectionReason
            }
        };
        var bytes = Encoding.UTF8.GetBytes(MessageSerializer.Serialize(hello));
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test_done", CancellationToken.None);
            }
        }
        catch
        {
            // best-effort close
        }

        _ws.Dispose();
    }
}
```

- [ ] **Step 2: Write the characterization tests**

Create `tests/Sendspin.SDK.Tests/Client/SendspinHostServiceArbitrationTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Loopback end-to-end coverage for multi-server arbitration: two FakeServers connect to the host's
/// real listener, and each scenario asserts which connection receives a client/goodbye and with what
/// reason — verifying the bytes on the wire, not just the decision logic.
/// </summary>
public class SendspinHostServiceArbitrationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static async Task<SendspinHostService> StartHostAsync(string? seed = null)
    {
        var host = new SendspinHostService(
            NullLoggerFactory.Instance,
            listenerOptions: new ListenerOptions { Port = 0 },
            lastPlayedServerId: seed);

        await host.StartAsync();
        await host.StopAdvertisingAsync(); // prevent real network servers from racing into arbitration
        return host;
    }

    private static Task WaitForServerConnectedAsync(SendspinHostService host, string serverId)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? s, ConnectedServerInfo info)
        {
            if (info.ServerId == serverId)
            {
                tcs.TrySetResult();
            }
        }

        host.ServerConnected += Handler;

        // Cover the race where it connected before we subscribed.
        if (host.ConnectedServers.Any(c => c.ServerId == serverId))
        {
            tcs.TrySetResult();
        }

        return tcs.Task.WaitAsync(Timeout);
    }

    [Fact]
    public async Task SingleDiscoveryServer_IsAcceptedWithoutGoodbye()
    {
        await using var host = await StartHostAsync();
        await using var server = new FakeServer("srv-only", "discovery");
        await server.ConnectAsync(host.ListeningPort);
        await WaitForServerConnectedAsync(host, "srv-only");

        var reason = await server.WaitForGoodbyeAsync(TimeSpan.FromMilliseconds(750));

        Assert.Null(reason); // accepted, stayed connected
        Assert.Contains(host.ConnectedServers, c => c.ServerId == "srv-only");
    }

    [Fact]
    public async Task NewPlaybackServer_SwitchesAndSendsAnotherServerToExisting()
    {
        await using var host = await StartHostAsync();
        await using var existing = new FakeServer("srv-existing", "discovery");
        await existing.ConnectAsync(host.ListeningPort);
        await WaitForServerConnectedAsync(host, "srv-existing");

        await using var incoming = new FakeServer("srv-new", "playback");
        await incoming.ConnectAsync(host.ListeningPort);

        Assert.Equal("another_server", await existing.WaitForGoodbyeAsync(Timeout));
    }

    [Fact]
    public async Task NewDiscoveryServer_AgainstPlaybackExisting_IsRejectedWithAnotherServer()
    {
        await using var host = await StartHostAsync();
        await using var existing = new FakeServer("srv-existing", "playback");
        await existing.ConnectAsync(host.ListeningPort);
        await WaitForServerConnectedAsync(host, "srv-existing");

        await using var incoming = new FakeServer("srv-new", "discovery");
        await incoming.ConnectAsync(host.ListeningPort);

        Assert.Equal("another_server", await incoming.WaitForGoodbyeAsync(Timeout));
    }

    [Fact]
    public async Task BothDiscovery_LastPlayedNewServer_WinsTieAndExistingGetsAnotherServer()
    {
        await using var host = await StartHostAsync(seed: "srv-new");
        await using var existing = new FakeServer("srv-existing", "discovery");
        await existing.ConnectAsync(host.ListeningPort);
        await WaitForServerConnectedAsync(host, "srv-existing");

        await using var incoming = new FakeServer("srv-new", "discovery");
        await incoming.ConnectAsync(host.ListeningPort);

        Assert.Equal("another_server", await existing.WaitForGoodbyeAsync(Timeout));
    }
}
```

- [ ] **Step 3: Run the tests to verify they pass against the current host**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj --filter "FullyQualifiedName~SendspinHostServiceArbitrationTests"`
Expected: **PASS** — 4 tests. (These characterize behavior the current host already implements.)

If any test hangs or flakes, confirm the host bound a port (Task 3) and that `StopAdvertisingAsync` ran; the waits are bounded by `Timeout` and will fail rather than hang indefinitely.

- [ ] **Step 4: Commit**

```bash
git add tests/Sendspin.SDK.Tests/Client/FakeServer.cs tests/Sendspin.SDK.Tests/Client/SendspinHostServiceArbitrationTests.cs
git commit -m "test: add loopback FakeServer harness and arbitration characterization tests"
```

---

## Task 5: Wire `Decide` into the host service + verify E3 (`user_request`) on the wire

The reconnect scenario asserts `user_request`, which **fails** against the current host (it sends the non-spec `reconnecting`). That failing test drives routing `ArbitrateConnectionAsync` through the pure `ServerArbitration.Decide`.

**Files:**
- Modify: `tests/Sendspin.SDK.Tests/Client/SendspinHostServiceArbitrationTests.cs` (add reconnect test)
- Modify: `src/Sendspin.SDK/Client/SendSpinHostService.cs` (`ArbitrateConnectionAsync` body)

- [ ] **Step 1: Add the failing reconnect test**

Append this test to `SendspinHostServiceArbitrationTests.cs` (inside the class):

```csharp
    [Fact]
    public async Task SameServerReconnect_SendsUserRequestToStaleConnection()
    {
        await using var host = await StartHostAsync();
        await using var first = new FakeServer("srv-1", "discovery");
        await first.ConnectAsync(host.ListeningPort);
        await WaitForServerConnectedAsync(host, "srv-1");

        await using var second = new FakeServer("srv-1", "discovery"); // same server_id reconnecting
        await second.ConnectAsync(host.ListeningPort);

        Assert.Equal("user_request", await first.WaitForGoodbyeAsync(Timeout));
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj --filter "FullyQualifiedName~SameServerReconnect_SendsUserRequestToStaleConnection"`
Expected: **FAIL** — `Assert.Equal() Failure: Expected "user_request", Actual "reconnecting"`.

- [ ] **Step 3: Route `ArbitrateConnectionAsync` through `ServerArbitration.Decide`**

In `src/Sendspin.SDK/Client/SendSpinHostService.cs`, replace the **body** of `ArbitrateConnectionAsync` (everything between the opening `{` and closing `}` of the method) with:

```csharp
        ActiveServerConnection? existingConnection;
        lock (_connectionsLock)
        {
            // There is at most one active connection.
            existingConnection = _connections.Values.FirstOrDefault();
        }

        var result = ServerArbitration.Decide(
            newServerId,
            newClient.ConnectionReason,
            existingConnection?.ServerId,
            existingConnection?.Client.ConnectionReason,
            LastPlayedServerId);

        _logger.LogInformation(
            "Arbitration: {Rationale}. New={NewServerId} (reason={NewReason}), Existing={ExistingServerId}",
            result.Rationale,
            newServerId,
            newClient.ConnectionReason ?? "discovery",
            existingConnection?.ServerId ?? "(none)");

        if (result.AcceptNew)
        {
            if (existingConnection is not null)
            {
                // LoserGoodbyeReason is non-null whenever there is an existing connection to drop.
                await DisconnectExistingAsync(existingConnection, result.LoserGoodbyeReason!);
            }

            return true;
        }

        // New server rejected (an existing connection always exists on this path).
        _logger.LogInformation("Arbitration: Rejecting {NewServerId}, sending goodbye", newServerId);
        try
        {
            await newConnection.DisconnectAsync(result.LoserGoodbyeReason!);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disconnecting rejected server {ServerId}", newServerId);
        }

        return false;
```

Leave `DisconnectExistingAsync`, `WaitForHandshakeAsync`, and the rest of the class unchanged. The XML-doc summary on `ArbitrateConnectionAsync` still applies; update its inline numbered priority comment to reference `ServerArbitration` if desired (optional, no behavior impact).

- [ ] **Step 4: Run the full arbitration suite to verify all pass**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj --filter "FullyQualifiedName~SendspinHostServiceArbitrationTests"`
Expected: **PASS** — 5 tests (the 4 characterization tests still pass; reconnect now returns `user_request`).

- [ ] **Step 5: Run the entire suite to confirm no regression**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj`
Expected: **PASS** — all tests (existing 192 + the new arbitration/persistence/port/decision tests), 0 failed.

- [ ] **Step 6: Commit**

```bash
git add src/Sendspin.SDK/Client/SendSpinHostService.cs tests/Sendspin.SDK.Tests/Client/SendspinHostServiceArbitrationTests.cs
git commit -m "refactor: route host arbitration through ServerArbitration.Decide; normalize reconnect goodbye to user_request"
```

---

## Task 6: Documentation & issue closeout

**Files:**
- Modify: `README.md`
- Issue: comment on and close #26 (done at PR time)

- [ ] **Step 1: Document the persistence seam and arbitration in the README**

In `README.md`, find the section that documents host/server-initiated mode and `IStaticDelayStore` (search for `IStaticDelayStore`). Add a subsection near it:

````markdown
### Multi-server arbitration & last-played persistence

When multiple servers can reach a player (server-initiated mode), `SendspinHostService` arbitrates
which one is active, completing each server's handshake first and then applying the spec's decision:

- a new `connection_reason: playback` server takes over from a `discovery` one;
- a `discovery` server cannot displace a `playback` one;
- when both are equal, the **last-played** server wins, else the existing connection stays.

The loser is sent `client/goodbye` with reason `another_server`; a same-server reconnect drops the
stale socket with `user_request`.

To let the tie-break survive restarts, supply an `ILastPlayedServerStore`:

```csharp
public sealed class FileLastPlayedServerStore : ILastPlayedServerStore
{
    private readonly string _path;
    public FileLastPlayedServerStore(string path) => _path = path;
    public string? Load() => File.Exists(_path) ? File.ReadAllText(_path) : null;
    public void Save(string serverId) => File.WriteAllText(_path, serverId);
}

await using var host = new SendspinHostService(
    loggerFactory,
    lastPlayedServerStore: new FileLastPlayedServerStore("last-played.txt"));
```

The store is optional and best-effort: a throwing implementation is logged and never breaks
arbitration. The existing `LastPlayedServerIdChanged` event and `lastPlayedServerId` seed parameter
continue to work (the seed wins over the store when both are supplied).
````

- [ ] **Step 2: Verify the README edit reads correctly**

Run: `git diff README.md`
Expected: the new subsection appears once, well-formed, near the `IStaticDelayStore` docs.

- [ ] **Step 3: Commit**

```bash
git add README.md
git commit -m "docs: document multi-server arbitration and ILastPlayedServerStore"
```

- [ ] **Step 4: Issue closeout (at PR creation)**

When opening the PR, include a comment for issue #26 recording:

- Arbitration, last-played tracking, and the `another_server` goodbye were already implemented in
  `SendspinHostService` before the issue was filed; the audit's `sendspin-dotnet` row is stale.
- This PR adds the `ILastPlayedServerStore` persistence seam, normalizes the same-server reconnect
  goodbye to `user_request`, and adds exhaustive decision-table + loopback end-to-end test coverage.
- Cross-repo follow-up: the `Sendspin/conformance` audit doc should correct the dotnet row.

Use `Closes #26` in the PR body.

---

## Final verification

- [ ] **Run the whole suite once more**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj`
Expected: **PASS** — 0 failed. Confirm no new analyzer warnings in the changed files.

- [ ] **Confirm `src/` diff is intentional and minimal**

Run: `git diff origin/dev --stat`
Expected: new `ServerArbitration.cs`, `ILastPlayedServerStore.cs`; modified `SendSpinHostService.cs`, `SendSpinListener.cs`, `README.md`; new test files. No unrelated changes.
