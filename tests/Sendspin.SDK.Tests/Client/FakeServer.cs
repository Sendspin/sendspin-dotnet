using System.Buffers.Text;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Noise;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Tests.Connection;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Minimal in-test Sendspin "server": a WebSocket client that connects to the host's listener
/// and speaks the encrypted protocol as the Noise initiator — answering client/init with
/// server/init plus handshake message 1, then sending an encrypted server/hello and the
/// server/activate that completes the host's handshake and sets its arbitration priority.
/// Captures any client/goodbye reason the host sends back through the encrypted channel.
/// </summary>
internal class FakeServer : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly byte[] _psk;
    private readonly IReadOnlyList<string> _activities;
    private readonly KeyPair _keys;
    private readonly TaskCompletionSource<string> _goodbye =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TestNoiseServer? _noise;
    private Task? _receiveLoop;

    /// <param name="psk">Must match a PSK the host's pairing record store resolves.</param>
    /// <param name="activities">server/activate activities: ["playback"], or empty for discovery.</param>
    /// <param name="keys">Pass an existing pair so two instances share one server_id.</param>
    internal FakeServer(byte[] psk, IReadOnlyList<string> activities, KeyPair? keys = null)
    {
        _psk = psk;
        _activities = activities;
        _keys = keys ?? KeyPair.Generate();

        // server_id IS the static public key: the host decodes it as the Noise remote
        // static, so it has to be a real 43-char base64url Curve25519 key.
        ServerId = Base64Url.EncodeToString(_keys.PublicKey);
    }

    /// <summary>The real base64url Curve25519 server id, generated or taken from the injected keys.</summary>
    internal string ServerId { get; }

    internal async Task ConnectAsync(int port)
    {
        await _ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/sendspin"), _cts.Token);
        _receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    /// <summary>Sends an encrypted application JSON message. Valid once the handshake completed.</summary>
    internal Task SendJsonAsync(string json) => SendEncryptedAsync(json);

    /// <summary>Returns the client/goodbye reason, or null if none arrives before the timeout.</summary>
    internal async Task<string?> WaitForGoodbyeAsync(TimeSpan timeout)
    {
        var completed = await Task.WhenAny(_goodbye.Task, Task.Delay(timeout));
        return completed == _goodbye.Task ? await _goodbye.Task : null;
    }

    private async Task ReceiveLoopAsync()
    {
        // Frames are reassembled to EndOfMessage: a Noise ciphertext only decrypts whole,
        // and every transport frame must be read in order to keep the nonces in step.
        var buffer = new byte[16384];
        using var message = new MemoryStream();
        try
        {
            while (_ws.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await OnCloseReceivedAsync();
                        return;
                    }

                    message.Write(buffer.AsSpan(0, result.Count));
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    await HandleHandshakeTextAsync(Encoding.UTF8.GetString(message.ToArray()));
                }
                else
                {
                    HandleEncryptedFrame(message.ToArray());
                }
            }
        }
        catch (WebSocketException)
        {
            // Socket torn down (host closed without a goodbye) — leave _goodbye unset.
        }
        catch (OperationCanceledException)
        {
            // Disposal cancelled the receive loop.
        }
        catch (Exception ex)
        {
            // A decrypt or parse failure is exactly what this double exists to catch, and it
            // would otherwise only fault _receiveLoop — which DisposeAsync discards, leaving a
            // bare 30s timeout as the sole symptom. Surface it through the channel the tests
            // already await so it lands as an immediate stack trace.
            _goodbye.TrySetException(ex);
        }
    }

    /// <summary>
    /// Answers the host's close handshake. Overridden by <see cref="SilentCloseFakeServer"/> to
    /// model the non-conformant peer that exposed #143: one that never replies to a Close frame.
    /// Uses CancellationToken.None, not _cts.Token: during disposal _cts is already cancelled,
    /// so the token would cancel the reply immediately and reintroduce the silence.
    /// </summary>
    protected virtual async Task OnCloseReceivedAsync()
    {
        if (_ws.State != WebSocketState.CloseReceived)
        {
            return;
        }

        try
        {
            await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
        }
        catch
        {
            // best-effort: the socket may already be gone
        }
    }

    /// <summary>Handles the two cleartext handshake messages the host sends: client/init and noise/handshake.</summary>
    private async Task HandleHandshakeTextAsync(string json)
    {
        switch (MessageSerializer.GetMessageType(json))
        {
            case "client/init":
            {
                using var doc = JsonDocument.Parse(json);
                string clientId = doc.RootElement.GetProperty("payload").GetProperty("client_id").GetString()!;
                _noise = new TestNoiseServer(SendspinIdentity.DecodePeerId(clientId), _psk, _keys);

                // The prologue binds the exact wire bytes, so Respond gets the text as received.
                var (serverInit, msg1) = _noise.Respond(json);
                await SendTextAsync(serverInit);
                await SendTextAsync(msg1);
                break;
            }

            case "noise/handshake":
            {
                _noise!.CompleteHandshake(json);
                await SendEncryptedAsync("""{"type":"server/hello","payload":{"name":"fake"}}""");
                await SendEncryptedAsync(BuildActivateJson());
                break;
            }
        }
    }

    /// <summary>
    /// Decrypts a transport frame and captures client/goodbye. Every frame is decrypted even
    /// when ignored: skipping one would desynchronize the Noise nonce for the rest.
    /// </summary>
    private void HandleEncryptedFrame(byte[] frame)
    {
        if (_noise is null)
        {
            return;
        }

        byte[] plaintext = _noise.DecryptFrame(frame);
        if (plaintext.Length == 0 || plaintext[0] != 0)
        {
            // Not a JSON application message (binary or fragment): nothing to inspect.
            return;
        }

        string json = Encoding.UTF8.GetString(plaintext, 1, plaintext.Length - 1);
        if (MessageSerializer.GetMessageType(json) == MessageTypes.ClientGoodbye)
        {
            var goodbye = MessageSerializer.Deserialize<ClientGoodbyeMessage>(json);
            _goodbye.TrySetResult(goodbye?.Payload.Reason ?? string.Empty);
        }
    }

    private string BuildActivateJson() => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["type"] = "server/activate",
        ["payload"] = new Dictionary<string, object>
        {
            ["activities"] = _activities,
            ["active_roles"] = Array.Empty<string>(),
        },
    });

    private Task SendTextAsync(string json) =>
        _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

    private Task SendEncryptedAsync(string json)
    {
        byte[] frame = _noise!.EncryptFrame([0, .. Encoding.UTF8.GetBytes(json)]);
        return _ws.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

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

        if (_receiveLoop is not null)
        {
            await _receiveLoop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        _cts.Dispose();
    }
}
