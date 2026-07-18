// Phase 0 spike (.NET side): Sendspin Noise KKpsk2 handshake using Noise.NET
// against a real aiosendspin 7.0.0 server. Mirrors spike_noise_handshake.py.

using System.Buffers.Text;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Noise;

const string Suite = "25519_ChaChaPoly_SHA256";
const string Pattern = "Noise_KKpsk2_25519_ChaChaPoly_SHA256";
const string Url = "ws://127.0.0.1:8927/sendspin";
const string SpecSentinelPskId = "GFsV9tLaSQm9HcFWpKsgYQOr7wFTvNUtkmFwuVz3zoo";

byte[] sentinelPsk = SHA256.HashData(Encoding.ASCII.GetBytes("sendspin-sentinel-psk-v1"));

Console.WriteLine("Spec-constant validation (C#):");
Check("Sentinel PSK bytes match spec",
    Convert.ToHexStringLower(sentinelPsk) == "1b5e24dbc1aed95fc2a5a338a90c05df44bd10f5ec1f4cd66cbf86272767b9d3");
Check("Derived sentinel psk_id matches spec", PskId(sentinelPsk) == SpecSentinelPskId);

Console.WriteLine("Handshake against real aiosendspin 7.0.0 server (Noise.NET):");

// Client identity: X25519 keypair; pubkey IS the client_id.
using var keyPair = KeyPair.Generate();
string clientId = B64Url(keyPair.PublicKey);
Check("client_id is 43-char base64url pubkey", clientId.Length == 43, clientId);

using var ws = new ClientWebSocket();
await ws.ConnectAsync(new Uri(Url), CancellationToken.None);

// 1. client/init (cleartext text frame)
string clientInit = JsonSerializer.Serialize(new
{
    type = "client/init",
    payload = new { client_id = clientId, version = 1, suite = Suite },
});
await ws.SendAsync(Encoding.UTF8.GetBytes(clientInit), WebSocketMessageType.Text, true, CancellationToken.None);

// 2. server/init (cleartext text frame)
string serverInit = await ReceiveText(ws);
using var siDoc = JsonDocument.Parse(serverInit);
string serverId = siDoc.RootElement.GetProperty("payload").GetProperty("server_id").GetString()!;
Check("server/init received", siDoc.RootElement.GetProperty("type").GetString() == "server/init",
    $"server_id={serverId[..12]}…");
byte[] serverPub = B64UrlDecode(serverId);

// 3. Noise responder: KKpsk2, prologue = exact init wire bytes.
byte[] prologue = Encoding.UTF8.GetBytes(clientInit + serverInit);
var protocol = Protocol.Parse(Pattern.AsSpan());
// Note: with KKpsk2 the PSK is consumed when WRITING message 2, after msg1's
// psk_id is known. The real SDK will resolve psk_id -> PSK between ReadMessage
// and WriteMessage; here the candidate is the sentinel.
using var state = protocol.Create(
    initiator: false,
    prologue: prologue,
    s: keyPair.PrivateKey,
    rs: serverPub,
    psks: new[] { sentinelPsk });

// 4. noise/handshake message 1 (server -> client): carries psk_id
string hs1Text = await ReceiveText(ws);
using var hs1Doc = JsonDocument.Parse(hs1Text);
Check("noise/handshake msg1 received", hs1Doc.RootElement.GetProperty("type").GetString() == "noise/handshake");
byte[] msg1Ct = B64UrlDecode(hs1Doc.RootElement.GetProperty("payload").GetProperty("data").GetString()!);

var plainBuf = new byte[Protocol.MaxMessageLength];
var (msg1Len, _, _) = state.ReadMessage(msg1Ct, plainBuf);
using var msg1Doc = JsonDocument.Parse(Encoding.UTF8.GetString(plainBuf, 0, msg1Len));
string gotPskId = msg1Doc.RootElement.GetProperty("psk_id").GetString()!;
Check("msg1 psk_id == published sentinel psk_id", gotPskId == SpecSentinelPskId, gotPskId);

// 5. noise/handshake message 2 (payload {}), completing the handshake
var msgBuf = new byte[Protocol.MaxMessageLength];
var (msg2Len, handshakeHash, transport) = state.WriteMessage(Encoding.UTF8.GetBytes("{}"), msgBuf);
Check("handshake complete (transport returned)", transport is not null);
Console.WriteLine($"         handshake hash h = {Convert.ToHexStringLower(handshakeHash!)[..32]}…");

string hs2 = JsonSerializer.Serialize(new
{
    type = "noise/handshake",
    payload = new { data = B64Url(msgBuf.AsSpan(0, msg2Len)) },
});
await ws.SendAsync(Encoding.UTF8.GetBytes(hs2), WebSocketMessageType.Text, true, CancellationToken.None);

// 6. Encrypted server/hello: binary frame -> decrypt -> [0x00] + JSON
byte[] frame = await ReceiveBinary(ws);
int helloLen = transport!.ReadMessage(frame, plainBuf);
Check("plaintext type byte is 0 (JSON body)", plainBuf[0] == 0);
using var helloDoc = JsonDocument.Parse(Encoding.UTF8.GetString(plainBuf, 1, helloLen - 1));
Check("server/hello decrypted", helloDoc.RootElement.GetProperty("type").GetString() == "server/hello",
    $"name='{helloDoc.RootElement.GetProperty("payload").GetProperty("name").GetString()}'");

// 7. Encrypted client/hello (client_id/version omitted under encryption)
string clientHello = JsonSerializer.Serialize(new
{
    type = "client/hello",
    payload = new
    {
        name = "dotnet-noise-spike",
        supported_roles = new[] { "controller@v1" },
        trust_level = "none",
        unpaired_access = new { enabled = false },
    },
});
byte[] helloPlain = [0, .. Encoding.UTF8.GetBytes(clientHello)];
var ctBuf = new byte[Protocol.MaxMessageLength];
int ctLen = transport.WriteMessage(helloPlain, ctBuf);
await ws.SendAsync(ctBuf.AsMemory(0, ctLen), WebSocketMessageType.Binary, true, CancellationToken.None);

// 8. server/activate
frame = await ReceiveBinary(ws);
int actLen = transport.ReadMessage(frame, plainBuf);
using var actDoc = JsonDocument.Parse(Encoding.UTF8.GetString(plainBuf, 1, actLen - 1));
Check("server/activate received", actDoc.RootElement.GetProperty("type").GetString() == "server/activate",
    $"payload={actDoc.RootElement.GetProperty("payload").GetRawText()}");

Console.WriteLine("\nDOTNET SPIKE RESULT: all checks passed.");
return;

static string B64Url(ReadOnlySpan<byte> data) => Base64Url.EncodeToString(data);

static byte[] B64UrlDecode(string s) => Base64Url.DecodeFromChars(s);

static string PskId(byte[] psk)
{
    byte[] input = [.. Encoding.ASCII.GetBytes("sendspin-psk-id-v1"), .. psk];
    return B64Url(SHA256.HashData(input));
}

static void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? " — " + detail : "")}");
    if (!ok) Environment.Exit(1);
}

static async Task<string> ReceiveText(ClientWebSocket ws)
{
    var buf = new byte[65536];
    var result = await ws.ReceiveAsync(buf, CancellationToken.None);
    if (result.MessageType != WebSocketMessageType.Text)
        throw new InvalidOperationException($"expected text frame, got {result.MessageType}");
    return Encoding.UTF8.GetString(buf, 0, result.Count);
}

static async Task<byte[]> ReceiveBinary(ClientWebSocket ws)
{
    var buf = new byte[65536];
    var result = await ws.ReceiveAsync(buf, CancellationToken.None);
    if (result.MessageType != WebSocketMessageType.Binary)
        throw new InvalidOperationException($"expected binary frame, got {result.MessageType}");
    return buf[..result.Count];
}
