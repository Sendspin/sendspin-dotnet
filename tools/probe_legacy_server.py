"""What does a pre-7 (plaintext) aiosendspin server do when it receives client/init?

Boots a real aiosendspin 6.1.1 server and sends the encrypted-protocol opener,
then reports exactly what comes back (close? error message? silence?).
Also probes the reverse-order case: a legacy client/hello, to confirm the
old flow still works on the same endpoint.
"""

import asyncio
import json

import aiohttp

HOST, PORT = "127.0.0.1", 8929
URL = f"ws://{HOST}:{PORT}/sendspin"

CLIENT_INIT = json.dumps(
    {
        "type": "client/init",
        "payload": {
            "client_id": "dYy8DfShANDt7Mjh1WwN934xhDzfS1KKHcViAAVJ7yM",
            "version": 1,
            "suite": "25519_ChaChaPoly_SHA256",
        },
    }
)

LEGACY_HELLO = json.dumps(
    {
        "type": "client/hello",
        "payload": {
            "client_id": "spike-legacy-probe",
            "name": "legacy-probe",
            "version": 1,
            "supported_roles": ["controller@v1"],
        },
    }
)


async def probe(name: str, first_message: str) -> None:
    print(f"--- probe: {name} ---")
    async with aiohttp.ClientSession() as http:
        async with http.ws_connect(URL) as ws:
            await ws.send_str(first_message)
            for i in range(3):
                try:
                    msg = await ws.receive(timeout=5)
                except asyncio.TimeoutError:
                    print(f"  [{i}] TIMEOUT after 5s (server silent, connection still open)")
                    break
                if msg.type == aiohttp.WSMsgType.TEXT:
                    data = json.loads(msg.data)
                    payload_keys = list(data.get("payload", {}) or {})
                    print(f"  [{i}] TEXT type={data.get('type')!r} payload_keys={payload_keys}")
                elif msg.type in (aiohttp.WSMsgType.CLOSE, aiohttp.WSMsgType.CLOSING, aiohttp.WSMsgType.CLOSED):
                    print(f"  [{i}] CLOSED code={ws.close_code} reason={msg.extra!r}")
                    break
                else:
                    print(f"  [{i}] {msg.type.name} data={msg.data[:80]!r}")


async def main() -> None:
    from aiosendspin.server.server import SendspinServer

    loop = asyncio.get_running_loop()
    server = SendspinServer(loop, "legacy-server-id", "legacy-server")
    await server.start_server(port=PORT, host=HOST, discover_clients=False)
    try:
        await probe("client/init to 6.1.1 server (new protocol opener)", CLIENT_INIT)
        await probe("legacy client/hello to 6.1.1 server (control)", LEGACY_HELLO)
    finally:
        await server.close()


if __name__ == "__main__":
    asyncio.run(main())
