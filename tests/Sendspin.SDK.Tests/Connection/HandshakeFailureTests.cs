using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Framing;

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

    /// <summary>
    /// A framing fatal in transport mode is a desync or a failed server-initiated
    /// re-handshake (key rotation / post-pairing promotion), not a rejected handshake.
    /// Reconnecting re-runs the Noise handshake from scratch via <c>Reset()</c>, which is
    /// exactly the recovery those need — so it must stay on the ordinary reconnect path.
    /// </summary>
    [Fact]
    public async Task TransportModeFatal_RecoversByReconnecting()
    {
        await using var server = new SimpleWebSocketServer();
        server.Start(0);

        var firstConnection = new TaskCompletionSource<WebSocketClientConnection>();
        var secondConnection = new TaskCompletionSource<bool>();
        var dials = 0;
        server.ClientConnected += (_, c) =>
        {
            if (Interlocked.Increment(ref dials) == 1)
                firstConnection.TrySetResult(c);
            else
                secondConnection.TrySetResult(true);
        };

        // Transport-ready when the frame arrives. StubFraming drops IsTransportReady on the
        // fatal exactly as NoiseWireFraming.Fail() does, so a connection that reads the flag
        // after ProcessInbound would misread this as a handshake-time failure.
        await using var connection = new SendspinConnection(
            NullLogger<SendspinConnection>.Instance,
            new ConnectionOptions { AutoReconnect = true, ReconnectDelayMs = 100 },
            new StubFraming { IsTransportReady = true, FatalOnInbound = "desync" });

        var reconnecting = new TaskCompletionSource<bool>();
        connection.StateChanged += (_, e) =>
        {
            if (e.NewState == ConnectionState.Reconnecting)
                reconnecting.TrySetResult(true);
        };

        await connection.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/sendspin"));
        var serverConn = await firstConnection.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Any inbound frame trips the framing's fatal path.
        await serverConn.SendAsync("{}");

        Assert.True(await reconnecting.Task.WaitAsync(TimeSpan.FromSeconds(10)),
            "A transport-mode framing fatal must re-enter the reconnect loop, not fail permanently");

        // Redialling on the ordinary socket schedule, not the 30s handshake backoff: a failed
        // key rotation should recover promptly.
        Assert.True(await secondConnection.Task.WaitAsync(TimeSpan.FromSeconds(10)),
            "Client should redial after a transport-mode fatal");
    }

    /// <summary>
    /// Once the peer has answered, it has proven it speaks the encrypted protocol, so a
    /// clean close is ambiguous (restarting server, draining proxy) rather than the measured
    /// legacy signature — which is a 1000 close with *no reply at all*.
    /// </summary>
    [Fact]
    public async Task CleanCloseAfterServerReplied_IsAmbiguous_AndRetries()
    {
        await using var server = new SimpleWebSocketServer();
        server.Start(0);

        var firstConnection = new TaskCompletionSource<WebSocketClientConnection>();
        var secondConnection = new TaskCompletionSource<bool>();
        var dials = 0;
        server.ClientConnected += (_, c) =>
        {
            if (Interlocked.Increment(ref dials) == 1)
                firstConnection.TrySetResult(c);
            else
                secondConnection.TrySetResult(true);
        };

        // Still mid-handshake (not transport-ready), but the server does reply before closing.
        await using var connection = new SendspinConnection(
            NullLogger<SendspinConnection>.Instance,
            new ConnectionOptions
            {
                AutoReconnect = true,
                ReconnectDelayMs = 100,
                HandshakeFailureBackoffMs = 100,
            },
            new StubFraming { IsTransportReady = false });

        SendspinHandshakeException? permanent = null;
        connection.StateChanged += (_, e) =>
        {
            if (e.Exception is SendspinHandshakeException handshake)
                permanent = handshake;
        };

        await connection.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/sendspin"));
        var serverConn = await firstConnection.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The reply is what distinguishes this from a pre-7.0.0 server: aiosendspin < 7.0.0
        // closes without ever answering client/init.
        await serverConn.SendAsync("{\"type\":\"server/init\"}");
        await Task.Delay(200);
        await serverConn.CloseAsync();

        Assert.True(await secondConnection.Task.WaitAsync(TimeSpan.FromSeconds(10)),
            "A clean close after the server replied is ambiguous and must be retried");
        Assert.Null(permanent);
    }

    [Theory]
    [InlineData(false, "does not support Sendspin encryption")]
    [InlineData(true, "handshake rejected")]
    public void FramingFatal_ClassifiesByTransportReadiness(bool transportReady, string expected)
    {
        var framing = new StubFraming
        {
            IsTransportReady = transportReady,
            FatalOnInbound = "expected server/init message",
        };

        var result = framing.ProcessInbound(WireFrame.FromText("{}"));
        Assert.NotNull(result.FatalReason);

        // This mirrors the classification IncomingConnection applies before closing: the
        // mode is read from `transportReady` (captured before ProcessInbound), not from
        // framing.IsTransportReady after the call — StubFraming, like the real
        // NoiseWireFraming.Fail(), drops IsTransportReady to false as part of going fatal,
        // so a post-call read would collapse both cases to LegacyServer.
        var failure = new SendspinHandshakeException(
            transportReady
                ? HandshakeFailureKind.HandshakeRejected
                : HandshakeFailureKind.LegacyServer,
            result.FatalReason);

        Assert.Contains(expected, failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The test above pins the classification rule, but constructs the exception directly —
    /// it never calls into <see cref="IncomingConnection"/>. This test drives a real
    /// <see cref="IncomingConnection"/> through a real accepted WebSocket, the way
    /// <c>SendSpinHostService</c> does, so it also catches the read-after-ProcessInbound
    /// trap that hit Task 7's dial-path equivalent of this diagnostic: if
    /// <c>IncomingConnection</c> read <c>IsTransportReady</c> after <c>ProcessInbound</c>
    /// instead of before, both cases below would log the <c>LegacyServer</c> message.
    /// </summary>
    [Theory]
    [InlineData(false, "does not support Sendspin encryption")]
    [InlineData(true, "handshake rejected")]
    public async Task IncomingConnection_LogsClassifiedFailure_OnFramingFatal(bool transportReady, string expected)
    {
        await using var server = new SimpleWebSocketServer();
        server.Start(0);

        var accepted = new TaskCompletionSource<WebSocketClientConnection>();
        server.ClientConnected += (_, c) => accepted.TrySetResult(c);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/sendspin"), CancellationToken.None);

        var serverSideSocket = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var logger = new CapturingLogger();
        await using var incoming = new IncomingConnection(
            logger,
            serverSideSocket,
            new StubFraming { IsTransportReady = transportReady, FatalOnInbound = "expected server/init message" });

        var disconnected = new TaskCompletionSource<bool>();
        incoming.StateChanged += (_, e) =>
        {
            if (e.NewState == ConnectionState.Disconnected)
                disconnected.TrySetResult(true);
        };

        await incoming.StartAsync();

        // Mimics a Sendspin server dialing in and sending a message our framing rejects.
        await client.SendAsync(
            Encoding.UTF8.GetBytes("{}"), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        (LogLevel Level, string Message) warning;
        lock (logger.Entries)
        {
            warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        }

        Assert.Contains(expected, warning.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Captures log calls so tests can assert on the classified message text.</summary>
    private sealed class CapturingLogger : ILogger<IncomingConnection>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
            {
                Entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
