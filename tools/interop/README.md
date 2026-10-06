# Live interop harness

Runs the .NET SDK against the pinned [`aiosendspin`](https://pypi.org/project/aiosendspin/)
10.0.0 release (the one [`.github/workflows/interop.yml`](../../.github/workflows/interop.yml)
installs) over a real WebSocket, exercising the encrypted protocol end to end.
This is the conformance gate that turns the loopback/known-answer-vector confidence into
real-server-verified confidence. `aiosendspin` 10.0.0 implements `Sendspin/spec` at
`1.0.0-rc1`, the tag the SDK is pinned to; see [docs/SPEC-VERSION.md](../../docs/SPEC-VERSION.md).

## Topology

The .NET SDK runs as a `SendspinHostService` (encrypted mode) listening on a local
port; the aiosendspin server dials it directly by URL (no mDNS, so it is hermetic in
CI). The server is always the Noise initiator regardless of who opened the socket, so
our host responds — the same path a real server-initiated deployment uses.

## Scenarios

- **`unpaired`** — the server dials for playback and our client is admitted over its
  [unpaired-access](https://github.com/Sendspin/spec) path (Sentinel PSK, trust
  `none`). Proves the Noise `KKpsk2` handshake, cipher-suite selection, prologue
  binding, transport encryption, and the `server/hello` → `client/hello` →
  `server/activate` flow against the reference.
- **`pairing`** — a Pairing PSK pairing attempt with a shared bootstrap secret. Proves
  the full pairing round-trip: handshake on the Pairing PSK →
  `server/activate(pairing)` → `client/pair-finalize` → `server/pair-finalize` →
  the long-term record persists on **both** sides → the server re-handshakes to the new
  PSK, promoting the client to `user` trust.
- **`static-pin`** — a `static_pairing_code` attempt with an 8-digit code both sides
  already know. Proves the CPace round and PSK wrapping, and that the client gesture-gated
  the attempt: the server side fails the run unless it saw `client/pair-pending` first.
- **`dynamic-pin`** — a `dynamic_pairing_code` attempt. The client derives a fresh code
  during the attempt and presents it; the harness reads it off the client's output and
  feeds it to the server, as an operator would type it in. Proves the `digits` emission
  format and the same CPace round with a code that was never shared up front.
- **`source`** — pairs, then the server asks the client to stream its `source@v1` input
  and decodes the chunks that arrive. Proves the server's start command, the client's
  `client-stream/start`, and real audio over the wire.
- **`player`** — pairs, waits for the client to report itself available, then pushes two
  seconds of PCM at the `player@v1` role. Proves `stream/start`, `stream/end`, and the
  13-byte audio chunk header: the client must decode exactly the samples the server sent
  and see a non-zero `send_ahead`. Also fails if the server logs the client's initial
  `client/state` as late (aiosendspin allows 5 s), and prints how long it took.

## Running locally

```bash
# Mirror .github/workflows/interop.yml: install the pinned aiosendspin release, then run
# a scenario.
pip install "aiosendspin[server]==10.0.0"
bash tools/interop/run.sh unpaired
bash tools/interop/run.sh pairing
bash tools/interop/run.sh static-pin
bash tools/interop/run.sh dynamic-pin
bash tools/interop/run.sh source
bash tools/interop/run.sh player
```

Use a different Python via `PYTHON=/path/to/python bash tools/interop/run.sh …`.

## Files

- `run.sh` — orchestrator: starts the .NET host, dials it from the server, checks both
  sides report success.
- `InteropClient/` — the .NET host process (prints JSON event lines; exit code is the
  verdict).
- `server.py` — the aiosendspin reference-server side.

## Not yet covered

- The visualizer and controller roles have no scenario.
- No scenario pushes an artwork image. The client advertises `artwork@v1` and the server
  activates it in the paired scenarios, but the announce/part/cancel transfer itself is
  covered only by unit tests.
- `client/pair-retry`: `dynamic-pin` enters the right code first time.
- Pairing alongside playback (`['playback', 'pairing']`).
