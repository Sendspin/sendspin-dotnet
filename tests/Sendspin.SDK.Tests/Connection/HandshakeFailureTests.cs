using System.Diagnostics;
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

    /// <summary>
    /// The legacy-server diagnostic is the case the whole clean break was costed on, so it
    /// must not arrive two seconds late (#98 item 4).
    /// </summary>
    /// <remarks>
    /// <c>FailPermanentlyAsync</c> runs <i>on</i> the receive loop, and the cleanup it awaits
    /// waits for <c>_receiveTask</c> — that same loop. A task cannot await itself to
    /// completion, so the wait could only ever end by burning its full two-second timeout,
    /// on the one path whose entire purpose is telling the operator what went wrong. The same
    /// self-await sits on every receive-loop-driven reconnect.
    /// </remarks>
    [Fact]
    public async Task LegacyServerDiagnostic_ReachesTheApplicationPromptly()
    {
        await using var server = new SimpleWebSocketServer();
        server.Start(0);

        var accepted = new TaskCompletionSource<WebSocketClientConnection>();
        server.ClientConnected += (_, c) => accepted.TrySetResult(c);

        await using var connection = new SendspinConnection(
            NullLogger<SendspinConnection>.Instance,
            new ConnectionOptions { AutoReconnect = true, ReconnectDelayMs = 10 },
            new StubFraming { IsTransportReady = false });

        var disconnected = new TaskCompletionSource<ConnectionStateChangedEventArgs>();
        connection.StateChanged += (_, e) =>
        {
            if (e.NewState == ConnectionState.Disconnected)
                disconnected.TrySetResult(e);
        };

        await connection.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/sendspin"));
        var serverConn = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // aiosendspin < 7.0.0 answers client/init with a normal-closure close, no reply.
        var stopwatch = Stopwatch.StartNew();
        await serverConn.CloseAsync();

        var final = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stopwatch.Stop();

        // Positive control: this is the permanent-failure path, not some other disconnect
        // that happens to be fast.
        var ex = Assert.IsType<SendspinHandshakeException>(final.Exception);
        Assert.Equal(HandshakeFailureKind.LegacyServer, ex.Kind);

        // Generous against scheduling noise but far below the 2s cleanup timeout: the healthy
        // path only has to dispose a socket, which is milliseconds.
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"Diagnostic took {stopwatch.ElapsedMilliseconds}ms to reach the application; "
            + "the receive-task self-await is back.");
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

        var logger = new CapturingLogger<IncomingConnection>();
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

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);

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

        var logger = new CapturingLogger<IncomingConnection>();
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

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);

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

        var logger = new CapturingLogger<IncomingConnection>();
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

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);

        // Positive control for the assertion above: "no warning was logged" is equally
        // satisfied by a logger that captured nothing and by a subject that said nothing, so
        // pin what this case should produce instead — the ordinary close, at Information.
        Assert.Contains(
            logger.MessagesAt(LogLevel.Information),
            m => m.Contains("Server closed connection", StringComparison.Ordinal));
    }

    /// <summary>
    /// The legacy signature is specifically a <b>1000</b> close with no reply, which is why the
    /// dial path requires NormalClosure as well. A peer that goes away mid-handshake any other
    /// way has said nothing about whether it speaks encryption, so blaming its version sends
    /// the operator to upgrade a server that was never the problem (#97).
    /// </summary>
    /// <param name="abort">
    /// The two ways a mid-handshake close arrives without a normal closure, both of which
    /// <see cref="WebSocketClientConnection"/> routes through the same callback. False: a clean
    /// Close frame carrying a non-1000 status (a restarting server, a draining proxy send
    /// 1001). True: an abrupt transport-level abort, which carries no status at all — the case
    /// that has no status to compare and so read as legacy by default.
    /// </param>
    [Theory]
    [InlineData(false, "EndpointUnavailable")]
    [InlineData(true, "no close status")]
    public async Task IncomingConnection_DoesNotBlameLegacyServer_WhenTheCloseIsNotANormalClosure(
        bool abort, string expectedStatus)
    {
        await using var server = new SimpleWebSocketServer();
        server.Start(0);

        var accepted = new TaskCompletionSource<WebSocketClientConnection>();
        server.ClientConnected += (_, c) => accepted.TrySetResult(c);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/sendspin"), CancellationToken.None);

        var serverSideSocket = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var logger = new CapturingLogger<IncomingConnection>();
        await using var incoming = new IncomingConnection(
            logger, serverSideSocket, new StubFraming { IsTransportReady = false });

        var disconnected = new TaskCompletionSource<bool>();
        incoming.StateChanged += (_, e) =>
        {
            if (e.NewState == ConnectionState.Disconnected)
                disconnected.TrySetResult(true);
        };

        await incoming.StartAsync();

        // Both arms share the legacy signature in every respect but the close status: the peer
        // never replies, so no frame arrives and the framing raises no fatal.
        if (abort)
        {
            client.Abort();
        }
        else
        {
            await client.CloseAsync(
                WebSocketCloseStatus.EndpointUnavailable, "going away", CancellationToken.None);
        }

        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);

        // Positive control alongside the absence assertion: an incomplete handshake is still
        // reported as a failure, and still names what it saw. A silent downgrade to the
        // ordinary "server closed connection" at Information would otherwise pass.
        Assert.Contains("the handshake did not complete", warning.Message);
        Assert.Contains(expectedStatus, warning.Message);
        Assert.DoesNotContain("does not support Sendspin encryption", warning.Message);
    }

    /// <summary>
    /// A permanent verdict reached while the reconnect loop is mid-attempt must still be
    /// published (#98 items 1 and 2b).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>FailPermanentlyAsync</c> records the verdict and then bails when the connection-lost
    /// guard is already held, leaving the holder to publish it. The reconnect loop only read
    /// the field at the top of an iteration, so a verdict recorded after that — while the loop
    /// sat in its delay or its dial — was skipped, and a loop that then reconnected
    /// successfully returned as though healthy. The verdict was never published: the connection
    /// stayed in Handshaking with no further event, and the field stayed set so the next
    /// ordinary drop was refused a retry.
    /// </para>
    /// <para>
    /// The interleaving is constructed rather than raced for. <c>StateChanged</c> is raised
    /// synchronously from inside the reconnect loop, so a handler that blocks on the second
    /// Handshaking holds the loop in exactly the window the bug lives in, while the newly
    /// started receive loop — already running by then — trips a pre-transport framing fatal.
    /// The handler waits for the Debug line FailPermanentlyAsync emits when it finds the guard
    /// held, so the ordering is observed rather than assumed.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task PermanentFailureRecordedMidReconnect_IsStillPublished()
    {
        await using var server = new SimpleWebSocketServer();
        server.Start(0);

        // Transport-ready to begin with, so the first drop is an ordinary socket loss and takes
        // the reconnect path. Flipped below to make the *second* connection's first inbound
        // frame a pre-transport fatal, which is what FailPermanentlyAsync reacts to.
        var framing = new StubFraming();
        var logger = new CapturingLogger<SendspinConnection>();

        var firstConnection = new TaskCompletionSource<WebSocketClientConnection>();
        var dials = 0;
        server.ClientConnected += (_, c) =>
        {
            if (Interlocked.Increment(ref dials) == 1)
            {
                firstConnection.TrySetResult(c);
            }
            else
            {
                // Any frame trips the fatal now that the stub is armed. Sent from the accept
                // callback so it is already in flight while the reconnect loop is held below.
                _ = c.SendAsync("{}");
            }
        };

        // Both schedules short: the first loss is classified on the framing's mode at that
        // moment, and pinning the test to one classification would make it fragile.
        await using var connection = new SendspinConnection(
            logger,
            new ConnectionOptions
            {
                AutoReconnect = true,
                ReconnectDelayMs = 50,
                HandshakeFailureBackoffMs = 50,
            },
            framing);

        var handshakingCount = 0;
        var heldTheLoop = false;
        var disconnected = new TaskCompletionSource<ConnectionStateChangedEventArgs>();
        connection.StateChanged += (_, e) =>
        {
            if (e.NewState == ConnectionState.Disconnected)
            {
                disconnected.TrySetResult(e);
                return;
            }

            if (e.NewState != ConnectionState.Handshaking)
                return;

            // The first Handshaking is the initial connect, where no verdict can be pending;
            // blocking there would simply hang.
            if (Interlocked.Increment(ref handshakingCount) != 2)
                return;

            // Hold the reconnect loop between "the dial succeeded" and "check the state and
            // return", which is the window the missing re-read left open.
            heldTheLoop = SpinWait.SpinUntil(
                () => logger.MessagesAt(LogLevel.Debug)
                    .Any(m => m.Contains("permanent failure recorded", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(10));
        };

        await connection.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/sendspin"));
        var serverConn = await firstConnection.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Arm the fatal before dropping, so the reconnect's first frame trips it. IsTransportReady
        // has to go false here too, and not merely as a side effect of the fatal: the receive
        // loop captures the framing's mode *before* calling ProcessInbound, and routes a fatal
        // raised in transport mode to the ordinary reconnect path instead of FailPermanentlyAsync.
        framing.IsTransportReady = false;
        framing.FatalOnInbound = "no matching psk";

        // Abrupt teardown: an ordinary loss, so the reconnect loop runs and takes the guard.
        await serverConn.DisposeAsync();

        var final = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(20));

        // Positive control on the construction itself: without this the test could pass because
        // the verdict was published by the ordinary FailPermanentlyAsync path, never exercising
        // the record-and-bail handoff this test exists for.
        Assert.True(heldTheLoop,
            "The reconnect loop was never held while a permanent failure was recorded, so the "
            + "interleaving under test did not occur.");

        var ex = Assert.IsType<SendspinHandshakeException>(final.Exception);
        Assert.Equal(HandshakeFailureKind.HandshakeRejected, ex.Kind);
        Assert.Contains("no matching psk", ex.Message);
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

}
