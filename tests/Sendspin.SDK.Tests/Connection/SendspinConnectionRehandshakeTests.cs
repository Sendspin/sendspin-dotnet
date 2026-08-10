using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Noise;

namespace Sendspin.SDK.Tests.Connection;

/// <summary>
/// Drives a server-initiated re-handshake over the dial path
/// (<see cref="SendspinConnection"/>) end to end, over a real loopback socket: the
/// receive loop must hand the deferred reply to the send path, which encodes it under
/// the retiring keys and commits the swap, after which traffic flows in both directions
/// under the new keys. If the dial path never encodes and commits the deferred reply,
/// message 2 never reaches the server and this test times out waiting for it.
/// </summary>
[Collection("RealSockets")]
public class SendspinConnectionRehandshakeTests
{
    [Fact]
    public async Task Rehandshake_OverTheDialPath_CommitsAndContinuesUnderTheNewKeys()
    {
        await using var server = new SimpleWebSocketServer();
        server.Start(0);

        var accepted = new TaskCompletionSource<WebSocketClientConnection>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var text = Channel.CreateUnbounded<string>();
        var binary = Channel.CreateUnbounded<byte[]>();
        server.ClientConnected += (_, c) =>
        {
            c.OnText = data => text.Writer.TryWrite(Encoding.UTF8.GetString(data));
            c.OnBinary = b => binary.Writer.TryWrite(b);
            accepted.TrySetResult(c);
        };

        var identity = SendspinIdentity.Generate();
        var store = new InMemoryPairingRecordStore();
        byte[] newPsk = RandomNumberGenerator.GetBytes(32);
        store.Upsert(new PairingRecord(newPsk, PskCategory.LongTerm));
        var framing = new NoiseWireFraming(identity, new RecordPskResolver(store));
        var noiseServer = new TestNoiseServer(identity.PublicKey, NoiseConstants.SentinelPsk.ToArray());

        await using var connection = new SendspinConnection(
            NullLogger<SendspinConnection>.Instance,
            new ConnectionOptions { AutoReconnect = false },
            framing);

        await connection.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/sendspin"));
        var serverConn = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        string clientInit = await ReadAsync(text);
        var (serverInit, msg1) = noiseServer.Respond(clientInit);
        await serverConn.SendAsync(serverInit);
        await serverConn.SendAsync(msg1);
        noiseServer.CompleteHandshake(await ReadAsync(text));

        // Server-initiated re-handshake to the long-term PSK, inside the channel. The
        // reply (Noise message 2, under the OLD keys) must come back over the wire.
        await serverConn.SendAsync(noiseServer.StartRehandshake(newPsk));
        noiseServer.CompleteRehandshake(await ReadAsync(binary));

        // Client -> server under the new keys.
        await connection.SendBinaryAsync(new byte[] { 4, 7 });
        Assert.Equal(new byte[] { 4, 7 }, noiseServer.DecryptFrame(await ReadAsync(binary)));

        // Server -> client under the new keys still surfaces.
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.TextMessageReceived += (_, t) => received.TrySetResult(t);
        const string json = """{"type":"server/hello","payload":{"name":"again"}}""";
        await serverConn.SendAsync(noiseServer.EncryptFrame([0, .. Encoding.UTF8.GetBytes(json)]));
        Assert.Equal(json, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(PskCategory.LongTerm, framing.MatchedPsk!.Category);
    }

    private static async Task<T> ReadAsync<T>(Channel<T> channel) =>
        await channel.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
}
