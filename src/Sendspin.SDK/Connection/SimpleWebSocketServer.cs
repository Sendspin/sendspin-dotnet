using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Sendspin.SDK.Connection;

/// <summary>
/// Minimal WebSocket server using TcpListener + WebSocket.CreateFromStream().
/// Replaces Fleck for NativeAOT compatibility — no HTTP.sys, no admin privileges.
/// </summary>
public sealed partial class SimpleWebSocketServer : IAsyncDisposable
{
    /// <summary>
    /// The WebSocket GUID used in the Sec-WebSocket-Accept computation per RFC 6455 Section 4.2.2.
    /// Note: Many online references incorrectly cite this as ending in "5AB0DC85B11C".
    /// The correct GUID from the RFC is "258EAFA5-E914-47DA-95CA-C5AB0DC85B11".
    /// </summary>
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private const int MaxHttpHeaderSize = 8192;

    private readonly ILogger? _logger;
    private readonly ConnectionOptions _connectionOptions;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private bool _disposed;

    /// <summary>
    /// Port the server is listening on.
    /// </summary>
    public int Port { get; private set; }

    /// <summary>
    /// Raised when a new WebSocket client connects. The handler receives a
    /// <see cref="WebSocketClientConnection"/> with the receive loop already started.
    /// </summary>
    public event EventHandler<WebSocketClientConnection>? ClientConnected;

    /// <param name="logger">Optional logger.</param>
    /// <param name="connectionOptions">
    /// Supplies the keep-alive settings applied to every accepted WebSocket. Defaults to
    /// <see cref="ConnectionOptions"/>'s own defaults, which match the dial path.
    /// </param>
    public SimpleWebSocketServer(ILogger? logger = null, ConnectionOptions? connectionOptions = null)
    {
        _logger = logger;
        _connectionOptions = connectionOptions ?? new ConnectionOptions();
    }

    /// <summary>
    /// Starts listening for incoming WebSocket connections.
    /// </summary>
    /// <param name="port">Port to bind to.</param>
    public void Start(int port)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_listener is not null)
            throw new InvalidOperationException("Server is already running");

        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();

        // Read the actual bound port (important when port 0 is used for OS-assigned port)
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));

        _logger?.LogInformation("WebSocket server listening on port {Port}", Port);
    }

    /// <summary>
    /// Stops the server and closes all pending accept operations.
    /// </summary>
    public async Task StopAsync()
    {
        if (_listener is null) return;

        _logger?.LogInformation("Stopping WebSocket server");

        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }

        _listener.Stop();

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch { /* Swallow — loop handles its own errors */ }
        }

        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var tcpClient = await _listener!.AcceptTcpClientAsync(cancellationToken)
                    .ConfigureAwait(false);

                // Handle each connection concurrently
                _ = HandleConnectionAsync(tcpClient, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error accepting TCP connection");
            }
        }
    }

    private async Task HandleConnectionAsync(TcpClient tcpClient, CancellationToken cancellationToken)
    {
        var remoteEndPoint = tcpClient.Client.RemoteEndPoint as IPEndPoint;
        try
        {
            var stream = tcpClient.GetStream();

            // Read the HTTP upgrade request. Headers may span multiple TCP segments,
            // so loop until the \r\n\r\n terminator is found.
            var request = await ReadHttpHeaderAsync(stream, cancellationToken).ConfigureAwait(false);

            if (request is null)
            {
                tcpClient.Dispose();
                return;
            }

            // Parse the request
            var pathMatch = GetRequestLineRegex().Match(request);
            if (!pathMatch.Success)
            {
                _logger?.LogWarning("Invalid HTTP request from {Endpoint}", remoteEndPoint);
                await SendHttpResponse(stream, 400, "Bad Request", cancellationToken);
                tcpClient.Dispose();
                return;
            }

            var path = pathMatch.Groups[1].Value;

            // Extract WebSocket key
            var keyMatch = WebSocketKeyHeaderRegex().Match(request);
            if (!keyMatch.Success)
            {
                _logger?.LogWarning("Missing Sec-WebSocket-Key from {Endpoint}", remoteEndPoint);
                await SendHttpResponse(stream, 400, "Missing Sec-WebSocket-Key", cancellationToken);
                tcpClient.Dispose();
                return;
            }

            var webSocketKey = keyMatch.Groups[1].Value;

            // Compute Sec-WebSocket-Accept per RFC 6455
            var acceptKey = ComputeAcceptKey(webSocketKey);

            // Send HTTP 101 Switching Protocols
            var response = $"HTTP/1.1 101 Switching Protocols\r\n" +
                           $"Upgrade: websocket\r\n" +
                           $"Connection: Upgrade\r\n" +
                           $"Sec-WebSocket-Accept: {acceptKey}\r\n" +
                           $"\r\n";

            var responseBytes = Encoding.UTF8.GetBytes(response);
            await stream.WriteAsync(responseBytes.AsMemory(), cancellationToken)
                .ConfigureAwait(false);

            // Create WebSocket from the stream
            var webSocket = WebSocket.CreateFromStream(stream, BuildWebSocketOptions());

            var clientIp = remoteEndPoint?.Address ?? IPAddress.Loopback;
            var clientPort = remoteEndPoint?.Port ?? 0;

            var connection = new WebSocketClientConnection(
                tcpClient,
                webSocket,
                clientIp,
                clientPort,
                path,
                _logger);

            connection.StartReceiving();

            _logger?.LogDebug("WebSocket connection established from {Endpoint} on path {Path}",
                remoteEndPoint, path);

            ClientConnected?.Invoke(this, connection);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error handling WebSocket upgrade from {Endpoint}", remoteEndPoint);
            tcpClient.Dispose();
        }
    }

    /// <summary>
    /// Builds the creation options for an accepted WebSocket, giving it the same
    /// <see cref="ConnectionOptions"/>-driven keep-alive the dial path configures on its
    /// <see cref="System.Net.WebSockets.ClientWebSocket"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="WebSocketCreationOptions.KeepAliveInterval"/> defaults to
    /// <see cref="TimeSpan.Zero"/> — keep-alive disabled — unlike ClientWebSocket. Left at that,
    /// an accepted connection sends nothing and a peer that dies without a FIN/RST (power loss,
    /// network partition, sleeping laptop) stays ESTABLISHED until the OS TCP timeout, ten
    /// minutes or more later, holding its arbitration slot the whole time.
    /// </remarks>
    private WebSocketCreationOptions BuildWebSocketOptions()
    {
        var options = new WebSocketCreationOptions
        {
            IsServer = true,
            KeepAliveInterval = TimeSpan.FromMilliseconds(_connectionOptions.KeepAliveIntervalMs),
        };

#if NET9_0_OR_GREATER
        // PING/PONG keep-alive: abort the socket if no PONG arrives in time, so a
        // half-open connection (frozen peer / network drop without a TCP FIN) surfaces
        // as a faulted ReceiveAsync instead of blocking forever. .NET 9+ only.
        if (_connectionOptions.KeepAliveTimeoutMs > 0)
        {
            options.KeepAliveTimeout = TimeSpan.FromMilliseconds(_connectionOptions.KeepAliveTimeoutMs);
        }
#else
        if (_connectionOptions.KeepAliveTimeoutMs > 0)
        {
            _logger?.LogDebug(
                "KeepAliveTimeoutMs is set but has no effect on this runtime (requires .NET 9+); " +
                "half-open connections are detected only by the OS TCP timeout.");
        }
#endif

        return options;
    }

    private static async Task<string?> ReadHttpHeaderAsync(
        NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxHttpHeaderSize];
        var totalRead = 0;

        while (totalRead < MaxHttpHeaderSize)
        {
            var bytesRead = await stream.ReadAsync(
                buffer.AsMemory(totalRead, MaxHttpHeaderSize - totalRead),
                cancellationToken).ConfigureAwait(false);

            if (bytesRead == 0)
                return null; // Client disconnected

            totalRead += bytesRead;

            // Check for end-of-headers marker in the newly read portion.
            // The marker could span the boundary between reads, so search
            // from a few bytes back to cover that case.
            var searchStart = Math.Max(0, totalRead - bytesRead - 3);
            var headerEnd = buffer.AsSpan(0, totalRead).Slice(searchStart)
                .IndexOf("\r\n\r\n"u8);

            if (headerEnd >= 0)
                return Encoding.UTF8.GetString(buffer, 0, searchStart + headerEnd + 4);
        }

        // Headers exceeded max size — reject
        return null;
    }

    private static string ComputeAcceptKey(string webSocketKey)
    {
        var combined = webSocketKey + WebSocketGuid;
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(combined));
        return Convert.ToBase64String(hash);
    }

    private static async Task SendHttpResponse(
        NetworkStream stream, int statusCode, string reason,
        CancellationToken cancellationToken)
    {
        var response = $"HTTP/1.1 {statusCode} {reason}\r\nContent-Length: 0\r\n\r\n";
        var bytes = Encoding.UTF8.GetBytes(response);
        await stream.WriteAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    [GeneratedRegex(@"^GET\s+(\S+)\s+HTTP/1\.1")]
    private static partial Regex GetRequestLineRegex();

    [GeneratedRegex(@"Sec-WebSocket-Key:\s*(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex WebSocketKeyHeaderRegex();

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync();
    }
}
