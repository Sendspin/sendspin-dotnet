using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
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
    private const string FakeServerId = "GFsV9tLaSQm9HcFWpKsgYQOr7wFTvNUtkmFwuVz3zoo";

    private sealed class FakeNoiseSession : INoiseSessionInfo
    {
        public string? ServerId { get; set; } = FakeServerId;
        public NoisePsk? MatchedPsk { get; set; } =
            new(NoiseConstants.SentinelPsk.ToArray(), PskCategory.Sentinel);
    }

    private static (SendspinClientService Client, FakeSendspinConnection Connection, FakeNoiseSession Session)
        CreateEncryptedClient(bool unpairedAccess = false, PskCategory category = PskCategory.Sentinel)
    {
        var connection = new FakeSendspinConnection();
        var session = new FakeNoiseSession
        {
            MatchedPsk = new NoisePsk(NoiseConstants.SentinelPsk.ToArray(), category),
        };
        var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection,
            capabilities: new ClientCapabilities { UnpairedAccessEnabled = unpairedAccess },
            noiseSession: session);
        // Put the fake in the Connected state so admissibility outcomes are observable:
        // inadmissible activates disconnect (with a recorded reason), admissible ones don't.
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        return (client, connection, session);
    }

    [Fact]
    public void ServerHello_TriggersEncryptedClientHello_WithoutClientIdOrVersion()
    {
        var (client, connection, _) = CreateEncryptedClient();
        using var _c = client;

        connection.RaiseTextMessageReceived("""
            {"type":"server/hello","payload":{"name":"srv"}}
            """);

        var hello = Assert.IsType<ClientHelloMessage>(Assert.Single(connection.SentMessages));
        Assert.Null(hello.Payload.ClientId);
        Assert.Null(hello.Payload.Version);
        Assert.Equal("none", hello.Payload.TrustLevel);
        Assert.NotNull(hello.Payload.UnpairedAccess);
        Assert.False(hello.Payload.UnpairedAccess.Enabled);

        // Serialized shape omits client_id/version entirely
        string json = MessageSerializer.Serialize(hello);
        Assert.DoesNotContain("client_id", json);
        Assert.DoesNotContain("\"version\"", json);
        Assert.Contains("\"trust_level\":\"none\"", json);

        // Identity comes from server/init via the Noise session
        Assert.Equal(FakeServerId, client.ServerId);
        Assert.Equal("srv", client.ServerName);
    }

    [Fact]
    public void HandshakeCompletes_OnInitialServerActivate_NotOnServerHello()
    {
        var (client, connection, _) = CreateEncryptedClient();
        using var _c = client;

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
    public void ActivateRoles_MirroredIntoLastServerHello_AndPersistAcrossOmission()
    {
        var (client, connection, _) = CreateEncryptedClient(unpairedAccess: true);
        using var _c = client;

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
        var (client, connection, _) = CreateEncryptedClient(unpairedAccess: false);
        using var _c = client;

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
        var (client, connection, _) = CreateEncryptedClient(unpairedAccess: true);
        using var _c = client;

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
    public void SentinelPsk_ManagementActivity_ClosesUnauthorized()
    {
        var (client, connection, _) = CreateEncryptedClient(unpairedAccess: true);
        using var _c = client;

        connection.RaiseTextMessageReceived("""
            {"type":"server/hello","payload":{"name":"srv"}}
            """);
        connection.RaiseTextMessageReceived("""
            {"type":"server/activate","payload":{"activities":["management"],"active_roles":[]}}
            """);

        // Management is never admissible on the sentinel PSK, regardless of unpaired access.
        Assert.Equal(Sendspin.SDK.Connection.ConnectionState.Disconnected, connection.State);
        Assert.Equal("unauthorized", connection.LastDisconnectReason);
    }

    [Fact]
    public void LongTermPsk_PlaybackAndManagement_IsAdmissible()
    {
        var (client, connection, _) = CreateEncryptedClient(category: PskCategory.LongTerm);
        using var _c = client;

        connection.RaiseTextMessageReceived("""
            {"type":"server/hello","payload":{"name":"srv"}}
            """);
        connection.RaiseTextMessageReceived("""
            {"type":"server/activate","payload":{"activities":["playback","management"],"active_roles":["player@v1"]}}
            """);

        Assert.Equal(Sendspin.SDK.Connection.ConnectionState.Connected, connection.State);
    }

    [Fact]
    public void SentinelPsk_EmptyActivitiesWithRoles_WithoutUnpairedAccess_Closes()
    {
        var (client, connection, _) = CreateEncryptedClient(unpairedAccess: false);
        using var _c = client;

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
    public void LegacyFlow_WithoutNoiseSession_IsUnchanged()
    {
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection);

        connection.RaiseTextMessageReceived("""
            {"type":"server/hello","payload":{"server_id":"legacy-1","name":"srv","version":1,"active_roles":["player@v1"],"connection_reason":"playback"}}
            """);

        // Legacy: server/hello completes the handshake directly; no activate needed.
        Assert.Equal("legacy-1", client.ServerId);
        Assert.Contains(connection.SentMessages, m => m is ClientStateMessage);
    }
}
