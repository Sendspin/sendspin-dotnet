# `client/state` available Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `client/state` report the spec's boolean `available` instead of the pre-#115 `state` string, so a current server can interpret the client's operational status and will send it audio.

**Architecture:** Three sequential changes to one message type and its five call sites. Task 1 fixes the wire shape and is independently shippable — it alone closes #77's conformance gap. Task 2 replaces three ad-hoc availability assertions with one computed value and a publish-on-change helper. Task 3 defers the initial message until clock sync, per the spec rule that a player claims `available: true` only once synchronised.

**Tech Stack:** .NET (`net8.0;net10.0`), xUnit, source-generated `System.Text.Json`.

## Global Constraints

- Target frameworks `net8.0;net10.0`. Nullable enabled; `CS8600;CS8601;CS8602;CS8603;CS8604;CS8618;CS8625` are **errors**.
- Package version stays **`9.1.0`**. #91 owns the bump. Do not touch `<Version>`.
- Commit messages: no AI attribution, no `Co-Authored-By`, no self-reference. Write as the repo owner.
- Full suite: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo`
- The **test project targets `net10.0` only**. `dotnet test ... -f net8.0` fails with NETSDK1005 — expected, not a regression. For `net8.0`: `dotnet build src/Sendspin.SDK/Sendspin.SDK.csproj -f net8.0 --nologo`.
- **Run `dotnet test` in the foreground.** If a run stalls, kill orphaned `dotnet.exe` / `vstest.console.dll` — this has bitten the repo repeatedly.
- **Clean rebuild (delete `obj` and `bin`) before any claim about compiler warnings.** A report on a sibling branch had to retract a false "no warning emitted" claim made from an incremental build.
- **Baseline entering Task 1: 402 passing, 0 failing** (verified on this branch's base). Every task reports the absolute count it observes and accounts for its delta.
- Standing test bar: any test asserting something did *not* happen must survive **"would this still pass if the machinery producing it were deleted?"**

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `src/Sendspin.SDK/Protocol/Messages/ClientStateMessage.cs` | The payload field and the three factories that replace the old three | 1 |
| `src/Sendspin.SDK/Client/SendSpinClient.cs` | Five call sites; then the computed availability and its publisher; then the gate | 1, 2, 3 |
| `tests/Sendspin.SDK.Tests/Protocol/ClientStateAvailableTests.cs` (create) | Wire-shape tests | 1 |
| `tests/Sendspin.SDK.Tests/Client/ClientAvailabilityTests.cs` (create) | Availability transitions and suppression | 2 |
| `tests/Sendspin.SDK.Tests/Client/InitialClientStateGatingTests.cs` (create) | The clock-sync gate and the per-role rule | 3 |

---

### Task 1: Report `available` on the wire (#77's conformance gap)

**Files:**
- Modify: `src/Sendspin.SDK/Protocol/Messages/ClientStateMessage.cs`
- Modify: `src/Sendspin.SDK/Client/SendSpinClient.cs` — five call sites: `~:558` (`SendPlayerStateAsync`), `~:592` / `~:600` (`EnterExternalSourceAsync` / `ExitExternalSourceAsync`), `~:1846` (`SendInitialClientStateAsync`), `~:2761` (`ReportClientErrorAsync`)
- Test: `tests/Sendspin.SDK.Tests/Protocol/ClientStateAvailableTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `ClientStatePayload.Available` (`bool?`), and three factories — `CreateInitial`, `CreateAvailability`, `CreatePlayerState`. Tasks 2 and 3 call `CreateAvailability` and `CreateInitial`.

**The spec text**, `messaging.md` `client/state`:

> - `available`: boolean - whether the client is available to participate in Sendspin playback
>   - `true` - client is operational and ready to participate in playback; for a player or source this means its clock is synchronized with the server.
>   - `false` - client's output is in use by an external system and is not currently participating in Sendspin playback with this server.
>
> The initial message MUST include all state fields. In subsequent messages, the client MAY send only the fields that have changed; the server MUST merge each update into existing state, retaining the last value of any field that is absent.

- [ ] **Step 1: Write the failing tests**

Create `tests/Sendspin.SDK.Tests/Protocol/ClientStateAvailableTests.cs`. Serialize each message and assert on the JSON, not on the object graph — the defect is a wire-shape defect and only the JSON proves it.

1. **Initial message carries `available: true` and no `state` key.** Assert **both**: `"available":true` is present, and the serialized JSON does **not** contain `"state"`. Asserting only the first would pass an implementation that emits both fields.
2. **Initial message includes the player object** with volume, muted, `required_lead_time_ms`, `min_buffer_ms`.
3. **An availability-only delta** serializes to `available` with **no** `player` key.
4. **A player-state delta carries `player` and no `available` key** — this is the §4 defect. Mutation-check it: set `Available = true` in the player-state factory and confirm this test fails.
5. **`available: false`** round-trips as `false`, not omitted — `JsonIgnoreCondition.WhenWritingNull` must not swallow a deliberate `false`. A `bool` (non-nullable) with `WhenWritingDefault` would; this test catches that mistake.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo --filter "FullyQualifiedName~ClientStateAvailableTests"`

Expected: fail to compile (the members do not exist). Record it.

- [ ] **Step 3: Reshape the payload**

In `ClientStatePayload`, replace:

```csharp
    [JsonPropertyName("state")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? State { get; init; }
```

with:

```csharp
    /// <summary>
    /// Whether this client is available to participate in Sendspin playback. For a player or
    /// source, <c>true</c> additionally means its clock is synchronized with the server.
    /// Null omits the field, for a delta that changes only the role objects.
    /// </summary>
    [JsonPropertyName("available")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Available { get; init; }
```

`bool?` is required, not stylistic: the spec needs both "initial MUST include all fields" and "a delta MAY send only what changed", and a non-nullable `bool` would serialize `false` into every player delta — which Step 5 exists to prevent.

Update the class-level doc comment, which currently says "Used to report client state (synchronized, error, external_source)".

- [ ] **Step 4: Replace the three factories**

Delete `CreateSynchronized`, `CreateState` and `CreateError`. Add three whose names say what they build:

```csharp
    /// <summary>
    /// Builds the initial client/state message, which per spec MUST carry every state field.
    /// </summary>
    public static ClientStateMessage CreateInitial(
        bool available,
        int volume = 100,
        bool muted = false,
        double staticDelayMs = 0.0,
        int requiredLeadTimeMs = 0,
        int minBufferMs = 0,
        List<string>? supportedCommands = null)

    /// <summary>
    /// Builds a delta that reports only a change in availability, with no role objects.
    /// </summary>
    public static ClientStateMessage CreateAvailability(bool available)

    /// <summary>
    /// Builds a delta carrying only the player object. It deliberately omits
    /// <c>available</c>: a volume or mute change says nothing about whether the client is
    /// available, and asserting it here would overwrite the server's view.
    /// </summary>
    public static ClientStateMessage CreatePlayerState(
        int volume,
        bool muted,
        double staticDelayMs,
        int requiredLeadTimeMs,
        int minBufferMs,
        List<string>? supportedCommands = null)
```

These are public API changes. The branch is pre-release and #91 owns the version bump, so that is consistent — but say so in your report rather than leaving it implicit.

- [ ] **Step 5: Update the five call sites, preserving today's behaviour**

Behaviour changes belong to Tasks 2 and 3. Here, map each site to the equivalent new shape:

| Site | Was | Becomes |
|---|---|---|
| `SendInitialClientStateAsync` (~`:1846`) | `CreateSynchronized(...)` | `CreateInitial(available: true, ...)` |
| `SendPlayerStateAsync` (~`:558`) | `CreateSynchronized(...)` | **`CreatePlayerState(...)`** — the §4 fix; it must stop asserting availability |
| `EnterExternalSourceAsync` (~`:592`) | `CreateState("external_source")` | `CreateAvailability(false)` |
| `ExitExternalSourceAsync` (~`:600`) | `CreateState("synchronized")` | `CreateAvailability(true)` |
| `ReportClientErrorAsync` (~`:2761`) | `CreateError(message)` | `CreateAvailability(false)` |

The `message` parameter of `ReportClientErrorAsync` was already never sent on the wire (the old `CreateError` discarded it). Keep the parameter for the caller's logging, and keep the log line — but make sure the doc comment no longer claims a wire field exists for it.

Also update `OnPipelineStateChanged`'s recovery branch: its comment says it reports `client/state: 'synchronized'`, which is no longer a thing. It currently calls `SendPlayerStateAckAsync()`; check what that sends. **If recovery now needs to report `available: true`, that is Task 2's publisher — for this task just make it compile and leave a note.** Do not invent a second availability path here.

Line numbers will have drifted; find the sites by name.

- [ ] **Step 6: Fix the test fallout**

Existing tests almost certainly assert `"state":"synchronized"` or construct `CreateSynchronized`. Fix each by moving it to the new shape — **never** by reintroducing the `state` field. Grep `tests/` for `"synchronized"`, `"external_source"`, `CreateSynchronized`, `CreateState`, `CreateError`, and `\"state\"`.

If a test asserted the *old* wire string as its whole point, it should now assert the new one; say in your report which tests you converted and that none was weakened.

- [ ] **Step 7: Run the tests, then the full suite, then commit**

```bash
git add src/Sendspin.SDK/Protocol/Messages/ClientStateMessage.cs src/Sendspin.SDK/Client/SendSpinClient.cs tests/Sendspin.SDK.Tests/Protocol/ClientStateAvailableTests.cs
git commit -m "fix(protocol)!: report client/state availability as the spec's boolean

client/state still sent a top-level state string of synchronized, error or
external_source. Spec #115 replaced that with a boolean available on 2026-07-07,
before the encryption work began, and the reshape never landed — so no server
released since then could interpret this client's operational status. Because the
spec forbids a server sending binary data before the initial client/state, the
symptom was a connection that activated cleanly and then never played audio.

A player-state delta now omits available entirely. It previously hard-coded
synchronized, so changing the volume while in external-source mode silently told
the server the client was available again.

Closes #77."
```

---

### Task 2: Availability becomes one computed value with a publish-on-change helper

**Files:**
- Modify: `src/Sendspin.SDK/Client/SendSpinClient.cs`
- Test: `tests/Sendspin.SDK.Tests/Client/ClientAvailabilityTests.cs`

**Interfaces:**
- Consumes: `ClientStateMessage.CreateAvailability` (Task 1).
- Produces: a private computed availability and a publisher. Task 3 reuses both.

**Why.** Task 1 leaves three sites each asserting availability independently — which is how the `SendPlayerStateAsync` defect arose in the first place. Replace them with one source of truth:

```
available = (!RequiresClockSync || IsClockSynced) && !IsExternalSource && !pipelineErrored
```

`RequiresClockSync` is true when the client advertises `player@v1` or `source@v1` — the two roles the spec names. There is an existing helper for the source-role check (`HasSourceRole()`); look for it and follow its shape rather than inventing a second idiom.

The three inputs all already exist: `IsClockSynced`, `IsExternalSource`, and the `_clientErrorReported` latch.

- [ ] **Step 1: Write the failing tests**

Create `tests/Sendspin.SDK.Tests/Client/ClientAvailabilityTests.cs`:

1. `EnterExternalSourceAsync` sends `available: false`; `ExitExternalSourceAsync` then sends `available: true`.
2. A pipeline error sends `available: false`; recovery to `Playing` sends `available: true`.
3. **Repeated identical values are suppressed** — drive two pipeline-error notifications and assert exactly **one** `available: false` reached the wire. Nothing else would catch this, and `OnPipelineStateChanged` genuinely fires repeatedly.
4. **The inputs compose** — with a pipeline error outstanding, `ExitExternalSourceAsync` must **not** publish `available: true`, because the error still makes the client unavailable. This is the test that proves it is a computed value rather than three independent setters; without it, three separate booleans would pass tests 1 and 2.
5. A volume change while in external-source mode sends a delta with **no** `available` key (regression guard on Task 1's §4 fix, now at the client level).

- [ ] **Step 2: Run to verify they fail**

Run with `--filter "FullyQualifiedName~ClientAvailabilityTests"`. Expected: tests 3 and 4 fail (nothing suppresses or composes yet); 1, 2 and 5 may already pass from Task 1. Record which, and say so — a test that was already green is not evidence for this task.

- [ ] **Step 3: Add the computed value and the publisher**

A private read-only property computing the expression above, and a private `async Task PublishAvailabilityAsync()` that compares it against the last value sent, returns without sending when unchanged, and otherwise sends `CreateAvailability(current)` and records it.

Seed the "last sent" tracker from the initial message so the first delta after it is not a spurious repeat.

Guard on `_connection.State == ConnectionState.Connected`, matching the existing `ReportClientErrorAsync` guard — a publish that lands while reconnecting would hit a closed socket.

- [ ] **Step 4: Route the three sites through it**

`EnterExternalSourceAsync`, `ExitExternalSourceAsync`, `OnPipelineError`, and `OnPipelineStateChanged`'s Error and Playing branches all set their input and then call the publisher. Remove their direct `SendMessageAsync(CreateAvailability(...))` calls so there is exactly one place that sends an availability delta.

Keep `IsExternalSource`'s existing "notify first, flip local state only on success" ordering — it is deliberate, and the publisher must not quietly change it.

- [ ] **Step 5: Run the tests, then the full suite, then commit**

```bash
git add src/Sendspin.SDK/Client/SendSpinClient.cs tests/Sendspin.SDK.Tests/Client/ClientAvailabilityTests.cs
git commit -m "refactor(client): compute availability from its three inputs

Three call sites each asserted availability independently, which is how the
player-state delta came to hard-code it. Availability is now derived from clock
sync, external-source state and the pipeline-error latch, and published only when
the derived value changes.

Composition is the point: with a pipeline error outstanding, leaving external
source no longer reports the client as available."
```

---

### Task 3: Defer the initial `client/state` until clock sync

**Files:**
- Modify: `src/Sendspin.SDK/Client/SendSpinClient.cs`
- Test: `tests/Sendspin.SDK.Tests/Client/InitialClientStateGatingTests.cs`

**Interfaces:**
- Consumes: Task 2's computed availability; `ClientStateMessage.CreateInitial`; the existing `ClockSyncConverged` event and `IsClockSynced`.
- Produces: nothing new.

**The spec rule.** A player reports `available: true` **only after** it has established clock synchronization. Today `SendInitialClientStateAsync` is fired immediately on activate — with `StartTimeSyncLoop()` on the *next line* — so the claim precedes the sync.

**Why the initial message is deferred rather than sent as `available: false` first.** The spec's server behaviour for a client reporting `available: false` is to remember its group, move it to a **solo group**, send `stream/end`, and on return to `true` **not** auto-rejoin. A reconnecting client may still be held in its group, so opening with `available: false` would silently and permanently drop a speaker out of its group on every reconnect. Deferring never emits `false` unless something is genuinely wrong.

**The per-role split.** A client with **no** `player`/`source` role — artwork- or visualizer-only — does not need clock sync, and the spec says `available` alone unlocks the server's streams for it. Those send immediately on activate.

- [ ] **Step 1: Write the failing tests**

Create `tests/Sendspin.SDK.Tests/Client/InitialClientStateGatingTests.cs`:

1. **A player client with sync not yet converged has sent no `client/state` at all.** Assert on the messages the fake connection received — zero `client/state`.
2. **Positive control, and it is not optional**: once sync converges, exactly **one** `client/state` is sent, carrying `available: true`. Without this, an implementation that never sends the initial state passes test 1.
3. **An artwork-only client** (roles without `player`/`source`) sends its initial `client/state` immediately on activate, without waiting for sync.
4. **Reconnect while converging emits no `available: false`.** Drive activate → disconnect → reconnect with sync unconverged, and assert no message anywhere carries `"available":false`. This is the test that protects the design decision, and it is the one most likely to be skipped.

- [ ] **Step 2: Run to verify they fail**

Run with `--filter "FullyQualifiedName~InitialClientStateGatingTests"`. Expected: test 1 fails (the state is sent immediately today); 3 may already pass; 2 and 4 may need the gate to exist before they mean anything. Record precisely which failed and why.

- [ ] **Step 3: Gate the initial send**

In the activate handler, replace the unconditional `SendInitialClientStateAsync().SafeFireAndForget(_logger)` with:

- if the client does not require clock sync (no `player`/`source` role) → send immediately, as today;
- otherwise → defer, and send on the first clock-sync convergence.

Subscribe to the existing convergence signal rather than polling. Send **once** per connection: a second convergence (after a resync) must not re-send the initial message. Reset that latch where the other per-connection state is reset in the activate handler, or a reconnect will never send its initial state.

`StartTimeSyncLoop()` must still start — it is what produces the convergence you are waiting for. Check the ordering.

- [ ] **Step 4: Make non-convergence diagnosable without a new timer**

Log once at Information when the initial state is **deferred** pending sync, and once when it is **sent**. A wedged client then shows the first with no second, which is diagnosable from logs alone. Deliberately do **not** add a timeout that falls back to `available: false` — that would reintroduce the reconnect hazard this design rejected.

- [ ] **Step 5: Fix the test fallout**

Many existing tests activate a client and then assert on `client/state`, or on messages that follow it. Those will now need clock sync established first. **Fix them by having the fixture converge sync — never by removing the gate.** If a test cannot establish sync, that is a test-infrastructure gap worth reporting, not a reason to weaken the change.

Report every test you touched and what you did to it.

- [ ] **Step 6: Run the tests, then the full suite, then commit**

```bash
git add src/Sendspin.SDK/Client/SendSpinClient.cs tests/Sendspin.SDK.Tests/Client/InitialClientStateGatingTests.cs
git commit -m "fix(client): report available only once the clock is synchronized

The spec lets a player report available: true only after it has established clock
synchronization, but the initial client/state was sent immediately on activate —
with the time-sync loop starting on the next line. Players now defer that message
until the first convergence; artwork- and visualizer-only clients, which need no
sync, still send it at once.

The initial message is deferred rather than opened with available: false because
the server remembers a client's group when it reports unavailable, moves it to a
solo group, and must not auto-rejoin it — so opening with false would drop a
reconnecting speaker out of its group permanently.

Part of #77."
```

---

## Verification Checklist

- [ ] `git grep -n 'JsonPropertyName("state")' src/` returns nothing for `client/state`.
- [ ] `git grep -n 'CreateSynchronized\|CreateState(\|CreateError(' src/ tests/` returns nothing.
- [ ] `git grep -n '"synchronized"\|"external_source"' src/` returns nothing.
- [ ] A serialized initial `client/state` contains `"available":true` and no `"state"`.
- [ ] A player-state delta contains `player` and no `available`.
- [ ] A player client sends no `client/state` before sync converges, and exactly one after.
- [ ] An artwork-only client sends its initial `client/state` immediately.
- [ ] Two identical availability transitions produce exactly one wire message.
- [ ] No path emits `"available":false` on a reconnect that is merely still converging.
- [ ] `dotnet test ... -f net10.0` passes with 0 failures.
- [ ] `dotnet build src/Sendspin.SDK/Sendspin.SDK.csproj` clean for both `net8.0` and `net10.0`.
- [ ] `<Version>9.1.0</Version>` unchanged.
