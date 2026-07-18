#!/usr/bin/env bash
# Live interop harness: runs the .NET SDK host against the aiosendspin 7.0.0 reference
# server for one scenario. Starts the .NET host (it prints its listening port), then
# dials it from the Python server, and checks both sides report success.
#
# Usage: run.sh <scenario>              scenario: unpaired | pairing
# Env:   PYTHON  - python interpreter with aiosendspin[server]==7.0.0 (default: python3)
set -euo pipefail

SCENARIO="${1:-unpaired}"
HERE="$(cd "$(dirname "$0")" && pwd)"
PYTHON="${PYTHON:-python3}"
PORT=8931
PSK_HEX="$(head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n')"

pass() { echo "INTEROP PASS: $SCENARIO"; exit 0; }
fail() { echo "INTEROP FAIL: $SCENARIO — $1" >&2; exit 1; }

client_args=("$SCENARIO" "$PORT")
[ "$SCENARIO" = "pairing" ] && client_args+=("$PSK_HEX")

# Start the .NET host in the background; capture its output.
CLIENT_OUT="$(mktemp)"
dotnet run --project "$HERE/InteropClient" -c Release -- "${client_args[@]}" >"$CLIENT_OUT" 2>&1 &
CLIENT_PID=$!
trap 'kill $CLIENT_PID 2>/dev/null || true' EXIT

# Wait for the host to report ready.
for _ in $(seq 1 60); do
  grep -q '"event":"host_ready"' "$CLIENT_OUT" 2>/dev/null && break
  kill -0 $CLIENT_PID 2>/dev/null || { cat "$CLIENT_OUT"; fail "host exited before ready"; }
  sleep 1
done
grep -q '"event":"host_ready"' "$CLIENT_OUT" || { cat "$CLIENT_OUT"; fail "host never became ready"; }

server_args=("$SCENARIO" "ws://127.0.0.1:${PORT}/sendspin")
[ "$SCENARIO" = "pairing" ] && server_args+=("$PSK_HEX")

# Dial from the reference server.
if ! "$PYTHON" "$HERE/server.py" "${server_args[@]}"; then
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
pass
