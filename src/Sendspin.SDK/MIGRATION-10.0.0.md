# Sendspin.SDK 10.0.0 Migration Guide

## Overview

Version 10.0.0 makes the transport encrypted end to end. Every connection now runs a Noise `KKpsk2` handshake, clients carry a persistent cryptographic identity, and servers are authenticated by a pre-shared key established through pairing.

**Why this matters**: before 10.0.0 the protocol was plaintext on the local network. Anyone on the same LAN could read metadata and audio, impersonate a server, or issue commands to a player. Encryption closes that, but it cannot be added transparently — both peers must speak it, so this is a hard break with no downgrade path.

**Server requirement**: a 10.x client requires a server speaking the encrypted protocol — `aiosendspin >= 7.0.0`. There is no negotiation and no fallback: against an older server the handshake fails. **The 9.x line remains maintained** for deployments that need to talk to those servers.

**Pairing requires `aiosendspin >= 9.0.0`**, a higher floor than connecting. 9.0.0 is the first release carrying the current pairing wire shape: `server/activate` names the chosen method inside a `pairing` object, with `pin_length` alongside it, rather than in a flat `selected_pair_method` field. 7.0.0 and 8.0.0 still send the old shape, so a 10.x client reads the offered method as absent and refuses every pairing attempt with `pair/abort` reason `method_not_supported`. Connecting and playback — including unpaired access — are unaffected and still work against `>= 7.0.0`.

---

## Breaking Changes Summary

| Area | Change | Impact |
|------|--------|--------|
| Transport | Plaintext removed; Noise `KKpsk2` always | **High** — server must be `aiosendspin >= 7.0.0`, and `>= 9.0.0` to pair |
| Client identity | New required persistent Curve25519 identity | **High** — silent data loss if unpersisted |
| Construction | `SendspinClientOptions` + `CreateForDial(...)` | **High** — every call site |
| Pairing | New: Pairing PSK, dynamic PIN, static PIN | Medium — new UX surface |
| Pairing gestures | PIN pairing can require an open `PairingWindow` | **High** if a PIN method is offered — silently never pairs without one |
| `client/state` | `available` is a boolean, not a state string | Medium |
| Roles | New `source@v1` (line-in / microphone) | None unless adopted |
| Record store | `IPairingRecordStore.Upsert` returns `bool` | Low — compiler error, one-line fix |
| Visualizer | `RequestVisualizerFormatAsync` lost its `bufferCapacity` parameter | Low — compiler error only if passed positionally |

---

## 1. The identity must persist — read this one first

This is the only change that **fails silently**. Everything else surfaces as a compiler error or an immediate handshake failure; this one compiles, runs, connects, and quietly discards every pairing the user has made whenever the app restarts.

A client's `client_id` **is** its public key, and the spec requires it to survive reboots. The SDK cannot choose a storage location for you, so you supply the store.

```csharp
// ❌ WRONG — compiles, runs, and loses every pairing on restart.
// A fresh keypair every launch means a new client_id, so the server sees an
// unknown client and the pairing records on both sides no longer match.
var options = new SendspinClientOptions { Identity = SendspinIdentity.Generate() };

// ✅ RIGHT — load if present, generate and persist on first run.
ISendspinIdentityStore store = new FileSendspinIdentityStore(
    Path.Combine(appDataDirectory, "identity.bin"));
var options = new SendspinClientOptions { Identity = SendspinIdentity.FromStore(store) };
```

`SendspinIdentity.FromStore` does the load-or-create-and-save in one call. `Generate()` exists for tests and for embedders implementing their own persistence around it.

On Windows the shipped file store inherits its parent directory's ACL, so place it somewhere already user-scoped such as `%LOCALAPPDATA%`. For hardware-backed protection (DPAPI, Keychain, Android keystore) implement `ISendspinIdentityStore` yourself — the blob is opaque, so an implementation only stores and returns bytes and the raw private key never leaves the SDK.

### The identity and the pairing store must be shared across connection modes

If your app both dials servers and listens for server-initiated connections, both paths must be given the **same** identity and the **same** `IPairingRecordStore`. Giving each mode its own means a pairing completed in one mode is invisible to the other, and the user re-pairs every time they switch.

### A custom `IPairingRecordStore` implementation needs a one-line update

`Upsert` now returns `bool` instead of `void`, so a 9.x implementation fails with a compiler error (CS0535). Return `true` unless your store enforces a capacity limit: `false` means "refused because the store is full," which the SDK uses to answer `storage_exhausted` instead of silently claiming a pairing that was never persisted. A failure of the underlying medium (disk full, permission revoked) is a fault, not exhaustion — throw for that, as before.

---

## 2. Construction moved to an options object

In 9.x you constructed the client directly and handed it a connection you had built
yourself. In 10.0.0 the connection and the Noise session must be wired to the same framing
instance or they drift apart, so a factory does it for you.

```csharp
// ❌ BEFORE (9.x) — you built the connection and passed it in
var connection = new SendspinConnection(logger, connectionOptions);
var client = new SendspinClientService(
    logger, connection, clockSynchronizer, capabilities /* , ... */);

// ✅ AFTER (10.0.0)
var client = SendspinClientService.CreateForDial(
    loggerFactory,
    new SendspinClientOptions
    {
        Identity = SendspinIdentity.FromStore(identityStore),   // required
        PairingRecordStore = pairingStore,                      // required to pair
        Capabilities = capabilities,
        AudioPipeline = audioPipeline,
    });
```

`SendspinClientOptions` members:

| Member | Required | Purpose |
|---|---|---|
| `Identity` | **yes** | The persistent client identity (§1) |
| `PairingRecordStore` | to pair | Where pairing records (PSKs) live |
| `Capabilities` | no | Roles and per-role support, as in 9.x |
| `AudioPipeline` | for playback | Unchanged from 9.x |
| `Suite` | no | Noise cipher suite; defaults to ChaCha20-Poly1305 |
| `ClockSynchronizer`, `StaticDelayStore` | no | Unchanged from 9.x |
| `PinLockoutStore`, `PresentPinAsync` | for PIN pairing | See §3 |
| `CaptureDevice`, `SourceEncoderFactory` | for `source@v1` | See §5 |

---

## 3. Pairing

An unpaired client connects under the published **Sentinel PSK**, which authenticates nothing — the session's trust level is `none`. To reach trust `user` (and therefore playback on most servers, plus any management operation), the client must pair.

Three methods, all optional to offer except the first:

- **Pairing PSK** — every client implements it. The client surfaces a *pairing token* (an `SP:`-prefixed string) that the operator transfers to the server. Get it from `EnsurePairingPsk()`.
- **Dynamic PIN** — the client derives a per-session PIN and displays it; the operator types it into the server. Requires `PinLockoutStore`, `PresentPinAsync`, and a `PairingRecordStore`; without any of them, the SDK refuses to offer the method rather than fail open.
- **Static PIN** — a fixed 8-digit device PIN. Requires `PinLockoutStore`, a `PairingRecordStore`, **and a `PairingWindow`** (below).

Enable the PIN methods through `ClientCapabilities.PinPairingMethods`.

**Every pair method needs a `PairingRecordStore`, including the PIN methods.** Without one the exchange runs to completion and the *server* writes a long-term record while the client stores nothing — so the client fails to authenticate on its very next connection, having told your app that pairing succeeded. The SDK therefore withholds a method it cannot complete: an unrunnable method is absent from `supported_pair_methods` in `client/hello`, is reported `enabled: false` by `management/get-pairing-config`, and any activation for it is answered `method_not_supported` with the connection left open.

This is the same discipline `pairing_psk` has always had. **It is silent when you get it wrong** — nothing throws; the method simply never appears. If a PIN method you configured is not being offered, check that `PairingRecordStore`, `PinLockoutStore`, and (for `dynamic_pin`) `PresentPinAsync` are all set.

`PresentPinAsync` is `Func<PinPresentation, CancellationToken, ValueTask>`: the argument carries the derived `Pin` **and** the server's `Languages` hint, rather than being a bare PIN string. Read `presentation.Pin` for the digits; match `presentation.Languages` (BCP 47, most-preferred first, possibly null) against the languages your app can actually speak when you announce the PIN aloud. The hint is informational — emitting in another language is never a protocol error.

### A `PairingWindow` is required for the gesture-gated methods

The spec gates some PIN attempts on a deliberate operator gesture, and the SDK will not complete those attempts without one. Gated attempts are:

- **every `static_pin` attempt**;
- a `dynamic_pin` attempt once the method has **escalated** (10 recorded failures) — escalation replaces the terminal lockout earlier 10.0.0 pre-releases applied, so a method that used to become permanently unusable now becomes gesture-gated instead, and a success resets it;
- a `dynamic_pin` attempt whose session PIN is **shorter than 6 digits** — short PINs are bought with a gesture.

The window is a property of the **device**, not of a connection: one instance is shared by every connection, and it admits exactly one attempt per opening no matter how many servers are connected.

```csharp
var window = new PairingWindow();   // one per device — share it across every connection

var options = new SendspinClientOptions
{
    Identity = identity,
    PinLockoutStore = lockouts,
    PairingWindow = window,         // omitted, every gated attempt waits forever
    // ...
};

// Wire it to a deliberate operator gesture: a button, a reset pinhole, a power-cycle pattern.
pairingButton.Pressed += (_, _) => window.Open();
```

**Leaving `PairingWindow` null does not fail loudly.** It defaults to null and a null window reads as permanently closed, which is the fail-closed direction: the client answers a gated activation with `client/pair-pending` and then waits. Nothing throws and nothing times out — pairing simply never completes. Subscribe to `ISendspinClient.PairingGestureRequested` to prompt the operator, and pass the same window to `SendspinHostService` (which forwards it to every connection it accepts).

An already-paired server can also open the window remotely with `management/open-pairing-window`.

Once an attempt has started it is bounded by `SendspinClientOptions.PairingAttemptTimeout` (2 minutes by default, the spec's recommendation), after which the client sends `pair/abort` with `attempt_timeout`. The wait for a gesture is not bounded by it.

### Unpaired access

`ClientCapabilities.UnpairedAccessEnabled` lets a server play to the client with no pairing record at all. It defaults to off, and it should stay off unless you have a reason: the Sentinel PSK is a published constant, so an unpaired session offers confidentiality against passive observers but **no protection against an active man-in-the-middle** — neither peer's identity is bound to anything.

### Persist what `PairingConfigChanged` reports

A paired server can read and change the client's pairing configuration through `management/*`. The SDK applies those changes to its own state and raises `PairingConfigChanged` — it deliberately does **not** write to the `ClientCapabilities` instance your app owns.

If you do not persist what that event reports and reapply it at startup, a server's configuration changes silently revert on the next launch. Every setting the event reports has a `ClientCapabilities` property to reapply it to; see `ISendspinClient.PairingConfigChanged`'s documentation for the full list.

---

## 4. `client/state` reports `available` as a boolean

The legacy state string is gone. `available` is now a single boolean composed from every input that determines whether this client can take part in playback right now — clock sync established, no outstanding pipeline error, and not held by an external source.

Consumers reading the raw protocol should expect `"available": true|false`. Consumers using the SDK's own API are unaffected.

---

## 5. New role: `source@v1`

Clients can now act as an audio **source** (line-in, microphone) rather than only a sink. Supply a `CaptureDevice`, add `"source@v1"` to `ClientCapabilities.Roles`, and the server can start and stop capture.

The spec requires a source to run only on a paired connection, and the SDK enforces that at the point the capture device opens: it refuses to stream unless the session is at trust `user` with the source role active. Streaming is always server-initiated; a source never streams unsolicited.

---

## 6. Visualizer buffer capacity is announced, not renegotiated

`RequestVisualizerFormatAsync` no longer takes `bufferCapacity`, and `VisualizerRequestFormat.BufferCapacity` is gone.

`buffer_capacity` is a `visualizer@v1_support` field of `client/hello`; the spec's `stream/request-format` visualizer object carries only `types`, `rate_max` and `spectrum`. Sending it there is a client deviation that `aiosendspin` names explicitly, and a server running `allow_noncompliant_clients=False` rejects the connection rather than ignoring the field.

Set it once, before connecting:

```csharp
Capabilities = new ClientCapabilities
{
    Roles = { "visualizer@v1" },
    VisualizerSupport = new VisualizerSupport { BufferCapacity = 65536, RateMax = 30, /* ... */ },
}
```

Callers using named arguments — the shape the docs have always shown — are unaffected. Only a positional call breaks, and it breaks at compile time.

---

## 7. PIN presentation grouping

New: `PinPresentation.Groups` splits the PIN into the groups the spec recommends for display and for spoken emission (`123456` → `123 456`; an 8-digit static PIN → `1234 5678`). `Pin` is unchanged and remains the contiguous digits.

Grouping is presentation-only. Separators never enter PIN derivation, operator entry, or the `PRS` transcript, so join `Groups` with whatever separator suits the surface, and strip separators from anything typed back in.

```csharp
PresentPinAsync = (presentation, ct) =>
{
    ShowPin(string.Join(" ", presentation.Groups));   // was: presentation.Pin
    return ValueTask.CompletedTask;
};
```

Optional — existing code reading `presentation.Pin` keeps working and simply shows an ungrouped PIN.

---

## 8. Checklist

- [ ] Server is `aiosendspin >= 7.0.0`, or stay on the 9.x line
- [ ] Server is `aiosendspin >= 9.0.0` if you need to pair — 7.0.0 and 8.0.0 refuse every pairing attempt
- [ ] `Identity` comes from a **store**, not `Generate()` — verify by restarting the app twice and confirming the pairing survives
- [ ] The same identity and pairing store are shared across dial and listen modes
- [ ] `PairingRecordStore` is configured and writes somewhere durable
- [ ] A pairing UX exists — at minimum, surfacing the token from `EnsurePairingPsk()`
- [ ] If any PIN method is offered: a `PairingWindow` is supplied and opened by a real operator gesture — verify by pairing with a static PIN and confirming it only succeeds after the gesture
- [ ] `UnpairedAccessEnabled` is a deliberate decision, not a default you inherited
- [ ] `PairingConfigChanged` is persisted and reapplied at startup
- [ ] Identity and PSK files are in a user-scoped location

---

## Getting help

Issues and questions: https://github.com/Sendspin/sendspin-dotnet/issues
