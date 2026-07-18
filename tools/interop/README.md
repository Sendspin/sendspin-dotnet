# Live interop harness

Runs the .NET SDK against the [`aiosendspin`](https://pypi.org/project/aiosendspin/)
**7.0.0** reference server over a real WebSocket, exercising the encrypted protocol
end to end. This is the conformance gate that turns the loopback/known-answer-vector
confidence into real-server-verified confidence.

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

## Running locally

```bash
pip install "aiosendspin[server]==7.0.0"
bash tools/interop/run.sh unpaired
bash tools/interop/run.sh pairing
```

Use a different Python via `PYTHON=/path/to/python bash tools/interop/run.sh …`.

## Files

- `run.sh` — orchestrator: starts the .NET host, dials it from the server, checks both
  sides report success.
- `InteropClient/` — the .NET host process (prints JSON event lines; exit code is the
  verdict).
- `server.py` — the aiosendspin reference-server side.

## Not yet covered

PIN pairing (dynamic/static) and the management family have full unit + KAT coverage;
adding them here is tracked in the interop follow-ups. PIN pairing needs a `PinProvider`
on the server side to feed the operator-entered PIN.
