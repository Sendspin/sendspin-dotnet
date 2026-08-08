# Pairing-config conformance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `management/get-pairing-config` and `management/set-pairing-config` conform to `management.md` — reporting every method the client implements and accepting every field the spec defines as settable — driven from effective SDK state that `client/hello` reads too, so the two surfaces can never disagree again.

**Architecture:** The root cause of [#122](https://github.com/Sendspin/sendspin-dotnet/issues/122) is that the SDK has no notion of a pairing method being *enabled*, only *implemented*: `ClientCapabilities.PinPairingMethods` means "implemented" and `BuildPairMethods` treats it as "enabled". This plan introduces a single private effective-config block on `SendSpinClient`, seeded from capabilities at construction and mutated only by `set-pairing-config`, following the `_unpairedAccessEnabled` pattern established by [#113](https://github.com/Sendspin/sendspin-dotnet/pull/113). `BuildPairMethods`, `CanOffer`, and `get-pairing-config` all read that one block, so the client's advertisement and its management answer are the same value by construction rather than by two code paths agreeing.

**Tech Stack:** C# / .NET (`net8.0;net10.0`), xUnit, `System.Text.Json` source-generated serialization via `MessageSerializerContext`.

## Global Constraints

- Spec source of truth: `Sendspin/spec@main` — `management.md` (pairing config), `pairing.md` (methods, failure counter, record kinds), `messaging.md` (`client/hello`).
- `<Version>9.2.0</Version>` in `src/Sendspin.SDK/Sendspin.SDK.csproj` stays unchanged; [#91](https://github.com/Sendspin/sendspin-dotnet/issues/91) owns the release.
- The SDK MUST NOT write to the consumer-owned `ClientCapabilities` instance. Effective state lives on the client; `PairingConfigChanged` tells the app to persist it. Pinned by `PairingConfigOwnershipTests`.
- Every new serializable DTO must be registered with `[JsonSerializable(...)]` in `src/Sendspin.SDK/Protocol/MessageSerializerContext.cs` or it fails at runtime under source-gen.
- Result codes are exactly the spec's: `ok`, `permission_denied`, `already_exists`, `invalid`, `not_found`, `storage_exhausted`.
- Patch semantics: only fields present in the payload are written; an absent field — including an absent method object — leaves the stored value unchanged (`management.md:98`).
- Test bar carried from the #82 design doc: any test asserting something did *not* happen must survive "would this pass if the machinery producing it were deleted?" Every negative assertion needs a positive control.
- Full suite green on `net10.0`; library builds clean on `net8.0` and `net10.0`.

## Out of scope (filed as follow-ups)

| Deferred | Why |
|---|---|
| Pairing window, `client/pair-pending`, `management/open-pairing-window`, escalation gesture-gating | The SDK has no window state machine at all. It is a second PR of comparable size, and every part of it is behavioural rather than config. See the escalation-deadlock issue. |
| `record_mode` storage-exhaustion fallback (admit the server under the shared-PSK record) | `IPairingRecordStore.Upsert` returns `void` and has no exhaustion signal, so the fallback cannot be implemented without a breaking interface change. |
| `locations?` pair-method descriptor hint | Informational only; no behaviour depends on it. |
| `storage` accounting on `management/result` | Spec makes it optional for clients that cannot bound the pool. SDK stores are app-supplied and unbounded, so omitting the key is conformant. |

---

### Task 1: Effective pairing-config state, read by `client/hello`

Introduce the effective block and make the advertisement read it. Nothing yet mutates it, so this task is pure refactor plus the new "disabled method is omitted" behaviour.

**Files:**
- Modify: `src/Sendspin.SDK/Client/SendSpinClient.cs` (fields near `_unpairedAccessEnabled:83`; constructor ~`:205-232`; `BuildPairMethods:1367`; `CanOffer:1398`)
- Test: `tests/Sendspin.SDK.Tests/Client/PairingConfigOwnershipTests.cs`

**Interfaces:**
- Consumes: `ClientCapabilities.PinPairingMethods`, `.MinPinLength`, `.StaticPin`, `.PinOutChannels`
- Produces: private fields `_pairingPskEnabled`, `_dynamicPinEnabled`, `_staticPinEnabled`, `_effectiveMinPinLength`, `_effectiveStaticPin`; private predicate `bool IsMethodImplemented(string method)`; private predicate `bool IsMethodEnabled(string method)`. Tasks 2–6 all read and write these.

- [ ] **Step 1: Write the failing test**

The observable change in this task is that the descriptor stops carrying `locked_out`, a
field that exists nowhere in the spec. Enablement itself is not yet reachable from the
wire — nothing parses it until Task 3 — so that is what this task's test pins.

```csharp
[Fact]
public void HelloPairMethodDescriptors_CarryOnlySpecDefinedFields()
{
    // `locked_out` appears in no spec file (README/connection/management/messaging/pairing).
    // The descriptor is method, out_channels?, min_pin_length?, locations? (pairing.md:279-283).
    var capabilities = new ClientCapabilities { PinPairingMethods = { "dynamic_pin" }, MinPinLength = 7 };
    var (client, connection, _, _) = CreateManagementClient(capabilities);
    using var _c = client;

    var hello = connection.SentMessages.OfType<ClientHelloMessage>().Last();
    string json = MessageSerializer.Serialize(hello);

    Assert.DoesNotContain("locked_out", json);

    // Positive control: the descriptor is genuinely present and populated, so the
    // assertion above is not passing on an empty supported_pair_methods list.
    var dynamicPin = Assert.Single(
        hello.Payload.SupportedPairMethods!, m => m.Method == "dynamic_pin");
    Assert.Equal(7, dynamicPin.MinPinLength);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj --filter "FullyQualifiedName~HelloPairMethodDescriptors_CarryOnlySpecDefinedFields"`
Expected: FAIL — `Assert.DoesNotContain("locked_out", json)`; `BuildPairMethods` sets `LockedOut` on both PIN descriptors today.

- [ ] **Step 3: Write minimal implementation**

Add beside `_unpairedAccessEnabled` in `SendSpinClient.cs`:

```csharp
// Effective pairing-method configuration: seeded from capabilities at construction and
// updated only by management/set-pairing-config. Held here rather than in the app-owned
// capabilities object, which the SDK does not mutate; PairingConfigChanged tells the app
// to persist the new values. client/hello, CanOffer, and get-pairing-config all read
// these, so the advertisement and the management answer cannot drift apart.
private bool _pairingPskEnabled;
private bool _dynamicPinEnabled;
private bool _staticPinEnabled;
private int _effectiveMinPinLength;
private string? _effectiveStaticPin;
```

Seed them in the constructor, next to `_unpairedAccessEnabled = _capabilities.UnpairedAccessEnabled;`:

```csharp
// Implemented methods start enabled: before this setting existed, "listed in
// PinPairingMethods" was what made a method live, and that must stay the default.
_pairingPskEnabled = true;
_dynamicPinEnabled = _capabilities.PinPairingMethods.Contains("dynamic_pin");
_staticPinEnabled = _capabilities.PinPairingMethods.Contains("static_pin");
_effectiveMinPinLength = _capabilities.MinPinLength;
_effectiveStaticPin = _capabilities.StaticPin;
```

Add the two predicates near `CanOffer`:

```csharp
/// <summary>
/// Whether the app built this client with the method's implementation. Distinct from
/// <see cref="IsMethodEnabled"/>: the spec omits a method object from
/// get-pairing-config only when it is not implemented, while a merely disabled method
/// still reports itself with enabled: false.
/// </summary>
private bool IsMethodImplemented(string method) => method switch
{
    "pairing_psk" => true, // every client implements it (pairing.md:65)
    _ => _capabilities.PinPairingMethods.Contains(method),
};

/// <summary>The method's effective enablement, as set by management/set-pairing-config.</summary>
private bool IsMethodEnabled(string method) => method switch
{
    "pairing_psk" => _pairingPskEnabled,
    "dynamic_pin" => _dynamicPinEnabled,
    "static_pin" => _staticPinEnabled,
    _ => false,
};
```

Rewrite `BuildPairMethods` to gate on enablement and read the effective minimum:

```csharp
private List<PairMethodDescriptor> BuildPairMethods()
{
    var methods = new List<PairMethodDescriptor>();
    if (IsMethodEnabled("pairing_psk"))
    {
        methods.Add(new PairMethodDescriptor { Method = "pairing_psk" });
    }

    if (IsMethodImplemented("dynamic_pin") && IsMethodEnabled("dynamic_pin"))
    {
        methods.Add(new PairMethodDescriptor
        {
            Method = "dynamic_pin",
            OutChannels = _capabilities.PinOutChannels,
            MinPinLength = _effectiveMinPinLength,
        });
    }

    if (IsMethodImplemented("static_pin") && IsMethodEnabled("static_pin"))
    {
        methods.Add(new PairMethodDescriptor { Method = "static_pin" });
    }

    return methods;
}
```

Update `CanOffer`'s two PIN arms to require enablement as well as implementation:

```csharp
"dynamic_pin" => IsMethodImplemented("dynamic_pin") && IsMethodEnabled("dynamic_pin")
                 && _pinLockoutStore is not null
                 && _presentPinAsync is not null,
"static_pin" => IsMethodImplemented("static_pin") && IsMethodEnabled("static_pin")
                && _pinLockoutStore is not null,
```

Replace the two `_capabilities.MinPinLength` reads at `:1518` and any other site with `_effectiveMinPinLength`, and `_capabilities.StaticPin` at `:1559` with `_effectiveStaticPin`.

Note: this step also drops the non-spec `LockedOut` assignment from both descriptors — `locked_out` appears nowhere in the spec (checked across `README.md`, `connection.md`, `management.md`, `messaging.md`, `pairing.md`). Leave the `PairMethodDescriptor.LockedOut` property itself in place; Task 8 removes it once nothing assigns it.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj --filter "FullyQualifiedName~Pairing|FullyQualifiedName~Pin"`
Expected: PASS — the new test plus every existing pairing test. This task is otherwise a pure refactor, so any other failure here is a regression, not expected churn.

- [ ] **Step 5: Commit**

```bash
git add src/Sendspin.SDK/Client/SendSpinClient.cs
git commit -m "refactor: read pairing-method enablement from effective SDK state"
```

---

### Task 2: `get-pairing-config` reports the shape the spec defines

**Files:**
- Modify: `src/Sendspin.SDK/Protocol/Messages/ManagementData.cs:18-28`
- Modify: `src/Sendspin.SDK/Protocol/MessageSerializerContext.cs:41`
- Modify: `src/Sendspin.SDK/Client/SendSpinClient.cs:1908` (the `ManagementGetPairingConfig` case)
- Test: `tests/Sendspin.SDK.Tests/Client/SendspinClientServiceManagementTests.cs`

**Interfaces:**
- Consumes: `IsMethodImplemented`, `IsMethodEnabled`, `_effectiveMinPinLength` (Task 1); `IsPinMethodLockedOut:1677`
- Produces: `PairingConfigData(PairingMethodState PairingPsk, PairingMethodState? StaticPin, DynamicPinConfigState? DynamicPin, RecordModeState RecordMode, PairingMethodState UnpairedAccess)`; `DynamicPinConfigState(bool Enabled, int MinPinLength, bool Escalated)`; `RecordModeState(string? PskId)`

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void GetPairingConfig_ReportsTheImplementedPinMethods_NotAnEmptySurface()
{
    // #122: the client advertised dynamic_pin in client/hello while telling a management
    // server it had no PIN methods at all. Same source of truth, so same answer.
    var capabilities = new ClientCapabilities { PinPairingMethods = { "dynamic_pin" }, MinPinLength = 8 };
    var (client, connection, _, _) = CreateManagementClient(capabilities);
    using var _c = client;

    connection.RaiseTextMessageReceived(
        """{"type":"management/get-pairing-config","payload":{}}""");

    var result = LastResult(connection);
    Assert.Equal("ok", result.Result);
    var data = result.Data!.Value;

    Assert.True(data.GetProperty("pairing_psk").GetProperty("enabled").GetBoolean());
    var dynamicPin = data.GetProperty("dynamic_pin");
    Assert.True(dynamicPin.GetProperty("enabled").GetBoolean());
    Assert.Equal(8, dynamicPin.GetProperty("min_pin_length").GetInt32());
    Assert.False(dynamicPin.GetProperty("escalated").GetBoolean());

    // static_pin is not implemented by this client, so per spec its object is absent —
    // absent for the right reason, which the dynamic_pin clause above proves.
    Assert.False(data.TryGetProperty("static_pin", out _));

    // record_mode is not optional in the spec's data shape.
    Assert.True(data.TryGetProperty("record_mode", out _));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj --filter "FullyQualifiedName~GetPairingConfig_ReportsTheImplementedPinMethods"`
Expected: FAIL — `KeyNotFoundException` on `data.GetProperty("dynamic_pin")`; the current handler emits only `pairing_psk` and `unpaired_access`.

- [ ] **Step 3: Write minimal implementation**

Replace the DTOs in `ManagementData.cs`:

```csharp
/// <summary>
/// Data payload of <c>management/result</c> for <c>management/get-pairing-config</c>.
/// A PIN-method object is absent only when the client does not implement that method;
/// an implemented method that is merely disabled still reports itself with
/// <c>enabled: false</c>.
/// </summary>
internal sealed record PairingConfigData(
    [property: JsonPropertyName("pairing_psk")] PairingMethodState PairingPsk,
    [property: JsonPropertyName("static_pin")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PairingMethodState? StaticPin,
    [property: JsonPropertyName("dynamic_pin")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DynamicPinConfigState? DynamicPin,
    [property: JsonPropertyName("record_mode")] RecordModeState RecordMode,
    [property: JsonPropertyName("unpaired_access")] PairingMethodState UnpairedAccess);

/// <summary>Enablement state of one pairing method.</summary>
internal sealed record PairingMethodState(
    [property: JsonPropertyName("enabled")] bool Enabled);

/// <summary>
/// Dynamic-PIN state. <c>escalated</c> is the spec's name for the failure counter having
/// reached 10 (pairing.md:182) — the method stays offered but every attempt is
/// gesture-gated.
/// </summary>
internal sealed record DynamicPinConfigState(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("min_pin_length")] int MinPinLength,
    [property: JsonPropertyName("escalated")] bool Escalated);

/// <summary>The shared-PSK record used as the storage-exhaustion fallback.</summary>
internal sealed record RecordModeState(
    [property: JsonPropertyName("psk_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PskId);
```

Register the two new records in `MessageSerializerContext.cs` beside `PairingConfigData`:

```csharp
[JsonSerializable(typeof(DynamicPinConfigState))]
[JsonSerializable(typeof(RecordModeState))]
```

Replace the `ManagementGetPairingConfig` case body:

```csharp
result.Data = System.Text.Json.JsonSerializer.SerializeToElement(
    new PairingConfigData(
        new PairingMethodState(_pairingPskEnabled),
        IsMethodImplemented("static_pin") ? new PairingMethodState(_staticPinEnabled) : null,
        IsMethodImplemented("dynamic_pin")
            ? new DynamicPinConfigState(
                _dynamicPinEnabled,
                _effectiveMinPinLength,
                IsPinMethodLockedOut("dynamic_pin"))
            : null,
        new RecordModeState(_recordModePskId),
        new PairingMethodState(_unpairedAccessEnabled)),
    MessageSerializerContext.Default.PairingConfigData);
```

Add the backing field beside the other effective state (Task 7 makes it settable):

```csharp
// record_mode.psk_id: the shared-PSK record admitted as the storage-exhaustion fallback.
// Null until a server sets one; the spec's default is a pre-provisioned shared-PSK
// record, which for an SDK is the app's to provision.
private string? _recordModePskId;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj --filter "FullyQualifiedName~GetPairingConfig"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Sendspin.SDK/Protocol src/Sendspin.SDK/Client/SendSpinClient.cs tests/
git commit -m "fix: report implemented PIN methods from get-pairing-config (#122)"
```

---

### Task 3: `set-pairing-config` accepts `dynamic_pin`

**Files:**
- Modify: `src/Sendspin.SDK/Client/SendSpinClient.cs:1919` (the `ManagementSetPairingConfig` case)
- Test: `tests/Sendspin.SDK.Tests/Client/PairingConfigOwnershipTests.cs`

**Interfaces:**
- Consumes: `IsMethodImplemented`, `_dynamicPinEnabled`, `_effectiveMinPinLength`
- Produces: the parse-then-apply structure later tasks extend; helper `bool TryReadMethodPatch(JsonElement payload, string method, out JsonElement obj)` returning false when the object is absent and setting `result.Result = "invalid"` when the method is not implemented.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void SetDynamicPinMinLength_TakesEffect_AndIsRangeChecked()
{
    var capabilities = new ClientCapabilities { PinPairingMethods = { "dynamic_pin" }, MinPinLength = 6 };
    var (client, connection, _, _) = CreateManagementClient(capabilities);
    using var _c = client;
    var events = new List<PairingConfigChangedEventArgs>();
    client.PairingConfigChanged += (_, e) => events.Add(e);

    connection.RaiseTextMessageReceived(
        """{"type":"management/set-pairing-config","payload":{"dynamic_pin":{"min_pin_length":9}}}""");
    Assert.Equal("ok", LastResult(connection).Result);
    Assert.Single(events);

    // The app's object is untouched; the effective value moved.
    Assert.Equal(6, capabilities.MinPinLength);
    connection.RaiseTextMessageReceived("""{"type":"management/get-pairing-config","payload":{}}""");
    Assert.Equal(9, LastResult(connection).Data!.Value
        .GetProperty("dynamic_pin").GetProperty("min_pin_length").GetInt32());

    // Out of the spec's 4-12 range is invalid, and changes nothing.
    connection.RaiseTextMessageReceived(
        """{"type":"management/set-pairing-config","payload":{"dynamic_pin":{"min_pin_length":13}}}""");
    Assert.Equal("invalid", LastResult(connection).Result);
    connection.RaiseTextMessageReceived("""{"type":"management/get-pairing-config","payload":{}}""");
    Assert.Equal(9, LastResult(connection).Data!.Value
        .GetProperty("dynamic_pin").GetProperty("min_pin_length").GetInt32());
}

[Fact]
public void DisabledDynamicPin_IsOmittedFromTheHelloAdvertisement()
{
    // messaging.md:194 — "An implemented method that is disabled is omitted." This is the
    // end-to-end proof that Task 1's effective state is what the advertisement reads:
    // a set-pairing-config change reaches client/hello without touching capabilities.
    var capabilities = new ClientCapabilities { PinPairingMethods = { "dynamic_pin" } };
    var (client, connection, _, _) = CreateManagementClient(capabilities);
    using var _c = client;

    // Positive control first: while enabled, the method is advertised.
    Assert.Contains(
        connection.SentMessages.OfType<ClientHelloMessage>().Last().Payload.SupportedPairMethods!,
        m => m.Method == "dynamic_pin");

    connection.RaiseTextMessageReceived(
        """{"type":"management/set-pairing-config","payload":{"dynamic_pin":{"enabled":false}}}""");
    Assert.Equal("ok", LastResult(connection).Result);

    // Reconnect so a fresh client/hello is built from the effective state.
    connection.ConnectAsync(ServerUri).GetAwaiter().GetResult();
    connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");

    var afterDisable = connection.SentMessages.OfType<ClientHelloMessage>().Last()
        .Payload.SupportedPairMethods!.Select(m => m.Method).ToList();
    Assert.DoesNotContain("dynamic_pin", afterDisable);
    Assert.Contains("pairing_psk", afterDisable); // the mandatory method survives
    Assert.Equal(["dynamic_pin"], capabilities.PinPairingMethods); // app's object untouched
}

[Fact]
public void SetFieldsOnAnUnimplementedMethod_IsInvalid()
{
    // The one case the old blanket rejection got right, kept as a control so the fix
    // cannot over-correct into accepting configuration for a method that cannot run.
    var capabilities = new ClientCapabilities(); // no PIN methods
    var (client, connection, _, _) = CreateManagementClient(capabilities);
    using var _c = client;

    connection.RaiseTextMessageReceived(
        """{"type":"management/set-pairing-config","payload":{"dynamic_pin":{"enabled":true}}}""");

    Assert.Equal("invalid", LastResult(connection).Result);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj --filter "FullyQualifiedName~SetDynamicPinMinLength"`
Expected: FAIL on the first assertion — result is `invalid`, because the handler rejects every payload carrying a `dynamic_pin` field.

- [ ] **Step 3: Write minimal implementation**

Delete the blanket rejection at `:1923-1927`. In its place, inside the existing parse-before-apply block, add:

```csharp
// dynamic_pin. Parsed here with the other fields so a request refused partway changes
// nothing and the single change event below always describes a fully applied request.
bool? requestedDynamicPinEnabled = null;
int? requestedMinPinLength = null;
if (payload.TryGetProperty("dynamic_pin", out var dp))
{
    if (!IsMethodImplemented("dynamic_pin"))
    {
        result.Result = "invalid";
        break;
    }

    if (dp.TryGetProperty("enabled", out var dpEnabled))
    {
        requestedDynamicPinEnabled = dpEnabled.GetBoolean();
    }

    if (dp.TryGetProperty("min_pin_length", out var minLen))
    {
        int value = minLen.GetInt32();
        if (value < 4 || value > 12)
        {
            result.Result = "invalid";
            break;
        }

        requestedMinPinLength = value;
    }
}
```

And in the apply block:

```csharp
bool dynamicPinChanged = false;
if (requestedDynamicPinEnabled is { } dpe)
{
    dynamicPinChanged |= dpe != _dynamicPinEnabled;
    _dynamicPinEnabled = dpe;
}

if (requestedMinPinLength is { } minPin)
{
    dynamicPinChanged |= minPin != _effectiveMinPinLength;
    _effectiveMinPinLength = minPin;
}
```

Extend the event condition to `if (unpairedAccessChanged || dynamicPinChanged || newPairingPsk is not null)`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj --filter "FullyQualifiedName~PairingConfigOwnershipTests"`
Expected: PASS, including Task 1's hello-omission test.

- [ ] **Step 5: Commit**

```bash
git add src/Sendspin.SDK/Client/SendSpinClient.cs tests/
git commit -m "fix: accept dynamic_pin settings in set-pairing-config (#122)"
```

---

### Task 4: `set-pairing-config` accepts `static_pin`

**Files:**
- Modify: `src/Sendspin.SDK/Client/SendSpinClient.cs` (`ManagementSetPairingConfig` case)
- Test: `tests/Sendspin.SDK.Tests/Client/PairingConfigOwnershipTests.cs`

**Interfaces:**
- Consumes: `IsMethodImplemented`, `_staticPinEnabled`, `_effectiveStaticPin`
- Produces: nothing new; extends the parse/apply blocks from Task 3.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void EnablingStaticPin_WithNoPinConfiguredAndNoneSupplied_IsInvalid()
{
    // management.md:98 names this case explicitly. Enabling a PIN method with no secret
    // behind it would advertise a method that can never authenticate anyone.
    var capabilities = new ClientCapabilities { PinPairingMethods = { "static_pin" } };
    var (client, connection, _, _) = CreateManagementClient(capabilities);
    using var _c = client;

    connection.RaiseTextMessageReceived(
        """{"type":"management/set-pairing-config","payload":{"static_pin":{"enabled":true}}}""");
    Assert.Equal("invalid", LastResult(connection).Result);

    // Positive control: the same enable succeeds when a PIN comes with it.
    connection.RaiseTextMessageReceived(
        """{"type":"management/set-pairing-config","payload":{"static_pin":{"enabled":true,"pin":"01234567"}}}""");
    Assert.Equal("ok", LastResult(connection).Result);
    Assert.Null(capabilities.StaticPin); // rotation did not touch the app's object
}

[Fact]
public void RotatingStaticPin_RejectsAnythingButEightDigits()
{
    var capabilities = new ClientCapabilities { PinPairingMethods = { "static_pin" }, StaticPin = "11111111" };
    var (client, connection, _, _) = CreateManagementClient(capabilities);
    using var _c = client;

    foreach (string bad in new[] { "1234567", "123456789", "1234567a", "" })
    {
        connection.RaiseTextMessageReceived(
            $$"""{"type":"management/set-pairing-config","payload":{"static_pin":{"pin":"{{bad}}"}}}""");
        Assert.Equal("invalid", LastResult(connection).Result);
    }

    connection.RaiseTextMessageReceived(
        """{"type":"management/set-pairing-config","payload":{"static_pin":{"pin":"87654321"}}}""");
    Assert.Equal("ok", LastResult(connection).Result);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj --filter "FullyQualifiedName~StaticPin"`
Expected: FAIL — the second half of the first test gets `invalid` where it expects `ok`, since nothing parses `static_pin` yet.

- [ ] **Step 3: Write minimal implementation**

In the parse block:

```csharp
// static_pin. The spec fixes the static PIN at 8 decimal digits (pairing.md:186) and
// rejects enabling the method with no secret behind it (management.md:98).
bool? requestedStaticPinEnabled = null;
string? requestedStaticPin = null;
if (payload.TryGetProperty("static_pin", out var sp))
{
    if (!IsMethodImplemented("static_pin"))
    {
        result.Result = "invalid";
        break;
    }

    if (sp.TryGetProperty("pin", out var pinEl))
    {
        string pin = pinEl.GetString() ?? string.Empty;
        if (pin.Length != 8 || !pin.All(char.IsAsciiDigit))
        {
            result.Result = "invalid";
            break;
        }

        requestedStaticPin = pin;
    }

    if (sp.TryGetProperty("enabled", out var spEnabled))
    {
        requestedStaticPinEnabled = spEnabled.GetBoolean();
    }

    if (requestedStaticPinEnabled == true
        && requestedStaticPin is null
        && string.IsNullOrEmpty(_effectiveStaticPin))
    {
        result.Result = "invalid";
        break;
    }
}
```

In the apply block:

```csharp
bool staticPinChanged = false;
if (requestedStaticPin is not null)
{
    staticPinChanged |= requestedStaticPin != _effectiveStaticPin;
    _effectiveStaticPin = requestedStaticPin;
}

if (requestedStaticPinEnabled is { } spe)
{
    staticPinChanged |= spe != _staticPinEnabled;
    _staticPinEnabled = spe;
}
```

Add `staticPinChanged` to the event condition.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj --filter "FullyQualifiedName~StaticPin"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Sendspin.SDK/Client/SendSpinClient.cs tests/
git commit -m "fix: accept static_pin enablement and rotation in set-pairing-config (#122)"
```

---

### Task 5: `pairing_psk.enabled`, and its handshake consequence

**Files:**
- Modify: `src/Sendspin.SDK/Client/SendSpinClient.cs` (`ManagementSetPairingConfig` case, `CanOffer:1398`)
- Modify: whichever site assembles handshake PSK candidates from `IPairingRecordStore` — locate with `rg "PskCategory.Pairing" src/Sendspin.SDK/Connection`
- Test: `tests/Sendspin.SDK.Tests/Client/PairingConfigOwnershipTests.cs`

**Interfaces:**
- Consumes: `_pairingPskEnabled`
- Produces: nothing new.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void DisablingPairingPsk_StopsOfferingTheMethod()
{
    // pairing.md:67 — the client keeps its Pairing PSK among handshake candidates
    // "whenever the method is enabled", so disabling it must withdraw both the
    // advertisement and the client's willingness to run the flow.
    var (client, connection, _, _) = CreateManagementClient(new ClientCapabilities());
    using var _c = client;

    connection.RaiseTextMessageReceived(
        """{"type":"management/set-pairing-config","payload":{"pairing_psk":{"enabled":false}}}""");
    Assert.Equal("ok", LastResult(connection).Result);

    connection.RaiseTextMessageReceived("""{"type":"management/get-pairing-config","payload":{}}""");
    Assert.False(LastResult(connection).Data!.Value
        .GetProperty("pairing_psk").GetProperty("enabled").GetBoolean());

    connection.ConnectAsync(ServerUri).GetAwaiter().GetResult();
    connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
    Assert.DoesNotContain(
        connection.SentMessages.OfType<ClientHelloMessage>().Last().Payload.SupportedPairMethods!,
        m => m.Method == "pairing_psk");

    // Positive control: re-enabling brings it back, so the test is not passing on a
    // client that simply never advertises the method.
    connection.RaiseTextMessageReceived(
        """{"type":"management/set-pairing-config","payload":{"pairing_psk":{"enabled":true}}}""");
    connection.ConnectAsync(ServerUri).GetAwaiter().GetResult();
    connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
    Assert.Contains(
        connection.SentMessages.OfType<ClientHelloMessage>().Last().Payload.SupportedPairMethods!,
        m => m.Method == "pairing_psk");
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj --filter "FullyQualifiedName~DisablingPairingPsk"`
Expected: FAIL — `pairing_psk.enabled` is parsed by nothing, so the config reports `true` after the set.

- [ ] **Step 3: Write minimal implementation**

Parse block, extending the existing `pairing_psk` handling that today reads only `psk`:

```csharp
bool? requestedPairingPskEnabled = null;
if (payload.TryGetProperty("pairing_psk", out var ppObj)
    && ppObj.TryGetProperty("enabled", out var ppEnabled))
{
    requestedPairingPskEnabled = ppEnabled.GetBoolean();
}
```

Apply block:

```csharp
bool pairingPskEnabledChanged = false;
if (requestedPairingPskEnabled is { } ppe)
{
    pairingPskEnabledChanged = ppe != _pairingPskEnabled;
    _pairingPskEnabled = ppe;
}
```

Add to the event condition. Add the enablement term to `CanOffer`'s `pairing_psk` arm:

```csharp
"pairing_psk" => _pairingPskEnabled
                 && _session.MatchedPsk?.Category == PskCategory.Pairing
                 && _pairingStore is not null,
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj --filter "FullyQualifiedName~PairingConfigOwnershipTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Sendspin.SDK/Client/SendSpinClient.cs tests/
git commit -m "fix: honour pairing_psk.enabled in set-pairing-config (#122)"
```

---

### Task 6: `pairing_psk.psk` collision returns `already_exists`

**Files:**
- Modify: `src/Sendspin.SDK/Client/SendSpinClient.cs` (`ManagementSetPairingConfig` case)
- Test: `tests/Sendspin.SDK.Tests/Client/PairingConfigOwnershipTests.cs`

**Interfaces:**
- Consumes: `NoiseConstants.DerivePskId`, `NoiseConstants.SentinelPsk`, `_pairingStore`
- Produces: nothing new.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void RotatingThePairingPsk_ToAPskIdInAnotherCategory_IsAlreadyExists()
{
    // management.md:98 — a rotation that collides with the Sentinel PSK or a stored
    // record would make one psk_id resolve to two different trust levels.
    var (client, connection, _, store) = CreateManagementClient(new ClientCapabilities());
    using var _c = client;

    // SessionPsk is already stored as this connection's LongTerm record.
    connection.RaiseTextMessageReceived(
        $$"""{"type":"management/set-pairing-config","payload":{"pairing_psk":{"psk":"{{ToBase64Url(SessionPsk)}}"}}}""");
    Assert.Equal("already_exists", LastResult(connection).Result);

    // And nothing was written: the Pairing record is still absent.
    Assert.DoesNotContain(store.List(), r => r.Category == PskCategory.Pairing);

    // Positive control: an unused PSK rotates successfully.
    var fresh = Enumerable.Repeat((byte)9, 32).ToArray();
    connection.RaiseTextMessageReceived(
        $$"""{"type":"management/set-pairing-config","payload":{"pairing_psk":{"psk":"{{ToBase64Url(fresh)}}"}}}""");
    Assert.Equal("ok", LastResult(connection).Result);
    Assert.Contains(store.List(), r => r.Category == PskCategory.Pairing);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj --filter "FullyQualifiedName~RotatingThePairingPsk_ToAPskIdInAnotherCategory"`
Expected: FAIL — result is `ok`; the handler replaces the Pairing record without checking for a collision.

- [ ] **Step 3: Write minimal implementation**

In the parse block, after decoding `newPairingPsk`:

```csharp
// A psk_id that already identifies a candidate in another category would make one id
// resolve to two trust levels at handshake time.
string newPskId = NoiseConstants.DerivePskId(newPairingPsk);
bool collides = newPskId == NoiseConstants.DerivePskId(NoiseConstants.SentinelPsk.Span);
if (!collides)
{
    lock (_pairingStoreLock)
    {
        collides = _pairingStore!.List()
            .Any(r => r.Category != PskCategory.Pairing && r.PskId == newPskId);
    }
}

if (collides)
{
    result.Result = "already_exists";
    break;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj --filter "FullyQualifiedName~RotatingThePairingPsk"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Sendspin.SDK/Client/SendSpinClient.cs tests/
git commit -m "fix: reject a colliding pairing_psk rotation with already_exists (#122)"
```

---

### Task 7: `record_mode` — set, validate, and protect the reference

**Files:**
- Modify: `src/Sendspin.SDK/Client/SendSpinClient.cs` (`ManagementSetPairingConfig` and `ManagementRemoveRecord:1860` cases)
- Test: `tests/Sendspin.SDK.Tests/Client/SendspinClientServiceManagementTests.cs`

**Interfaces:**
- Consumes: `_recordModePskId` (Task 2), `_pairingStore`
- Produces: private predicate `bool IsSharedPskRecord(PairingRecord record)` — a `LongTerm` record with no `ServerId`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void RecordMode_MustReferenceASharedPskRecord_AndProtectsItFromRemoval()
{
    // management.md:111 — psk_id MUST reference a shared-PSK record, enforced at
    // configuration time, and the referenced record cannot be removed while referenced.
    var (client, connection, _, store) = CreateManagementClient(new ClientCapabilities());
    using var _c = client;

    var shared = Enumerable.Repeat((byte)3, 32).ToArray();
    store.Upsert(new PairingRecord(shared, PskCategory.LongTerm)); // no ServerId => shared
    string sharedId = NoiseConstants.DerivePskId(shared);

    // A stored-pubkey record is not a legal target: SessionPsk is bound to a server_id.
    connection.RaiseTextMessageReceived(
        $$"""{"type":"management/set-pairing-config","payload":{"record_mode":{"psk_id":"{{NoiseConstants.DerivePskId(SessionPsk)}}"}}}""");
    Assert.Equal("invalid", LastResult(connection).Result);

    // Nor is one that does not exist.
    connection.RaiseTextMessageReceived(
        """{"type":"management/set-pairing-config","payload":{"record_mode":{"psk_id":"nope"}}}""");
    Assert.Equal("invalid", LastResult(connection).Result);

    // The shared record is accepted and reported back.
    connection.RaiseTextMessageReceived(
        $$"""{"type":"management/set-pairing-config","payload":{"record_mode":{"psk_id":"{{sharedId}}"}}}""");
    Assert.Equal("ok", LastResult(connection).Result);
    connection.RaiseTextMessageReceived("""{"type":"management/get-pairing-config","payload":{}}""");
    Assert.Equal(sharedId, LastResult(connection).Data!.Value
        .GetProperty("record_mode").GetProperty("psk_id").GetString());

    // And it is now protected from removal.
    connection.RaiseTextMessageReceived(
        $$"""{"type":"management/remove-record","payload":{"psk_id":"{{sharedId}}"}}""");
    Assert.Equal("invalid", LastResult(connection).Result);
    Assert.Contains(store.List(), r => r.PskId == sharedId);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj --filter "FullyQualifiedName~RecordMode_MustReferenceASharedPskRecord"`
Expected: FAIL on the first assertion — `record_mode` is ignored entirely, so the result is `ok`.

- [ ] **Step 3: Write minimal implementation**

Add the predicate near `IsMethodImplemented`:

```csharp
/// <summary>
/// A shared-PSK record: a long-term record with no bound server_id, so the same PSK may
/// authenticate any server holding it. record_mode's fallback target must be one of
/// these (management.md:111).
/// </summary>
private static bool IsSharedPskRecord(PairingRecord record)
    => record.Category == PskCategory.LongTerm && record.ServerId is null;
```

Parse block:

```csharp
string? requestedRecordModePskId = null;
if (payload.TryGetProperty("record_mode", out var rm)
    && rm.TryGetProperty("psk_id", out var rmPskId))
{
    string target = rmPskId.GetString() ?? string.Empty;
    bool valid;
    lock (_pairingStoreLock)
    {
        valid = _pairingStore?.List().Any(r => r.PskId == target && IsSharedPskRecord(r)) == true;
    }

    if (!valid)
    {
        result.Result = "invalid";
        break;
    }

    requestedRecordModePskId = target;
}
```

Apply block:

```csharp
bool recordModeChanged = false;
if (requestedRecordModePskId is not null)
{
    recordModeChanged = requestedRecordModePskId != _recordModePskId;
    _recordModePskId = requestedRecordModePskId;
}
```

Add `recordModeChanged` to the event condition. In the `ManagementRemoveRecord` case, before the store lookup:

```csharp
// A record referenced by record_mode.psk_id cannot be removed while the reference
// exists (management.md:111); both halves of that constraint are rejected as invalid.
if (pskId == _recordModePskId)
{
    result.Result = "invalid";
    break;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj --filter "FullyQualifiedName~RecordMode"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Sendspin.SDK/Client/SendSpinClient.cs tests/
git commit -m "fix: implement record_mode configuration and its referential constraint (#122)"
```

---

### Task 8: Surface the effective config to the app, and drop the non-spec field

**Files:**
- Modify: `src/Sendspin.SDK/Client/PairingConfigChangedEventArgs.cs`
- Modify: `src/Sendspin.SDK/Protocol/Messages/PairingMessages.cs:92-95`
- Modify: `src/Sendspin.SDK/Client/ISendSpinClient.cs:299-307` (event doc)
- Modify: `src/Sendspin.SDK/README.md` (PIN pairing section at `:112`)
- Test: `tests/Sendspin.SDK.Tests/Client/PairingConfigOwnershipTests.cs`

**Interfaces:**
- Consumes: every effective field from Tasks 1–7
- Produces: `PairingConfigChangedEventArgs` gains `PairingPskEnabled`, `DynamicPinEnabled`, `StaticPinEnabled`, `MinPinLength`, `StaticPin`, `RecordModePskId` (all `init`-only).

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void PairingConfigChanged_CarriesEveryEffectiveValue_SoTheAppCanPersistThem()
{
    // The SDK holds effective config in memory only; without the values on the event the
    // app cannot persist them and every restart silently reverts a server's changes.
    var capabilities = new ClientCapabilities { PinPairingMethods = { "dynamic_pin" } };
    var (client, connection, _, _) = CreateManagementClient(capabilities);
    using var _c = client;
    var events = new List<PairingConfigChangedEventArgs>();
    client.PairingConfigChanged += (_, e) => events.Add(e);

    connection.RaiseTextMessageReceived(
        """{"type":"management/set-pairing-config","payload":{"dynamic_pin":{"enabled":false,"min_pin_length":10}}}""");

    var change = Assert.Single(events);
    Assert.False(change.DynamicPinEnabled);
    Assert.Equal(10, change.MinPinLength);
    Assert.True(change.PairingPskEnabled); // untouched fields still report their current value
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj --filter "FullyQualifiedName~PairingConfigChanged_CarriesEveryEffectiveValue"`
Expected: FAIL to compile — `DynamicPinEnabled` is not a member of `PairingConfigChangedEventArgs`.

- [ ] **Step 3: Write minimal implementation**

Add the properties to `PairingConfigChangedEventArgs` with XML docs matching the existing style, then populate them at all three raise sites (`ManagementRemoveRecord:1898`, `ManagementSetPairingConfig:1982`, and any other). Extract a single builder so a future field cannot be added at one site only:

```csharp
/// <summary>
/// Snapshots every effective pairing-config value. One builder for all raise sites, so a
/// field added later cannot reach one event and miss another.
/// </summary>
private PairingConfigChangedEventArgs CurrentPairingConfig(bool pairingPskReplaced) => new()
{
    UnpairedAccessEnabled = _unpairedAccessEnabled,
    PairingPskReplaced = pairingPskReplaced,
    PairingPskEnabled = _pairingPskEnabled,
    DynamicPinEnabled = _dynamicPinEnabled,
    StaticPinEnabled = _staticPinEnabled,
    MinPinLength = _effectiveMinPinLength,
    StaticPin = _effectiveStaticPin,
    RecordModePskId = _recordModePskId,
};
```

Delete `PairMethodDescriptor.LockedOut` (`PairingMessages.cs:92-95`) — Task 1 removed its last assignment and the field appears nowhere in the spec. Update the `PairAbortPayload.Reason` doc comment at `:65-66` to the spec's six reasons, and note in the summary that `locked_out` is not among them (the abort site itself is the escalation follow-up's to fix).

Update `src/Sendspin.SDK/README.md`'s PIN pairing section to document that a paired management server can enable, disable, and configure each method at runtime, and that `PairingConfigChanged` must be persisted.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj`
Expected: PASS, full suite.

- [ ] **Step 5: Commit**

```bash
git add src/ tests/
git commit -m "feat: report effective pairing config on PairingConfigChanged (#122)"
```

---

### Task 9: Full verification

- [ ] **Step 1: Full suite on net10.0**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj -v q --nologo`
Expected: 0 failed.

- [ ] **Step 2: Both target frameworks build clean**

Run: `dotnet build src/Sendspin.SDK/Sendspin.SDK.csproj -c Release --nologo`
Expected: `0 Error(s)`, and the warning count no higher than the pre-change baseline.

- [ ] **Step 3: Version unchanged**

Run: `rg "<Version>" src/Sendspin.SDK/Sendspin.SDK.csproj`
Expected: `<Version>9.2.0</Version>`

- [ ] **Step 4: Spec checklist**

Re-read `management.md:57-113` and confirm each field appears in a task: `pairing_psk.enabled` (T2/T5), `pairing_psk.psk` (T6), `static_pin.enabled`/`.pin` (T2/T4), `dynamic_pin.enabled`/`.min_pin_length`/`.escalated` (T2/T3), `record_mode.psk_id` (T2/T7), `unpaired_access.enabled` (pre-existing), patch semantics (T3–T7 parse-then-apply), unimplemented-method rejection (T3/T4), result codes (T3–T7).
