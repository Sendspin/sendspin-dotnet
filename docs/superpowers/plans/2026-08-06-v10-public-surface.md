# v10 Public Surface Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Finalise the SDK's public surface for v10.0.0 — close the pairing-initiation gap, stop lying to consumers about `ClientId` and `ClientRoles`, stop mutating consumer-owned config, and hide the crypto internals.

**Architecture:** Nine independent changes to the public surface of `Sendspin.SDK`, on one branch. The pairing token (Task 1) is a self-contained encoder/decoder that Task 2 consumes; everything else is independent, except that Task 9 (visibility) runs last because making types `internal` breaks anything an earlier task might have written against them.

**Tech Stack:** .NET (`net8.0;net10.0`), xUnit, source-generated `System.Text.Json`, Noise `KKpsk2` over Curve25519.

## Global Constraints

- Target frameworks `net8.0;net10.0`. Nullable enabled; `CS8600;CS8601;CS8602;CS8603;CS8604;CS8618;CS8625` are **errors**.
- Package version stays **`9.1.0`**. #91 owns the bump. Do not touch `<Version>`.
- Commit messages: no AI attribution, no `Co-Authored-By`, no self-reference. Write as the repo owner.
- Full suite: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo`
- The **test project targets `net10.0` only**. `dotnet test ... -f net8.0` fails with NETSDK1005 — that is expected, not a regression. To check `net8.0`, build the library alone: `dotnet build src/Sendspin.SDK/Sendspin.SDK.csproj -f net8.0 --nologo`.
- **Run `dotnet test` in the foreground.** If a run stalls, kill orphaned `dotnet.exe` / `vstest.console.dll` processes — this has bitten this repo repeatedly.
- **Baseline entering Task 1: 402 passing, 0 failing.** Every task reports the absolute count it observes and accounts for the delta.
- `Base64UrlText` (`Connection/Noise/Base64UrlText.cs`) is **`internal`** — usable inside the SDK, not from a public signature.
- `SendspinIdentity.PeerId` is already the base64url public key. It is the `client_id`.
- Standing test bar: any test asserting something did *not* happen must survive **"would this still pass if the machinery producing it were deleted?"** That check has found a real gap in each of the last three slices.

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `src/Sendspin.SDK/Connection/Noise/Pairing/Base32.cs` (create) | RFC 4648 base32 encode/decode, no padding on output | 1 |
| `src/Sendspin.SDK/Connection/Noise/Pairing/PairingToken.cs` (create) | The `SP:`-prefixed token: encode at version 0, lenient decode of 0 and 1 | 1 |
| `src/Sendspin.SDK/Client/ISendspinClient.cs` | Gains `ClientId`, `TrustLevel`, `EnsurePairingPsk`, `RotatePairingPsk`, `PairingConfigChanged` | 2,3,8 |
| `src/Sendspin.SDK/Client/SendSpinClient.cs` | Implements them; stops mutating `_capabilities` | 2,3,8 |
| `src/Sendspin.SDK/Client/SendspinTrustLevel.cs` (create) | The public trust enum | 3 |
| `src/Sendspin.SDK/Client/ClientRoles.cs` | `@v1` suffixes, plus `Source` and `Color` | 4 |
| `src/Sendspin.SDK/Client/ClientCapabilities.cs` | Loses `ClientId` and `EmitPin`; `SourceLineSense` → `SourceSupport` | 5,6,7 |
| `src/Sendspin.SDK/Client/SourceSupport.cs` (create) | `LineSense` + optional `Codec` | 7 |
| `src/Sendspin.SDK/Client/SendspinClientOptions.cs` | Gains `PresentPinAsync` | 6 |
| `src/Sendspin.SDK/Discovery/MdnsServiceAdvertiser.cs` | `AdvertiserOptions.ClientId` → `InstanceName` | 5 |
| `src/Sendspin.SDK/Audio/Source/SourceStreamPipeline.cs` | Encoder codec from config, not the capture format | 7 |
| Six crypto types | `public` → `internal` | 9 |

**Task order is load-bearing.** Task 9 (visibility) is last: making `NoisePsk`, `PskCategory`-adjacent types and `INoiseSessionInfo` internal will break any test an earlier task wrote against them, and doing it once at the end is one sweep instead of nine.

---

### Task 1: The pairing token (#79)

**Files:**
- Create: `src/Sendspin.SDK/Connection/Noise/Pairing/Base32.cs`
- Create: `src/Sendspin.SDK/Connection/Noise/Pairing/PairingToken.cs`
- Test: `tests/Sendspin.SDK.Tests/Connection/Pairing/PairingTokenTests.cs`

**Interfaces:**
- Consumes: nothing. Self-contained.
- Produces: `internal static class Base32 { public static string Encode(ReadOnlySpan<byte>); public static byte[] Decode(string); }` and the public `PairingToken` below. Task 2 calls `PairingToken.Encode`.

**The format**, spec #125 (`d9154c45`, merged 2026-07-29):

```
token   = "SP:" || version || body
payload = client_key (32 bytes) || pairing_psk (32 bytes)
body    = base32(payload) per RFC 4648, '=' padding stripped, every '2' transliterated to '9'
```

A version-0 token is **107 characters**: `"SP:"` (3) + version (1) + body (103).

**Reference vectors — both are KATs, use both.** `client_key = 0x00…0x1f` in both cases:

```
pairing_psk = 0xe0…0xff  (spec #125):
SP:0AAAQEAYEAUDAOCAJBIFQYDIOB4IBCEQTCQKRMFYYDENBWHA5DYP6BYPC4PSOLZXH5DU6V97M5XXO74HR6LZ7J5PW674PT6X37T6757Y

pairing_psk = 0x20…0x3f  (aiosendspin main):
SP:0AAAQEAYEAUDAOCAJBIFQYDIOB4IBCEQTCQKRMFYYDENBWHA5DYPSAIJCEMSCKJRHFAUSUKZMFUXC6MBRGIZTINJWG44DSOR3HQ6T4PY
```

The algorithm was verified by hand before this plan was written: payload bytes `00 01 02 03 04` give bit groups `00000 00000 00000 10000 00100 00000 11000 00100` → `A A A Q E A Y E`, which is the published `SP:0AAAQEAYE…` prefix. Standard RFC 4648 alphabet `A-Z2-7`, no custom table.

- [ ] **Step 1: Write the failing tests**

Create `tests/Sendspin.SDK.Tests/Connection/Pairing/PairingTokenTests.cs`. Cover, at minimum:

1. **KAT 1, encode** — `client_key = 0x00…0x1f`, `psk = 0xe0…0xff` produces the first string above, exactly. Assert the full string and that its length is 107.
2. **KAT 2, encode** — the second vector, same assertions.
3. **KAT 1, decode** — the first string round-trips to the exact 64-byte payload. Assert both halves separately.
4. **KAT 2, decode** — same.
5. **A version-`1` token decodes.** Take KAT 2 and change the version character from `0` to `1`; the payload must come back identical. This is the whole point of the version decision — if this test is absent the branch has silently not delivered it.
6. **A version-`2` token is rejected.**
7. **Lenient decode** — lower-cased, surrounded by whitespace, and with the `SP:` prefix omitted all decode to KAT 2's payload. Three separate assertions, not one combined input, so each leniency rule is independently pinned.
8. **The `9`↔`2` transliteration is real** — assert no encoded token contains `2`, and that a token with `2` in place of `9` still decodes to the same payload.
9. **Length rejection** — a body that base32-decodes to 63 bytes and one that decodes to 65 bytes are both rejected. Pins the exact-length rule rather than "some length check".
10. **Malformed** — empty string, `"SP:"` alone, and a body containing a character outside the base32 alphabet (e.g. `1`, `8`) are all rejected.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo --filter "FullyQualifiedName~PairingTokenTests"`

Expected: all fail to compile (the types do not exist). That is the correct red state here; record it.

- [ ] **Step 3: Implement `Base32`**

RFC 4648, alphabet `ABCDEFGHIJKLMNOPQRSTUVWXYZ234567`. `Encode` emits no `=` padding. `Decode` re-pads internally to a multiple of 8, rejects characters outside the alphabet, and is case-insensitive on input.

Keep it `internal` — it exists for `PairingToken` and nothing else, and a public base32 is surface this SDK does not want to own.

- [ ] **Step 4: Implement `PairingToken`**

```csharp
namespace Sendspin.SDK.Connection.Noise.Pairing;

/// <summary>
/// The Sendspin pairing token: a single string carrying a client's static public key and its
/// Pairing PSK together, for distribution as a QR code or a pasted string. The app renders the
/// QR; the SDK supplies the string verbatim, with no URI wrapper.
/// </summary>
public static class PairingToken
{
    /// <summary>Token version this SDK emits.</summary>
    public const int EmittedVersion = 0;

    /// <summary>
    /// Builds the token for a client key and Pairing PSK, both 32 bytes. The result is 107
    /// characters and contains only QR alphanumeric characters.
    /// </summary>
    public static string Encode(ReadOnlySpan<byte> clientKey, ReadOnlySpan<byte> pairingPsk) { … }

    /// <summary>
    /// Parses a token, tolerating case, surrounding whitespace and a missing <c>SP:</c> prefix.
    /// Accepts versions 0 and 1, which carry an identical payload.
    /// </summary>
    /// <exception cref="FormatException">
    /// The token is malformed, carries an unrecognised version, or does not decode to exactly
    /// 64 bytes.
    /// </exception>
    public static (byte[] ClientKey, byte[] PairingPsk) Decode(string token) { … }
}
```

`Decode` is a guard chain, not nested conditionals — trim, upper-case, strip optional `SP:`, read and check the version, transliterate `9`→`2`, base32-decode, check the length is exactly 64, split. Each rejection throws `FormatException` with a message naming which rule failed; `FormatException` is the type the client's inbound filter already handles.

**Accept versions 0 and 1.** Both carry the same 64-byte payload — the upstream clash is only over which integer each side stamps — so accepting both is what lets this SDK read a token from an `aiosendspin` 7.0.0 server. Reject everything else.

- [ ] **Step 5: Run the tests, then the full suite, then commit**

Expected: 0 failing, total up by however many tests you wrote (at least 10 facts).

```bash
git add src/Sendspin.SDK/Connection/Noise/Pairing/Base32.cs src/Sendspin.SDK/Connection/Noise/Pairing/PairingToken.cs tests/Sendspin.SDK.Tests/Connection/Pairing/PairingTokenTests.cs
git commit -m "feat(pairing): implement the spec pairing token format

Spec #125 standardises a single SP:-prefixed versioned string carrying a
client's static key and its Pairing PSK together, for QR or paste. We
implemented none of it and surfaced the raw PSK instead.

Encoding emits version 0, per spec. Decoding accepts versions 0 and 1: both
carry an identical 64-byte payload, and the shipped aiosendspin release stamps
1 while spec-conformant implementations stamp 0, so accepting both is what lets
this client read a token from either. Both published reference vectors ship as
known-answer tests.

Part of #79."
```

---

### Task 2: Pairing initiation (#85 item 1)

**Files:**
- Modify: `src/Sendspin.SDK/Client/ISendspinClient.cs`
- Modify: `src/Sendspin.SDK/Client/SendSpinClient.cs`
- Test: `tests/Sendspin.SDK.Tests/Client/PairingInitiationTests.cs`

**Interfaces:**
- Consumes: `PairingToken.Encode` (Task 1); `IPairingRecordStore` with `List()` / `Upsert(PairingRecord)` / `Remove(string pskId)`; `PairingRecord(ReadOnlyMemory<byte> Psk, PskCategory Category, string? ServerId = null, bool Used = false)`; `PskCategory.Pairing`; `SendspinIdentity.PublicKey`.
- Produces: `string EnsurePairingPsk()` and `string RotatePairingPsk()` on `ISendspinClient`. Task 8 raises an event when a server replaces the PSK these return.

**The gap.** The client can only *receive* a Pairing PSK, via `management/set-pairing-config` (`SendSpinClient.cs:1450`). There is no way for an app to initiate the pairing method the spec makes mandatory for clients — no generation, no accessor, nothing. This is the largest functional gap in the release.

**Spec #122's lifecycle rules**, each of which is a test:

- generated from a CSPRNG
- persists across reboots
- per-client and long-lived, not per-server
- **not consumed or rotated by a successful pairing** — the record must survive pairing
- the client MUST NOT rotate on its own; only a deliberate local operator action or `management/set-pairing-config` may replace it

- [ ] **Step 1: Write the failing tests**

Create `tests/Sendspin.SDK.Tests/Client/PairingInitiationTests.cs`:

1. **Idempotent** — `EnsurePairingPsk()` twice returns the same token, and the store holds exactly one `PskCategory.Pairing` record.
2. **Persisted, not ephemeral** — after `EnsurePairingPsk()`, a *new* client over the *same* store returns the same token. This is what "persists across reboots" means in a test.
3. **The token embeds the right key** — decode the returned token with `PairingToken.Decode` and assert `ClientKey` equals the identity's `PublicKey`, and `PairingPsk` equals the stored record's PSK.
4. **Pairing does not consume it** — complete a pairing so a `LongTerm` record is written, then assert the `Pairing` record is **still present** and `EnsurePairingPsk()` still returns the same token. Spec #122's most accident-prone rule.
5. **Rotation replaces** — `RotatePairingPsk()` returns a different token, and the store still holds exactly one `Pairing` record (not two).
6. **No store configured** — `EnsurePairingPsk()` throws `InvalidOperationException`. It must not return an ephemeral token that would silently fail to survive a restart.
7. **CSPRNG, not `Random`** — two fresh clients over two fresh stores produce different PSKs. Weak as a randomness test, but it does catch a hard-coded or seeded default, which is the realistic failure.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo --filter "FullyQualifiedName~PairingInitiationTests"`

Expected: all fail to compile. Record it.

- [ ] **Step 3: Add the interface members**

```csharp
    /// <summary>
    /// Returns this client's pairing token, generating and persisting a Pairing PSK if none is
    /// stored. Idempotent: repeated calls return the same token until the PSK is replaced by
    /// <see cref="RotatePairingPsk"/> or by a server's <c>management/set-pairing-config</c>.
    /// Hand the string to your UI to render as a QR code or to display for pasting.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No pairing record store is configured, so a generated PSK could not be persisted.
    /// </exception>
    string EnsurePairingPsk();

    /// <summary>
    /// Replaces this client's Pairing PSK with a freshly generated one and returns the new
    /// token. Any token previously handed out stops being valid. The spec forbids the client
    /// rotating on its own, so this exists to be called only by deliberate operator action.
    /// </summary>
    /// <exception cref="InvalidOperationException">No pairing record store is configured.</exception>
    string RotatePairingPsk();
```

- [ ] **Step 4: Implement**

In `SendSpinClient`, a private helper that resolves the single `Pairing` record and a token builder over it:

- `EnsurePairingPsk()`: throw `InvalidOperationException` if `_pairingStore is null`; find the first `PskCategory.Pairing` record; if absent, generate 32 bytes with `RandomNumberGenerator.GetBytes(32)` and `Upsert` a `new PairingRecord(psk, PskCategory.Pairing)`; return `PairingToken.Encode(_identity.PublicKey.Span, psk)`.
- `RotatePairingPsk()`: same store guard; `Remove` every existing `Pairing` record, then generate and `Upsert` one. Removing *every* one matters — the existing `set-pairing-config` handler at `:1460` already does this, and leaving a second record behind would make `EnsurePairingPsk` non-deterministic.

Match the existing handler's shape rather than inventing a second idiom: it already loops `_pairingStore.List().Where(r => r.Category == PskCategory.Pairing)` and calls `Remove(old.PskId)`.

- [ ] **Step 5: Run the tests, then the full suite, then commit**

```bash
git add src/Sendspin.SDK/Client/ISendspinClient.cs src/Sendspin.SDK/Client/SendSpinClient.cs tests/Sendspin.SDK.Tests/Client/PairingInitiationTests.cs
git commit -m "feat(pairing): let an app initiate Pairing PSK pairing

The client could only ever receive a Pairing PSK, through
management/set-pairing-config. There was no way to generate one, no accessor,
and so no way for an app to initiate the pairing method the spec makes
mandatory for clients.

EnsurePairingPsk generates from a CSPRNG on first call, persists to the record
store, and returns the pairing token; it is idempotent thereafter. Rotation is a
separate, explicitly named operation because the spec forbids the client
rotating on its own, and a successful pairing deliberately leaves the record in
place rather than consuming it.

Closes #85 item 1."
```

---

### Task 3: `TrustLevel` and the real `ClientId` (#85 items 2 and 4, accessor half)

**Files:**
- Create: `src/Sendspin.SDK/Client/SendspinTrustLevel.cs`
- Modify: `src/Sendspin.SDK/Client/ISendspinClient.cs`, `src/Sendspin.SDK/Client/SendSpinClient.cs`
- Test: `tests/Sendspin.SDK.Tests/Client/ClientIdentityAccessorTests.cs`

**Interfaces:**
- Consumes: `_session.MatchedPsk?.Category` (`INoiseSessionInfo`); `SendspinIdentity.PeerId`, which is already the base64url public key.
- Produces: `SendspinTrustLevel TrustLevel { get; }` and `string ClientId { get; }` on `ISendspinClient`. Task 5 deletes `ClientCapabilities.ClientId`, which this replaces.

Both are read-only accessors over state the client already holds. They ship together because they are the same kind of change to the same interface, and a reviewer would accept or reject them as one.

- [ ] **Step 1: Write the failing tests**

1. `ClientId` equals `Base64UrlText.Encode(identity.PublicKey)` for a known identity — i.e. the identity's `PeerId`. Use a fixed key so the expectation is a literal, not a re-computation of the implementation.
2. `TrustLevel` is `None` before any handshake.
3. `TrustLevel` is `Unpaired` on a Sentinel-keyed session.
4. `TrustLevel` is `Pairing` on a Pairing-keyed session.
5. `TrustLevel` is `Paired` on a LongTerm-keyed session.

Tests 2–5 must each set up a *different* matched category and assert a *different* value. A single test that only checks `Paired` would pass against an implementation that returns `Paired` unconditionally.

- [ ] **Step 2: Run to verify they fail**

Run with `--filter "FullyQualifiedName~ClientIdentityAccessorTests"`. Expected: fail to compile.

- [ ] **Step 3: Add the enum**

```csharp
namespace Sendspin.SDK.Client;

/// <summary>
/// How far the current session's peer is trusted. Under the encrypted protocol every session
/// is authenticated and encrypted, so this describes <em>trust</em>, not confidentiality —
/// <see cref="Unpaired"/> does not mean the connection is in the clear.
/// </summary>
public enum SendspinTrustLevel
{
    /// <summary>No session, or the handshake has not completed.</summary>
    None = 0,

    /// <summary>
    /// Authenticated with the published Sentinel PSK. The peer proved nothing beyond knowing a
    /// constant anyone can read, so it is authenticated but untrusted.
    /// </summary>
    Unpaired = 1,

    /// <summary>Authenticated with the bootstrap Pairing PSK; pairing is in progress.</summary>
    Pairing = 2,

    /// <summary>Authenticated with a long-term PSK from a completed pairing.</summary>
    Paired = 3,
}
```

The `Unpaired` doc comment is not decoration. Without it an app author reads the name as "insecure transport" and renders a warning that is wrong.

- [ ] **Step 4: Implement the accessors**

`ClientId => _identity.PeerId;`

`TrustLevel` maps `_session.MatchedPsk?.Category` — `null` → `None`, `Sentinel` → `Unpaired`, `Pairing` → `Pairing`, `LongTerm` → `Paired`. Use a `switch` expression with an explicit arm per category rather than a `default:` that collapses unknown values into `None`; a future category should be a compile-time gap, not a silent downgrade to "untrusted".

- [ ] **Step 5: Run the tests, then the full suite, then commit**

```bash
git add src/Sendspin.SDK/Client/SendspinTrustLevel.cs src/Sendspin.SDK/Client/ISendspinClient.cs src/Sendspin.SDK/Client/SendSpinClient.cs tests/Sendspin.SDK.Tests/Client/ClientIdentityAccessorTests.cs
git commit -m "feat(client): expose TrustLevel and the real client id

Trust was only reachable through the INoiseSessionInfo the caller constructed,
so an app had no supported way to render paired-versus-unpaired. ClientId was
worse: the only property with that name was a settable, platform-named field on
the capabilities object that the wire ignores entirely.

TrustLevel is a new enum rather than the internal PSK category, since that type
is not part of the public surface. Its Unpaired member documents that it means
untrusted rather than unencrypted, because the name invites the wrong reading.

Closes #85 item 2."
```

---

### Task 4: `ClientRoles` constants (#85 item 3)

**Files:**
- Modify: `src/Sendspin.SDK/Client/ClientRoles.cs`
- Test: `tests/Sendspin.SDK.Tests/Client/ClientRolesTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `ClientRoles.{Player,Controller,Metadata,Artwork,Visualizer,Color,Source}`, all `@v1`-suffixed.

The constants are bare (`"player"`) while `ClientCapabilities.Roles` defaults and the wire both use `@v1`, so the constants never matched what they were for. `Source` was promised and never added. `Color` is missing too — the shipped defaults already include `"color@v1"` — which #85 does not mention.

- [ ] **Step 1: Write the failing test**

```csharp
[Theory]
[InlineData("player@v1")]
[InlineData("controller@v1")]
[InlineData("metadata@v1")]
[InlineData("artwork@v1")]
[InlineData("visualizer@v1")]
[InlineData("color@v1")]
[InlineData("source@v1")]
public void EveryRoleConstant_IsTheWireValue(string expected)
{
    var all = typeof(ClientRoles)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToList();

    Assert.Contains(expected, all);
}
```

Plus one test asserting **every** constant on the class ends in `@v1`, so a future bare constant fails rather than sitting unnoticed:

```csharp
[Fact]
public void NoRoleConstant_IsMissingItsVersionSuffix()
{
    var bare = typeof(ClientRoles)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (Name: f.Name, Value: (string)f.GetRawConstantValue()!))
        .Where(x => !x.Value.EndsWith("@v1", StringComparison.Ordinal))
        .ToList();

    Assert.Empty(bare);
}
```

- [ ] **Step 2: Run to verify it fails**

Run with `--filter "FullyQualifiedName~ClientRolesTests"`. Expected: the theory fails for all seven values (the constants are bare, and two do not exist); the suffix test fails listing five bare constants.

- [ ] **Step 3: Update the constants**

Append `@v1` to `Player`, `Controller`, `Metadata`, `Artwork`, `Visualizer`. Add `Color = "color@v1"` and `Source = "source@v1"`, each with a caller-facing doc comment in the file's existing style.

- [ ] **Step 4: Sweep the call sites**

`git grep -n 'ClientRoles\.'` across `src/`, `tests/` and `tools/`. Any site that concatenated a suffix onto a constant, or compared a constant against an `@v1` string, is now doubly-suffixed or newly matching — fix each. If a site becomes redundant (e.g. `ClientRoles.Player + "@v1"`), simplify it rather than leaving the concatenation.

- [ ] **Step 5: Run the tests, then the full suite, then commit**

```bash
git add src/Sendspin.SDK/Client/ClientRoles.cs tests/Sendspin.SDK.Tests/Client/ClientRolesTests.cs
git commit -m "fix(client)!: make the ClientRoles constants the values the wire needs

The constants were bare strings like \"player\" while both the wire and the
ClientCapabilities.Roles defaults use @v1 suffixes, so the constants never
matched what they existed for and anything using them sent a role the server
rejects. Source was promised and never added, and Color was missing even though
the shipped defaults already advertise color@v1.

Closes #85 item 3."
```

---

### Task 5: Remove `ClientCapabilities.ClientId` (#85 item 4, removal half)

**Files:**
- Modify: `src/Sendspin.SDK/Client/ClientCapabilities.cs` (delete the property at `:11-15`)
- Modify: `src/Sendspin.SDK/Discovery/MdnsServiceAdvertiser.cs` (`AdvertiserOptions.ClientId` at `:252-256` → `InstanceName`)
- Modify: `src/Sendspin.SDK/Client/SendspinHostService.cs` (`:46` forwarder, `:215` seed)
- Test: existing tests only; adjust what breaks

**Interfaces:**
- Consumes: `ISendspinClient.ClientId` from Task 3 — the real value, already available before this task runs.
- Produces: `AdvertiserOptions.InstanceName`; `SendspinHostService.InstanceName`.

**Why removal and not read-only.** `CreateClientHelloMessage` already omits the property (`SendSpinClient.cs:298`) because under encryption `client_id` **is** the public key. A read-only property named `ClientId` on a *capabilities* object still implies the app has a say in something it does not.

**Why the advertiser gets renamed rather than repointed.** `connection.md:11-15` specifies a client's mDNS advertisement as service type, port, and the `path` and `name` TXT records. **There is no `client_id` in it.** So this value was never a protocol value — it is the DNS-SD instance label, and `InstanceName` is what it is.

- [ ] **Step 1: Delete the property and let the compiler find the callers**

Remove `ClientCapabilities.ClientId`. Build. Expect breaks at `SendspinHostService.cs:215` and anywhere in `tests/` or `tools/` that sets it.

- [ ] **Step 2: Rename the advertiser option**

`AdvertiserOptions.ClientId` → `InstanceName`, and update its doc comment: it is the DNS-SD service instance label, not a protocol identifier, and the spec does not carry a client id in the advertisement. Keep the same default value — changing what is advertised is not this task's business.

Rename the `MdnsServiceAdvertiser.ClientId` member and `SendspinHostService.ClientId` (`:46`) to match.

- [ ] **Step 3: Reseed the host service**

`SendspinHostService.cs:215` currently seeds `ClientId = _options.Capabilities.ClientId`. The capabilities property is gone. Seed `InstanceName` from `_options.Capabilities.ClientName` — the friendly name is what the spec's `name` TXT record carries, and it is the closest honest source. Do **not** seed it from the identity's public key: a 43-character base64url string as a DNS-SD instance label is hostile to anyone reading `dns-sd` output, and the spec does not ask for it.

- [ ] **Step 4: Fix the fallout**

`git grep -n 'ClientId'` across `src/`, `tests/`, `tools/`. Every remaining hit should be either `ISendspinClient.ClientId` (Task 3's real accessor) or a rename you have already made. Anything else is a leftover.

- [ ] **Step 5: Run the full suite, then commit**

Expected: 0 failing. If a test asserted the old `sendspin-windows-{host}` value, update it to the new source rather than reintroducing the property.

```bash
git add -A src/ tests/ tools/
git commit -m "fix(client)!: remove the misleading ClientId capability

Under encryption client_id IS the Curve25519 public key, and
CreateClientHelloMessage already omits this property — so it was a settable,
platform-named field that the wire ignores, which is worse than absent. The real
value is now a read-only property on the client.

Its only consumer was the mDNS instance label, and the spec's client
advertisement (connection.md) carries no client_id at all: service type, port,
and the path and name TXT records only. So AdvertiserOptions.ClientId was never
a protocol value and is renamed InstanceName, seeded from the friendly name.

Closes #85 item 4."
```

---

### Task 6: PIN presentation becomes an awaited delegate (#85 item 7)

**Files:**
- Modify: `src/Sendspin.SDK/Client/ClientCapabilities.cs` (delete `EmitPin` at `:166-171`; fix the malformed doc comment at `:99`)
- Modify: `src/Sendspin.SDK/Client/SendspinClientOptions.cs` (add `PresentPinAsync`)
- Modify: `src/Sendspin.SDK/Client/SendSpinClient.cs` (`:1177` call site)
- Test: `tests/Sendspin.SDK.Tests/Client/PinPresentationTests.cs`

**Interfaces:**
- Consumes: `SendspinClientOptions`, already the composition root for `IAudioPipeline`, `IPairingRecordStore`, `IPinLockoutStore`.
- Produces: `Func<string, CancellationToken, ValueTask>? PresentPinAsync { get; init; }` on `SendspinClientOptions`.

`ClientCapabilities.EmitPin` is a settable `Action<string>` on a config DTO with no async form and no cancellation.

**A delegate, not an `IPinPresenter` interface.** ohf-sage: *Prefer not introducing an abstraction for logic with only one real consumer; avoid speculative flexibility* `[mined · 2 PRs]`. An interface here would have one method, one implementer and no state. The store interfaces it would mimic are not the precedent they appear to be — `IPairingRecordStore`, `IPinLockoutStore` and `IStaticDelayStore` are all multi-method stateful collaborators, which is what earns an interface.

- [ ] **Step 1: Write the failing tests**

1. **The delegate is awaited and receives the PIN** — offer `dynamic_pin`, set `PresentPinAsync` to capture its argument, drive a pairing far enough to derive a PIN, and assert the captured PIN is the one the server can verify. Assert it was *awaited*: have the delegate set a flag only after an `await Task.Yield()`, and assert the flag is set by the time the client sends its next message.
2. **Fails closed when `dynamic_pin` is offered with no delegate** — matches the pre-existing ruling that PIN pairing fails closed when configured. Assert the specific failure, not merely "did not succeed".
3. **Cancellation is passed, not defaulted** — assert the token the delegate receives is the one the client is operating under (i.e. cancelling it is observable), rather than `CancellationToken.None`.

- [ ] **Step 2: Run to verify they fail**

Run with `--filter "FullyQualifiedName~PinPresentationTests"`. Expected: fail to compile.

- [ ] **Step 3: Add the option**

```csharp
    /// <summary>
    /// Presents a derived dynamic PIN to the operator through the app's out-channel (display,
    /// speaker) so it can be entered into the server. Required when <c>"dynamic_pin"</c> is
    /// offered in <see cref="ClientCapabilities.PinPairingMethods"/>; pairing fails closed
    /// without it. Awaited before the client proceeds, so a slow presenter delays pairing
    /// rather than racing it.
    /// </summary>
    public Func<string, CancellationToken, ValueTask>? PresentPinAsync { get; init; }
```

- [ ] **Step 4: Delete `EmitPin` and rework the call site**

Remove the property. Update the `PinPairingMethods` doc comment at `:144-149`, which references `EmitPin` by name.

`SendSpinClient.cs:1177` is `_capabilities.EmitPin?.Invoke(pin);` — a fire-and-forget call in what may be a synchronous path. Await the delegate instead. If the enclosing method is not async, make it async and follow the awaits outward; do **not** paper over it with `.GetAwaiter().GetResult()` or `SafeFireAndForget`, either of which reintroduces the swallow the previous slice removed. If the call chain cannot be made async without a wide refactor, stop and report rather than choosing a workaround.

**Fail closed:** when `dynamic_pin` is offered and `PresentPinAsync` is null, the pairing attempt must fail rather than silently proceeding with an unpresented PIN.

- [ ] **Step 5: Fix the malformed doc comment**

`ClientCapabilities.cs:99` — `RequiredLeadTimeMs`'s doc block is missing its opening `/// <summary>`, so the text after `MacAddress` runs into it. One line. In scope because this task edits the file and ohf-sage requires caller-facing docs.

- [ ] **Step 6: Run the tests, then the full suite, then commit**

```bash
git add src/Sendspin.SDK/Client/ClientCapabilities.cs src/Sendspin.SDK/Client/SendspinClientOptions.cs src/Sendspin.SDK/Client/SendSpinClient.cs tests/Sendspin.SDK.Tests/Client/PinPresentationTests.cs
git commit -m "fix(client)!: present the dynamic PIN through an awaited delegate

EmitPin was a settable Action<string> on a configuration object, with no async
form and no cancellation, invoked fire-and-forget. Presenting a PIN is a
collaborator the SDK calls, not a value it advertises, so it belongs on the
options object beside the other seams — and it needs to be awaited, since a
presenter that has not finished displaying the PIN cannot have had it entered.

A delegate rather than an interface: a single stateless method with one
implementer does not earn one, unlike the multi-method stores it would have sat
beside.

Closes #85 item 7."
```

---

### Task 7: `SourceSupport` and real codec selection (#85 item 8)

**Files:**
- Create: `src/Sendspin.SDK/Client/SourceSupport.cs`
- Modify: `src/Sendspin.SDK/Client/ClientCapabilities.cs` (replace `SourceLineSense` at `:137-142`)
- Modify: `src/Sendspin.SDK/Client/SendSpinClient.cs` (`:309`, `:369`)
- Modify: `src/Sendspin.SDK/Audio/Source/SourceStreamPipeline.cs` (`:112`)
- Test: `tests/Sendspin.SDK.Tests/Audio/SourceCodecSelectionTests.cs`

**Interfaces:**
- Consumes: `ISourceAudioEncoderFactory.Create(string codec, AudioFormat format)`; `IAudioCaptureDevice.Format`.
- Produces: `SourceSupport` with `LineSense` and `Codec`; `ClientCapabilities.SourceSupport` replacing `SourceLineSense`.

**Two defects behind one item.** The bool is the cosmetic half. The real one is `SourceStreamPipeline.cs:112`:

```csharp
_encoder = _encoderFactory.Create(format.Codec, format);
```

`format` comes from the capture device, so **a PCM capture device can never emit Opus**, whatever the server negotiates.

- [ ] **Step 1: Write the failing tests**

1. **The defect** — a PCM capture device with `SourceSupport { Codec = "opus" }` must produce an **opus** encoder. Assert the codec passed to the factory, or the resulting `_encoder.Codec`.
2. **Positive control, not optional** — with `SourceSupport.Codec` unset, the encoder still matches the capture device's format. Without this, a fix that hard-codes opus passes test 1.
3. **`LineSense` still reaches the wire** — `source@v1_support.features.lineSense` is advertised when `SourceSupport { LineSense = true }`, and the source role gate at `:309` still honours it.

- [ ] **Step 2: Run to verify they fail**

Run with `--filter "FullyQualifiedName~SourceCodecSelectionTests"`. Expected: test 1 fails (PCM in, PCM out); tests 2 and 3 fail to compile until the type exists.

- [ ] **Step 3: Add the type**

```csharp
namespace Sendspin.SDK.Client;

/// <summary>
/// How this client's <c>source@v1</c> role behaves. Only meaningful when the role is
/// advertised and a capture device is configured.
/// </summary>
public sealed class SourceSupport
{
    /// <summary>
    /// Whether this client reports line-sense signal presence, advertised in
    /// <c>source@v1_support.features</c> and reported through <c>client/state</c>.
    /// </summary>
    public bool LineSense { get; init; }

    /// <summary>
    /// Codec to encode captured audio as. Null keeps the capture device's own codec, which is
    /// the previous behaviour. Set this when the device captures PCM but the stream should
    /// carry a compressed codec.
    /// </summary>
    public string? Codec { get; init; }
}
```

Two members, because two defects need two members. Do not grow this into a source-configuration tree.

- [ ] **Step 4: Replace `SourceLineSense`**

`ClientCapabilities.SourceLineSense` → `public SourceSupport? SourceSupport { get; set; }`. Update `SendSpinClient.cs:309` (`!_capabilities.SourceLineSense`) and `:369` (`Features = _capabilities.SourceLineSense ? new SourceFeatures { LineSense = true } : null`) to read through it, treating null as "no source support configured".

- [ ] **Step 5: Take the codec from configuration**

`SourceStreamPipeline.cs:112` must prefer the configured codec and fall back to the capture format:

```csharp
string codec = _configuredCodec ?? format.Codec;
_encoder = _encoderFactory.Create(codec, format);
```

Thread the configured codec in through the pipeline's existing construction path. Note the pipeline's constructor is already source-breaking on this branch's base, so adding a parameter is consistent with the branch — but prefer passing it in the way the pipeline already receives its configuration rather than adding a parallel channel.

The fallback is what keeps existing behaviour the default; only an explicit choice changes anything.

- [ ] **Step 6: Run the tests, then the full suite, then commit**

```bash
git add src/Sendspin.SDK/Client/SourceSupport.cs src/Sendspin.SDK/Client/ClientCapabilities.cs src/Sendspin.SDK/Client/SendSpinClient.cs src/Sendspin.SDK/Audio/Source/SourceStreamPipeline.cs tests/Sendspin.SDK.Tests/Audio/SourceCodecSelectionTests.cs
git commit -m "fix(source)!: let the source role choose its output codec

The encoder was created from the capture device's own format, so a PCM capture
device could never emit Opus no matter what the server negotiated. The codec is
now configuration, defaulting to the capture format so existing behaviour is
unchanged unless an explicit choice is made.

SourceLineSense becomes SourceSupport, which is where that choice lives.

Closes #85 item 8."
```

---

### Task 8: The SDK stops mutating consumer config (#88 item 3)

**Files:**
- Create: `src/Sendspin.SDK/Client/PairingConfigChangedEventArgs.cs`
- Modify: `src/Sendspin.SDK/Client/ISendspinClient.cs`, `src/Sendspin.SDK/Client/SendSpinClient.cs`
- Test: `tests/Sendspin.SDK.Tests/Client/PairingConfigOwnershipTests.cs`

**Interfaces:**
- Consumes: `EnsurePairingPsk` from Task 2 — the token this event says has stopped being current.
- Produces: `event EventHandler<PairingConfigChangedEventArgs>? PairingConfigChanged;`

**The defect.** `SendSpinClient.cs:1448`:

```csharp
_capabilities.UnpairedAccessEnabled = uaEnabled.GetBoolean();
```

The SDK reaches into the **consumer-owned** `ClientCapabilities` instance. The app's config object silently disagrees with reality, and the setting reverts on restart.

The spec permits the server to perform this operation, so the SDK must honour it — but not by writing into an object it does not own.

- [ ] **Step 1: Write the failing tests**

1. **The load-bearing one** — send `set-pairing-config` flipping `unpaired_access.enabled` to true, then assert **all three**: the app's `ClientCapabilities` instance is unchanged; the event fired carrying the new value; and admissibility now behaves as though unpaired access is enabled. A test asserting only the first passes if the handler were deleted entirely, which is why the third clause is not optional.
2. **Seeded from capabilities** — a client built with `UnpairedAccessEnabled = true` admits an unpaired server before any `set-pairing-config` arrives. Pins that the effective value starts from the app's configuration rather than `false`.
3. **PSK replacement notifies** — `set-pairing-config` supplying a `pairing_psk` raises the event, and `EnsurePairingPsk()` afterwards returns the *new* token. This is the contract Task 2's docs promise.
4. **No event on an unrelated request** — a `set-pairing-config` that changes nothing (or a different management request) does not raise it.

- [ ] **Step 2: Run to verify they fail**

Run with `--filter "FullyQualifiedName~PairingConfigOwnershipTests"`. Expected: test 1 fails on the "unchanged" clause (today the SDK mutates it); 2 may pass already; 3 and 4 fail to compile.

- [ ] **Step 3: Add the event args**

```csharp
namespace Sendspin.SDK.Client;

/// <summary>
/// A server changed this client's pairing configuration through
/// <c>management/set-pairing-config</c>. The SDK applies the change to its own state and
/// raises this so the app can persist it; the SDK does not write to the
/// <see cref="ClientCapabilities"/> instance the app owns.
/// </summary>
public sealed class PairingConfigChangedEventArgs : EventArgs
{
    /// <summary>The effective unpaired-access setting after the change.</summary>
    public bool UnpairedAccessEnabled { get; init; }

    /// <summary>
    /// True when the server replaced this client's Pairing PSK, so any pairing token
    /// previously obtained from <see cref="ISendspinClient.EnsurePairingPsk"/> is stale.
    /// </summary>
    public bool PairingPskReplaced { get; init; }
}
```

- [ ] **Step 4: Hold the effective value in the client**

Add a private field seeded at construction from `_capabilities.UnpairedAccessEnabled`. Replace the mutation at `:1448` with an assignment to that field.

**Then repoint every reader.** `git grep -n 'UnpairedAccessEnabled' src/` — the admissibility sites are `SendSpinClient.cs:963` and `:971`, and both must read the field, not `_capabilities`. Missing one leaves the server's change half-applied, which is worse than today's honest-but-rude mutation. Also check `:374`, which advertises the value in `client/hello`; whether that should report the effective or the configured value is a judgement — report the **effective** value, since that is what the client will actually do, and say so in your report.

- [ ] **Step 5: Raise the event**

Raise once per `set-pairing-config` that changed something, after both possible changes have been applied, so a request carrying both an `unpaired_access` flip and a new `pairing_psk` produces one event describing both. Do not raise when nothing changed.

- [ ] **Step 6: Run the tests, then the full suite, then commit**

```bash
git add src/Sendspin.SDK/Client/PairingConfigChangedEventArgs.cs src/Sendspin.SDK/Client/ISendspinClient.cs src/Sendspin.SDK/Client/SendSpinClient.cs tests/Sendspin.SDK.Tests/Client/PairingConfigOwnershipTests.cs
git commit -m "fix(client)!: stop mutating consumer-owned capabilities

Handling management/set-pairing-config, the SDK wrote unpaired access straight
into the ClientCapabilities instance the app had handed it. The app's own
configuration object then disagreed with reality, and the setting reverted on
restart because nothing persisted it.

The spec permits the server to make this change, so the SDK still honours it —
but against its own effective state, raising an event so the app can persist the
change. The same event reports a server-supplied Pairing PSK replacing the
stored one, which makes any previously issued pairing token stale.

Closes #88 item 3."
```

---

### Task 9: Hide the crypto internals (#85 item 5)

**Files:**
- Modify: `NoiseConstants.cs`, `NoisePsk` (in `NoisePsk.cs` or `PairingRecordStore.cs` — grep), `INoisePskResolver`, `SentinelPskResolver`, `RecordPskResolver`, `INoiseSessionInfo.cs`
- Modify: whatever the compiler flags

**Interfaces:**
- Consumes: everything the prior eight tasks built. **This task runs last** because making these types `internal` breaks anything written against them, and one sweep beats nine.
- Produces: a smaller public surface. Nothing depends on it.

**Six of the eight types #85 lists, not all eight.** Two cannot be made internal, and the issue's own text is why:

| Type | Action |
|---|---|
| `NoiseConstants`, `NoisePsk`, `INoisePskResolver`, `SentinelPskResolver`, `RecordPskResolver`, `INoiseSessionInfo` | → `internal` |
| `NoiseCipherSuite` | **stays public** — the type of `SendspinClientOptions.Suite` |
| `PskCategory` | **stays public** — the type of `PairingRecord.Category`, and #85 says record types stay public |

Hiding those two would mean gutting the store API the issue wants kept. Do not attempt it; report the correction instead.

- [ ] **Step 1: Change the six declarations**

`public` → `internal` on each of the six. Build the library:

`dotnet build src/Sendspin.SDK/Sendspin.SDK.csproj -f net10.0 --nologo`

- [ ] **Step 2: Follow the compiler**

Every error is either a genuine public-surface leak to fix, or a member that should have been internal already. Two shapes to expect:

- **A public member typed by a now-internal type** — CS0050 / CS0053 ("inconsistent accessibility"). Each one is a real finding: it means that type was reachable from the public surface after all. Fix by making the member internal if nothing outside needs it, or **stop and report** if a public consumer genuinely needs it, because that is a design question this task cannot decide alone.
- **Tests referencing internal types.** The test project needs `InternalsVisibleTo`. Check whether `Sendspin.SDK.csproj` already grants it — if it does, tests compile unchanged; if not, add it rather than reverting a type to public.

- [ ] **Step 3: Verify the surface actually shrank**

```bash
git grep -n 'public .*\b\(NoiseConstants\|NoisePsk\|INoisePskResolver\|SentinelPskResolver\|RecordPskResolver\|INoiseSessionInfo\)\b' src/
```

Should return nothing but incidental doc-comment references. Then confirm the two survivors are still reachable: `SendspinClientOptions.Suite` and `PairingRecord.Category` must still compile from outside the assembly — a consumer smoke-check is enough (construct the options object and read a record's category in a test that does *not* rely on `InternalsVisibleTo`).

- [ ] **Step 4: Run the full suite and both builds, then commit**

```bash
git add -A src/ tests/
git commit -m "refactor!: make the Noise internals internal

Eight crypto types were public, and every public type is a compatibility
commitment for v11. Six of them are mechanics no consumer needs: the
constants, the PSK record, the resolver interface and its two
implementations, and the session-info view.

Two of the eight stay public, because the issue's own requirement to keep the
store API public reaches them: NoiseCipherSuite is the type of
SendspinClientOptions.Suite, and PskCategory is the type of
PairingRecord.Category. Hiding those would mean gutting the store API rather
than hiding mechanics.

Closes #85 item 5."
```

---

### Task 10: Make the shipped record stores thread-safe (from Task 2's re-review)

*Added after Task 2, whose re-review found that a per-client lock structurally cannot keep `IPairingRecordStore`'s documented promise.*

**Files:**
- Modify: `src/Sendspin.SDK/Connection/Noise/PairingRecordStore.cs` — `InMemoryPairingRecordStore`, `FilePairingRecordStore`, and the `IPairingRecordStore` doc comment
- Test: `tests/Sendspin.SDK.Tests/Connection/PairingRecordStoreConcurrencyTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: no signature changes. Behaviour only.

**Why a per-client lock is not enough.** Task 2 added `lock (_pairingStoreLock)` around the client's own store accesses, which was correct and stays. But `SendspinHostService` constructs one client per incoming connection over the **shared** `_options.PairingRecordStore` (`SendSpinHostService.cs:381-413`), each with its own private lock — N clients, N locks, one `Dictionary`. And `RecordPskResolver.Resolve` reads the store from the framing handshake path, which no client-private lock can reach.

Both stores back onto a plain `Dictionary<string, PairingRecord>`, so a concurrent mutation during `List()`'s `Values.ToList()` can throw, or a lost update can hand out a token whose PSK the store no longer holds.

**The two locks are complementary, not redundant.** Store-level locking makes each individual call safe. The client's lock makes multi-call *sequences* atomic — `RotatePairingPsk` removes every Pairing record and then upserts one, and that sequence must not interleave with another writer. Do not remove the client lock.

- [ ] **Step 1: Write the failing test**

Create `tests/Sendspin.SDK.Tests/Connection/PairingRecordStoreConcurrencyTests.cs`. The realistic failure is `List()` throwing while another thread mutates, so drive exactly that: one task looping `Upsert`/`Remove` with distinct psk_ids while another loops `List()`, for a few thousand iterations, and assert no exception escapes either.

A concurrency test that passes by luck is worse than none, so make the window wide: run the writer and reader concurrently on `Task.Run`, and fail the test on the first exception captured from either. Run it against `InMemoryPairingRecordStore`.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo --filter "FullyQualifiedName~PairingRecordStoreConcurrencyTests"`

Expected: fails with `InvalidOperationException` ("Collection was modified"). **If it passes, do not proceed by assuming the test is fine** — raise the iteration count until it fails, and report what it took. A test that never went red proves nothing about the fix.

- [ ] **Step 3: Lock both stores**

Add a private lock object to `InMemoryPairingRecordStore` and to `FilePairingRecordStore`, and take it in `List`, `Upsert` and `Remove`.

For `FilePairingRecordStore`, `Save()` is called from inside `Upsert`/`Remove`, so it runs under the lock already — check that the lock is not taken twice in a way that obscures ownership, and that no `await` sits inside it (there is none today; `SecureFile.WriteAllTextAtomic` is synchronous).

Keep `List()` returning a snapshot (`Values.ToList()`), which it already does — that is what makes the returned list safe to enumerate after the lock is released.

- [ ] **Step 4: Reconcile the interface doc**

`IPairingRecordStore`'s summary currently says "Implementations need not be thread-safe; the SDK serializes access." That promise is now false — the SDK invites app threads in through `EnsurePairingPsk` and `RotatePairingPsk`. State what is actually true: the SDK may call from an app thread and from a connection's receive thread, so an implementation **must** be safe for concurrent use; the two shipped implementations are.

This is the same class of defect as #85 item 4 — a doc comment that misleads a consumer — so fixing it belongs on this branch rather than after it.

- [ ] **Step 5: Run the test, then the full suite, then commit**

```bash
git add src/Sendspin.SDK/Connection/Noise/PairingRecordStore.cs tests/Sendspin.SDK.Tests/Connection/PairingRecordStoreConcurrencyTests.cs
git commit -m "fix(pairing): make the shipped record stores safe for concurrent use

IPairingRecordStore promised that the SDK serialized access, and that held while
every mutation ran on a connection's receive path. Adding app-callable pairing
initiation broke it: an operator displaying a QR code now writes to the store
from an app thread while a connected server may be writing from the receive
thread, over a plain Dictionary.

A per-client lock cannot close this. The host service builds one client per
incoming connection over one shared store, so each gets its own lock, and the
PSK resolver reads the store from the framing path where no client lock reaches.
Locking the stores themselves closes both, and the client's lock stays because it
serializes multi-call sequences that per-call locking cannot.

The interface doc now states what is actually true."
```

---

## Verification Checklist

- [ ] Both published pairing-token KATs reproduce exactly, in both directions.
- [ ] A version-`1` token decodes; a version-`2` token does not.
- [ ] `EnsurePairingPsk()` is idempotent, survives a new client over the same store, and a completed pairing leaves the `Pairing` record in place.
- [ ] `EnsurePairingPsk()` with no store throws rather than returning an ephemeral token.
- [ ] `ISendspinClient.TrustLevel` returns a different value for each PSK category and `None` with no session.
- [ ] `ISendspinClient.ClientId` equals the identity's base64url public key.
- [ ] `git grep -n 'ClientRoles\.'` shows no site concatenating or stripping a version suffix; every constant ends `@v1`; `Source` and `Color` exist.
- [ ] `git grep -n 'ClientId' src/` shows no `ClientCapabilities.ClientId` and no `AdvertiserOptions.ClientId`.
- [ ] `git grep -n 'EmitPin'` returns nothing.
- [ ] A PCM capture device with `SourceSupport.Codec = "opus"` produces an opus encoder; unset still follows the capture format.
- [ ] `set-pairing-config` leaves the consumer's `ClientCapabilities` untouched, raises the event, and changes admissibility — all three asserted in one test.
- [ ] Both `UnpairedAccessEnabled` admissibility readers use the effective field, not `_capabilities`.
- [ ] The six types in Task 9 are `internal`; `NoiseCipherSuite` and `PskCategory` remain public and reachable from outside the assembly.
- [ ] `ClientCapabilities.cs`'s `RequiredLeadTimeMs` doc comment is well-formed.
- [ ] `dotnet test ... -f net10.0` passes with 0 failures.
- [ ] `dotnet build src/Sendspin.SDK/Sendspin.SDK.csproj` clean for both `net8.0` and `net10.0`; no new IL2026/IL3050.
- [ ] Both shipped record stores survive concurrent `List`/`Upsert`/`Remove`, and `IPairingRecordStore`'s doc no longer claims the SDK serializes access.
- [ ] `<Version>9.1.0</Version>` unchanged.
- [ ] **Interop against `aiosendspin[server]==7.0.0` reviewed with care.** Unlike the previous slice, this branch changes values that go on the wire — the role strings — and the shape of what the app configures. Read `.github/workflows/interop.yml` and `tools/interop/` and check the harness still compiles and still advertises valid roles.
