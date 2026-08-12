using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Connection;

/// <summary>
/// The dial path's teardown must not wait for the peer's Close frame (#160), for the same
/// reason the listen path was changed away from it in #143.
/// </summary>
/// <remarks>
/// The peer here is deterministic rather than merely slow: it completes the WebSocket upgrade
/// and then never reads the socket again, so it can never answer a Close frame. That makes the
/// test a statement about the shape of the call, not a race — <c>CloseAsync</c> hangs here
/// every time and <c>CloseOutputAsync</c> returns here every time.
/// </remarks>
[Collection("RealSockets")]
public class DialPathCloseHandshakeTests
{
    [Fact]
    public async Task DisconnectAsync_AgainstAPeerThatNeverAnswersTheClose_StillCompletes()
    {
        var peer = new SilentUpgradingPeer();
        peer.Start();

        var connection = new SendspinConnection(
            NullLogger<SendspinConnection>.Instance,
            new ConnectionOptions { AutoReconnect = false },
            new StubFraming());

        bool completed;
        try
        {
            await connection.ConnectAsync(new Uri($"ws://127.0.0.1:{peer.Port}/sendspin"));

            var disconnect = connection.DisconnectAsync(GoodbyeReasons.Shutdown);

            // Bounded here rather than left to hang: a test that waits forever burns the whole
            // CI job instead of failing, which is how #143 stayed invisible for so long.
            completed = await Task.WhenAny(disconnect, Task.Delay(TimeSpan.FromSeconds(5)))
                == disconnect;
        }
        finally
        {
            // Before the assert, and before disposing the connection: dropping the peer faults
            // any close still parked on the peer's reply, so a regression fails this test
            // rather than wedging the run.
            peer.Dispose();
        }

        Assert.True(
            completed,
            "DisconnectAsync must not wait for a Close frame the peer will never send");

        await connection.DisposeAsync();
    }

    /// <summary>
    /// Accepts one TCP connection, completes the RFC 6455 upgrade, and then never reads the
    /// socket again — so it receives no Close frame and therefore never sends one.
    /// </summary>
    private sealed class SilentUpgradingPeer : IDisposable
    {
        private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private TcpClient? _accepted;

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public void Start()
        {
            _listener.Start();
            _ = Task.Run(AcceptOnceAsync);
        }

        private async Task AcceptOnceAsync()
        {
            try
            {
                _accepted = await _listener.AcceptTcpClientAsync(_cts.Token);
                var stream = _accepted.GetStream();

                var buffer = new byte[8192];
                int total = 0;
                while (buffer.AsSpan(0, total).IndexOf("\r\n\r\n"u8) < 0)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(total), _cts.Token);
                    if (read == 0) return;
                    total += read;
                }

                string request = Encoding.UTF8.GetString(buffer, 0, total);
                string key = request
                    .Split("\r\n")
                    .First(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                    .Split(':')[1]
                    .Trim();

                string accept = Convert.ToBase64String(
                    SHA1.HashData(Encoding.UTF8.GetBytes(key + WebSocketGuid)));

                await stream.WriteAsync(
                    Encoding.UTF8.GetBytes(
                        "HTTP/1.1 101 Switching Protocols\r\n"
                        + "Upgrade: websocket\r\n"
                        + "Connection: Upgrade\r\n"
                        + $"Sec-WebSocket-Accept: {accept}\r\n\r\n"),
                    _cts.Token);

                // And that is all. No receive loop, so the client's Close frame is never read
                // and never answered.
            }
            catch (Exception)
            {
                // Disposal races the accept/read; nothing here is the subject of the test.
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _accepted?.Dispose();
            _listener.Stop();
            _cts.Dispose();
        }
    }
}
