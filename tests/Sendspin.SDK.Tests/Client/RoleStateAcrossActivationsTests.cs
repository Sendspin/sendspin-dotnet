using Sendspin.SDK.Client;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Models;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// What a <c>server/activate</c> keeps and discards of the roles active before it: across an
/// in-band re-handshake, where the activate is "a subsequent one on the same connection" (spec
/// #287) and so persists the roles it omits and removes the ones it leaves out (spec #275), and
/// for a removed stream role, whose output stops without a preceding <c>stream/end</c> (spec
/// #289).
/// </summary>
public class RoleStateAcrossActivationsTests
{
    private const string TrackAState = """
        {"type":"server/state","payload":{"metadata":{"title":"Track A"},"controller":{"volume":40}}}
        """;

    /// <summary>
    /// An in-band re-handshake as the client observes it: a fresh handshake hash, and the PSK
    /// the new session matched.
    /// </summary>
    private static void Rehandshake(FakeNoiseSession session, PskCategory category = PskCategory.LongTerm)
    {
        session.MatchedPsk = new NoisePsk(NoiseConstants.SentinelPsk.ToArray(), category);
        session.HandshakeHash = Enumerable.Repeat((byte)0xAB, 32).ToArray();
    }

    private static (SendspinClientService Client, FakeSendspinConnection Connection, FakeAudioPipeline Pipeline)
        PlayerClient()
    {
        var pipe = new FakeAudioPipeline();
        var (client, connection, _) = TestClient.Create(
            connected: false,
            configure: options => options with
            {
                AudioPipeline = pipe,
                ClockSynchronizer = new ConvergedClockSynchronizer(),
                Capabilities = new ClientCapabilities
                {
                    Roles = new List<string> { "player@v1", "artwork@v1" },
                },
            });
        return (client, connection, pipe);
    }

    [Fact]
    public void Rehandshake_ThenActivateOmittingActiveRoles_KeepsTheRolesAndTheirState()
    {
        var (client, connection, session) = TestClient.Create();
        using var _c = client;

        TestClient.CompleteHandshake(connection, "metadata@v1", "controller@v1");
        connection.RaiseTextMessageReceived(TrackAState);

        Rehandshake(session);
        connection.RaiseTextMessageReceived("""
            {"type":"server/activate","payload":{"activities":["playback"]}}
            """);

        Assert.Equal(new[] { "metadata@v1", "controller@v1" }, client.LastServerHello!.ActiveRoles);
        Assert.Equal("Track A", client.CurrentGroup?.Metadata?.Title);
        Assert.Equal(40, client.CurrentGroup?.Volume);
    }

    [Fact]
    public void Rehandshake_ThenActivateDroppingMetadata_ClearsItAndAnnounces()
    {
        var (client, connection, session) = TestClient.Create();
        using var _c = client;

        TestClient.CompleteHandshake(connection, "metadata@v1", "controller@v1");
        connection.RaiseTextMessageReceived(TrackAState);

        GroupState? groupRaised = null;
        client.GroupStateChanged += (_, g) => groupRaised = g;

        Rehandshake(session);
        connection.RaiseTextMessageReceived("""
            {"type":"server/activate","payload":{"activities":["playback"],"active_roles":["controller@v1"]}}
            """);

        Assert.Null(client.CurrentGroup?.Metadata);
        Assert.Same(client.CurrentGroup, groupRaised);

        // The role still active is untouched.
        Assert.Equal(40, client.CurrentGroup?.Volume);
    }

    [Fact]
    public void Rehandshake_OntoASessionNotPlaybackCapable_TreatsThePersistedRolesAsEmpty()
    {
        // messaging.md: "if a later activation changes activities so the connection is no longer
        // playback-capable without explicitly sending active_roles, the persisted roles are
        // treated as empty rather than the message rejected". An unpaired session on a client
        // without unpaired access is the case: [] is an allowed set, ['playback'] is not.
        var (client, connection, session) = TestClient.Create();
        using var _c = client;

        TestClient.CompleteHandshake(connection, "metadata@v1", "controller@v1");
        connection.RaiseTextMessageReceived(TrackAState);

        Rehandshake(session, PskCategory.Sentinel);
        connection.RaiseTextMessageReceived("""
            {"type":"server/activate","payload":{"activities":[]}}
            """);

        Assert.Equal(Sendspin.SDK.Connection.ConnectionState.Connected, connection.State);
        Assert.Empty(client.LastServerHello!.ActiveRoles);
        Assert.Null(client.CurrentGroup?.Metadata);
    }

    [Fact]
    public void Rehandshake_OntoAnUnpairedSession_WithSourceStillPersisted_ClosesUnauthorized()
    {
        // The source trust gate reads the roles in effect, not just the ones the message
        // carries: a source@v1 persisted across a re-handshake down to an unpaired session is
        // active there on exactly the terms an explicit one would be.
        var (client, connection, session) = TestClient.Create(
            unpairedAccess: true,
            configure: options => options with
            {
                Capabilities = new ClientCapabilities { Roles = ["source@v1"] },
                CaptureDevice = new FakeCaptureDevice(),
                ClockSynchronizer = new ConvergedClockSynchronizer(),
            });
        using var _c = client;

        TestClient.CompleteHandshake(connection, "source@v1");

        Rehandshake(session, PskCategory.Sentinel);
        connection.RaiseTextMessageReceived("""
            {"type":"server/activate","payload":{"activities":["playback"]}}
            """);

        Assert.Equal(Sendspin.SDK.Connection.ConnectionState.Disconnected, connection.State);
        Assert.Equal("unauthorized", connection.LastDisconnectReason);
    }

    [Fact]
    public void Activate_DroppingPlayer_StopsPlaybackAndClearsItsBuffers_WithoutAStreamEnd()
    {
        var (client, connection, pipe) = PlayerClient();
        using var _c = client;

        TestClient.CompleteHandshake(connection, "player@v1", "artwork@v1");

        var streamEnds = 0;
        client.StreamEndReceived += (_, _) => streamEnds++;

        connection.RaiseTextMessageReceived("""
            {"type":"server/activate","payload":{"activities":["playback"],"active_roles":["artwork@v1"]}}
            """);

        // The pipeline's stop is what halts output and discards what it had buffered.
        Assert.Equal(1, pipe.StopCount);
        Assert.Equal(0, streamEnds);
    }

    [Fact]
    public void Activate_DroppingAnotherRole_LeavesPlaybackRunning()
    {
        var (client, connection, pipe) = PlayerClient();
        using var _c = client;

        TestClient.CompleteHandshake(connection, "player@v1", "artwork@v1");

        connection.RaiseTextMessageReceived("""
            {"type":"server/activate","payload":{"activities":["playback"],"active_roles":["player@v1"]}}
            """);

        Assert.Equal(0, pipe.StopCount);
    }
}
