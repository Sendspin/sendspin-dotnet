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
| Output delay | "Static delay" renamed to "output delay" across the C# surface (spec PR #164); the wire is unchanged | Medium — compiler errors only, see §8 for the full table |
| Output delay | `client/state` now always reports `static_delay_ms`, as an integer 0-5000 | Low — wire-only, unless you set a negative or fractional delay |
| Clock sync | `IClockSynchronizer` gains `ServerToClientTimeUncompensated` | Low — compiler error, one-line fix, and only for a custom synchronizer |
| Clock sync | Filter constants, burst cadence and timestamping now match the reference implementation | Low — behavioural, no code change; see §11 |
| Connection | `ISendspinConnection` gains `SendTimeMessageAsync`; `TextMessageReceived` carries `TextMessageReceivedEventArgs` | Low — compiler error, only for a custom connection or a raw event subscriber |
| `client/state` | Role objects follow `active_roles`; `ClientStateMessage.CreateInitial` takes payload objects | Low — compiler error only if you build the message yourself |
| Stream teardown | `stream/end` and `stream/clear` honour `roles`; their payloads lost `Reason`, `StreamId` and `TargetTimestamp` | Low — compiler error, and the removed members never carried a value |
| Undefined wire surface | `VisualizerTypes.Pitch` (and binary type 21), `PlayerStatePayload.BufferLevel`/`.Error`, and `AudioChunk.Slot` removed | Low — compiler error; none were spec-defined and nothing in the SDK populated them |
| Sync correction | `MaxSpeedCorrection` defaults to `0.005`; a larger configured value is **clamped** to it, with a warning | Medium — no compiler error, but correction is slower than you configured |
| Sync correction | `DeadbandMicroseconds` defaults to `100` µs, down from 1 ms | Medium — behavioural, no compiler error |
| Sync correction | New one-shot hard-sync tier above 5 ms, applied by the buffer on **both** read paths | Medium — behavioural, no compiler error |
| Sync correction | `Read` applies the sub-5 ms correction itself and holds `TargetPlaybackRate` at 1.0 | Medium — double-corrects if you also drive a resampler from that rate |
| Sync correction | `ITimedAudioBuffer.Read` is no longer `[Obsolete]` — it is the default path again | None — drop any `CS0618` suppression that existed to call it |
| Buffer capacity | `ClientCapabilities.BufferCapacity` is derived from the new `AudioBufferCapacityMs` instead of defaulting to a flat 32 MB | Medium — the server sends far less ahead unless you raise the duration |
| Buffer capacity | `TimedAudioBuffer`'s `bufferCapacityMs` parameter defaults to 30 s, up from 500 ms | Low — larger default allocation |
| Audio pipeline | `IAudioPipeline.StartAsync` returns `Task<AudioPipelineStartOutcome>` instead of `Task` | Low — compiler error, and only for a custom pipeline; see §14 |

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
| `ClockSynchronizer`, `OutputDelayStore` | no | Unchanged from 9.x |
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

## 8. Output delay

### "Static delay" is now "output delay" on the C# surface

Spec 168a677 (spec PR #164) renamed the player's static delay to **output delay**: `static_delay_ms` → `output_delay_ms`, `set_static_delay` → `set_output_delay`. The .NET surface follows the spec's vocabulary from 10.0.0 on. **The wire does not move with it.** No server has adopted the rename — `aiosendspin` still reads only the old names — so every byte this SDK sends is unchanged: `client/state` still carries `static_delay_ms`, and the `supported_commands` entry it advertises is still `set_static_delay`. Inbound, both spellings are accepted, and the post-rename one wins if a server sends both.

The rename is mechanical: every renamed member is a compiler error at your call site, and none of them changed behaviour.

| Was | Now |
|---|---|
| `IStaticDelayStore` | `IOutputDelayStore` |
| `IStaticDelayStore.Save(double staticDelayMs)` | `IOutputDelayStore.Save(double outputDelayMs)` |
| `SendspinClientOptions.StaticDelayStore` | `SendspinClientOptions.OutputDelayStore` |
| `IClockSynchronizer.StaticDelayMs` | `IClockSynchronizer.OutputDelayMs` |
| `KalmanClockSynchronizer.StaticDelayMs` | `KalmanClockSynchronizer.OutputDelayMs` |
| `ClientCapabilities.SupportsSetStaticDelay` | `ClientCapabilities.SupportsSetOutputDelay` |
| `ISendspinClient.SendPlayerStateAsync(int, bool, double? staticDelayMs)` | `…, double? outputDelayMs` |
| `SendspinHostService.SendPlayerStateAsync(int, bool, double? staticDelayMs, string?)` | `…, double? outputDelayMs, string?` |
| `PlayerStatePayload.StaticDelayMs` | `PlayerStatePayload.OutputDelayMs` — still `[JsonPropertyName("static_delay_ms")]` |
| `ClientStateMessage.CreatePlayerState(int, bool, int staticDelayMs, …)` | `…, int outputDelayMs, …` |

Two names deliberately keep the old spelling, because each is named for its own wire literal and both literals exist while servers migrate: `Commands.SetStaticDelay` (`"set_static_delay"`, alongside `Commands.SetOutputDelay`) and `PlayerCommand.StaticDelayMs` (`static_delay_ms`, alongside `PlayerCommand.OutputDelayMs`).

### `static_delay_ms` is reported as a spec-conformant integer

`client/state` now always carries `static_delay_ms`, projected onto the spec's wire type: an **integer in 0–5000**.

Three things changed, all on what goes out on the wire:

- **It is no longer omitted at zero.** The spec marks `static_delay_ms` REQUIRED for players, exactly like `required_lead_time_ms` and `min_buffer_ms`. Zero is its default, so it used to be missing from almost every player's initial state — and a server reads an absent value as "unchanged", which on the first message means it has no value at all.
- **It is an integer.** A fractional delay used to serialize as e.g. `12.5`. It is now rounded.
- **Negatives are clamped to 0.** The spec states negative values are not supported, and `aiosendspin` raises `ValueError` on parse rather than tolerating one — so a negative delay failed the connection.

`IClockSynchronizer.OutputDelayMs` is **unchanged**: still a `double`, still accepting −5000…5000. Negative values still schedule audio *later*, and that is still applied to playback. Only the report is constrained, and the SDK logs a warning naming both values when a configured delay does not survive the projection — because the server's group calibration is then working from a different number than your playback is.

`ClientStateMessage.CreateInitial` / `CreatePlayerState` take `int outputDelayMs` rather than `double`. Only relevant if you build these protocol messages yourself; project your own value onto 0–5000 first.

### `SendPlayerStateAsync`'s delay parameter is now nullable, and applies

```csharp
// ISendspinClient (dial path)
Task SendPlayerStateAsync(int volume, bool muted, double? outputDelayMs = null);   // was double = 0.0

// SendspinHostService (listen path)
Task SendPlayerStateAsync(int volume, bool muted, double? outputDelayMs = null, string? serverId = null);
```

Both facades carry the same signature and the same semantics.

**Omit it for volume and mute changes.** The old `0.0` default reported `static_delay_ms: 0` on every such call, and the spec requires the server to *merge* each `client/state`, "retaining the last value of any field that is absent" — so a present value overwrites. One volume change after the server set a 250 ms delay wiped it back to 0. The reported delay is now always the one actually applied, regardless of what you pass.

**Supplying a value is now a real update, not just a report.** It is written to `IClockSynchronizer.OutputDelayMs` *and* persisted through `IOutputDelayStore`, which is what the spec requires of a client-initiated change ("clients must persist `static_delay_ms` locally across reboots and server reconnections"). Previously the value was reported and nothing else: playback kept using the old delay, nothing was persisted, and the next reconnect silently reverted to it.

If you were calling the three-argument form purely to report a delay you had already applied yourself, it now also persists it — which is almost certainly what you wanted.

### A custom `IClockSynchronizer` needs a one-line update

The interface gains `ServerToClientTimeUncompensated(long)`, so a 9.x implementation fails with a compiler error (CS0535). It is `ServerToClientTime` without the output delay, and for most implementations that is the conversion they already had before subtracting it:

```csharp
public long ServerToClientTimeUncompensated(long serverTime) => serverTime - Offset;

public long ServerToClientTime(long serverTime) =>
    ServerToClientTimeUncompensated(serverTime) - (long)(OutputDelayMs * 1000);
```

Both exist because `static_delay_ms` belongs to the player role alone: it compensates for hardware past the audio port, so it applies to scheduling sound and not to the visualizer and artwork roles' display timestamps, which the spec translates with the clock offset alone. The SDK calls the uncompensated conversion for those, which is what keeps visuals with the audio on a device that has a delay configured.

---

## 9. `client/state` role objects follow the server's `active_roles`

A `player` object used to go out on every `client/state`, and a `source` object never did.

- **`player` is now sent only when the server activated the player role.** A state object for an inactive role is a client deviation the reference server rejects outright under `allow_noncompliant_clients=False`, so a source-only or artwork-only client was previously non-conformant on its very first message.
- **`source` is now built.** If your app calls `SetSourceSignalAsync` before the initial `client/state` goes out — a line-sense client sensing signal at boot — the signal is remembered and carried by that message instead of being discarded. A client that reports only *transitions* previously left the server never knowing there was signal until it changed. The remembered signal also survives reconnects, since it describes the input, not the session.

`ClientStateMessage.CreateInitial` now takes the role payloads rather than loose player fields, because which objects belong depends on `active_roles`, which the message type cannot see:

```csharp
ClientStateMessage.CreateInitial(
    available: true,
    player: new PlayerStatePayload { Volume = 42, Muted = false, /* ... */ },
    source: new SourceStatePayload { Signal = "present" });
```

Only relevant if you construct these messages yourself; both parameters default to null.

---

## 10. Stream teardown is per-role

`stream/end` and `stream/clear` carry an optional `roles` array, and the SDK now honours it.

Both payloads were modelled on fields the spec does not define — `StreamEndPayload.Reason`, `StreamEndPayload.StreamId`, `StreamClearPayload.StreamId`, `StreamClearPayload.TargetTimestamp` — so no server ever populated them. They are replaced by the two members the spec does define, `ServerTransmitted` and `Roles`.

**The behavioural half matters more than the compiler error.** A role-targeted `stream/end` is routine: whenever a `server/activate` drops a stream role from `active_roles`, the server ends that role's output first. Previously *any* `stream/end` stopped the audio pipeline and flipped the group to Idle, so deactivating the artwork role stopped playback on this client while every other client in the group kept going; likewise a `stream/clear` for the visualizer flushed buffered audio. Now the pipeline is touched only when `roles` is absent (meaning all streams) or names `player`.

Roles the SDK does not implement itself — `artwork`, `visualizer`, and the application-specific ones whose names start with `_` — are surfaced instead:

```csharp
client.StreamEndReceived += (_, payload) =>
{
    // payload.Roles == null means every active stream.
    if (payload.Roles is null || payload.Roles.Contains("artwork"))
        ClearArtwork();
};

client.StreamClearReceived += (_, payload) => { /* same shape */ };
```

Both events are also forwarded by `SendspinHostService`. Playback-only apps need no change: the audio pipeline is still driven by the SDK.

---

## 11. Clock synchronization matches the reference implementation

Interoperating with C++ and JS clients means more than speaking the same messages: a group stays in sync only if every member turns the same measurements into the same clock estimate. The SDK's filter ran the reference algebra with different constants, on a different schedule, over timestamps taken at different points — so a .NET player and a C++ player on one network predicted measurably different server clocks. All of that is now the reference's.

**Nothing to change in your code**, unless you supply your own `IClockSynchronizer` or `ISendspinConnection` (below) or override the filter constants yourself.

### Filter defaults

`KalmanClockSynchronizer`'s process noise now carries the reference's `Config` defaults, converted into this class's units (dt in seconds, drift in µs/s):

| Parameter | Was | Now | Reference |
|---|---|---|---|
| `processNoiseOffset` | `100.0` µs²/s | `0.0` | `process_std_dev = 0.0` — no offset random walk at all |
| `processNoiseDrift` | `1.0` (µs/s)²/s | `1e-4` | `drift_process_std_dev = 1e-11` per √µs |

The old constants inflated the filter's covariance permanently: the offset estimate tracked per-burst measurement noise instead of smoothing it, and the drift estimate gained 10 (µs/s)² of variance over every 10 s interval, so it forgot history as fast as it accumulated it and never settled. Expect a **quieter offset and a drift estimate that actually converges** — and, because `OffsetUncertainty` feeds `IsConverged`, slightly different convergence timing. The derivation from the reference's units is written out at the constants.

### Drift is applied through its significance gate

Both conversions — `ClientToServerTime` and the two `ServerToClientTime*` — now extrapolate with the drift estimate only once it clears the 2σ SNR test, and with zero drift before that, exactly as the reference's `effective_drift` does. `IsDriftReliable` reports that gate; it used to be a diagnostic that nothing acted on.

This matters most during startup. Two measurements bootstrap drift from a finite difference of two noisy offsets, which on a LAN is ~1000 µs/s of pure noise; applying it put roughly a millisecond of error into any timestamp extrapolated a second past the last update — the whole of the spec's accuracy budget — while a reference client in the same group extrapolated flat. The mapping stays linear in offset and drift either way, so the source role's inverse is as well-defined as before.

### Burst cadence

| Setting | Was | Now |
|---|---|---|
| Between probes in a burst | fixed 50 ms | none — the next probe follows the previous reply |
| Per-probe timeout | 2 s | 10 s (the reference's `DEFAULT_RESPONSE_TIMEOUT_MS`) |
| On a probe timeout | abandon the rest of the burst | advance to the next probe |
| Between bursts, converged | 1–10 s, by uncertainty | 10 s (the reference's `DEFAULT_BURST_INTERVAL_MS`) |
| Between bursts, converging | 500 ms for the first 3 measurements | 500 ms until the filter converges, for at most 60 bursts |

A burst exists to collect candidates so the cleanest can be chosen, so a single slow reply is the worst moment to stop collecting; it used to yield a one-sample burst. The one deliberate departure from the reference is the converging-window interval — see below.

### A player announces itself in about two seconds

The old adaptive interval switched to 10 s pacing after three measurements while convergence needs five, so on a *good* network — where uncertainty drops below a millisecond by the third burst — measurements four and five each arrived 10 s late. A player was invisible to the server (no initial `client/state`, no `available: true`) for over twenty seconds after every connect, and perversely appeared faster on a poor network. Keeping the fast tier until the filter actually converges brings that to roughly two seconds, in line with the C++ and JS clients. The spec's gate is unchanged: nothing is announced before the filter has converged.

The fast tier is a budget of 60 bursts, not a mode that lasts until convergence. On a link noisy enough that the sub-millisecond convergence gate is out of reach — offset uncertainty falls as the square root of the sample count, so a 100 ms round trip puts the gate thousands of measurements away — an unbounded tier would sustain 5-6 probes a second indefinitely from the client least able to afford the traffic. After the budget the interval widens to 10 s and one warning names the uncertainty it gave up at; the client keeps probing at that cadence and still reports `available` if the gate is eventually met. A reconnect, or a return from a pairing window, starts a fresh converging window.

### T1 and T4 are stamped at the transport boundary

`client_transmitted` is now stamped inside the connection's send path, after the send queue and immediately before the frame is written, and the client receive time is captured in the receive loop before the frame is decrypted and parsed. Previously both were taken in the client: T1 before serialization and encryption, T4 after deserialization. Both ends of the exchange were widened by that work, inflating the measured round trip — and with it `max_error` and the measurement variance — and biasing the offset by half of any asymmetry between the two.

Two interface changes fall out of this, both compile-time:

- **`ISendspinConnection.SendTimeMessageAsync(Action<long> onTransmitted, CancellationToken)`** is new. A custom connection implements it by stamping T1 inside whatever lock serializes its sends, invoking `onTransmitted` with it before the frame reaches the socket, and sending `ClientTimeMessage.Create(t1)`.
- **`TextMessageReceived` is `EventHandler<TextMessageReceivedEventArgs>`**, not `EventHandler<string>`. Read `e.Json` for the payload; `e.ReceivedAtMicroseconds` carries the transport's receive stamp. A subscriber updates one lambda; a custom connection raises the event with a timestamp taken before it decodes the frame.

```csharp
// ❌ BEFORE
connection.TextMessageReceived += (_, json) => Handle(json);

// ✅ AFTER
connection.TextMessageReceived += (_, e) => Handle(e.Json);
```

---

---

## 12. Sync correction now follows the spec's caps and the reference's strategy

Nothing here is a compiler error, and nothing here throws. All of it changes what you hear.

### The speed cap is enforced, not suggested

`SyncCorrectionOptions.MaxSpeedCorrection` defaulted to `0.02`, and `CliDefaults` to `0.04`. The
spec makes ±0.5% a MUST for continuous correction, measured as a sliding average over 150 ms
(`roles/player/v1.md:134`). The default is now `0.005` and `CliDefaults` matches it.

A larger configured value is **clamped where correction is applied**, not rejected — every
correction path uses `EffectiveMaxSpeedCorrection`, so an out-of-spec speed cannot be produced
whatever the configuration says. The SDK logs one warning per corrector when it sees one:

```csharp
// Still constructs, still plays. Corrects at 0.5%, and says so in the log.
var options = new SyncCorrectionOptions { MaxSpeedCorrection = 0.02 };
Console.WriteLine(options.MaxSpeedCorrection);          // 0.02  — what you asked for
Console.WriteLine(options.EffectiveMaxSpeedCorrection); // 0.005 — what is applied
Console.WriteLine(options.ExceedsSpecSpeedCap);         // true
```

If you read this from configuration — a `MaxSpeedCorrectionPercent` setting, say — clamp it at
the point of reading or drop the setting, so the log stops complaining and the value on screen
matches the behaviour. `Validate()` still rejects genuinely nonsensical values (zero, negative,
above 1.0).

The reasoning the old doc comment gave (pitch perception starts around 3%, so 2-4% is
inaudible) is not the reason the cap exists. Every player in a group must recover from the same
disturbance at the same bounded rate, or they audibly diverge while converging even though each
one sounds fine alone.

### Errors above 5 ms snap instead of grinding

Previously every error below the 500 ms re-anchor threshold was corrected continuously. At the
new cap a 400 ms error would take 80 seconds to close, during which the player trails the group.
There is now a one-shot tier — `HardSyncThresholdMicroseconds`, default 5 ms, matching
`HARD_SYNC_THRESHOLD_US` in the C++ reference — that skips or inserts the exact excess in a
single discontinuity. The spec both describes this (`roles/player/v1.md:178`) and exempts it
from the speed cap, on the condition that it stays rare; `AudioBufferStats.HardSyncCount`
reports how rare it is in practice.

The snap is applied by `TimedAudioBuffer` on **both** read paths, including `ReadRaw`. Skipping
buffered content or manufacturing silence is a buffer-timeline operation an external corrector
cannot perform on samples it has already been handed, so it cannot be delegated. **Stand down
while `ITimedAudioBuffer.IsHardSyncPending` is true** — rate 1.0, no stepping. Do not infer it
from `SyncCorrectionMode.HardSync`: that is a provider's forecast from the smoothed error, while
the buffer declines to snap when the raw and smoothed errors disagree in sign, when the raw error
is past the re-anchor ceiling, and inside its startup and reconnect windows. The two disagree in
both directions, and only the flag is the actor.

### `Read` corrects; `ReadRaw` reports

`Read` is **no longer `[Obsolete]`**. It implements the spec's full suggested strategy, so it is
the default path again and the one to reach for in a new player; if you suppressed `CS0618` to
call it, drop the suppression. `ReadRaw` is now documented as the advanced seam — for platforms
that own a smooth-correction mechanism the buffer cannot drive from the inside — rather than as
the path everyone must take. No behaviour changed with the attribute: both paths do exactly what
they did before.

The two read paths have a clean split, and which one you use decides who corrects.

`Read` corrects end to end: nothing below the dead band, whole-frame drop/duplicate at a
capped interval between the dead band and 5 ms, and a snap above that. It holds
`TargetPlaybackRate` at 1.0 throughout, because it is not asking anyone for anything. It
previously *advised* a rate in that middle band which nothing applied — the SDK has no
resampler on that path — so ordinary clock drift accumulated unopposed until the hard-sync tier
spliced it, roughly every hundred seconds at a realistic 50 ppm. If you drive a resampler from
`TargetPlaybackRateChanged` **and** call `Read`, stop doing one of the two: that is now a double
correction.

`ReadRaw` is unchanged in intent — you apply the continuous correction from an
`ISyncCorrectionProvider` — except that the buffer still performs the one-shot snap itself, as
described above.

### New (additive): `SyncCorrectedSampleSource` for smooth correction

Nothing to migrate — this is a component you may now opt into. It applies the same spec-fixed
ladder as `Read`, but realizes the continuous tier by trimming playback speed through a resampler
the SDK now carries (a vendored copy of NAudio's `WdlResampler`; see `THIRD-PARTY-NOTICES.md`)
rather than by dropping and duplicating whole frames. A ±0.5% speed change is inaudible where
frame stepping is faintly granular, so on any device with the cycles for it this is the better
mechanism.

```csharp
// In place of your own IAudioSampleSource:
sourceFactory: (buffer, nowMicroseconds) => new SyncCorrectedSampleSource(buffer, nowMicroseconds)
```

It drives `ReadRaw` internally and owns the whole external-correction protocol — provider updates,
`NotifyExternalCorrection`, `ReportExternalPlaybackRate`, standing down during a hard sync — so
there is no correction code left on your side. It implements `IPlaybackLifecycleAware`, so
`AudioPipeline` reaches it with the two events that invalidate correction state: `Clear` resets
the resampler and the provider, and `NotifyReconnect` suppresses corrections while the clock
re-converges. Implement that interface on your own source if it keeps state of its own; a source
that only reads the buffer has nothing to invalidate and should not implement it. If your host
must not carry a resampler in its output chain, set the new `SyncCorrectionOptions.Mechanism` to
`SyncCorrectionMechanism.FrameStepping` and the same class splices frames instead, constructing no
resampler at all. The default is `SmoothResampling`. `Mechanism` is the only addition to
`SyncCorrectionOptions`, and it changes nothing for `TimedAudioBuffer.Read`, which always steps
frames.

If you have already hand-rolled this composition against `ReadRaw`, read the new class before
keeping yours: it carries fixes for two artefacts the obvious implementation has (windowsSpin
issue #63). Bypassing the resampler when the rate returns to exactly 1.0 strands the input and
fractional read position it is holding, and re-entry clicks — reproduced here at nearly full scale
on a 0.5-amplitude sine. Padding a mid-callback shortfall with silence puts a bit-exact zero into
continuous audio, which is a broadband click rather than a gap; holding the last frame keeps the
waveform continuous, and only a callback that produced nothing at all should be silent.

### The dead band moved to 100 µs

`DeadbandMicroseconds` sat at 1 ms — exactly the spec's MUST floor, which left no margin under
it and made the ±0.5 ms SHOULD target unreachable by construction. It is now 100 µs, the spec's
suggested band and the reference's `SOFT_SYNC_THRESHOLD_US`. If your platform's timing jitter
genuinely needs a wider band, raise it deliberately and record the measurement that justifies
it.

### Content holes are detected

Chunk timestamps are now re-validated at every segment boundary during playback, not only
before it starts. A lost chunk or a mid-play discard used to shift every later sample earlier in
absolute time while the pace-based sync error read zero, so nothing corrected it. The step is
now folded into the sync error and closed. Chunks whose content the read cursor has already
passed are dropped on arrival, as `roles/player/v1.md:145` asks. Both are counted in
`AudioBufferStats.ContentHolesDetected` and `.LateChunksDropped`.

### Playback anchors to the schedule

`TimedAudioBuffer` now anchors its sync-error reference to the first segment's *scheduled* time
rather than to the callback that happened to start playback, and snaps the sub-callback residual
so the first sample lands on time. It also gates readiness on `MinBufferMilliseconds` (new,
default `PlayerBufferCapacity.DefaultMinBufferMilliseconds`, 150 ms) rather than on 80% of the
target depth, because a live stream is scheduled only `min_buffer_ms` ahead and the larger gate
guaranteed a late start on exactly those streams.

Driving the buffer through `AudioPipeline` leaves nothing to keep in step: the client forwards
`ClientCapabilities.MinBufferMs` to `IAudioPipeline.SetMinBufferMilliseconds` at construction and
again on every `UpdateTimingAsync`, so the gate always matches what the server was told. Set
`MinBufferMilliseconds` yourself only when driving a buffer outside a pipeline.

One-shot snaps taken at playback start now count in `AudioBufferStats.HardSyncCount`, which used
to stay at 0 for them. They are the same splice the hard-sync tier performs, and the spec requires
those to be rare — so if you alert on that counter, expect up to one more per stream start.

---

## 13. `buffer_capacity` is derived from the buffer you actually have

`ClientCapabilities.BufferCapacity` defaulted to a flat 32 MB with no relationship to the audio
buffer. The spec makes it a hard per-player byte limit that servers fill toward
(`roles/player/v1.md:34-35`), so that was a promise the client could not keep: a server behaving
exactly as allowed could send minutes of Opus to a client holding a fraction of a second of it,
and everything past the buffer was discarded before it played.

There is now one number to set:

```csharp
var capabilities = new ClientCapabilities { AudioBufferCapacityMs = 60_000 };

// The same number goes to the buffer. Both default to
// PlayerBufferCapacity.DefaultDecodedBufferMilliseconds (30 s), so leaving both alone is safe.
var buffer = new TimedAudioBuffer(format, clockSync, capabilities.AudioBufferCapacityMs);
```

`BufferCapacity` is derived from that duration and your advertised codecs, taking the byte rate
of the *most compressed* one (a megabyte of Opus is minutes; a megabyte of PCM is seconds) and
advertising four fifths of it, as the C++ reference does. Opus is assumed to occupy no more than
a conservative 64 kbps whatever `AudioFormat.Bitrate` says, because nothing negotiates that
field — `client/hello`'s `supported_formats` entry is codec, channels, sample rate and bit depth
— so a server encoding below the requested rate would otherwise fill the advertisement legally
with audio the buffer cannot hold. A *lower* declared bitrate is still honoured, since assuming
less can only advertise less. Setting `BufferCapacity` explicitly
still works and still overrides the derivation — but it makes the promise yours to keep;
`PlayerBufferCapacity.HoldableMilliseconds` will tell you whether it holds.

Expect the server to send less ahead than it used to. That is the fix, not a regression: raise
`AudioBufferCapacityMs` on both sides if you want a deeper queue.

---

### `ISyncCorrectionProvider` emits one currency: the rate

`DropEveryNFrames` and `InsertEveryNFrames` are **removed** from the interface. A provider now
reports its correction only as `TargetPlaybackRate`, in every continuous tier.

They were a second currency for the same decision, and which one a provider chose was read from
its *own* copy of `SyncCorrectionOptions.Mechanism` while the object that actually had (or did
not have) a resampler read the *buffer's* copy. A caller that paired a `SmoothResampling`
calculator with a `FrameStepping` host — or supplied any custom provider that emits a rate —
got a correction nothing applied, while the stats reported `Resampling`.

A provider cannot see the mechanism, so it no longer picks one. `SyncCorrectedSampleSource`
translates the rate into a drop/insert interval itself when it has no resampler; the two are the
same correction, because one frame in N is a speed change of 1/N.

```csharp
// Before
if (provider.CurrentMode == SyncCorrectionMode.Dropping)
{
    dropEveryN = provider.DropEveryNFrames;
}

// After
var deviation = provider.TargetPlaybackRate - 1.0;
if (deviation != 0)
{
    var everyN = (int)Math.Ceiling(1.0 / Math.Abs(deviation));   // drop if > 0, insert if < 0
}
```

`SyncCorrectionMode` is unchanged and still reports the tier; `Dropping` and `Inserting` now mean
"too far out to be worth trimming smoothly", not "use this mechanism".

Nothing to do if you use `SyncCorrectedSampleSource` or `TimedAudioBuffer.Read`.

### `NotifyExternalCorrection` no longer moves the read cursor

It records `SamplesDroppedForSync` / `SamplesInsertedForSync` and nothing else. `ReadRaw` already
credits every sample it hands over, and a corrector has to size its read to the correction —
dropping consumes an extra frame per splice, inserting one fewer — so the consumption was already
counted. Adjusting again counted the same frames twice and made the sync error converge at twice
the physical correction: it settled near zero while the player stayed about half the drift out of
the group. Size your read to the correction and this needs no compensation.

---

## 14. `IAudioPipeline.StartAsync` reports what it did

A `stream/start` for a stream that is already running is a configuration update rather than a
restart (§10 covers the teardown half of that rule), and how much of the decode chain a given
change forces the pipeline to rebuild is the pipeline's decision. It now reports that decision
instead of leaving callers to infer it:

```csharp
// Before
Task StartAsync(AudioFormat format, long? targetTimestamp = null, CancellationToken ct = default);

// After
Task<AudioPipelineStartOutcome> StartAsync(
    AudioFormat format, long? targetTimestamp = null, CancellationToken ct = default);
```

`AudioPipelineStartOutcome` is `Restarted`, `DecoderReplaced` or `FormatReannounced`. Only the
last one leaves audio encoded for the previous stream still decodable; the SDK's client uses this
to decide whether chunks queued before the message can be drained into the pipeline or have to be
dropped, which it previously worked out by reading `State` and `CurrentFormat`.

Nothing changes for a caller that awaits the result — `await pipeline.StartAsync(format)` compiles
and behaves as before. A **custom `IAudioPipeline`** has to return an outcome, which is a compiler
error until it does:

- Rebuilt the decode chain, or started cold → `Restarted`
- Replaced the decoder and kept the buffered audio and timeline → `DecoderReplaced`
- Re-announced the format already running and rebuilt nothing → `FormatReannounced`

---

## 15. Stream-lifecycle messages reach the pipeline one at a time

`stream/start`, `stream/end` and `stream/clear` are handled off the receive loop — the pipeline
calls they make open and close an output device, and the receive loop must not wait for that.
They are now dispatched on a per-client chain, so each handler runs only after the one dispatched
before it has finished. A track boundary's `stream/end` + `stream/start` can no longer take effect
in the reverse order and leave the pipeline stopped for a stream the server has started.

Nothing to do for a caller. For a **custom `IAudioPipeline`** it means the SDK's client no longer
issues an overlapping `StartAsync` / `StopAsync` / `Clear`, so an implementation needs no
reentrancy guard of its own for that caller. It still needs one for anything else that can reach
it — an app calling `SwitchDeviceAsync` or `DisposeAsync` from its own thread. `AudioPipeline`
uses a single lifecycle gate for all four, and after `DisposeAsync` its `StartAsync` and
`SwitchDeviceAsync` throw `ObjectDisposedException` rather than rebuilding a decode chain.

One behavioural note for a custom pipeline that drives an `IAudioDecoder`: `AudioPipeline` no
longer calls `IAudioDecoder.Reset()` from `Clear()`. A `stream/clear` defers the reset to the next
`ProcessAudioChunk`, because `Clear` runs on whichever thread delivered the message while the
receive loop may be inside `Decode` — and Concentus' Opus decoder is single-threaded. A re-anchor
does not reset the decoder at all: it discards audio the decoder has already produced and then
carries on with the next packet of the same stream, so a reset there would only manufacture a
transient the codec did not have.

---

## 16. Checklist

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
- [ ] No `MaxSpeedCorrection` above `SyncCorrectionOptions.SpecMaxSpeedCorrection` reaches the SDK — including from configuration; check the log for the clamp warning
- [ ] Nothing drives a resampler from `TargetPlaybackRate` while also calling `Read`
- [ ] `ClientCapabilities.AudioBufferCapacityMs` and `TimedAudioBuffer`'s `bufferCapacityMs` are the same number
- [ ] `TimedAudioBuffer.MinBufferMilliseconds` matches `ClientCapabilities.MinBufferMs` — automatic behind `AudioPipeline`; check it only if you drive a buffer yourself
- [ ] A custom `IAudioSampleSource` that keeps correction state implements `IPlaybackLifecycleAware`
- [ ] A custom `IAudioPipeline` returns an `AudioPipelineStartOutcome` from `StartAsync`
- [ ] A custom `ISyncCorrectionProvider` emits its correction as `TargetPlaybackRate` — the
      `DropEveryNFrames` / `InsertEveryNFrames` members are gone
- [ ] An external corrector stands down on `ITimedAudioBuffer.IsHardSyncPending`, not on
      `SyncCorrectionMode.HardSync`
- [ ] Nothing expects `NotifyExternalCorrection` to move the read cursor — it feeds the stats,
      and `ReadRaw` has already credited what it handed you

---

## Getting help

Issues and questions: https://github.com/Sendspin/sendspin-dotnet/issues
