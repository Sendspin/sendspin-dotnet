# Design: finalise the v10 public surface

*Closes [#85](https://github.com/Sendspin/sendspin-dotnet/issues/85) (items 1–5, 7, 8), [#88](https://github.com/Sendspin/sendspin-dotnet/issues/88) item 3, and [#79](https://github.com/Sendspin/sendspin-dotnet/issues/79). Slice B of the #84/#85/#86/#88 group; slice A shipped in [PR #104](https://github.com/Sendspin/sendspin-dotnet/pull/104), slice C in [PR #105](https://github.com/Sendspin/sendspin-dotnet/pull/105).*

## 0. Decisions, and who made them

Two were the repo owner's, taken with the tradeoffs stated. The rest were delegated to `Sendspin/spec` and ohf-sage.

**Owner decision 1 — #79 ships here, emitting token version `0` and accepting `0` and `1` on decode.** The alternative was deferring item 1 until upstream resolves the version clash. Cost accepted: a token this SDK emits is rejected by the Music Assistant beta, which pins `aiosendspin` 7.0.0 (emits and accepts version `1`), until MA repins. `aiosendspin` 7.1.0 is still a draft release, so waiting had no date attached.

**Owner decision 2 — one branch, not three.** This directly overrides ohf-sage's **MUST keep pull requests small and single-purpose** `[mined · 11 PRs · 👍]`. Recorded as deliberate, not oversight: every item here is a breaking change to the same public surface, and bundling them means windowsSpin absorbs one migration instead of three. A later reader should treat the size as bought, not accidental.

**ohf-sage rulings that shaped the design** (Sendspin is not yet mined, so the applicable layer is *Overall — Marcel, authoritative everywhere*):

| Rule | Weight | Where it lands |
|---|---|---|
| **Prefer** not introducing an abstraction for logic with only one real consumer; avoid speculative flexibility | `[mined · 2 PRs]` | §7 — killed the `IPinPresenter` interface in favour of a delegate |
| **Prefer** the simplest solution that works; reject overengineered "AI slop" | `[mined · 4 PRs · 👍]` | §8 — `SourceSupport` carries two members, not a config tree |
| **MUST** write caller-facing docstrings (what the method does, not its internals) | `[authored+mined]` | every new public member; also fixes `ClientCapabilities.cs:99` |
| **Prefer** early guard clauses / returns over nested `if`/`else` | `[mined · 4 PRs · 👍]` | §1 — the lenient decoder is a validation chain |
| **MUST** order file/class contents public first, private/helper at the bottom | `[enforced]` | the new files |
| **MUST** fix a problem at its root cause and correct owning location | `[mined · 3 PRs]` | §5 — remove `ClientId`, don't paper over it |

**Spec finding that settled §5.** `connection.md:11-15` prescribes only the service type, port, and the `path` / `name` TXT records for a client's mDNS advertisement. **There is no `client_id` anywhere in it.** So `AdvertiserOptions.ClientId` is not a protocol value at all — it is a local DNS-SD instance label wearing a misleading name.

## 1. The pairing token (#79)

New `PairingToken` in `src/Sendspin.SDK/Connection/Noise/Pairing/`.

```
token   = "SP:" || version || body
payload = client_key (32 bytes) || pairing_psk (32 bytes)
body    = base32(payload), RFC 4648, '=' padding stripped, every '2' transliterated to '9'
```

A version-0 token is 107 characters: `"SP:"` (3) + version (1) + body (103). 64 bytes is 512 bits, so 103 base32 characters carry it with the final group padded.

**The algorithm was verified by hand against both published KATs before writing this**, because getting it wrong is expensive and the two vectors differ only in PSK bytes. From `client_key = 0x00…0x1f`, the first five payload bytes `00 01 02 03 04` give bit groups `00000 00000 00000 10000 00100 00000 11000 00100` → `A A A Q E A Y E`, reproducing the published `SP:0AAAQEAYE…` prefix. Both vectors ship as tests.

**Encode** emits version `0`.

**Decode is lenient, per spec**, and is a guard chain rather than nested conditionals: trim; upper-case; strip an optional `SP:`; take the first character as the version and reject an unrecognised one; transliterate `9`→`2`; re-pad to a multiple of 8; base32-decode; reject unless exactly 64 bytes.

**Decode accepts version `0` and version `1`** (owner decision 1). Both versions carry the identical 64-byte payload — the clash is purely which integer the two implementations stamp and accept — so accepting both costs nothing and is what lets this SDK read a token from a 7.0.0 server.

No base32 exists in `src/`. `SimpleBase.dll` appears only in test and tool output directories as a transitive dependency of the mDNS library, so it is not available to the SDK and adding a package reference for ~40 lines each way is the wrong trade.

## 2. Pairing initiation (#85 item 1)

The client can only ever *receive* a Pairing PSK, via `management/set-pairing-config`. There is no way for an app to initiate the pairing method the spec makes mandatory for clients. That is the largest functional gap in the release.

Two members, on `ISendspinClient`:

- **`EnsurePairingPsk()`** — returns the pairing token, generating and persisting a Pairing PSK from a CSPRNG if the store holds none. Idempotent: called twice, it returns the same token.
- **`RotatePairingPsk()`** — replaces it deliberately. This exists because spec #122 forbids the client rotating on its own, which means rotation must be an explicit operator action with its own name rather than a side effect of anything else.

Spec #122's lifecycle rules are the constraints, and each is testable:

| Rule | Consequence here |
|---|---|
| Generated from a CSPRNG | `RandomNumberGenerator`, never `Random` |
| Persists across reboots | written to `IPairingRecordStore`, not held in memory |
| Per-client and long-lived | one `Pairing`-category record, not one per server |
| **Not** consumed or rotated by a successful pairing | pairing must leave the record in place — this is the rule most likely to be broken by accident |
| Client MUST NOT rotate on its own | only `RotatePairingPsk()` or `management/set-pairing-config` may replace it |

`EnsurePairingPsk` needs a store. With none configured, it throws rather than returning an ephemeral token that would silently fail to survive a restart — per ohf-sage, a silent default here is exactly the swallow the previous slice removed.

## 3. Trust level (#85 item 2)

`TrustLevel` on `ISendspinClient`, so an app can render "paired / unpaired" without constructing the `INoiseSessionInfo` itself.

A **new public enum** is required rather than exposing the existing `PskCategory`, because §6 makes that type internal. This is a genuine interaction between two items of the same issue, not a preference:

```csharp
public enum SendspinTrustLevel
{
    /// <summary>No session, or the handshake has not completed.</summary>
    None,

    /// <summary>Authenticated with the published Sentinel PSK: authenticated but untrusted.</summary>
    Unpaired,

    /// <summary>Authenticated with the bootstrap Pairing PSK; pairing is in progress.</summary>
    Pairing,

    /// <summary>Authenticated with a long-term PSK from a completed pairing.</summary>
    Paired,
}
```

The mapping from the internal `PskCategory` lives at the boundary. Note `Unpaired` deliberately does not mean "no encryption" — under mandatory encryption every session is authenticated, and the distinction this enum draws is *trust*, not confidentiality. The doc comments must say so, or an app author will read `Unpaired` as "insecure transport" and render the wrong thing.

## 4. `ClientRoles` (#85 item 3)

The constants are bare (`"player"`) while the wire and `ClientCapabilities.Roles` both use `@v1` suffixes, so the constants never matched what they were for. Add the suffixes, and add the two missing members: `Source` (promised, never delivered) and **`Color`** — the shipped defaults already include `"color@v1"` with no constant for it, which the issue does not mention.

Every existing role constant changes value. That is breaking, but only for code that was already sending a string the server rejects.

## 5. `ClientId` (#85 item 4)

`ClientCapabilities.ClientId` is a settable string defaulting to `sendspin-windows-{hostname}`. Under encryption `client_id` **is** the Curve25519 public key, and `CreateClientHelloMessage` already omits the property (`SendSpinClient.cs:298`). So it is a mutable, platform-named, silently-ignored field — the most actively misleading member on the public surface.

**Remove it**, rather than making it read-only. ohf-sage's root-cause rule applies: a read-only property named `ClientId` on a *capabilities* object still implies the app has a say in something it does not.

Three consequences:

1. **The real value gets surfaced**: a read-only `ClientId` on `ISendspinClient`, derived from the identity's public key — the value the protocol actually uses.
2. **`AdvertiserOptions.ClientId` becomes `InstanceName`.** Per the spec finding in §0 this was never a protocol value; it is the DNS-SD instance label. Renaming it is the honest fix, and it removes the last consumer of the removed property (`SendspinHostService.cs:215`).
3. `SendspinHostService.ClientId` (`:46`), which forwards the advertiser's value, follows the rename.

## 6. Crypto visibility (#85 item 5)

Eight types are public; the issue asks for all eight to become internal. **Two of them cannot, and the issue's own text is why.**

| Type | Ruling |
|---|---|
| `NoiseConstants`, `NoisePsk`, `INoisePskResolver`, `SentinelPskResolver`, `RecordPskResolver`, `INoiseSessionInfo` | → `internal` |
| `NoiseCipherSuite` | **stays public** — it is the type of `SendspinClientOptions.Suite` |
| `PskCategory` | **stays public** — it is the type of `PairingRecord.Category`, and the issue says record types stay public |

Hiding those two would mean gutting the store API the issue wants kept, which is a larger change than the one being asked for. Six of eight is the correct answer here, and the issue's list should be corrected rather than followed.

`RecordPskResolver` going internal is worth a second look during implementation: it is the resolver a paired client uses, so if anything outside the SDK constructs one, it needs a public path. Nothing in this repo does.

## 7. PIN presentation (#85 item 7)

`ClientCapabilities.EmitPin` is a settable `Action<string>` on a config DTO, with no async form and no cancellation.

**Replace it with a delegate on `SendspinClientOptions`, not an interface:**

```csharp
public Func<string, CancellationToken, ValueTask>? PresentPinAsync { get; init; }
```

The issue suggests "an event or a small `IPinPresenter` seam ... fits the SDK's existing store-interface idiom better", and that was the design's first answer. ohf-sage overrules it: **Prefer not introducing an abstraction for logic with only one real consumer; avoid speculative flexibility** `[mined · 2 PRs]`. An `IPinPresenter` would have one method, one implementer, and no state. The store interfaces it would be mimicking are not the precedent they look like — every one of them (`IPairingRecordStore`, `IPinLockoutStore`, `IStaticDelayStore`) is a multi-method stateful collaborator, which is what earns an interface.

The delegate fixes both stated objections — it is async, it takes a `CancellationToken`, and it lives on the composition root beside the other seams rather than on a capabilities DTO.

## 8. Source support (#85 item 8)

Two defects behind one item:

1. `ClientCapabilities.SourceLineSense` is a bare bool where the plan promised a `SourceSupport` object.
2. `SourceStreamPipeline.cs:112` derives the encoder from `format.Codec`, where `format` comes from the **capture device** — so a PCM capture device can never emit Opus, no matter what the server negotiates.

```csharp
public sealed class SourceSupport
{
    /// <summary>Whether this client's source role reports line-sense signal presence.</summary>
    public bool LineSense { get; init; }

    /// <summary>Codec to encode captured audio as. Null keeps the capture device's own codec.</summary>
    public string? Codec { get; init; }
}
```

Two members, because that is what the two defects need. Per ohf-sage's simplest-solution rule this is not the place to design a source-configuration tree; a third member can be added when something needs it.

`SourceLineSense` is replaced by `SourceSupport`, and the pipeline takes its codec from configuration, falling back to the capture format when unset — so the existing behaviour is the default and only an explicit choice changes it.

## 9. The SDK stops mutating consumer config (#88 item 3)

`SendSpinClient.cs:1448`, handling `management/set-pairing-config`:

```csharp
_capabilities.UnpairedAccessEnabled = uaEnabled.GetBoolean();
```

The SDK reaches into the **consumer-owned** `ClientCapabilities` instance, so the app's own config object silently disagrees with reality and the setting reverts on restart.

The spec permits the server to perform this operation, so the SDK must honour it — but not by writing into an object it does not own. **The SDK keeps its own effective value, seeded from `Capabilities.UnpairedAccessEnabled` at construction, and raises an event.**

```csharp
event EventHandler<PairingConfigChangedEventArgs>? PairingConfigChanged;
```

Carrying what changed and the new effective value, so the app can persist it. The same event covers a server-supplied Pairing PSK replacing the stored one, which is the other half of the same handler and the notification §2's `EnsurePairingPsk` contract needs — an app holding a token must learn when that token stopped being current.

Admissibility (`:963`, `:971`) must read the SDK's effective value, not `_capabilities`. Missing one of those two call sites would leave the server's change half-applied, which is worse than today's honest-but-rude mutation.

## 10. Out of scope

| Deferred | Reason |
|---|---|
| #85 item 6 | Already done — `SendspinClientOptions` landed in [#94](https://github.com/Sendspin/sendspin-dotnet/pull/94); `SendspinHostService` takes 6 parameters, not 11 |
| #106, #107, #108, #110 | Follow-ups filed from slice C; unrelated to the public surface |
| #109 — the 13 remaining broad catch-alls | Slice C's deliberate deferral; its own single-purpose change |
| #77, #80, #81, #82, #89, #90 | Own issues, not public-API shape |
| #91 — the version bump | Release engineering. **`main` already carries source-breaking changes at an unchanged `9.1.0`, so nothing may be published at that version.** This branch adds many more |

Package version stays `9.1.0` in-branch; #91 owns the bump.

## 11. Testing

Every test must assert the **absence of the harm**, and any test asserting that something did *not* happen must be checked against "would this pass if the machinery producing it were deleted?" That check has caught a real gap in each of the last three slices, most recently a compound predicate whose decode half was invisible to the whole suite, so it is a standing gate rather than advice.

| Item | Test | The assertion that matters |
|---|---|---|
| 1 | both published KATs, encode direction | byte-exact 107-character token |
| 1 | both KATs, decode direction | round-trips to the exact 64-byte payload |
| 1 | a version-`1` token | **decodes** (this is the whole point of owner decision 1) |
| 1 | a version-`2` token | rejected |
| 1 | lenient inputs: lower-case, whitespace, missing `SP:`, `9`-for-`2` | all decode to the same payload |
| 1 | 63- and 65-byte payloads | rejected — pins the exact-length rule, not merely "some length check" |
| 2 | `EnsurePairingPsk()` twice | same token both times; exactly one `Pairing` record in the store |
| 2 | complete a pairing, then read the store | the `Pairing` record **survives** — spec #122's most accident-prone rule |
| 2 | `RotatePairingPsk()` | token changes; still exactly one `Pairing` record |
| 2 | `EnsurePairingPsk()` with no store configured | throws; does not return an ephemeral token |
| 3 | each `PskCategory`, and no session | maps to the right `SendspinTrustLevel`, including `None` |
| 4 | every constant in `ClientRoles` | equals the `@v1` string the wire needs; `Source` and `Color` exist |
| 5 | `client/hello` for a known identity | `ISendspinClient.ClientId` equals the identity's base64url public key |
| 6 | — | compile-time; the suite proves nothing here. Verified by `dotnet build` and by grepping for `public` on the six types |
| 7 | `dynamic_pin` offered with `PresentPinAsync` set | the delegate is awaited, and receives the PIN the server can verify |
| 7 | `dynamic_pin` offered with it unset | fails closed — matches the pre-existing "fail closed when PIN is configured" ruling from slice A |
| 8 | PCM capture device, `SourceSupport.Codec = "opus"` | the encoder created is **opus** — this is the defect, and a test that only checks `SourceSupport` round-trips would miss it |
| 8 | `SourceSupport.Codec` unset | encoder still matches the capture format (positive control: proves the fallback did not become "always opus") |
| 9 | `set-pairing-config` flipping `unpaired_access` | the app's `ClientCapabilities` instance is **unchanged**, the event fires with the new value, and admissibility follows the new value |
| 9 | `set-pairing-config` supplying a `pairing_psk` | the event fires; the previously returned token is no longer current |

Item 9's first row is the load-bearing one: it must assert *both* that the consumer's object was not touched *and* that behaviour changed anyway. A test that only checks the former passes if the handler were deleted entirely.

## 12. Success criteria

1. `PairingToken` reproduces both published KATs in both directions; a version-`1` token decodes and a version-`2` token does not.
2. An app can obtain a pairing token without a server having spoken first, and a successful pairing leaves the Pairing PSK in place.
3. `ISendspinClient` exposes `TrustLevel` and a `ClientId` that equals the identity's public key.
4. No `ClientRoles` constant differs from the string the wire requires; `Source` and `Color` exist.
5. `ClientCapabilities` has no `ClientId`; nothing in `src/` reads one.
6. The six types in §6 are `internal`; `NoiseCipherSuite` and `PskCategory` remain public and reachable.
7. `EmitPin` is gone; a PIN is presented through an awaited delegate with cancellation.
8. A PCM capture device can emit Opus, and an unset codec still follows the capture format.
9. Handling `set-pairing-config` mutates no consumer-owned object, raises an event, and admissibility honours the change.
10. Full suite green on `net10.0`; `dotnet build` clean for `net8.0` and `net10.0`; no new IL2026/IL3050; `<Version>9.1.0</Version>` unchanged.
11. The interop workflow still passes against `aiosendspin[server]==7.0.0`. **This one needs care rather than assumption** — unlike slice C, this branch changes values that go on the wire (role strings) and the shape of what the app configures, so it can plausibly break interop in a way slice C could not.
