using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Framing;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Tests.Client;

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

    /// <summary>
    /// The listen path classifies a framing fatal exactly as the dial path does: a fatal
    /// raised before transport mode is a rejected handshake (no matching PSK, PSK bound to
    /// another server, unsupported version), and a fatal on an established session is a
    /// desync. Neither is the legacy-server signature — a pre-7.0.0 server never sends
    /// anything at all, so it cannot produce a fatal (see the close-handler test below).
    /// This drives a real <see cref="IncomingConnection"/> through a real accepted WebSocket,
    /// the way <c>SendSpinHostService</c> does, so it also catches the read-after-
    /// <c>ProcessInbound</c> trap: <c>StubFraming</c>, like the real
    /// <c>NoiseWireFraming.Fail()</c>, drops <c>IsTransportReady</c> as part of going fatal,
    /// so a post-call read would collapse both cases below into the first.
    /// </summary>
    [Theory]
    [InlineData(false, "handshake rejected")]
    [InlineData(true, "established session")]
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

    /// <summary>
    /// The case the LegacyServer diagnostic exists for: a pre-7.0.0 server dials in, receives
    /// the client/init sent from StartAsync, fails to deserialize it, and closes without ever
    /// replying. No frame arrives, so there is no framing fatal — the close handler is the
    /// only place this is visible, and without the diagnostic it reads as an ordinary
    /// "Server closed connection" at Information.
    /// </summary>
    [Fact]
    public async Task IncomingConnection_LogsLegacyServerDiagnostic_OnCloseBeforeTransportMode()
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
            logger, serverSideSocket, new StubFraming { IsTransportReady = false });

        var disconnected = new TaskCompletionSource<bool>();
        incoming.StateChanged += (_, e) =>
        {
            if (e.NewState == ConnectionState.Disconnected)
                disconnected.TrySetResult(true);
        };

        await incoming.StartAsync();

        // The measured pre-7.0.0 signature: a normal-closure close with no reply at all.
        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);

        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        (LogLevel Level, string Message) warning;
        lock (logger.Entries)
        {
            warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        }

        Assert.Contains("does not support Sendspin encryption", warning.Message);
        Assert.Contains("aiosendspin >= 7.0.0", warning.Message);
    }

    /// <summary>
    /// Once the peer has answered, it has proven it speaks the encrypted protocol, so a close
    /// is ambiguous (restarting server, draining proxy) rather than the legacy signature.
    /// </summary>
    [Fact]
    public async Task IncomingConnection_DoesNotBlameLegacyServer_WhenThePeerReplied()
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
            logger, serverSideSocket, new StubFraming { IsTransportReady = false });

        var received = new TaskCompletionSource<bool>();
        incoming.TextMessageReceived += (_, _) => received.TrySetResult(true);

        var disconnected = new TaskCompletionSource<bool>();
        incoming.StateChanged += (_, e) =>
        {
            if (e.NewState == ConnectionState.Disconnected)
                disconnected.TrySetResult(true);
        };

        await incoming.StartAsync();

        var reply = Encoding.UTF8.GetBytes("{\"type\":\"server/init\"}");
        await client.SendAsync(reply, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lock (logger.Entries)
        {
            Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
        }
    }

    /// <summary>
    /// The documented Quick Start never subscribes to ConnectionStateChanged, so a permanent
    /// handshake failure that only travels on that event leaves the app with a ConnectAsync
    /// that returned normally and a first command that throws "WebSocket is not connected".
    /// ConnectAsync must surface the diagnostic itself.
    /// </summary>
    /// <param name="closeDelayMs">
    /// Both orderings of the same failure. With no delay the connection fails before the
    /// handshake wait has published its TaskCompletionSource, so the failure has to be
    /// recorded and re-checked; with a delay the wait is already parked on the TCS and the
    /// exception is delivered through it. Before the fix the first hung for the full 30 s and
    /// reported TimeoutException, and the second returned as though the connect had succeeded.
    /// </param>
    [Theory]
    [InlineData(0)]
    [InlineData(250)]
    public async Task ConnectAsync_ThrowsHandshakeException_WhenTheHandshakeFailsPermanently(int closeDelayMs)
    {
        await using var server = new SimpleWebSocketServer();
        server.Start(0);

        // The pre-7.0.0 signature: accept, then close with 1000 without ever replying.
        server.ClientConnected += (_, c) => _ = Task.Run(async () =>
        {
            if (closeDelayMs > 0)
                await Task.Delay(closeDelayMs);

            await c.CloseAsync();
        });

        await using var connection = new SendspinConnection(
            NullLogger<SendspinConnection>.Instance,
            new ConnectionOptions { AutoReconnect = true, ReconnectDelayMs = 10 },
            new StubFraming { IsTransportReady = false });

        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection,
            new FakeNoiseSession(),
            new SendspinClientOptions { Identity = SendspinIdentity.Generate() });

        var ex = await Assert.ThrowsAsync<SendspinHandshakeException>(
            () => client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/sendspin")));

        Assert.Equal(HandshakeFailureKind.LegacyServer, ex.Kind);
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
