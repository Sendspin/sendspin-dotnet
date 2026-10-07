# Spec version

Sendspin.SDK **10.0.0** implements [`Sendspin/spec`](https://github.com/Sendspin/spec) at tag
**`1.0.0-rc1`** — commit [`671a34d`](https://github.com/Sendspin/spec/commit/671a34d), tagged
2026-09-17.

`1.0.0-rc1` is a release candidate, not the final 1.0. When 1.0 is tagged, diff
`1.0.0-rc1...<1.0 tag>`, give each change the treatment the rc1 changes got, and move this pin.

This file is the record of which spec the SDK targets, which server it is tested against, how
that is checked, and where the SDK is known to differ. It is not a claim of full conformance:
the list under [Known deviations](#known-deviations) is what is known, and the checks under
[How conformance is checked](#how-conformance-is-checked) cover only what they say they cover.

## Compatibility floor

**`aiosendspin >= 10.0.0`.** That release (2026-10-05) is the reference server's implementation
of `1.0.0-rc1`, and it is the release Music Assistant ships.

- There is one floor, and it applies to connecting as much as to pairing. A 10.x client
  speaks only the rc1 wire: every connection is encrypted, and there is no downgrade
  negotiation and no fallback to an older framing.
- An older server needs the SDK's 9.x line, which stays maintained. The two lines can share a
  server: `aiosendspin` 10.0.0 treats a client as legacy — the old 9-byte audio header and the
  old fragment framing — when its `client/hello` is unencrypted, or carries `trust_level`, or
  carries `supported_commands` in `player@v1_support`. SDK 10 sends none of the three; the
  9.3.x line does, and keeps working against the same server on that legacy path.

## How conformance is checked

The [Interop workflow](../.github/workflows/interop.yml) installs
`aiosendspin[server]==10.0.0` and runs the SDK against it over a real WebSocket on every push
and pull request to `main`. It runs six scenarios (see
[`tools/interop`](../tools/interop/README.md)):

| Scenario | What it exercises |
|---|---|
| `unpaired` | Noise `KKpsk2` handshake on the Sentinel PSK, cipher-suite selection, `server/hello` → `client/hello` → `server/activate`, admission over unpaired access |
| `pairing` | Pairing PSK attempt, the long-term record persisting on both sides, the in-band re-handshake onto the new PSK |
| `static-pin` | `static_pairing_code`: the CPace round, PSK wrapping, and the gesture gate (`client/pair-pending` must come first) |
| `dynamic-pin` | `dynamic_pairing_code` with the `digits` format: the client derives and emits the code, the harness types it into the server |
| `source` | `source@v1`: the server's `start` command, `client-stream/start`, and decoded audio arriving at the server |
| `player` | `player@v1`: `stream/start`, the 13-byte audio chunk header with `send_ahead`, `stream/end`, every sample sent coming out of the client's decoder, and the initial `client/state` arriving inside the server's 5 s limit |

The pin moves deliberately, with each `aiosendspin` release: a release is where a spec wire
change first becomes observable.

Everything else is covered by unit tests only. In particular, nothing in the live scenarios
exercises:

- an artwork image transfer. The server activates `artwork@v1` in the paired scenarios but no
  scenario pushes an image; the parser is tied to the reference server by a known-answer test
  built from `aiosendspin` 10.0.0's own announce/part/cancel packers.
- the visualizer and controller roles.
- `client/pair-retry` (a mistyped dynamic pairing code).
- pairing alongside playback (`['playback', 'pairing']`).

## Known deviations

### Not implemented (optional in the spec)

- **`client/leave`.** A client MAY send it to leave its group while it does something else. The
  SDK never sends it and has no API for it.
- **The `qr_code` emission format.** `dynamic_pairing_code` is offered with `formats: ['digits']`
  only. The spec suggests a client whose display can render a QR code also offer `qr_code`.
- **The `speaker` out-channel.** A `"speaker"` entry in
  `ClientCapabilities.PairingCodeOutChannels` is dropped from the advertised `out_channels`,
  with a warning, and `dynamic_pairing_code` is withheld if that leaves no channel. The spec
  allows `'display' | 'speaker'`. The value is an informational hint, so nothing about pairing
  depends on it.

### Stricter than the spec

- **The dynamic pairing code hold-back starts at 10 rounds, not 20.** The spec's round limit is
  20 rounds since the last verified `server_kc`, and it allows a client to hold attempts back
  earlier. The SDK does so at 10, counted from the moment a round's code is emitted.
- **An operator gesture does not reset the round count.** The spec has the deliberate operator
  action that releases a held-back attempt also reset the count. In the SDK only a verified
  `server_kc` resets it, so once the method is held back every attempt needs an open
  `PairingWindow`, and a failed round aborts rather than retries, until one pairing succeeds.

### Differs from the spec

- **Buffered audio is not kept across every in-place format change.** On a `stream/start`
  that changes the format of a running stream the spec says a player MUST keep buffered chunks
  and decode each chunk in the format in effect when it was received. `AudioPipeline` does so
  for a sample-rate or channel change by playing the old buffer out before it re-opens the
  output, with three exceptions:
  - a second rate or channel change that arrives before the first has switched takes the
    restart path, and the audio still buffered is discarded;
  - if the output stops reading for more than a second past the old audio's duration, the
    switch goes ahead and what was left of that audio is dropped;
  - a chunk that reaches the pipeline between the `stream/start` and the decoder swap is
    decoded by the old decoder. The window opens only when the stream-lifecycle chain or the
    pipeline is busy, and it exists for a codec or bit-depth change too.
- **Multi-server arbitration ranks `['playback', 'pairing']` as playback.** The spec's rule that
  a pairing attempt is not displaced by an incoming connection is applied only to a connection
  whose activity is pairing alone. An attempt running alongside playback can be displaced by an
  incoming playback connection. This affects `SendspinHostService` only.
- **Output suspension during a dynamic pairing attempt is left to the application.** Where the
  pairing code's out-channel is also a role's output — the display showing artwork, the speaker
  playing the stream — the spec has the client suspend that output for the attempt. The SDK
  keeps delivering audio, artwork and visualizer data; the application decides what to show or
  play while `PresentPairingCodeAsync` is presenting the code.
- **Reserved binary IDs are ignored from the start of transport mode.** The spec's ignore rules
  apply once the client has received the initial `server/activate`. The SDK's framing layer
  drops binary IDs 2 and 3 at any point after the handshake. A server may not send them before
  activation, so the difference is not observable against a conformant one.
- **A `stream/clear` that names `artwork` discards that role's pending images.** The spec's
  `roles` for `stream/clear` are `player` and `visualizer`, so a conformant server does not send
  it. With `roles` omitted, artwork is left alone as the spec says.
- **A role version replacement is not treated as a removal.** Active roles are compared by
  family (`player`, not `player@v1`). Every role has only a `v1`, so no replacement can occur on
  the rc1 wire.

### Stricter than the reference server

Not deviations, but configurations `aiosendspin` 10.0.0 accepts and the SDK rejects at
construction, because the spec is the record: a player that lists neither `flac` nor `pcm` in
`supported_formats`, and a `mac_address` that is not lowercase colon-separated.
