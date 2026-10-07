using Microsoft.Extensions.Logging;
using Noise;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Discovery;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Each accepted socket arbitrates on its own task, so two of them can be inside arbitration at
/// once. The decision, the loser's eviction and the winner's registration have to be one unit,
/// or both read an empty registry and both are admitted (#314).
/// </summary>
/// <remarks>
/// Loopback against the host's real listener, like <see cref="SendspinHostServiceArbitrationTests"/>.
/// The interleavings are a few instructions wide, so each test holds a connection at a log line
/// inside the window — the host's own logger is the only thing it calls there. Every test
/// waits for its line to be reached and fails if it never is: reworded, it would otherwise stop
/// forcing the interleaving and leave the test passing for nothing.
/// </remarks>
[Collection("RealSockets")]
public class SendspinHostServiceConcurrentArbitrationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static readonly byte[] TestPsk = Enumerable.Repeat((byte)0x42, 32).ToArray();

    private static async Task<SendspinHostService> StartHostAsync(
        ILoggerFactory loggerFactory, FakeAudioPipeline? pipeline = null)
    {
        var records = new InMemoryPairingRecordStore();

        // Unbound LongTerm record — see SendspinHostServiceArbitrationTests for the reasoning.
        records.Upsert(new PairingRecord(TestPsk, PskCategory.LongTerm));

        var host = new SendspinHostService(
            loggerFactory,
            new SendspinClientOptions
            {
                Identity = SendspinIdentity.Generate(),
                PairingRecordStore = records,
                AudioPipeline = pipeline,
            },
            listenerOptions: new ListenerOptions { Port = 0 },
            advertiserOptions: new AdvertiserOptions { Enabled = false });

        await host.StartAsync();
        return host;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {because}");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task TwoServersHandshakingTogether_OnlyOneIsAdmitted()
    {
        using var hold = new ArbitrationHold();
        await using var host = await StartHostAsync(hold.LoggerFactory);
        hold.Watch(host);

        await using var first = new FakeServer(TestPsk, []);
        await using var second = new FakeServer(TestPsk, []);
        await first.ConnectAsync(host.ListeningPort);
        await second.ConnectAsync(host.ListeningPort);
        await hold.ReleaseOnceBothReachedArbitrationAsync();

        // Empty against empty with no last-played server: whichever registered first is kept.
        var firstGoodbye = first.WaitForGoodbyeAsync(Timeout);
        var secondGoodbye = second.WaitForGoodbyeAsync(Timeout);
        var rejected = await Task.WhenAny(firstGoodbye, secondGoodbye);
        Assert.Equal("concurrent_attempt", await rejected);

        var admitted = Assert.Single(host.ConnectedServers);
        Assert.Equal(rejected == firstGoodbye ? second.ServerId : first.ServerId, admitted.ServerId);
    }

    [Fact]
    public async Task OneServerDiallingTwiceTogether_KeepsOneConnectionAndClosesTheOther()
    {
        using var hold = new ArbitrationHold();
        await using var host = await StartHostAsync(hold.LoggerFactory);
        hold.Watch(host);

        int connects = 0;
        int disconnects = 0;
        host.ServerConnected += (_, _) => Interlocked.Increment(ref connects);
        host.ServerDisconnected += (_, _) => Interlocked.Increment(ref disconnects);

        // A dual-homed server reaching the same mDNS record over IPv4 and IPv6: two sockets,
        // one identity.
        var keys = KeyPair.Generate();
        await using var first = new FakeServer(TestPsk, [], keys);
        await using var second = new FakeServer(TestPsk, [], keys);
        await first.ConnectAsync(host.ListeningPort);
        await second.ConnectAsync(host.ListeningPort);
        await hold.ReleaseOnceBothReachedArbitrationAsync();

        // The one that registered first is the stale socket of a same-server reconnect. It used
        // to be overwritten in the registry instead: left open with no goodbye, and able to take
        // the live connection's entry with it when it eventually closed.
        var firstGoodbye = first.WaitForGoodbyeAsync(Timeout);
        var secondGoodbye = second.WaitForGoodbyeAsync(Timeout);
        Assert.Equal("user_request", await await Task.WhenAny(firstGoodbye, secondGoodbye));

        // The survivor registers only after the eviction has been reported.
        await WaitUntilAsync(() => Volatile.Read(ref connects) == 2, "the second connection to be admitted");

        Assert.Equal(first.ServerId, Assert.Single(host.ConnectedServers).ServerId);
        Assert.Equal(1, Volatile.Read(ref disconnects));
    }

    [Fact]
    public async Task ServerThatLeftWhileQueuedForArbitration_DoesNotDisplaceTheHolder()
    {
        // The pipeline is only here to say when the queued connection has been disposed.
        var pipeline = new FakeAudioPipeline();
        int queuedGone = 0;
        using var hold = new ArbitrationHold(message =>
        {
            if (message.StartsWith("Connection state: Connected -> Disconnected", StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref queuedGone, 1);
            }
        });
        await using var host = await StartHostAsync(hold.LoggerFactory, pipeline);
        hold.Watch(host);

        int connects = 0;
        host.ServerConnected += (_, _) => Interlocked.Increment(ref connects);

        // The holder is held inside its own arbitration, so the playback server behind it —
        // which would win — has to queue. It hangs up there.
        await using var holder = new FakeServer(TestPsk, []);
        await holder.ConnectAsync(host.ListeningPort);
        await hold.WaitUntilHeldAsync();

        var queued = new FakeServer(TestPsk, ["playback"]);
        await queued.ConnectAsync(host.ListeningPort);
        await hold.WaitUntilAnotherIsQueuedAsync();
        await queued.DisposeAsync();
        await WaitUntilAsync(
            () => Volatile.Read(ref queuedGone) == 1, "the host to log the queued connection's disconnect");

        hold.Release();
        await WaitUntilAsync(() => pipeline.SubscriberCount == 1, "the queued connection to be disposed");

        Assert.Equal(1, Volatile.Read(ref connects));
        Assert.Equal(holder.ServerId, Assert.Single(host.ConnectedServers).ServerId);
        Assert.Null(await holder.WaitForGoodbyeAsync(TimeSpan.FromMilliseconds(250)));
    }

    [Fact]
    public async Task StaleConnectionsDisconnect_ArrivingAfterItsServerRedialled_LeavesTheNewEntry()
    {
        // Holds the first connection between its close and the state change that reports it,
        // which is where DisconnectAllAsync has already forgotten it but its handler has not run.
        using var closing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int held = 0;
        await using var host = await StartHostAsync(new HookLoggerFactory(message =>
        {
            if (message.StartsWith("Connection state: Disconnecting -> Disconnected", StringComparison.Ordinal)
                && Interlocked.Exchange(ref held, 1) == 0)
            {
                closing.Set();
                release.Wait(Timeout);
            }
        }));

        int connects = 0;
        int disconnects = 0;
        host.ServerConnected += (_, _) => Interlocked.Increment(ref connects);
        host.ServerDisconnected += (_, _) => Interlocked.Increment(ref disconnects);

        var keys = KeyPair.Generate();
        await using var stale = new FakeServer(TestPsk, [], keys);
        await stale.ConnectAsync(host.ListeningPort);
        await WaitUntilAsync(() => Volatile.Read(ref connects) == 1, "the first connection to be admitted");

        var disconnectAll = Task.Run(() => host.DisconnectAllAsync());
        Assert.True(closing.Wait(Timeout), "the state-change log line the hold keys on never appeared");

        await using var redial = new FakeServer(TestPsk, [], keys);
        await redial.ConnectAsync(host.ListeningPort);
        await WaitUntilAsync(() => Volatile.Read(ref connects) == 2, "the redial to be admitted");

        // The stale connection's disconnect now runs, under the id the redial is registered by.
        release.Set();
        await disconnectAll.WaitAsync(Timeout);

        Assert.Equal(redial.ServerId, Assert.Single(host.ConnectedServers).ServerId);
        Assert.Equal(0, Volatile.Read(ref disconnects));
    }

    [Fact]
    public async Task ServerThatActivatesAfterTheHostStopped_IsNotAdmitted()
    {
        // Holds the connection with its server/hello read and its server/activate still on the
        // socket: provisional, which is where StopAsync used to leave it (#344).
        using var provisional = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var host = await StartHostAsync(new HookLoggerFactory(message =>
        {
            if (message.StartsWith("Received text: {\"type\":\"server/hello\"", StringComparison.Ordinal))
            {
                provisional.Set();
                release.Wait(Timeout);
            }
        }));

        int connects = 0;
        host.ServerConnected += (_, _) => Interlocked.Increment(ref connects);

        await using var server = new FakeServer(TestPsk, []);
        var goodbye = server.WaitForGoodbyeAsync(Timeout);
        await server.ConnectAsync(host.ListeningPort);
        Assert.True(provisional.Wait(Timeout), "the server/hello log line the hold keys on never appeared");

        await StopWhileHeldAsync(host, release.Set);

        await WaitUntilAsync(
            () => goodbye.IsCompleted || Volatile.Read(ref connects) == 1,
            "the connection to be closed or admitted");
        Assert.Equal(0, Volatile.Read(ref connects));
        Assert.Equal("shutdown", await goodbye);
        Assert.Empty(host.ConnectedServers);
    }

    [Fact]
    public async Task ServerInsideArbitrationWhenTheHostStops_IsNotAdmitted()
    {
        using var hold = new ArbitrationHold();
        await using var host = await StartHostAsync(hold.LoggerFactory);

        int connects = 0;
        host.ServerConnected += (_, _) => Interlocked.Increment(ref connects);

        // Past the decision and not yet registered, so StopAsync finds no connection to close
        // and the registration that follows used to land on a host that had stopped (#344).
        await using var server = new FakeServer(TestPsk, []);
        var goodbye = server.WaitForGoodbyeAsync(Timeout);
        await server.ConnectAsync(host.ListeningPort);
        await hold.WaitUntilHeldAsync();

        await StopWhileHeldAsync(host, hold.Release);

        await WaitUntilAsync(
            () => goodbye.IsCompleted || Volatile.Read(ref connects) == 1,
            "the connection to be closed or admitted");
        Assert.Equal(0, Volatile.Read(ref connects));
        Assert.Equal("shutdown", await goodbye);
        Assert.Empty(host.ConnectedServers);
    }

    /// <summary>
    /// Stops the host around a connection held on its receive thread. The listener going down
    /// is the last thing StopAsync does that is visible from here without that thread, so the
    /// hold is released then: closing the held connection has to wait for it.
    /// </summary>
    private static async Task StopWhileHeldAsync(SendspinHostService host, Action release)
    {
        var stop = Task.Run(() => host.StopAsync());
        await WaitUntilAsync(() => !host.IsRunning, "the listener to stop");
        release();
        await stop.WaitAsync(Timeout);
    }

    /// <summary>
    /// Holds every connection that reaches a "no existing connection" verdict at that log line:
    /// after the registry was read, before it is written.
    /// </summary>
    private sealed class ArbitrationHold : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private int _held;
        private int _hellos;

        internal ArbitrationHold(Action<string>? alsoOnMessage = null)
        {
            LoggerFactory = new HookLoggerFactory(message =>
            {
                alsoOnMessage?.Invoke(message);
                if (message.StartsWith("Arbitration: no existing connection", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _held);
                    _release.Wait(Timeout);
                }
            });
        }

        internal ILoggerFactory LoggerFactory { get; }

        // A connection's hello is announced only once the handshake's completion has returned,
        // and arbitration runs inside that completion. So a held connection has not announced
        // one, and a connection that has is past the point where it queued behind the hold.
        internal void Watch(SendspinHostService host) =>
            host.ServerHelloReceived += (_, _) => Interlocked.Increment(ref _hellos);

        internal Task WaitUntilHeldAsync() => WaitUntilAsync(
            () => Volatile.Read(ref _held) >= 1,
            "a connection to reach the arbitration log line the hold keys on");

        internal async Task WaitUntilAnotherIsQueuedAsync()
        {
            await WaitUntilHeldAsync();
            await WaitUntilAsync(
                () => Volatile.Read(ref _hellos) >= 1, "the second connection to queue behind the first");
        }

        /// <summary>
        /// Releases once two connections are inside arbitration together: both at the verdict
        /// when nothing serialises them, or one at the verdict and one queued behind it.
        /// </summary>
        internal async Task ReleaseOnceBothReachedArbitrationAsync()
        {
            await WaitUntilHeldAsync();
            await WaitUntilAsync(
                () => Volatile.Read(ref _held) == 2 || Volatile.Read(ref _hellos) >= 1,
                "the second connection to reach arbitration");
            Release();
        }

        internal void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _release.Dispose();
        }
    }

    /// <summary>Hands every message any of the host's loggers writes to one callback, on the
    /// thread that logged it — so the callback can hold that thread where it stands.</summary>
    private sealed class HookLoggerFactory(Action<string> onMessage) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => new HookLogger(onMessage);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class HookLogger(Action<string> onMessage) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) => onMessage(formatter(state, exception));
        }
    }
}
