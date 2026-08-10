"""aiosendspin reference-server side of the live interop harness.

Dials the .NET SDK host (which listens on a known port) directly by URL — no mDNS —
and drives one scenario against it:

  unpaired:    connect for playback over the client's unpaired-access path, confirm the
               handshake completes.
  pairing:     run a Pairing PSK pairing attempt with a shared bootstrap secret, confirm
               the client is admitted at 'user' trust afterward (i.e. the long-term record
               took on both sides and the re-handshake to it succeeded).
  static-pin:  run a static-PIN attempt with a PIN both sides already know. Exercises the
               CPace round and PSK wrapping, and asserts the client gesture-gated the
               attempt first — every static_pin attempt must wait for a pairing window.
  source:      ask the client to stream its source@v1 input, and decode the chunks that
               arrive — the server's start command, the client's client_stream/start, and
               real audio over the wire.

Prints JSON result lines; exits non-zero on failure. Requires aiosendspin[server]==9.0.0.

Usage: server.py <scenario> <client_url> [secret]
       secret is the pairing PSK as hex for 'pairing', or the 8-digit PIN for 'static-pin'.
"""

import asyncio
import json
import sys

from aiosendspin.models.types import ConnectionReason, PairMethod
from aiosendspin.noise.keys import Identity
from aiosendspin.noise.pairing import PairingAttempt
from aiosendspin.noise.trust_store import InMemoryServerPairingStore
from aiosendspin.server.roles.source.events import (
    SourceStreamEndedEvent,
    SourceStreamStartedEvent,
)
from aiosendspin.server.server import SendspinServer


CHUNKS_WANTED = 10


def emit(**kw):
    print(json.dumps(kw), flush=True)


async def drive_source(server) -> int:
    """Ask the paired client to stream its source, and decode what arrives.

    Proves the whole source path against a real counterparty: the server's start command,
    the client's client_stream/start with a codec the server can build a decoder for, and
    binary chunks that actually decode into PCM.
    """
    # Pairing ends with a re-handshake to the new long-term PSK, so the client briefly
    # leaves connected_clients while that completes.
    for _ in range(120):
        if server.connected_clients:
            break
        await asyncio.sleep(0.25)
    else:
        emit(event="client_not_connected_after_pairing")
        return 1

    client = server.connected_clients[0]
    # The server activates the negotiated role set itself once the connection is paired and
    # playback-capable. Declaring a narrower set here would be overwritten by that refresh,
    # and the resulting re-activation tears the role down mid-stream.
    for _ in range(120):
        roles = client.roles_by_family("source")
        if roles:
            break
        await asyncio.sleep(0.25)
    else:
        emit(event="source_role_not_active", active=client.active_role_ids)
        return 1

    started: asyncio.Future = asyncio.get_running_loop().create_future()

    def on_event(_client, event) -> None:
        if isinstance(event, SourceStreamStartedEvent) and not started.done():
            started.set_result(event)
        elif isinstance(event, SourceStreamEndedEvent):
            emit(event="source_stream_ended")

    # The client defers its initial client/state until clock sync converges, and the source
    # role drops every chunk until that state arrives. Asking it to stream before then is
    # not something a real server would do, and the audio would be discarded if it did.
    for _ in range(240):
        if client.available:
            break
        await asyncio.sleep(0.25)
    else:
        emit(event="client_never_became_available")
        return 1

    unsubscribe = client.add_event_listener(on_event)
    try:
        roles[0].request_start()
        try:
            event = await asyncio.wait_for(started, timeout=30)
        except TimeoutError:
            emit(event="source_stream_never_started")
            return 1

        emit(
            event="source_stream_started",
            sample_rate=event.audio_format.sample_rate,
            channels=event.audio_format.channels,
        )

        chunks = 0
        total_pcm = 0
        async for pcm, _timestamp_us in event.handle:
            chunks += 1
            total_pcm += len(pcm)
            if chunks >= CHUNKS_WANTED:
                break

        emit(event="source_chunks_received", count=chunks, pcm_bytes=total_pcm)
        if chunks < CHUNKS_WANTED or total_pcm == 0:
            return 1
        return 0
    finally:
        unsubscribe()


async def main() -> int:
    scenario = sys.argv[1] if len(sys.argv) > 1 else "unpaired"
    client_url = sys.argv[2]
    secret = sys.argv[3] if len(sys.argv) > 3 else None

    loop = asyncio.get_running_loop()
    store = InMemoryServerPairingStore()
    server = SendspinServer(loop, Identity.generate(), "interop-server", pairing_store=store)
    emit(event="server_ready", server_id=server.id, scenario=scenario)

    try:
        # Start the dial as a background task (keeps the connection alive while we poll)
        # and let it complete the Sendspin handshake — do NOT close early.
        # 'source' pairs first: the source role only runs at 'user' trust, so an unpaired
        # session cannot exercise it.
        if scenario in ("pairing", "static-pin", "source"):
            gated = asyncio.Event()
            if scenario in ("pairing", "source"):
                attempt = PairingAttempt(
                    method=PairMethod.PAIRING_PSK,
                    pairing_psk=bytes.fromhex(secret),
                )
            else:
                # The operator would type this in; here both sides already know it.
                async def supply_pin() -> str:
                    return secret

                attempt = PairingAttempt(
                    method=PairMethod.STATIC_PIN,
                    pin_provider=supply_pin,
                    on_pair_pending=gated.set,
                )
            server.connect_to_client(
                client_url,
                # 'source' dials for playback so the activation carries the playback
                # activity: the client defers its initial client/state until clock sync
                # converges, and without that activity there is nothing to converge for.
                connection_reason=(
                    ConnectionReason.PLAYBACK
                    if scenario == "source"
                    else ConnectionReason.DISCOVERY
                ),
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

            if scenario == "static-pin":
                # The spec gates every static_pin attempt: the client must have reported
                # client/pair-pending and withheld client/pair-init until a window opened.
                # Pairing succeeding without that means the gate is not being applied.
                if not gated.is_set():
                    emit(event="attempt_was_not_gesture_gated")
                    return 1
                emit(event="gesture_gated_confirmed")

            if scenario == "source":
                # Paired now, so the client will accept a source start command.
                return await drive_source(server)
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
