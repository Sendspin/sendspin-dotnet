using Sendspin.SDK.Connection;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// In-memory <see cref="ISendspinConnection"/> test double.
/// Tests drive the <c>SendspinClientService</c> by calling <see cref="RaiseTextMessageReceived"/>
/// instead of running a real WebSocket.
/// </summary>
internal sealed class FakeSendspinConnection : ISendspinConnection
{
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public Uri? ServerUri { get; private set; }
    public List<IMessage> SentMessages { get; } = new();

    /// <summary>
    /// When true, <see cref="SendMessageAsync"/> throws <see cref="InvalidOperationException"/> like the
    /// real <see cref="SendspinConnection"/> when <see cref="State"/> is not
    /// <see cref="ConnectionState.Connected"/>. Off by default so the many tests that drive the client
    /// without connecting keep recording sent messages.
    /// </summary>
    public bool EnforceConnectionState { get; set; }

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;
    public event EventHandler<string>? TextMessageReceived;
    public event EventHandler<ReadOnlyMemory<byte>>? BinaryMessageReceived;

    public Task ConnectAsync(Uri serverUri, CancellationToken cancellationToken = default)
    {
        ServerUri = serverUri;
        SetState(ConnectionState.Connected);
        return Task.CompletedTask;
    }

    /// <summary>The reason passed to the most recent <see cref="DisconnectAsync"/> call.</summary>
    public string? LastDisconnectReason { get; private set; }

    public Task DisconnectAsync(string reason = "user_request", CancellationToken cancellationToken = default)
    {
        LastDisconnectReason = reason;
        SetState(ConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    public Task SendMessageAsync<T>(T message, CancellationToken cancellationToken = default)
        where T : IMessage
    {
        if (EnforceConnectionState && State != ConnectionState.Connected)
        {
            throw new InvalidOperationException("WebSocket is not connected");
        }

        SentMessages.Add(message);
        return Task.CompletedTask;
    }

    /// <summary>Binary frames sent via <see cref="SendBinaryAsync"/>, in order.</summary>
    public List<byte[]> SentBinary { get; } = new();

    public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        SentBinary.Add(data.ToArray());
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void RaiseTextMessageReceived(string json)
        => TextMessageReceived?.Invoke(this, json);

    public void RaiseBinaryMessageReceived(ReadOnlyMemory<byte> data)
        => BinaryMessageReceived?.Invoke(this, data);

    private void SetState(ConnectionState newState)
    {
        var old = State;
        State = newState;
        StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs { OldState = old, NewState = newState });
    }
}
