# Multi-Server Arbitration: Verify, Test, Harden & Formalize Persistence

**Date:** 2026-06-04
**Issue:** [#26 — Implement last-played-server persistence, multi-server arbitration, and auto `another_server` goodbye on switch](https://github.com/Sendspin/sendspin-dotnet/issues/26)
**Status:** Approved design — ready for implementation planning

## Background

Issue #26 was filed from a cross-SDK conformance audit and asserts that `sendspin-dotnet`
has *no* last-played concept, *no* handshake-first arbitration, and does *not* send
`client/goodbye: another_server` on switch.

**That premise is stale.** `SendspinHostService.ArbitrateConnectionAsync` has implemented all
three since the repository's first commit (`2d1b499`), months before the issue was filed
(2026-05-27). The audit almost certainly inspected the *client-initiated* path
(`SendspinClientService` / `SendspinConnection`) and never reached the host service, where
arbitration actually lives. The spec section the audit cites is explicitly *server-initiated*,
which is exactly what `SendspinHostService` implements.

The real gaps are narrower than "implement multi-server":

1. **Zero test coverage.** The arbitration logic is entirely untested. (The connection-state
   test-double work in PR #51 was sequenced as its prerequisite, since arbitration is all
   connection-state transitions.)
2. **Unverified conformance.** Because it is untested, correctness is unproven, and several
   edge cases are under-specified.
3. **Informal persistence.** Last-played is an ad-hoc `LastPlayedServerId` property + event +
   constructor seed, not a store interface like the existing `IStaticDelayStore`.

## Goals

- Make the arbitration decision provably conformant to the spec via exhaustive tests.
- Formalize last-played persistence as an `ILastPlayedServerStore` seam (additive, non-breaking).
- Harden the three spec-silent edge cases with explicit, documented rulings.
- Cover the wire behavior (the `client/goodbye` reason actually sent) end-to-end.
- Close #26 with a record of the stale-audit finding.

## Non-Goals

- No client-initiated (`SendspinClientService`) arbitration — multi-server contention is a
  server-initiated (host) concern by spec.
- No reconnect-backoff coordination or other broader conformance-doc items.
- No breaking API changes; this stays in the 9.x line.
- Correcting the audit doc in the `Sendspin/conformance` repo (cross-repo follow-up only).

## Spec rules (reference)

From the conformance doc's summary of the SendSpin spec:

- Clients **must** persist the `server_id` of the server most recently `playback_state: playing`.
- On a second connection, complete the `client/hello` ↔ `server/hello` handshake **first**, then:
  - new `connection_reason: playback` → switch
  - new `discovery` while existing was `playback` → keep existing
  - both `discovery` → prefer last-played, else keep existing
- On switch, send `client/goodbye` reason `another_server` to the server being left.
- Goodbye reasons: `another_server` / `shutdown` / `user_request` → server does **not**
  auto-reconnect; `restart` (or no goodbye) → server **does** auto-reconnect.

## Section 1 — Extract the arbitration decision

New internal, side-effect-free unit at `src/Sendspin.SDK/Client/ServerArbitration.cs`. Kept
`internal` (the test assembly already has `InternalsVisibleTo`, matching the existing
`SendTimeSyncBurstAsync` test-seam convention), so the public API surface does not grow.

```csharp
internal static class ServerArbitration
{
    internal readonly record struct ArbitrationResult(
        bool AcceptNew, string? LoserGoodbyeReason, string Rationale);

    // existingServerId == null  ⇒ no active connection
    internal static ArbitrationResult Decide(
        string newServerId,  string? newReason,
        string? existingServerId, string? existingReason,
        string? lastPlayedServerId);
}
```

`ArbitrateConnectionAsync` keeps the I/O but collapses to: call `Decide(...)`, then

- if `AcceptNew` → disconnect the existing connection (if any) with `LoserGoodbyeReason`, accept new;
- else → disconnect the new connection with `LoserGoodbyeReason`, keep existing.

`Rationale` feeds the existing arbitration log line. Reason matching normalizes `null` → `discovery`
and compares case-insensitively (preserving current behavior).

### Decision table

| Case | Result |
| --- | --- |
| No existing connection | Accept, loser = — |
| Same `server_id` reconnecting | Accept, loser = `user_request` |
| new `playback`, existing not `playback` | Accept, loser = `another_server` (switch) |
| existing `playback`, new not `playback` | Reject, loser = `another_server` (to new) |
| tie (same reason class), last-played == new | Accept, loser = `another_server` |
| tie (same reason class), otherwise | Reject, loser = `another_server` |

### Edge-case rulings (spec-silent)

- **E1 — both servers `playback`:** treated as a tie (last-played decides, else keep existing),
  consistent with the both-`discovery` rule. *(= current behavior)*
- **E2 — rejected new server's goodbye reason:** `another_server`, so a losing newcomer does not
  auto-reconnect into a storm. *(= current behavior)*
- **E3 — same-server reconnect:** the stale socket is disconnected with **`user_request`** (a
  spec-valid "do not auto-reconnect" reason). This replaces the previous non-spec
  `"reconnecting"` token. `user_request` is chosen over the other no-reconnect reasons because
  the peer is the *same* server (not `another_server`) and the client is alive (not `shutdown`).
  A code comment will record this rationale.

## Section 2 — Formalize last-played persistence (additive)

New seam mirroring `IStaticDelayStore`, at `src/Sendspin.SDK/Client/ILastPlayedServerStore.cs`:

```csharp
public interface ILastPlayedServerStore
{
    string? Load();              // last-played server_id, or null if none stored
    void Save(string serverId);  // best-effort, non-throwing
}
```

Wiring into `SendspinHostService` — **all additive, nothing existing breaks**:

- New optional ctor param `ILastPlayedServerStore? lastPlayedServerStore = null`.
- **Load:** at construction, seed `LastPlayedServerId` from `store.Load()` **only if** the explicit
  `lastPlayedServerId` seed param was not supplied (the seed param wins, to avoid surprising
  current callers).
- **Save:** in `SetLastPlayedServerId`, call `store.Save(serverId)` wrapped best-effort
  (try/catch + log), mirroring `TrySaveStaticDelay`. The `LastPlayedServerIdChanged` event still
  fires exactly as before.
- Both `Load` and `Save` are best-effort: a throwing store logs and continues, never breaking
  arbitration.

## Section 3 — Test plan (three layers)

Layers are split by *what can lie*: pure logic is swept exhaustively where it is cheap; wire
behavior is proven on a few representative cases; the store seam is tested in isolation. No layer
re-tests what a cheaper one already proved.

### Layer 1 — Pure decision matrix

`tests/Sendspin.SDK.Tests/Client/ServerArbitrationTests.cs`, `[Theory]` / `[InlineData]` over
`Decide(...)`. Deterministic, no I/O. Cases:

| Inputs | Expect |
| --- | --- |
| no existing | Accept, loser — |
| same `server_id` reconnect | Accept, loser `user_request` |
| new `playback`, existing `discovery` | Accept, loser `another_server` |
| existing `playback`, new `discovery` | Reject, loser `another_server` |
| both `discovery`, last-played = new | Accept, loser `another_server` |
| both `discovery`, last-played = existing | Reject |
| both `discovery`, last-played = null | Reject (existing wins) |
| both `playback` (E1), last-played = new | Accept |
| both `playback` (E1), last-played ≠ new | Reject |
| `null` reason normalizes to `discovery` | matches discovery rows |
| reason case-insensitive (`"Playback"`, `"PLAYBACK"`) | matches |

### Layer 2 — Persistence unit tests

Extend the host-service tests with a `FakeLastPlayedServerStore`:

- `Save` is called with the `server_id` that transitioned to `playing`.
- `Load` seeds `LastPlayedServerId` at construction (when no seed param).
- The explicit `lastPlayedServerId` seed param wins over the store.
- A throwing store is swallowed (best-effort) and does not break arbitration.

Mirrors the existing `IStaticDelayStore` test style (`FakeStaticDelayStore`).

### Layer 3 — Loopback end-to-end

`tests/Sendspin.SDK.Tests/Client/SendspinHostServiceArbitrationTests.cs`, with a minimal
`FakeServer` helper: a `ClientWebSocket` to `ws://127.0.0.1:<port>/sendspin` that receives
`client/hello`, replies `server/hello` with a chosen `server_id` / `connection_reason`, and
captures the `client/goodbye` reason (or socket close).

Scenarios assert the **reason on the wire**:

1. Single `discovery` server → accepted, no goodbye, stays connected.
2. Existing `discovery`, new `playback` → existing receives `another_server`; new stays.
3. Existing `playback`, new `discovery` → new receives `another_server`; existing stays.
4. Both `discovery`, last-played = new (seeded) → existing receives `another_server`; new stays.
5. Same-server reconnect → stale receives `user_request` (verifies E3 end-to-end).

Synchronization is via awaited protocol events with timeouts — never `Task.Delay`/sleeps.

**Enabling detail:** loopback tests bind port `0` (OS-assigned) to avoid fixed-port collisions
under parallel test runs, so they need the *actual* bound port. `SimpleWebSocketServer.Port`
already reports it (read from `LocalEndpoint` after `Start`, explicitly handling port `0`), but
`SendspinListener` and `SendspinHostService` currently surface only the *configured* port. The
plan adds a small additive bound-port accessor that threads `SimpleWebSocketServer.Port` up
through `SendspinListener` and `SendspinHostService`.

## Section 4 — Closeout

- **Issue #26:** post a comment recording that arbitration / last-played / `another_server` were
  already implemented in `SendspinHostService` (predating the issue), that this work adds the
  `ILastPlayedServerStore` seam, the `user_request` normalization (E3), and full test coverage,
  and that the conformance audit's dotnet row is stale. The resulting PR should close #26.
- **README:** document `ILastPlayedServerStore` and the multi-server arbitration behavior for
  embedders (per the SendSpin "document user-facing features" standard).
- **Versioning:** all-additive → stays in 9.x, no breaking bump.
- **Cross-repo:** correcting the audit doc in `Sendspin/conformance` is a separate follow-up.

## Files touched

| File | Change |
| --- | --- |
| `src/Sendspin.SDK/Client/ServerArbitration.cs` | **new** — pure decision + `ArbitrationResult` |
| `src/Sendspin.SDK/Client/ILastPlayedServerStore.cs` | **new** — persistence seam |
| `src/Sendspin.SDK/Client/SendSpinHostService.cs` | call `Decide`; wire the store (additive ctor param, load/save) |
| `src/Sendspin.SDK/Connection/SendSpinListener.cs` (+ host service) | additive bound-port accessor surfacing `SimpleWebSocketServer.Port` for tests |
| `tests/Sendspin.SDK.Tests/Client/ServerArbitrationTests.cs` | **new** — Layer 1 |
| `tests/Sendspin.SDK.Tests/Client/SendspinHostServiceArbitrationTests.cs` | **new** — Layers 2 & 3 + `FakeServer` / `FakeLastPlayedServerStore` |
| `README.md` | document the store + arbitration behavior |

## Risks & mitigations

- **Loopback flakiness:** mitigated by awaiting explicit protocol events (handshake completion,
  goodbye frame) with bounded timeouts rather than sleeping; bind an OS-assigned port to avoid
  collisions.
- **Behavior change from E3 (`reconnecting` → `user_request`):** the only intentional behavior
  change. It is covered by a Layer 1 case and a Layer 3 scenario, and documented in code.
- **Hidden coupling in `ArbitrateConnectionAsync`:** the extraction is mechanical (decision logic
  has no I/O dependencies), reducing regression risk; existing behavior is otherwise preserved.
