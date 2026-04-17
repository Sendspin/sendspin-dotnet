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

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;
    public event EventHandler<string>? TextMessageReceived;
    public event EventHandler<ReadOnlyMemory<byte>>? BinaryMessageReceived;

    public Task ConnectAsync(Uri serverUri, CancellationToken cancellationToken = default)
    {
        ServerUri = serverUri;
        SetState(ConnectionState.Connected);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(string reason = "user_request", CancellationToken cancellationToken = default)
    {
        SetState(ConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    public Task SendMessageAsync<T>(T message, CancellationToken cancellationToken = default)
        where T : IMessage
    {
        SentMessages.Add(message);
        return Task.CompletedTask;
    }

    public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

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
