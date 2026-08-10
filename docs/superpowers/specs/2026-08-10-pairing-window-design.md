# Pairing window and spec #130/#131 conformance — design

**Issue:** [#127](https://github.com/Sendspin/sendspin-dotnet/issues/127) (v10 blocker)
**Spec basis:** `Sendspin/spec` at `5b0e6469` ("Improve PIN pairing window", spec PR #130,
2026-08-05) and `21b97460` ("Add language hint for spoken dynamic PIN", spec PR #131,
2026-08-05).
**Date:** 2026-08-10

---

## Why this is larger than the issue as filed

#127 describes the pairing window, the escalation semantics, `client/pair-pending`,
`management/open-pairing-window`, and removal of the non-spec `locked_out`. All of that is
real. What the issue does not say is that the same upstream spec PR — #130 — also reshaped
`server/activate`, and that reshape is a prerequisite for the gating policy.

The SDK is pinned to spec `7c04eb7` (2026-07-14). Three spec commits have landed since:

| Spec commit | Date | Subject |
|---|---|---|
| `8a77e1bf` | 2026-07-31 | Resolve more pre-1.0 spec gaps (spec #122) |
| `5b0e6469` | 2026-08-05 | **Improve PIN pairing window** (spec #130) |
| `21b97460` | 2026-08-05 | Add language hint for spoken dynamic PIN (spec #131) |

Issue #92 tracks re-pinning the spec and auditing against spec #122/#125/#126 — a list that
predates the two commits that matter here.

### The reshape, and what it breaks today

Spec #130 replaced a flat field with a nested object:

```diff
- selected_pair_method?: 'dynamic_pin' | 'pairing_psk' | 'static_pin'
+ pairing?: object
+   method: 'dynamic_pin' | 'pairing_psk' | 'static_pin'
+   pin_length?: integer      # required when method is dynamic_pin
+   languages?: string[]      # spec #131
```

`ServerActivatePayload` still models the flat `selected_pair_method`
(`ServerActivateMessage.cs:42`). Against a current-spec server that field is never present,
so `SelectedPairMethod` is null, `CanOffer(null)` returns false, and the client answers
**every** pairing activation with `pair/abort` reason `method_not_supported` — all three
methods, `pairing_psk` included, not just the PIN flows.

This is invisible today because `.github/workflows/interop.yml` pins
`aiosendspin[server]==7.0.0`, which predates spec #130, and because the interop gate has no
PIN-pairing scenario (that gap is issue #90 item 4).

The SDK was correct for the spec it was written against. This is drift, not a long-standing
bug.

### Why the reshape blocks the window

Spec #130 also moved `pin_length` out of `server/pair-init` and into the activation, to be
validated on receipt of the activation (`pairing.md:149`). The gating policy is:

> `dynamic_pin` — only when the method is escalated, or when the session's `pin_length` is
> below **6** (`pairing.md:230`).

That decision must be made **before** `client/pair-init` is sent. The SDK currently reads
`pin_length` from `server/pair-init` (`SendSpinClient.cs:1698`), which arrives after. Without
the reshape the gate cannot be evaluated at all.

Scope decision: one PR covering all of spec #130 + #131, rather than splitting the reshape
out, because they are one upstream change and the intermediate state is not conformant.

---

## Audit results

Full drift for `pairing.md` + `management.md` + the `server/activate` section, against spec
HEAD.

### Wire shape

| # | Gap | Consequence |
|---|---|---|
| 1 | `server/activate` models flat `selected_pair_method` | All pairing aborts `method_not_supported` |
| 2 | `pin_length` read from `server/pair-init` | Dynamic PIN aborts `pin_length_unacceptable`; gate unevaluable |
| 3 | `languages` (spec #131) not modelled | Spoken PIN emission cannot honour operator language preference |
| 4 | `ServerPairInitPayload.PinLength` exists | Reads a field the spec removed |

### Pairing window

| # | Gap |
|---|---|
| 5 | No window state: no open/close, no lifetime, no single-attempt admission |
| 6 | No gating policy |
| 7 | `client/pair-pending` absent entirely |
| 8 | `client/pair-init` not withheld pending a window |
| 9 | `management/open-pairing-window` absent |
| 10 | Terminal lockout and non-spec `locked_out` abort reason still present |

### Adjacent

- **`attempt_timeout` is entirely unimplemented.** The string appears nowhere in `src/`. The
  spec requires the client to bound every attempt (recommended 2 minutes) and abort with that
  reason on expiry (`pairing.md:26`). It predates spec #130, but the window closes *on
  attempt-timeout expiry*, so implementing the window without it would leave a close condition
  that can never fire. In scope.
- The failure counter itself is already spec-correct: `RecordPinFailure` is called at exactly
  one site, the `server_kc` verification failure (`SendSpinClient.cs:1817-1820`), matching
  `pairing.md:180`. Only the behaviour *at 10* is wrong.
- `pairing_index` is already sent on `client/pair-init`. The receive-side rules in
  `pairing.md:290` bind the server, not the client — no client-side gap.
- `locations?` on the pair-method descriptor is missing. Tracked as #129, out of scope.
- `storage` accounting on `management/result` is absent. Permitted: `management.md:141` lets a
  client with unbounded or unknown storage omit it. Out of scope.

---

## Design

### 1. Wire shape

`ServerActivatePayload` replaces `SelectedPairMethod` with a nested object:

```csharp
[JsonPropertyName("pairing")]
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public PairingActivation? Pairing { get; set; }

public sealed class PairingActivation
{
    [JsonPropertyName("method")]     public string Method { get; set; } = string.Empty;
    [JsonPropertyName("pin_length")] public int? PinLength { get; set; }
    [JsonPropertyName("languages")]  public List<string>? Languages { get; set; }
}
```

`ServerPairInitPayload` drops `PinLength`, leaving only `nonce_A`.

`HandlePairingActivate` validates in this order:

1. **Method admissibility** — `CanOffer(payload.Pairing?.Method)`; on failure `pair/abort`
   reason `method_not_supported`, connection left open (unchanged behaviour).
2. **PIN length** — for `dynamic_pin` only: `pin_length` must be present and within
   `[_effectiveMinPinLength, 12]`; otherwise `pair/abort` reason `pin_length_unacceptable`.

Method admissibility comes first because a method the client does not offer is not a PIN-length
question. Both are spec-defined reasons; only the ordering is ours.

`pin_length` and `languages` are retained on the pending attempt state for the gate and the PIN
presenter respectively.

These are breaking changes to public types. v10 is already a major release.

### 2. `PresentPinAsync` gains a context object

The `languages` hint is informational and never grounds for abort (`pairing.md:161`), so
parsing and ignoring it would be conformant. But the spec says the client SHOULD emit in the
best-matching language it supports, and an application cannot comply with a hint the SDK never
surfaces.

The presenter delegate changes from `Func<string, CancellationToken, Task>` to take a context:

```csharp
public sealed record PinPresentation(string Pin, IReadOnlyList<string>? Languages);

// SendspinClientOptions
public Func<PinPresentation, CancellationToken, Task>? PresentPinAsync { get; init; }
```

Language *matching* stays with the application: the spec's RFC 4647 Lookup is over the set of
languages the app can actually speak, which the SDK does not know.

### 3. `PairingWindow`

The window is device-level, not connection-level. The spec closes it on "drop of the
connection carrying its attempt" (`pairing.md:225`) — phrasing that only makes sense if the
window outlives any single connection — and defines it as "a state in which the client has
decided to accept one pairing attempt", singular. `SendSpinClient` is per-connection and
`SendspinHostService` runs several concurrently, so window state must live above the
per-connection client or each server would get its own "single" window.

```csharp
public sealed class PairingWindow
{
    public PairingWindow(TimeSpan? lifetime = null, TimeProvider? timeProvider = null);

    public bool IsOpen { get; }
    public void Open();       // operator gesture, or management/open-pairing-window
    public void Close();      // operator cancellation
    public event EventHandler? StateChanged;

    internal bool TryConsume();   // admits exactly one attempt; returns false if not open
}
```

- **Lifetime** runs from `Open()` until `client/pair-init` is sent (`pairing.md:235`), default
  5 minutes. On expiry the window closes silently. After `client/pair-init` the attempt
  timeout governs instead.
- **Single admission** — `TryConsume` returns true to at most one caller per opening, under a
  lock. This is what makes two concurrently-awaiting connections resolve to exactly one winner.
  The losers remain pending: they have already sent `client/pair-pending`, they send nothing
  further, and they wait for a subsequent opening. Nothing is re-sent when they lose the race.
- **`Close()` closes the window only.** It does not abort an attempt already in progress —
  once `client/pair-init` is sent the window has been consumed and the attempt is bounded by
  its own timeout. Operator cancellation *of an attempt* is a separate action that sends
  `pair/abort` reason `user_cancelled`, and which also closes the window per `pairing.md:225`.
- **Thread safety** — every member is safe for concurrent use across connections.
- **No window configured** — `SendspinClientOptions.PairingWindow` is optional; a null window
  is treated as permanently closed, so gated attempts stay pending indefinitely. That is the
  fail-closed direction, and it matches how `CanOffer` already refuses PIN methods that lack
  their supporting store.
- Supplied through `SendspinClientOptions.PairingWindow`, alongside the existing stores. A host
  that never sets one gets a client where gated attempts can never proceed, which is the
  fail-closed direction.

**Known trap.** `SendspinHostService` builds a fresh `SendspinClientOptions` per connection by
hand-copying selected fields (`SendSpinHostService.cs:447-451`). A new option that is not added
to that mirror silently fails to reach any connection: gating would degrade to "never gated"
and every single-connection test would still pass. This is the "one fact in two places"
defect shape. Mitigated by an explicit regression test (see Testing). Issue #95 proposes
converting the options type to a record to delete this mirror; that remains out of scope here.

**Clock.** The SDK has no clock abstraction — `TimeProvider` appears nowhere in `src/`. This
design introduces `TimeProvider` (BCL from net8.0, so both target frameworks have it) for the
window lifetime and the attempt timeout, so both timers are testable without sleeping. It
defaults to `TimeProvider.System`.

### 4. Gating policy and `client/pair-pending`

Evaluated in `HandlePairingActivate` after validation:

| Method | Gesture-gated? |
|---|---|
| `pairing_psk` | Never |
| `static_pin` | Every attempt |
| `dynamic_pin` | Only when escalated, or when `pin_length < 6` |

```
if (gated && !window.TryConsume())
{
    send client/pair-pending { pairing_index }   // does NOT start the attempt or its timeout
    mark this activation as awaiting a window
    raise PairingGestureRequested
    return;
}
StartPinAttempt(...)   // window already consumed when gated
```

`client/pair-pending` carries only `pairing_index` and explicitly does not start the attempt
(`pairing.md:26`, `:294`), so no attempt timeout is armed when it is sent.

When the window opens later, a client holding a pending gated activation consumes it and sends
`client/pair-init`. A client whose pending activation has been superseded (a newer pairing
`server/activate`, or the connection left pairing) discards it without consuming the window.

New public event so a host can prompt its operator:

```csharp
event EventHandler<PairingGestureRequestedEventArgs>? PairingGestureRequested;
// carries: Method, PairingIndex
```

Raised exactly once per gated activation that finds no open window — at the same point
`client/pair-pending` is sent. It is not re-raised when a window opens and is lost to another
connection, so a host prompt maps one-to-one with a pending activation.

### 5. Escalation replaces terminal lockout

- Remove the refuse-at-10 block and the `locked_out` abort reason
  (`SendSpinClient.cs:1667-1674`), and the mention of `locked_out` in the `PairAbortPayload`
  doc comment (`PairingMessages.cs:65-66`). `locked_out` appears nowhere in the spec.
- Rename `IsPinMethodLockedOut` to `IsMethodEscalated`. The predicate is unchanged
  (`failures >= 10`); it now feeds the gating policy and `get-pairing-config.escalated`
  instead of refusal.
- Counting and reset are already correct and are not touched.
- `CanOffer` continues to require an `IPinLockoutStore` for both PIN methods: without one the
  counter cannot persist, escalation could never trigger, and attempts would stay ungated
  forever. The behaviour stays; the comment explaining it is rewritten, because it currently
  cites the terminal lockout this change deletes.

This is what fixes the deadlock in #127: at 10 failures the method is still offered and still
runs — it just requires a gesture first, and a successful `server_kc` verification inside that
gated attempt resets the counter.

### 6. Attempt timeout

Armed when an attempt starts — `client/pair-init` for the PIN flows, `client/pair-finalize`
for Pairing PSK (`pairing.md:26`). Default 2 minutes, configurable via
`SendspinClientOptions`. On expiry: send `pair/abort` reason `attempt_timeout`, clear PIN
state, and close the window.

### 7. `management/open-pairing-window`

New `MessageTypes.ManagementOpenPairingWindow`, handled in the existing management dispatch:

| Condition | Result |
|---|---|
| Not a management session | `permission_denied` |
| No PIN method enabled | `invalid` |
| Window already open | `ok` (no-op) |
| Otherwise | `window.Open()`, then `ok` |

---

## File-by-file

**Create**

- `src/Sendspin.SDK/Client/PairingWindow.cs` — the shared window.
- `src/Sendspin.SDK/Client/PairingGestureRequestedEventArgs.cs`
- `src/Sendspin.SDK/Client/PinPresentation.cs`
- `tests/Sendspin.SDK.Tests/Client/PairingWindowTests.cs`
- `tests/Sendspin.SDK.Tests/Client/PairingGatingTests.cs`
- `tests/Sendspin.SDK.Tests/Client/PairingAttemptTimeoutTests.cs`

**Modify**

- `Protocol/Messages/ServerActivateMessage.cs` — `PairingActivation`, drop `SelectedPairMethod`.
- `Protocol/Messages/PairingMessages.cs` — add `ClientPairPendingMessage`/`Payload`; drop
  `PinLength` from `ServerPairInitPayload`; correct the `pair/abort` reason doc.
- `Protocol/Messages/MessageTypes.cs` — `ClientPairPending`, `ManagementOpenPairingWindow`.
- `Protocol/MessageSerializerContext` — source-gen entries for the new types (this is the
  mandatory transport path; reflection JSON breaks AOT, see #89).
- `Client/SendSpinClient.cs` — activation parsing and validation, gating, pending state,
  window consumption, escalation rename, attempt timeout, management handler.
- `Client/SendspinClientOptions.cs` — `PairingWindow`, `AttemptTimeout`, presenter signature.
- `Client/ISendSpinClient.cs` — `PairingGestureRequested`.
- `Client/SendSpinHostService.cs` — **propagate `PairingWindow` through the per-connection
  options mirror.**
- `tests/.../Client/FakeServer.cs`, `Connection/TestNoiseServer.cs` — emit the new activation
  shape.

---

## Testing

Every behavioural rule below is a spec MUST or SHOULD, and each gets a test that fails against
the current code.

**Window mechanics** — open/close; lifetime expiry closes silently; `TryConsume` succeeds once
per opening; **two connections sharing one window produce exactly one winner**; a closed window
refuses.

**Gating policy** — table-driven over method × escalated × `pin_length`: `pairing_psk` never
gated; `static_pin` always; `dynamic_pin` gated iff escalated or `pin_length < 6`. The
boundary cases at 5/6 are explicit.

**`client/pair-pending`** — sent on a gated activation with no open window, carrying the
current `pairing_index`; **no attempt is started and no attempt timeout is armed**; the attempt
begins only once the window opens.

**Escalation** — at 10 failures the method is still offered and the attempt still runs, gated,
rather than being refused; a successful `server_kc` verification resets the counter and
de-escalates. This is the #127 deadlock, tested directly.

**Wire conformance** — activation with the nested `pairing` object round-trips, including
`pin_length` and `languages`; `pair/abort` never carries `locked_out`; `server/pair-init` with
only `nonce_A` is accepted.

**Validation order** — an activation naming an unofferable method with a bad `pin_length`
produces `method_not_supported`, not `pin_length_unacceptable`.

**Attempt timeout** — expiry produces `pair/abort` reason `attempt_timeout` and closes the
window; driven by a fake `TimeProvider`, not by sleeping.

**`management/open-pairing-window`** — all four outcomes, including the no-op `ok`.

**Options mirror** — a `PairingWindow` set on `SendspinHostService` options reaches the
per-connection client. Guards the trap described in §3; would fail today if the mirror is
missed.

**End-to-end** — at least one gated flow driven through `TestNoiseServer`: activation →
`pair-pending` → window opens → `pair-init` → PAKE → finalize.

---

## Out of scope

- `locations?` on the pair-method descriptor (#129).
- `storage` accounting on `management/result` (permitted to omit).
- Converting `SendspinClientOptions` to a record to delete the options mirror (#95).
- The wider spec re-pin and audit (#92) — but that issue's audit list should be updated to name
  spec #130 and #131, and `docs/SPEC-VERSION.md` (#91) should record the spec commit this work
  targets.
