using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Noise;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Tests.Connection;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// An automatic reconnect of the dial path is a new connection: the client's per-connection
/// handshake state starts over, so the new connection's <c>server/hello</c> is answered, its
/// <c>server/activate</c> completes the handshake, and nothing is applied in between. Driven
/// over a real <see cref="SendspinConnection"/> and a loopback server, because the state
/// sequence that connection walks on a reconnect is the thing under test.
/// </summary>
[Collection("RealSockets")]
public class DialReconnectHandshakeTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly byte[] Psk = Enumerable.Repeat((byte)0x5A, 32).ToArray();

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();

    [Fact]
    public async Task AutomaticReconnect_RunsTheHelloExchangeAgain_AndGatesUntilTheNewActivate()
    {
        var identity = SendspinIdentity.Generate();
        var store = new InMemoryPairingRecordStore();
        store.Upsert(new PairingRecord(Psk, PskCategory.LongTerm));
        var keys = KeyPair.Generate();

        var (port, sessions) = StartServer(identity, keys, Psk);

        await using var client = SendspinClientService.CreateForDial(
            NullLoggerFactory.Instance,
            new SendspinClientOptions
            {
                Identity = identity,
                Suite = NoiseCipherSuite.ChaChaPoly,
                PairingRecordStore = store,
                Capabilities = new ClientCapabilities { Roles = ["controller@v1", "metadata@v1"] },
            },
            new ConnectionOptions { ReconnectDelayMs = 100, AutoReconnect = true });

        var states = new ConcurrentQueue<ConnectionState>();
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += (_, e) =>
        {
            states.Enqueue(e.NewState);
            if (e.NewState == ConnectionState.Connected && states.Contains(ConnectionState.Reconnecting))
            {
                reconnected.TrySetResult();
            }
        };

        var groupNames = new ConcurrentQueue<string?>();
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.GroupStateChanged += (_, group) =>
        {
            groupNames.Enqueue(group.Name);
            if (group.Name == "applied")
            {
                applied.TrySetResult();
            }
        };

        // First connection: hello, client/hello, activate.
        var connecting = client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/sendspin"));
        var first = await sessions.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        await first.WaitForAsync("client/hello").WaitAsync(Wait);
        await first.SendAsync(Activate("metadata@v1"));
        await connecting.WaitAsync(Wait);
        Assert.Equal(ConnectionState.Connected, client.ConnectionState);
        Assert.Equal(["metadata@v1"], client.LastServerHello!.ActiveRoles);

        // The server drops the socket; the connection redials on its own.
        await first.CloseAsync();
        var second = await sessions.Reader.ReadAsync().AsTask().WaitAsync(Wait);

        // The new connection's hello is answered with a client/hello of its own.
        await second.WaitForAsync("client/hello").WaitAsync(Wait);

        // Between that hello and the activate nothing else is applied.
        await second.SendAsync(GroupUpdate("dropped"));
        await second.SendAsync(Activate("controller@v1", "metadata@v1"));
        await reconnected.Task.WaitAsync(Wait);
        await second.SendAsync(GroupUpdate("applied"));
        await applied.Task.WaitAsync(Wait);

        Assert.Equal(ConnectionState.Connected, client.ConnectionState);
        Assert.Equal(["controller@v1", "metadata@v1"], client.LastServerHello!.ActiveRoles);
        Assert.DoesNotContain("dropped", groupNames);
    }

    [Fact]
    public async Task ActivationTheClientRefuses_FailsConnectAsync()
    {
        // First contact with a server, unpaired access off (the default): the session runs on
        // the Sentinel PSK, the server activates playback, and the client refuses it (#325).
        var identity = SendspinIdentity.Generate();
        var (port, sessions) = StartServer(identity, KeyPair.Generate(), NoiseConstants.SentinelPsk.ToArray());

        await using var client = SendspinClientService.CreateForDial(
            NullLoggerFactory.Instance,
            new SendspinClientOptions
            {
                Identity = identity,
                Suite = NoiseCipherSuite.ChaChaPoly,
                PairingRecordStore = new InMemoryPairingRecordStore(),
                Capabilities = new ClientCapabilities { Roles = ["controller@v1", "metadata@v1"] },
            },
            new ConnectionOptions { AutoReconnect = false });

        var connecting = client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/sendspin"));
        var session = await sessions.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        await session.WaitForAsync("client/hello").WaitAsync(Wait);
        await session.SendAsync(Activate("metadata@v1"));

        var ex = await Assert.ThrowsAsync<SendspinHandshakeException>(() => connecting.WaitAsync(Wait));
        Assert.Equal(HandshakeFailureKind.PairingRequired, ex.Kind);
        Assert.Equal(ConnectionState.Disconnected, client.ConnectionState);
        await session.WaitForAsync("client/goodbye").WaitAsync(Wait);
    }

    [Fact]
    public async Task InBandRehandshake_HoldsApplicationMessagesUntilTheNewActivate()
    {
        // The real framing swaps keys as the client's reply is written, so anything sent from
        // then on would reach the server under the new keys ahead of its server/activate (#317).
        var identity = SendspinIdentity.Generate();
        byte[] rotated = Enumerable.Repeat((byte)0x3C, 32).ToArray();
        var store = new InMemoryPairingRecordStore();
        store.Upsert(new PairingRecord(Psk, PskCategory.LongTerm));
        store.Upsert(new PairingRecord(rotated, PskCategory.LongTerm));
        var (port, sessions) = StartServer(identity, KeyPair.Generate(), Psk);

        await using var client = SendspinClientService.CreateForDial(
            NullLoggerFactory.Instance,
            new SendspinClientOptions
            {
                Identity = identity,
                Suite = NoiseCipherSuite.ChaChaPoly,
                PairingRecordStore = store,
                Capabilities = new ClientCapabilities { Roles = ["controller@v1", "metadata@v1"] },
            },
            new ConnectionOptions { AutoReconnect = false });

        var connecting = client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/sendspin"));
        var session = await sessions.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        await session.WaitForAsync("client/hello").WaitAsync(Wait);
        await session.SendAsync(Activate("controller@v1", "metadata@v1"));
        await connecting.WaitAsync(Wait);
        await session.WaitForAsync("client/state").WaitAsync(Wait);

        // Message 1 out, message 2 back: both ends are now on the new keys.
        await session.StartRehandshakeAsync(rotated);
        await session.WaitForAsync("noise/handshake").WaitAsync(Wait);
        int statesBefore = session.Count("client/state");

        // Availability, because it is a client/state that needs no particular role.
        await client.EnterExternalSourceAsync();
        await Task.Delay(200);
        Assert.Equal(statesBefore, session.Count("client/state"));

        // The activate ends the hold, and the state it withheld follows.
        await session.SendAsync(Activate("controller@v1", "metadata@v1"));
        await session.WaitForAsync("client/state").WaitAsync(Wait);
        Assert.Equal(ConnectionState.Connected, client.ConnectionState);
    }

    public ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        return ValueTask.CompletedTask;
    }

    /// <summary>Accepts dialled connections on a loopback port, one <see cref="ServerSession"/> each.</summary>
    private (int Port, Channel<ServerSession> Sessions) StartServer(
        SendspinIdentity clientIdentity, KeyPair keys, byte[] psk)
    {
        var sessions = Channel.CreateUnbounded<ServerSession>();
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                var tcp = await _listener.AcceptTcpClientAsync(_stop.Token);
                sessions.Writer.TryWrite(await ServerSession.AcceptAsync(tcp, clientIdentity, keys, psk, _stop.Token));
            }
        });

        return (port, sessions);
    }

    private static string Activate(params string[] roles)
    {
        string list = string.Join(",", roles.Select(r => "\"" + r + "\""));
        return "{\"type\":\"server/activate\",\"payload\":{\"activities\":[\"playback\"],\"active_roles\":[" + list + "]}}";
    }

    private static string GroupUpdate(string name) =>
        "{\"type\":\"group/update\",\"payload\":{\"playback_state\":\"playing\",\"group_id\":\"g1\",\"group_name\":\"" + name + "\"}}";

    /// <summary>
    /// The server end of one dialled connection: runs the Noise handshake as the initiator,
    /// sends <c>server/hello</c> when it completes, and records the type of every JSON message
    /// the client sends after that, completing an in-band re-handshake when the client's reply
    /// to one arrives. Everything else is sent by the test.
    /// </summary>
    /// <remarks>
    /// Accepts the WebSocket itself rather than through <see cref="SimpleWebSocketServer"/>,
    /// which starts reading before it announces the connection: a dialling client sends
    /// <c>client/init</c> at once, and a handler attached after that has missed it.
    /// </remarks>
    private sealed class ServerSession
    {
        private readonly WebSocket _socket;
        private readonly SendspinIdentity _clientIdentity;
        private readonly KeyPair _keys;
        private readonly byte[] _psk;
        private readonly Channel<string> _received = Channel.CreateUnbounded<string>();
        private readonly ConcurrentQueue<string> _seen = new();
        private TestNoiseServer? _noise;

        private ServerSession(WebSocket socket, SendspinIdentity clientIdentity, KeyPair keys, byte[] psk)
        {
            _socket = socket;
            _clientIdentity = clientIdentity;
            _keys = keys;
            _psk = psk;
        }

        public static async Task<ServerSession> AcceptAsync(
            TcpClient tcp, SendspinIdentity clientIdentity, KeyPair keys, byte[] psk, CancellationToken stop)
        {
            var stream = tcp.GetStream();

            // The upgrade request is a few header lines ending in a blank one.
            var request = new StringBuilder();
            var one = new byte[1];
            while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                Assert.Equal(1, await stream.ReadAsync(one, stop));
                request.Append((char)one[0]);
            }

            string key = request.ToString().Split("\r\n")
                .Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                .Split(':', 2)[1].Trim();
            string accept = Convert.ToBase64String(
                SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(
                Encoding.ASCII.GetBytes(
                    "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                    + "Sec-WebSocket-Accept: " + accept + "\r\n\r\n"),
                stop);

            var session = new ServerSession(
                WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, Timeout.InfiniteTimeSpan),
                clientIdentity,
                keys,
                psk);
            _ = Task.Run(() => session.ReceiveAsync(stop));
            return session;
        }

        public Task SendAsync(string json) =>
            SendAsync(_noise!.EncryptFrame([0, .. Encoding.UTF8.GetBytes(json)]), WebSocketMessageType.Binary);

        /// <summary>Completes when the client has sent a message of the given type.</summary>
        public async Task WaitForAsync(string type)
        {
            while (await _received.Reader.ReadAsync() != type)
            {
            }
        }

        /// <summary>How many messages of the given type the client has sent so far.</summary>
        public int Count(string type) => _seen.Count(t => t == type);

        /// <summary>Sends Noise message 1 of an in-band re-handshake onto <paramref name="psk"/>.</summary>
        public Task StartRehandshakeAsync(byte[] psk) =>
            SendAsync(_noise!.StartRehandshake(psk), WebSocketMessageType.Binary);

        /// <summary>Drops the connection the way a restarting server does.</summary>
        public Task CloseAsync() =>
            _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);

        private Task SendAsync(byte[] data, WebSocketMessageType type) =>
            _socket.SendAsync(data, type, endOfMessage: true, CancellationToken.None);

        private async Task ReceiveAsync(CancellationToken stop)
        {
            var buffer = new byte[16384];
            using var message = new MemoryStream();
            try
            {
                while (_socket.State == WebSocketState.Open)
                {
                    message.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _socket.ReceiveAsync(buffer, stop);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            return;
                        }

                        message.Write(buffer.AsSpan(0, result.Count));
                    }
                    while (!result.EndOfMessage);

                    await HandleAsync(result.MessageType == WebSocketMessageType.Text, message.ToArray());
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
            {
                // The client went away or the test is over.
            }
        }

        private async Task HandleAsync(bool text, byte[] data)
        {
            if (!text)
            {
                byte[] plaintext = _noise!.DecryptFrame(data);
                if (plaintext.Length > 0 && plaintext[0] == 0)
                {
                    string message = Encoding.UTF8.GetString(plaintext, 1, plaintext.Length - 1);
                    string type = MessageSerializer.GetMessageType(message)!;
                    if (type == "noise/handshake")
                    {
                        // The client's reply to a re-handshake, under the keys it retires.
                        _noise.CompleteHandshake(message);
                    }

                    _seen.Enqueue(type);
                    _received.Writer.TryWrite(type);
                }

                return;
            }

            string json = Encoding.UTF8.GetString(data);
            if (MessageSerializer.GetMessageType(json) == "client/init")
            {
                _noise = new TestNoiseServer(_clientIdentity.PublicKey, _psk, _keys);
                var (serverInit, msg1) = _noise.Respond(json);
                await SendAsync(Encoding.UTF8.GetBytes(serverInit), WebSocketMessageType.Text);
                await SendAsync(Encoding.UTF8.GetBytes(msg1), WebSocketMessageType.Text);
            }
            else
            {
                _noise!.CompleteHandshake(json);
                await SendAsync("""{"type":"server/hello","payload":{"name":"loopback"}}""");
            }
        }
    }
}
