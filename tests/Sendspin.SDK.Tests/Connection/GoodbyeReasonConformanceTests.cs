using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Connection;

/// <summary>
/// <c>client/goodbye</c> reasons are a closed set in the spec (messaging.md:426), and the
/// server's behaviour branches on them: a client that sends nothing — or a reason the server
/// cannot parse — is treated as <c>restart</c> and auto-reconnected (messaging.md:442). That
/// is the wrong outcome when the app is being closed, and it produced a live failure: the
/// server kept reconnecting to a client that had exited, then collided with the client's own
/// dial on relaunch.
/// </summary>
[Collection("RealSockets")]
public class GoodbyeReasonConformanceTests : IAsyncDisposable
{
    /// <summary>The spec's closed set, messaging.md:426.</summary>
    private static readonly string[] SpecReasons =
    [
        "another_server", "shutdown", "restart", "user_request",
        "unauthorized", "pairing_required", "concurrent_attempt", "unpaired",
    ];

    private readonly SimpleWebSocketServer _server = new();

    [Fact]
    public void GoodbyeReasons_AreExactlyTheSpecsClosedSet()
    {
        // A constant per reason, so a call site cannot invent a string the server will not
        // understand. Exact-set equality rather than a subset check: an extra member here
        // would be a reason the spec does not define, which is the defect this pins.
        Assert.Equal(
            SpecReasons.OrderBy(r => r, StringComparer.Ordinal),
            GoodbyeReasons.All.OrderBy(r => r, StringComparer.Ordinal));
    }

    [Fact]
    public async Task DisposingAConnection_SaysShutdown_NotAnUnparseableReason()
    {
        // The app is closing and is not coming back, which is exactly what the spec reserves
        // 'shutdown' for (messaging.md:436). Before this, DisposeAsync sent "disposing" — a
        // string that appears nowhere in the spec, so a conformant server falls back to the
        // no-goodbye rule and auto-reconnects to a client that has gone away.
        _server.Start(0);

        var goodbye = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allReceived = new List<string>();
        _server.ClientConnected += (_, c) =>
            c.OnText = data =>
            {
                string text = Encoding.UTF8.GetString(data);
                lock (allReceived)
                {
                    allReceived.Add(text);
                }

                if (text.Contains("client/goodbye", StringComparison.Ordinal))
                    goodbye.TrySetResult(text);
            };

        // StubFraming keeps the framing transport-ready, so the goodbye is actually encoded
        // and sent rather than throwing on the way out (see SendspinConnectionReconnectTests).
        var connection = new SendspinConnection(
            NullLogger<SendspinConnection>.Instance,
            new ConnectionOptions { AutoReconnect = false },
            new StubFraming());

        await connection.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}/sendspin"));
        await connection.DisposeAsync();

        bool arrived = await Task.WhenAny(goodbye.Task, Task.Delay(TimeSpan.FromSeconds(5))) == goodbye.Task;
        string seen;
        lock (allReceived)
        {
            seen = allReceived.Count == 0 ? "(nothing)" : string.Join(" | ", allReceived);
        }

        Assert.True(arrived, $"no client/goodbye reached the server. Frames received: {seen}");
        Assert.Contains("\"reason\":\"shutdown\"", goodbye.Task.Result, StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
