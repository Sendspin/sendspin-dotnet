using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Connection.Noise.Pairing;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Ownership of the unpaired-access setting: a server's <c>management/set-pairing-config</c>
/// changes the client's <em>effective</em> state and raises
/// <see cref="SendspinClientService.PairingConfigChanged"/>, but never writes to the
/// <see cref="ClientCapabilities"/> instance the app owns.
/// </summary>
public class PairingConfigOwnershipTests
{
    private static readonly Uri ServerUri = new("ws://test.local:8927/sendspin");
    private static readonly byte[] SessionPsk = Enumerable.Repeat((byte)7, 32).ToArray();

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static ManagementResultPayload LastResult(FakeSendspinConnection connection) =>
        connection.SentMessages.OfType<ManagementResultMessage>().Last().Payload;

    /// <summary>
    /// A client on a paired, management-activated session, built around the caller's
    /// <see cref="ClientCapabilities"/> instance so tests can observe whether the SDK
    /// writes to it.
    /// </summary>
    private static (SendspinClientService Client, FakeSendspinConnection Connection, FakeNoiseSession Session, InMemoryPairingRecordStore Store)
        CreateManagementClient(ClientCapabilities capabilities)
    {
        var store = new InMemoryPairingRecordStore();
        store.Upsert(new PairingRecord(SessionPsk, PskCategory.LongTerm, FakeNoiseSession.FakeServerId));
        var (client, connection, session) = TestClient.Create(
            configure: options =>
            {
                options.PairingRecordStore = store;
                options.Capabilities = capabilities;
            });
        session.MatchedPsk = new NoisePsk(SessionPsk, PskCategory.LongTerm, FakeNoiseSession.FakeServerId);
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["playback","management"],"active_roles":[]}}""");
        return (client, connection, session, store);
    }

    [Fact]
    public void SetPairingConfig_FlipsEffectiveState_NotTheAppsCapabilitiesInstance()
    {
        var capabilities = new ClientCapabilities();
        var (client, connection, session, _) = CreateManagementClient(capabilities);
        using var _c = client;
        var events = new List<PairingConfigChangedEventArgs>();
        client.PairingConfigChanged += (_, e) => events.Add(e);

        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"unpaired_access":{"enabled":true}}}""");
        Assert.Equal("ok", LastResult(connection).Result);

        // 1. The app's config object is untouched: the SDK does not own it.
        Assert.False(capabilities.UnpairedAccessEnabled);

        // 2. The app was told to persist the new effective value.
        var change = Assert.Single(events);
        Assert.True(change.UnpairedAccessEnabled);
        Assert.False(change.PairingPskReplaced);

        // 3. The change is actually in effect: a later sentinel-keyed connection offering
        //    playback is admitted, where without the flip it would close 'pairing_required'.
        session.MatchedPsk = new NoisePsk(NoiseConstants.SentinelPsk.ToArray(), PskCategory.Sentinel);
        connection.ConnectAsync(ServerUri).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["playback"],"active_roles":["player@v1"]}}""");

        Assert.Equal(ConnectionState.Connected, connection.State);
        Assert.Null(connection.LastDisconnectReason);
        Assert.Equal(["playback"], client.LastServerActivate!.ActivitiesList);
    }

    [Fact]
    public void UnpairedAccessConfiguredOn_IsEffective_BeforeAnyServerChange()
    {
        // The effective value starts from the app's configuration, not from false.
        var capabilities = new ClientCapabilities { UnpairedAccessEnabled = true };
        var (client, connection, _) = TestClient.Create(
            PskCategory.Sentinel,
            configure: options => options.Capabilities = capabilities);
        using var _c = client;
        var events = new List<PairingConfigChangedEventArgs>();
        client.PairingConfigChanged += (_, e) => events.Add(e);

        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["playback"],"active_roles":["player@v1"]}}""");

        Assert.Equal(ConnectionState.Connected, connection.State);
        Assert.NotNull(client.LastServerActivate);
        // No server changed anything, so nothing to persist.
        Assert.Empty(events);
    }

    [Fact]
    public void ServerSuppliedPairingPsk_RaisesEvent_AndStalesTheOldToken()
    {
        var capabilities = new ClientCapabilities();
        var (client, connection, _, _) = CreateManagementClient(capabilities);
        using var _c = client;
        string before = client.EnsurePairingPsk();
        var events = new List<PairingConfigChangedEventArgs>();
        client.PairingConfigChanged += (_, e) => events.Add(e);

        byte[] newPsk = Enumerable.Repeat((byte)5, 32).ToArray();
        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"pairing_psk":{"psk":"PSK"}}}"""
                .Replace("PSK", ToBase64Url(newPsk)));
        Assert.Equal("ok", LastResult(connection).Result);

        var change = Assert.Single(events);
        Assert.True(change.PairingPskReplaced);
        Assert.False(change.UnpairedAccessEnabled);

        // Task 2's documented contract: the old token stopped being current.
        string after = client.EnsurePairingPsk();
        Assert.NotEqual(before, after);
        Assert.Equal(newPsk, PairingToken.Decode(after).PairingPsk);
    }

    [Fact]
    public void SetPairingConfig_ChangingNothing_DoesNotRaise()
    {
        var capabilities = new ClientCapabilities();
        var (client, connection, _, _) = CreateManagementClient(capabilities);
        using var _c = client;
        var events = new List<PairingConfigChangedEventArgs>();
        client.PairingConfigChanged += (_, e) => events.Add(e);

        // Same value as the current effective state: applied, but nothing changed.
        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"unpaired_access":{"enabled":false}}}""");
        Assert.Equal("ok", LastResult(connection).Result);

        // An empty patch and an unrelated management request change nothing either.
        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{}}""");
        Assert.Equal("ok", LastResult(connection).Result);
        connection.RaiseTextMessageReceived("""{"type":"management/list-records","payload":{}}""");
        Assert.Equal("ok", LastResult(connection).Result);

        Assert.Empty(events);

        // Positive control: the same subscription observes a real change, so the empty
        // list above is the handler declining to raise — not dead event machinery.
        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"unpaired_access":{"enabled":true}}}""");
        Assert.Single(events);
    }

    [Fact]
    public void CompoundRequest_RaisesOneEventDescribingBothChanges()
    {
        var capabilities = new ClientCapabilities();
        var (client, connection, _, store) = CreateManagementClient(capabilities);
        using var _c = client;
        var events = new List<PairingConfigChangedEventArgs>();
        client.PairingConfigChanged += (_, e) => events.Add(e);

        byte[] newPsk = Enumerable.Repeat((byte)6, 32).ToArray();
        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"unpaired_access":{"enabled":true},"pairing_psk":{"psk":"PSK"}}}"""
                .Replace("PSK", ToBase64Url(newPsk)));
        Assert.Equal("ok", LastResult(connection).Result);

        // Exactly one event (Assert.Single fails on zero and on two), describing both
        // changes — not one event per field.
        var change = Assert.Single(events);
        Assert.True(change.UnpairedAccessEnabled);
        Assert.True(change.PairingPskReplaced);

        // Both changes were applied, not merely reported.
        var record = Assert.Single(store.List(), r => r.Category == PskCategory.Pairing);
        Assert.Equal(newPsk, record.Psk.ToArray());
    }

    [Fact]
    public void RequestRefusedPartway_AppliesNothing_AndRaisesNothing()
    {
        // A compound request whose psk is undecodable answers 'invalid' — and must not
        // leave the unpaired_access half applied with no event to report it.
        var capabilities = new ClientCapabilities();
        var (client, connection, session, store) = CreateManagementClient(capabilities);
        using var _c = client;
        var events = new List<PairingConfigChangedEventArgs>();
        client.PairingConfigChanged += (_, e) => events.Add(e);

        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"unpaired_access":{"enabled":true},"pairing_psk":{"psk":"tooshort"}}}""");

        Assert.Equal("invalid", LastResult(connection).Result);
        Assert.Empty(events);
        Assert.DoesNotContain(store.List(), r => r.Category == PskCategory.Pairing);

        // Admissibility is unchanged: the same real-handshake check the flip test uses,
        // expecting refusal — a sentinel-keyed playback activate still closes
        // 'pairing_required' because the flip did not stick.
        session.MatchedPsk = new NoisePsk(NoiseConstants.SentinelPsk.ToArray(), PskCategory.Sentinel);
        connection.ConnectAsync(ServerUri).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["playback"],"active_roles":["player@v1"]}}""");

        Assert.Equal(ConnectionState.Disconnected, connection.State);
        Assert.Equal("pairing_required", connection.LastDisconnectReason);
    }

    [Fact]
    public void ClientHello_AdvertisesTheEffectiveValue_AfterAServerChange()
    {
        // Judgement pinned deliberately: the hello reports what the client will actually
        // do (the effective value), not what the app configured.
        var capabilities = new ClientCapabilities();
        var (client, connection, _, _) = CreateManagementClient(capabilities);
        using var _c = client;

        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"unpaired_access":{"enabled":true}}}""");
        Assert.Equal("ok", LastResult(connection).Result);

        // A later handshake re-advertises unpaired access.
        connection.ConnectAsync(ServerUri).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");

        var hello = connection.SentMessages.OfType<ClientHelloMessage>().Last();
        Assert.True(hello.Payload.UnpairedAccess!.Enabled);
        Assert.False(capabilities.UnpairedAccessEnabled);
    }

    [Fact]
    public void HelloPairMethodDescriptors_CarryOnlySpecDefinedFields()
    {
        // `locked_out` appears in no spec file (README/connection/management/messaging/pairing).
        // The descriptor is method, out_channels?, min_pin_length?, locations? (pairing.md:279-283).
        var capabilities = new ClientCapabilities { PinPairingMethods = { "dynamic_pin" }, MinPinLength = 7 };
        var (client, connection, _, _) = CreateManagementClient(capabilities);
        using var _c = client;

        var hello = connection.SentMessages.OfType<ClientHelloMessage>().Last();
        string json = MessageSerializer.Serialize(hello);

        Assert.DoesNotContain("locked_out", json);

        // Positive control: the descriptor is genuinely present and populated, so the
        // assertion above is not passing on an empty supported_pair_methods list.
        var dynamicPin = Assert.Single(
            hello.Payload.SupportedPairMethods!, m => m.Method == "dynamic_pin");
        Assert.Equal(7, dynamicPin.MinPinLength);
    }

    [Fact]
    public void GetPairingConfig_ReportsTheImplementedPinMethods_NotAnEmptySurface()
    {
        // #122: the client advertised dynamic_pin in client/hello while telling a management
        // server it had no PIN methods at all. Same source of truth, so same answer.
        var capabilities = new ClientCapabilities { PinPairingMethods = { "dynamic_pin" }, MinPinLength = 8 };
        var (client, connection, _, _) = CreateManagementClient(capabilities);
        using var _c = client;

        connection.RaiseTextMessageReceived(
            """{"type":"management/get-pairing-config","payload":{}}""");

        var result = LastResult(connection);
        Assert.Equal("ok", result.Result);
        var data = result.Data!.Value;

        Assert.True(data.GetProperty("pairing_psk").GetProperty("enabled").GetBoolean());
        var dynamicPin = data.GetProperty("dynamic_pin");
        Assert.True(dynamicPin.GetProperty("enabled").GetBoolean());
        Assert.Equal(8, dynamicPin.GetProperty("min_pin_length").GetInt32());
        Assert.False(dynamicPin.GetProperty("escalated").GetBoolean());

        // static_pin is not implemented by this client, so per spec its object is absent —
        // absent for the right reason, which the dynamic_pin clause above proves.
        Assert.False(data.TryGetProperty("static_pin", out _));

        // record_mode is not optional in the spec's data shape.
        Assert.True(data.TryGetProperty("record_mode", out _));
    }
}
