using System.Text.Json;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Connection.Noise.Pairing;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// A server can disable a pairing method with management/set-pairing-config, and the SDK
/// tells the app to persist the result. Before #131 four of the seven values it reports had
/// no ClientCapabilities property to reapply, so the change reverted on the next start —
/// most sharply for pairing_psk, which was hardcoded back on.
/// </summary>
public class PairingConfigSeedingTests
{
    private static readonly byte[] SessionPsk = Enumerable.Repeat((byte)7, 32).ToArray();

    /// <summary>
    /// A client that has answered a server/hello, which is what makes it build a
    /// client/hello from the seeded effective state. TestClient.Create dials the fake but
    /// sends nothing on its own — client/hello is a reply to server/hello
    /// (SendSpinClient.cs:1327), so without this the advertisement never exists.
    /// </summary>
    private static (SendspinClientService Client, FakeSendspinConnection Connection)
        CreateAndGreet(ClientCapabilities capabilities, IPairingRecordStore? store = null)
    {
        var (client, connection, session) = TestClient.Create(
            configure: o => o with
            {
                Capabilities = capabilities,
                PairingRecordStore = store ?? new InMemoryPairingRecordStore(),
                // Present so a PIN method is never withheld for want of a lockout store,
                // which would confound "withheld because disabled".
                PinLockoutStore = new InMemoryPinLockoutStore(),
            });
        session.MatchedPsk = new NoisePsk(SessionPsk, PskCategory.LongTerm, FakeNoiseSession.FakeServerId);
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        return (client, connection);
    }

    private static List<string> HelloPairMethods(FakeSendspinConnection connection) =>
        connection.SentMessages.OfType<ClientHelloMessage>().Last()
            .Payload.SupportedPairMethods?.Select(m => m.Method).ToList() ?? [];

    /// <summary>
    /// Activates the connection for management. Management requests are scoped to
    /// connections whose activities include 'management', so any test driving
    /// management/* messages needs this first.
    /// </summary>
    private static void ActivateManagement(FakeSendspinConnection connection) =>
        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["management"],"active_roles":[]}}""");

    /// <summary>
    /// Drives a management-activated session and returns the full get-pairing-config data.
    /// </summary>
    private static JsonElement GetPairingConfigData(FakeSendspinConnection connection)
    {
        ActivateManagement(connection);
        connection.RaiseTextMessageReceived(
            """{"type":"management/get-pairing-config","payload":{}}""");

        var result = connection.SentMessages.OfType<ManagementResultMessage>().Last().Payload;
        Assert.Equal("ok", result.Result);
        return result.Data!.Value;
    }

    private static JsonElement RecordModeFromGetPairingConfig(FakeSendspinConnection connection) =>
        GetPairingConfigData(connection).GetProperty("record_mode");

    [Fact]
    public void PairingPskEnabledFalse_IsNotAdvertised()
    {
        // #131's security-shaped case: a server turned pairing_psk off, the app persisted
        // that, and the method must not come back on restart.
        var (client, connection) = CreateAndGreet(
            new ClientCapabilities { PairingPskEnabled = false });
        using var _c = client;

        Assert.DoesNotContain("pairing_psk", HelloPairMethods(connection));
    }

    [Fact]
    public void PairingPskEnabledDefault_IsAdvertised()
    {
        // Positive control: without it, a bug that advertises nothing at all would pass the
        // test above.
        var (client, connection) = CreateAndGreet(new ClientCapabilities());
        using var _c = client;

        Assert.Contains("pairing_psk", HelloPairMethods(connection));
    }

    [Theory]
    [InlineData("dynamic_pin")]
    [InlineData("static_pin")]
    public void PinMethodDisabled_IsImplementedButNotAdvertised(string method)
    {
        // Disabled is not unimplemented: the method stays in PinPairingMethods so a server
        // can turn it back on, but client/hello must not offer it — while get-pairing-config
        // must still report the method's object, with enabled: false, so a management server
        // sees "implemented but disabled" rather than "not implemented" (the two the AND in
        // the seeding block exists to keep distinct).
        var capabilities = new ClientCapabilities
        {
            PinPairingMethods = { method },
            StaticPin = "12345678",
            DynamicPinEnabled = method != "dynamic_pin",
            StaticPinEnabled = method != "static_pin",
        };
        var (client, connection) = CreateAndGreet(capabilities);
        using var _c = client;

        Assert.DoesNotContain(method, HelloPairMethods(connection));

        var data = GetPairingConfigData(connection);
        Assert.True(data.TryGetProperty(method, out var methodState),
            "a disabled method is still implemented and must still be reported");
        Assert.False(methodState.GetProperty("enabled").GetBoolean());
    }

    [Theory]
    [InlineData("dynamic_pin")]
    [InlineData("static_pin")]
    public void PinMethodListedAndEnabledByDefault_IsAdvertised(string method)
    {
        // The defaults must reproduce pre-#131 behaviour: listing a method is still enough
        // to offer it, with no new property to set.
        var capabilities = new ClientCapabilities
        {
            PinPairingMethods = { method },
            StaticPin = "12345678",
        };
        var (client, connection) = CreateAndGreet(capabilities);
        using var _c = client;

        Assert.Contains(method, HelloPairMethods(connection));
    }

    [Fact]
    public void PinMethodEnabledFlag_WithoutTheMethodImplemented_StaysDisabled()
    {
        // The flags default true, so they must be ANDed with PinPairingMethods. client/hello
        // omitting the method is not proof of that AND by itself: BuildPairMethods gates on
        // IsMethodImplemented independently, so it omits an unlisted method whether or not
        // the AND exists. The AND is only observable on PairingConfigChanged, where
        // CurrentPairingConfig reports _dynamicPinEnabled/_staticPinEnabled raw with no
        // implemented-gate — the very event apps are told to persist — so that path is
        // asserted here too.
        var (client, connection) = CreateAndGreet(new ClientCapabilities());
        using var _c = client;

        Assert.DoesNotContain("dynamic_pin", HelloPairMethods(connection));
        Assert.DoesNotContain("static_pin", HelloPairMethods(connection));

        var events = new List<PairingConfigChangedEventArgs>();
        client.PairingConfigChanged += (_, e) => events.Add(e);
        ActivateManagement(connection);
        // unpaired_access is unrelated to the PIN flags; it exists only to make the event
        // fire so the raw effective state can be observed.
        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"unpaired_access":{"enabled":true}}}""");

        var change = Assert.Single(events);
        Assert.False(change.DynamicPinEnabled);
        Assert.False(change.StaticPinEnabled);
    }

    [Fact]
    public void RecordModePskId_NamingNoStoredRecord_SeedsNull()
    {
        // A server can remove that record with management/remove-record while the app is
        // down. Reporting a psk_id no record backs tells the next server a fallback exists
        // that cannot be used.
        var store = new InMemoryPairingRecordStore();
        store.Upsert(new PairingRecord(SessionPsk, PskCategory.LongTerm, FakeNoiseSession.FakeServerId));

        var (client, connection) = CreateAndGreet(
            new ClientCapabilities { RecordModePskId = "not-a-stored-record" },
            store);
        using var _c = client;

        // RecordModeState.PskId carries JsonIgnore(WhenWritingNull) (ManagementData.cs:49-50,
        // predating this change), so an unset fallback omits the key rather than writing
        // psk_id: null.
        Assert.False(RecordModeFromGetPairingConfig(connection).TryGetProperty("psk_id", out _));
    }

    [Fact]
    public void RecordModePskId_NamingASharedPskRecord_IsSeeded()
    {
        var store = new InMemoryPairingRecordStore();
        store.Upsert(new PairingRecord(SessionPsk, PskCategory.LongTerm, FakeNoiseSession.FakeServerId));
        // A shared-PSK record is long-term with no bound server_id (management.md:111).
        // PairingRecord derives PskId from the PSK, so it cannot be chosen.
        var shared = new PairingRecord(Enumerable.Repeat((byte)3, 32).ToArray(), PskCategory.LongTerm);
        store.Upsert(shared);

        var (client, connection) = CreateAndGreet(
            new ClientCapabilities { RecordModePskId = shared.PskId },
            store);
        using var _c = client;

        Assert.Equal(shared.PskId, RecordModeFromGetPairingConfig(connection)
            .GetProperty("psk_id").GetString());
    }
}
