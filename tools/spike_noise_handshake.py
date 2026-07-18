"""Phase 0 spike: prove the Sendspin Noise KKpsk2 handshake from raw primitives.

Server side: real aiosendspin 7.0.0 SendspinServer (the pinned interop target).
Client side: NO aiosendspin client code — raw WebSocket + noiseprotocol state
machine + hand-built JSON, mirroring exactly what the .NET SDK will implement.

Success criteria:
  1. Spec constants validate (Sentinel PSK bytes + published psk_id).
  2. client/init -> server/init -> noise/handshake x2 completes (KKpsk2,
     25519_ChaChaPoly_SHA256, prologue = exact init wire bytes, sentinel PSK).
  3. Transport mode: receive + decrypt server/hello (binary frame, type byte 0).
  4. Send encrypted client/hello; receive server/activate.
"""

import asyncio
import base64
import hashlib
import json
import sys

import aiohttp
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import x25519
from noise.connection import Keypair, NoiseConnection

HOST, PORT = "127.0.0.1", 8927
URL = f"ws://{HOST}:{PORT}/sendspin"
SUITE = "25519_ChaChaPoly_SHA256"
PATTERN = b"Noise_KKpsk2_25519_ChaChaPoly_SHA256"

# --- Spec constants (README.md "Pre-Shared Key" section) ---
SENTINEL_PSK = hashlib.sha256(b"sendspin-sentinel-psk-v1").digest()
SPEC_SENTINEL_PSK_HEX = "1b5e24dbc1aed95fc2a5a338a90c05df44bd10f5ec1f4cd66cbf86272767b9d3"
SPEC_SENTINEL_PSK_ID = "GFsV9tLaSQm9HcFWpKsgYQOr7wFTvNUtkmFwuVz3zoo"


def b64url(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode()


def b64url_decode(s: str) -> bytes:
    return base64.urlsafe_b64decode(s + "=" * (-len(s) % 4))


def psk_id(psk: bytes) -> str:
    return b64url(hashlib.sha256(b"sendspin-psk-id-v1" + psk).digest())


def check(name: str, ok: bool, detail: str = "") -> None:
    print(f"  [{'PASS' if ok else 'FAIL'}] {name}" + (f" — {detail}" if detail else ""))
    if not ok:
        sys.exit(1)


async def start_server():
    from aiosendspin.noise.keys import Identity
    from aiosendspin.noise.trust_store import InMemoryServerPairingStore
    from aiosendspin.server.server import SendspinServer

    loop = asyncio.get_running_loop()
    server = SendspinServer(
        loop,
        Identity.generate(),
        "spike-server",
        pairing_store=InMemoryServerPairingStore(),
    )
    await server.start_server(port=PORT, host=HOST, discover_clients=False)
    return server


async def raw_client() -> None:
    # Client identity: fresh X25519 keypair; the pubkey IS the client_id.
    priv = x25519.X25519PrivateKey.generate()
    priv_raw = priv.private_bytes(
        serialization.Encoding.Raw, serialization.PrivateFormat.Raw, serialization.NoEncryption()
    )
    pub_raw = priv.public_key().public_bytes(
        serialization.Encoding.Raw, serialization.PublicFormat.Raw
    )
    client_id = b64url(pub_raw)
    check("client_id is 43-char base64url pubkey", len(client_id) == 43, client_id)

    async with aiohttp.ClientSession() as http:
        async with http.ws_connect(URL, autoping=True) as ws:
            # 1. client/init (cleartext text frame)
            client_init = json.dumps(
                {
                    "type": "client/init",
                    "payload": {"client_id": client_id, "version": 1, "suite": SUITE},
                }
            )
            await ws.send_str(client_init)

            # 2. server/init (cleartext text frame)
            msg = await ws.receive(timeout=10)
            server_init = msg.data
            si = json.loads(server_init)
            check("server/init received", si["type"] == "server/init", f"server_id={si['payload']['server_id'][:12]}…")
            server_pub = b64url_decode(si["payload"]["server_id"])

            # 3. Noise responder: KKpsk2, prologue = exact init wire bytes
            noise = NoiseConnection.from_name(PATTERN)
            noise.set_as_responder()
            noise.set_keypair_from_private_bytes(Keypair.STATIC, priv_raw)
            noise.set_keypair_from_public_bytes(Keypair.REMOTE_STATIC, server_pub)
            noise.set_prologue(client_init.encode() + server_init.encode())
            noise.set_psks(psks=[b"\x00" * 32])  # placeholder until psk_id resolves
            noise.start_handshake()

            # 4. noise/handshake message 1 (server -> client): carries psk_id
            msg = await ws.receive(timeout=10)
            hs1 = json.loads(msg.data)
            check("noise/handshake msg1 received", hs1["type"] == "noise/handshake")
            msg1_plain = noise.read_message(b64url_decode(hs1["payload"]["data"]))
            got_psk_id = json.loads(msg1_plain)["psk_id"]
            check("msg1 psk_id == published sentinel psk_id", got_psk_id == SPEC_SENTINEL_PSK_ID, got_psk_id)

            # 5. Mix the real (sentinel) PSK, send noise/handshake message 2
            noise.set_psks(psks=[SENTINEL_PSK])
            msg2 = noise.write_message(json.dumps({}).encode())
            await ws.send_str(
                json.dumps({"type": "noise/handshake", "payload": {"data": b64url(bytes(msg2))}})
            )
            check("handshake complete (transport mode)", noise.handshake_finished)
            print(f"         handshake hash h = {bytes(noise.get_handshake_hash()).hex()[:32]}…")

            # 6. Encrypted server/hello: binary frame -> decrypt -> [0x00] + JSON
            msg = await ws.receive(timeout=10)
            check("post-handshake frame is BINARY", msg.type == aiohttp.WSMsgType.BINARY)
            plain = bytes(noise.decrypt(msg.data))
            check("plaintext type byte is 0 (JSON body)", plain[0] == 0)
            hello = json.loads(plain[1:])
            check("server/hello decrypted", hello["type"] == "server/hello", f"name={hello['payload']['name']!r}")

            # 7. Encrypted client/hello (client_id/version omitted under encryption)
            client_hello = {
                "type": "client/hello",
                "payload": {
                    "name": "dotnet-raw-spike",
                    "supported_roles": ["controller@v1"],
                    "trust_level": "none",
                    "unpaired_access": {"enabled": False},
                },
            }
            ct = noise.encrypt(b"\x00" + json.dumps(client_hello).encode())
            await ws.send_bytes(bytes(ct))

            # 8. server/activate
            msg = await ws.receive(timeout=10)
            check("second encrypted frame received", msg.type == aiohttp.WSMsgType.BINARY)
            plain = bytes(noise.decrypt(msg.data))
            act = json.loads(plain[1:])
            check(
                "server/activate received",
                act["type"] == "server/activate",
                f"activities={act['payload'].get('activities')} active_roles={act['payload'].get('active_roles')}",
            )


async def main() -> None:
    print("Spec-constant validation:")
    check("Sentinel PSK bytes match spec", SENTINEL_PSK.hex() == SPEC_SENTINEL_PSK_HEX)
    check("Derived sentinel psk_id matches spec", psk_id(SENTINEL_PSK) == SPEC_SENTINEL_PSK_ID)

    print("Handshake against real aiosendspin 7.0.0 server:")
    server = await start_server()
    try:
        await raw_client()
    finally:
        await server.close()
    print("\nSPIKE RESULT: all checks passed.")


if __name__ == "__main__":
    asyncio.run(main())
