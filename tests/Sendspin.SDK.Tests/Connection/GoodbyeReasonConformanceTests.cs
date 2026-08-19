using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
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
    public void EveryGoodbyeIsBuiltThroughTheSink_WhichSubstitutesRestartForAnUndefinedReason()
    {
        // The guard is at the sink, not at each call site: three callers in a row reached the
        // wire with a string the spec does not define, so validating where the message is built
        // is the only place a fourth cannot slip past. 'restart' because that is already what a
        // server assumes for a reason it cannot parse (messaging.md:442) — the substitution
        // makes the frame conformant without changing the server's reaction to it.
        Assert.Equal(
            GoodbyeReasons.Restart,
            ClientGoodbyeMessage.Create("switching_connection_mode").Payload.Reason);

        // ...and the guard does not swallow the reasons the spec does define.
        foreach (string reason in SpecReasons)
        {
            Assert.Equal(reason, ClientGoodbyeMessage.Create(reason).Payload.Reason);
        }
    }

    [Fact]
    public void DisconnectAllAsync_DefaultsToAReasonTheSpecDefines()
    {
        // The default reaches the wire verbatim, so it is part of the protocol surface. It was
        // "switching_connection_mode" — undefined, hence a silent drop the server reads as a
        // crash and auto-reconnects from, colliding with the client's own dial. 'another_server'
        // is the reason the spec makes mandatory for a client leaving one server for another,
        // and the one whose documented server reaction is: do not auto-reconnect, but keep
        // showing the client as available (messaging.md:426).
        var disconnectAll = typeof(SendspinHostService)
            .GetMethod(nameof(SendspinHostService.DisconnectAllAsync));
        Assert.NotNull(disconnectAll);

        Assert.Equal(
            GoodbyeReasons.AnotherServer,
            disconnectAll.GetParameters()[0].DefaultValue as string);
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
