# Design: `client/state` reports a boolean `available`

*Closes [#77](https://github.com/Sendspin/sendspin-dotnet/issues/77). Branched from `main` rather than from [#111](https://github.com/Sendspin/sendspin-dotnet/pull/111) so it can merge first — it is what unblocks app testing, and `ClientStateMessage.cs` is untouched by that branch.*

## 0. Why this is more than a field rename

Spec [#115](https://github.com/Sendspin/spec/pull/115) (merged 2026-07-07) replaced `client/state`'s top-level `state` string with a boolean `available`. The SDK never followed, and `grep` finds no `available` anywhere in `src/`. `aiosendspin` 7.0.0 shipped 2026-07-15 — after #115 — so a current server expects the new shape.

**Why this is the release's highest-value fix.** `messaging.md`:

> The server MUST NOT send binary data to a client before that client has sent its initial `client/state`.

A `client/state` the server cannot interpret therefore presents as a connection that establishes cleanly, activates, and then never receives audio — the hardest failure shape to diagnose from an app. **The interop harness cannot catch it**: `tools/interop/server.py` validates nothing about `client/state` and emits `success` once the scenario completes, so a green interop run bounds the failure to layers the harness does not cover rather than eliminating it.

**Two owner decisions, taken with their costs stated:**

1. **`error` maps to `available: false`.** The spec deleted the `error` state with no replacement, and the normative sentence for `available` is broad — "whether the client is available to participate in Sendspin playback" — which a failed pipeline plainly is not. Cost accepted: the spec's server behaviour for `available: false` moves the client to a solo group, sends `stream/end`, and **does not auto-rejoin**, so a transient underrun costs group membership until an explicit `switch`.
2. **The clock-sync gate is fixed here too**, not deferred. It is a second conformance gap in the same message that #77 does not mention, and it governs *when audio starts flowing*, so fixing the shape without it would leave the more functionally significant half undone.

## 1. The wire change

`ClientStatePayload.State` (`string?`, `[JsonPropertyName("state")]`) becomes `Available` (`bool?`, `[JsonPropertyName("available")]`), keeping `JsonIgnoreCondition.WhenWritingNull` so a delta can omit it.

`bool?` rather than `bool` is load-bearing: the spec's delta rules require both "initial message MUST include all state fields" and "subsequent messages MAY send only the fields that have changed". A non-nullable `bool` would serialize `false` into every player-state delta, which under §4 is actively harmful.

## 2. Availability becomes computed state, not a string a caller passes

Today three call sites each assert an operational string independently, which is how the bug in §4 arose. Replace that with one source of truth:

```
available = (!RequiresClockSync || IsClockSynced) && !IsExternalSource && !pipelineErrored
```

`RequiresClockSync` is true when the client advertises `player@v1` or `source@v1` — the two roles the spec names ("for a player or source this means its clock is synchronized with the server").

The three inputs already exist and need no new plumbing:

| Input | Existing signal |
|---|---|
| clock synchronised | `IsClockSynced` / the `ClockSyncConverged` event |
| external source active | `IsExternalSource`, set by `EnterExternalSourceAsync` / `ExitExternalSourceAsync` |
| pipeline errored | the `_clientErrorReported` latch driven by `OnPipelineError` / `OnPipelineStateChanged` |

A private publisher sends `{"available": <value>}` as a delta **only when the computed value differs from the last value sent**. The existing ad-hoc sends at the external-source and pipeline-error sites collapse into calls to it. Suppressing no-change sends matters: `OnPipelineStateChanged` fires repeatedly, and re-sending `available: false` on each would be noise the server must merge.

## 3. Initial-state timing, and the reconnect hazard that decides it

Two shapes were considered.

**(A) Defer the initial `client/state` until clock sync is established, then send it once with `available: true`.**

**(B) Send it immediately with `available: false`, then send `available: true` on sync.**

**(B) is rejected, and the reason is a reconnect hazard rather than taste.** The spec's server behaviour for a client reporting `available: false` is to remember its current group, move it to a **solo group**, and send `stream/end` — and on return to `available: true` the server **MUST NOT auto-rejoin it**. A reconnecting client may still be held in its group by the server, so an initial `available: false` on every reconnect would silently and permanently drop a speaker out of its group. Shape (A) never emits `false` unless something is genuinely wrong, so it cannot cause that.

**Chosen: (A).** Consequences:

- For a `player`/`source` client, the initial `client/state` is deferred until the first clock-sync convergence. Audio therefore starts a convergence-time later (~2 s per the existing reconnect comment) than today. That is correct rather than a regression: a player that has not synchronised cannot render correctly, and the spec forbids claiming `available: true` before it has.
- For a client with **no** `player`/`source` role — artwork- or visualizer-only — clock sync is not required and the spec says `available` alone unlocks the server's streams, so the initial message is sent immediately on activate.

**Non-convergence is made diagnosable without a new timer.** If sync never converges the initial state is never sent and no audio arrives. Rather than invent a timeout and a magic number, the SDK logs once at Information when it *defers* the initial state, and again when it sends it. A wedged client then shows "deferred" with no matching "sent", which is diagnosable straight from the logs. It deliberately does **not** fall back to sending `available: false`, for the reconnect reason above.

## 4. `SendPlayerStateAsync` must stop asserting availability — a pre-existing bug the rename would otherwise carry forward

`ClientStateMessage.CreateSynchronized` hard-codes `State = "synchronized"`, and `SendPlayerStateAsync` (`SendSpinClient.cs:555`) uses it for **volume and mute updates**. So today, changing the volume while in external-source mode silently tells the server the client is synchronized again.

Under the new shape that becomes `available: true`, which is worse than cosmetic — it would assert availability the client does not have, and interacts with the group machinery in §3.

**Player-state deltas must omit `available` entirely.** Only the initial message and the §2 publisher may set it. This is the one place where the rename fixes a live defect rather than a spelling.

## 5. Out of scope

| Deferred | Reason |
|---|---|
| Putting error *detail* on the wire | The owner chose the plain `available: false` mapping. `PlayerStatePayload.Error` is an existing SDK extension on a path that does not currently send it; leave it untouched |
| #80, #81 — re-handshake sniff and key-swap race | Own issues, and both sit on the pairing path rather than this one |
| #82 — source pipeline deltas | Only reachable with the `source` role |
| #91 — the version bump | Package version stays `9.1.0`; nothing may be published at it |

## 6. Testing

Every test must assert the **absence of the harm**, and any test asserting that something did *not* happen must survive "would this pass if the machinery producing it were deleted?" That check has found a real defect in each of the last four slices, so it is a standing gate.

| Item | Test | The assertion that matters |
|---|---|---|
| §1 | the initial `client/state` for a player | carries `"available": true` and **no** `"state"` key — assert the absence explicitly, or the old field could ship alongside the new one |
| §1 | a player-state delta | carries `player` and **no** `available` key (§4) |
| §3 | a player client, sync not yet converged | **no** `client/state` has been sent at all. Positive control: once sync converges, exactly one is sent, with `available: true` |
| §3 | an artwork-only client (no `player`/`source` role) | initial `client/state` sent immediately on activate, without waiting for sync |
| §2 | `EnterExternalSourceAsync` | sends `available: false`; `ExitExternalSourceAsync` sends `available: true` |
| §2 | pipeline error, then recovery | `available: false` then `available: true` |
| §2 | pipeline error reported twice | exactly **one** `available: false` reaches the wire — pins the no-change suppression, which nothing else would catch |
| §2 | volume change while in external-source mode | the delta does **not** contain `available` (the §4 defect, mutation-checked: hard-code `Available = true` in the player delta and this must fail) |
| §3 | reconnect while sync is converging | no `available: false` is ever emitted — the hazard that chose shape (A) |

The last row is the one that would be skipped and the one that protects the design decision.

## 7. Success criteria

1. No `[JsonPropertyName("state")]` remains in `client/state`; `available` is a boolean present on the initial message.
2. A player-state delta never carries `available`.
3. A `player`/`source` client sends no `client/state` before clock sync converges, and exactly one `available: true` after.
4. An artwork- or visualizer-only client sends its initial `client/state` immediately.
5. External source and pipeline error both map to `available: false`, recovery to `true`, with repeated identical values suppressed.
6. No path emits `available: false` on a reconnect that is merely still converging.
7. Full suite green on `net10.0`; library builds clean on `net8.0` and `net10.0`; `<Version>9.1.0</Version>` unchanged.
8. The interop workflow still passes — and note it cannot prove this fix, per §0, so its value here is only that nothing else regressed.
