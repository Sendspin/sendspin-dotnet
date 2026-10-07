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
/// inside the window — the host's own logger is the only thing it calls there.
/// </remarks>
[Collection("RealSockets")]
public class SendspinHostServiceConcurrentArbitrationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static readonly byte[] TestPsk = Enumerable.Repeat((byte)0x42, 32).ToArray();

    private static async Task<SendspinHostService> StartHostAsync(ILoggerFactory loggerFactory)
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
            },
            listenerOptions: new ListenerOptions { Port = 0 },
            advertiserOptions: new AdvertiserOptions { Enabled = false });

        await host.StartAsync();
        return host;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Dials <paramref name="first"/> and <paramref name="second"/> together and holds whichever
    /// decides first at its "no existing connection" verdict — after the registry was read,
    /// before it is written — for long enough that the other reaches arbitration too.
    /// </summary>
    private static async Task<SendspinHostService> AdmitBothInsideOneArbitrationWindowAsync(
        FakeServer first, FakeServer second)
    {
        int decided = 0;
        using var release = new ManualResetEventSlim();
        var host = await StartHostAsync(new HookLoggerFactory(message =>
        {
            if (message.StartsWith("Arbitration: no existing connection", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref decided);
                release.Wait(Timeout);
            }
        }));

        await first.ConnectAsync(host.ListeningPort);
        await second.ConnectAsync(host.ListeningPort);

        // Unserialised, the second reaches the same verdict and this returns at once.
        // Serialised, it cannot get that far while the first is held, and the wait runs out.
        await WaitUntilAsync(() => Volatile.Read(ref decided) == 2, TimeSpan.FromSeconds(1));
        release.Set();
        return host;
    }

    [Fact]
    public async Task TwoServersHandshakingTogether_OnlyOneIsAdmitted()
    {
        await using var first = new FakeServer(TestPsk, []);
        await using var second = new FakeServer(TestPsk, []);
        await using var host = await AdmitBothInsideOneArbitrationWindowAsync(first, second);

        // Empty against empty with no last-played server: whichever registered first is kept.
        var firstGoodbye = first.WaitForGoodbyeAsync(TimeSpan.FromSeconds(10));
        var secondGoodbye = second.WaitForGoodbyeAsync(TimeSpan.FromSeconds(10));
        var rejected = await Task.WhenAny(firstGoodbye, secondGoodbye);
        Assert.Equal("concurrent_attempt", await rejected);

        var admitted = Assert.Single(host.ConnectedServers);
        Assert.Equal(rejected == firstGoodbye ? second.ServerId : first.ServerId, admitted.ServerId);
    }

    [Fact]
    public async Task OneServerDiallingTwiceTogether_KeepsOneConnectionAndClosesTheOther()
    {
        // A dual-homed server reaching the same mDNS record over IPv4 and IPv6: two sockets,
        // one identity.
        var keys = KeyPair.Generate();
        await using var first = new FakeServer(TestPsk, [], keys);
        await using var second = new FakeServer(TestPsk, [], keys);
        await using var host = await AdmitBothInsideOneArbitrationWindowAsync(first, second);

        int disconnects = 0;
        host.ServerDisconnected += (_, _) => Interlocked.Increment(ref disconnects);

        // The one that registered first is the stale socket of a same-server reconnect. It used
        // to be overwritten in the registry instead: left open with no goodbye, and able to take
        // the live connection's entry with it when it eventually closed.
        var firstGoodbye = first.WaitForGoodbyeAsync(TimeSpan.FromSeconds(10));
        var secondGoodbye = second.WaitForGoodbyeAsync(TimeSpan.FromSeconds(10));
        var displaced = await Task.WhenAny(firstGoodbye, secondGoodbye);
        Assert.Equal("user_request", await displaced);

        // One eviction reported, and the survivor still holds the entry afterwards.
        await WaitUntilAsync(() => Volatile.Read(ref disconnects) == 1, TimeSpan.FromSeconds(10));
        Assert.Equal(first.ServerId, Assert.Single(host.ConnectedServers).ServerId);
        Assert.Equal(1, Volatile.Read(ref disconnects));
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
        await WaitUntilAsync(() => Volatile.Read(ref connects) == 1, Timeout);

        var disconnectAll = Task.Run(() => host.DisconnectAllAsync());
        Assert.True(closing.Wait(Timeout));

        await using var redial = new FakeServer(TestPsk, [], keys);
        await redial.ConnectAsync(host.ListeningPort);
        await WaitUntilAsync(() => Volatile.Read(ref connects) == 2, Timeout);
        Assert.Equal(2, Volatile.Read(ref connects));

        // The stale connection's disconnect now runs, under the id the redial is registered by.
        release.Set();
        await disconnectAll.WaitAsync(Timeout);

        Assert.Equal(redial.ServerId, Assert.Single(host.ConnectedServers).ServerId);
        Assert.Equal(0, Volatile.Read(ref disconnects));
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
