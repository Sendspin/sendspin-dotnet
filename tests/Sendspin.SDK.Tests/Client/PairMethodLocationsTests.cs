using Sendspin.SDK.Client;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Connection.Noise.Pairing;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// The pair-method descriptor's <c>locations</c> hint (pairing.md, "client/hello pair-method
/// descriptor"): where an operator can find a method's configured secret, for
/// <c>static_pin</c> and <c>pairing_psk</c> only, and updated by the client when the secret is
/// rotated (#129).
/// </summary>
public class PairMethodLocationsTests
{
    private static readonly byte[] SessionPsk = Enumerable.Repeat((byte)7, 32).ToArray();

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static ManagementResultPayload LastResult(FakeSendspinConnection connection) =>
        connection.SentMessages.OfType<ManagementResultMessage>().Last().Payload;

    private static PairMethodDescriptor? Descriptor(FakeSendspinConnection connection, string method) =>
        connection.SentMessages.OfType<ClientHelloMessage>().Last()
            .Payload.SupportedPairMethods?.FirstOrDefault(d => d.Method == method);

    /// <summary>
    /// A paired, management-activated client built around the caller's capabilities, with the
    /// dependencies <c>CanRun</c> needs so the pairing code methods are actually advertised (#132).
    /// </summary>
    private static (SendspinClientService Client, FakeSendspinConnection Connection)
        CreateManagementClient(ClientCapabilities capabilities)
    {
        var store = new InMemoryPairingRecordStore();
        store.Upsert(new PairingRecord(SessionPsk, PskCategory.LongTerm, FakeNoiseSession.FakeServerId));

        var (client, connection, session) = TestClient.Create(
            configure: options => options with
            {
                PairingRecordStore = store,
                Capabilities = capabilities,
                PairingCodeLockoutStore = new InMemoryPairingCodeLockoutStore(),
                PresentPairingCodeAsync = (_, _) => ValueTask.CompletedTask,
                PairingWindow = new PairingWindow(),
            });
        session.MatchedPsk = new NoisePsk(SessionPsk, PskCategory.LongTerm, FakeNoiseSession.FakeServerId);
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["playback","management"],"active_roles":[]}}""");
        return (client, connection);
    }

    [Fact]
    public void DeclaredLocations_AreAdvertisedOnTheirMethods()
    {
        var (client, connection) = CreateManagementClient(new ClientCapabilities
        {
            PairingCodeMethods = { "static_pin" },
            StaticPairingCode = "12345678",
            StaticPairingCodeLocations = { PairMethodLocations.Device, PairMethodLocations.Leaflet },
            PairingPskLocations = { PairMethodLocations.Device },
        });
        using var _c = client;

        Assert.Equal(
            new[] { "device", "leaflet" },
            Descriptor(connection, "static_pin")!.Locations);
        Assert.Equal(new[] { "device" }, Descriptor(connection, "pairing_psk")!.Locations);
    }

    /// <summary>
    /// The field is optional, and absence is how the spec says "no hint". An empty array would
    /// be a positive claim that the secret is findable nowhere.
    /// </summary>
    [Fact]
    public void UndeclaredLocations_OmitTheFieldRatherThanSendingAnEmptyArray()
    {
        var (client, connection) = CreateManagementClient(new ClientCapabilities
        {
            PairingCodeMethods = { "static_pin" },
            StaticPairingCode = "12345678",
        });
        using var _c = client;

        Assert.Null(Descriptor(connection, "static_pin")!.Locations);
        Assert.Null(Descriptor(connection, "pairing_psk")!.Locations);

        // Asserted on the wire too: a null List<string> still serializes as "locations":null
        // without the WhenWritingNull condition, which is a different thing from absence.
        string json = MessageSerializer.Serialize(
            connection.SentMessages.OfType<ClientHelloMessage>().Last());
        Assert.DoesNotContain("locations", json);

        // Positive control: the descriptors themselves did go out, so the absence above is
        // about the field and not about an empty method list.
        Assert.Contains("static_pin", json);
        Assert.Contains("pairing_psk", json);
    }

    /// <summary>The spec scopes the hint to static_pin and pairing_psk; dynamic_pin has no secret to find.</summary>
    [Fact]
    public void DynamicPairingCode_NeverCarriesALocationsHint()
    {
        var (client, connection) = CreateManagementClient(new ClientCapabilities
        {
            PairingCodeMethods = { "dynamic_pin" },
            StaticPairingCodeLocations = { PairMethodLocations.Device },
            PairingPskLocations = { PairMethodLocations.Device },
        });
        using var _c = client;

        var dynamicPairingCode = Descriptor(connection, "dynamic_pin");
        Assert.NotNull(dynamicPairingCode);
        Assert.Null(dynamicPairingCode.Locations);

        // Positive control: the hint machinery is live on this client, so the null above is
        // dynamic_pin being excluded rather than locations being switched off wholesale.
        Assert.Equal(new[] { "device" }, Descriptor(connection, "pairing_psk")!.Locations);
    }

    /// <summary>
    /// The spec's "when the secret is rotated, the client updates the hint accordingly".
    /// </summary>
    [Fact]
    public void ServerSettingTheStaticPairingCode_MovesTheHintToOperator()
    {
        var capabilities = new ClientCapabilities
        {
            PairingCodeMethods = { "static_pin" },
            StaticPairingCode = "12345678",
            StaticPairingCodeLocations = { PairMethodLocations.Device },
        };
        var (client, connection) = CreateManagementClient(capabilities);
        using var _c = client;
        var events = new List<PairingConfigChangedEventArgs>();
        client.PairingConfigChanged += (_, e) => events.Add(e);

        Assert.Equal(new[] { "device" }, Descriptor(connection, "static_pin")!.Locations);

        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"static_pin":{"pin":"87654321"}}}""");
        Assert.Equal("ok", LastResult(connection).Result);

        // The app is told, so it can persist the hint beside the pairing code it also has to persist.
        var change = Assert.Single(events);
        Assert.Equal(new[] { "operator" }, change.StaticPairingCodeLocations);
        Assert.Equal("87654321", change.StaticPairingCode);

        // And it is in effect: the next handshake advertises the new hint.
        connection.SimulateReconnected();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        Assert.Equal(new[] { "operator" }, Descriptor(connection, "static_pin")!.Locations);

        // The SDK does not write to the capabilities instance the app owns.
        Assert.Equal(new[] { "device" }, capabilities.StaticPairingCodeLocations);
    }

    [Fact]
    public void ServerSettingThePairingPsk_MovesTheHintToOperator()
    {
        var capabilities = new ClientCapabilities
        {
            PairingPskLocations = { PairMethodLocations.Leaflet },
        };
        var (client, connection) = CreateManagementClient(capabilities);
        using var _c = client;
        var events = new List<PairingConfigChangedEventArgs>();
        client.PairingConfigChanged += (_, e) => events.Add(e);

        byte[] newPsk = Enumerable.Repeat((byte)0x5A, 32).ToArray();
        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"pairing_psk":{"psk":"NEWPSK"}}}"""
                .Replace("NEWPSK", ToBase64Url(newPsk)));
        Assert.Equal("ok", LastResult(connection).Result);

        var change = Assert.Single(events);
        Assert.Equal(new[] { "operator" }, change.PairingPskLocations);
        Assert.Equal(new[] { "leaflet" }, capabilities.PairingPskLocations);
    }

    /// <summary>
    /// The negative half of the rule, and the one worth pinning: a PSK the <em>client</em>
    /// mints is still found wherever the app renders it, so rotating one must not claim the
    /// operator chose it. Without this, "the secret changed" and "the operator set the secret"
    /// collapse into the same trigger.
    /// </summary>
    [Fact]
    public void ClientRotatingItsOwnPairingPsk_LeavesTheHintAlone()
    {
        var (client, connection) = CreateManagementClient(new ClientCapabilities
        {
            PairingPskLocations = { PairMethodLocations.Device },
        });
        using var _c = client;

        client.RotatePairingPsk();

        connection.SimulateReconnected();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");

        Assert.Equal(new[] { "device" }, Descriptor(connection, "pairing_psk")!.Locations);
    }
}
