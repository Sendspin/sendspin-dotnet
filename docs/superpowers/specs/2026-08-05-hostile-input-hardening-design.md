# Design: Hostile-input hardening

*Closes [#88](https://github.com/Sendspin/sendspin-dotnet/issues/88) items 1, 2 and 4. Slice C of the #84/#85/#86/#88 group; slice A (#84/#86) shipped in [PR #104](https://github.com/Sendspin/sendspin-dotnet/pull/104), slice B (#85) follows.*

## 0. How the open questions were decided

The repo owner delegated the design decisions on this slice to two authorities rather than answering them directly. Both were consulted before writing this, and the rulings are recorded inline so a later reader can check the reasoning rather than trusting it.

**`Sendspin/spec` @ `d5f64a6a`** (current HEAD). Note this is well past what the SDK pins, and past what #92 asked for: spec **#122 merged on 2026-07-31**, followed by #129, #130, #131 and #132. The fragmentation rules quoted in #88 as "in-flight" are therefore now **normative**, which upgrades part of item 4 from hardening to conformance.

**ohf-sage** (`chrisuthe/ohf-sage`, principles doc at `C:\CodeProjects\ohf-principles-agent\agent\ohf-sage.md`). Sendspin is not yet mined, so the applicable layer is *Overall (Marcel — authoritative everywhere)*. Four rules bear on this slice:

| Rule | Weight |
|---|---|
| **MUST** catch specific, expected exception types — no broad or bare `except`, which hides real bugs | `[mined · 11 PRs · 👍]` |
| **MUST** never silently swallow errors or anomalous data — let it propagate or raise; don't collapse failures into a bool, `None`, or a silent default | `[mined · 6 PRs · 👍]` |
| **MUST** keep pull requests small and single-purpose | `[mined · 11 PRs · 👍]` |
| **Prefer** reusing existing shared helpers over reimplementing | `[authored+mined]` |

The first two decide item 2 outright. The third bounds the sweep (see §2.2). The fourth settles item 1's mechanism: the source-generated `MessageSerializer` already exists, so hand-built JSON is the wrong tool independent of the injection bug.

## 1. Item 1 — JSON injection via unvalidated `server_id`

`SendSpinClient.cs:1341-1344` and `:1403-1404` build JSON by **string interpolation** and then re-parse it. `server_id` arrives raw from `management/add-record` with no validation and is interpolated unescaped into the `management/list-records` response:

```csharp
? $"{{\"psk_id\":\"{r.PskId}\",\"used\":{(r.Used ? "true" : "false")}}}"
: $"{{\"psk_id\":\"{r.PskId}\",\"server_id\":\"{r.ServerId}\",\"used\":{…}}}"
```

A `server_id` containing a quote or brace injects structure into a message the client sends. The peer is AEAD-authenticated, but authentication is not authorisation: a Sentinel-keyed server is authenticated and untrusted.

### 1.1 Serialize, don't interpolate

Route both responses through the existing source-generated `MessageSerializer`. Beyond closing the injection, this is the ohf-sage "reuse existing helpers" rule, and it keeps the payloads inside the AOT-safe serialization path the SDK already maintains.

### 1.2 Validate `server_id` on ingest

A `server_id` **is** a base64url Curve25519 public key — 43 characters, no padding. Validate on ingest at `management/add-record` and reject a malformed one with `management/result` `invalid_request` rather than storing it. Validating at the boundary is the ohf-sage "fix it at the root cause and correct owning location" rule: the store should never hold a value that is not a server id.

### 1.3 A dedicated PSK decode helper

`SendspinIdentity.DecodePeerId` is currently used to decode **PSKs** at `:1353` and `:1434`. It works — both are 32 bytes — but a malformed PSK reports "peer id must decode to 32 bytes", which sends a maintainer to the wrong place.

Add an internal PSK decode helper. Two call sites, so this is not the speculative single-consumer abstraction ohf-sage warns against.

### 1.4 The management catch-all at `:1356`

`catch (Exception)` around the PSK decode swallows everything. Narrow it to the decode's actual failure types and answer `invalid_request`. Note `:1312` in the same method **already** narrows to `JsonException or KeyNotFoundException or FormatException` — that is the in-repo precedent to match, not a pattern to invent.

## 2. Item 2 — the catch-alls that swallow hostile authenticated input

Under mandatory encryption every message has been AEAD-authenticated, so a malformed one means the peer is broken or hostile. Swallowing it and continuing leaves the connection in an undefined state — and per ohf-sage, silently swallowing anomalous data is a MUST-not regardless.

### 2.1 What a protocol error does

The spec's `client/goodbye` reason list is **closed**: `another_server | shutdown | restart | user_request | unauthorized | pairing_required | concurrent_attempt | unpaired` (`messaging.md:426`). **None denotes a protocol error, so none may be invented** — a new wire value would be non-conformant.

The fragmentation rule (`messaging.md:107`) says malformed sequences are protocol errors and "the receiver MUST close the connection" — without prescribing a goodbye. That matches how the framing layer already signals fatals: `InboundFrameResult.Fatal` closes the socket without an application-level error.

**Ruling: a protocol error closes the connection, and no new wire value is invented.** How the close is expressed depends on the layer:

- **Framing layer** — `InboundFrameResult.Fatal` closes the socket with no application-level message at all. This is the vehicle for §3's fragmentation errors.
- **Application layer** — `DisconnectAsync` sends a goodbye whenever the socket is open, and there is no goodbye-less close API on `ISendspinConnection`. Rather than widen that interface, reuse the existing `unauthorized`, which the client already sends for a pure protocol-order violation (`ValidateActivateAdmissibility`, `SendSpinClient.cs:956`) and for the management self-removal close (`:1340`).

*Amended after Task 2, whose review found the original single ruling — close without a goodbye everywhere — unimplementable at the application layer without new interface surface. `unauthorized` is a lossy reuse: its worst realistic cost is that an operator debugging a serialization bug sees an auth-flavoured close. That is cheaper than interface surface this slice deliberately avoided adding, and criterion 6 holds either way.*

### 2.2 Which catch-alls, and which are deliberately left

There are 18 `catch (Exception)` sites across `SendSpinClient.cs` and `SendSpinHostService.cs` — leaving 13 after this slice. (An earlier count of 20 here double-counted the two management catches at `:1312` and `:1356`, which are already narrowed.) ohf-sage's rule is a MUST and applies to all of them — but its "keep PRs small and single-purpose" rule is equally weighted and this slice is about *hostile peer input*.

**In scope — the inbound-message path**, where the caught exception may be attacker-influenced:

| Site | Method |
|---|---|
| `SendSpinClient.cs:800` | `OnTextMessageReceived` — #88's named site |
| `SendSpinClient.cs:2220` | `OnBinaryMessageReceived` |
| `SendSpinClient.cs:2160` | `HandleStreamStartAsync(string json)` |
| `SendSpinClient.cs:2182` | `HandleStreamEndAsync(string json)` |
| `SendSpinHostService.cs:503` | inside `HandleServerConnectedAsync` — #88's named site |

**Out of scope, deliberately:** reconnect handshake (`:402`), initial state send (`:1545`), time-sync loops (`:1617`, `:1673`), static-delay persistence (`:2053`, `:2090`), host start/stop/disconnect (`:277`, `:350`, `:654`), arbitration (`:625`), last-played persistence (`:172`, `:190`), and the outer `HandleServerConnectedAsync` guard (`:489`). None handles peer-supplied payloads; sweeping them is a separate single-purpose change.

### 2.3 The shape of the fix

Narrow each in-scope catch to the failure types a malformed payload actually produces — `JsonException` and the decode failures already precedented at `:1312` — and on catching one, **close the connection**. Everything else propagates.

Propagation is the point, not an oversight: a bug in our own role handling should surface as a lost connection an operator can see, not be hidden behind a log line. That is the second ohf-sage MUST.

### 2.4 The tier above the five catches: an unparseable frame never reaches them

*Added after Task 2, whose review found this gap.* Narrowing the five catches only helps a frame that got as far as a handler. A frame that is not valid JSON at all never does:

```csharp
// MessageSerializer.GetMessageType — swallows its own JsonException and returns null
```

`GetMessageType` returns `null`, so the frame falls to `SendSpinClient.cs:795`'s `default:` branch, is logged at Debug, and the connection stays up. That is the *most* malformed input a hostile authenticated peer can send, and it receives the mildest response in the file — so §5's "malformed JSON in an authenticated text frame → the connection closes" row is not literally satisfied by §2.3 alone.

**Ruling: fix it here, not as a follow-up.** It is the central case of this slice's stated purpose, and deferring the central case to keep the PR small inverts the point of the rule.

The whole difficulty is a distinction §2.3 does not have to make:

| Input | Response | Why |
|---|---|---|
| not parseable as JSON, or no `type` member | **close** | no conformant server produces this |
| parses, `type` is a string we do not know | **tolerate** (log at Debug, continue) | forward compatibility — a newer server may send message types this SDK predates |

Collapsing those two into one branch is the failure mode in both directions: closing on the second breaks forward compatibility, and tolerating the first is the bug being fixed. The signal must therefore be three-valued — parsed-and-known, parsed-but-unknown, unparseable — not the current nullable string.

## 3. Item 4 — fragmentation

Spec `messaging.md:101-107` at `d5f64a6a`. Three malformed sequences are protocol errors the receiver MUST close on:

1. a fragment-end frame with no fragmented message in flight
2. a non-fragment frame while a fragmented message is in flight in the same direction
3. an `orig_type` of `2` or `3`

**Current state, verified in `NoiseWireFraming.cs`:**

| Rule | Status |
|---|---|
| 1 | **Handled** — `Fail("fragment-end with no fragmented message in flight")` |
| 2 | **Not handled.** `HandleTransportFrame`'s `_ => DispatchMessage(...)` branch dispatches a non-fragment frame with no check on `_reassemblyBuffer`. The stale buffer survives, so the next fragment-more is treated as a *continuation of the abandoned message* |
| 3 | **Not handled.** `_reassemblyOrigType = plaintext.Span[1]` accepts any byte. `DispatchMessage(2, …)` falls through to the binary branch and surfaces `[2][payload]` to `BinaryMessageParser` as though it were an application message |

So item 4's substance is **two unimplemented spec MUSTs**, not the DoS framing #88 gave it. Both are also state-confusion bugs a hostile peer can drive deliberately.

### 3.1 The reassembly cap

`NoiseConstants.MaxReassembledMessageBytes` is 64 MiB, and fragmentation is reachable as soon as transport mode is up — before `server/hello`. **The spec sets no maximum**, so this is hardening, not conformance, and it is ours to choose.

Rejected approaches: a single much smaller cap breaks large artwork and hi-res audio after activation; refusing fragmentation entirely before the first message would impose a rule the spec does not have, and could strand a conformant server; keying the cap on the matched PSK category would break unpaired-access artwork, which legitimately arrives on a Sentinel-keyed session.

**Chosen: a phase-aware cap keyed on framing-local state.** Before the framing layer has surfaced its first application message, the cap is 128 KiB; afterwards, the existing 64 MiB. The only legitimate pre-first-message content is `server/hello` (a name), so 128 KiB is generous by orders of magnitude while cutting pre-authorisation exposure by ~512×.

This needs no new interface and no coupling to the client — one bool set in `DispatchMessage`. Keeping it framing-local matters: the branch that made the transport encrypted-only deliberately avoided adding to `IWireFraming`, and there is no reason to spend that budget here.

## 4. Out of scope

| Deferred | Reason |
|---|---|
| #88 item 3 — a server remotely mutating consumer-owned `ClientCapabilities` | Needs an event for the app to observe, which is public-API work: slice B (#85) |
| #80 — the `noise/handshake` substring sniff in `DispatchMessage` | Same method this slice touches, but a separate defect with its own issue. Do not fix it in passing |
| #89 — the reflection-based JSON that trips IL2026/IL3050 | §1.1 moves the management payloads *onto* the source-generated serializer, which helps, but #89's remaining sites are its own |
| The 13 non-peer-input catch-alls | §2.2 |
| #85, #79, #91, #92 | Slice B, upstream-blocked, release engineering |

Package version stays `9.1.0` (#91 owns the bump).

## 5. Testing

Every test must assert the **absence of the harm**, and any test asserting that something did *not* happen must be checked against the question "would this pass if the machinery producing it were deleted?" Three of five tasks in slice A shipped a test that failed that question, so it is now a standing check rather than a nicety.

| Item | Test | The assertion that matters |
|---|---|---|
| 1 | `management/add-record` with `server_id` containing `"` and `}` | the record is **rejected**, the store is unchanged, and the reply is `invalid_request` |
| 1 | `management/list-records` with a stored record | the response is valid JSON whose `server_id` round-trips exactly |
| 1 | malformed PSK in `add-record` | error names the **PSK**, not "peer id" |
| 2 | malformed JSON in an authenticated text frame | the connection **closes**; no `client/goodbye` invented |
| 2 | a handler throwing a non-payload exception | it **propagates** rather than being swallowed |
| 2.4 | a frame that is not JSON at all (`"{{{"`), and one with no `type` member | the connection **closes** — neither reaches a handler, so §2.3's tests cannot cover this |
| 2.4 | **positive control**: a well-formed frame whose `type` is `"server/some-future-thing"` | still **tolerated** — logged and ignored, connection stays up. Without this, a fix that closes on every unrecognised frame passes the row above |
| 4 | non-fragment frame while reassembly is in flight | connection **closes**; the buffer does not survive |
| 4 | `orig_type` of `2`, and of `3` | connection **closes**; nothing surfaces to `BinaryMessageParser` |
| 4 | fragment-end with none in flight | still closes (regression guard on the one rule already handled) |
| 4 | reassembly exceeding 128 KiB before the first application message | closes |
| 4 | **positive control**: a legitimately fragmented large message *after* the first application message | reassembles and dispatches |

The positive control on item 4 is not optional: a cap regression that rejected all fragmentation would pass every negative test above.

## 6. Success criteria

1. No JSON is built by string interpolation in the management path; a `server_id` containing JSON metacharacters cannot reach the store or an outgoing message.
2. A malformed PSK produces an error naming the PSK.
3. Each of the five in-scope catch-alls catches only specific types; a protocol error closes the connection and anything else propagates.
3a. An authenticated text frame that is not parseable JSON, or that carries no `type`, closes the connection — while a frame with an unrecognised but well-formed `type` is still tolerated.
4. All three spec-mandated malformed fragment sequences close the connection.
5. A fragmented message cannot exceed 128 KiB before the first application message is surfaced, and large fragmented messages still work afterwards.
6. No new `client/goodbye` reason is introduced.
7. Full suite green on `net10.0`; `dotnet build` clean on `net8.0` and `net10.0`; the interop workflow still passes against `aiosendspin[server]==7.0.0`.
