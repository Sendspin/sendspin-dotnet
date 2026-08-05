# Hostile-Input Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop the management path building JSON by string interpolation, stop the inbound-message path swallowing malformed authenticated input, and implement the three fragmentation rules the spec makes normative.

**Architecture:** Management responses move onto typed DTOs serialized through the existing source-generated `MessageSerializerContext`, so `ManagementResultPayload.Data` is produced by a serializer rather than string concatenation. The five inbound-path catch-alls narrow to the failure types a malformed payload actually produces and close the connection on one. Fragmentation gains the two missing spec closes plus a phase-aware reassembly cap held entirely inside `NoiseWireFraming`.

**Tech Stack:** C# / .NET (`net8.0;net10.0`), xUnit, source-generated `System.Text.Json`, `Noise.NET`.

## Global Constraints

- Design of record: `docs/superpowers/specs/2026-08-05-hostile-input-hardening-design.md`. Where this plan and the design disagree, **the design governs** — report the discrepancy rather than picking one.
- Spec of record: `Sendspin/spec` @ `d5f64a6a`, `messaging.md:101-107` for fragmentation and `:426` for the `client/goodbye` reason list.
- **No new `client/goodbye` reason.** The spec's list is closed (`another_server | shutdown | restart | user_request | unauthorized | pairing_required | concurrent_attempt | unpaired`). A protocol error closes the connection **without** a goodbye, via the framing layer's existing `InboundFrameResult.Fatal`.
- **Never catch bare `Exception`** in code this plan touches. ohf-sage, Overall layer, `[mined · 11 PRs · 👍]`: catch specific expected types. And `[mined · 6 PRs · 👍]`: never silently swallow anomalous data — let it propagate.
- **Do not fix issues that are not this slice's**: #80's `noise/handshake` substring sniff lives in a method Task 3 touches — leave it. #88 item 3 (config mutation) is slice B. The 14 non-peer-input catch-alls listed in design §2.2 are out of scope.
- Target frameworks `net8.0;net10.0`; nullable on with `CS8600;CS8601;CS8602;CS8603;CS8604;CS8618;CS8625` as errors.
- Package version stays `9.1.0` (#91 owns the bump).
- Commit messages: no AI attribution, no `Co-Authored-By`, no self-reference. Write as the repo owner.
- Baseline entering this plan: **375 passing, 0 failing** on `net10.0`.
- **Express test-count gates as deltas**, and record the absolute figure observed. Zero failing is the invariant.
- Full suite: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo`; filtered: append `--filter "FullyQualifiedName~<ClassName>"`.
- Run `dotnet test` in the **foreground**. Backgrounded runs have held file locks in this repo; if one stalls, kill orphaned `dotnet.exe` / `vstest.console.dll` processes.
- **Any test asserting that something did *not* happen must be checked against: "would this pass if the machinery producing it were deleted?"** Three of five tasks in the previous slice shipped a test that failed that question.
- Branch `fix/hostile-input-hardening`, already created from `main` @ the merge of PR #104.

## File Structure

**Created:**

| File | Responsibility |
|---|---|
| `src/Sendspin.SDK/Protocol/Messages/ManagementData.cs` | Typed DTOs for `management/result` `data` payloads |
| `tests/Sendspin.SDK.Tests/Client/ManagementInputValidationTests.cs` | `server_id` and PSK validation, response round-tripping |
| `tests/Sendspin.SDK.Tests/Connection/FragmentationConformanceTests.cs` | The three malformed sequences plus the cap |

**Modified:**

| File | Change |
|---|---|
| `src/Sendspin.SDK/Client/SendSpinClient.cs` | Serialize management data; validate `server_id`; PSK decode helper; narrow four catch-alls |
| `src/Sendspin.SDK/Protocol/MessageSerializerContext.cs` | Register the new DTOs |
| `src/Sendspin.SDK/Connection/Noise/SendspinIdentity.cs` | Add the PSK decode helper alongside `DecodePeerId` |
| `src/Sendspin.SDK/Connection/Noise/NoiseWireFraming.cs` | Two missing fragment closes; phase-aware cap |
| `src/Sendspin.SDK/Connection/Noise/NoiseConstants.cs` | The pre-first-message cap constant |
| `src/Sendspin.SDK/Client/SendSpinHostService.cs` | Narrow the one inbound-path catch-all |

---

### Task 1: Serialize management responses and validate their input (#88 item 1)

**Files:**
- Create: `src/Sendspin.SDK/Protocol/Messages/ManagementData.cs`
- Modify: `src/Sendspin.SDK/Protocol/MessageSerializerContext.cs`, `src/Sendspin.SDK/Connection/Noise/SendspinIdentity.cs`, `src/Sendspin.SDK/Client/SendSpinClient.cs` (`HandleManagement`, ~`:1300-1440`)
- Test: `tests/Sendspin.SDK.Tests/Client/ManagementInputValidationTests.cs`

**Interfaces:**
- Consumes: `ManagementResultPayload.Data` is `JsonElement?` (`ManagementMessages.cs:32`) — that is why the current code hand-builds JSON strings; `MessageSerializerContext` (source-generated, `[JsonSerializable]` per type); `NoiseConstants.KeySize`; `Base64UrlText.Decode` (internal, throws `FormatException`).
- Produces: `internal static byte[] SendspinIdentity.DecodePsk(string encoded)`; the DTO types in `ManagementData.cs`. No later task depends on these.

- [ ] **Step 1: Write the failing tests**

Create `tests/Sendspin.SDK.Tests/Client/ManagementInputValidationTests.cs`. Read `SendspinClientServiceManagementTests.cs` first and reuse its client-construction helper rather than writing a new one — it already builds a management-activated client, which these tests need.

Cover exactly these cases:

1. **Injection is rejected at ingest.** Send `management/add-record` whose `server_id` is `srv","used":true,"x":"` (a value containing `"` and `:` and `,`). Assert the record store is **unchanged** (`store.List()` is empty) and the `management/result` carries `invalid_request`. This is the test that matters most: it asserts the absence of the harm at the boundary.
2. **A valid record round-trips through `list-records`.** Store a record with a legitimate 43-character base64url `server_id`, request `management/list-records`, then parse the emitted `ManagementResultMessage`'s `Data` with `JsonDocument` and assert the `server_id` string equals the original **exactly**. A serializer regression to interpolation would still pass a "contains" check, so compare for equality.
3. **`server_id` of the wrong length is rejected.** A 42-character and a 44-character value both give `invalid_request` and leave the store unchanged.
4. **A malformed PSK names the PSK.** Send `add-record` with a valid `server_id` but a PSK that is not decodable, and assert the failure surfaces as `invalid_request` **and** that the log or error text mentions the PSK rather than "peer id". If the code path has no observable message, assert on `invalid_request` and note in the report that the message is only visible in logs.
5. **Positive control for `set-pairing-config`.** The `unpaired_access` response at `~:1403` is also interpolated. Exercise it and assert its `Data` parses and carries the expected boolean, so the serializer change is proven for both response shapes.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo --filter "FullyQualifiedName~ManagementInputValidationTests"`

Expected: the injection and length tests FAIL (no validation exists today, so the record is stored and the result is `ok`). The round-trip and `set-pairing-config` tests may pass already — note which, since a test that passes before the change is not testing the change.

- [ ] **Step 3: Add the typed DTOs**

Create `src/Sendspin.SDK/Protocol/Messages/ManagementData.cs` with records shaped to the wire payloads the current interpolation produces — `{"records":[{"psk_id":…,"server_id":…,"used":…}]}` and `{"pairing_psk":{"enabled":true},"unpaired_access":{"enabled":…}}`. Use `[JsonPropertyName]` for the snake_case names and `JsonIgnoreCondition.WhenWritingNull` on the optional `server_id`, matching the conditional the interpolation currently expresses.

Register each new top-level type in `MessageSerializerContext` with `[JsonSerializable(typeof(...))]`, following the file's own instruction comment at `:13`.

- [ ] **Step 4: Replace the interpolation**

In `HandleManagement`, replace both `JsonDocument.Parse($"...")` sites with `JsonSerializer.SerializeToElement(dto, MessageSerializerContext.Default.<Type>)`. Check the exact accessor name the source generator emits for each registered type and use it — do **not** fall back to a reflection-based `JsonSerializer.SerializeToElement(dto)` overload, which would reintroduce the IL2026/IL3050 warnings that issue #89 tracks.

- [ ] **Step 5: Validate `server_id` on ingest**

Add a private predicate that accepts a `server_id` only if it is exactly 43 characters and decodes to `NoiseConstants.KeySize` bytes via `Base64UrlText.Decode`. Apply it where `add-record` reads `server_id` (~`:1420-1440`), answering `invalid_request` and returning **before** anything reaches the store.

Guard clause, not a nested `if` — ohf-sage, Overall layer: *Prefer early guard clauses / returns over nested `if`/`else`*.

- [ ] **Step 6: Add the PSK decode helper and narrow the management catch**

In `SendspinIdentity.cs`, beside `DecodePeerId`, add:

```csharp
    /// <summary>
    /// Decodes a base64url pre-shared key into raw bytes. Distinct from
    /// <see cref="DecodePeerId"/> so a malformed PSK does not report itself as a bad peer id.
    /// </summary>
    internal static byte[] DecodePsk(string encoded)
    {
        byte[] psk = Base64UrlText.Decode(encoded);
        if (psk.Length != NoiseConstants.KeySize)
            throw new FormatException($"PSK must decode to {NoiseConstants.KeySize} bytes");
        return psk;
    }
```

Match `DecodePeerId`'s actual body for the decode-and-length-check shape before writing this; if it validates differently, mirror it rather than inventing a second convention.

Point the two PSK call sites (~`:1353`, ~`:1434`) at it, and narrow the `catch (Exception)` at ~`:1356` to the types the decode throws — the sibling catch at `:1312` already narrows to `JsonException or KeyNotFoundException or FormatException`, which is the in-repo precedent to match.

- [ ] **Step 7: Run the tests, then the full suite, then commit**

Run the filtered tests, then the full suite. Expected: 0 failing, total up by 5.

```bash
git add src/Sendspin.SDK/Protocol/Messages/ManagementData.cs src/Sendspin.SDK/Protocol/MessageSerializerContext.cs src/Sendspin.SDK/Connection/Noise/SendspinIdentity.cs src/Sendspin.SDK/Client/SendSpinClient.cs tests/Sendspin.SDK.Tests/Client/ManagementInputValidationTests.cs
git commit -m "fix(management)!: serialize result payloads and validate server_id on ingest

management/list-records and set-pairing-config built their data payloads by string
interpolation and re-parsed them, and server_id arrived from add-record with no
validation — so a server_id containing a quote or brace injected structure into a
message the client sent. Both payloads now go through the source-generated
serializer, and a server_id is accepted only as a 43-character base64url key.

A dedicated PSK decode helper replaces DecodePeerId on the two PSK paths, so a
malformed PSK no longer reports itself as a bad peer id.

Part of #88."
```

---

### Task 2: Narrow the inbound-path catch-alls (#88 item 2)

**Files:**
- Modify: `src/Sendspin.SDK/Client/SendSpinClient.cs` — `OnTextMessageReceived` (`~:800`), `HandleStreamStartAsync` (`~:2160`), `HandleStreamEndAsync` (`~:2182`), `OnBinaryMessageReceived` (`~:2220`)
- Modify: `src/Sendspin.SDK/Client/SendSpinHostService.cs` — the inner catch in `HandleServerConnectedAsync` (`~:503`)
- Test: `tests/Sendspin.SDK.Tests/Client/ManagementInputValidationTests.cs` (append) or a new file, your choice — say which in the report

**Interfaces:**
- Consumes: `DisconnectAsync(string reason)` on the client; `FakeSendspinConnection.LastDisconnectReason`.
- Produces: nothing later tasks depend on.

- [ ] **Step 1: Write the failing tests**

Two tests, and the second is the one that proves the principle rather than the mechanism:

1. **Malformed authenticated payload closes the connection.** Raise a text message that is well-formed enough to route but whose payload is malformed JSON — e.g. `{"type":"server/state","payload":{` — and assert the connection ends up `Disconnected`. Today it is swallowed and the connection stays up, so this fails first.
2. **A non-payload exception propagates rather than being swallowed.** Subscribe a handler that throws (for instance an event handler on a role event the message triggers), raise a valid message, and assert the exception **escapes** `RaiseTextMessageReceived` rather than being logged and dropped. `Assert.Throws` around the raise is the assertion. This is the ohf-sage MUST — a bug in our own handling must surface, not hide.

If the second test cannot be expressed against the current event surface, say so in the report and explain what you asserted instead — do **not** silently drop it, because it is the half of the finding that is about swallowing rather than about breadth.

- [ ] **Step 2: Run to verify they fail**

Run the filtered tests. Expected: both FAIL — the catch-all swallows both cases today.

- [ ] **Step 3: Narrow the five catches**

At each of the five sites, replace `catch (Exception ex)` with a `when` filter naming only the types a malformed payload produces. Start from the precedent already in the file at `SendSpinClient.cs:1312` — `JsonException or KeyNotFoundException or FormatException` — and verify against each site what its body actually parses; do not copy the list blindly.

On catching one, close the connection. **Do not invent a `client/goodbye` reason** — the spec's list is closed and none denotes a protocol error. Use the reason the surrounding code already uses for an unauthorised/protocol close if one applies, or close without a goodbye; state which you chose and why in the report.

Everything not named in the filter propagates. That is the intent, not an oversight.

- [ ] **Step 4: Expect fallout and fix it correctly**

Narrowing these catches may surface exceptions previously swallowed, breaking tests that relied on a malformed input being ignored. Fix such a test by making its input **valid**, or by asserting the new close — not by widening the catch back. If a test cannot pass without widening it, stop and report: that would mean something legitimate throws on the inbound path, which is a finding.

- [ ] **Step 5: Run the suite and commit**

Expected: 0 failing, total up by 2 (plus any adjustments, which you must account for).

```bash
git add src/Sendspin.SDK/Client/SendSpinClient.cs src/Sendspin.SDK/Client/SendSpinHostService.cs tests/
git commit -m "fix(client)!: stop swallowing malformed authenticated input

Under mandatory encryption every message has been AEAD-authenticated, so a
malformed one means the peer is broken or hostile; logging and continuing left
the connection in an undefined state. The five inbound-message-path catches now
name only the failure types a malformed payload produces and close the
connection, and anything else propagates so a bug in our own handling surfaces.

Part of #88."
```

---

### Task 3: Fragmentation conformance and a phase-aware cap (#88 item 4)

**Files:**
- Modify: `src/Sendspin.SDK/Connection/Noise/NoiseWireFraming.cs` — `HandleTransportFrame`, `HandleFragment`, `DispatchMessage`
- Modify: `src/Sendspin.SDK/Connection/Noise/NoiseConstants.cs` — add the pre-first-message cap
- Test: `tests/Sendspin.SDK.Tests/Connection/FragmentationConformanceTests.cs`

**Interfaces:**
- Consumes: `InboundFrameResult.Fatal(string reason)` — the existing mechanism that closes the socket without an application-level error; `NoiseConstants.MessageTypeFragmentMore` (2), `MessageTypeFragmentEnd` (3), `MessageTypeJsonBody` (0), `MaxReassembledMessageBytes`; `TestNoiseServer` in `tests/Sendspin.SDK.Tests/Connection/TestNoiseServer.cs` — a working server-side `KKpsk2` initiator with frame encryption, which is how a test gets encrypted fragment frames into the framing layer.
- Produces: `NoiseConstants.MaxReassembledMessageBytesBeforeFirstMessage`. Nothing later depends on it.

**The spec text this task implements**, `messaging.md:107` @ `d5f64a6a`:

> **Malformed sequences** are protocol errors; the receiver MUST close the connection. They are: a fragment-end frame received with no fragmented message in flight, a non-fragment frame received while a fragmented message is in flight in the same direction, and an `orig_type` of `2` or `3`.

Rule 1 is already handled. Rules 2 and 3 are not — verified:

- **Rule 2:** `HandleTransportFrame`'s `_ => DispatchMessage(...)` branch dispatches a non-fragment frame with no check on `_reassemblyBuffer`, so the stale buffer survives and the next fragment-more is treated as a continuation of the abandoned message.
- **Rule 3:** `_reassemblyOrigType = plaintext.Span[1]` accepts any byte, and `DispatchMessage(2, …)` falls through to the binary branch, surfacing `[2][payload]` to `BinaryMessageParser` as if it were an application message.

- [ ] **Step 1: Write the failing tests**

Create `tests/Sendspin.SDK.Tests/Connection/FragmentationConformanceTests.cs`. Model the setup on `NoiseWireFramingTests.cs`, which already drives a real handshake via `TestNoiseServer` and then feeds encrypted frames — reuse that pattern rather than constructing frames by hand.

Cover:

1. **Rule 2** — complete a handshake, send a fragment-more frame (opening a reassembly), then send a **non-fragment** frame. Assert the result's `FatalReason` is non-null. Then assert the reassembly did not survive: a subsequent fragment-more must be treated as *opening* a new message, not continuing the abandoned one.
2. **Rule 3, `orig_type` = 2** — open a fragmented message whose `orig_type` byte is `2`. Assert `FatalReason` is non-null and that **nothing was surfaced** (neither `Text` nor `Binary` is set).
3. **Rule 3, `orig_type` = 3** — same, with `3`.
4. **Rule 1 regression guard** — a fragment-end with nothing in flight still yields a `FatalReason`.
5. **The cap before the first application message** — fragment past `MaxReassembledMessageBytesBeforeFirstMessage` before any application message has been surfaced. Assert `FatalReason` is non-null.
6. **Positive control** — after a first application message has been surfaced, a legitimately fragmented message larger than the pre-first-message cap reassembles and dispatches. Without this, a cap regression that rejected *all* fragmentation would pass tests 1-5.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo --filter "FullyQualifiedName~FragmentationConformanceTests"`

Expected: tests 1, 2, 3 and 5 FAIL; test 4 passes already (rule 1 is implemented); test 6 should pass already. Record which failed and how — if test 2 or 3 *passes* before the change, stop and report, because that would mean the gap does not reproduce as the design describes.

- [ ] **Step 3: Close on a non-fragment frame while reassembly is in flight**

In `HandleTransportFrame`, before the `_ =>` dispatch branch, fail when `_reassemblyBuffer is not null`. Keep it a guard, and give the reason text the spec's own wording so a log line is greppable against the spec.

- [ ] **Step 4: Reject `orig_type` of 2 or 3**

In `HandleFragment`, after reading `plaintext.Span[1]` into `_reassemblyOrigType`, fail when it is `MessageTypeFragmentMore` or `MessageTypeFragmentEnd`. Fail **before** allocating the reassembly buffer, so a rejected sequence leaves no state behind.

- [ ] **Step 5: Add the phase-aware cap**

In `NoiseConstants.cs`, beside `MaxReassembledMessageBytes`:

```csharp
    /// <summary>
    /// Tighter bound on reassembly before the framing layer has surfaced its first
    /// application message. The only legitimate content that early is <c>server/hello</c>,
    /// so this is generous while limiting what an unauthorised peer can make us buffer.
    /// The spec sets no maximum; this is local hardening.
    /// </summary>
    public const int MaxReassembledMessageBytesBeforeFirstMessage = 128 * 1024;
```

In `NoiseWireFraming`, add a `private bool _surfacedApplicationMessage;` set to `true` in `DispatchMessage`, cleared in `Reset()` alongside the other per-connection state. Choose the cap from it in `HandleFragment`.

Set the flag where a message is genuinely surfaced to the application. Note `DispatchMessage` also handles the re-handshake path, which is consumed by the framing and never surfaces — a re-handshake must **not** flip the flag. Check `DispatchMessage`'s branches and place the assignment accordingly.

Leave the `noise/handshake` substring sniff in that method alone; it is issue #80.

- [ ] **Step 6: Run the tests, then the full suite, then commit**

Expected: 0 failing, total up by 6 from Task 2's figure.

```bash
git add src/Sendspin.SDK/Connection/Noise/NoiseWireFraming.cs src/Sendspin.SDK/Connection/Noise/NoiseConstants.cs tests/Sendspin.SDK.Tests/Connection/FragmentationConformanceTests.cs
git commit -m "fix(noise): close on the spec's malformed fragment sequences

Two of the three malformed sequences the spec requires closing on were not
implemented. A non-fragment frame arriving mid-reassembly was dispatched
normally and left the stale buffer in place, so the next fragment-more continued
an abandoned message; and an orig_type of 2 or 3 was accepted and surfaced to the
binary parser as though it were an application message. Both are reachable
deliberately by a hostile peer.

Reassembly before the first surfaced application message is now bounded far
tighter than the 64 MiB post-handshake ceiling.

Part of #88."
```

---

## Verification Checklist

- [ ] `git grep -n 'JsonDocument.Parse(\$' src/` returns nothing.
- [ ] `git grep -cn 'catch (Exception' src/Sendspin.SDK/Client/SendSpinClient.cs` is 8 or fewer (was 12; four narrowed).
- [ ] `git grep -n 'catch (Exception' src/Sendspin.SDK/Client/SendSpinHostService.cs` no longer matches the inner `HandleServerConnectedAsync` catch.
- [ ] No new `client/goodbye` reason string appears anywhere in `src/`.
- [ ] All three malformed fragment sequences produce a `FatalReason`.
- [ ] A fragmented message over 128 KiB before the first application message is refused; a larger one after it succeeds.
- [ ] `dotnet test ... -f net10.0` passes with 0 failures.
- [ ] `dotnet build` clean for both `net8.0` and `net10.0`; no new IL2026/IL3050 (the management payloads must go through the source-generated context, not a reflection overload).
- [ ] `<Version>9.1.0</Version>` unchanged.
- [ ] The `noise/handshake` substring sniff in `DispatchMessage` is untouched (#80).
