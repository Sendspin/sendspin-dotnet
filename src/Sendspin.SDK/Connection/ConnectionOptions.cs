namespace Sendspin.SDK.Connection;

/// <summary>
/// Configuration options for the Sendspin connection.
/// </summary>
public sealed class ConnectionOptions
{
    /// <summary>
    /// Maximum number of reconnection attempts before giving up.
    /// Set to -1 for infinite retries.
    /// </summary>
    public int MaxReconnectAttempts { get; set; } = -1;

    /// <summary>
    /// Initial delay between reconnection attempts in milliseconds.
    /// </summary>
    public int ReconnectDelayMs { get; set; } = 1000;

    /// <summary>
    /// Maximum delay between reconnection attempts in milliseconds.
    /// </summary>
    public int MaxReconnectDelayMs { get; set; } = 30000;

    /// <summary>
    /// Multiplier for exponential backoff.
    /// </summary>
    public double ReconnectBackoffMultiplier { get; set; } = 2.0;

    /// <summary>
    /// Connection timeout in milliseconds.
    /// </summary>
    public int ConnectTimeoutMs { get; set; } = 10000;

    /// <summary>
    /// Interval for sending keep-alive pings in milliseconds.
    /// Set to 0 to disable.
    /// </summary>
    public int KeepAliveIntervalMs { get; set; } = 15000;

    /// <summary>
    /// Time to wait for a keep-alive PONG before declaring the connection dead.
    /// Enables the PING/PONG keep-alive strategy, which detects a half-open socket
    /// (frozen peer, network drop with no TCP FIN). With a 15s interval, a dead peer
    /// is detected within ~30s.
    /// Set to 0 to turn off PONG-timeout detection: keep-alive pings still run on
    /// <see cref="KeepAliveIntervalMs"/>, but a dead peer is then only detected by the
    /// (much longer) OS TCP timeout.
    /// Requires .NET 9+ on the client WebSocket; on net8.0 this is ignored and
    /// detection always falls back to the OS TCP timeout.
    /// </summary>
    public int KeepAliveTimeoutMs { get; set; } = 15000;

    /// <summary>
    /// Buffer size for receiving WebSocket messages.
    /// </summary>
    public int ReceiveBufferSize { get; set; } = 64 * 1024; // 64KB

    /// <summary>
    /// Whether to automatically reconnect on connection loss.
    /// </summary>
    public bool AutoReconnect { get; set; } = true;
}
