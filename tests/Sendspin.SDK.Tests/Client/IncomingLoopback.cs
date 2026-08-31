using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Connection;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// A real loopback pair for tests that need the concrete server-initiated transport rather than
/// <see cref="FakeSendspinConnection"/>: a <see cref="SimpleWebSocketServer"/> on an
/// OS-assigned port, a <see cref="ClientWebSocket"/> standing in for the server that dialled in,
/// and the <see cref="IncomingConnection"/> over the accepted end — already started, and so
/// parked in <see cref="ConnectionState.Handshaking"/>, exactly where
/// <c>SendspinHostService</c> leaves a freshly accepted connection.
/// </summary>
/// <remarks>
/// The peer runs a receive pump from the moment it connects, and that pump answers a Close
/// frame. It has to: <see cref="IncomingConnection.DisconnectAsync"/> performs a full WebSocket
/// closing handshake, which parks forever against a peer that never replies — so a test that
/// awaits a disconnect without a pump does not fail, it hangs.
/// </remarks>
internal sealed class IncomingLoopback : IAsyncDisposable
{
    private readonly SimpleWebSocketServer _server = new();
    private readonly ClientWebSocket _peer = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<string> _peerReceived = Channel.CreateUnbounded<string>();

    private IncomingConnection? _incoming;
    private Task? _pump;

    private IncomingLoopback()
    {
    }

    /// <summary>The client-facing end: what a <c>SendspinClientService</c> is built over.</summary>
    internal IncomingConnection Incoming =>
        _incoming ?? throw new InvalidOperationException("Loopback was not started");

    internal static async Task<IncomingLoopback> StartAsync()
    {
        var loopback = new IncomingLoopback();
        await loopback.InitializeAsync();
        return loopback;
    }

    /// <summary>Sends a message from the peer, as the server that dialled in would.</summary>
    internal Task SendFromPeerAsync(string json) => _peer.SendAsync(
        Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

    /// <summary>
    /// Whether a text frame matching <paramref name="predicate"/> reaches the peer before the
    /// timeout. Frames that do not match are consumed and skipped. Reports the absence rather
    /// than throwing, so an assertion of either polarity reads the same way.
    /// </summary>
    internal async Task<bool> PeerReceivesAsync(Func<string, bool> predicate, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (await _peerReceived.Reader.WaitToReadAsync(cts.Token))
            {
                while (_peerReceived.Reader.TryRead(out var message))
                {
                    if (predicate(message))
                    {
                        return true;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timed out, or the socket closed with nothing matching: same answer either way.
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        // The peer goes first, and by abort rather than close: several of these tests leave the
        // incoming connection open, and its disposal runs the same closing handshake described
        // above. Aborting makes that fail immediately instead of waiting on a pump that is
        // itself being torn down.
        _cts.Cancel();
        _peer.Abort();
        _peer.Dispose();

        if (_pump is not null)
        {
            await _pump.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        if (_incoming is not null)
        {
            await _incoming.DisposeAsync();
        }

        await _server.DisposeAsync();
        _cts.Dispose();
    }

    private async Task InitializeAsync()
    {
        _server.Start(0); // port 0 = OS-assigned, so parallel tests cannot collide

        var accepted = new TaskCompletionSource<WebSocketClientConnection>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _server.ClientConnected += (_, c) => accepted.TrySetResult(c);

        await _peer.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}/sendspin"), CancellationToken.None);
        var socket = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Constructed before anything is sent from the peer: the server starts its receive loop
        // before raising ClientConnected, and IncomingConnection installs its own OnMessage here.
        _incoming = new IncomingConnection(NullLogger<IncomingConnection>.Instance, socket);
        await _incoming.StartAsync();

        _pump = Task.Run(PumpPeerAsync);
    }

    private async Task PumpPeerAsync()
    {
        var buffer = new byte[8192];
        try
        {
            while (_peer.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _peer.ReceiveAsync(buffer, _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        // CancellationToken.None: _cts is already cancelled during disposal, and
                        // a cancelled reply would reintroduce the silence this pump exists to
                        // prevent.
                        await _peer.CloseOutputAsync(
                            WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                        return;
                    }

                    message.Write(buffer.AsSpan(0, result.Count));
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    _peerReceived.Writer.TryWrite(Encoding.UTF8.GetString(message.ToArray()));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal cancelled the pump.
        }
        catch (WebSocketException)
        {
            // Socket torn down; nothing more will arrive.
        }
        finally
        {
            _peerReceived.Writer.TryComplete();
        }
    }
}
