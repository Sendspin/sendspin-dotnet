using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Sendspin.SDK.Connection;

/// <summary>
/// Wraps a System.Net.WebSockets.WebSocket accepted by SimpleWebSocketServer.
/// Provides event-based message dispatch (OnText, OnBinary, OnClose, OnError)
/// and send methods, replacing Fleck's IWebSocketConnection.
/// </summary>
public sealed class WebSocketClientConnection : IAsyncDisposable
{
    private readonly TcpClient _tcpClient;
    private readonly WebSocket _webSocket;
    private readonly ILogger? _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveLoop;
    private bool _disposed;

    /// <summary>Client IP address.</summary>
    public IPAddress ClientIpAddress { get; }

    /// <summary>Client port.</summary>
    public int ClientPort { get; }

    /// <summary>The HTTP request path used during the WebSocket upgrade.</summary>
    public string Path { get; }

    /// <summary>
    /// Raised when a text message is received, carrying the frame's raw payload bytes.
    /// Bytes rather than a decoded string because the Noise prologue binds the exact bytes
    /// of the two init messages: decoding here and re-encoding downstream is lossy for
    /// input that is not valid UTF-8, and the loss is unrecoverable by the time the framing
    /// sees the frame (#124).
    /// </summary>
    public Action<byte[]>? OnText { get; set; }

    /// <summary>Raised when a binary message is received.</summary>
    public Action<byte[]>? OnBinary { get; set; }

    /// <summary>
    /// Raised when the connection closes, carrying the peer's close status — or <c>null</c>
    /// when the close carried none.
    /// </summary>
    /// <remarks>
    /// Null covers every abnormal end that reaches this callback: a mid-handshake TCP drop, a
    /// keep-alive abort, and the local-teardown fallback in the receive loop's <c>finally</c>.
    /// A subscriber that classifies a close on its status must treat null as <i>unknown</i> and
    /// never as a normal closure — <see cref="IncomingConnection"/> read a statusless close as
    /// the legacy-server signature for want of this parameter (#97).
    /// </remarks>
    public Action<WebSocketCloseStatus?>? OnClose { get; set; }

    /// <summary>Raised when an error occurs.</summary>
    public Action<Exception>? OnError { get; set; }

    public WebSocketClientConnection(
        TcpClient tcpClient,
        WebSocket webSocket,
        IPAddress clientIpAddress,
        int clientPort,
        string path,
        ILogger? logger = null)
    {
        _tcpClient = tcpClient;
        _webSocket = webSocket;
        ClientIpAddress = clientIpAddress;
        ClientPort = clientPort;
        Path = path;
        _logger = logger;
    }

    /// <summary>
    /// Starts the background receive loop that dispatches messages to callbacks.
    /// </summary>
    public void StartReceiving()
    {
        if (_receiveLoop is not null)
            throw new InvalidOperationException("Receive loop already started");

        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Sends a text message.
    /// </summary>
    public async Task SendAsync(string message, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_webSocket.State != WebSocketState.Open)
            throw new InvalidOperationException("WebSocket is not open");

        var bytes = Encoding.UTF8.GetBytes(message);
        await _webSocket.SendAsync(
            bytes.AsMemory(),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a binary message.
    /// </summary>
    public async Task SendAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_webSocket.State != WebSocketState.Open)
            throw new InvalidOperationException("WebSocket is not open");

        await _webSocket.SendAsync(
            data.AsMemory(),
            WebSocketMessageType.Binary,
            endOfMessage: true,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Initiates a graceful WebSocket close.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="WebSocket.CloseOutputAsync"/>, not <see cref="WebSocket.CloseAsync"/>:
    /// the latter performs the full closing handshake and waits for the peer's Close frame,
    /// which a crashed, hung, or non-conformant peer will never send. Every teardown path —
    /// host shutdown, arbitration eviction, and disposal — reaches this through
    /// <see cref="IncomingConnection.DisconnectAsync"/>, all with no cancellation, so waiting
    /// here would block application shutdown forever (#143). CloseOutputAsync sends our Close
    /// frame and returns without waiting, which still gives the peer a clean close.
    /// </remarks>
    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_webSocket.State == WebSocketState.Open ||
            _webSocket.State == WebSocketState.CloseReceived)
        {
            try
            {
                await _webSocket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "closing",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                _logger?.LogDebug(ex, "Error during graceful WebSocket close");
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   _webSocket.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await _webSocket.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        cancellationToken).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        // Complete the close handshake so the client isn't left waiting
                        if (_webSocket.State == WebSocketState.CloseReceived)
                        {
                            try
                            {
                                await _webSocket.CloseOutputAsync(
                                    WebSocketCloseStatus.NormalClosure,
                                    string.Empty,
                                    cancellationToken).ConfigureAwait(false);
                            }
                            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
                            {
                                _logger?.LogDebug(ex, "Error completing close handshake");
                            }
                        }

                        // The only site with a status to report: the peer sent a Close frame.
                        OnClose?.Invoke(result.CloseStatus);
                        return;
                    }

                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var data = ms.ToArray();

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    OnText?.Invoke(data);
                }
                else if (result.MessageType == WebSocketMessageType.Binary)
                {
                    OnBinary?.Invoke(data);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (WebSocketException ex) when (
            _webSocket.State == WebSocketState.Aborted ||
            _webSocket.State == WebSocketState.Closed)
        {
            _logger?.LogDebug(ex, "WebSocket closed during receive");

            // Abnormal: the socket aborted rather than closing, so no Close frame and no
            // status ever arrived.
            OnClose?.Invoke(null);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "WebSocket receive error");
            OnError?.Invoke(ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (_webSocket.State != WebSocketState.Closed &&
                _webSocket.State != WebSocketState.Aborted)
            {
                // The loop left without the peer closing — cancellation, or a local close that
                // moved the socket out of Open. No peer status exists to report.
                OnClose?.Invoke(null);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _cts.CancelAsync();

        if (_receiveLoop is not null)
        {
            try { await _receiveLoop.ConfigureAwait(false); }
            catch { /* Swallow — loop handles its own errors */ }
        }

        _webSocket.Dispose();
        _tcpClient.Dispose();
        _cts.Dispose();
    }
}
