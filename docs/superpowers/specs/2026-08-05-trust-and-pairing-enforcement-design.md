# Design: Trust and pairing enforcement

*Closes [#74](https://github.com/Sendspin/sendspin-dotnet/issues/74), [#75](https://github.com/Sendspin/sendspin-dotnet/issues/75), [#76](https://github.com/Sendspin/sendspin-dotnet/issues/76), [#87](https://github.com/Sendspin/sendspin-dotnet/issues/87). Follows the v10 encrypted-only clean break ([#94](https://github.com/Sendspin/sendspin-dotnet/pull/94)).*

## 1. Why these four together

They are one problem in four tickets. All live on the `server/activate` → pairing → `server/command` paths in `SendspinClientService`, and two of them cannot be fixed independently:

- **#74's prescribed response is #76's fix.** The spec's answer to a disallowed pairing method is `pair/abort { method_not_supported }` with the connection left open — which is exactly what #76 asks for, in the same `switch` arm.
- **#74 chains into #75.** An attacker pairs over the published Sentinel PSK constant (#74), reaching trust `user`, and then streams the microphone (#75). Either alone is serious; together they are a capture-device takeover requiring no operator action and no out-of-band secret.

Two findings from exploration reshaped the work relative to the issue text, and are recorded here because they change what gets built:

1. **#87 item 4 needs no new interface.** `NoiseWireFraming` assigns `_handshakeHash` at `NoiseWireFraming.cs:241`, inside `RunResponderExchange`, which serves both the initial handshake and in-band re-handshakes. `INoiseSessionInfo.HandshakeHash` therefore already changes on every re-handshake and is already visible to the client service.
2. **#87 item 2's premise is narrower than stated.** Every reader of `record.Used` was checked: it appears in the `management/list-records` response (`SendSpinClient.cs:1208-1209`) and in persistence. **Nothing gates on it.** Setting it early does not burn a credential, because no code path rejects a used record — and spec #122 says the Pairing PSK is not consumed by a successful pairing at all. What is actually wrong is a side-effecting `Resolve()`, an untruthful report to the server, and a synchronous disk write on the crypto receive path.

## 2. One admissibility rule for pairing methods

The spec rule quoted in both #74 and #76 has two limbs that resolve all three of #74, #76, and #87 item 1:

> If `'pairing'` is in `activities` with a `selected_pair_method` the matched PSK **disallows** *or the client does not currently offer* — reply with `pair/abort` reason `method_not_supported`, **leaving the connection open**. The check uses the **live pairing configuration, which may have drifted from `supported_pair_methods`**.

- #74 is limb one: a Sentinel-keyed session disallows `pairing_psk`.
- #87 item 1 is limb two: with no record store we do not *currently* offer `pairing_psk`, even though `BuildPairMethods` advertised it.
- #76 is the shared consequence: keep the connection open.

The spec explicitly anticipates the advertisement drifting from live capability, which is precisely the no-store case.

### 2.1 `CanOffer`

`HandlePairingActivate` (`SendSpinClient.cs:941`) evaluates a live-capability check before any method-specific work:

```csharp
/// <summary>
/// Whether this client can currently complete the given pairing method on this
/// session. The spec's check is against live capability, which may differ from
/// what supported_pair_methods advertised.
/// </summary>
private bool CanOffer(string? method) => method switch
{
    // pairing_psk is admissible only on a session already keyed by the Pairing PSK
    // (#74), and only when we can persist the resulting long-term record (#87-1).
    "pairing_psk" => _session.MatchedPsk?.Category == PskCategory.Pairing
                     && _pairingStore is not null,
    "dynamic_pin" => _capabilities.PinPairingMethods.Contains("dynamic_pin"),
    "static_pin"  => _capabilities.PinPairingMethods.Contains("static_pin"),
    _ => false,
};
```

On failure: send `pair/abort { method_not_supported }` and **return without disconnecting**. The `DisconnectAsync("unauthorized")` at `SendSpinClient.cs:972` is deleted — that is #76.

Only after the check passes does the method dispatch run. `method_not_supported` is already in use at `:970`, so no new wire value is introduced.

### 2.2 Consequences

- **#74 closes at the source.** The long-term PSK is never generated and `client/pair-finalize` is never sent on a Sentinel-keyed session. There is no window in which an unauthenticated peer receives a credential.
- **#87 item 1 aborts before the server persists.** Today the client mints the PSK, sends it, then warns after the fact and raises `PairingCompleted` regardless — leaving the server holding a half the client can never authenticate against. Aborting up front means the server writes nothing.

### 2.3 What must not change

`ValidateActivateAdmissibility` and its `IsAdmissible` table (`SendSpinClient.cs:~808-869`) are **not** touched. `IsAdmissible` admits `Sentinel + {pairing}`, and that is correct: the PIN methods legitimately run on a Sentinel-keyed session because the CPace PAKE is what authenticates there. Only `pairing_psk` requires the session to already be Pairing-PSK-keyed, and that belongs in `CanOffer`.

Widening the admissibility table to reject Sentinel-keyed pairing would break PIN pairing entirely. This is the easiest thing to get wrong while fixing #74.

## 3. The source gate (#75)

Today the `source@v1` trust check runs only when `server/activate` lists the role (`SendSpinClient.cs:778-786`). `server/command { source: { command: "start" } }` routes straight into `_sourcePipeline.HandleCommandAsync` at `:1825-1827` with no trust check and no active-role check, so a Sentinel-keyed server that never activates the role can open the capture device.

### 3.1 Enforce where the harm happens

`SourceStreamPipeline` gains a `Func<bool> canStream` constructor parameter — matching the `Func<>` delegate idiom it already uses for `_sendBinaryAsync` and `_sendMessageAsync` — checked at the top of `StartStreamingAsync`:

```csharp
private async Task StartStreamingAsync()
{
    lock (_lock)
    {
        if (_streaming || _disposed) return;
        if (!_canStream())
        {
            _logger.LogWarning(
                "Refusing to stream: source@v1 requires user trust and an active source role");
            return;                      // the capture device is never opened
        }
        _streaming = true;
    }
    // ...unchanged
}
```

The check is **before** `_streaming = true` (currently `SourceStreamPipeline.cs:79`), so a refused start cannot leave the pipeline wedged in a streaming state it never entered.

The client supplies the predicate: `_session.MatchedPsk?.Category == PskCategory.LongTerm` **and** `source@` present in the current `active_roles`.

Gating at the point where the capture device opens means every caller is covered — the activate path, the command path, and any path added later. Gating at the command dispatch instead would leave the same structural weakness that produced this bug: one chokepoint, bypassed by a second route.

### 3.2 This is not a duplicated check

The existing activate-time gate at `:778-786` stays. It does a different job: it refuses the *activation* and closes the connection with `unauthorized`, which is the spec's prescribed response to an inadmissible `server/activate`. The new gate refuses to *stream*. Different triggers, different responses, no shared predicate to drift.

### 3.3 Refused commands are silent

A refused `server/command` is logged and ignored, with no reply to the server. No stream began, so there is no `client_stream/end` to send, and the spec prescribes no response to a refused command.

### 3.4 The docstring

`SourceStreamPipeline.cs:19-20` currently reads:

> The pipeline does not itself enforce trust; the client service refuses to activate the source role at trust level `none`.

That is the guarantee this issue disproves. It is rewritten to describe what the pipeline actually enforces, because a reader will trust it instead of re-deriving reachability.

## 4. Pairing lifecycle (#87 items 2-4)

### 4.1 `Resolve()` becomes a pure lookup

`RecordPskResolver.Resolve` (`PairingRecordStore.cs:~123-135`) drops its `Upsert(record with { Used = true })`. This also removes a synchronous disk write from inside `ProcessInbound`, on the crypto receive path.

The client marks the matched PSK used once an encrypted application message actually arrives — proof the AEAD verified. In practice that is `server/hello`, the first message the client can decrypt.

**No interface change.** `IPairingRecordStore` already exposes `List()` and `Upsert()`, which is everything needed:

```csharp
// once per session, on the first decrypted application message
private void MarkMatchedPskUsed()
{
    if (_markedPskUsed || _pairingStore is null) return;
    if (_session.MatchedPsk is not { } matched) return;

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

Adding a `MarkUsed` method to `IPairingRecordStore` was considered and rejected: it is a public interface, so every implementer would have to add it, and the existing operations already express the intent. A `Confirm` on `INoisePskResolver` was rejected for the same reason plus a worse one — it would make a lookup abstraction responsible for lifecycle.

`_markedPskUsed` resets wherever the session identity can change: alongside the existing per-connection reset path, and on re-handshake (detected by the same `HandshakeHash` change §4.3 uses), so a rotated-to PSK is marked in its turn.

### 4.2 The Pairing PSK is not retired

Spec #122 states the Pairing PSK is not consumed or rotated by a successful pairing, persists across reboots, and may pair the client with any number of servers. The current code already leaves the bootstrap record in place; this design records that as deliberate, in the record's doc comment, so it is not "fixed" later by someone reading #87 item 3 without the spec context.

### 4.3 `_pairingCounter` resets on re-handshake

`PinPairing.BuildSid` documents the counter as "the number of pairing `server/activate` messages since the last Noise handshake". `_pairingCounter` (`SendSpinClient.cs:36`) is client-lifetime and never resets, so after any key rotation the CPace `sid` diverges from a conformant server's and PIN pairing fails.

Because `INoiseSessionInfo.HandshakeHash` already changes on every re-handshake, the client caches the last-seen hash and resets the counter to zero when it differs, before incrementing. No interface change.

Lower urgency than the rest — the PIN methods are spec-optional and unproven against a live counterparty — but it is a real conformance bug and the fix is small.

## 5. Testing

Every test asserts the **absence of the harm**, not the presence of a log line.

| Issue | Test | The assertion that matters |
|---|---|---|
| #74 | `pairing_psk` selected on a Sentinel-keyed session | `client/pair-finalize` is **never sent** — not merely that an abort was sent |
| #74/#76 | same | connection still `Connected`; `LastDisconnectReason` is null |
| #87-1 | `pairing_psk` with `PairingRecordStore = null` | no `pair-finalize`, **no `PairingCompleted` event**, abort sent, connection open |
| #75 | `server/command source start` at trust `none` | `FakeCaptureDevice.Capturing == false` — the device never opened |
| #75 | `server/command source start` with `source@` not in `active_roles` | same |
| #75 | **positive control**: user trust + active role | `Capturing == true` |
| #87-2 | `RecordPskResolver.Resolve` | store contents unchanged after resolve; `Used` set only after an encrypted message arrives |
| #87-4 | `HandshakeHash` changes between two pairing activates | counter restarts at 1, observable via `pairing_index` on `client/pair-init` |

The positive control on #75 is not optional: a gate that refuses everything would pass all three negative tests.

`FakeCaptureDevice` (with its `Capturing` flag) is currently private to `SendspinClientServiceSourceTests` and gets promoted to a shared test helper alongside `TestClient`.

`SendspinClientServicePairingTests` currently only ever constructs `PskCategory.Pairing`, which is why #74 went unnoticed — the negative case was unrepresentable in the existing fixtures.

## 6. Scope

**Closes #74, #75, #76, #87.**

**Not in scope:**

| Deferred | Reason |
|---|---|
| #86 — record-store hardening | Different file, no shared decisions. **Its item 4 (sync disk IO on the receive path) is resolved as a side effect of §4.1** — update #86 to reflect that |
| #88 — JSON injection, `catch(Exception)`, config mutation, fragmentation caps | Four independent defects in unrelated paths |
| #85 — pairing initiation API | This design covers server-driven pairing; #85 is the client initiating it |
| #79, #91, #92 | Upstream-blocked and release engineering, unchanged |

Package version stays `9.1.0` (#91 owns the bump).

## 7. Success criteria

1. A Sentinel-keyed server that sends `server/activate` with `activities: ["pairing"], selected_pair_method: "pairing_psk"` receives `pair/abort { method_not_supported }`, the connection stays open, and no `client/pair-finalize` is emitted.
2. A client with no `IPairingRecordStore` behaves identically, and never raises `PairingCompleted`.
3. `server/command { source: { command: "start" } }` at trust `none`, or with the source role inactive, does not open the capture device.
4. The same command at trust `user` with an active source role does open it.
5. `RecordPskResolver.Resolve` performs no writes.
6. `_pairingCounter` restarts after a re-handshake.
7. Full suite green on `net10.0`; `dotnet build` clean on `net8.0` and `net10.0`.
8. The interop workflow still passes against `aiosendspin[server]==7.0.0`.
