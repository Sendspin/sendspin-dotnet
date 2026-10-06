#!/usr/bin/env bash
# Live interop harness: runs the .NET SDK host against the aiosendspin reference server
# pinned in .github/workflows/interop.yml (10.0.0) for one scenario. Starts the .NET host (it prints its listening port), then
# dials it from the Python server, and checks both sides report success.
#
# Usage: run.sh <scenario>   scenario: unpaired | pairing | static-pin | dynamic-pin | source | player
# Env:   PYTHON  - python interpreter with aiosendspin[server] at that pin (default: python3)
set -euo pipefail

SCENARIO="${1:-unpaired}"
HERE="$(cd "$(dirname "$0")" && pwd)"
PYTHON="${PYTHON:-python3}"
PORT=8931

# The shared secret both sides need, per scenario: a random Pairing PSK, or a static PIN
# (the spec fixes static PINs at 8 digits). A dynamic code is not shared up front: the client
# derives it during the attempt, so there is nothing to hand either side here.
case "$SCENARIO" in
  pairing|source|player) SECRET="$(head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n')" ;;
  static-pin)            SECRET="31415926" ;;
  *)                     SECRET="" ;;
esac

pass() { echo "INTEROP PASS: $SCENARIO"; exit 0; }
fail() { echo "INTEROP FAIL: $SCENARIO — $1" >&2; exit 1; }

client_args=("$SCENARIO" "$PORT")
[ -n "$SECRET" ] && client_args+=("$SECRET")

# Start the .NET host in the background; capture its output.
CLIENT_OUT="$(mktemp)"
dotnet run --project "$HERE/InteropClient" -c Release -- "${client_args[@]}" >"$CLIENT_OUT" 2>&1 &
CLIENT_PID=$!
trap 'kill $CLIENT_PID 2>/dev/null || true; rm -f "$CLIENT_OUT" "${SERVER_OUT:-}"' EXIT

# Wait for the host to report ready.
for _ in $(seq 1 60); do
  grep -q '"event":"host_ready"' "$CLIENT_OUT" 2>/dev/null && break
  kill -0 $CLIENT_PID 2>/dev/null || { cat "$CLIENT_OUT"; fail "host exited before ready"; }
  sleep 1
done
grep -q '"event":"host_ready"' "$CLIENT_OUT" || { cat "$CLIENT_OUT"; fail "host never became ready"; }

server_args=("$SCENARIO" "ws://127.0.0.1:${PORT}/sendspin")
[ -n "$SECRET" ] && server_args+=("$SECRET")
# A Pairing PSK attempt is bound to the client that issued the token, so the server side
# needs the id the host announced.
CLIENT_ID="$(grep -o '"client_id":"[^"]*"' "$CLIENT_OUT" | head -1 | cut -d'"' -f4)"
case "$SCENARIO" in
  pairing|source|player) server_args+=("$CLIENT_ID") ;;
  # The server side plays the operator, reading the code off the client's output.
  dynamic-pin)           server_args+=("$CLIENT_OUT") ;;
esac

# Dial from the reference server.
SERVER_OUT="$(mktemp)"
if ! "$PYTHON" "$HERE/server.py" "${server_args[@]}" | tee "$SERVER_OUT"; then
  echo "--- client output ---"; cat "$CLIENT_OUT"
  fail "server side reported failure"
fi

# Wait for the client to report success.
for _ in $(seq 1 30); do
  grep -q '"event":"success"' "$CLIENT_OUT" 2>/dev/null && break
  sleep 1
done
echo "--- client output ---"; cat "$CLIENT_OUT"
grep -q '"event":"success"' "$CLIENT_OUT" || fail "client did not report success"

if [ "$SCENARIO" = player ]; then
  # The stream is PCM end to end, so every sample sent has to come out of the decoder. A
  # client reading the chunk header at the wrong length would still decode, just not this many.
  sent="$(grep -o '"samples": *[0-9]*' "$SERVER_OUT" | tr -dc '0-9' || true)"
  decoded="$(grep -o '"samples": *[0-9]*' "$CLIENT_OUT" | tr -dc '0-9' || true)"
  [ -n "$sent" ] && [ "$sent" = "$decoded" ] || fail "server sent $sent samples, client decoded $decoded"
fi
pass
