using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Connection;

namespace Sendspin.SDK.Tests.Connection;

/// <summary>
/// A pre-7.0.0 aiosendspin server closes with code 1000 and no reply when it receives
/// client/init. That is a permanent condition: v10 speaks only the encrypted protocol,
/// so retrying cannot help and the SDK must say so.
/// </summary>
[Collection("RealSockets")]
public class HandshakeFailureTests
{
    // StubFraming is shared with the reconnect tests — see StubFraming.cs.

    [Fact]
    public void HandshakeFailureBackoff_DefaultsTo30Seconds()
    {
        Assert.Equal(30000, new ConnectionOptions().HandshakeFailureBackoffMs);
    }

    [Fact]
    public void LegacyServerException_CarriesTheUpgradeGuidance()
    {
        var ex = new SendspinHandshakeException(HandshakeFailureKind.LegacyServer);

        Assert.Equal(HandshakeFailureKind.LegacyServer, ex.Kind);
        Assert.Contains("does not support Sendspin encryption", ex.Message);
        Assert.Contains("aiosendspin >= 7.0.0", ex.Message);
        Assert.Contains("9.x", ex.Message);
    }

    [Fact]
    public void HandshakeRejectedException_NamesTheReason()
    {
        var ex = new SendspinHandshakeException(HandshakeFailureKind.HandshakeRejected, "unsupported suite");

        Assert.Equal(HandshakeFailureKind.HandshakeRejected, ex.Kind);
        Assert.Contains("unsupported suite", ex.Message);
    }

    [Fact]
    public async Task CleanCloseBeforeTransportMode_IsLegacyServer_AndNeverRetries()
    {
        await using var server = new SimpleWebSocketServer();
        server.Start(0);

        var accepted = new TaskCompletionSource<WebSocketClientConnection>();
        var dials = 0;
        server.ClientConnected += (_, c) =>
        {
            Interlocked.Increment(ref dials);
            accepted.TrySetResult(c);
        };

        // Framing that never reaches transport mode: the pre-7.0.0 server signature.
        await using var connection = new SendspinConnection(
            NullLogger<SendspinConnection>.Instance,
            new ConnectionOptions { AutoReconnect = true, ReconnectDelayMs = 10 },
            new StubFraming { IsTransportReady = false });

        var states = new List<ConnectionState>();
        var disconnected = new TaskCompletionSource<ConnectionStateChangedEventArgs>();
        connection.StateChanged += (_, e) =>
        {
            lock (states)
            {
                states.Add(e.NewState);
            }

            if (e.NewState == ConnectionState.Disconnected)
                disconnected.TrySetResult(e);
        };

        await connection.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/sendspin"));
        var serverConn = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // aiosendspin < 7.0.0 answers client/init with a normal-closure close, no reply.
        await serverConn.CloseAsync();

        var final = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Give a stray reconnect (10ms socket-drop delay) every chance to fire before
        // asserting it didn't.
        await Task.Delay(500);

        Assert.Equal(ConnectionState.Disconnected, connection.State);
        lock (states)
        {
            // Handshaking proves the socket really came up: without it, "never retried"
            // could pass for the unrelated reason that the dial never succeeded at all.
            Assert.Contains(ConnectionState.Handshaking, states);
            Assert.DoesNotContain(ConnectionState.Reconnecting, states);
        }

        Assert.Equal(1, Volatile.Read(ref dials));

        var ex = Assert.IsType<SendspinHandshakeException>(final.Exception);
        Assert.Equal(HandshakeFailureKind.LegacyServer, ex.Kind);
    }

    [Fact]
    public async Task FramingFatal_DoesNotReenterTheReconnectLoop()
    {
        await using var server = new SimpleWebSocketServer();
        server.Start(0);

        var accepted = new TaskCompletionSource<WebSocketClientConnection>();
        var dials = 0;
        server.ClientConnected += (_, c) =>
        {
            Interlocked.Increment(ref dials);
            accepted.TrySetResult(c);
        };

        await using var connection = new SendspinConnection(
            NullLogger<SendspinConnection>.Instance,
            new ConnectionOptions { AutoReconnect = true, ReconnectDelayMs = 10 },
            new StubFraming { IsTransportReady = false, FatalOnInbound = "bad psk" });

        var states = new List<ConnectionState>();
        var disconnected = new TaskCompletionSource<ConnectionStateChangedEventArgs>();
        connection.StateChanged += (_, e) =>
        {
            lock (states)
            {
                states.Add(e.NewState);
            }

            if (e.NewState == ConnectionState.Disconnected)
                disconnected.TrySetResult(e);
        };

        await connection.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/sendspin"));
        var serverConn = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Any inbound frame trips the framing's fatal path.
        await serverConn.SendAsync("{}");

        var final = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(500);

        Assert.Equal(ConnectionState.Disconnected, connection.State);
        lock (states)
        {
            Assert.Contains(ConnectionState.Handshaking, states);
            Assert.DoesNotContain(ConnectionState.Reconnecting, states);
        }

        Assert.Equal(1, Volatile.Read(ref dials));

        var ex = Assert.IsType<SendspinHandshakeException>(final.Exception);
        Assert.Equal(HandshakeFailureKind.HandshakeRejected, ex.Kind);
        Assert.Contains("bad psk", ex.Message);
    }

    [Fact]
    public async Task AmbiguousHandshakeDrop_BacksOffOnTheHandshakeSchedule()
    {
        await using var server = new SimpleWebSocketServer();
        server.Start(0);

        var accepted = new TaskCompletionSource<WebSocketClientConnection>();
        server.ClientConnected += (_, c) => accepted.TrySetResult(c);

        // 5s handshake backoff vs a 50ms socket-drop delay: if the wrong schedule is
        // used, the client redials almost immediately and is no longer Reconnecting.
        await using var connection = new SendspinConnection(
            NullLogger<SendspinConnection>.Instance,
            new ConnectionOptions
            {
                AutoReconnect = true,
                ReconnectDelayMs = 50,
                HandshakeFailureBackoffMs = 5000,
            },
            new StubFraming { IsTransportReady = false });

        var sawHandshaking = false;
        connection.StateChanged += (_, e) =>
        {
            if (e.NewState == ConnectionState.Handshaking)
                sawHandshaking = true;
        };

        await connection.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/sendspin"));
        var serverConn = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Tear the socket down abruptly rather than closing cleanly: an abort
        // mid-handshake is the ambiguous case, not the legacy-server signature.
        await serverConn.DisposeAsync();
        await Task.Delay(1000);

        Assert.True(sawHandshaking,
            "Client should reach Handshaking first, so the backoff under test is a mid-handshake drop");
        Assert.Equal(ConnectionState.Reconnecting, connection.State);
    }
}
