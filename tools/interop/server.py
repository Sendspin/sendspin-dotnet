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
               attempt first — every static_pairing_code attempt must wait for a pairing window.
  dynamic-pin: run a dynamic-pairing-code attempt. The client derives a fresh code for the
               attempt and shows it; the code is read back off the client's output and fed
               to the server, as an operator would type it in.
  source:      ask the client to stream its source@v1 input, and decode the chunks that
               arrive — the server's start command, the client's client_stream/start, and
               real audio over the wire.
  player:      stream two seconds of PCM to the client's player@v1 role — stream/start, the
               binary audio chunks and stream/end — and fail if the client's initial
               client/state arrived too late for the server's liking.

Prints JSON result lines; exits non-zero on failure. Requires the pinned aiosendspin 10.0.0
release (see .github/workflows/interop.yml).

Usage: server.py <scenario> <client_url> [secret]
       secret is the pairing PSK as hex for 'pairing', the 8-digit PIN for 'static-pin', or
       the file the client's output is being written to for 'dynamic-pin'.
"""

import asyncio
import json
import logging
import math
import re
import struct
import sys
import time

from aiosendspin.models.types import ConnectionReason, PairingCodeFormat, PairMethod
from aiosendspin.noise.keys import Identity
from aiosendspin.noise.pairing import PairingAttempt
from aiosendspin.noise.trust_store import InMemoryServerPairingStore
from aiosendspin.server import AudioFormat
from aiosendspin.server.roles.source.events import (
    SourceStreamEndedEvent,
    SourceStreamStartedEvent,
)
from aiosendspin.server.server import SendspinServer


CHUNKS_WANTED = 10

PLAYER_FORMAT = AudioFormat(sample_rate=48000, bit_depth=16, channels=2)
# 100 ms of a 440 Hz tone, a whole number of cycles so it repeats without a seam.
PLAYER_PCM = b"".join(
    struct.pack("<hh", sample, sample)
    for sample in (int(8000 * math.sin(2 * math.pi * 440 * i / 48000)) for i in range(4800))
)
PLAYER_COMMITS = 20


def emit(**kw):
    print(json.dumps(kw), flush=True)


class StateTimeoutWatch(logging.Handler):
    """Notes whether the server flagged a late initial client/state.

    aiosendspin tolerates the lateness by default and only logs it, so the log is the one
    place it shows.
    """

    def __init__(self) -> None:
        super().__init__(logging.WARNING)
        self.fired = False

    def emit(self, record: logging.LogRecord) -> None:
        if "did not send the required initial client/state in time" in record.getMessage():
            self.fired = True


async def drive_player(server, state_timeout: StateTimeoutWatch) -> int:
    """Stream PCM to the paired client's player role.

    The only live cover for stream/start and stream/end on the player role, and for the
    binary audio chunk header the client has to read to find the samples.
    """
    paired_at = time.monotonic()
    # The playback activation follows pairing at once, and the client answers it with its
    # initial client/state only when clock sync has converged. Until that arrives the server
    # does not count the client as connected, and a real server would not start a stream on
    # a client that has not reported itself available.
    for _ in range(240):
        if server.connected_clients and server.connected_clients[0].available:
            break
        await asyncio.sleep(0.25)
    else:
        emit(event="client_never_became_available")
        return 1

    # The wait above is the client's initial client/state delay, to the polling interval.
    emit(event="client_available", seconds_after_pairing=round(time.monotonic() - paired_at, 2))
    if state_timeout.fired:
        emit(event="initial_client_state_late")
        return 1

    client = server.connected_clients[0]

    stream = client.group.start_stream()
    for _ in range(PLAYER_COMMITS):
        stream.prepare_audio(PLAYER_PCM, PLAYER_FORMAT)
        await stream.commit_audio()
    # The server holds back audio that is further ahead than it sends, and stop() drops what
    # is still held; close() in turn drops what is still queued, stream/end included. Give
    # each a moment to reach the wire.
    await asyncio.sleep(1)
    await client.group.stop()
    await asyncio.sleep(1)

    emit(event="player_audio_sent", samples=PLAYER_COMMITS * len(PLAYER_PCM) // 2)
    return 0


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
    client_id = sys.argv[4] if len(sys.argv) > 4 else None

    # Attaching a handler takes aiosendspin's warnings away from logging's stderr fallback;
    # basicConfig puts them back in the output.
    logging.basicConfig(level=logging.WARNING)
    state_timeout = StateTimeoutWatch()
    logging.getLogger("aiosendspin").addHandler(state_timeout)

    loop = asyncio.get_running_loop()
    store = InMemoryServerPairingStore()
    server = SendspinServer(loop, Identity.generate(), "interop-server", pairing_store=store)
    emit(event="server_ready", server_id=server.id, scenario=scenario)

    try:
        # Start the dial as a background task (keeps the connection alive while we poll)
        # and let it complete the Sendspin handshake — do NOT close early.
        # 'source' pairs first: the source role only runs at 'user' trust, so an unpaired
        # session cannot exercise it. 'player' pairs the same way.
        if scenario in ("pairing", "static-pin", "dynamic-pin", "source", "player"):
            gated = asyncio.Event()
            if scenario in ("pairing", "source", "player"):
                attempt = PairingAttempt(
                    method=PairMethod.PAIRING_PSK,
                    pairing_psk=bytes.fromhex(secret),
                    # A Pairing PSK token names the client it was issued by; the attempt
                    # runs only on a connection presenting that client_id.
                    client_id=client_id,
                )
            elif scenario == "dynamic-pin":
                # The operator would read this off the device's display; here the client
                # prints it, and it does not exist until the attempt is under way.
                async def read_code() -> str:
                    for _ in range(120):
                        with open(secret, encoding="utf-8") as client_output:
                            shown = re.search(
                                r'"event":"pairing_code","code":"(\d+)"', client_output.read()
                            )
                        if shown:
                            return shown.group(1)
                        await asyncio.sleep(0.25)
                    raise TimeoutError("the client never presented a pairing code")

                attempt = PairingAttempt(
                    method=PairMethod.DYNAMIC_PAIRING_CODE,
                    pairing_code_provider=read_code,
                    pairing_format=PairingCodeFormat.DIGITS,
                )
            else:
                # The operator would type this in; here both sides already know it.
                async def supply_code() -> str:
                    return secret

                attempt = PairingAttempt(
                    method=PairMethod.STATIC_PAIRING_CODE,
                    pairing_code_provider=supply_code,
                    on_pair_pending=lambda _message: gated.set(),
                )
            server.connect_to_client(
                client_url,
                # 'source' and 'player' dial for playback so the activation carries the
                # playback activity: the client defers its initial client/state until clock
                # sync converges, and without that activity there is nothing to converge for.
                connection_reason=(
                    ConnectionReason.PLAYBACK
                    if scenario in ("source", "player")
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
                # The spec gates every static_pairing_code attempt: the client must have reported
                # client/pair-pending and withheld client/pair-init until a window opened.
                # Pairing succeeding without that means the gate is not being applied.
                if not gated.is_set():
                    emit(event="attempt_was_not_gesture_gated")
                    return 1
                emit(event="gesture_gated_confirmed")

            if scenario == "source":
                # Paired now, so the client will accept a source start command.
                return await drive_source(server)

            if scenario == "player":
                return await drive_player(server, state_timeout)
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
