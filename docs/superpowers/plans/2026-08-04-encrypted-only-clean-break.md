# Encrypted-Only Clean Break Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Sendspin SDK v10 speak only the encrypted Sendspin protocol, deleting every path that falls back to the legacy plaintext protocol, and give a legacy server a clear, non-retrying diagnostic.

**Architecture:** A required `SendspinClientOptions` object carries the Curve25519 identity and stores, and `INoiseSessionInfo` becomes a non-nullable constructor argument — so a client that speaks plaintext is no longer constructible. `NoiseWireFraming` (which implements both `IWireFraming` and `INoiseSessionInfo`) becomes the only framing, wired by factory rather than by hand. Handshake failures are then classified as permanent or ambiguous, and permanent ones never re-enter the reconnect loop.

**Tech Stack:** C# / .NET (`net8.0;net10.0` multi-target), xUnit, `Noise.NET` 1.0.0, source-generated `System.Text.Json`.

## Global Constraints

- Spec source of truth: `Sendspin/spec` @ `7c04eb7`. Re-pinning to `d9154c45` is **out of scope** (issue #92).
- Package version stays `9.1.0`. The bump to `10.0.0` is **out of scope** (issue #91).
- Target frameworks: `net8.0;net10.0`. Code must compile on both — `KeepAliveTimeout` and similar net9+ APIs stay behind `#if NET9_0_OR_GREATER`.
- Nullable reference types are enabled and these are errors: `CS8600;CS8601;CS8602;CS8603;CS8604;CS8618;CS8625`.
- No self-referencing text in commit messages (no AI attribution, no `Co-Authored-By`).
- Baseline before starting: **325 tests passing, 0 failing** on `net10.0`. Every task must end at least as green.
- Full test command used throughout:
  `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo`
- Single-test command used throughout:
  `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo --filter "FullyQualifiedName~<TestName>"`
- Work happens on branch `feat/encrypted-only-clean-break`, already created from `main` @ `cad7efd`.

## File Structure

**Created:**

| File | Responsibility |
|---|---|
| `src/Sendspin.SDK/Client/SendspinClientOptions.cs` | The single required options object for both client and host construction |
| `src/Sendspin.SDK/Connection/SendspinHandshakeException.cs` | `HandshakeFailureKind` + the typed permanent-failure exception |
| `tests/Sendspin.SDK.Tests/Client/TestClient.cs` | Shared encrypted-client test factory and `FakeNoiseSession` |
| `tests/Sendspin.SDK.Tests/Connection/HandshakeFailureTests.cs` | Coverage for classification, no-retry, and backoff |

**Modified:**

| File | Change |
|---|---|
| `src/Sendspin.SDK/Client/SendSpinClient.cs` | Options ctor; delete legacy hello flow, timeout fork, admissibility bypass, `ConnectionReason` |
| `src/Sendspin.SDK/Client/SendSpinHostService.cs` | Options ctor; required framing; drop `FromConnectionReason` |
| `src/Sendspin.SDK/Client/ServerArbitration.cs` | Delete `FromConnectionReason` |
| `src/Sendspin.SDK/Connection/SendSpinConnection.cs` | Required framing; handshake-failure classification; `CreateForDial` wiring |
| `src/Sendspin.SDK/Connection/IncomingConnection.cs` | Required framing; legacy-server diagnostic |
| `src/Sendspin.SDK/Connection/ConnectionOptions.cs` | Add `HandshakeFailureBackoffMs` |
| `src/Sendspin.SDK/Connection/Framing/IWireFraming.cs` | Doc comment loses the `PlaintextWireFraming` reference |
| `src/Sendspin.SDK/Protocol/Messages/ClientHelloMessage.cs` | Collapse to the single encrypted shape |
| `tools/interop/InteropClient/Program.cs` | Follow the host-service constructor change |
| `docs/encryption-and-linein-plan.md` | Record the Option A decision in §6 |
| 10 test files under `tests/Sendspin.SDK.Tests/Client/` | Migrate to `TestClient` |

**Deleted:**

- `src/Sendspin.SDK/Connection/Framing/PlaintextWireFraming.cs`
- `tests/Sendspin.SDK.Tests/Connection/PlaintextWireFramingTests.cs`

---

### Task 1: `SendspinClientOptions` and the additive options constructor

Introduce the options object and a new constructor **without** removing anything. The suite stays green because the old constructor still exists and still takes the legacy path. This isolates the new seam for review before the risky migration.

**Files:**
- Create: `src/Sendspin.SDK/Client/SendspinClientOptions.cs`
- Modify: `src/Sendspin.SDK/Client/SendSpinClient.cs:150-161` (constructor)
- Test: `tests/Sendspin.SDK.Tests/Client/SendspinClientOptionsTests.cs`

**Interfaces:**
- Consumes: `SendspinIdentity`, `IPairingRecordStore`, `NoiseCipherSuite`, `ClientCapabilities`, `IClockSynchronizer`, `IAudioPipeline`, `IStaticDelayStore`, `IPinLockoutStore`, `IAudioCaptureDevice`, `ISourceAudioEncoderFactory` — all existing.
- Produces: `SendspinClientOptions` (required init property `Identity`); `SendspinClientService(ILogger<SendspinClientService>, ISendspinConnection, INoiseSessionInfo, SendspinClientOptions)`. Tasks 2–5 consume both.

- [ ] **Step 1: Write the failing test**

Create `tests/Sendspin.SDK.Tests/Client/SendspinClientOptionsTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection.Noise;

namespace Sendspin.SDK.Tests.Client;

public class SendspinClientOptionsTests
{
    private sealed class StubSession : INoiseSessionInfo
    {
        public string? ServerId => "GFsV9tLaSQm9HcFWpKsgYQOr7wFTvNUtkmFwuVz3zoo";
        public NoisePsk? MatchedPsk => new(NoiseConstants.SentinelPsk.ToArray(), PskCategory.Sentinel);
        public ReadOnlyMemory<byte>? HandshakeHash => new byte[32];
    }

    [Fact]
    public void OptionsConstructor_BuildsAClientOverTheGivenSession()
    {
        var connection = new FakeSendspinConnection();

        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection,
            new StubSession(),
            new SendspinClientOptions
            {
                Identity = SendspinIdentity.Generate(),
                Capabilities = new ClientCapabilities { ClientName = "opts-test" },
            });

        // ServerId stays null until server/init lands; construction succeeding over an
        // explicit session is the behavior under test.
        Assert.Null(client.ServerId);
    }

    [Fact]
    public void Options_DefaultsToChaChaPolySuite()
    {
        var options = new SendspinClientOptions { Identity = SendspinIdentity.Generate() };
        Assert.Equal(NoiseCipherSuite.ChaChaPoly, options.Suite);
    }
}
```

> `ClientName` lives on `ClientCapabilities` (`ClientCapabilities.cs:20`), not on `SendspinClientService` — the service's only relevant public property is `ServerId` (`SendSpinClient.cs:98`). Do not assert on a `client.ClientName` or `client.Capabilities`; neither exists.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo --filter "FullyQualifiedName~SendspinClientOptionsTests"`

Expected: FAIL to compile — `SendspinClientOptions` does not exist.

- [ ] **Step 3: Create the options type**

Create `src/Sendspin.SDK/Client/SendspinClientOptions.cs`:

```csharp
using Sendspin.SDK.Audio;
using Sendspin.SDK.Audio.Source;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Connection.Noise.Pairing;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Client;

/// <summary>
/// Everything a Sendspin client needs that is not the connection itself.
/// </summary>
/// <remarks>
/// The identity is required: under the encrypted protocol a client's <c>client_id</c>
/// IS its Curve25519 public key, so there is no such thing as a client without one.
/// This type is the single construction seam for both the dial path
/// (<see cref="SendspinClientService.CreateForDial"/>) and the listen path
/// (<see cref="SendspinHostService"/>).
/// </remarks>
public sealed class SendspinClientOptions
{
    /// <summary>
    /// Persistent Curve25519 identity. <c>client_id</c> is derived from it, and the spec
    /// requires it to survive reboots — persist and reuse the same keypair.
    /// </summary>
    required public SendspinIdentity Identity { get; init; }

    /// <summary>Pairing record store holding the PSKs this client has been paired with.</summary>
    public IPairingRecordStore? PairingRecordStore { get; init; }

    /// <summary>Roles, features and names advertised in <c>client/hello</c>.</summary>
    public ClientCapabilities Capabilities { get; init; } = new();

    /// <summary>Noise cipher suite announced in <c>client/init</c>.</summary>
    public NoiseCipherSuite Suite { get; init; } = NoiseCipherSuite.ChaChaPoly;

    /// <summary>Clock synchronizer. A <see cref="KalmanClockSynchronizer"/> is created when null.</summary>
    public IClockSynchronizer? ClockSynchronizer { get; init; }

    /// <summary>Audio pipeline for the player role.</summary>
    public IAudioPipeline? AudioPipeline { get; init; }

    /// <summary>Persistence for the player's static delay calibration.</summary>
    public IStaticDelayStore? StaticDelayStore { get; init; }

    /// <summary>Lockout counter persistence for the PIN pairing methods.</summary>
    public IPinLockoutStore? PinLockoutStore { get; init; }

    /// <summary>Capture device for the <c>source@v1</c> role.</summary>
    public IAudioCaptureDevice? CaptureDevice { get; init; }

    /// <summary>Encoder factory for the <c>source@v1</c> role.</summary>
    public ISourceAudioEncoderFactory? SourceEncoderFactory { get; init; }
}
```

- [ ] **Step 4: Add the new constructor, delegating to the existing one**

In `src/Sendspin.SDK/Client/SendSpinClient.cs`, directly above the existing constructor, add:

```csharp
    /// <summary>
    /// Constructs a client for the encrypted Sendspin protocol.
    /// </summary>
    /// <param name="session">
    /// The Noise session backing this connection. In production this is the same
    /// <see cref="NoiseWireFraming"/> instance the connection uses for framing.
    /// </param>
    public SendspinClientService(
        ILogger<SendspinClientService> logger,
        ISendspinConnection connection,
        INoiseSessionInfo session,
        SendspinClientOptions options)
        : this(
            logger,
            connection,
            options.ClockSynchronizer,
            options.Capabilities,
            options.AudioPipeline,
            options.StaticDelayStore,
            session,
            options.PairingRecordStore,
            options.PinLockoutStore,
            options.CaptureDevice,
            options.SourceEncoderFactory)
    {
        _identity = options.Identity;
        _suite = options.Suite;
    }
```

Add the two backing fields next to `_noiseSession` (around `SendSpinClient.cs:27`):

```csharp
    private readonly SendspinIdentity? _identity;
    private readonly NoiseCipherSuite _suite = NoiseCipherSuite.ChaChaPoly;
```

They are nullable/defaulted only for the duration of Tasks 1–2; Task 3 makes `_identity` non-nullable.

- [ ] **Step 5: Run the new tests**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo --filter "FullyQualifiedName~SendspinClientOptionsTests"`

Expected: PASS (2 tests).

- [ ] **Step 6: Run the full suite**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo`

Expected: PASS, 327 total (325 baseline + 2 new).

- [ ] **Step 7: Commit**

```bash
git add src/Sendspin.SDK/Client/SendspinClientOptions.cs src/Sendspin.SDK/Client/SendSpinClient.cs tests/Sendspin.SDK.Tests/Client/SendspinClientOptionsTests.cs
git commit -m "feat(client): add SendspinClientOptions and the options constructor

Introduces the single construction seam for the encrypted protocol: a required
Curve25519 identity plus the stores and capabilities the client needs. The
existing constructor is untouched, so behavior is unchanged.

Groundwork for #78."
```

---

### Task 2: Shared `TestClient` helper and test migration

Migrate every test that constructs a client onto an encrypted session. **This is the task most likely to surface real behavioral differences** — the legacy path skipped the admissibility gate and used a client-speaks-first handshake. Migrate one file at a time.

**Files:**
- Create: `tests/Sendspin.SDK.Tests/Client/TestClient.cs`
- Modify (one per step): `SendspinClientServiceArtworkTests.cs`, `SendspinClientServiceColorTests.cs`, `SendspinClientServiceErrorStateTests.cs`, `SendspinClientServiceEventTests.cs`, `SendspinClientServicePlayerFormatTests.cs`, `SendspinClientServiceServerStateTests.cs`, `SendspinClientServiceStaticDelayTests.cs`, `SendspinClientServiceTimeSyncTests.cs`, `SendspinClientServiceVisualizerTests.cs`, `SendspinClientServiceEncryptedFlowTests.cs`

**Interfaces:**
- Consumes: `SendspinClientOptions` and the options constructor from Task 1.
- Produces: `TestClient.Create(PskCategory, bool, Action<SendspinClientOptions>?)` returning `(SendspinClientService Client, FakeSendspinConnection Connection, FakeNoiseSession Session)`; the `internal sealed class FakeNoiseSession`. Tasks 3–8 use both.

- [ ] **Step 1: Create the shared helper**

Create `tests/Sendspin.SDK.Tests/Client/TestClient.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection.Noise;

namespace Sendspin.SDK.Tests.Client;

/// <summary>Mutable <see cref="INoiseSessionInfo"/> stand-in for tests.</summary>
internal sealed class FakeNoiseSession : INoiseSessionInfo
{
    internal const string FakeServerId = "GFsV9tLaSQm9HcFWpKsgYQOr7wFTvNUtkmFwuVz3zoo";

    public string? ServerId { get; set; } = FakeServerId;
    public NoisePsk? MatchedPsk { get; set; }
    public ReadOnlyMemory<byte>? HandshakeHash { get; set; } = new byte[32];
}

/// <summary>
/// Builds a client over the encrypted protocol with a fake Noise session.
/// </summary>
/// <remarks>
/// The default is a paired, long-term-keyed session (trust <c>user</c>) — the normal
/// operating state — so role tests are not incidentally blocked by the server/activate
/// admissibility table or the source@v1 trust gate. Tests that exercise gating pass
/// <see cref="PskCategory.Sentinel"/> explicitly.
/// </remarks>
internal static class TestClient
{
    internal static (SendspinClientService Client, FakeSendspinConnection Connection, FakeNoiseSession Session)
        Create(
            PskCategory category = PskCategory.LongTerm,
            bool unpairedAccess = false,
            Action<SendspinClientOptions>? configure = null)
    {
        var connection = new FakeSendspinConnection();
        var session = new FakeNoiseSession
        {
            MatchedPsk = new NoisePsk(NoiseConstants.SentinelPsk.ToArray(), category),
        };

        var options = new SendspinClientOptions
        {
            Identity = SendspinIdentity.Generate(),
            Capabilities = new ClientCapabilities { UnpairedAccessEnabled = unpairedAccess },
        };
        configure?.Invoke(options);

        var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection,
            session,
            options);

        return (client, connection, session);
    }

    /// <summary>
    /// Drives the encrypted handshake to completion: server/hello, then a server/activate
    /// granting playback and the given roles. Use in tests that need a connected client.
    /// </summary>
    internal static void CompleteHandshake(
        FakeSendspinConnection connection,
        params string[] activeRoles)
    {
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""
            {"type":"server/hello","payload":{"name":"srv"}}
            """);

        string roles = string.Join(",", activeRoles.Select(r => $"\"{r}\""));
        connection.RaiseTextMessageReceived(
            $$"""
            {"type":"server/activate","payload":{"activities":["playback"],"active_roles":[{{roles}}]}}
            """);
    }
}
```

> **Correction applied during execution:** `Action<SendspinClientOptions>` does not compile — `SendspinClientOptions` is `init`-only throughout, so assigning inside the callback is CS8852. The shipped helper takes `Action<TestClientOptions>`, a mutable 1:1 mirror copied into the real options at construction. Call sites are unchanged (the lambda's type is inferred), so the usage shown in later tasks still holds. The two types must be kept in sync by hand — a new property on `SendspinClientOptions` produces no compile error, only an option tests cannot set.
>
> `CompleteHandshake`'s raw string also needs `$$$` with `{{{roles}}}`, not `$$`/`{{roles}}`: the `server/activate` JSON ends in `]}}`, a two-brace run, so a `$$` prefix hits CS9007.

- [ ] **Step 2: Verify the helper compiles against the existing suite**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo`

Expected: PASS, 327. Nothing uses the helper yet; this only proves it compiles.

- [ ] **Step 3: Commit the helper**

```bash
git add tests/Sendspin.SDK.Tests/Client/TestClient.cs
git commit -m "test: add shared encrypted-client test factory"
```

- [ ] **Step 4: Migrate the first file**

In `tests/Sendspin.SDK.Tests/Client/SendspinClientServiceVisualizerTests.cs` (1 construction — smallest, so failures are easiest to read), replace the manual `new SendspinClientService(...)` with:

```csharp
var (client, connection, _) = TestClient.Create();
```

Delete any now-unused `NullLogger`/`FakeSendspinConnection` locals the replacement orphans.

- [ ] **Step 5: Run that file's tests**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo --filter "FullyQualifiedName~SendspinClientServiceVisualizerTests"`

Expected: PASS.

If a test now fails because the client never reaches a connected state, add `TestClient.CompleteHandshake(connection, "visualizer@v1");` after construction. **Do not** weaken an assertion to make it pass — the encrypted flow completing on `server/activate` is the real behavior v10 ships.

- [ ] **Step 6: Repeat Steps 4–5 for each remaining file, in this order**

Ascending by construction count, so problems surface in the smallest file first:

1. `SendspinClientServiceErrorStateTests.cs` (1)
2. `SendspinClientServicePlayerFormatTests.cs` (2)
3. `SendspinClientServiceTimeSyncTests.cs` (2)
4. `SendspinClientServiceEventTests.cs` (5)
5. `SendspinClientServiceColorTests.cs` (6)
6. `SendspinClientServiceArtworkTests.cs` (8)
7. `SendspinClientServiceServerStateTests.cs` (11)
8. `SendspinClientServiceStaticDelayTests.cs` (11)

Commit after each file:

```bash
git add tests/Sendspin.SDK.Tests/Client/<file>.cs
git commit -m "test: migrate <file> to the encrypted test client"
```

> `SendspinClientServiceEventTests.cs:40,48` asserts `ConnectionReason == "playback"`. That property only ever populates on the legacy path and Task 3 deletes it. Delete those two assertions here and note it in the commit body — the surrounding test keeps its other assertions.

- [ ] **Step 7: Collapse the duplicate fake in the encrypted tests**

In `SendspinClientServiceEncryptedFlowTests.cs`, delete the private `FakeNoiseSession` class and the private `CreateEncryptedClient` helper, and route its call sites through `TestClient.Create(category: ..., unpairedAccess: ...)`. Do the same for the private session fakes in `SendspinClientServiceManagementTests.cs`, `SendspinClientServicePairingTests.cs`, `SendspinClientServicePinPairingTests.cs`, and `SendspinClientServiceSourceTests.cs` if they declare their own.

- [ ] **Step 8: Run the full suite**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo`

Expected: PASS. Total may drop by 2 (the deleted `ConnectionReason` assertions live inside existing tests, so the count should hold at 327 — investigate any other change).

- [ ] **Step 9: Commit**

```bash
git add tests/Sendspin.SDK.Tests/Client/
git commit -m "test: route all client tests through the shared encrypted factory

Every client test now runs on a Noise session rather than the legacy
plaintext path, so the suite measures the protocol v10 actually ships."
```

---

### Task 3: Delete the legacy protocol path from `SendspinClientService`

**Files:**
- Modify: `src/Sendspin.SDK/Client/SendSpinClient.cs` — lines `27` (field), `150-161` (old ctor), `224-231` (legacy hello), `239` (timeout fork), `685-703` (hello tail), `806-819` (admissibility bypass), `106`/`386`/`572`/`700` (`ConnectionReason`)
- Test: `tests/Sendspin.SDK.Tests/Client/SendspinClientServiceEncryptedFlowTests.cs`

**Interfaces:**
- Consumes: `TestClient.Create` from Task 2.
- Produces: `SendspinClientService` with a non-nullable `INoiseSessionInfo _session` and no `ConnectionReason` property. Task 4 relies on `ConnectionReason` being gone.

- [ ] **Step 1: Write the failing security regression test**

Append to `tests/Sendspin.SDK.Tests/Client/SendspinClientServiceEncryptedFlowTests.cs`:

```csharp
    [Fact]
    public void InadmissibleActivate_AlwaysDisconnects_NoBypassPath()
    {
        // A Sentinel-keyed session with unpaired access off may not be granted playback.
        // Before the clean break, a client with no session info skipped this gate entirely.
        var (client, connection, _) = TestClient.Create(
            category: PskCategory.Sentinel,
            unpairedAccess: false);
        using var _c = client;

        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""
            {"type":"server/hello","payload":{"name":"srv"}}
            """);
        connection.RaiseTextMessageReceived("""
            {"type":"server/activate","payload":{"activities":["playback"],"active_roles":["player@v1"]}}
            """);

        Assert.Equal(ConnectionState.Disconnected, connection.State);
        Assert.Equal("pairing_required", connection.LastDisconnectReason);
    }
```

> The reason is `pairing_required`, not `unauthorized`. Traced through `ValidateActivateAdmissibility` (`SendSpinClient.cs:824-840`): a Sentinel PSK with `activities: ["playback"]` fails `AllowedSet` while `unpairedAccessEnabled` is false, but *would* pass with it true — and the rule ordering prefers `pairing_required` in exactly that case. `unauthorized` is the fall-through for activations that stay inadmissible either way.

- [ ] **Step 2: Run it to verify it passes already**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo --filter "FullyQualifiedName~InadmissibleActivate_AlwaysDisconnects"`

Expected: PASS. This test guards behavior that already works when a session is present; it exists to fail loudly if the bypass is ever reintroduced. Commit it before the deletion so the guard predates the change.

```bash
git add tests/Sendspin.SDK.Tests/Client/SendspinClientServiceEncryptedFlowTests.cs
git commit -m "test: pin that an inadmissible server/activate always disconnects"
```

- [ ] **Step 3: Make the session non-nullable and delete the old constructor**

> **Carried forward from Task 2:** `SendspinClientServiceEncryptedFlowTests.LegacyFlow_WithoutNoiseSession_IsUnchanged` is the last caller of the legacy constructor and the only coverage of the legacy branch in `HandleServerHello`. Task 2 deliberately left it. Delete that test in this step — it cannot compile once the constructor is gone, and its subject is the path being removed. Expect the suite total to drop by one.

In `SendSpinClient.cs`, change the field at line 27:

```csharp
    private readonly INoiseSessionInfo _session;
    private readonly SendspinIdentity _identity;
    private readonly NoiseCipherSuite _suite;
```

Delete the 11-optional-parameter constructor entirely. Promote the Task 1 constructor from a delegating one to the real one, moving the former constructor's body into it:

```csharp
    public SendspinClientService(
        ILogger<SendspinClientService> logger,
        ISendspinConnection connection,
        INoiseSessionInfo session,
        SendspinClientOptions options)
    {
        _logger = logger;
        _connection = connection;
        _session = session;
        _identity = options.Identity;
        _suite = options.Suite;
        _capabilities = options.Capabilities;
        _pairingStore = options.PairingRecordStore;
        _pinLockoutStore = options.PinLockoutStore;
        _captureDevice = options.CaptureDevice;
        _sourceEncoderFactory = options.SourceEncoderFactory;
        _clockSynchronizer = options.ClockSynchronizer ?? new KalmanClockSynchronizer();

        // ...retain the remainder of the deleted constructor's body verbatim
        // (source pipeline construction, audio pipeline wiring, event subscriptions).
    }
```

Then replace every remaining `_noiseSession` reference with `_session`.

- [ ] **Step 4: Delete the legacy handshake branch**

At `SendSpinClient.cs:224-231`, delete the whole `if (_noiseSession is null)` block — the client never speaks first. At line 239, replace the timeout fork with the flat spec value:

```csharp
        // 30 s per the spec's recommended handshake-phase timeout.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
```

- [ ] **Step 5: Delete the admissibility bypass**

At `SendSpinClient.cs:806-819`, replace the nullable lookup and its bypass:

```csharp
    private bool ValidateActivateAdmissibility(ServerActivatePayload payload, out string goodbyeReason)
    {
        goodbyeReason = string.Empty;
        var psk = _session.MatchedPsk;
        if (psk is null)
        {
            // A session always has a matched PSK once the handshake completes; reaching
            // here means the framing surfaced an activate before transport mode.
            goodbyeReason = "unauthorized";
            return false;
        }

        // ...rest unchanged
```

- [ ] **Step 6: Delete the legacy hello tail and `ConnectionReason`**

At `SendSpinClient.cs:685-703`, the `if (_noiseSession is not null) { ... return; }` guard becomes unconditional — delete the `if` and the `return`, and delete the legacy tail below it (lines 699-703 assigning `ServerId`/`ConnectionReason` and the legacy log). Keep the `ServerHelloReceived` invocation and `FinishHandshake` call in whichever branch the encrypted flow needs (the encrypted flow finishes in `HandleServerActivate`, so `FinishHandshake()` does **not** move up).

Delete the `ConnectionReason` property at line 106 and its assignments at lines 386 and 572.

- [ ] **Step 7: Build and fix the compiler's list**

Run: `dotnet build c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo`

Expected: errors at `SendSpinHostService.cs:638` and `:663`, which still reference the deleted `ConnectionReason`.

**Apply this stopgap — it is required, not optional.** The solution must compile for Step 8's test run, so `SendSpinHostService.cs` cannot be left broken for Task 4:

```csharp
// line 638 — Task 4 replaces this with the activities-only PriorityOf
            : ConnectionPriority.Empty;
```

```csharp
// line 660-664 — drop the reason from the arbitration log
        _logger.LogInformation(
            "Arbitration: {Rationale}. New={NewServerId}, Existing={ExistingServerId}",
            result.Rationale,
            newServerId,
            existingConnection?.ServerId ?? "(none)");
```

Task 4 then deletes `FromConnectionReason` itself and finishes the host-service work. Fix all other errors **inside `SendSpinClient.cs`** now.

- [ ] **Step 8: Run the full suite**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo`

Expected: PASS at the Task 2 total.

- [ ] **Step 9: Commit**

```bash
git add src/Sendspin.SDK/Client/SendSpinClient.cs src/Sendspin.SDK/Client/SendSpinHostService.cs
git commit -m "feat(client)!: drop the legacy plaintext protocol path

The Noise session is now a required constructor argument, so a client that
speaks the pre-encryption protocol cannot be constructed. Removes the
client/hello-first flow, the 10s legacy handshake timeout, and the
server/activate admissibility bypass that skipped the spec's gate whenever
no session was present.

ConnectionReason goes with it: server/hello carries only a name under
encryption, so the property was permanently null.

Part of #78."
```

---

### Task 4: `SendspinHostService` on the options object

**Files:**
- Modify: `src/Sendspin.SDK/Client/SendSpinHostService.cs` — constructor (lines ~90-115), framing construction (`395-401`), `PriorityOf` (`635-638`), arbitration log (`663`)
- Modify: `src/Sendspin.SDK/Client/ServerArbitration.cs:38-46`
- Modify: `tools/interop/InteropClient/Program.cs:38-45`
- Test: `tests/Sendspin.SDK.Tests/Client/ServerArbitrationTests.cs:70-78`

**Interfaces:**
- Consumes: `SendspinClientOptions` (Task 1), the required-session client constructor (Task 3).
- Produces: `SendspinHostService(ILoggerFactory, SendspinClientOptions, ListenerOptions?, AdvertiserOptions?, string?, ILastPlayedServerStore?)`. Task 8 uses it for the listen-path diagnostic.

- [ ] **Step 1: Delete the legacy arbitration test**

In `tests/Sendspin.SDK.Tests/Client/ServerArbitrationTests.cs`, delete the `FromConnectionReason_MapsLegacyReasons` theory (lines ~70-78) and its `[InlineData]` rows.

- [ ] **Step 2: Confirm nothing else depended on it**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo --filter "FullyQualifiedName~ServerArbitrationTests"`

Expected: PASS with a lower count (the legacy theory is gone). There is no red-first step here — this task begins by removing coverage for a method being deleted, so the gate is that the remaining arbitration tests still pass.

- [ ] **Step 3: Delete `FromConnectionReason`**

In `src/Sendspin.SDK/Client/ServerArbitration.cs`, delete lines 38-46 (the doc comment and the method).

- [ ] **Step 4: Fix `PriorityOf`**

In `SendSpinHostService.cs`, replace lines 631-638:

```csharp
    /// <summary>
    /// A connection's arbitration priority, from its declared server/activate activities.
    /// </summary>
    private static ConnectionPriority PriorityOf(SendspinClientService client)
        => client.LastServerActivate is { } activate
            ? ServerArbitration.FromActivities(activate.ActivitiesList)
            : ConnectionPriority.Empty;
```

And at line 660-664, drop the now-meaningless reason from the log:

```csharp
        _logger.LogInformation(
            "Arbitration: {Rationale}. New={NewServerId}, Existing={ExistingServerId}",
            result.Rationale,
            newServerId,
            existingConnection?.ServerId ?? "(none)");
```

- [ ] **Step 5: Move the host service onto the options object**

Replace the constructor's parameter list:

```csharp
    public SendspinHostService(
        ILoggerFactory loggerFactory,
        SendspinClientOptions options,
        ListenerOptions? listenerOptions = null,
        AdvertiserOptions? advertiserOptions = null,
        string? lastPlayedServerId = null,
        ILastPlayedServerStore? lastPlayedServerStore = null)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SendspinHostService>();
        _options = options;
        _capabilities = options.Capabilities;
        // ...retain the remaining assignments, sourcing identity, pairingRecordStore,
        // pinLockoutStore, audioPipeline and clockSynchronizer from `options`.
```

Store the whole `options` in a `private readonly SendspinClientOptions _options;` field so per-connection client construction can pass it straight through.

- [ ] **Step 6: Make framing unconditional**

At `SendSpinHostService.cs:395-401`, replace the conditional with an unconditional construction:

```csharp
            // Each incoming connection gets its own Noise framing — the per-connection
            // crypto state cannot be shared. The server (dialer) is the Noise initiator
            // per spec; our side responds.
            var framing = new Connection.Noise.NoiseWireFraming(
                _options.Identity,
                _options.PairingRecordStore is null
                    ? null
                    : new Connection.Noise.RecordPskResolver(_options.PairingRecordStore),
                _options.Suite);
```

> Signature confirmed at `NoiseWireFraming.cs:50-58`:
> `NoiseWireFraming(SendspinIdentity identity, INoisePskResolver? pskResolver = null, NoiseCipherSuite suite = NoiseCipherSuite.ChaChaPoly)`.
> A null resolver defaults to `SentinelPskResolver.Instance` (pre-pairing), which is the correct behavior when no record store is configured.

Then update the per-connection client construction to the Task 3 shape:

```csharp
            var client = new SendspinClientService(
                _loggerFactory.CreateLogger<SendspinClientService>(),
                connection,
                framing,
                _options);
```

Note this drops the per-connection `clockSync` fallback if one existed — preserve it by passing an options copy with `ClockSynchronizer` set, using a `with`-style re-construction, since `SendspinClientOptions` has `init` properties.

- [ ] **Step 7: Update the interop client**

In `tools/interop/InteropClient/Program.cs`, replace the host construction:

```csharp
await using var host = new SendspinHostService(
    loggerFactory,
    new SendspinClientOptions
    {
        Identity = identity,
        Capabilities = caps,
        PairingRecordStore = records,
    },
    listenerOptions: new ListenerOptions { Port = port },
    advertiserOptions: new AdvertiserOptions { Enabled = false });
```

- [ ] **Step 8: Build and run the full suite**

Run: `dotnet build c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo`
Then: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo`

Expected: build clean, tests PASS. Fix every host-service test that constructed the service with the old parameter list (`SendspinHostServiceArbitrationTests.cs`, `SendspinHostServicePersistenceTests.cs`, `SendspinHostServicePortTests.cs`).

- [ ] **Step 9: Commit**

```bash
git add src/Sendspin.SDK/Client/SendSpinHostService.cs src/Sendspin.SDK/Client/ServerArbitration.cs tools/interop/InteropClient/Program.cs tests/Sendspin.SDK.Tests/Client/
git commit -m "feat(host)!: require SendspinClientOptions and drop legacy arbitration

The host service now takes the same options object as the client, builds Noise
framing for every incoming connection unconditionally, and ranks arbitration
purely on declared server/activate activities. ServerArbitration.FromConnectionReason
is deleted.

Part of #78."
```

---

### Task 5: Required framing, `CreateForDial`, and deleting `PlaintextWireFraming`

**Files:**
- Modify: `src/Sendspin.SDK/Connection/SendSpinConnection.cs:37-45`
- Modify: `src/Sendspin.SDK/Connection/IncomingConnection.cs:30-38`
- Modify: `src/Sendspin.SDK/Connection/Framing/IWireFraming.cs:12-13`
- Modify: `src/Sendspin.SDK/Client/SendSpinClient.cs` (add `CreateForDial`)
- Delete: `src/Sendspin.SDK/Connection/Framing/PlaintextWireFraming.cs`
- Delete: `tests/Sendspin.SDK.Tests/Connection/PlaintextWireFramingTests.cs`
- Test: `tests/Sendspin.SDK.Tests/Connection/SendspinConnectionReconnectTests.cs` (4 construction sites)

**Interfaces:**
- Consumes: `SendspinClientOptions` (Task 1), required-session client (Task 3).
- Produces: `SendspinClientService.CreateForDial(ILoggerFactory, SendspinClientOptions, ConnectionOptions?)` returning `SendspinClientService`. Task 7's tests dial through it.

- [ ] **Step 1: Write the failing test**

Append to `tests/Sendspin.SDK.Tests/Connection/PlaintextWireFramingTests.cs`'s directory a new file is unnecessary — instead add to `tests/Sendspin.SDK.Tests/Client/SendspinClientOptionsTests.cs`:

```csharp
    [Fact]
    public void CreateForDial_WiresOneNoiseFramingAsBothFramingAndSession()
    {
        var options = new SendspinClientOptions { Identity = SendspinIdentity.Generate() };

        using var client = SendspinClientService.CreateForDial(
            NullLoggerFactory.Instance,
            options);

        // The dial path must be encrypted end to end: the client's session id is the
        // framing's, and no plaintext framing exists to fall back to.
        Assert.NotNull(client);
        Assert.Null(client.ServerId); // no server/init yet
    }
```

Add `using Microsoft.Extensions.Logging.Abstractions;` if not already present.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo --filter "FullyQualifiedName~CreateForDial"`

Expected: FAIL to compile — `CreateForDial` does not exist.

- [ ] **Step 3: Add the factory**

In `src/Sendspin.SDK/Client/SendSpinClient.cs`, add a static factory:

```csharp
    /// <summary>
    /// Builds a client that dials a server, wiring one <see cref="NoiseWireFraming"/> as
    /// both the connection's framing and the client's Noise session so the two cannot
    /// drift apart.
    /// </summary>
    public static SendspinClientService CreateForDial(
        ILoggerFactory loggerFactory,
        SendspinClientOptions options,
        ConnectionOptions? connectionOptions = null)
    {
        var framing = new NoiseWireFraming(
            options.Identity,
            options.PairingRecordStore is null ? null : new RecordPskResolver(options.PairingRecordStore),
            options.Suite);

        var connection = new SendspinConnection(
            loggerFactory.CreateLogger<SendspinConnection>(),
            connectionOptions,
            framing);

        return new SendspinClientService(
            loggerFactory.CreateLogger<SendspinClientService>(),
            connection,
            framing,
            options);
    }
```

- [ ] **Step 4: Make framing required on both connection types**

`SendSpinConnection.cs:37-45`:

```csharp
    public SendspinConnection(
        ILogger<SendspinConnection> logger,
        ConnectionOptions? options,
        IWireFraming framing)
    {
        _logger = logger;
        _options = options ?? new ConnectionOptions();
        _framing = framing;
    }
```

`IncomingConnection.cs:30-38`: same treatment — `IWireFraming framing` with no default, and `_framing = framing;`.

- [ ] **Step 5: Delete the plaintext framing**

```bash
git rm src/Sendspin.SDK/Connection/Framing/PlaintextWireFraming.cs
git rm tests/Sendspin.SDK.Tests/Connection/PlaintextWireFramingTests.cs
```

In `IWireFraming.cs:12-13`, remove the sentence referencing it:

```csharp
/// encrypts outbound application frames, decrypts inbound ones, and splits/reassembles
/// fragmented messages.
```

And in the `IsTransportReady` and `Start()` doc comments, drop the "Always true for plaintext framing" / "Empty for plaintext framing" clauses.

- [ ] **Step 6: Fix the four reconnect-test construction sites**

In `tests/Sendspin.SDK.Tests/Connection/SendspinConnectionReconnectTests.cs`, each `new SendspinConnection(logger, options)` becomes:

```csharp
new SendspinConnection(logger, options, new NoiseWireFraming(SendspinIdentity.Generate()))
```

Add `using Sendspin.SDK.Connection.Noise;`.

- [ ] **Step 7: Build and run the full suite**

Run: `dotnet build c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo`
Then: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo`

Expected: build clean, tests PASS (total drops by the deleted `PlaintextWireFramingTests` count, rises by 1 for `CreateForDial`).

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(connection)!: require wire framing and delete the plaintext path

PlaintextWireFraming is gone, so both connection types now require an explicit
framing and no type can be constructed into a plaintext-speaking state.
CreateForDial assembles the dial path so the identity, framing and Noise session
are wired from one options object.

Closes the construction half of #78."
```

---

### Task 6: Collapse `ClientHelloMessage` to the encrypted shape

**Files:**
- Modify: `src/Sendspin.SDK/Protocol/Messages/ClientHelloMessage.cs:21-54`, `60-82`
- Modify: `src/Sendspin.SDK/Client/SendSpinClient.cs` (the `CreateClientHelloMessage` caller)
- Test: `tests/Sendspin.SDK.Tests/Protocol/MessageSerializerTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `ClientHelloMessage.Create(string name, List<string> supportedRoles, ...)` — the `clientId` parameter is gone. No later task depends on this.

- [ ] **Step 1: Write the failing test**

Append to `tests/Sendspin.SDK.Tests/Protocol/MessageSerializerTests.cs`:

```csharp
    [Fact]
    public void ClientHello_NeverSerializesClientIdOrVersion()
    {
        var hello = ClientHelloMessage.Create(
            name: "test-client",
            supportedRoles: ["player@v1"]);

        string json = MessageSerializer.Serialize(hello);

        // Under encryption both travel in client/init instead.
        Assert.DoesNotContain("client_id", json);
        Assert.DoesNotContain("\"version\"", json);
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo --filter "FullyQualifiedName~ClientHello_NeverSerializes"`

Expected: FAIL to compile — `Create` still requires a `clientId` first parameter.

- [ ] **Step 3: Drop the parameter**

In `ClientHelloMessage.cs`, remove `string? clientId` from `Create`'s parameter list and remove both `ClientId = clientId` and `Version = clientId is null ? null : 1` from the initializer. Delete the `ClientId` and `Version` properties from `ClientHelloPayload` (lines 62-68 and 76-82) along with their `JsonPropertyName`/`JsonIgnore` attributes.

- [ ] **Step 4: Fix the caller**

In `SendSpinClient.cs`, find `CreateClientHelloMessage` and drop the `clientId` argument it passes.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo`

Expected: PASS. Fix any test asserting the legacy hello shape — including `SendspinClientServiceEncryptedFlowTests`'s `Assert.Null(hello.Payload.ClientId)`, which no longer compiles now that the property is gone. Delete those assertions; the serialization test above replaces them.

- [ ] **Step 6: Commit**

```bash
git add src/Sendspin.SDK/Protocol/Messages/ClientHelloMessage.cs src/Sendspin.SDK/Client/SendSpinClient.cs tests/Sendspin.SDK.Tests/
git commit -m "feat(protocol)!: client/hello carries only the encrypted shape

client_id and version travel in client/init, so the dual-shape Create overload
and the two payload properties are gone.

Part of #78."
```

---

### Task 7: Handshake-failure classification on the dial path

**Files:**
- Create: `src/Sendspin.SDK/Connection/SendspinHandshakeException.cs`
- Create: `tests/Sendspin.SDK.Tests/Connection/HandshakeFailureTests.cs`
- Modify: `src/Sendspin.SDK/Connection/ConnectionOptions.cs` (add property)
- Modify: `src/Sendspin.SDK/Connection/SendSpinConnection.cs:260-271` (close handling), `282-290` (fatal handling), `339-369` (`HandleConnectionLostAsync`), `371-400` (`TryReconnectAsync`)

**Interfaces:**
- Consumes: `IWireFraming.IsTransportReady` (existing).
- Produces: `HandshakeFailureKind` enum, `SendspinHandshakeException`, `ConnectionOptions.HandshakeFailureBackoffMs`. Task 8 reuses the enum and exception.

- [ ] **Step 1: Write the failing tests**

Create `tests/Sendspin.SDK.Tests/Connection/HandshakeFailureTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Framing;

namespace Sendspin.SDK.Tests.Connection;

/// <summary>
/// A pre-7.0.0 aiosendspin server closes with code 1000 and no reply when it receives
/// client/init. That is a permanent condition: v10 speaks only the encrypted protocol,
/// so retrying cannot help and the SDK must say so.
/// </summary>
[Collection("RealSockets")]
public class HandshakeFailureTests
{
    /// <summary>
    /// Test framing with a settable transport-ready flag, standing in for the
    /// deleted PlaintextWireFraming. <c>IsTransportReady</c> is what classifies a
    /// close as legacy-server versus an ordinary mid-session drop.
    /// </summary>
    private sealed class StubFraming : IWireFraming
    {
        public string? FatalOnInbound { get; set; }
        public bool IsTransportReady { get; set; }
        public IReadOnlyList<WireFrame> Start() => [WireFrame.FromText("""{"type":"client/init"}""")];
        public IEnumerable<WireFrame> EncodeText(string json) => [WireFrame.FromText(json)];
        public IEnumerable<WireFrame> EncodeBinary(ReadOnlyMemory<byte> data) => [WireFrame.FromBinary(data)];
        public InboundFrameResult ProcessInbound(WireFrame frame) =>
            FatalOnInbound is { } reason ? InboundFrameResult.Fatal(reason) : InboundFrameResult.None;
        public void Reset() { }
    }

    [Fact]
    public void HandshakeFailureBackoff_DefaultsTo30Seconds()
    {
        Assert.Equal(30000, new ConnectionOptions().HandshakeFailureBackoffMs);
    }

    [Fact]
    public void LegacyServerException_CarriesTheUpgradeGuidance()
    {
        var ex = new SendspinHandshakeException(HandshakeFailureKind.LegacyServer);

        Assert.Equal(HandshakeFailureKind.LegacyServer, ex.Kind);
        Assert.Contains("does not support Sendspin encryption", ex.Message);
        Assert.Contains("aiosendspin >= 7.0.0", ex.Message);
        Assert.Contains("9.x", ex.Message);
    }

    [Fact]
    public void HandshakeRejectedException_NamesTheReason()
    {
        var ex = new SendspinHandshakeException(HandshakeFailureKind.HandshakeRejected, "unsupported suite");

        Assert.Equal(HandshakeFailureKind.HandshakeRejected, ex.Kind);
        Assert.Contains("unsupported suite", ex.Message);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo --filter "FullyQualifiedName~HandshakeFailureTests"`

Expected: FAIL to compile — `SendspinHandshakeException` and `HandshakeFailureBackoffMs` do not exist.

- [ ] **Step 3: Create the exception type**

Create `src/Sendspin.SDK/Connection/SendspinHandshakeException.cs`:

```csharp
namespace Sendspin.SDK.Connection;

/// <summary>Why a Sendspin handshake failed permanently.</summary>
public enum HandshakeFailureKind
{
    /// <summary>
    /// The peer closed the connection during client/init without replying — the
    /// signature of a server predating the encrypted protocol (aiosendspin &lt; 7.0.0).
    /// </summary>
    LegacyServer,

    /// <summary>
    /// The peer spoke the encrypted protocol but the handshake was rejected: an
    /// unusable PSK, an unsupported suite, a version mismatch, or malformed input.
    /// </summary>
    HandshakeRejected,
}

/// <summary>
/// A permanent handshake failure. Retrying cannot succeed, so the connection does not
/// re-enter the reconnect loop when this is raised.
/// </summary>
public sealed class SendspinHandshakeException : Exception
{
    public SendspinHandshakeException(HandshakeFailureKind kind, string? detail = null)
        : base(BuildMessage(kind, detail))
    {
        Kind = kind;
    }

    /// <summary>The classification of this failure.</summary>
    public HandshakeFailureKind Kind { get; }

    private static string BuildMessage(HandshakeFailureKind kind, string? detail) => kind switch
    {
        HandshakeFailureKind.LegacyServer =>
            "Server closed the connection during client/init and does not support Sendspin "
            + "encryption. Upgrade the server to aiosendspin >= 7.0.0, or pin Sendspin SDK 9.x.",
        HandshakeFailureKind.HandshakeRejected =>
            $"Sendspin handshake rejected: {detail ?? "no detail"}.",
        _ => $"Sendspin handshake failed: {kind}.",
    };
}
```

- [ ] **Step 4: Add the backoff option**

In `src/Sendspin.SDK/Connection/ConnectionOptions.cs`, after `MaxReconnectDelayMs`:

```csharp
    /// <summary>
    /// Delay before retrying after an ambiguous handshake failure, in milliseconds.
    /// Handshake failures are not ordinary socket drops, so they back off separately
    /// from <see cref="ReconnectDelayMs"/>. Permanent failures (see
    /// <see cref="SendspinHandshakeException"/>) are never retried at all.
    /// </summary>
    public int HandshakeFailureBackoffMs { get; set; } = 30000;
```

- [ ] **Step 5: Run the new tests**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo --filter "FullyQualifiedName~HandshakeFailureTests"`

Expected: PASS (3 tests).

- [ ] **Step 6: Classify failures in the receive loop**

In `SendSpinConnection.cs`, add a field beside `_connectionLostGuard`:

```csharp
    private SendspinHandshakeException? _permanentFailure;
```

Replace the close handling at lines 260-271:

```csharp
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInformation("Server closed connection: {Status} - {Description}",
                            result.CloseStatus, result.CloseStatusDescription);

                        // A clean close before the framing reached transport mode is the
                        // legacy-server signature: aiosendspin < 7.0.0 fails to deserialize
                        // client/init and closes with 1000, no reply.
                        if (!_framing.IsTransportReady
                            && result.CloseStatus == WebSocketCloseStatus.NormalClosure)
                        {
                            await FailPermanentlyAsync(
                                new SendspinHandshakeException(HandshakeFailureKind.LegacyServer));
                            return;
                        }

                        await HandleConnectionLostAsync();
                        return;
                    }
```

Replace the fatal handling at lines 284-290:

```csharp
                if (inbound.FatalReason is { } fatal)
                {
                    // Per spec: close without sending an application-level error message.
                    _logger.LogWarning("Wire framing failure: {Reason}; closing connection", fatal);
                    await FailPermanentlyAsync(
                        new SendspinHandshakeException(HandshakeFailureKind.HandshakeRejected, fatal));
                    return;
                }
```

- [ ] **Step 7: Add the permanent-failure path**

Add beside `HandleConnectionLostAsync`:

```csharp
    /// <summary>
    /// Ends the connection without retrying. Used for conditions a retry cannot fix —
    /// a server that does not speak the encrypted protocol, or a rejected handshake.
    /// </summary>
    private async Task FailPermanentlyAsync(SendspinHandshakeException failure)
    {
        _permanentFailure = failure;
        _logger.LogError("{Message}", failure.Message);

        await CleanupWebSocketAsync();
        SetState(ConnectionState.Disconnected, failure.Message, failure);
    }
```

`SetState` must accept an exception so it reaches `ConnectionStateChangedEventArgs.Exception`. Check its current signature; if it takes only `(ConnectionState, string?)`, add an optional `Exception? exception = null` parameter and pass it into the event args.

- [ ] **Step 8: Apply the backoff and honor permanence in the reconnect loop**

In `TryReconnectAsync`, at the top of the `while` body:

```csharp
            if (_permanentFailure is not null)
            {
                SetState(ConnectionState.Disconnected, _permanentFailure.Message, _permanentFailure);
                return;
            }
```

And in `CalculateReconnectDelay`, when the last failure was an ambiguous handshake failure (framing not transport-ready at the time of loss), return `_options.HandshakeFailureBackoffMs` instead of the socket-drop schedule. Track that with a `private bool _lastLossDuringHandshake;` set in `HandleConnectionLostAsync` from `!_framing.IsTransportReady`.

Clear `_permanentFailure` in `ConnectAsync` (beside `_reconnectAttempt = 0;`) so an explicit reconnect by the app is allowed.

- [ ] **Step 9: Repair the existing reconnect test, which this change breaks**

**Read this before running the suite.** `SendspinConnectionReconnectTests.CleanServerClose_DrivesReconnect` asserts that a graceful server close drives a reconnect (windowsSpin issue #1 — a Music Assistant restart mid-session). Its server is a plain `SimpleWebSocketServer` that never speaks Noise, so after Task 5 its framing is a `NoiseWireFraming` stuck at `IsTransportReady == false`. Step 6's new rule classifies that close as `LegacyServer` and refuses to reconnect — the test fails, and it is **right to fail** as written.

The test's real intent is a mid-session drop, so give it a framing that has reached transport mode. In `SendspinConnectionReconnectTests.cs`, replace the framing argument added in Task 5 for this test with the shared stub:

```csharp
await using var connection = new SendspinConnection(
    NullLogger<SendspinConnection>.Instance,
    new ConnectionOptions { ReconnectDelayMs = 100, AutoReconnect = true },
    new StubFraming { IsTransportReady = true });
```

Promote `StubFraming` out of `HandshakeFailureTests` into its own file `tests/Sendspin.SDK.Tests/Connection/StubFraming.cs` (marked `internal sealed`) so both test classes share it. Apply the same treatment to any other test in that file whose close happens mid-session rather than mid-handshake.

- [ ] **Step 10: Add the three integration tests**

Append to `HandshakeFailureTests.cs`. These use `SimpleWebSocketServer`, the loopback harness `SendspinConnectionReconnectTests` already uses — do not write a new server.

```csharp
    [Fact]
    public async Task CleanCloseBeforeTransportMode_IsLegacyServer_AndNeverRetries()
    {
        using var server = new SimpleWebSocketServer();
        server.Start(0);

        var accepted = new TaskCompletionSource<WebSocketClientConnection>();
        server.ClientConnected += (_, c) => accepted.TrySetResult(c);

        // Framing that never reaches transport mode: the pre-7.0.0 server signature.
        await using var connection = new SendspinConnection(
            NullLogger<SendspinConnection>.Instance,
            new ConnectionOptions { AutoReconnect = true, ReconnectDelayMs = 10 },
            new StubFraming { IsTransportReady = false });

        var states = new List<ConnectionState>();
        connection.StateChanged += (_, e) => states.Add(e.NewState);

        await connection.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/sendspin"));
        var serverConn = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // aiosendspin < 7.0.0 answers client/init with a normal-closure close, no reply.
        await serverConn.CloseAsync();
        await Task.Delay(500);

        Assert.Equal(ConnectionState.Disconnected, connection.State);
        Assert.DoesNotContain(ConnectionState.Reconnecting, states);
    }

    [Fact]
    public async Task FramingFatal_DoesNotReenterTheReconnectLoop()
    {
        using var server = new SimpleWebSocketServer();
        server.Start(0);

        var accepted = new TaskCompletionSource<WebSocketClientConnection>();
        server.ClientConnected += (_, c) => accepted.TrySetResult(c);

        await using var connection = new SendspinConnection(
            NullLogger<SendspinConnection>.Instance,
            new ConnectionOptions { AutoReconnect = true, ReconnectDelayMs = 10 },
            new StubFraming { IsTransportReady = false, FatalOnInbound = "bad psk" });

        var states = new List<ConnectionState>();
        connection.StateChanged += (_, e) => states.Add(e.NewState);

        await connection.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/sendspin"));
        var serverConn = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Any inbound frame trips the framing's fatal path.
        await serverConn.SendTextAsync("{}");
        await Task.Delay(500);

        Assert.Equal(ConnectionState.Disconnected, connection.State);
        Assert.DoesNotContain(ConnectionState.Reconnecting, states);
    }

    [Fact]
    public async Task AmbiguousHandshakeDrop_BacksOffOnTheHandshakeSchedule()
    {
        using var server = new SimpleWebSocketServer();
        server.Start(0);

        var accepted = new TaskCompletionSource<WebSocketClientConnection>();
        server.ClientConnected += (_, c) => accepted.TrySetResult(c);

        // 5s handshake backoff vs a 50ms socket-drop delay: if the wrong schedule is
        // used, the client reconnects almost immediately and the assertion fails.
        await using var connection = new SendspinConnection(
            NullLogger<SendspinConnection>.Instance,
            new ConnectionOptions
            {
                AutoReconnect = true,
                ReconnectDelayMs = 50,
                HandshakeFailureBackoffMs = 5000,
            },
            new StubFraming { IsTransportReady = false });

        await connection.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/sendspin"));
        var serverConn = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Abort rather than close cleanly: an abort mid-handshake is the ambiguous case.
        serverConn.Abort();
        await Task.Delay(1000);

        Assert.Equal(ConnectionState.Reconnecting, connection.State);
    }
```

> `SimpleWebSocketServer`'s exact member names (`Start(int)`, `Port`, `ClientConnected`, and the per-connection `CloseAsync`/`SendTextAsync`/`Abort`) come from `SendspinConnectionReconnectTests.cs` and `SimpleWebSocketServerTests.cs`. Read both and match whatever they actually expose — if `SendTextAsync` or `Abort` do not exist on `WebSocketClientConnection`, use the nearest equivalent those tests already use.

- [ ] **Step 11: Run the full suite**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo`

Expected: PASS.

- [ ] **Step 12: Commit**

```bash
git add src/Sendspin.SDK/Connection/ tests/Sendspin.SDK.Tests/Connection/
git commit -m "feat(connection): classify handshake failures and stop the reconnect storm

A clean close before transport mode is the legacy-server signature and a framing
fatal is a rejected handshake; both are permanent, raise a typed
SendspinHandshakeException, and no longer feed the reconnect loop. Ambiguous
mid-handshake drops back off on the new HandshakeFailureBackoffMs.

Closes #83."
```

---

### Task 8: Legacy-server diagnostic on the listen path

The listen path has no reconnect loop, so the fix here is purely diagnostic: a server that dials us and cannot complete the Noise handshake should say why, once, at `Warning`.

**Files:**
- Modify: `src/Sendspin.SDK/Connection/IncomingConnection.cs` (inbound frame handling)
- Test: `tests/Sendspin.SDK.Tests/Connection/HandshakeFailureTests.cs`

**Interfaces:**
- Consumes: `HandshakeFailureKind`, `SendspinHandshakeException` (Task 7).
- Produces: nothing later tasks depend on.

- [ ] **Step 1: Write the failing test**

Append to `HandshakeFailureTests.cs`:

```csharp
    [Theory]
    [InlineData(false, "does not support Sendspin encryption")]
    [InlineData(true, "handshake rejected")]
    public void FramingFatal_ClassifiesByTransportReadiness(bool transportReady, string expected)
    {
        var framing = new StubFraming
        {
            IsTransportReady = transportReady,
            FatalOnInbound = "expected server/init message",
        };

        var result = framing.ProcessInbound(WireFrame.FromText("{}"));
        Assert.NotNull(result.FatalReason);

        // This mirrors the classification IncomingConnection applies before closing.
        var failure = new SendspinHandshakeException(
            framing.IsTransportReady
                ? HandshakeFailureKind.HandshakeRejected
                : HandshakeFailureKind.LegacyServer,
            result.FatalReason);

        Assert.Contains(expected, failure.Message, StringComparison.OrdinalIgnoreCase);
    }
```

> The suite has no logging test double, and this change does not add one — asserting on captured log output would mean taking a new test dependency for a one-line diagnostic. This test pins the classification rule instead; Task 7's integration tests already prove the wiring on the dial path.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo --filter "FullyQualifiedName~FramingFatal_ClassifiesByTransportReadiness"`

Expected: PASS immediately — this pins a rule the exception type already implements. Its value is regression protection for Step 3's wiring.

- [ ] **Step 3: Add the diagnostic**

In `IncomingConnection.cs`, find where `ProcessInbound`'s `FatalReason` is handled and log the classified message before closing:

```csharp
                if (inbound.FatalReason is { } fatal)
                {
                    var failure = new SendspinHandshakeException(
                        _framing.IsTransportReady
                            ? HandshakeFailureKind.HandshakeRejected
                            : HandshakeFailureKind.LegacyServer,
                        fatal);
                    _logger.LogWarning("{Message}", failure.Message);
                    // ...retain the existing close behavior
                }
```

- [ ] **Step 4: Run the full suite**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Sendspin.SDK/Connection/IncomingConnection.cs tests/Sendspin.SDK.Tests/Connection/HandshakeFailureTests.cs
git commit -m "feat(connection): diagnose handshake failures on the listen path

Part of #83."
```

---

### Task 9: Record the decision and verify against the reference server

**Files:**
- Modify: `docs/encryption-and-linein-plan.md` §6

**Interfaces:**
- Consumes: everything above.
- Produces: nothing.

- [ ] **Step 1: Replace the undecided §6 heading and preamble**

In `docs/encryption-and-linein-plan.md`, change the section 6 heading from:

```markdown
## 6. Optional: version gating & downgrade (decision pending — NOT committed to)
```

to:

```markdown
## 6. Version gating & downgrade — DECIDED: Option A (2026-08-04)
```

And replace the paragraph beginning "How does a new-SDK client behave against a pre-encryption..." through "...the default assumption is Option A unless real-world migration pain forces Option B." with:

```markdown
**Decision: Option A, the clean break.** v10.0.0 speaks only the encrypted protocol. The v9.x
line remains available for deployments whose servers predate encryption, and applications move
to 10.x when their user base is ready for encryption.

Confirmed 2026-08-04 on the evidence below plus three developments since Phase 1 exit: the
reference client is encrypted-only and only its *server* dual-stacks (upgrade pressure is
one-directional), the spec still has no downgrade negotiation, and aiosendspin #298 documents
`allow_noncompliant_clients` as defaulting to `False` in a future version and being removed
with all backwards-compat paths later.

Implemented by the encrypted-only clean break (design:
`docs/superpowers/specs/2026-08-04-encrypted-only-clean-break-design.md`), closing #78 and #83.
Option B below is retained as a record of the rejected alternative.
```

- [ ] **Step 2: Correct the §8 status error**

§8 lists the Phase 2 `client/state` reshape as delivered. It was not (issue #77). Append to the Phase 2 paragraph:

```markdown
**Correction (2026-08-04):** the `client/state` reshape to a boolean `available` was listed
above as delivered but never landed — tracked as #77.
```

- [ ] **Step 3: Run the full suite one final time**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo`

Expected: PASS, 0 failed.

- [ ] **Step 4: Verify both target frameworks build**

Run: `dotnet build c:\CodeProjects\SendspinSDK\SendspinSDK.slnx --nologo`

Expected: clean build for `net8.0` and `net10.0`. `net8.0` is the one most likely to break on a `#if NET9_0_OR_GREATER` boundary.

- [ ] **Step 5: Verify against the real reference server**

Run the interop harness, which pins `aiosendspin[server]==7.0.0`:

```bash
bash tools/interop/run.sh
```

Expected: the same scenarios that passed before the change still pass. This is the conformance gate — a green unit suite does not prove the wire is still right.

- [ ] **Step 6: Commit**

```bash
git add docs/encryption-and-linein-plan.md
git commit -m "docs: record the Option A decision and correct the Phase 2 status

Section 6 has said 'no decision made yet' since Phase 1 exit while the code
defaulted to plaintext. Records the clean break, and corrects section 8's claim
that the client/state reshape shipped (it did not — #77).

Closes #78."
```

---

## Verification Checklist

Run before opening the PR. Each maps to a success criterion in the design.

- [ ] `git grep -rn "PlaintextWireFraming"` returns nothing.
- [ ] `git grep -rn "FromConnectionReason"` returns nothing outside `MIGRATION-*.md`.
- [ ] `git grep -n "_noiseSession is null\|_identity is null"` returns nothing.
- [ ] `SendspinClientService` cannot be constructed without an identity and a session (verified by the compiler — no nullable overload exists).
- [ ] `ValidateActivateAdmissibility` has no `return true` before the admissibility table.
- [ ] `dotnet test ... -f net10.0` passes with 0 failures.
- [ ] `dotnet build` is clean for both `net8.0` and `net10.0`.
- [ ] `tools/interop/run.sh` passes against `aiosendspin[server]==7.0.0`.
- [ ] `src/Sendspin.SDK/Sendspin.SDK.csproj` still says `<Version>9.1.0</Version>` (the bump belongs to #91).
