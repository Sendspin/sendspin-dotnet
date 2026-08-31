using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Discovery;
using Sendspin.SDK.Extensions;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// The two halves of #253, where a rejected server-initiated connection tore down the live
/// client-initiated session: a connection closed from inside its own handshake completion must
/// touch none of the state it shares with other connections, and a client-initiated session the
/// application registered with <see cref="SendspinHostService.AdoptClientInitiated"/> must make
/// the incoming server lose arbitration before it is ever accepted.
/// </summary>
/// <remarks>
/// Loopback end-to-end, like <see cref="SendspinHostServiceArbitrationTests"/>: a real
/// <see cref="FakeServer"/> completes a real Noise handshake against the host's listener, so the
/// re-entrancy that causes the bug — <c>MarkConnected</c> dispatching straight through the
/// handshake waiter into the application's <c>ServerConnected</c> handler — is reproduced rather
/// than simulated. The adopted client-initiated session is an in-memory
/// <see cref="FakeSendspinConnection"/>, which is all arbitration reads of it.
/// </remarks>
[Collection("RealSockets")]
public class SendspinHostServiceClientInitiatedTests
{
    // Same generous ceiling and rationale as SendspinHostServiceArbitrationTests: real sockets
    // on a busy runner. The awaits complete as soon as the expected event fires.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // Short bound for the negative waits ("no goodbye arrives"), which have to sit out their
    // whole duration.
    private static readonly TimeSpan NoEventWindow = TimeSpan.FromMilliseconds(750);

    private static readonly byte[] TestPsk = Enumerable.Repeat((byte)0x42, 32).ToArray();

    /// <summary>The id the adopted client-initiated session is arbitrated under. Any string does:
    /// unlike a server-initiated connection's, it is supplied by the caller, not derived from a
    /// Noise static key.</summary>
    private const string DialledServerId = "dialled-server";

    private static async Task<SendspinHostService> StartHostAsync(
        IAudioPipeline? audioPipeline = null,
        IClockSynchronizer? clockSynchronizer = null,
        ILoggerFactory? loggerFactory = null)
    {
        var records = new InMemoryPairingRecordStore();

        // Unbound LongTerm record, so the session trusts 'user' and a playback activate is
        // admissible — see SendspinHostServiceArbitrationTests for the full reasoning.
        records.Upsert(new PairingRecord(TestPsk, PskCategory.LongTerm));

        var host = new SendspinHostService(
            loggerFactory ?? NullLoggerFactory.Instance,
            new SendspinClientOptions
            {
                Identity = SendspinIdentity.Generate(),
                PairingRecordStore = records,

                // Supplied, therefore SHARED across every connection the host accepts — which is
                // the arrangement #253 was reported against, and the only one in which a
                // connection's own teardown can reach another connection's state.
                AudioPipeline = audioPipeline,
                ClockSynchronizer = clockSynchronizer,
            },
            listenerOptions: new ListenerOptions { Port = 0 },
            advertiserOptions: new AdvertiserOptions { Enabled = false });

        await host.StartAsync(); // prevent real network servers from racing into arbitration
        return host;
    }

    /// <summary>
    /// A connected client-initiated session with a playback activate behind it, so it carries the
    /// same arbitration priority an incoming playback server does.
    /// </summary>
    private static (SendspinClientService Client, FakeSendspinConnection Connection) DialledSession()
    {
        var (client, connection, _) = TestClient.Create();
        TestClient.CompleteHandshake(connection);
        return (client, connection);
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

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {because}");
            await Task.Delay(10);
        }
    }

    // -- Fix 1: a connection closed inside its own handshake completion ----------------------

    [Fact]
    public async Task ServerConnectedHandlerThatDisconnects_LeavesTheSharedClockAndPipelineAlone()
    {
        var pipeline = new FakeAudioPipeline();
        var clock = new ConvergedClockSynchronizer();
        var logs = new CapturingLoggerFactory();
        await using var host = await StartHostAsync(pipeline, clock, logs);

        // The reported shape, verbatim: the application refuses the incoming server from inside
        // ServerConnected. That handler runs INSIDE MarkConnected's synchronous state dispatch —
        // the host's handshake waiter completes inline — so by the time FinishHandshake resumes,
        // the connection it is finishing is already closing.
        host.ServerConnected += (_, _) =>
            host.DisconnectAllAsync(GoodbyeReasons.AnotherServer).SafeFireAndForget();

        await using var server = new FakeServer(TestPsk, ["playback"]);
        await server.ConnectAsync(host.ListeningPort);

        // Both waits are positive, and both land strictly after the handshake completion whose
        // tail is under test: the goodbye is written from the refusal itself, and the socket
        // close that ends the peer's receive loop follows it.
        Assert.Equal("another_server", await server.WaitForGoodbyeAsync(Timeout));
        await server.WaitForReceiveLoopExitAsync(TimeSpan.FromSeconds(5));

        // None of the handshake tail ran for a connection that no longer exists. Each of these
        // is shared with whatever other session the application is running — which is how the
        // teardown of a refused socket stopped playback on a session it never touched.
        Assert.Equal(0, clock.ResetCount);
        Assert.Equal(0, pipeline.NotifyReconnectCount);
        Assert.DoesNotContain(
            logs.Messages,
            m => m.Contains("Sending initial client/state", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AcceptedServer_StillResetsTheClockAndNotifiesThePipeline()
    {
        // Positive control for the test above. Without it, an implementation that had simply
        // deleted the handshake tail would pass just as well.
        var pipeline = new FakeAudioPipeline();
        var clock = new ConvergedClockSynchronizer();
        var logs = new CapturingLoggerFactory();
        await using var host = await StartHostAsync(pipeline, clock, logs);

        await using var server = new FakeServer(TestPsk, ["playback"]);
        await server.ConnectAsync(host.ListeningPort);
        await WaitForServerConnectedAsync(host, server.ServerId);

        // Polled, not asserted outright: ServerConnected is raised from inside MarkConnected, so
        // it fires BEFORE the handshake tail that follows it on the same stack.
        await WaitUntilAsync(
            () => clock.ResetCount == 1
                && pipeline.NotifyReconnectCount == 1
                && logs.Messages.Any(m => m.Contains("Sending initial client/state", StringComparison.Ordinal)),
            "the accepted connection's handshake tail");
    }

    // -- Fix 2: arbitration sees the client-initiated session --------------------------------

    [Fact]
    public async Task AdoptedClientInitiatedSession_MakesAnIncomingServerLoseArbitration()
    {
        // The reported scenario end to end, with both fixes in play. The shared pipeline and
        // clock synchronizer stand in for the singletons an application runs across both
        // connection modes — in #253 they were what the refused connection's teardown reached.
        var pipeline = new FakeAudioPipeline();
        var clock = new ConvergedClockSynchronizer();
        var logs = new CapturingLoggerFactory();
        await using var host = await StartHostAsync(pipeline, clock, logs);

        var (dialled, dialledConnection) = DialledSession();
        using var _d = dialled;
        host.AdoptClientInitiated(dialled, DialledServerId);

        var announced = new List<string>();
        host.ServerConnected += (_, info) => announced.Add(info.ServerId);

        await using var incoming = new FakeServer(TestPsk, ["playback"]);
        await incoming.ConnectAsync(host.ListeningPort);

        // Playback against playback. The spec's own table would accept this ("higher or equal
        // is accepted") — which is exactly what #253 saw — so this passes only because an
        // adopted client-initiated holder is not displaceable.
        Assert.Equal("concurrent_attempt", await incoming.WaitForGoodbyeAsync(Timeout));
        await incoming.WaitForReceiveLoopExitAsync(TimeSpan.FromSeconds(5));

        // Refused at the door: never registered, never announced.
        Assert.Empty(announced);
        Assert.DoesNotContain(host.ConnectedServers, c => c.ServerId == incoming.ServerId);

        // The session the application dialled is untouched by the whole episode — including the
        // state it shares with the connection that was just refused. Arbitration refuses from
        // inside the same synchronous dispatch an application's own refusal runs in, so the
        // handshake guard covers the SDK's own rejection too.
        Assert.Equal(ConnectionState.Connected, dialled.ConnectionState);
        Assert.Null(dialledConnection.LastDisconnectReason);
        Assert.False(dialledConnection.WasDisposed);
        Assert.Equal(0, clock.ResetCount);
        Assert.Equal(0, pipeline.NotifyReconnectCount);
        Assert.DoesNotContain(
            logs.Messages,
            m => m.Contains("Sending initial client/state", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisconnectAllAsync_LeavesTheAdoptedSessionConnected()
    {
        await using var host = await StartHostAsync();

        // A server-initiated connection first, as the positive control: DisconnectAllAsync must
        // still say goodbye to the connections this host does own.
        await using var accepted = new FakeServer(TestPsk, ["playback"]);
        await accepted.ConnectAsync(host.ListeningPort);
        await WaitForServerConnectedAsync(host, accepted.ServerId);

        var (dialled, dialledConnection) = DialledSession();
        using var _d = dialled;
        host.AdoptClientInitiated(dialled, DialledServerId);

        await host.DisconnectAllAsync();

        Assert.Equal("another_server", await accepted.WaitForGoodbyeAsync(Timeout));
        Assert.Equal(ConnectionState.Connected, dialled.ConnectionState);
        Assert.Null(dialledConnection.LastDisconnectReason);
    }

    [Fact]
    public async Task StoppingAndDisposingTheHost_LeavesTheAdoptedSessionAlone()
    {
        // Not 'await using': this test disposes the host itself, and a second implicit dispose
        // would race it (SendspinHostService.DisposeAsync has no idempotence guard).
        var host = await StartHostAsync();
        var (dialled, dialledConnection) = DialledSession();
        using var _d = dialled;
        host.AdoptClientInitiated(dialled, DialledServerId);

        await host.StopAsync();

        // Stop is not the end of the adoption: stop/start is a resumable pair, and an
        // application that stops advertising while it plays from the session it dialled still
        // wants that session arbitrated for when it starts listening again.
        Assert.Equal(DialledServerId, host.AdoptedClientInitiatedServerId);

        await host.DisposeAsync();

        // Disposal is terminal, so the adoption goes with it — otherwise the client would keep
        // a state-changed handler pointing at a host that no longer exists.
        Assert.Null(host.AdoptedClientInitiatedServerId);

        // Released, never torn down: the client is the application's throughout.
        Assert.Equal(ConnectionState.Connected, dialled.ConnectionState);
        Assert.Null(dialledConnection.LastDisconnectReason);
        Assert.False(dialledConnection.WasDisposed);
    }

    [Fact]
    public async Task ReleaseClientInitiated_AdmitsIncomingServersAgain()
    {
        await using var host = await StartHostAsync();
        var (dialled, _) = DialledSession();
        using var _d = dialled;

        host.AdoptClientInitiated(dialled, DialledServerId);
        host.ReleaseClientInitiated(DialledServerId);

        await using var incoming = new FakeServer(TestPsk, ["playback"]);
        await incoming.ConnectAsync(host.ListeningPort);
        await WaitForServerConnectedAsync(host, incoming.ServerId);

        Assert.Null(await incoming.WaitForGoodbyeAsync(NoEventWindow));
    }

    [Fact]
    public async Task AdoptedSessionDisconnecting_ReleasesItself()
    {
        await using var host = await StartHostAsync();
        var (dialled, _) = DialledSession();
        using var _d = dialled;
        host.AdoptClientInitiated(dialled, DialledServerId);

        // The application closed the session it dialled without telling the host. Arbitrating
        // on behalf of a connection that is gone would lock the client out of every server.
        await dialled.DisconnectAsync(GoodbyeReasons.UserRequest);

        await using var incoming = new FakeServer(TestPsk, ["playback"]);
        await incoming.ConnectAsync(host.ListeningPort);
        await WaitForServerConnectedAsync(host, incoming.ServerId);

        Assert.Null(await incoming.WaitForGoodbyeAsync(NoEventWindow));
    }

    [Fact]
    public async Task AdoptingAnAlreadyDisconnectedSession_DoesNotHoldArbitrationShut()
    {
        await using var host = await StartHostAsync();
        var (dialled, _) = DialledSession();
        using var _d = dialled;

        // Adopted after it died, so the disconnect event that would normally release the
        // adoption has already been and gone. Without the state re-read at the end of
        // AdoptClientInitiated, nothing would ever admit a server again.
        await dialled.DisconnectAsync(GoodbyeReasons.UserRequest);
        host.AdoptClientInitiated(dialled, DialledServerId);

        await using var incoming = new FakeServer(TestPsk, ["playback"]);
        await incoming.ConnectAsync(host.ListeningPort);
        await WaitForServerConnectedAsync(host, incoming.ServerId);

        Assert.Null(await incoming.WaitForGoodbyeAsync(NoEventWindow));
    }

    [Fact]
    public async Task AdoptingAgain_ReplacesThePreviousAdoptionWithoutTouchingIt()
    {
        await using var host = await StartHostAsync();
        var (first, firstConnection) = DialledSession();
        using var _f = first;
        var (second, _) = DialledSession();
        using var _s = second;

        host.AdoptClientInitiated(first, "first-server");
        host.AdoptClientInitiated(second, "second-server");

        // The replaced session is released from arbitration, not disconnected: it belongs to the
        // application, and adopting another says nothing about whether it is still wanted.
        Assert.Equal(ConnectionState.Connected, first.ConnectionState);
        Assert.Null(firstConnection.LastDisconnectReason);

        // And the replaced session's own teardown does not release the adoption that replaced it.
        await first.DisconnectAsync(GoodbyeReasons.UserRequest);

        await using var incoming = new FakeServer(TestPsk, ["playback"]);
        await incoming.ConnectAsync(host.ListeningPort);
        Assert.Equal("concurrent_attempt", await incoming.WaitForGoodbyeAsync(Timeout));
    }

    [Fact]
    public async Task AdoptingTwiceUnderTheSameServerId_KeepsTheSecondAndReleasesTheFirst()
    {
        // Re-adopting under the SAME id is the case the docs have to answer for: the second
        // adoption wins, and the first client is released rather than disconnected. Its later
        // teardown then leaves the surviving adoption alone — which it must, since both
        // adoptions answer to the same server id.
        await using var host = await StartHostAsync();
        var (first, firstConnection) = DialledSession();
        using var _f = first;
        var (second, _) = DialledSession();
        using var _s = second;

        host.AdoptClientInitiated(first, DialledServerId);
        host.AdoptClientInitiated(second, DialledServerId);

        Assert.Equal(DialledServerId, host.AdoptedClientInitiatedServerId);
        Assert.Equal(ConnectionState.Connected, first.ConnectionState);
        Assert.Null(firstConnection.LastDisconnectReason);

        await first.DisconnectAsync(GoodbyeReasons.UserRequest);

        Assert.Equal(DialledServerId, host.AdoptedClientInitiatedServerId);

        await using var incoming = new FakeServer(TestPsk, ["playback"]);
        await incoming.ConnectAsync(host.ListeningPort);
        Assert.Equal("concurrent_attempt", await incoming.WaitForGoodbyeAsync(Timeout));
    }

    [Fact]
    public async Task ReleasingAServerIdThatIsNotAdopted_LeavesTheAdoptionInPlace()
    {
        await using var host = await StartHostAsync();
        var (dialled, _) = DialledSession();
        using var _d = dialled;
        host.AdoptClientInitiated(dialled, DialledServerId);

        host.ReleaseClientInitiated("some-other-server");

        await using var incoming = new FakeServer(TestPsk, ["playback"]);
        await incoming.ConnectAsync(host.ListeningPort);
        Assert.Equal("concurrent_attempt", await incoming.WaitForGoodbyeAsync(Timeout));
    }
}
