using Microsoft.Extensions.Logging.Abstractions;
using Noise;
using Sendspin.SDK.Client;
using Sendspin.SDK.Discovery;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Noise;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Loopback end-to-end coverage for multi-server arbitration: two FakeServers complete a real
/// Noise handshake against the host's listener, and each scenario asserts which connection
/// receives a client/goodbye and with what reason — verifying the bytes on the wire, not just
/// the decision logic. Priority comes from each server/activate's activities.
/// </summary>
[Collection("RealSockets")]
public class SendspinHostServiceArbitrationTests
{
    // Generous ceiling: these loopback tests spin up real WebSocket connections and
    // handshakes, so a busy CI runner can exceed a few seconds. The awaits complete as
    // soon as the expected event fires, so a high ceiling only bounds the failure case
    // (it does not slow the happy path); it exists to keep the socket timing
    // non-flaky under load, not as an expected duration.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static readonly byte[] TestPsk = Enumerable.Repeat((byte)0x42, 32).ToArray();

    private static async Task<SendspinHostService> StartHostAsync(string? seed = null)
    {
        var records = new InMemoryPairingRecordStore();

        // A LongTerm record makes the session trust 'user', so a server/activate granting
        // 'playback' is admissible. On the sentinel PSK it would be refused by the spec's
        // admissibility table, which is not what these tests are exercising. The record is
        // deliberately unbound: each FakeServer generates a fresh server identity, and a
        // bound record whose server_id differs fails the handshake.
        records.Upsert(new PairingRecord(TestPsk, PskCategory.LongTerm));

        var host = new SendspinHostService(
            NullLoggerFactory.Instance,
            new SendspinClientOptions
            {
                Identity = SendspinIdentity.Generate(),
                PairingRecordStore = records,
            },
            listenerOptions: new ListenerOptions { Port = 0 },
            advertiserOptions: new AdvertiserOptions { Enabled = false },
            lastPlayedServerId: seed);

        await host.StartAsync(); // prevent real network servers from racing into arbitration
        return host;
    }

    private static async Task WaitForServerConnectedAsync(SendspinHostService host, string serverId)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? s, ConnectedServerInfo info)
        {
            if (info.ServerId == serverId)
            {
                tcs.TrySetResult();
            }
        }

        host.ServerConnected += Handler;
        try
        {
            // Cover the race where it connected before we subscribed.
            if (host.ConnectedServers.Any(c => c.ServerId == serverId))
            {
                tcs.TrySetResult();
            }

            await tcs.Task.WaitAsync(Timeout);
        }
        finally
        {
            host.ServerConnected -= Handler;
        }
    }

    [Fact]
    public async Task SingleDiscoveryServer_IsAcceptedWithoutGoodbye()
    {
        await using var host = await StartHostAsync();
        await using var server = new FakeServer(TestPsk, []);
        await server.ConnectAsync(host.ListeningPort);
        await WaitForServerConnectedAsync(host, server.ServerId);

        var reason = await server.WaitForGoodbyeAsync(TimeSpan.FromMilliseconds(750));

        Assert.Null(reason); // accepted, stayed connected
        Assert.Contains(host.ConnectedServers, c => c.ServerId == server.ServerId);
    }

    [Fact]
    public async Task NewPlaybackServer_SwitchesAndSendsAnotherServerToExisting()
    {
        await using var host = await StartHostAsync();
        await using var existing = new FakeServer(TestPsk, []);
        await existing.ConnectAsync(host.ListeningPort);
        await WaitForServerConnectedAsync(host, existing.ServerId);

        await using var incoming = new FakeServer(TestPsk, ["playback"]);
        await incoming.ConnectAsync(host.ListeningPort);

        Assert.Equal("another_server", await existing.WaitForGoodbyeAsync(Timeout));
    }

    [Fact]
    public async Task NewDiscoveryServer_AgainstPlaybackExisting_IsRejectedWithConcurrentAttempt()
    {
        await using var host = await StartHostAsync();
        await using var existing = new FakeServer(TestPsk, ["playback"]);
        await existing.ConnectAsync(host.ListeningPort);
        await WaitForServerConnectedAsync(host, existing.ServerId);

        await using var incoming = new FakeServer(TestPsk, []);
        await incoming.ConnectAsync(host.ListeningPort);

        Assert.Equal("concurrent_attempt", await incoming.WaitForGoodbyeAsync(Timeout));
    }

    [Fact]
    public async Task BothDiscovery_LastPlayedNewServer_WinsTieAndExistingGetsAnotherServer()
    {
        // The incoming server is built first because its real server_id is what the host has
        // to be seeded with as the last-played server.
        await using var incoming = new FakeServer(TestPsk, []);
        await using var host = await StartHostAsync(seed: incoming.ServerId);
        await using var existing = new FakeServer(TestPsk, []);
        await existing.ConnectAsync(host.ListeningPort);
        await WaitForServerConnectedAsync(host, existing.ServerId);

        await incoming.ConnectAsync(host.ListeningPort);

        Assert.Equal("another_server", await existing.WaitForGoodbyeAsync(Timeout));
    }

    [Fact]
    public async Task DisplacedPairingConnection_GetsPairAbortNotGoodbye()
    {
        // connection.md: a displaced connection that is a pairing handshake receives pair/abort
        // concurrent_attempt, not client/goodbye another_server — a goodbye is not something a
        // pairing state machine processes as an attempt teardown (#203). Management is the only
        // priority that displaces a pairing attempt at all.
        await using var host = await StartHostAsync();
        await using var pairing = new FakeServer(TestPsk, ["pairing"]);
        await pairing.ConnectAsync(host.ListeningPort);
        await WaitForServerConnectedAsync(host, pairing.ServerId);

        await using var incoming = new FakeServer(TestPsk, ["management"]);
        await incoming.ConnectAsync(host.ListeningPort);

        Assert.True(await pairing.WaitForPairAbortAsync("concurrent_attempt", Timeout));

        // And no goodbye alongside it: the pair/abort replaces the goodbye, it does not precede
        // one. A short wait, since by here the abort has already been observed.
        Assert.Null(await pairing.WaitForGoodbyeAsync(TimeSpan.FromMilliseconds(750)));
    }

    [Fact]
    public async Task RejectedIncomingPairing_GetsPairAbortNotGoodbye()
    {
        // The other half of connection.md's rule: a rejected incoming pairing is told
        // pair/abort concurrent_attempt. A playback holder outranks it.
        await using var host = await StartHostAsync();
        await using var existing = new FakeServer(TestPsk, ["playback"]);
        await existing.ConnectAsync(host.ListeningPort);
        await WaitForServerConnectedAsync(host, existing.ServerId);

        await using var incoming = new FakeServer(TestPsk, ["pairing"]);
        await incoming.ConnectAsync(host.ListeningPort);

        Assert.True(await incoming.WaitForPairAbortAsync("concurrent_attempt", Timeout));
        Assert.Null(await incoming.WaitForGoodbyeAsync(TimeSpan.FromMilliseconds(750)));
    }

    [Fact]
    public async Task SameServerReconnect_SendsUserRequestToStaleConnection()
    {
        await using var host = await StartHostAsync();

        // Both connections are the SAME server, and server_id is the static public key, so
        // the two instances have to share one key pair to present one identity.
        var keys = KeyPair.Generate();
        await using var first = new FakeServer(TestPsk, [], keys);
        await first.ConnectAsync(host.ListeningPort);
        await WaitForServerConnectedAsync(host, first.ServerId);

        await using var second = new FakeServer(TestPsk, [], keys); // same server_id reconnecting
        await second.ConnectAsync(host.ListeningPort);

        Assert.Equal("user_request", await first.WaitForGoodbyeAsync(Timeout));
    }

    /// <summary>
    /// Regression test for #143: disposing the host closes each connected socket via a full
    /// WebSocket closing handshake, which blocks forever against a peer that never answers.
    /// SilentCloseFakeServer models exactly that peer. This only asserts that disposal
    /// terminates within a generous bound, not that it is fast.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_WithPeerThatNeverAnswersClose_TerminatesWithinBound()
    {
        // Not 'await using host': SendspinHostService.DisposeAsync has no idempotence guard,
        // so a second, implicit dispose racing the explicit one below would just add a second
        // hang on top of whatever this test is trying to prove.
        var host = await StartHostAsync();
        await using var server = new SilentCloseFakeServer(TestPsk, []);
        await server.ConnectAsync(host.ListeningPort);
        await WaitForServerConnectedAsync(host, server.ServerId);

        var dispose = host.DisposeAsync().AsTask();
        Assert.Same(dispose, await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(10))));
        await dispose; // surface any exception

        // Integration-level sanity check that the peer isn't left parked in the full
        // host+arbitration flow. This doesn't discriminate the resource-release fix by itself
        // (the Close frame change 1 already sends is enough to satisfy it) — see
        // SimpleWebSocketServerTests.IncomingConnection_Dispose_ReleasesUnderlyingSocket for the
        // targeted test that isolates and proves the host actually releases the socket (#143).
        await server.WaitForReceiveLoopExitAsync(TimeSpan.FromSeconds(5));
    }
}
