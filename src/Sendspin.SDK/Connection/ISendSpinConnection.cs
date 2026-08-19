using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Connection;

/// <summary>
/// Interface for the Sendspin WebSocket connection.
/// </summary>
public interface ISendspinConnection : IAsyncDisposable
{
    /// <summary>
    /// Current connection state.
    /// </summary>
    ConnectionState State { get; }

    /// <summary>
    /// The URI of the currently connected server.
    /// </summary>
    Uri? ServerUri { get; }

    /// <summary>
    /// Connects to a Sendspin server.
    /// </summary>
    /// <param name="serverUri">WebSocket URI (e.g., ws://host:port/sendspin)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task ConnectAsync(Uri serverUri, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnects from the server.
    /// </summary>
    /// <param name="reason">Reason for disconnection</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task DisconnectAsync(string reason = "restart", CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a JSON protocol message.
    /// </summary>
    Task SendMessageAsync<T>(T message, CancellationToken cancellationToken = default) where T : IMessage;

    /// <summary>
    /// Sends raw binary data.
    /// </summary>
    Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a <c>client/time</c> probe whose <c>client_transmitted</c> (T1) is stamped by the
    /// transport, as close to the socket write as the implementation can manage.
    /// </summary>
    /// <param name="onTransmitted">
    /// Invoked with T1 the moment it is stamped, before the frame reaches the socket and while
    /// the send is still serialized against other sends. Callers register their pending
    /// exchange from here: a reply cannot exist yet, so there is no window in which a
    /// <c>server/time</c> can arrive for a T1 the caller has not recorded. It runs on the
    /// sending thread holding the send lock — do only bookkeeping in it.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// The probe is built by the transport rather than the caller because a T1 captured before
    /// the call is widened by serialization, encryption and any queueing ahead of it: that time
    /// lands in the measured round trip and biases the offset by half of any send/receive
    /// asymmetry. The reference implementation stamps at the same point for the same reason
    /// (sendspin-cpp <c>SendspinConnection::send_time_message</c>).
    /// </remarks>
    Task SendTimeMessageAsync(Action<long> onTransmitted, CancellationToken cancellationToken = default);

    /// <summary>
    /// Event raised when connection state changes.
    /// </summary>
    event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Event raised when a text (JSON) message is received.
    /// </summary>
    event EventHandler<TextMessageReceivedEventArgs>? TextMessageReceived;

    /// <summary>
    /// Event raised when a binary message is received.
    /// </summary>
    event EventHandler<ReadOnlyMemory<byte>>? BinaryMessageReceived;
}

/// <summary>
/// A received text (JSON) message together with the moment the transport saw it.
/// </summary>
/// <remarks>
/// The timestamp exists for the clock-synchronization exchange: T4 must be "the client's
/// receive time (captured locally when the response arrives)", so it is taken in the receive
/// loop before the frame is decrypted and parsed. Reading the clock after parsing folded that
/// work into the measured round trip. Only text carries it — <c>server/time</c> is a text
/// message, and no binary frame is timing-critical in the same way.
/// </remarks>
public sealed class TextMessageReceivedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TextMessageReceivedEventArgs"/> class.
    /// </summary>
    /// <param name="json">The decoded message payload.</param>
    /// <param name="receivedAtMicroseconds">
    /// When the transport saw the frame, in the <c>HighPrecisionTimer</c> time base shared with
    /// audio scheduling and with the T1 stamp.
    /// </param>
    public TextMessageReceivedEventArgs(string json, long receivedAtMicroseconds)
    {
        Json = json;
        ReceivedAtMicroseconds = receivedAtMicroseconds;
    }

    /// <summary>The decoded message payload.</summary>
    public string Json { get; }

    /// <summary>When the transport saw the frame, before decryption and parsing.</summary>
    public long ReceivedAtMicroseconds { get; }
}
