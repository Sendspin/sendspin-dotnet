using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// #253's other half: a client-initiated session the application registers with
/// <see cref="SendspinHostService.AdoptClientInitiated"/> is visible to arbitration, so an
/// incoming server-initiated connection loses at the door instead of being accepted as "no
/// existing connection" — and the host never touches the adopted session while doing it.
/// </summary>
/// <remarks>
/// The host is constructed but never started: adoption and arbitration need neither the
/// listener nor the mDNS advertiser, and starting the advertiser in a unit test would put real
/// multicast traffic on the network. The incoming connection an arbitration test needs comes
/// from <see cref="IncomingLoopback"/>, so the goodbye is asserted on the wire.
/// </remarks>
public class SendspinHostServiceAdoptionTests
{
    private const string DialledServerId = "dialled-server";

    private static readonly TimeSpan GoodbyeWindow = TimeSpan.FromSeconds(5);

    // Bounds a wait that has to sit out its whole duration ("no goodbye arrives").
    private static readonly TimeSpan NoEventWindow = TimeSpan.FromMilliseconds(750);

    private static SendspinHostService NewHost() => new(NullLoggerFactory.Instance);

    /// <summary>A connected client-initiated session, as the application would hold one.</summary>
    private static (SendspinClientService Client, FakeSendspinConnection Connection) DialledSession()
    {
        var connection = new FakeSendspinConnection();
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        var client = new SendspinClientService(NullLogger<SendspinClientService>.Instance, connection);
        return (client, connection);
    }

    /// <summary>A client over a real incoming connection, as the host builds for a server that
    /// dialled in — what arbitration is asked to decide about.</summary>
    private static SendspinClientService IncomingClient(IncomingLoopback loopback) =>
        new(NullLogger<SendspinClientService>.Instance, loopback.Incoming);

    private static Task<bool> PeerGetsGoodbyeAsync(IncomingLoopback loopback, TimeSpan timeout) =>
        loopback.PeerReceivesAsync(m => m.Contains("client/goodbye", StringComparison.Ordinal), timeout);

    [Fact]
    public async Task AdoptedClientInitiatedSession_MakesAnIncomingServerLoseArbitration()
    {
        await using var host = NewHost();
        var (dialled, dialledConnection) = DialledSession();
        using var _d = dialled;
        host.AdoptClientInitiated(dialled, DialledServerId);

        await using var loopback = await IncomingLoopback.StartAsync();
        using var incoming = IncomingClient(loopback);

        bool accepted = await host.ArbitrateConnectionAsync(incoming, loopback.Incoming, "srv-b");

        Assert.False(accepted);
        Assert.True(await PeerGetsGoodbyeAsync(loopback, GoodbyeWindow));

        // Refused at the door: the caller's false is what keeps it out of _connections, so it is
        // never announced and never reported as connected.
        Assert.Empty(host.ConnectedServers);

        // And the session the application dialled is untouched by the whole episode.
        Assert.Equal(ConnectionState.Connected, dialled.ConnectionState);
        Assert.Equal(ConnectionState.Connected, dialledConnection.State);
    }

    [Fact]
    public async Task WithoutAnAdoptedSession_AnIncomingServerIsAcceptedAsBefore()
    {
        // Positive control for the test above, and the guard on the existing behaviour: with
        // nothing adopted, arbitration still takes the first server that arrives.
        await using var host = NewHost();
        await using var loopback = await IncomingLoopback.StartAsync();
        using var incoming = IncomingClient(loopback);

        bool accepted = await host.ArbitrateConnectionAsync(incoming, loopback.Incoming, "srv-b");

        Assert.True(accepted);
        Assert.False(await PeerGetsGoodbyeAsync(loopback, NoEventWindow));
    }

    [Fact]
    public async Task StoppingAndDisposingTheHost_LeavesTheAdoptedSessionAlone()
    {
        var host = NewHost();
        var (dialled, dialledConnection) = DialledSession();
        using var _d = dialled;
        host.AdoptClientInitiated(dialled, DialledServerId);

        await host.StopAsync();
        await host.DisposeAsync();

        Assert.Equal(ConnectionState.Connected, dialled.ConnectionState);
        Assert.Equal(ConnectionState.Connected, dialledConnection.State);
    }

    [Fact]
    public async Task DisconnectAllAsync_LeavesTheAdoptedSessionConnected()
    {
        await using var host = NewHost();
        var (dialled, dialledConnection) = DialledSession();
        using var _d = dialled;
        host.AdoptClientInitiated(dialled, DialledServerId);

        // The call the application makes to leave the other servers FOR this session. Killing
        // it here is precisely the cascade #253 reported.
        await host.DisconnectAllAsync();

        Assert.Equal(DialledServerId, host.AdoptedClientInitiatedServerId);
        Assert.Equal(ConnectionState.Connected, dialled.ConnectionState);
        Assert.Equal(ConnectionState.Connected, dialledConnection.State);
    }

    [Fact]
    public async Task ReleaseClientInitiated_ClearsTheAdoptionWithoutTouchingTheClient()
    {
        await using var host = NewHost();
        var (dialled, _) = DialledSession();
        using var _d = dialled;
        host.AdoptClientInitiated(dialled, DialledServerId);

        host.ReleaseClientInitiated(DialledServerId);

        Assert.Null(host.AdoptedClientInitiatedServerId);
        Assert.Equal(ConnectionState.Connected, dialled.ConnectionState);
    }

    [Fact]
    public async Task ReleasingAServerIdThatIsNotAdopted_LeavesTheAdoptionInPlace()
    {
        await using var host = NewHost();
        var (dialled, _) = DialledSession();
        using var _d = dialled;
        host.AdoptClientInitiated(dialled, DialledServerId);

        host.ReleaseClientInitiated("some-other-server");

        Assert.Equal(DialledServerId, host.AdoptedClientInitiatedServerId);
    }

    [Fact]
    public async Task AdoptedSessionDisconnecting_ReleasesItself()
    {
        // Arbitrating on behalf of a connection that is gone would lock the client out of every
        // server on the network.
        await using var host = NewHost();
        var (dialled, _) = DialledSession();
        using var _d = dialled;
        host.AdoptClientInitiated(dialled, DialledServerId);

        await dialled.DisconnectAsync("user_request");

        Assert.Null(host.AdoptedClientInitiatedServerId);
    }

    [Fact]
    public async Task AdoptingAnAlreadyDisconnectedSession_DoesNotHoldArbitrationShut()
    {
        // Adopted after it died, so the disconnect event that would normally release the
        // adoption has already been and gone.
        await using var host = NewHost();
        var (dialled, _) = DialledSession();
        using var _d = dialled;
        await dialled.DisconnectAsync("user_request");

        host.AdoptClientInitiated(dialled, DialledServerId);

        Assert.Null(host.AdoptedClientInitiatedServerId);
    }

    [Fact]
    public async Task AdoptingAgain_ReplacesThePreviousAdoptionWithoutTouchingIt()
    {
        await using var host = NewHost();
        var (first, _) = DialledSession();
        using var _f = first;
        var (second, _) = DialledSession();
        using var _s = second;

        host.AdoptClientInitiated(first, "first-server");
        host.AdoptClientInitiated(second, "second-server");

        Assert.Equal("second-server", host.AdoptedClientInitiatedServerId);

        // The replaced session is released from arbitration, not disconnected: it belongs to the
        // application, and adopting another says nothing about whether it is still wanted.
        Assert.Equal(ConnectionState.Connected, first.ConnectionState);

        // And its own teardown does not release the adoption that replaced it — the case that
        // makes the release match on the client instance, not just on the id.
        await first.DisconnectAsync("user_request");

        Assert.Equal("second-server", host.AdoptedClientInitiatedServerId);
    }
}
