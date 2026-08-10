# Sendspin.SDK 10.0.0 Migration Guide

## Overview

Version 10.0.0 makes the transport encrypted end to end. Every connection now runs a Noise `KKpsk2` handshake, clients carry a persistent cryptographic identity, and servers are authenticated by a pre-shared key established through pairing.

**Why this matters**: before 10.0.0 the protocol was plaintext on the local network. Anyone on the same LAN could read metadata and audio, impersonate a server, or issue commands to a player. Encryption closes that, but it cannot be added transparently — both peers must speak it, so this is a hard break with no downgrade path.

**Server requirement**: a 10.x client requires a server speaking the encrypted protocol — `aiosendspin >= 7.0.0`. There is no negotiation and no fallback: against an older server the handshake fails. **The 9.x line remains maintained** for deployments that need to talk to those servers.

---

## Breaking Changes Summary

| Area | Change | Impact |
|------|--------|--------|
| Transport | Plaintext removed; Noise `KKpsk2` always | **High** — server must be `aiosendspin >= 7.0.0` |
| Client identity | New required persistent Curve25519 identity | **High** — silent data loss if unpersisted |
| Construction | `SendspinClientOptions` + `CreateForDial(...)` | **High** — every call site |
| Pairing | New: Pairing PSK, dynamic PIN, static PIN | Medium — new UX surface |
| `client/state` | `available` is a boolean, not a state string | Medium |
| Roles | New `source@v1` (line-in / microphone) | None unless adopted |

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
- **Dynamic PIN** — the client derives a per-session PIN and displays it; the operator types it into the server. Requires both `PinLockoutStore` and `PresentPinAsync`; without either, the SDK refuses to offer the method rather than fail open.
- **Static PIN** — a fixed 8-digit device PIN. Requires `PinLockoutStore`.

Enable the PIN methods through `ClientCapabilities.PinPairingMethods`.

### Unpaired access

`ClientCapabilities.UnpairedAccessEnabled` lets a server play to the client with no pairing record at all. It defaults to off, and it should stay off unless you have a reason: the Sentinel PSK is a published constant, so an unpaired session offers confidentiality against passive observers but **no protection against an active man-in-the-middle** — neither peer's identity is bound to anything.

### Persist what `PairingConfigChanged` reports

A paired server can read and change the client's pairing configuration through `management/*`. The SDK applies those changes to its own state and raises `PairingConfigChanged` — it deliberately does **not** write to the `ClientCapabilities` instance your app owns.

If you do not persist what that event reports and reapply it at startup, a server's configuration changes silently revert on the next launch. Note that only some of the reported values currently have a `ClientCapabilities` counterpart to reapply them to; see the event's documentation for which.

---

## 4. `client/state` reports `available` as a boolean

The legacy state string is gone. `available` is now a single boolean composed from every input that determines whether this client can take part in playback right now — clock sync established, no outstanding pipeline error, and not held by an external source.

Consumers reading the raw protocol should expect `"available": true|false`. Consumers using the SDK's own API are unaffected.

---

## 5. New role: `source@v1`

Clients can now act as an audio **source** (line-in, microphone) rather than only a sink. Supply a `CaptureDevice`, add `"source@v1"` to `ClientCapabilities.Roles`, and the server can start and stop capture.

The spec requires a source to run only on a paired connection, and the SDK enforces that at the point the capture device opens: it refuses to stream unless the session is at trust `user` with the source role active. Streaming is always server-initiated; a source never streams unsolicited.

---

## 6. Checklist

- [ ] Server is `aiosendspin >= 7.0.0`, or stay on the 9.x line
- [ ] `Identity` comes from a **store**, not `Generate()` — verify by restarting the app twice and confirming the pairing survives
- [ ] The same identity and pairing store are shared across dial and listen modes
- [ ] `PairingRecordStore` is configured and writes somewhere durable
- [ ] A pairing UX exists — at minimum, surfacing the token from `EnsurePairingPsk()`
- [ ] `UnpairedAccessEnabled` is a deliberate decision, not a default you inherited
- [ ] `PairingConfigChanged` is persisted and reapplied at startup
- [ ] Identity and PSK files are in a user-scoped location

---

## Getting help

Issues and questions: https://github.com/Sendspin/sendspin-dotnet/issues
