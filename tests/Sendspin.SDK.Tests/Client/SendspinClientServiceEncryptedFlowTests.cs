using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Coverage for the encrypted-protocol hello/activate flow: server/hello arrives first
/// (name only), the client answers with the encrypted-shape client/hello, and the
/// initial server/activate completes the handshake, gated by the spec's admissibility
/// table for the matched PSK.
/// </summary>
public class SendspinClientServiceEncryptedFlowTests
{
    /// <summary>
    /// The fake is put in the Connected state before each scenario so admissibility outcomes are
    /// observable: inadmissible activates disconnect (with a recorded reason), admissible ones don't.
    /// </summary>
    private static readonly Uri ServerUri = new("ws://test.local:8927/sendspin");

    [Fact]
    public void ServerHello_TriggersEncryptedClientHello_WithoutClientIdOrVersion()
    {
        var (client, connection, _) = TestClient.Create(PskCategory.Sentinel);
        using var _c = client;
        connection.ConnectAsync(ServerUri).GetAwaiter().GetResult();

        connection.RaiseTextMessageReceived("""
            {"type":"server/hello","payload":{"name":"srv"}}
            """);

        var hello = Assert.IsType<ClientHelloMessage>(Assert.Single(connection.SentMessages));
        Assert.Equal("none", hello.Payload.TrustLevel);
        Assert.NotNull(hello.Payload.UnpairedAccess);
        Assert.False(hello.Payload.UnpairedAccess.Enabled);

        // Serialized shape omits client_id/version entirely
        string json = MessageSerializer.Serialize(hello);
        Assert.DoesNotContain("client_id", json);
        Assert.DoesNotContain("\"version\"", json);
        Assert.Contains("\"trust_level\":\"none\"", json);

        // Identity comes from server/init via the Noise session
        Assert.Equal(FakeNoiseSession.FakeServerId, client.ServerId);
        Assert.Equal("srv", client.ServerName);
    }

    [Fact]
    public void HandshakeCompletes_OnInitialServerActivate_NotOnServerHello()
    {
        // Clock already converged: this test asserts the connected tail ran (initial
        // client/state), which a sync-requiring client otherwise defers until convergence —
        // InitialClientStateGatingTests owns that gate.
        var (client, connection, _) = TestClient.Create(
            PskCategory.Sentinel,
            configure: options => options with { ClockSynchronizer = new ConvergedClockSynchronizer() });
        using var _c = client;
        connection.ConnectAsync(ServerUri).GetAwaiter().GetResult();

        int helloEvents = 0;
        int activateEvents = 0;
        client.ServerHelloReceived += (_, _) => helloEvents++;
        client.ServerActivateReceived += (_, _) => activateEvents++;

        connection.RaiseTextMessageReceived("""
            {"type":"server/hello","payload":{"name":"srv"}}
            """);

        // Handshake tail (initial client/state, time sync) must NOT run yet:
        // only the client/hello reply may be sent before server/activate.
        Assert.Equal(0, helloEvents);
        Assert.Single(connection.SentMessages);

        connection.RaiseTextMessageReceived("""
            {"type":"server/activate","payload":{"activities":[],"active_roles":[]}}
            """);

        Assert.Equal(1, helloEvents);
        Assert.Equal(1, activateEvents);
        Assert.NotNull(client.LastServerActivate);
        // The connected tail ran: initial client/state was sent after activate.
        Assert.Contains(connection.SentMessages, m => m is ClientStateMessage);
    }

    [Fact]
    public async Task ConnectAsync_AwaitsTheServerDrivenHandshake_AndCompletesOnActivate()
    {
        // The client's own connect path (not just the fake's): ConnectAsync opens the
        // connection and then parks on the handshake TCS. Because the encrypted flow is
        // server-driven, the task must still be pending after server/hello alone.
        // Clock already converged so the final assertion (initial client/state sent) is not
        // deferred behind sync convergence.
        var (client, connection, _) = TestClient.Create(
            PskCategory.LongTerm,
            configure: options => options with { ClockSynchronizer = new ConvergedClockSynchronizer() });
        using var _c = client;

        var connectTask = client.ConnectAsync(ServerUri);

        connection.RaiseTextMessageReceived("""
            {"type":"server/hello","payload":{"name":"srv"}}
            """);

        Assert.False(connectTask.IsCompleted);
        Assert.IsType<ClientHelloMessage>(Assert.Single(connection.SentMessages));

        connection.RaiseTextMessageReceived("""
            {"type":"server/activate","payload":{"activities":["playback"],"active_roles":["player@v1"]}}
            """);

        await connectTask;

        Assert.Equal(Sendspin.SDK.Connection.ConnectionState.Connected, connection.State);
        Assert.Equal(FakeNoiseSession.FakeServerId, client.ServerId);
        Assert.NotNull(client.LastServerActivate);
        Assert.Contains(connection.SentMessages, m => m is ClientStateMessage);
    }

    [Fact]
    public async Task ConnectAsync_WhenCallerCancels_ThrowsCanceled_WithoutTheHandshakeTimeoutClose()
    {
        var (client, connection, _) = TestClient.Create(PskCategory.LongTerm);
        using var _c = client;
        using var cts = new CancellationTokenSource();

        var connectTask = client.ConnectAsync(ServerUri, cts.Token);

        connection.RaiseTextMessageReceived("""
            {"type":"server/hello","payload":{"name":"srv"}}
            """);
        Assert.False(connectTask.IsCompleted);

        cts.Cancel();

        // The caller's token is linked into the handshake wait, so cancelling it surfaces as
        // OperationCanceledException - distinct from the TimeoutException the 30 s handshake
        // timeout raises, and without that path's client/goodbye 'restart' close.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => connectTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(connection.LastDisconnectReason);
    }

    [Fact]
    public void ActivateRoles_MirroredIntoLastServerHello_AndPersistAcrossOmission()
    {
        var (client, connection, _) = TestClient.Create(PskCategory.Sentinel, unpairedAccess: true);
        using var _c = client;
        connection.ConnectAsync(ServerUri).GetAwaiter().GetResult();

        connection.RaiseTextMessageReceived("""
            {"type":"server/hello","payload":{"name":"srv"}}
            """);
        connection.RaiseTextMessageReceived("""
            {"type":"server/activate","payload":{"activities":["playback"],"active_roles":["player@v1","controller@v1"]}}
            """);

        Assert.Equal(["player@v1", "controller@v1"], client.LastServerHello!.ActiveRoles);

        // A later activate omitting active_roles keeps the previous roles.
        connection.RaiseTextMessageReceived("""
            {"type":"server/activate","payload":{"activities":["playback"]}}
            """);

        Assert.Equal(["player@v1", "controller@v1"], client.LastServerHello!.ActiveRoles);
    }

    [Fact]
    public void SentinelPsk_PlaybackWithoutUnpairedAccess_ClosesWithPairingRequired()
    {
        var (client, connection, _) = TestClient.Create(PskCategory.Sentinel);
        using var _c = client;
        connection.ConnectAsync(ServerUri).GetAwaiter().GetResult();

        connection.RaiseTextMessageReceived("""
            {"type":"server/hello","payload":{"name":"srv"}}
            """);
        connection.RaiseTextMessageReceived("""
            {"type":"server/activate","payload":{"activities":["playback"],"active_roles":["player@v1"]}}
            """);

        // Enabling unpaired access would make this admissible => 'pairing_required'.
        Assert.Equal(Sendspin.SDK.Connection.ConnectionState.Disconnected, connection.State);
        Assert.Equal("pairing_required", connection.LastDisconnectReason);
    }

    [Fact]
    public void SentinelPsk_PlaybackWithUnpairedAccess_IsAdmissible()
    {
        var (client, connection, _) = TestClient.Create(PskCategory.Sentinel, unpairedAccess: true);
        using var _c = client;
        connection.ConnectAsync(ServerUri).GetAwaiter().GetResult();

        connection.RaiseTextMessageReceived("""
            {"type":"server/hello","payload":{"name":"srv"}}
            """);
        connection.RaiseTextMessageReceived("""
            {"type":"server/activate","payload":{"activities":["playback"],"active_roles":["player@v1"]}}
            """);

        Assert.Equal(Sendspin.SDK.Connection.ConnectionState.Connected, connection.State);
        Assert.NotNull(client.LastServerActivate);
    }

    [Fact]
    public void SentinelPsk_UnknownActivity_ClosesUnauthorized()
    {
        var (client, connection, _) = TestClient.Create(PskCategory.Sentinel, unpairedAccess: true);
        using var _c = client;
        connection.ConnectAsync(ServerUri).GetAwaiter().GetResult();

        connection.RaiseTextMessageReceived("""
            {"type":"server/hello","payload":{"name":"srv"}}
            """);
        connection.RaiseTextMessageReceived("""
            {"type":"server/activate","payload":{"activities":["management"],"active_roles":[]}}
            """);

        // 'management' is not an activity any more (#183), and an activity outside the
        // vocabulary is never admissible, regardless of unpaired access.
        Assert.Equal(Sendspin.SDK.Connection.ConnectionState.Disconnected, connection.State);
        Assert.Equal("unauthorized", connection.LastDisconnectReason);
    }

    [Fact]
    public void LongTermPsk_Playback_IsAdmissible()
    {
        var (client, connection, _) = TestClient.Create(PskCategory.LongTerm);
        using var _c = client;
        connection.ConnectAsync(ServerUri).GetAwaiter().GetResult();

        connection.RaiseTextMessageReceived("""
            {"type":"server/hello","payload":{"name":"srv"}}
            """);
        connection.RaiseTextMessageReceived("""
            {"type":"server/activate","payload":{"activities":["playback"],"active_roles":["player@v1"]}}
            """);

        Assert.Equal(Sendspin.SDK.Connection.ConnectionState.Connected, connection.State);
    }

    [Fact]
    public void SentinelPsk_EmptyActivitiesWithRoles_WithoutUnpairedAccess_Closes()
    {
        var (client, connection, _) = TestClient.Create(PskCategory.Sentinel);
        using var _c = client;
        connection.ConnectAsync(ServerUri).GetAwaiter().GetResult();

        connection.RaiseTextMessageReceived("""
            {"type":"server/hello","payload":{"name":"srv"}}
            """);
        // Empty activities is an allowed set, but non-empty active_roles requires a
        // playback-capable connection - which the sentinel PSK without unpaired access
        // is not.
        connection.RaiseTextMessageReceived("""
            {"type":"server/activate","payload":{"activities":[],"active_roles":["player@v1"]}}
            """);

        Assert.Equal(Sendspin.SDK.Connection.ConnectionState.Disconnected, connection.State);
        Assert.Equal("pairing_required", connection.LastDisconnectReason);
    }

    [Fact]
    public void InadmissibleActivate_AlwaysDisconnects_NoBypassPath()
    {
        // A Sentinel-keyed session with unpaired access off may not be granted playback.
        // Before the clean break, a client with no session info skipped this gate entirely.
        var (client, connection, _) = TestClient.Create(
            category: PskCategory.Sentinel,
            unpairedAccess: false);
        using var _c = client;

        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""
            {"type":"server/hello","payload":{"name":"srv"}}
            """);
        connection.RaiseTextMessageReceived("""
            {"type":"server/activate","payload":{"activities":["playback"],"active_roles":["player@v1"]}}
            """);

        Assert.Equal(ConnectionState.Disconnected, connection.State);
        Assert.Equal("pairing_required", connection.LastDisconnectReason);
    }

    [Fact]
    public void ServerActivate_WithNullMatchedPsk_AlwaysDisconnects_NoBypassPath()
    {
        // Before the clean break, ValidateActivateAdmissibility had a `psk is null => return
        // true` bypass. TestClient.Create always seeds a non-null MatchedPsk, so that branch
        // needs its own coverage: a session that somehow completed with no matched PSK must
        // still be refused, never waved through.
        var (client, connection, session) = TestClient.Create(
            category: PskCategory.Sentinel,
            unpairedAccess: false);
        using var _c = client;
        session.MatchedPsk = null;

        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""
            {"type":"server/hello","payload":{"name":"srv"}}
            """);
        connection.RaiseTextMessageReceived("""
            {"type":"server/activate","payload":{"activities":["playback"],"active_roles":["player@v1"]}}
            """);

        Assert.Equal(ConnectionState.Disconnected, connection.State);
        Assert.Equal("unauthorized", connection.LastDisconnectReason);
    }
}
