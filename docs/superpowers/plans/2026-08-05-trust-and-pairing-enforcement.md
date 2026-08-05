# Trust and Pairing Enforcement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close four security and conformance defects on the `server/activate` → pairing → `server/command` paths: an unauthenticated server can mint a permanent credential, and can open the capture device without ever activating the source role.

**Architecture:** One live-capability check (`CanOffer`) governs which pairing method may run, replacing scattered per-arm logic and resolving the disallowed-method and no-store cases into the single spec rule that covers both. The `source@v1` gate moves to the point where the capture device opens, so every caller is covered rather than one chokepoint. Two pairing-lifecycle defects are fixed with no new public interface surface.

**Tech Stack:** C# / .NET (`net8.0;net10.0` multi-target), xUnit, `Noise.NET` 1.0.0, source-generated `System.Text.Json`.

## Global Constraints

- Design of record: `docs/superpowers/specs/2026-08-05-trust-and-pairing-enforcement-design.md`. Where this plan and the design disagree, **the design governs** — report the discrepancy rather than silently picking one.
- Target frameworks `net8.0;net10.0`. Code must compile on both.
- Nullable reference types are enabled and these are errors: `CS8600;CS8601;CS8602;CS8603;CS8604;CS8618;CS8625`.
- Package version stays `9.1.0`. The bump to `10.0.0` is issue #91 and out of scope.
- **No new public API surface.** The design deliberately avoids adding to `IPairingRecordStore`, `INoisePskResolver`, or `IWireFraming`. If a task seems to need it, stop and report.
- Commit messages must contain no AI attribution, no `Co-Authored-By`, and no self-reference. Write as the repo owner.
- Baseline entering this plan: **333 tests passing, 0 failing** on `net10.0`.
- **Express test-count gates as deltas, not absolute totals**, and record the absolute figure observed. Zero failing is the invariant that matters.
- Full test command: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo`
- Filtered: append `--filter "FullyQualifiedName~<ClassName>"`
- Branch `fix/trust-and-pairing-enforcement`, already created from `main` @ `85c43ec`.

## File Structure

**Modified:**

| File | Change |
|---|---|
| `src/Sendspin.SDK/Client/SendSpinClient.cs` | `CanOffer`; reshape `HandlePairingActivate`; drop the disconnect; supply the source predicate; mark PSK used; reset the pairing counter |
| `src/Sendspin.SDK/Audio/Source/SourceStreamPipeline.cs` | `Func<bool> canStream` ctor param, checked in `StartStreamingAsync`; corrected `<remarks>` |
| `src/Sendspin.SDK/Connection/Noise/PairingRecordStore.cs` | `RecordPskResolver.Resolve` becomes a pure lookup; `PairingRecord.Used` doc |
| `tests/Sendspin.SDK.Tests/Client/SendspinClientServicePairingTests.cs` | Negative pairing tests |
| `tests/Sendspin.SDK.Tests/Client/SendspinClientServiceSourceTests.cs` | Source gate tests; `FakeCaptureDevice` moves out |

**Created:**

| File | Responsibility |
|---|---|
| `tests/Sendspin.SDK.Tests/Client/FakeCaptureDevice.cs` | Shared capture-device double, promoted from `SendspinClientServiceSourceTests` |

---

### Task 1: One admissibility rule for pairing methods (#74, #76, #87-1)

**Files:**
- Modify: `src/Sendspin.SDK/Client/SendSpinClient.cs` — `HandlePairingActivate` (currently ~`:941-975`)
- Test: `tests/Sendspin.SDK.Tests/Client/SendspinClientServicePairingTests.cs`

**Interfaces:**
- Consumes: `TestClient.Create(PskCategory, bool, Action<TestClientOptions>?)` — the shared test factory; `_session` (`INoiseSessionInfo`, non-nullable); `_pairingStore` (`IPairingRecordStore?`).
- Produces: `private bool CanOffer(string? method)` on `SendspinClientService`. No later task depends on it.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Sendspin.SDK.Tests/Client/SendspinClientServicePairingTests.cs`. Note the existing `Create()` helper at the top of that file builds a **Pairing**-keyed client with a store; these tests deliberately vary those two things.

```csharp
    /// <summary>
    /// Builds a client whose Noise session is keyed by the given PSK category, with an
    /// optional record store. The spec admits Sentinel + {pairing} because the PIN
    /// methods authenticate via CPace there — but pairing_psk specifically requires a
    /// session already keyed by the Pairing PSK.
    /// </summary>
    private static (SendspinClientService, FakeSendspinConnection) CreateWith(
        PskCategory category,
        bool withStore)
    {
        var (client, connection, _) = TestClient.Create(
            category,
            configure: options =>
            {
                if (withStore)
                    options.PairingRecordStore = new InMemoryPairingRecordStore();
            });
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        return (client, connection);
    }

    private static void SendPairingActivate(FakeSendspinConnection connection, string method) =>
        connection.RaiseTextMessageReceived(
            $$"""
            {"type":"server/activate","payload":{"activities":["pairing"],"selected_pair_method":"{{method}}"}}
            """);

    [Fact]
    public void PairingPsk_OnSentinelKeyedSession_AbortsAndNeverSendsFinalize()
    {
        // #74: a server on the published Sentinel PSK must not be able to obtain a
        // permanent credential. The abort must not close the connection (#76).
        var (client, connection) = CreateWith(PskCategory.Sentinel, withStore: true);
        using var _c = client;

        SendPairingActivate(connection, "pairing_psk");

        Assert.DoesNotContain(connection.SentMessages, m => m is ClientPairFinalizeMessage);
        var abort = Assert.Single(connection.SentMessages.OfType<PairAbortMessage>());
        Assert.Equal("method_not_supported", abort.Payload.Reason);
        Assert.Equal(ConnectionState.Connected, connection.State);
        Assert.Null(connection.LastDisconnectReason);
    }

    [Fact]
    public void PairingPsk_WithNoRecordStore_AbortsAndDoesNotClaimSuccess()
    {
        // #87-1: aborting up front means the server never persists a half we cannot use.
        var (client, connection) = CreateWith(PskCategory.Pairing, withStore: false);
        using var _c = client;

        bool paired = false;
        client.PairingCompleted += (_, _) => paired = true;

        SendPairingActivate(connection, "pairing_psk");

        Assert.DoesNotContain(connection.SentMessages, m => m is ClientPairFinalizeMessage);
        Assert.False(paired, "PairingCompleted must not fire when the record cannot be persisted");
        var abort = Assert.Single(connection.SentMessages.OfType<PairAbortMessage>());
        Assert.Equal("method_not_supported", abort.Payload.Reason);
        Assert.Equal(ConnectionState.Connected, connection.State);
    }

    [Fact]
    public void UnsupportedPairMethod_AbortsWithoutClosingTheConnection()
    {
        // #76: spec #123 — reply method_not_supported and leave the connection open so
        // the server can re-activate with a method we do offer.
        var (client, connection) = CreateWith(PskCategory.Pairing, withStore: true);
        using var _c = client;

        SendPairingActivate(connection, "telepathy");

        var abort = Assert.Single(connection.SentMessages.OfType<PairAbortMessage>());
        Assert.Equal("method_not_supported", abort.Payload.Reason);
        Assert.Equal(ConnectionState.Connected, connection.State);
        Assert.Null(connection.LastDisconnectReason);
    }

    [Fact]
    public void PairingPsk_OnPairingKeyedSessionWithStore_StillSendsFinalize()
    {
        // Positive control: the gate must not refuse the legitimate case.
        var (client, connection) = CreateWith(PskCategory.Pairing, withStore: true);
        using var _c = client;

        SendPairingActivate(connection, "pairing_psk");

        Assert.Single(connection.SentMessages.OfType<ClientPairFinalizeMessage>());
        Assert.DoesNotContain(connection.SentMessages, m => m is PairAbortMessage);
    }
```

Add `using System.Linq;` and `using Sendspin.SDK.Connection;` to the file if not already present (`ConnectionState` lives in `Sendspin.SDK.Connection`).

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo --filter "FullyQualifiedName~SendspinClientServicePairingTests"`

Expected: `PairingPsk_OnSentinelKeyedSession_AbortsAndNeverSendsFinalize` FAILS (a finalize *is* sent today), `PairingPsk_WithNoRecordStore_AbortsAndDoesNotClaimSuccess` FAILS (finalize sent, `PairingCompleted` fires), `UnsupportedPairMethod_AbortsWithoutClosingTheConnection` FAILS (the connection is disconnected today). The positive control should PASS already.

**Record which failed and how.** If the Sentinel test passes at this point, stop and report — that would mean the vulnerability is not reproducible as described and the design's premise needs re-checking.

- [ ] **Step 3: Add `CanOffer`**

In `src/Sendspin.SDK/Client/SendSpinClient.cs`, directly above `HandlePairingActivate`:

```csharp
    /// <summary>
    /// Whether this client can currently complete <paramref name="method"/> on this
    /// session. The spec's check is against live capability, which may have drifted
    /// from what supported_pair_methods advertised in client/hello.
    /// </summary>
    private bool CanOffer(string? method) => method switch
    {
        // pairing_psk is admissible only on a session already keyed by the Pairing PSK,
        // and only when the resulting long-term record can actually be persisted.
        "pairing_psk" => _session.MatchedPsk?.Category == PskCategory.Pairing
                         && _pairingStore is not null,
        "dynamic_pin" => _capabilities.PinPairingMethods.Contains("dynamic_pin"),
        "static_pin" => _capabilities.PinPairingMethods.Contains("static_pin"),
        _ => false,
    };
```

- [ ] **Step 4: Reshape `HandlePairingActivate`**

Replace the body's `switch` so the capability check runs before any method-specific work, and the abort no longer disconnects:

```csharp
    private void HandlePairingActivate(ServerActivatePayload payload)
    {
        _pinState = null;
        _pairingCounter++;

        if (!CanOffer(payload.SelectedPairMethod))
        {
            // Spec: reply method_not_supported and LEAVE THE CONNECTION OPEN. The server
            // may re-activate with another method, or re-handshake for a fresh
            // supported_pair_methods advertisement.
            _logger.LogWarning(
                "Cannot offer pair method {Method} on this session; aborting the attempt",
                payload.SelectedPairMethod);
            _connection.SendMessageAsync(new PairAbortMessage
            {
                Payload = new PairAbortPayload { Reason = "method_not_supported" },
            }).SafeFireAndForget(_logger);
            return;
        }

        switch (payload.SelectedPairMethod)
        {
            case "pairing_psk":
                _pendingPairingPsk = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
                _logger.LogInformation("Pairing PSK flow: delivering long-term PSK to server {ServerId}", ServerId);
                _connection.SendMessageAsync(new ClientPairFinalizeMessage
                {
                    Payload = new ClientPairFinalizePayload { LongTermPsk = B64Url(_pendingPairingPsk) },
                }).SafeFireAndForget(_logger);
                break;

            case "dynamic_pin":
                StartPinAttempt(dynamic: true);
                break;

            case "static_pin":
                StartPinAttempt(dynamic: false);
                break;
        }
    }
```

Two things changed beyond adding the guard, both deliberate:
- The `when _capabilities.PinPairingMethods.Contains(...)` clauses come off the PIN arms — `CanOffer` now owns that check, and leaving it in both places would let the two drift.
- The `default:` arm is gone; `CanOffer` returning false is the only route to an abort. The `switch` is exhaustive over what `CanOffer` admits, so no `default` is reachable.

- [ ] **Step 5: Update the method's doc comment**

The existing `<summary>` above `HandlePairingActivate` says "Anything else is aborted." Replace that sentence with:

```
    /// A method the matched PSK disallows, or that this client cannot currently
    /// complete, is refused with pair/abort reason 'method_not_supported' and the
    /// connection is left open (spec #123).
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo --filter "FullyQualifiedName~SendspinClientServicePairingTests"`

Expected: PASS, all tests in the class including the four new ones.

- [ ] **Step 7: Run the full suite**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo`

Expected: 0 failing, total up by 4 from the entering figure. If any pre-existing test now fails, investigate rather than adjusting it — a test that depended on the old disconnect-on-abort behavior is telling you something.

- [ ] **Step 8: Commit**

```bash
git add src/Sendspin.SDK/Client/SendSpinClient.cs tests/Sendspin.SDK.Tests/Client/SendspinClientServicePairingTests.cs
git commit -m "fix(pairing)!: gate pair methods on live capability and stop closing on abort

pairing_psk now requires a session already keyed by the Pairing PSK and a
configured record store, so a server on the published Sentinel PSK can no longer
obtain a permanent credential, and a store-less client aborts before the server
persists a half it could never authenticate against.

A refused method replies pair/abort method_not_supported and leaves the
connection open, per spec #123, so the server can re-activate or re-handshake.

Closes #74. Closes #76."
```

---

### Task 2: Gate the source role where the capture device opens (#75)

**Files:**
- Create: `tests/Sendspin.SDK.Tests/Client/FakeCaptureDevice.cs`
- Modify: `src/Sendspin.SDK/Audio/Source/SourceStreamPipeline.cs` — `<remarks>` (~`:16-21`), constructor (~`:40-54`), `StartStreamingAsync` (~`:73-80`)
- Modify: `src/Sendspin.SDK/Client/SendSpinClient.cs` — pipeline construction (~`:184-190`)
- Modify: `tests/Sendspin.SDK.Tests/Client/SendspinClientServiceSourceTests.cs`

**Interfaces:**
- Consumes: `TestClient.Create` with `options.CaptureDevice`; `LastServerHello.ActiveRoles`; `_session.MatchedPsk`.
- Produces: `SourceStreamPipeline(IAudioCaptureDevice, IClockSynchronizer, Func<IMessage,Task>, Func<byte[],Task>, ILogger, Func<bool>, ISourceAudioEncoderFactory?)` — note `canStream` goes **before** the optional `encoderFactory`, since a required parameter cannot follow an optional one. `internal sealed class FakeCaptureDevice` in namespace `Sendspin.SDK.Tests.Client`.

- [ ] **Step 1: Promote `FakeCaptureDevice` to a shared file**

Cut the `private sealed class FakeCaptureDevice` from `tests/Sendspin.SDK.Tests/Client/SendspinClientServiceSourceTests.cs` (currently ~`:19-31`) into a new file `tests/Sendspin.SDK.Tests/Client/FakeCaptureDevice.cs`, changed to `internal sealed`:

```csharp
using Sendspin.SDK.Audio.Source;
using Sendspin.SDK.Models;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Capture-device double. <see cref="Capturing"/> is the assertion that matters for
/// the source trust gate: it records whether the device was ever actually opened.
/// </summary>
internal sealed class FakeCaptureDevice : IAudioCaptureDevice
{
    public AudioFormat Format { get; } = new() { Codec = "pcm", SampleRate = 48000, Channels = 2, BitDepth = 16 };
    public bool Capturing { get; private set; }
    public event EventHandler<CapturedAudio>? AudioCaptured;

    public Task StartAsync(CancellationToken ct = default) { Capturing = true; return Task.CompletedTask; }
    public Task StopAsync(CancellationToken ct = default) { Capturing = false; return Task.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Emit(byte[] pcm, long captureTimeUs) =>
        AudioCaptured?.Invoke(this, new CapturedAudio(pcm, captureTimeUs));
}
```

Verify the `using` directives against the original — `AudioFormat` and `CapturedAudio` must resolve. Run the source tests to confirm the move alone breaks nothing:

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo --filter "FullyQualifiedName~SendspinClientServiceSourceTests"`
Expected: PASS, unchanged count.

Commit this move on its own:

```bash
git add tests/Sendspin.SDK.Tests/Client/
git commit -m "test: promote FakeCaptureDevice to a shared test double"
```

- [ ] **Step 2: Write the failing tests**

Append to `tests/Sendspin.SDK.Tests/Client/SendspinClientServiceSourceTests.cs`:

```csharp
    /// <summary>
    /// Builds a source-capable client, drives the encrypted handshake, and optionally
    /// activates the source role — so a test can reach the server/command path with the
    /// role either active or inactive, at either trust level.
    /// </summary>
    private static (SendspinClientService Client, FakeSendspinConnection Connection, FakeCaptureDevice Capture)
        CreateSourceClient(PskCategory category, bool activateSourceRole)
    {
        var capture = new FakeCaptureDevice();
        var (client, connection, _) = TestClient.Create(
            category,
            configure: options =>
            {
                options.CaptureDevice = capture;
                options.Capabilities = new ClientCapabilities { Roles = ["source@v1"] };
            });
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");

        // Empty activities deliberately. A Sentinel-keyed session granted ["playback"]
        // is INADMISSIBLE without unpaired access, so the client would disconnect with
        // 'pairing_required' before the server/command ever arrived and the test would
        // fail for the wrong reason. An empty set is admissible on both PSK categories
        // (and on LongTerm with an active role, via the withPlayback extension in
        // IsAdmissible) — and it is the more faithful attack shape, since #75 describes
        // a server that omits the role entirely and just sends the command.
        string roles = activateSourceRole ? "\"source@v1\"" : string.Empty;
        connection.RaiseTextMessageReceived(
            $$"""
            {"type":"server/activate","payload":{"activities":[],"active_roles":[{{roles}}]}}
            """);

        return (client, connection, capture);
    }

    private static void SendSourceStart(FakeSendspinConnection connection) =>
        connection.RaiseTextMessageReceived(
            """{"type":"server/command","payload":{"source":{"command":"start"}}}""");

    [Fact]
    public void SourceStart_AtTrustNone_NeverOpensTheCaptureDevice()
    {
        // #75: a Sentinel-keyed server must not be able to start capture via
        // server/command, bypassing the activate-time trust gate entirely.
        var (client, connection, capture) = CreateSourceClient(PskCategory.Sentinel, activateSourceRole: false);
        using var _c = client;

        SendSourceStart(connection);

        Assert.False(capture.Capturing, "the capture device must never open at trust 'none'");
    }

    [Fact]
    public void SourceStart_WithSourceRoleInactive_NeverOpensTheCaptureDevice()
    {
        // Even at user trust, a source that was never activated must not stream.
        var (client, connection, capture) = CreateSourceClient(PskCategory.LongTerm, activateSourceRole: false);
        using var _c = client;

        SendSourceStart(connection);

        Assert.False(capture.Capturing, "the capture device must never open with the role inactive");
    }

    [Fact]
    public void SourceStart_AtUserTrustWithActiveRole_OpensTheCaptureDevice()
    {
        // Positive control: a gate that refuses everything would pass both tests above.
        var (client, connection, capture) = CreateSourceClient(PskCategory.LongTerm, activateSourceRole: true);
        using var _c = client;

        SendSourceStart(connection);

        Assert.True(capture.Capturing, "the legitimate case must still stream");
    }
```

> Verified: `ClientCapabilities.Roles` is `List<string>` (`ClientCapabilities.cs:25`) with a default value, so assigning `Roles = ["source@v1"]` replaces it wholesale — which is what these tests want. `TestClient.Create` applies `configure` before re-applying `UnpairedAccessEnabled`, so replacing the whole `Capabilities` object is safe.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo --filter "FullyQualifiedName~SendspinClientServiceSourceTests"`

Expected: the two negative tests FAIL (`Capturing` is `true` — this is the vulnerability), the positive control PASSES.

If a negative test passes here, stop and report: either the vulnerability does not reproduce, or the test is not reaching the command path.

- [ ] **Step 4: Add the gate to the pipeline**

In `src/Sendspin.SDK/Audio/Source/SourceStreamPipeline.cs`, add the field beside the other delegates:

```csharp
    private readonly Func<bool> _canStream;
```

Add the constructor parameter **before** the optional `encoderFactory`:

```csharp
    /// <summary>Creates a source pipeline bound to a capture device and the connection's send paths.</summary>
    /// <param name="canStream">
    /// Evaluated immediately before the capture device is opened. Must be false unless the
    /// connection is at trust 'user' and the source role is currently active.
    /// </param>
    public SourceStreamPipeline(
        IAudioCaptureDevice capture,
        IClockSynchronizer clock,
        Func<IMessage, Task> sendMessageAsync,
        Func<byte[], Task> sendBinaryAsync,
        ILogger logger,
        Func<bool> canStream,
        ISourceAudioEncoderFactory? encoderFactory = null)
    {
        _capture = capture;
        _clock = clock;
        _sendMessageAsync = sendMessageAsync;
        _sendBinaryAsync = sendBinaryAsync;
        _logger = logger;
        _canStream = canStream;
        _encoderFactory = encoderFactory ?? new DefaultSourceAudioEncoderFactory();
    }
```

Check it in `StartStreamingAsync`, **before** `_streaming = true`:

```csharp
    private async Task StartStreamingAsync()
    {
        lock (_lock)
        {
            if (_streaming || _disposed)
                return;

            // Trust gate. Enforced here rather than at the command dispatch so that every
            // caller is covered — the activate path, the server/command path, and any path
            // added later. A source streams potentially sensitive audio, so the spec
            // requires a paired connection.
            if (!_canStream())
            {
                _logger.LogWarning(
                    "Refusing to stream: source@v1 requires user trust and an active source role");
                return;
            }

            _streaming = true;
        }
        // ...remainder unchanged
```

The check must sit **inside** the lock and **before** `_streaming = true`, so a refused start cannot leave the pipeline believing it is streaming.

- [ ] **Step 5: Correct the `<remarks>`**

The `<remarks>` block currently ends with a claim this task disproves:

```
/// The pipeline does not itself enforce trust; the client service refuses to activate
/// the source role at trust level <c>none</c>.
```

Replace those two lines with:

```
/// The pipeline enforces the spec's trust rule itself, at the point the capture device
/// opens: it refuses to stream unless <c>canStream</c> reports user trust and an active
/// source role. Activate-time checking alone is insufficient, because server/command
/// reaches this pipeline without passing through server/activate.
```

- [ ] **Step 6: Supply the predicate from the client**

In `src/Sendspin.SDK/Client/SendSpinClient.cs`, at the pipeline construction (~`:184`):

```csharp
            _sourcePipeline = new SourceStreamPipeline(
                _captureDevice,
                _clockSynchronizer,
                msg => _connection.SendMessageAsync(msg),
                data => _connection.SendBinaryAsync(data),
                _logger,
                IsSourceStreamingPermitted,
                _sourceEncoderFactory);
```

And add the predicate as a private method:

```csharp
    /// <summary>
    /// The spec's precondition for streaming captured audio: a paired ('user'-trust)
    /// connection with the source role currently active. Evaluated per start attempt,
    /// because both trust and the active-role set can change over a connection's life.
    /// </summary>
    private bool IsSourceStreamingPermitted() =>
        _session.MatchedPsk?.Category == PskCategory.LongTerm
        && (LastServerHello?.ActiveRoles.Any(r => r.StartsWith("source@", StringComparison.Ordinal)) ?? false);
```

This is a method group, so it is evaluated lazily at each start attempt — correct, since the pipeline is constructed before any `server/activate` has arrived.

- [ ] **Step 7: Run the tests**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo --filter "FullyQualifiedName~SendspinClientServiceSourceTests"`

Expected: PASS, all three new tests plus the pre-existing ones.

Pre-existing source tests drive `server/command source start` after activating the role at `PskCategory.LongTerm` via `TestClient`'s default, so they should continue to pass. If one fails because its client is not at `LongTerm` or does not activate the role, fix the **test setup** to reflect the real precondition — do not weaken the gate.

- [ ] **Step 8: Run the full suite and commit**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo`

Expected: 0 failing, total up by 3 from Task 1's figure.

```bash
git add src/Sendspin.SDK/Audio/Source/SourceStreamPipeline.cs src/Sendspin.SDK/Client/SendSpinClient.cs tests/Sendspin.SDK.Tests/Client/SendspinClientServiceSourceTests.cs
git commit -m "fix(source)!: enforce the source@v1 trust gate where the capture device opens

The gate ran only at server/activate time, so server/command reached the pipeline
without it and an unpaired server could open the capture device. It now runs
immediately before the device opens, covering every caller.

Closes #75."
```

---

### Task 3: `Resolve()` becomes a pure lookup (#87-2)

**Files:**
- Modify: `src/Sendspin.SDK/Connection/Noise/PairingRecordStore.cs` — `RecordPskResolver.Resolve` (~`:123-135`), `PairingRecord.Used` doc (~`:17`)
- Modify: `src/Sendspin.SDK/Client/SendSpinClient.cs` — `OnTextMessageReceived` (~`:629`)
- Test: `tests/Sendspin.SDK.Tests/Client/SendspinClientServicePairingTests.cs`

**Interfaces:**
- Consumes: `IPairingRecordStore.List()` / `.Upsert(PairingRecord)`; `NoiseConstants.DerivePskId(ReadOnlySpan<byte>)`; `NoisePsk.Key` (`ReadOnlyMemory<byte>`); `PairingRecord.PskId`.
- Produces: `private void MarkMatchedPskUsed()` and the `_markedPskUsed` field on `SendspinClientService`. Task 4 resets `_markedPskUsed` on re-handshake.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Sendspin.SDK.Tests/Client/SendspinClientServicePairingTests.cs`:

```csharp
    [Fact]
    public void Resolve_DoesNotMutateTheStore()
    {
        // Resolve runs inside ProcessInbound on the crypto receive path. It must be a
        // pure lookup: no state change, no disk write, and no marking a record used
        // before the AEAD has actually verified anything.
        var store = new InMemoryPairingRecordStore();
        var psk = new byte[32];
        psk[0] = 0x42;
        store.Upsert(new PairingRecord(psk, PskCategory.LongTerm, "srv-1"));

        var resolver = new RecordPskResolver(store);
        var resolved = resolver.Resolve(NoiseConstants.DerivePskId(psk));

        Assert.NotNull(resolved);
        Assert.False(Assert.Single(store.List()).Used, "Resolve must not mark the record used");
    }

    [Fact]
    public void MatchedPsk_IsMarkedUsed_OnceAnEncryptedMessageArrives()
    {
        // The record is marked used at the first proof the AEAD verified: an encrypted
        // application message we could actually decrypt.
        var store = new InMemoryPairingRecordStore();
        var psk = NoiseConstants.SentinelPsk.ToArray();
        store.Upsert(new PairingRecord(psk, PskCategory.LongTerm, null));

        var (client, connection, _) = TestClient.Create(
            PskCategory.LongTerm,
            configure: options => options.PairingRecordStore = store);
        using var _c = client;
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();

        Assert.False(Assert.Single(store.List()).Used, "not used before any message arrives");

        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");

        Assert.True(Assert.Single(store.List()).Used, "used once a decrypted message arrives");
    }
```

> Verified at `TestClient.cs:77-80`: the fake session's `MatchedPsk` is `new NoisePsk(NoiseConstants.SentinelPsk.ToArray(), category)` — the sentinel *bytes* with whatever category is asked for. That is why the second test seeds the store with those exact bytes: `MarkMatchedPskUsed` looks the record up by `psk_id`, which is derived from the key material, so a different PSK would simply not be found and the test would fail for the wrong reason.

Add `using Sendspin.SDK.Connection.Noise;` if not already present.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo --filter "FullyQualifiedName~SendspinClientServicePairingTests"`

Expected: `Resolve_DoesNotMutateTheStore` FAILS (`Used` is true — `Resolve` sets it today). `MatchedPsk_IsMarkedUsed_OnceAnEncryptedMessageArrives` FAILS at the final assertion (nothing marks it, because the client never did).

- [ ] **Step 3: Make `Resolve` pure**

In `src/Sendspin.SDK/Connection/Noise/PairingRecordStore.cs`, `RecordPskResolver.Resolve` becomes:

```csharp
    /// <inheritdoc/>
    /// <remarks>
    /// A pure lookup. This runs inside the framing layer's inbound path, before the
    /// AEAD has verified anything, so it must not mutate the store — the record is
    /// marked used by the client once a decrypted message proves the session
    /// authenticated.
    /// </remarks>
    public NoisePsk? Resolve(string pskId)
    {
        foreach (var record in _store.List())
        {
            if (record.PskId == pskId)
            {
                return new NoisePsk(record.Psk, record.Category, record.ServerId);
            }
        }

        return SentinelPskResolver.Instance.Resolve(pskId);
    }
```

- [ ] **Step 4: Mark the record used from the client**

In `src/Sendspin.SDK/Client/SendSpinClient.cs`, add the field beside `_pairingStore`:

```csharp
    private bool _markedPskUsed;
```

Add the method:

```csharp
    /// <summary>
    /// Marks the session's matched PSK used, once. Called on the first decrypted
    /// application message, which is the first proof the AEAD verified — the record
    /// must not be marked on a merely attempted connection.
    /// </summary>
    private void MarkMatchedPskUsed()
    {
        if (_markedPskUsed || _pairingStore is null)
            return;
        if (_session.MatchedPsk is not { } matched)
            return;

        string pskId = NoiseConstants.DerivePskId(matched.Key.Span);
        foreach (var record in _pairingStore.List())
        {
            if (record.PskId == pskId && !record.Used)
            {
                _pairingStore.Upsert(record with { Used = true });
                break;
            }
        }

        _markedPskUsed = true;
    }
```

Call it at the top of `OnTextMessageReceived` (~`:629`), before the message is dispatched:

```csharp
    private void OnTextMessageReceived(object? sender, string json)
    {
        // Reaching here means the framing layer decrypted and authenticated a frame.
        MarkMatchedPskUsed();

        // ...existing body unchanged
```

- [ ] **Step 5: Update the `Used` doc comment**

On `PairingRecord.Used` (~`PairingRecordStore.cs:17`), replace the summary with one that states both what it means and what it does not:

```csharp
    /// <summary>
    /// True once a server has authenticated a session with this record. Reported to
    /// servers in management/list-records; nothing in the SDK gates on it. Per spec
    /// #122 the Pairing PSK is NOT consumed by a successful pairing — it persists and
    /// may pair this client with any number of servers — so a used Pairing PSK record
    /// is deliberately retained, not retired.
    /// </summary>
```

- [ ] **Step 6: Run the tests, then the full suite**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo --filter "FullyQualifiedName~SendspinClientServicePairingTests"`
Then: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo`

Expected: 0 failing, total up by 2 from Task 2's figure.

- [ ] **Step 7: Commit**

```bash
git add src/Sendspin.SDK/Connection/Noise/PairingRecordStore.cs src/Sendspin.SDK/Client/SendSpinClient.cs tests/Sendspin.SDK.Tests/Client/SendspinClientServicePairingTests.cs
git commit -m "fix(pairing): make PSK resolution a pure lookup

Resolve marked records used before the AEAD had verified anything, and wrote to
disk from inside the framing layer's inbound path. The record is now marked once
a decrypted message proves the session authenticated.

Part of #87."
```

---

### Task 4: Reset the pairing counter on re-handshake (#87-4)

**Files:**
- Modify: `src/Sendspin.SDK/Client/SendSpinClient.cs` — field block (~`:36`), `HandlePairingActivate`
- Test: `tests/Sendspin.SDK.Tests/Client/SendspinClientServicePinPairingTests.cs`

**Interfaces:**
- Consumes: `INoiseSessionInfo.HandshakeHash` (`ReadOnlyMemory<byte>?`); `_markedPskUsed` from Task 3.
- Produces: nothing later tasks depend on.

- [ ] **Step 1: Write the failing test**

Append to `tests/Sendspin.SDK.Tests/Client/SendspinClientServicePinPairingTests.cs`. The test below is self-contained — it builds its own client via `TestClient.Create` rather than the file's existing fixture, because it needs to mutate the session's handshake hash mid-test.

```csharp
    [Fact]
    public void PairingCounter_RestartsAfterReHandshake()
    {
        // PinPairing.BuildSid defines the counter as pairing activates *since the last
        // Noise handshake*. A client-lifetime counter diverges from a conformant
        // server's after any key rotation, and CPace then fails.
        var (client, connection, session) = TestClient.Create(
            PskCategory.Sentinel,
            configure: options => options.Capabilities = new ClientCapabilities
            {
                PinPairingMethods = ["dynamic_pin"],
            });
        using var _c = client;
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");

        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"selected_pair_method":"dynamic_pin"}}""");
        var first = connection.SentMessages.OfType<ClientPairInitMessage>().Last();
        Assert.Equal(1, first.Payload.PairingIndex);

        // Simulate an in-band re-handshake: the framing layer installs new transport
        // keys and a new handshake hash.
        session.HandshakeHash = new byte[32] { 9, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                                               0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"selected_pair_method":"dynamic_pin"}}""");
        var second = connection.SentMessages.OfType<ClientPairInitMessage>().Last();

        Assert.Equal(1, second.Payload.PairingIndex);
    }
```

> Verified: `FakeNoiseSession.HandshakeHash` is `public ReadOnlyMemory<byte>? { get; set; }` with a 32-byte default (`TestClient.cs`), so a test can simulate a re-handshake without touching production code. `ClientPairInitPayload.PairingIndex` is `public int` (`PairingMessages.cs:113`). `TestClient.Create` returns `(Client, Connection, Session)` where `Session` is the `FakeNoiseSession` — that third element is what this test mutates.
>
> Note the client must be **Sentinel**-keyed here: the PIN methods legitimately run on a Sentinel session, and `IsAdmissible` admits `Sentinel + {pairing}`, so this activate is admissible. Do not change it to `LongTerm` — `LongTerm + {pairing}` is also admissible, but Sentinel is the realistic PIN-pairing shape.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo --filter "FullyQualifiedName~PairingCounter_RestartsAfterReHandshake"`

Expected: FAIL — the second assertion sees `2`, because the counter is client-lifetime.

- [ ] **Step 3: Track the handshake hash and reset**

Add the field beside `_pairingCounter`:

```csharp
    private byte[]? _lastHandshakeHash;
```

At the top of `HandlePairingActivate`, before `_pairingCounter++`:

```csharp
        // The spec defines the CPace counter as pairing activates since the last Noise
        // handshake. Re-handshakes happen inside the framing layer, but they install a
        // fresh handshake hash — so a change in it is our signal that the session was
        // re-keyed and the counter (and the used-marking) must restart.
        var currentHash = _session.HandshakeHash?.ToArray();
        if (currentHash is not null
            && (_lastHandshakeHash is null || !currentHash.AsSpan().SequenceEqual(_lastHandshakeHash)))
        {
            _lastHandshakeHash = currentHash;
            _pairingCounter = 0;
            _markedPskUsed = false;
        }

        _pinState = null;
        _pairingCounter++;
```

Resetting `_markedPskUsed` here is deliberate and comes from the design: a re-handshake can install a different PSK, and the newly matched record should be marked in its turn.

- [ ] **Step 4: Run the test, then the full suite**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo --filter "FullyQualifiedName~SendspinClientServicePinPairingTests"`
Then: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo`

Expected: 0 failing, total up by 1 from Task 3's figure.

- [ ] **Step 5: Commit**

```bash
git add src/Sendspin.SDK/Client/SendSpinClient.cs tests/Sendspin.SDK.Tests/Client/SendspinClientServicePinPairingTests.cs
git commit -m "fix(pairing): restart the CPace pairing counter after a re-handshake

The spec defines the counter as pairing activates since the last Noise handshake,
but it was client-lifetime, so the CPace sid diverged from a conformant server's
after any key rotation. A change in the session's handshake hash is the signal.

Closes #87."
```

---

## Verification Checklist

Run before opening the PR. Each maps to a success criterion in the design.

- [ ] A Sentinel-keyed `server/activate` selecting `pairing_psk` produces `pair/abort { method_not_supported }`, no `client/pair-finalize`, and the connection stays open.
- [ ] The same with no record store: additionally no `PairingCompleted` event.
- [ ] `server/command source start` at trust `none`, or with the source role inactive, leaves `FakeCaptureDevice.Capturing == false`.
- [ ] The same at trust `user` with an active source role sets it `true`.
- [ ] `git grep -n "Used = true" src/Sendspin.SDK/Connection/Noise/PairingRecordStore.cs` returns nothing.
- [ ] `git grep -n "DisconnectAsync" src/Sendspin.SDK/Client/SendSpinClient.cs` shows no call reachable from `HandlePairingActivate`.
- [ ] `dotnet test ... -f net10.0` passes with 0 failures.
- [ ] `dotnet build` clean for both `net8.0` and `net10.0`.
- [ ] `src/Sendspin.SDK/Sendspin.SDK.csproj` still says `<Version>9.1.0</Version>`.
- [ ] No new members on `IPairingRecordStore`, `INoisePskResolver`, or `IWireFraming`.
