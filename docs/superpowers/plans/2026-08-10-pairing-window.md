# Pairing Window and Spec #130/#131 Conformance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bring the SDK's pairing flow to spec `5b0e6469` + `21b97460` — the nested `server/activate` pairing object, a device-level pairing window with gesture gating, `client/pair-pending`, `management/open-pairing-window`, escalation replacing the terminal lockout, and the missing attempt timeout.

**Architecture:** Pairing state today lives entirely in the per-connection `SendSpinClient`. The window is device-level, so it becomes a separate thread-safe `PairingWindow` object supplied through `SendspinClientOptions` and shared by every connection a host runs. `SendSpinClient` consults it when an activation's method is gesture-gated, withholding `client/pair-init` and sending `client/pair-pending` until the window opens. The failure counter is unchanged; only what happens at 10 changes — from refusal to gating.

**Tech Stack:** C# 13, .NET 8 / .NET 10, `System.Text.Json` source-generated serialization (AOT-safe via `MessageSerializerContext`), xUnit 2.9.

**Spec:** `Sendspin/spec` at `5b0e6469` (PR #130) and `21b97460` (PR #131).
**Design:** `docs/superpowers/specs/2026-08-10-pairing-window-design.md`
**Issue:** https://github.com/Sendspin/sendspin-dotnet/issues/127

## Global Constraints

- Test project targets **net10.0 only**; the library multi-targets **net8.0;net10.0**. Every API used must exist on both. `TimeProvider` is BCL from net8.0 — safe.
- All new protocol types MUST be registered in `MessageSerializerContext`. This is the mandatory transport path; reflection-based `System.Text.Json` breaks `PublishAot` (#89).
- `TreatWarningsAsErrors` is false, but **introduce no new analyzer warnings in files you touch**. Existing warnings in untouched lines stay.
- Commit messages: no self-references, no AI attribution, no `Co-Authored-By`.
- Never use `Task.Delay` as a test synchronisation primitive for the window; use the fake clock. The attempt timeout is the one exception and uses a short configured timeout plus condition polling.
- Full-suite runs are subject to a known ~3% pre-existing hang in `SendspinHostServiceArbitrationTests` (#143). If a full run hangs, that is not your change — re-run. Verify with `--blame-hang --blame-hang-timeout 120s`.
- Run tests with: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj -f net10.0 --filter "<filter>"`

---

## File Structure

**Create:**
- `src/Sendspin.SDK/Client/PairingWindow.cs` — the shared device-level window. Owns open/close/consume and lifetime expiry. No protocol knowledge.
- `src/Sendspin.SDK/Client/PinPresentation.cs` — context record passed to the PIN presenter (`Pin` + `Languages`).
- `src/Sendspin.SDK/Client/PairingGestureRequestedEventArgs.cs` — event payload when a gated activation is awaiting a gesture.
- `tests/Sendspin.SDK.Tests/Client/PairingWindowTests.cs`
- `tests/Sendspin.SDK.Tests/Client/PairingGatingTests.cs`
- `tests/Sendspin.SDK.Tests/Client/PairingWindowOptionsTests.cs`
- `tests/Sendspin.SDK.Tests/Client/PairingAttemptTimeoutTests.cs`

**Modify:**
- `src/Sendspin.SDK/Protocol/Messages/ServerActivateMessage.cs` — nested `PairingActivation`; delete `SelectedPairMethod`.
- `src/Sendspin.SDK/Protocol/Messages/PairingMessages.cs` — add `ClientPairPendingMessage`/`Payload`; delete `ServerPairInitPayload.PinLength`; fix the `pair/abort` reason doc.
- `src/Sendspin.SDK/Protocol/Messages/MessageTypes.cs` — `ClientPairPending`, `ManagementOpenPairingWindow`.
- `src/Sendspin.SDK/Protocol/MessageSerializerContext.cs` — register the new types.
- `src/Sendspin.SDK/Client/SendSpinClient.cs` — activation parsing/validation, gating, pending state, escalation, attempt timeout, management handler.
- `src/Sendspin.SDK/Client/SendspinClientOptions.cs` — `PairingWindow`, `AttemptTimeout`, presenter signature.
- `src/Sendspin.SDK/Client/ISendSpinClient.cs` — `PairingGestureRequested`.
- `src/Sendspin.SDK/Client/SendSpinHostService.cs` — propagate `PairingWindow` through the per-connection options mirror.
- `tests/Sendspin.SDK.Tests/Client/FakeServer.cs` — emit the nested activation shape.

**Why these boundaries:** `PairingWindow` knows nothing about messages, so it is unit-testable without a connection and safe to share across connections. `PinPresentation` and the event args are separate small files matching the existing one-type-per-file convention in `Client/`.

---

## Task 1: Activation carries a nested `pairing` object

Fixes the live defect: against a current-spec server the flat `selected_pair_method` is never present, so every pairing activation is refused.

**Files:**
- Modify: `src/Sendspin.SDK/Protocol/Messages/ServerActivateMessage.cs:38-43`
- Modify: `src/Sendspin.SDK/Client/SendSpinClient.cs:1615,1622,1630,1654`
- Modify: `tests/Sendspin.SDK.Tests/Client/FakeServer.cs`
- Test: `tests/Sendspin.SDK.Tests/Protocol/MessageSerializerTests.cs`

**Interfaces:**
- Produces: `ServerActivatePayload.Pairing` of type `PairingActivation?` with `string Method`, `int? PinLength`, `List<string>? Languages`. Tasks 2, 3 and 7 read it.

- [ ] **Step 1: Write the failing test**

Add to `tests/Sendspin.SDK.Tests/Protocol/MessageSerializerTests.cs`:

```csharp
[Fact]
public void ServerActivate_DeserializesNestedPairingObject()
{
    // Spec 5b0e6469 replaced the flat selected_pair_method with a pairing object
    // carrying method, pin_length and (spec #131) languages.
    const string json = """
        {"type":"server/activate","payload":{"activities":["pairing"],
        "active_roles":[],"pairing":{"method":"dynamic_pin","pin_length":8,
        "languages":["ca","es"]}}}
        """;

    var msg = MessageSerializer.Deserialize<ServerActivateMessage>(json);

    Assert.NotNull(msg);
    Assert.Equal("dynamic_pin", msg!.Payload.Pairing!.Method);
    Assert.Equal(8, msg.Payload.Pairing.PinLength);
    Assert.Equal(new[] { "ca", "es" }, msg.Payload.Pairing.Languages);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj -f net10.0 --filter "FullyQualifiedName~ServerActivate_DeserializesNestedPairingObject"`
Expected: FAIL — compile error, `ServerActivatePayload` has no member `Pairing`.

- [ ] **Step 3: Replace the flat field**

In `src/Sendspin.SDK/Protocol/Messages/ServerActivateMessage.cs`, delete the `SelectedPairMethod` property and add:

```csharp
    /// <summary>
    /// Parameters of the pairing attempt this activation admits. Present exactly when
    /// 'pairing' is in activities; ignored otherwise.
    /// </summary>
    [JsonPropertyName("pairing")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PairingActivation? Pairing { get; set; }
}

/// <summary>
/// The <c>pairing</c> object on <c>server/activate</c>: which method the server picked and
/// the parameters that method needs.
/// </summary>
public sealed class PairingActivation
{
    /// <summary>Pairing method the server picked: dynamic_pin, pairing_psk, or static_pin.</summary>
    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    /// <summary>
    /// The dynamic PIN length for this session. Required when <see cref="Method"/> is
    /// 'dynamic_pin'; absent otherwise. Validated on receipt of the activation, because the
    /// gesture-gating policy turns on it before client/pair-init is sent.
    /// </summary>
    [JsonPropertyName("pin_length")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PinLength { get; set; }

    /// <summary>
    /// BCP 47 language tags in descending operator preference, for spoken PIN emission.
    /// Informational: never grounds for pair/abort.
    /// </summary>
    [JsonPropertyName("languages")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Languages { get; set; }
}
```

- [ ] **Step 4: Update the four call sites**

In `src/Sendspin.SDK/Client/SendSpinClient.cs`, replace every `payload.SelectedPairMethod` with `payload.Pairing?.Method`:

- `:1615` → `if (!CanOffer(payload.Pairing?.Method))`
- `:1622` → `payload.Pairing?.Method);`
- `:1630` → `switch (payload.Pairing?.Method)`
- `:1654` → `$"CanOffer admitted pair method '{payload.Pairing?.Method}' with no dispatch arm");`

- [ ] **Step 5: Update the test server**

In `tests/Sendspin.SDK.Tests/Client/FakeServer.cs`, find where it builds a pairing `server/activate` and change the flat `selected_pair_method` key to the nested form:

```csharp
["pairing"] = new Dictionary<string, object> { ["method"] = method },
```

If the fake also drives dynamic PIN, include `["pin_length"] = pinLength` in that same dictionary.

- [ ] **Step 6: Run the pairing suites to verify they pass**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj -f net10.0 --filter "FullyQualifiedName~Pairing|FullyQualifiedName~MessageSerializer"`
Expected: PASS

- [ ] **Step 7: Commit**

```bash
git add src/Sendspin.SDK/Protocol/Messages/ServerActivateMessage.cs src/Sendspin.SDK/Client/SendSpinClient.cs tests/Sendspin.SDK.Tests/Client/FakeServer.cs tests/Sendspin.SDK.Tests/Protocol/MessageSerializerTests.cs
git commit -m "fix: read the activation's nested pairing object (#127)

Spec 5b0e6469 replaced server/activate's flat selected_pair_method with a
pairing object carrying method, pin_length and languages. The flat field is
never present on a current-spec server, so CanOffer saw a null method and every
pairing activation was refused with method_not_supported -- all three methods,
not only the PIN flows. The interop gate pins a server predating the change, so
nothing caught it."
```

---

## Task 2: Validate `pin_length` at the activation

**Files:**
- Modify: `src/Sendspin.SDK/Client/SendSpinClient.cs:1606-1656` (`HandlePairingActivate`), `:1692-1717` (`HandleServerPairInit`)
- Modify: `src/Sendspin.SDK/Protocol/Messages/PairingMessages.cs:127-136`
- Test: `tests/Sendspin.SDK.Tests/Client/PairingGatingTests.cs` (create)

**Interfaces:**
- Consumes: `ServerActivatePayload.Pairing` (Task 1).
- Produces: `SendSpinClient` field `private int _activationPinLength;` holding the validated length for the current activation. Task 7 reads it for the gating decision.

- [ ] **Step 1: Write the failing tests**

Create `tests/Sendspin.SDK.Tests/Client/PairingGatingTests.cs`:

```csharp
using Sendspin.SDK.Client;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Spec 5b0e6469 moved pin_length out of server/pair-init and into the activation, to be
/// validated on receipt (pairing.md:149). The gesture-gating policy turns on it before
/// client/pair-init is sent, so reading it later is not an option.
/// </summary>
public class PairingGatingTests
{
    [Fact]
    public async Task DynamicPinActivation_WithPinLengthBelowClientMinimum_AbortsAtTheActivation()
    {
        await using var h = await PairingHarness.StartAsync(minPinLength: 6);

        h.SendPairingActivate(method: "dynamic_pin", pinLength: 4);

        var abort = await h.NextMessageAsync<PairAbortMessage>();
        Assert.Equal("pin_length_unacceptable", abort.Payload.Reason);
        // Aborted at the activation: no attempt was ever started.
        Assert.Empty(h.SentOfType<ClientPairInitMessage>());
    }

    [Fact]
    public async Task DynamicPinActivation_WithMissingPinLength_AbortsAtTheActivation()
    {
        await using var h = await PairingHarness.StartAsync(minPinLength: 6);

        h.SendPairingActivate(method: "dynamic_pin", pinLength: null);

        var abort = await h.NextMessageAsync<PairAbortMessage>();
        Assert.Equal("pin_length_unacceptable", abort.Payload.Reason);
    }

    [Fact]
    public async Task UnofferableMethod_WithBadPinLength_ReportsMethodNotSupportedFirst()
    {
        // Ordering is ours, not the spec's: a method the client does not offer is not a
        // PIN-length question. Pinned so the two reasons cannot silently swap.
        await using var h = await PairingHarness.StartAsync(minPinLength: 6, dynamicPin: false);

        h.SendPairingActivate(method: "dynamic_pin", pinLength: 4);

        var abort = await h.NextMessageAsync<PairAbortMessage>();
        Assert.Equal("method_not_supported", abort.Payload.Reason);
    }
}
```

**Build `PairingHarness` first — every later task depends on it.** It is a test helper in this
same file that wires a `SendspinClientService` to a `FakeSendspinConnection` and drives it to
transport mode. Read `tests/Sendspin.SDK.Tests/Client/SendspinClientServicePairingTests.cs`
first and reuse its construction and handshake sequence rather than inventing a second pattern;
only the surface below is new.

It must expose exactly this surface, because Tasks 3, 7, 8 and 9 all call into it:

```csharp
internal sealed class PairingHarness : IAsyncDisposable
{
    // All parameters optional. minPinLength seeds ClientCapabilities.MinPinLength;
    // dynamicPin/staticPin/pairingPsk control which methods are implemented and enabled
    // (dynamicPin defaults to true when minPinLength is supplied, staticPin to true when a
    // PIN string is supplied); lockouts defaults to a fresh InMemoryPinLockoutStore;
    // management controls whether the activation includes the 'management' activity.
    public static Task<PairingHarness> StartAsync(
        int minPinLength = 6,
        bool dynamicPin = true,
        string? staticPin = null,
        bool pairingPsk = false,
        PairingWindow? window = null,
        IPinLockoutStore? lockouts = null,
        TimeSpan? attemptTimeout = null,
        Func<PinPresentation, CancellationToken, ValueTask>? presentPin = null,
        bool management = false);

    public ISendspinClient Client { get; }

    /// <summary>Feeds a pairing server/activate with the nested pairing object.</summary>
    public void SendPairingActivate(string method, int? pinLength = null, IReadOnlyList<string>? languages = null);

    /// <summary>Feeds a bare management request of the given type.</summary>
    public void SendManagement(string type);

    /// <summary>Waits for the next sent message of type T, failing the test on timeout.</summary>
    public Task<T> NextMessageAsync<T>(TimeSpan? timeout = null) where T : class;

    /// <summary>Every message of type T sent so far.</summary>
    public IReadOnlyList<T> SentOfType<T>() where T : class;

    /// <summary>Raw JSON of every message sent so far.</summary>
    public IReadOnlyList<string> AllSentJson();

    /// <summary>Drives a dynamic-PIN attempt as far as the PIN presenter being invoked.</summary>
    public Task CompleteDynamicPinToPresentationAsync();

    /// <summary>Drives a static-PIN attempt through to server/pair-finalize.</summary>
    public Task CompleteStaticPinPairingAsync();
}
```

`NextMessageAsync` must default to a timeout of a few seconds and throw a clear assertion
failure rather than hanging — a hung pairing test is indistinguishable from the pre-existing
#143 flake, which would waste a lot of time.

If `InMemoryPinLockoutStore` does not already exist in the test project, add a minimal one
implementing `IPinLockoutStore` over a `Dictionary<string, int>`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj -f net10.0 --filter "FullyQualifiedName~PairingGatingTests"`
Expected: FAIL — the client currently sends `client/pair-init` and only checks `pin_length` when `server/pair-init` arrives.

- [ ] **Step 3: Validate at the activation**

In `SendSpinClient.cs`, add the field near the other pairing fields:

```csharp
    // pin_length from the current pairing activation, validated on receipt. 0 when the
    // activation is not dynamic_pin. The gating policy reads it before client/pair-init.
    private int _activationPinLength;
```

In `HandlePairingActivate`, immediately after the `CanOffer` block and before the `switch`:

```csharp
        _activationPinLength = 0;
        if (payload.Pairing?.Method == "dynamic_pin")
        {
            // Validated here, not at server/pair-init: the spec moved pin_length into the
            // activation (pairing.md:149) precisely because the gating decision needs it
            // before client/pair-init is sent.
            int? length = payload.Pairing.PinLength;
            if (length is null || length < _effectiveMinPinLength || length > 12)
            {
                _logger.LogWarning(
                    "Activation pin_length {Length} is outside [{Min}, 12]; aborting the attempt",
                    length, _effectiveMinPinLength);
                _connection.SendMessageAsync(new PairAbortMessage
                {
                    Payload = new PairAbortPayload { Reason = "pin_length_unacceptable" },
                }).SafeFireAndForget(_logger);
                return;
            }

            _activationPinLength = length.Value;
        }
```

- [ ] **Step 4: Stop reading `pin_length` from `server/pair-init`**

In `HandleServerPairInit`, delete the `pinLength` local and its validation block, and use the
activation's value:

```csharp
        state.NonceA = PinPairing.DecodeB64Url(msg.Payload.NonceA);
        var h = _session.HandshakeHash!.Value.ToArray();
        string pin = PinPairing.DerivePin(h, state.NonceA, state.NonceB!, _activationPinLength);
        state.Pin = pin;
```

In `src/Sendspin.SDK/Protocol/Messages/PairingMessages.cs`, delete the `PinLength` property from
`ServerPairInitPayload`, leaving only `NonceA`.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj -f net10.0 --filter "FullyQualifiedName~PairingGatingTests|FullyQualifiedName~Pairing"`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/Sendspin.SDK/Client/SendSpinClient.cs src/Sendspin.SDK/Protocol/Messages/PairingMessages.cs tests/Sendspin.SDK.Tests/Client/PairingGatingTests.cs
git commit -m "fix: validate pin_length on the activation, not on server/pair-init (#127)

The spec moved pin_length into the activation's pairing object so the client can
validate it on receipt. It has to be known there anyway: the gesture-gating
policy gates a dynamic-PIN attempt when pin_length is below 6, a decision taken
before client/pair-init is sent, whereas server/pair-init arrives after it."
```

---

## Task 3: Surface the `languages` hint to the PIN presenter

**Files:**
- Create: `src/Sendspin.SDK/Client/PinPresentation.cs`
- Modify: `src/Sendspin.SDK/Client/SendspinClientOptions.cs:61-68`
- Modify: `src/Sendspin.SDK/Client/SendSpinClient.cs:1725-1731` and the presenter field declaration
- Test: `tests/Sendspin.SDK.Tests/Client/PairingGatingTests.cs`

**Interfaces:**
- Produces: `public sealed record PinPresentation(string Pin, IReadOnlyList<string>? Languages)`; `SendspinClientOptions.PresentPinAsync` becomes `Func<PinPresentation, CancellationToken, ValueTask>?`.

- [ ] **Step 1: Write the failing test**

Add to `PairingGatingTests.cs`:

```csharp
[Fact]
public async Task DynamicPinPresenter_ReceivesTheActivationLanguages()
{
    // The hint is informational and never grounds for abort, but the spec asks the client
    // to emit in the best-matching language it supports. It cannot do that if the SDK
    // never hands the hint over. Matching itself stays with the app, which alone knows
    // which languages it can speak.
    PinPresentation? seen = null;
    await using var h = await PairingHarness.StartAsync(
        minPinLength: 6,
        presentPin: (p, _) => { seen = p; return ValueTask.CompletedTask; });

    h.SendPairingActivate(method: "dynamic_pin", pinLength: 8, languages: ["ca", "es"]);
    await h.CompleteDynamicPinToPresentationAsync();

    Assert.NotNull(seen);
    Assert.Equal(new[] { "ca", "es" }, seen!.Languages);
    Assert.Equal(8, seen.Pin.Length);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj -f net10.0 --filter "FullyQualifiedName~DynamicPinPresenter_ReceivesTheActivationLanguages"`
Expected: FAIL — compile error, the presenter takes a `string`.

- [ ] **Step 3: Add the context record**

Create `src/Sendspin.SDK/Client/PinPresentation.cs`:

```csharp
namespace Sendspin.SDK.Client;

/// <summary>
/// A dynamic pairing PIN to present to the operator, with the server's language hint.
/// </summary>
/// <param name="Pin">The derived PIN, exactly the session's <c>pin_length</c> digits.</param>
/// <param name="Languages">
/// BCP 47 tags in descending operator preference from the activation, or null when the server
/// sent none. Informational: emitting in another language is never a protocol error. Match
/// with RFC 4647 Lookup against the languages the application can actually speak, falling back
/// to its own default when nothing matches.
/// </param>
public sealed record PinPresentation(string Pin, IReadOnlyList<string>? Languages);
```

- [ ] **Step 4: Change the delegate and thread the value through**

In `SendspinClientOptions.cs` replace the `PresentPinAsync` property type:

```csharp
    public Func<PinPresentation, CancellationToken, ValueTask>? PresentPinAsync { get; init; }
```

In `SendSpinClient.cs`, change the `_presentPinAsync` field to the same type, add a field for the
activation's languages next to `_activationPinLength`:

```csharp
    // languages from the current pairing activation, handed to the PIN presenter. Null when
    // the server sent none.
    private List<string>? _activationLanguages;
```

Set it in `HandlePairingActivate` alongside `_activationPinLength`
(`_activationLanguages = payload.Pairing?.Languages;`), and pass it in `InvokePinPresenterAsync`:

```csharp
    private async Task InvokePinPresenterAsync(string pin, CancellationToken cancellationToken)
    {
        await _presentPinAsync!(new PinPresentation(pin, _activationLanguages), cancellationToken);
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj -f net10.0 --filter "FullyQualifiedName~PairingGatingTests"`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/Sendspin.SDK/Client/PinPresentation.cs src/Sendspin.SDK/Client/SendspinClientOptions.cs src/Sendspin.SDK/Client/SendSpinClient.cs tests/Sendspin.SDK.Tests/Client/PairingGatingTests.cs
git commit -m "feat: hand the activation's language hint to the PIN presenter (#127)

Spec 21b97460 added a languages hint for spoken PIN emission. Parsing and
ignoring it would be conformant, since it is never grounds for abort, but the
client is asked to emit in the best-matching language it supports and cannot do
that with a hint the SDK keeps to itself. Matching stays with the application,
which alone knows which languages it can speak."
```

---

## Task 4: The `PairingWindow` type

**Files:**
- Create: `src/Sendspin.SDK/Client/PairingWindow.cs`
- Test: `tests/Sendspin.SDK.Tests/Client/PairingWindowTests.cs`

**Interfaces:**
- Produces: `PairingWindow` with `bool IsOpen`, `void Open()`, `void Close()`, `event EventHandler? StateChanged`, and `internal bool TryConsume()`. Tasks 5, 7, 8 and 9 use it. `InternalsVisibleTo("Sendspin.SDK.Tests")` is already configured, so tests can call `TryConsume`.

**Design note:** lifetime expiry is evaluated lazily on read rather than by a timer. The window's expiry is only ever observable at the moment something tries to consume it, so a timer would add a thread and a disposal contract for no behavioural difference. This keeps the whole type testable with a clock that only overrides `GetUtcNow`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Sendspin.SDK.Tests/Client/PairingWindowTests.cs`:

```csharp
using Sendspin.SDK.Client;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// The pairing window is device-level, not connection-level: the spec closes it on "drop of
/// the connection carrying its attempt" and calls it a state in which the client has decided
/// to accept ONE attempt. A host running several servers therefore shares one window, and
/// exactly one connection may consume any given opening.
/// </summary>
public class PairingWindowTests
{
    [Fact]
    public void NewWindow_IsClosed()
    {
        Assert.False(new PairingWindow().IsOpen);
    }

    [Fact]
    public void Open_MakesItOpen_AndCloseShutsIt()
    {
        var window = new PairingWindow();

        window.Open();
        Assert.True(window.IsOpen);

        window.Close();
        Assert.False(window.IsOpen);
    }

    [Fact]
    public void TryConsume_SucceedsOnceThenCloses()
    {
        // "The window admits exactly one attempt."
        var window = new PairingWindow();
        window.Open();

        Assert.True(window.TryConsume());
        Assert.False(window.TryConsume());
        Assert.False(window.IsOpen);
    }

    [Fact]
    public void TryConsume_OnAClosedWindow_Fails()
    {
        Assert.False(new PairingWindow().TryConsume());
    }

    [Fact]
    public void Window_ExpiresAfterItsLifetime()
    {
        var clock = new FakeClock();
        var window = new PairingWindow(TimeSpan.FromMinutes(5), clock);
        window.Open();

        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        Assert.False(window.IsOpen);
        Assert.False(window.TryConsume());
    }

    [Fact]
    public void Window_DoesNotExpireEarly()
    {
        var clock = new FakeClock();
        var window = new PairingWindow(TimeSpan.FromMinutes(5), clock);
        window.Open();

        clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));

        Assert.True(window.IsOpen);
        Assert.True(window.TryConsume());
    }

    [Fact]
    public void Reopening_RestartsTheLifetime()
    {
        var clock = new FakeClock();
        var window = new PairingWindow(TimeSpan.FromMinutes(5), clock);
        window.Open();
        clock.Advance(TimeSpan.FromMinutes(4));

        window.Open();
        clock.Advance(TimeSpan.FromMinutes(4));

        Assert.True(window.IsOpen);
    }

    [Fact]
    public void ConcurrentConsumers_ProduceExactlyOneWinner()
    {
        // The multi-server case: two connections race for one opening.
        var window = new PairingWindow();
        window.Open();

        int winners = 0;
        Parallel.For(0, 64, _ =>
        {
            if (window.TryConsume())
                Interlocked.Increment(ref winners);
        });

        Assert.Equal(1, winners);
    }

    [Fact]
    public void StateChanged_FiresOnOpenAndClose()
    {
        var window = new PairingWindow();
        int fired = 0;
        window.StateChanged += (_, _) => Interlocked.Increment(ref fired);

        window.Open();
        window.Close();

        Assert.Equal(2, fired);
    }

    /// <summary>Clock stub: only GetUtcNow matters, since the window expires lazily.</summary>
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj -f net10.0 --filter "FullyQualifiedName~PairingWindowTests"`
Expected: FAIL — compile error, `PairingWindow` does not exist.

- [ ] **Step 3: Implement the window**

Create `src/Sendspin.SDK/Client/PairingWindow.cs`:

```csharp
namespace Sendspin.SDK.Client;

/// <summary>
/// The state in which this client has decided to accept one pairing attempt.
/// </summary>
/// <remarks>
/// <para>
/// The window is a property of the device, not of a connection: the spec closes it on "drop of
/// the connection carrying its attempt", which only makes sense if it outlives any single
/// connection, and it admits exactly one attempt no matter how many servers are connected.
/// Share one instance across every connection a host runs by passing it in
/// <see cref="SendspinClientOptions.PairingWindow"/>.
/// </para>
/// <para>
/// Opened by a deliberate operator gesture on the device — a button press, a reset pinhole, a
/// power-cycle pattern — or by a paired server through <c>management/open-pairing-window</c>.
/// Gestures should be hard to induce remotely.
/// </para>
/// <para>All members are safe to call concurrently.</para>
/// </remarks>
public sealed class PairingWindow
{
    /// <summary>The spec's recommended window lifetime.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(5);

    private readonly TimeSpan _lifetime;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();

    private DateTimeOffset? _openedAt;

    /// <param name="lifetime">
    /// How long an opening lasts before it closes silently. Defaults to
    /// <see cref="DefaultLifetime"/>. Measured from opening until the attempt starts; once
    /// <c>client/pair-init</c> has been sent the attempt timeout governs instead.
    /// </param>
    /// <param name="timeProvider">Clock; defaults to <see cref="TimeProvider.System"/>.</param>
    public PairingWindow(TimeSpan? lifetime = null, TimeProvider? timeProvider = null)
    {
        _lifetime = lifetime ?? DefaultLifetime;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Raised when the window opens or closes. Not raised on silent expiry.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Whether an unexpired opening is available.</summary>
    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return !IsExpiredLocked();
            }
        }
    }

    /// <summary>
    /// Opens the window, or restarts the lifetime of an opening already in progress.
    /// </summary>
    public void Open()
    {
        lock (_gate)
        {
            _openedAt = _timeProvider.GetUtcNow();
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Closes the window. Does not abort an attempt already in progress: once
    /// <c>client/pair-init</c> has been sent the opening is spent and the attempt is bounded by
    /// its own timeout.
    /// </summary>
    public void Close()
    {
        lock (_gate)
        {
            _openedAt = null;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Claims the current opening for one attempt, closing the window. Returns false when no
    /// unexpired opening is available. At most one caller can succeed per opening, which is
    /// what makes concurrent connections resolve to a single winner.
    /// </summary>
    internal bool TryConsume()
    {
        lock (_gate)
        {
            if (IsExpiredLocked())
                return false;

            _openedAt = null;
            return true;
        }
    }

    /// <summary>
    /// Whether there is no usable opening. Expiry is evaluated on read rather than by a timer:
    /// it is only ever observable when something tries to consume the window, so a timer would
    /// buy a background thread and a disposal contract for no behavioural difference.
    /// </summary>
    private bool IsExpiredLocked()
        => _openedAt is not { } openedAt
           || _timeProvider.GetUtcNow() - openedAt > _lifetime;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj -f net10.0 --filter "FullyQualifiedName~PairingWindowTests"`
Expected: PASS (9 tests)

- [ ] **Step 5: Commit**

```bash
git add src/Sendspin.SDK/Client/PairingWindow.cs tests/Sendspin.SDK.Tests/Client/PairingWindowTests.cs
git commit -m "feat: add the device-level pairing window (#127)

The window admits exactly one pairing attempt and is a property of the device
rather than of a connection -- the spec closes it on drop of the connection
carrying its attempt, which only makes sense if it outlives any one connection.
A host running several servers shares one instance, and TryConsume under a lock
is what makes concurrent connections resolve to a single winner.

Expiry is lazy rather than timer-driven: it is only observable when something
tries to consume the window."
```

---

## Task 5: Supply the window through options, including the host-service mirror

**Files:**
- Modify: `src/Sendspin.SDK/Client/SendspinClientOptions.cs`
- Modify: `src/Sendspin.SDK/Client/SendSpinHostService.cs:447-451`
- Test: `tests/Sendspin.SDK.Tests/Client/PairingWindowOptionsTests.cs` (create)

**Interfaces:**
- Produces: `SendspinClientOptions.PairingWindow` of type `PairingWindow?`.

**Why this is its own task:** `SendspinHostService` builds a fresh `SendspinClientOptions` per connection by hand-copying selected fields. An option missing from that mirror silently never reaches any connection — gating would degrade to "never gated" and every single-connection test would still pass. This is the "one fact in two places" defect shape, so it gets its own test.

- [ ] **Step 1: Write the failing test**

Create `tests/Sendspin.SDK.Tests/Client/PairingWindowOptionsTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Discovery;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// SendspinHostService rebuilds SendspinClientOptions per connection by hand-copying fields.
/// A new option left out of that mirror never reaches any connection, and the failure is
/// silent: gating degrades to "never gated" and single-connection tests still pass. This
/// pins the propagation.
/// </summary>
public class PairingWindowOptionsTests
{
    [Fact]
    public async Task PairingWindow_SetOnHostOptions_ReachesPerConnectionOptions()
    {
        var window = new PairingWindow();
        var host = new SendspinHostService(
            NullLoggerFactory.Instance,
            new SendspinClientOptions
            {
                Identity = SendspinIdentity.Generate(),
                PairingWindow = window,
            },
            listenerOptions: new ListenerOptions { Port = 0 },
            advertiserOptions: new AdvertiserOptions { Enabled = false });

        await using (host)
        {
            Assert.Same(window, host.BuildClientOptionsForTest().PairingWindow);
        }
    }
}
```

`BuildClientOptionsForTest()` does not exist. Rather than adding a test-only method to
production code, extract the existing inline options construction in `SendSpinHostService.cs`
into a private method and expose it via the existing `InternalsVisibleTo`:

```csharp
    internal SendspinClientOptions BuildClientOptionsForTest() => BuildClientOptions();
```

If the existing construction is conditional (it branches on `_options.ClockSynchronizer`), keep
that branching inside `BuildClientOptions()` unchanged — this is a pure extraction.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj -f net10.0 --filter "FullyQualifiedName~PairingWindowOptionsTests"`
Expected: FAIL — compile error, `SendspinClientOptions` has no `PairingWindow`.

- [ ] **Step 3: Add the option**

In `SendspinClientOptions.cs`:

```csharp
    /// <summary>
    /// The device's pairing window, shared by every connection this application runs.
    /// <b>Required</b> to complete any gesture-gated pairing attempt: static PIN always, and
    /// dynamic PIN once the method is escalated or the session's PIN is shorter than 6 digits.
    /// A null window is treated as permanently closed, so gated attempts stay pending — the
    /// fail-closed direction. Open it from the application's operator gesture.
    /// </summary>
    public PairingWindow? PairingWindow { get; init; }
```

- [ ] **Step 4: Extract the mirror and add the field to it**

In `SendSpinHostService.cs`, extract the per-connection options construction at `:447-451` into
`private SendspinClientOptions BuildClientOptions()`, add `PairingWindow = _options.PairingWindow,`
to every branch, add the `BuildClientOptionsForTest` shim, and call `BuildClientOptions()` from
the original site.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj -f net10.0 --filter "FullyQualifiedName~PairingWindowOptionsTests|FullyQualifiedName~Arbitration"`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/Sendspin.SDK/Client/SendspinClientOptions.cs src/Sendspin.SDK/Client/SendSpinHostService.cs tests/Sendspin.SDK.Tests/Client/PairingWindowOptionsTests.cs
git commit -m "feat: supply the pairing window through client options (#127)

The host service rebuilds per-connection options by hand-copying fields, so an
option missing from that mirror silently never arrives: gating would degrade to
never-gated and every single-connection test would still pass. The construction
is extracted to one method and the propagation is pinned by a test."
```

---

## Task 6: The `client/pair-pending` message

**Files:**
- Modify: `src/Sendspin.SDK/Protocol/Messages/MessageTypes.cs:19`
- Modify: `src/Sendspin.SDK/Protocol/Messages/PairingMessages.cs`
- Modify: `src/Sendspin.SDK/Protocol/MessageSerializerContext.cs`
- Test: `tests/Sendspin.SDK.Tests/Protocol/MessageSerializerTests.cs`

**Interfaces:**
- Produces: `ClientPairPendingMessage` with `Payload.PairingIndex` (int). Task 7 sends it.

- [ ] **Step 1: Write the failing test**

Add to `MessageSerializerTests.cs`:

```csharp
[Fact]
public void ClientPairPending_SerializesWithPairingIndex()
{
    var json = MessageSerializer.Serialize(new ClientPairPendingMessage
    {
        Payload = new ClientPairPendingPayload { PairingIndex = 3 },
    });

    Assert.Contains("\"type\":\"client/pair-pending\"", json, StringComparison.Ordinal);
    Assert.Contains("\"pairing_index\":3", json, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj -f net10.0 --filter "FullyQualifiedName~ClientPairPending_SerializesWithPairingIndex"`
Expected: FAIL — compile error, type does not exist.

- [ ] **Step 3: Add the message type constant**

In `MessageTypes.cs`, in the Pairing block:

```csharp
    public const string ClientPairPending = "client/pair-pending";
```

- [ ] **Step 4: Add the message and payload**

In `PairingMessages.cs`:

```csharp
/// <summary>
/// Reports that the selected attempt is gesture-gated and no pairing window is open. Sent
/// immediately on receiving such a pairing <c>server/activate</c>; <c>client/pair-init</c>
/// follows once a window opens. Does not start the attempt or its timeout.
/// </summary>
public sealed class ClientPairPendingMessage : IMessageWithPayload<ClientPairPendingPayload>
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.ClientPairPending;

    [JsonPropertyName("payload")]
    public ClientPairPendingPayload Payload { get; set; } = new();
}

/// <summary>Payload of <c>client/pair-pending</c>.</summary>
public sealed class ClientPairPendingPayload
{
    /// <summary>Number of pairing server/activate messages received since the last Noise handshake.</summary>
    [JsonPropertyName("pairing_index")]
    public int PairingIndex { get; set; }
}
```

While in this file, correct the `PairAbortPayload` doc comment to drop `locked_out`, which
appears nowhere in the spec:

```csharp
    /// <summary>
    /// Abort reason: attempt_timeout, concurrent_attempt, method_not_supported,
    /// pin_length_unacceptable, pin_mismatch, or user_cancelled.
    /// </summary>
```

- [ ] **Step 5: Register for source-generated serialization**

In `MessageSerializerContext.cs`, alongside the other pairing messages:

```csharp
[JsonSerializable(typeof(ClientPairPendingMessage))]
```

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj -f net10.0 --filter "FullyQualifiedName~ClientPairPending_SerializesWithPairingIndex"`
Expected: PASS

- [ ] **Step 7: Commit**

```bash
git add src/Sendspin.SDK/Protocol/Messages/MessageTypes.cs src/Sendspin.SDK/Protocol/Messages/PairingMessages.cs src/Sendspin.SDK/Protocol/MessageSerializerContext.cs tests/Sendspin.SDK.Tests/Protocol/MessageSerializerTests.cs
git commit -m "feat: add the client/pair-pending message (#127)

Signals that a gesture-gated attempt is waiting on a pairing window. It
precedes an attempt without starting one, so no attempt timeout is armed when
it is sent. Also drops locked_out from the pair/abort reason doc: it appears
nowhere in the spec."
```

---

## Task 7: Gating policy, and escalation replacing the terminal lockout

This is the task that fixes the deadlock in #127.

**Files:**
- Create: `src/Sendspin.SDK/Client/PairingGestureRequestedEventArgs.cs`
- Modify: `src/Sendspin.SDK/Client/SendSpinClient.cs:1585-1594` (`CanOffer` comment), `:1606-1690` (`HandlePairingActivate`, `StartPinAttempt`), `:1858-1859` (`IsPinMethodLockedOut`), `:2114`
- Modify: `src/Sendspin.SDK/Client/ISendSpinClient.cs`
- Modify: `src/Sendspin.SDK/Client/SendspinClientOptions.cs` (doc on `PinLockoutStore`)
- Test: `tests/Sendspin.SDK.Tests/Client/PairingGatingTests.cs`

**Interfaces:**
- Consumes: `PairingWindow.TryConsume()` (Task 4), `SendspinClientOptions.PairingWindow` (Task 5), `ClientPairPendingMessage` (Task 6), `_activationPinLength` (Task 2).
- Produces: `ISendspinClient.PairingGestureRequested` event; `SendSpinClient.IsMethodEscalated(string)`.

- [ ] **Step 1: Write the failing tests**

Add to `PairingGatingTests.cs`:

```csharp
[Fact]
public async Task StaticPinActivation_WithNoWindowOpen_SendsPairPendingAndWithholdsInit()
{
    var window = new PairingWindow();
    await using var h = await PairingHarness.StartAsync(staticPin: "12345678", window: window);

    h.SendPairingActivate(method: "static_pin");

    var pending = await h.NextMessageAsync<ClientPairPendingMessage>();
    Assert.Equal(1, pending.Payload.PairingIndex);
    Assert.Empty(h.SentOfType<ClientPairInitMessage>());
}

[Fact]
public async Task StaticPinActivation_WhenTheWindowOpens_SendsPairInit()
{
    var window = new PairingWindow();
    await using var h = await PairingHarness.StartAsync(staticPin: "12345678", window: window);
    h.SendPairingActivate(method: "static_pin");
    await h.NextMessageAsync<ClientPairPendingMessage>();

    window.Open();

    var init = await h.NextMessageAsync<ClientPairInitMessage>();
    Assert.Equal(1, init.Payload.PairingIndex);
    Assert.False(window.IsOpen); // consumed by the attempt
}

[Fact]
public async Task StaticPinActivation_WithAWindowAlreadyOpen_SendsPairInitImmediately()
{
    var window = new PairingWindow();
    window.Open();
    await using var h = await PairingHarness.StartAsync(staticPin: "12345678", window: window);

    h.SendPairingActivate(method: "static_pin");

    await h.NextMessageAsync<ClientPairInitMessage>();
    Assert.Empty(h.SentOfType<ClientPairPendingMessage>());
}

[Fact]
public async Task DynamicPinActivation_WithLongPinAndNoEscalation_IsNotGated()
{
    var window = new PairingWindow();
    await using var h = await PairingHarness.StartAsync(minPinLength: 6, window: window);

    h.SendPairingActivate(method: "dynamic_pin", pinLength: 8);

    await h.NextMessageAsync<ClientPairInitMessage>();
    Assert.Empty(h.SentOfType<ClientPairPendingMessage>());
}

[Theory]
[InlineData(4)]
[InlineData(5)]
public async Task DynamicPinActivation_WithShortPin_IsGated(int pinLength)
{
    // "short PINs are bought with a gesture" -- the boundary is below 6.
    var window = new PairingWindow();
    await using var h = await PairingHarness.StartAsync(minPinLength: 4, window: window);

    h.SendPairingActivate(method: "dynamic_pin", pinLength: pinLength);

    await h.NextMessageAsync<ClientPairPendingMessage>();
    Assert.Empty(h.SentOfType<ClientPairInitMessage>());
}

[Fact]
public async Task DynamicPinActivation_AtSixDigits_IsNotGated()
{
    var window = new PairingWindow();
    await using var h = await PairingHarness.StartAsync(minPinLength: 4, window: window);

    h.SendPairingActivate(method: "dynamic_pin", pinLength: 6);

    await h.NextMessageAsync<ClientPairInitMessage>();
    Assert.Empty(h.SentOfType<ClientPairPendingMessage>());
}

[Fact]
public async Task DynamicPinActivation_WhenEscalated_IsGatedButStillRuns()
{
    // The #127 deadlock. At 10 failures the old code refused the attempt outright with a
    // non-spec pair/abort reason, and the counter could only reset inside an attempt -- so
    // it never could. Escalation gates the attempt instead of refusing it.
    var lockouts = new InMemoryPinLockoutStore();
    lockouts.SetFailures("dynamic_pin", 10);
    var window = new PairingWindow();
    await using var h = await PairingHarness.StartAsync(
        minPinLength: 6, window: window, lockouts: lockouts);

    h.SendPairingActivate(method: "dynamic_pin", pinLength: 8);

    // Gated, not refused: a pending signal and no abort.
    await h.NextMessageAsync<ClientPairPendingMessage>();
    Assert.Empty(h.SentOfType<PairAbortMessage>());

    window.Open();
    await h.NextMessageAsync<ClientPairInitMessage>();
}

[Fact]
public async Task EscalatedMethod_IsStillOffered_AndNeverAbortsWithLockedOut()
{
    // "Escalation is not an error state - the method stays offered."
    var lockouts = new InMemoryPinLockoutStore();
    lockouts.SetFailures("dynamic_pin", 10);
    await using var h = await PairingHarness.StartAsync(
        minPinLength: 6, window: new PairingWindow(), lockouts: lockouts);

    h.SendPairingActivate(method: "dynamic_pin", pinLength: 8);
    await h.NextMessageAsync<ClientPairPendingMessage>();

    Assert.DoesNotContain(h.AllSentJson(), j => j.Contains("locked_out", StringComparison.Ordinal));
}

[Fact]
public async Task PairingPskActivation_IsNeverGated()
{
    await using var h = await PairingHarness.StartAsync(pairingPsk: true, window: new PairingWindow());

    h.SendPairingActivate(method: "pairing_psk");

    await h.NextMessageAsync<ClientPairFinalizeMessage>();
    Assert.Empty(h.SentOfType<ClientPairPendingMessage>());
}

[Fact]
public async Task GatedActivation_RaisesPairingGestureRequestedOnce()
{
    var window = new PairingWindow();
    await using var h = await PairingHarness.StartAsync(staticPin: "12345678", window: window);
    var raised = new List<PairingGestureRequestedEventArgs>();
    h.Client.PairingGestureRequested += (_, e) => raised.Add(e);

    h.SendPairingActivate(method: "static_pin");
    await h.NextMessageAsync<ClientPairPendingMessage>();

    Assert.Single(raised);
    Assert.Equal("static_pin", raised[0].Method);
    Assert.Equal(1, raised[0].PairingIndex);
}
```

`InMemoryPinLockoutStore` may not exist; if not, add a minimal one to the test project
implementing `IPinLockoutStore` with a `Dictionary<string,int>`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj -f net10.0 --filter "FullyQualifiedName~PairingGatingTests"`
Expected: FAIL — no gating exists; the escalation tests fail with a `locked_out` abort.

- [ ] **Step 3: Add the event args**

Create `src/Sendspin.SDK/Client/PairingGestureRequestedEventArgs.cs`:

```csharp
namespace Sendspin.SDK.Client;

/// <summary>
/// Raised when a pairing attempt is gesture-gated and no <see cref="PairingWindow"/> is open,
/// so the application can prompt its operator to perform the gesture.
/// </summary>
/// <remarks>
/// Raised once per gated activation, at the moment <c>client/pair-pending</c> is sent. It is
/// not re-raised when a window opens and is claimed by another connection, so a prompt maps
/// one-to-one with a pending activation.
/// </remarks>
public sealed class PairingGestureRequestedEventArgs : EventArgs
{
    /// <summary>The pairing method awaiting the gesture: static_pin or dynamic_pin.</summary>
    required public string Method { get; init; }

    /// <summary>The activation's pairing index.</summary>
    required public int PairingIndex { get; init; }
}
```

- [ ] **Step 4: Implement gating and remove the lockout refusal**

In `SendSpinClient.cs`:

Add fields near the other pairing state:

```csharp
    private readonly PairingWindow? _pairingWindow;
    // Set when a gated activation is waiting on a window; cleared when the attempt starts or
    // the activation is superseded.
    private string? _pendingGatedMethod;
```

Assign `_pairingWindow = options.PairingWindow;` in the constructor next to the other option
fields, and subscribe once:

```csharp
        if (_pairingWindow is not null)
            _pairingWindow.StateChanged += OnPairingWindowStateChanged;
```

Unsubscribe in the same place the other event handlers are detached (alongside
`_connection.TextMessageReceived -= ...`).

Rename the predicate and give it the escalation meaning:

```csharp
    /// <summary>
    /// Whether the method's failure counter has reached the spec's escalation threshold. An
    /// escalated method stays offered and still runs; every attempt is gesture-gated until a
    /// successful server_kc verification resets the counter.
    /// </summary>
    private bool IsMethodEscalated(string method)
        => (_pinLockoutStore?.GetFailures(method) ?? 0) >= 10;
```

Update the caller at `:2114` to `IsMethodEscalated("dynamic_pin")`.

Add the gating decision and the pending path. In `HandlePairingActivate`, replace the
`case "dynamic_pin":` / `case "static_pin":` arms with:

```csharp
            case "dynamic_pin":
            case "static_pin":
                BeginOrDeferPinAttempt(payload.Pairing!.Method);
                break;
```

and add:

```csharp
    /// <summary>
    /// Starts a PIN attempt, or defers it until an operator gesture opens the pairing window.
    /// </summary>
    /// <remarks>
    /// Gating policy (pairing.md:227-230): static_pin gates every attempt; dynamic_pin gates
    /// only when the method is escalated or the session's PIN is shorter than 6 digits — short
    /// PINs are bought with a gesture. pairing_psk is never gated and does not reach here.
    /// </remarks>
    private void BeginOrDeferPinAttempt(string method)
    {
        bool gated = method == "static_pin"
                     || IsMethodEscalated(method)
                     || _activationPinLength < 6;

        if (gated && _pairingWindow?.TryConsume() != true)
        {
            // Signals the wait without starting the attempt, so no attempt timeout is armed.
            _pendingGatedMethod = method;
            _connection.SendMessageAsync(new ClientPairPendingMessage
            {
                Payload = new ClientPairPendingPayload { PairingIndex = _pairingCounter },
            }).SafeFireAndForget(_logger);

            _logger.LogInformation(
                "PIN pairing ({Method}): awaiting an operator gesture to open the pairing window",
                method);
            PairingGestureRequested?.Invoke(this, new PairingGestureRequestedEventArgs
            {
                Method = method,
                PairingIndex = _pairingCounter,
            });
            return;
        }

        StartPinAttempt(dynamic: method == "dynamic_pin");
    }

    /// <summary>
    /// A window opened while this connection was waiting on a gesture. Exactly one waiting
    /// connection can claim any opening; the losers stay pending and send nothing.
    /// </summary>
    private void OnPairingWindowStateChanged(object? sender, EventArgs e)
    {
        if (_pendingGatedMethod is not { } method)
            return;

        if (_pairingWindow?.TryConsume() != true)
            return;

        _pendingGatedMethod = null;
        StartPinAttempt(dynamic: method == "dynamic_pin");
    }
```

Delete the lockout refusal from `StartPinAttempt` — the whole block from `if (IsPinMethodLockedOut(method))`
through its closing brace, including the `locked_out` abort. Its opening comment about the
operator gesture being "assumed to have occurred" goes too; replace the method's doc with:

```csharp
    /// <summary>
    /// Begins a PIN-pairing attempt by sending client/pair-init. For dynamic PIN it includes
    /// commit_B over a fresh nonce_B. Any gesture gating has already been satisfied by
    /// <see cref="BeginOrDeferPinAttempt"/>, which consumed the pairing window.
    /// </summary>
```

Clear `_pendingGatedMethod` in `ClearPinState()` so a superseded activation does not later
claim a window.

Update the `CanOffer` comment at `:1585-1589`, which currently cites the terminal lockout:

```csharp
        // A PIN method without a lockout store cannot persist the failure counter, so the
        // method could never escalate to gesture-gating and every attempt would stay ungated.
        // Refuse rather than fail open. Dynamic PIN additionally requires a presenter: without
        // PresentPinAsync the derived PIN would reach nobody.
```

Update the `PinLockoutStore` doc in `SendspinClientOptions.cs` for the same reason — it
currently describes "the spec's terminal lockout after 10 failed attempts", which no longer
exists.

Add the event to `ISendSpinClient.cs`:

```csharp
    /// <summary>
    /// Raised when a pairing attempt is gesture-gated and no pairing window is open. Prompt
    /// the operator, then call <see cref="PairingWindow.Open"/> on the window supplied in
    /// <see cref="SendspinClientOptions.PairingWindow"/>.
    /// </summary>
    event EventHandler<PairingGestureRequestedEventArgs>? PairingGestureRequested;
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj -f net10.0 --filter "FullyQualifiedName~PairingGatingTests"`
Expected: PASS

- [ ] **Step 6: Verify no `locked_out` remains**

Run: `git grep -n "locked_out" -- src/` — expect no output.

- [ ] **Step 7: Commit**

```bash
git add src/Sendspin.SDK/Client/PairingGestureRequestedEventArgs.cs src/Sendspin.SDK/Client/SendSpinClient.cs src/Sendspin.SDK/Client/ISendSpinClient.cs src/Sendspin.SDK/Client/SendspinClientOptions.cs tests/Sendspin.SDK.Tests/Client/PairingGatingTests.cs
git commit -m "fix: gate PIN attempts on the pairing window instead of locking out (#127)

The failure counter was a terminal lockout, and a self-locking one: at 10
failures the client refused to start an attempt, while the counter could only
reset on a successful server_kc verification, which happens inside an attempt.
Ten failures disabled the method permanently with no recovery short of clearing
the store out of band.

The spec escalates instead: the method stays offered and still runs, but every
attempt is gesture-gated until a success de-escalates it. static_pin gates every
attempt; dynamic_pin gates when escalated or when the session PIN is shorter
than 6 digits. client/pair-pending signals the wait without starting the attempt.

Also removes the locked_out abort reason, which appears nowhere in the spec."
```

---

## Task 8: Attempt timeout

**Files:**
- Modify: `src/Sendspin.SDK/Client/SendspinClientOptions.cs`
- Modify: `src/Sendspin.SDK/Client/SendSpinClient.cs` (`StartPinAttempt`, `HandlePairingActivate` pairing_psk arm, `ClearPinState`)
- Test: `tests/Sendspin.SDK.Tests/Client/PairingAttemptTimeoutTests.cs` (create)

**Interfaces:**
- Produces: `SendspinClientOptions.PairingAttemptTimeout` (`TimeSpan`, default 2 minutes).

- [ ] **Step 1: Write the failing tests**

Create `tests/Sendspin.SDK.Tests/Client/PairingAttemptTimeoutTests.cs`:

```csharp
using Sendspin.SDK.Client;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// The client bounds each pairing attempt from its first message (pairing.md:26) and aborts
/// with attempt_timeout on expiry. Nothing implemented this before; the pairing window also
/// closes on attempt-timeout expiry, so without it that close condition could never fire.
/// </summary>
public class PairingAttemptTimeoutTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(250);

    [Fact]
    public async Task PinAttempt_ThatStalls_AbortsWithAttemptTimeout()
    {
        var window = new PairingWindow();
        window.Open();
        await using var h = await PairingHarness.StartAsync(
            staticPin: "12345678", window: window, attemptTimeout: Short);

        h.SendPairingActivate(method: "static_pin");
        await h.NextMessageAsync<ClientPairInitMessage>();

        // Server never replies. The attempt must not hang forever.
        var abort = await h.NextMessageAsync<PairAbortMessage>(TimeSpan.FromSeconds(5));
        Assert.Equal("attempt_timeout", abort.Payload.Reason);
    }

    [Fact]
    public async Task PairPending_DoesNotArmTheAttemptTimeout()
    {
        // pair-pending precedes an attempt and does not start it, so a client waiting on a
        // gesture must not abort itself.
        await using var h = await PairingHarness.StartAsync(
            staticPin: "12345678", window: new PairingWindow(), attemptTimeout: Short);

        h.SendPairingActivate(method: "static_pin");
        await h.NextMessageAsync<ClientPairPendingMessage>();

        await Task.Delay(Short + Short);
        Assert.Empty(h.SentOfType<PairAbortMessage>());
    }

    [Fact]
    public async Task CompletedAttempt_DoesNotAbortAfterwards()
    {
        var window = new PairingWindow();
        window.Open();
        await using var h = await PairingHarness.StartAsync(
            staticPin: "12345678", window: window, attemptTimeout: Short);

        h.SendPairingActivate(method: "static_pin");
        await h.CompleteStaticPinPairingAsync();

        await Task.Delay(Short + Short);
        Assert.Empty(h.SentOfType<PairAbortMessage>());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj -f net10.0 --filter "FullyQualifiedName~PairingAttemptTimeoutTests"`
Expected: FAIL — the first test times out waiting for an abort that is never sent.

- [ ] **Step 3: Add the option**

In `SendspinClientOptions.cs`:

```csharp
    /// <summary>
    /// How long a pairing attempt may run before the client aborts it with
    /// <c>attempt_timeout</c>, measured from the attempt's first message. The spec recommends
    /// 2 minutes. Does not apply while a gesture-gated attempt waits on a pairing window:
    /// <c>client/pair-pending</c> precedes an attempt without starting one.
    /// </summary>
    public TimeSpan PairingAttemptTimeout { get; init; } = TimeSpan.FromMinutes(2);
```

- [ ] **Step 4: Arm and disarm the timer**

In `SendSpinClient.cs`, add two fields:

```csharp
    // Bounds the in-flight pairing attempt. Armed by the attempt's first message, disposed by
    // ClearPinState when the attempt ends for any reason.
    private CancellationTokenSource? _attemptTimeoutCts;

    private readonly TimeSpan _attemptTimeout;
```

Add:

```csharp
    /// <summary>
    /// Starts the attempt timeout. Called from the attempt's first message — client/pair-init
    /// for the PIN flows, client/pair-finalize for Pairing PSK.
    /// </summary>
    private void ArmAttemptTimeout()
    {
        _attemptTimeoutCts?.Cancel();
        _attemptTimeoutCts?.Dispose();
        var cts = new CancellationTokenSource();
        _attemptTimeoutCts = cts;

        _ = Task.Delay(_attemptTimeout, cts.Token).ContinueWith(
            t =>
            {
                if (t.IsCanceled)
                    return;

                _logger.LogWarning("Pairing attempt timed out; aborting");
                AbortPin("attempt_timeout");
                _pairingWindow?.Close();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
```

Assign `_attemptTimeout = options.PairingAttemptTimeout;` in the constructor.

Call `ArmAttemptTimeout();` at the end of `StartPinAttempt` (after the send) and in the
`case "pairing_psk":` arm of `HandlePairingActivate` (after its `client/pair-finalize` send).

In `ClearPinState()`, cancel and dispose:

```csharp
        _attemptTimeoutCts?.Cancel();
        _attemptTimeoutCts?.Dispose();
        _attemptTimeoutCts = null;
```

Ensure the successful-finalize path also calls `ClearPinState()` so a completed attempt cannot
abort afterwards.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj -f net10.0 --filter "FullyQualifiedName~PairingAttemptTimeoutTests"`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/Sendspin.SDK/Client/SendspinClientOptions.cs src/Sendspin.SDK/Client/SendSpinClient.cs tests/Sendspin.SDK.Tests/Client/PairingAttemptTimeoutTests.cs
git commit -m "feat: bound pairing attempts with the spec's attempt timeout (#127)

The spec requires the client to bound every attempt from its first message and
abort with attempt_timeout on expiry. Nothing implemented it -- the reason
appeared nowhere in the source. The pairing window also closes on attempt-timeout
expiry, so without this that close condition could never fire.

A gesture-gated attempt waiting on a window is not yet an attempt, so
client/pair-pending does not arm the timer."
```

---

## Task 9: `management/open-pairing-window`

**Files:**
- Modify: `src/Sendspin.SDK/Protocol/Messages/MessageTypes.cs`
- Modify: `src/Sendspin.SDK/Client/SendSpinClient.cs:1198-1204` (dispatch), `:1982+` (`ExecuteManagementOperation`)
- Test: `tests/Sendspin.SDK.Tests/Client/PairingGatingTests.cs`

**Interfaces:**
- Consumes: `PairingWindow.Open()` / `IsOpen` (Task 4).

- [ ] **Step 1: Write the failing tests**

Add to `PairingGatingTests.cs`:

```csharp
[Fact]
public async Task OpenPairingWindow_OnAManagementSession_OpensTheWindow()
{
    var window = new PairingWindow();
    await using var h = await PairingHarness.StartAsync(
        staticPin: "12345678", window: window, management: true);

    h.SendManagement("management/open-pairing-window");

    var result = await h.NextMessageAsync<ManagementResultMessage>();
    Assert.Equal("ok", result.Payload.Result);
    Assert.True(window.IsOpen);
}

[Fact]
public async Task OpenPairingWindow_WhenAlreadyOpen_IsANoOpOk()
{
    var window = new PairingWindow();
    window.Open();
    await using var h = await PairingHarness.StartAsync(
        staticPin: "12345678", window: window, management: true);

    h.SendManagement("management/open-pairing-window");

    var result = await h.NextMessageAsync<ManagementResultMessage>();
    Assert.Equal("ok", result.Payload.Result);
    Assert.True(window.IsOpen);
}

[Fact]
public async Task OpenPairingWindow_WithNoPinMethodEnabled_IsInvalid()
{
    var window = new PairingWindow();
    await using var h = await PairingHarness.StartAsync(
        window: window, management: true); // no PIN methods configured

    h.SendManagement("management/open-pairing-window");

    var result = await h.NextMessageAsync<ManagementResultMessage>();
    Assert.Equal("invalid", result.Payload.Result);
    Assert.False(window.IsOpen);
}

[Fact]
public async Task OpenPairingWindow_OutsideAManagementSession_IsPermissionDenied()
{
    var window = new PairingWindow();
    await using var h = await PairingHarness.StartAsync(
        staticPin: "12345678", window: window, management: false);

    h.SendManagement("management/open-pairing-window");

    var result = await h.NextMessageAsync<ManagementResultMessage>();
    Assert.Equal("permission_denied", result.Payload.Result);
    Assert.False(window.IsOpen);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj -f net10.0 --filter "FullyQualifiedName~OpenPairingWindow"`
Expected: FAIL — the message type is unknown, so no `management/result` is produced.

- [ ] **Step 3: Add the message type**

In `MessageTypes.cs`, in the Management block:

```csharp
    public const string ManagementOpenPairingWindow = "management/open-pairing-window";
```

- [ ] **Step 4: Route it**

In `SendSpinClient.cs` add the case to the dispatch list at `:1198-1204`:

```csharp
                case MessageTypes.ManagementOpenPairingWindow:
```

(placed with the other management cases, all falling through to `HandleManagement`).

- [ ] **Step 5: Implement the operation**

In `ExecuteManagementOperation`, add:

```csharp
            case MessageTypes.ManagementOpenPairingWindow:
            {
                // Opens the window in place of the operator gesture. Rejected when no PIN
                // method is enabled, since there would be nothing for the window to admit.
                bool anyPinMethod = (IsMethodImplemented("static_pin") && IsMethodEnabled("static_pin"))
                                    || (IsMethodImplemented("dynamic_pin") && IsMethodEnabled("dynamic_pin"));
                if (!anyPinMethod || _pairingWindow is null)
                {
                    result.Result = "invalid";
                    break;
                }

                // A no-op ok when a window is already open.
                _pairingWindow.Open();
                break;
            }
```

`permission_denied` needs no new code: `HandleManagement` already rejects requests outside a
management session before reaching this switch. Confirm that by reading it; if the gate is
keyed off an explicit list of message types, add the new type there too.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj -f net10.0 --filter "FullyQualifiedName~OpenPairingWindow"`
Expected: PASS

- [ ] **Step 7: Commit**

```bash
git add src/Sendspin.SDK/Protocol/Messages/MessageTypes.cs src/Sendspin.SDK/Client/SendSpinClient.cs tests/Sendspin.SDK.Tests/Client/PairingGatingTests.cs
git commit -m "feat: implement management/open-pairing-window (#127)

Lets a paired server open the pairing window in place of the operator gesture:
a no-op ok when one is already open, invalid when no PIN method is enabled and
the window would have nothing to admit."
```

---

## Task 10: End-to-end gated flow, and full verification

**Files:**
- Test: `tests/Sendspin.SDK.Tests/Connection/PairingWindowEndToEndTests.cs` (create)

- [ ] **Step 1: Write the end-to-end test**

Create `tests/Sendspin.SDK.Tests/Connection/PairingWindowEndToEndTests.cs`, driving a real
`NoiseWireFraming` + `TestNoiseServer` through the whole gated static-PIN sequence:
activation → `client/pair-pending` → window opens → `client/pair-init` → `server/pair-auth` →
`client/pair-auth` → `server/pair-confirm` → `client/pair-confirm` → `client/pair-finalize` →
`server/pair-finalize`, asserting the pairing record is persisted and the window is closed at
the end.

Model the harness on `tests/Sendspin.SDK.Tests/Connection/RehandshakeConcurrencyTests.cs`,
which already builds a real `IncomingConnection` over a fake `WebSocket` and completes a real
handshake. Reuse `ConnectAndHandshakeAsync`-style setup rather than writing a third pattern.

- [ ] **Step 2: Run it and verify it passes**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj -f net10.0 --filter "FullyQualifiedName~PairingWindowEndToEnd"`
Expected: PASS

- [ ] **Step 3: Run the full suite**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj -f net10.0 --blame-hang --blame-hang-timeout 120s --blame-hang-dump-type none`
Expected: PASS, with a total **higher** than the pre-change count. Note the count — a total that
went *down* means tests stopped being discovered, which does not turn the run red.

If the run hangs in `SendspinHostServiceArbitrationTests`, that is the pre-existing #143 flake;
re-run.

- [ ] **Step 4: Release build both target frameworks**

Run: `dotnet build src/Sendspin.SDK/Sendspin.SDK.csproj -c Release`
Expected: 0 errors, and no new warnings in the files this plan touched.

- [ ] **Step 5: Confirm the spec conformance claims**

Run each and confirm:
- `git grep -n "locked_out" -- src/` → no output
- `git grep -n "selected_pair_method" -- src/` → no output
- `git grep -n "attempt_timeout" -- src/` → at least one hit

- [ ] **Step 6: Commit**

```bash
git add tests/Sendspin.SDK.Tests/Connection/PairingWindowEndToEndTests.cs
git commit -m "test: drive a gated static-PIN pairing end to end (#127)

Covers the whole sequence over a real Noise session: activation, pair-pending,
the window opening, and the PAKE round through to a persisted record."
```

---

## Follow-ups to file (do not implement here)

- Update **#92**: its audit list names spec #122/#125/#126 and predates #130 and #131. Re-pin
  `docs/SPEC-VERSION.md` (#91) to the spec commit this work targets.
- **#90 item 4**: the interop gate pins `aiosendspin[server]==7.0.0`, which predates spec #130,
  and has no PIN-pairing scenario. That combination is why the activation-shape break went
  unnoticed.
- **#129**: `locations?` is still missing from the pair-method descriptor.
