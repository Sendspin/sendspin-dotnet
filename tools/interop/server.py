"""aiosendspin reference-server side of the live interop harness.

Dials the .NET SDK host (which listens on a known port) directly by URL — no mDNS —
and drives one scenario against it:

  unpaired: connect for playback over the client's unpaired-access path, confirm the
            handshake completes.
  pairing:  run a Pairing PSK pairing attempt with a shared bootstrap secret, confirm
            the client is admitted at 'user' trust afterward (i.e. the long-term record
            took on both sides and the re-handshake to it succeeded).

Prints JSON result lines; exits non-zero on failure. Requires aiosendspin[server]==7.0.0.

Usage: server.py <scenario> <client_url> [pairing_psk_hex]
"""

import asyncio
import json
import sys

from aiosendspin.models.types import ConnectionReason, PairMethod
from aiosendspin.noise.keys import Identity
from aiosendspin.noise.pairing import PairingAttempt
from aiosendspin.noise.trust_store import InMemoryServerPairingStore
from aiosendspin.server.server import SendspinServer


def emit(**kw):
    print(json.dumps(kw), flush=True)


async def main() -> int:
    scenario = sys.argv[1] if len(sys.argv) > 1 else "unpaired"
    client_url = sys.argv[2]
    pairing_psk = bytes.fromhex(sys.argv[3]) if len(sys.argv) > 3 else None

    loop = asyncio.get_running_loop()
    store = InMemoryServerPairingStore()
    server = SendspinServer(loop, Identity.generate(), "interop-server", pairing_store=store)
    emit(event="server_ready", server_id=server.id, scenario=scenario)

    try:
        # Start the dial as a background task (keeps the connection alive while we poll)
        # and let it complete the Sendspin handshake — do NOT close early.
        if scenario == "pairing":
            attempt = PairingAttempt(method=PairMethod.PAIRING_PSK, pairing_psk=pairing_psk)
            server.connect_to_client(
                client_url,
                connection_reason=ConnectionReason.DISCOVERY,
                retry_initial_connection=True,
                retry_indefinitely=True,
                pairing_attempt=attempt,
            )
            # Pairing produces a persisted long-term record on the server side, then a
            # re-handshake promotes the client to 'user' trust.
            for _ in range(120):
                records = await store.list_records()
                if records:
                    emit(event="pairing_persisted_serverside", count=len(records))
                    break
                await asyncio.sleep(0.25)
            else:
                emit(event="pairing_not_persisted")
                return 1
        else:
            server.connect_to_client(
                client_url,
                connection_reason=ConnectionReason.PLAYBACK,
                retry_initial_connection=True,
                retry_indefinitely=True,
            )
            # Wait for the full Sendspin handshake to complete (client appears connected).
            for _ in range(120):
                if server.connected_clients:
                    emit(event="connected_serverside", count=len(server.connected_clients))
                    break
                await asyncio.sleep(0.25)
            else:
                emit(event="client_never_connected")
                return 1

        emit(event="success", scenario=scenario)
        return 0
    except Exception as exc:  # noqa: BLE001 — harness boundary; report and fail
        emit(event="error", scenario=scenario, detail=f"{type(exc).__name__}: {exc}")
        return 2
    finally:
        await server.close()


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
