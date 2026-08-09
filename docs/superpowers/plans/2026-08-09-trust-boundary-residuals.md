# Trust-boundary residuals Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the two remaining places where a security decision is made from state that belongs to a different context — a `psk_id` that resolves to two trust levels ([#130](https://github.com/Sendspin/sendspin-dotnet/issues/130)), and an activate that outlives the session that authorised it ([#100](https://github.com/Sendspin/sendspin-dotnet/issues/100)).

**Architecture:** Both are one-line fixes with a cross-context test each. They are batched because they are the same defect shape and want the same reviewer's mindset: *state from one security context reaching another.* The third member of that family, [#81](https://github.com/Sendspin/sendspin-dotnet/issues/81) (re-handshake key swap racing the send path), was found already fixed in `7f2cf39` with concurrency tests, and needs only to be closed.

**Tech Stack:** C# / .NET (`net8.0;net10.0`), xUnit.

**Branch base:** stacked on `fix/pairing-config-pin-methods` (PR #133), because both fixes touch `SendSpinClient.cs` regions that PR moved. This PR should not merge before #133.

## Global Constraints

- `<Version>9.2.0</Version>` in `src/Sendspin.SDK/Sendspin.SDK.csproj` stays unchanged; #91 owns the release.
- Full suite green on `net10.0`; library builds clean on `net8.0` and `net10.0`.
- Test bar: every negative assertion needs a positive control — "would this pass if the machinery producing it were deleted?" For a security fix the control matters doubly: a guard that refuses *everything* passes every negative test.
- Match the surrounding comment style: substantial prose comments explaining *why*.
- Commit messages must not carry AI-authorship trailers.

---

### Task 1: `management/add-record` must reject the Sentinel PSK (#130)

**Files:**
- Modify: `src/Sendspin.SDK/Client/SendSpinClient.cs` (the `ManagementAddRecord` case, duplicate check at `:1916-1926`)
- Test: `tests/Sendspin.SDK.Tests/Client/SendspinClientServiceManagementTests.cs`

**Interfaces:**
- Consumes: `NoiseConstants.SentinelPskId` (pre-existing, `NoiseConstants.cs:60`, computed once from `SentinelPskBytes`), `NoiseConstants.DerivePskId`
- Produces: nothing other tasks depend on.

**The rule.** `management.md:37`:

> A `psk` whose `psk_id` is already known, whether as a record or as the Sentinel PSK or the client's Pairing PSK (see Pre-Shared Key), is rejected as `already_exists`.

The current check consults records only (`:1918`). Records cover the Pairing PSK incidentally, since it lives in the store as a `PskCategory.Pairing` record. The Sentinel PSK is not covered.

**Why it matters.** `RecordPskResolver.Resolve` (`Connection/Noise/PairingRecordStore.cs:238-249`) walks the record store **first** and only then falls back to `SentinelPskResolver`. A `LongTerm` record holding the published Sentinel PSK therefore shadows Sentinel resolution, and every anonymous peer that knows the well-known constant authenticates at trust `user` — permanently, surviving removal of the planting server's own record.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void AddRecord_CarryingTheSentinelPsk_IsRejected_AndDoesNotShadowSentinelResolution()
{
    // management.md:37 — a psk_id already known as the Sentinel PSK is already_exists.
    // Without this, a LongTerm record holding the published Sentinel constant shadows
    // Sentinel resolution (RecordPskResolver searches records before falling back), so
    // every anonymous peer that knows the constant authenticates at trust 'user'.
    var (client, connection, _, store) = Create();
    using var _c = client;

    string sentinel = ToBase64Url(NoiseConstants.SentinelPsk.ToArray());
    connection.RaiseTextMessageReceived(
        """{"type":"management/add-record","payload":{"psk":"PSK"}}""".Replace("PSK", sentinel));

    Assert.Equal("already_exists", LastResult(connection).Result);

    // Nothing was written: the Sentinel psk_id must not appear in the store at all,
    // which is the property that keeps resolution unambiguous.
    Assert.DoesNotContain(store.List(), r => r.PskId == NoiseConstants.SentinelPskId);
}

[Fact]
public void AddRecord_CarryingAnUnrelatedPsk_StillSucceeds()
{
    // Positive control: a guard that rejected every add would pass the test above.
    var (client, connection, _, store) = Create();
    using var _c = client;

    var fresh = Enumerable.Repeat((byte)0x5A, 32).ToArray();
    connection.RaiseTextMessageReceived(
        """{"type":"management/add-record","payload":{"psk":"PSK"}}""".Replace("PSK", ToBase64Url(fresh)));

    Assert.Equal("ok", LastResult(connection).Result);
    Assert.Contains(store.List(), r => r.PskId == NoiseConstants.DerivePskId(fresh));
}
```

Use the fixture helper already in that file (`Create()`), and its existing base64url and `LastResult` helpers. If a helper is named differently there, use the file's own — do not import one from another test class.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj --filter "FullyQualifiedName~AddRecord_CarryingTheSentinelPsk" --nologo`
Expected: FAIL — result is `ok`, and the store now holds a record whose `PskId` equals `NoiseConstants.SentinelPskId`.

- [ ] **Step 3: Write minimal implementation**

In the `ManagementAddRecord` case, before the store query at `:1916`:

```csharp
// The Sentinel PSK is a published constant, and RecordPskResolver searches records
// before falling back to it — so a record holding it would shadow Sentinel resolution
// and admit every anonymous peer at trust 'user'. The store query below covers records
// (including the Pairing record); this covers the candidate that is not in the store.
if (NoiseConstants.DerivePskId(psk) == NoiseConstants.SentinelPskId)
{
    result.Result = "already_exists";
    break;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj --filter "FullyQualifiedName~AddRecord_Carrying" --nologo`
Expected: PASS, both tests.

- [ ] **Step 5: Commit**

```bash
git add src/Sendspin.SDK/Client/SendSpinClient.cs tests/
git commit -m "fix: reject a management/add-record carrying the Sentinel PSK (#130)"
```

---

### Task 2: an accepted activate must not outlive its session (#100)

**Files:**
- Modify: `src/Sendspin.SDK/Client/SendSpinClient.cs` (`SendHandshakeAsync:304`, beside the existing per-connection resets at `:314-320`)
- Test: `tests/Sendspin.SDK.Tests/Client/SendspinClientServiceManagementTests.cs`

**Interfaces:**
- Consumes: `LastServerActivate` (`:169`), the `_activateReceived` / `_markedPskUsed` reset block
- Produces: nothing other tasks depend on.

**The defect.** `HandleManagement` (`:1837`) gates on `LastServerActivate?.ActivitiesList.Contains(Activities.Management)`. Nothing clears that field on disconnect or on a new handshake. On the **dial path** — an app calling `ConnectAsync` on a long-lived client — a legitimately accepted `activate {activities:["management"]}` survives the reconnect, so in the window between the new handshake completing and the new session's first `server/activate`, `management/*` is permitted **with no admissibility check for the new session**. If the new session is keyed differently (a rotated or downgraded PSK), the old session's permission is honoured for it.

**The fix, and a correction worth preserving.** An earlier fix wave deferred this, reasoning that clearing the field would disturb `SendspinHostService.PriorityOf`, which reads `LastServerActivate` for arbitration. **That reasoning does not hold.** `SendHandshakeAsync` is private and reached only from the dial path (`ConnectAsync:297` and the reconnect handshake at `:467`), so clearing it there touches neither the listen path nor `PriorityOf`. Put that in the code comment — it is the kind of ruling that gets re-litigated.

- [ ] **Step 1: Write the failing test**

The shape matters: it must be **cross-connection**. A same-connection test passes for timing reasons and pins nothing.

```csharp
[Fact]
public async Task ManagementPermittedOnOneConnection_IsNotPermittedOnTheNextBeforeItsOwnActivate()
{
    // A permission decision must be read from the session it was made for. Nothing
    // cleared LastServerActivate on reconnect, so the window between a new handshake
    // completing and that session's first server/activate honoured the PREVIOUS
    // session's grant — even when the new session is keyed differently.
    var (client, connection, _, _) = Create();
    using var _c = client;

    // Positive control first: management is genuinely permitted on this connection.
    connection.RaiseTextMessageReceived(
        """{"type":"management/list-records","payload":{}}""");
    Assert.Equal("ok", LastResult(connection).Result);

    // Reconnect. No server/activate arrives on the new connection.
    connection.SimulateConnectionLoss();
    connection.SimulateReconnected();
    connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");

    connection.RaiseTextMessageReceived(
        """{"type":"management/list-records","payload":{}}""");

    Assert.Equal("permission_denied", LastResult(connection).Result);
}
```

Check how the file's existing tests drive a reconnect before writing this — `SendspinClientServiceSourceTests.ReconnectAfterStart_DoesNotResumeStreaming_UntilTheServerStartsAgain` shows the `SimulateConnectionLoss` / `SimulateReconnected` / `server/hello` sequence. If the reconnect on this fixture does not reach `SendHandshakeAsync`, say so in your report rather than reshaping the test until it passes — that would mean the fix belongs somewhere else, and the controller needs to know.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj --filter "FullyQualifiedName~ManagementPermittedOnOneConnection" --nologo`
Expected: FAIL — the second `management/list-records` answers `ok`, because the previous connection's activate is still in `LastServerActivate`.

- [ ] **Step 3: Write minimal implementation**

In `SendHandshakeAsync`, beside the existing resets:

```csharp
// A new handshake is a new session, and an activate authorises the session it arrived
// on — not the next one. Left standing, it permitted management/* in the window
// between this handshake completing and this session's first server/activate, with no
// admissibility check for the new session's PSK. Cleared here rather than on
// disconnect because SendHandshakeAsync is private to the dial path
// (ConnectAsync and the reconnect handshake), so the listen path's arbitration —
// SendspinHostService.PriorityOf, which also reads LastServerActivate — is untouched.
LastServerActivate = null;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj -v q --nologo`
Expected: PASS, full suite. Run the whole suite here, not the focused filter: `LastServerActivate` is read at `:1837` (management gate) and `:3079` (pairing activity check), and the host service reads it for arbitration, so a regression would surface outside this test.

- [ ] **Step 5: Commit**

```bash
git add src/Sendspin.SDK/Client/SendSpinClient.cs tests/
git commit -m "fix: clear the accepted activate when a new handshake starts (#100)"
```

---

### Task 3: Full verification

- [ ] **Step 1:** `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj -v q --nologo` → 0 failed.
- [ ] **Step 2:** `dotnet build src/Sendspin.SDK/Sendspin.SDK.csproj -c Release --nologo` → `0 Error(s)`.
- [ ] **Step 3:** `rg "<Version>" src/Sendspin.SDK/Sendspin.SDK.csproj` → `9.2.0`.
- [ ] **Step 4:** Confirm both trust-boundary properties hold together: a Sentinel-keyed session still resolves at `PskCategory.Sentinel` after a rejected add-record, and management is refused on a fresh connection until that connection's own activate arrives.
