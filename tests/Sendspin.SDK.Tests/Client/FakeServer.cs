using System.Net.WebSockets;
using System.Text;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Minimal in-test Sendspin "server": a WebSocket client that connects to the host's listener,
/// completes the handshake by replying to client/hello with a server/hello carrying a chosen
/// server_id and connection_reason, and captures any client/goodbye reason the host sends.
/// </summary>
internal sealed class FakeServer : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly string _serverId;
    private readonly string _connectionReason;
    private readonly TaskCompletionSource<string> _goodbye =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _receiveLoop;

    internal FakeServer(string serverId, string connectionReason)
    {
        _serverId = serverId;
        _connectionReason = connectionReason;
    }

    internal async Task ConnectAsync(int port)
    {
        await _ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/sendspin"), CancellationToken.None);
        _receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    /// <summary>Returns the client/goodbye reason, or null if none arrives before the timeout.</summary>
    internal async Task<string?> WaitForGoodbyeAsync(TimeSpan timeout)
    {
        var completed = await Task.WhenAny(_goodbye.Task, Task.Delay(timeout));
        return completed == _goodbye.Task ? await _goodbye.Task : null;
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[8192];
        try
        {
            while (_ws.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                var type = MessageSerializer.GetMessageType(json);

                if (type == MessageTypes.ClientHello)
                {
                    await SendServerHelloAsync();
                }
                else if (type == MessageTypes.ClientGoodbye)
                {
                    var goodbye = MessageSerializer.Deserialize<ClientGoodbyeMessage>(json);
                    _goodbye.TrySetResult(goodbye?.Payload.Reason ?? string.Empty);
                }
            }
        }
        catch (WebSocketException)
        {
            // Socket torn down (host closed without a goodbye) — leave _goodbye unset.
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SendServerHelloAsync()
    {
        var hello = new ServerHelloMessage
        {
            Payload = new ServerHelloPayload
            {
                ServerId = _serverId,
                Name = _serverId,
                Version = 1,
                ActiveRoles = new List<string> { "player@v1" },
                ConnectionReason = _connectionReason
            }
        };
        var bytes = Encoding.UTF8.GetBytes(MessageSerializer.Serialize(hello));
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test_done", CancellationToken.None);
            }
        }
        catch
        {
            // best-effort close
        }

        _ws.Dispose();
    }
}
