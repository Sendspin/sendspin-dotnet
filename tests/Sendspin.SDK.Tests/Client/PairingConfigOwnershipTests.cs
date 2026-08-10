using System.Text;
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
    /// writes to it. <paramref name="pinLockoutStore"/> is optional and only needed by
    /// tests that go on to drive a real PIN pairing attempt on this same client.
    /// </summary>
    private static (SendspinClientService Client, FakeSendspinConnection Connection, FakeNoiseSession Session, InMemoryPairingRecordStore Store)
        CreateManagementClient(ClientCapabilities capabilities, IPinLockoutStore? pinLockoutStore = null)
    {
        var store = new InMemoryPairingRecordStore();
        store.Upsert(new PairingRecord(SessionPsk, PskCategory.LongTerm, FakeNoiseSession.FakeServerId));

        // Pre-opened so a static_pin attempt (always gesture-gated now) proceeds immediately,
        // exactly as it did before gating existed -- the one test that drives a real static_pin
        // attempt through this helper is not about gating.
        var window = new PairingWindow();
        window.Open();
        var (client, connection, session) = TestClient.Create(
            configure: options =>
            {
                options.PairingRecordStore = store;
                options.Capabilities = capabilities;
                options.PinLockoutStore = pinLockoutStore;
                options.PairingWindow = window;
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

    [Fact]
    public void SetDynamicPinMinLength_TakesEffect_AndIsRangeChecked()
    {
        var capabilities = new ClientCapabilities { PinPairingMethods = { "dynamic_pin" }, MinPinLength = 6 };
        var (client, connection, _, _) = CreateManagementClient(capabilities);
        using var _c = client;
        var events = new List<PairingConfigChangedEventArgs>();
        client.PairingConfigChanged += (_, e) => events.Add(e);

        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"dynamic_pin":{"min_pin_length":9}}}""");
        Assert.Equal("ok", LastResult(connection).Result);
        Assert.Single(events);

        // The app's object is untouched; the effective value moved.
        Assert.Equal(6, capabilities.MinPinLength);
        connection.RaiseTextMessageReceived("""{"type":"management/get-pairing-config","payload":{}}""");
        Assert.Equal(9, LastResult(connection).Data!.Value
            .GetProperty("dynamic_pin").GetProperty("min_pin_length").GetInt32());

        // Out of the spec's 4-12 range is invalid, and changes nothing.
        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"dynamic_pin":{"min_pin_length":13}}}""");
        Assert.Equal("invalid", LastResult(connection).Result);
        connection.RaiseTextMessageReceived("""{"type":"management/get-pairing-config","payload":{}}""");
        Assert.Equal(9, LastResult(connection).Data!.Value
            .GetProperty("dynamic_pin").GetProperty("min_pin_length").GetInt32());
    }

    [Theory]
    [InlineData(4, "ok")]
    [InlineData(12, "ok")]
    [InlineData(3, "invalid")]
    [InlineData(13, "invalid")]
    public void SetDynamicPinMinLength_AcceptsTheFullSpecRange_AndRejectsJustOutsideIt(int value, string expected)
    {
        // SetDynamicPinMinLength_TakesEffect_AndIsRangeChecked only ever exercised 9 (valid)
        // and 13 (invalid), so an off-by-one in `value < 4 || value > 12` at either edge of
        // the spec's 4-12 range would pass unnoticed. Each boundary pair sits next to its
        // own positive control: 4 and 12 accepted right where 3 and 13 are rejected.
        var capabilities = new ClientCapabilities { PinPairingMethods = { "dynamic_pin" }, MinPinLength = 6 };
        var (client, connection, _, _) = CreateManagementClient(capabilities);
        using var _c = client;

        connection.RaiseTextMessageReceived(
            $$$$"""{"type":"management/set-pairing-config","payload":{"dynamic_pin":{"min_pin_length":{{{{value}}}}}}}""");
        Assert.Equal(expected, LastResult(connection).Result);
    }

    [Fact]
    public void EnablingStaticPin_WithNoPinConfiguredAndNoneSupplied_IsInvalid()
    {
        // management.md:98 names this case explicitly. Enabling a PIN method with no secret
        // behind it would advertise a method that can never authenticate anyone.
        var capabilities = new ClientCapabilities { PinPairingMethods = { "static_pin" } };
        var (client, connection, _, _) = CreateManagementClient(capabilities);
        using var _c = client;

        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"static_pin":{"enabled":true}}}""");
        Assert.Equal("invalid", LastResult(connection).Result);

        // Positive control: the same enable succeeds when a PIN comes with it.
        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"static_pin":{"enabled":true,"pin":"01234567"}}}""");
        Assert.Equal("ok", LastResult(connection).Result);
        Assert.Null(capabilities.StaticPin); // rotation did not touch the app's object
    }

    [Fact]
    public void RotatingStaticPin_RejectsAnythingButEightDigits()
    {
        var capabilities = new ClientCapabilities { PinPairingMethods = { "static_pin" }, StaticPin = "11111111" };
        var (client, connection, _, _) = CreateManagementClient(capabilities);
        using var _c = client;

        foreach (string bad in new[] { "1234567", "123456789", "1234567a", "" })
        {
            connection.RaiseTextMessageReceived(
                $$$$"""{"type":"management/set-pairing-config","payload":{"static_pin":{"pin":"{{{{bad}}}}"}}}""");
            Assert.Equal("invalid", LastResult(connection).Result);
        }

        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"static_pin":{"pin":"87654321"}}}""");
        Assert.Equal("ok", LastResult(connection).Result);
    }

    [Fact]
    public void RejectedStaticPinRotation_LeavesThePreviouslyConfiguredPinInForce()
    {
        // get-pairing-config never returns a configured secret (management.md:77), so unlike
        // the min_pin_length boundary test above, a get-pairing-config re-query cannot prove
        // this. The only observable effect of the stored PIN is whether it authenticates a
        // real pairing attempt, so that is what this drives: a rejected rotation must not
        // have replaced (or cleared) the PIN configured before it.
        var lockout = new InMemoryPinLockoutStore();
        var capabilities = new ClientCapabilities { PinPairingMethods = { "static_pin" }, StaticPin = "11111111" };
        var (client, connection, session, _) = CreateManagementClient(capabilities, lockout);
        using var _c = client;

        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"static_pin":{"pin":"1234567"}}}""");
        Assert.Equal("invalid", LastResult(connection).Result);

        // Re-pair on a fresh connection, still keyed by the client's existing long-term PSK
        // (admissible for a standalone 'pairing' activity), and run a real CPace exchange
        // against the ORIGINAL PIN. It must still verify.
        connection.ConnectAsync(ServerUri).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"pairing":{"method":"static_pin"}}}""");

        byte[] sid = PinPairing.BuildSid(session.HandshakeHash!.Value.Span, 1);
        var server = CPace.Start(CPaceRole.Initiator, Encoding.ASCII.GetBytes("11111111"), sid, ad: PinPairing.AdServer);
        connection.RaiseTextMessageReceived(
            $$$"""{"type":"server/pair-auth","payload":{"pake_msg_1":"{{{ToBase64Url(server.PublicShare)}}}"}}""");
        var auth = connection.SentMessages.OfType<ClientPairAuthMessage>().Last();
        server.Derive(Base64UrlText.Decode(auth.Payload.PakeMsg2), PinPairing.AdClient);
        connection.RaiseTextMessageReceived(
            $$$"""{"type":"server/pair-confirm","payload":{"server_kc":"{{{ToBase64Url(server.Tag())}}}"}}""");

        var confirm = connection.SentMessages.OfType<ClientPairConfirmMessage>().Last();
        Assert.True(server.Verify(Base64UrlText.Decode(confirm.Payload.ClientKc)));
    }

    [Fact]
    public void DisabledDynamicPin_IsOmittedFromTheHelloAdvertisement()
    {
        // messaging.md:194 — "An implemented method that is disabled is omitted." This is the
        // end-to-end proof that Task 1's effective state is what the advertisement reads:
        // a set-pairing-config change reaches client/hello without touching capabilities.
        var capabilities = new ClientCapabilities { PinPairingMethods = { "dynamic_pin" } };
        var (client, connection, _, _) = CreateManagementClient(capabilities);
        using var _c = client;

        // Positive control first: while enabled, the method is advertised.
        Assert.Contains(
            connection.SentMessages.OfType<ClientHelloMessage>().Last().Payload.SupportedPairMethods!,
            m => m.Method == "dynamic_pin");

        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"dynamic_pin":{"enabled":false}}}""");
        Assert.Equal("ok", LastResult(connection).Result);

        // Reconnect so a fresh client/hello is built from the effective state.
        connection.ConnectAsync(ServerUri).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");

        var afterDisable = connection.SentMessages.OfType<ClientHelloMessage>().Last()
            .Payload.SupportedPairMethods!.Select(m => m.Method).ToList();
        Assert.DoesNotContain("dynamic_pin", afterDisable);
        Assert.Contains("pairing_psk", afterDisable); // the mandatory method survives
        Assert.Equal(["dynamic_pin"], capabilities.PinPairingMethods); // app's object untouched
    }

    [Fact]
    public void SetFieldsOnAnUnimplementedMethod_IsInvalid()
    {
        // The one case the old blanket rejection got right, kept as a control so the fix
        // cannot over-correct into accepting configuration for a method that cannot run.
        var capabilities = new ClientCapabilities(); // no PIN methods
        var (client, connection, _, _) = CreateManagementClient(capabilities);
        using var _c = client;

        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"dynamic_pin":{"enabled":true}}}""");

        Assert.Equal("invalid", LastResult(connection).Result);
    }

    [Fact]
    public void DisabledDynamicPin_IsStillReportedByGetPairingConfig_WithEnabledFalse()
    {
        // A disabled method is still an implemented one: absence in get-pairing-config
        // means "this client cannot do it at all", which would be a different and
        // wrong answer. Only client/hello omits a disabled method.
        var capabilities = new ClientCapabilities { PinPairingMethods = { "dynamic_pin" } };
        var (client, connection, _, _) = CreateManagementClient(capabilities);
        using var _c = client;

        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"dynamic_pin":{"enabled":false}}}""");
        Assert.Equal("ok", LastResult(connection).Result);

        connection.RaiseTextMessageReceived("""{"type":"management/get-pairing-config","payload":{}}""");
        var data = LastResult(connection).Data!.Value;

        Assert.True(data.TryGetProperty("dynamic_pin", out var dynamicPin),
            "a disabled method is still implemented and must still be reported");
        Assert.False(dynamicPin.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void DisablingPairingPsk_StopsOfferingTheMethod()
    {
        // pairing.md:67 — the client keeps its Pairing PSK among handshake candidates
        // "whenever the method is enabled", so disabling it must withdraw both the
        // advertisement and the client's willingness to run the flow.
        var (client, connection, _, _) = CreateManagementClient(new ClientCapabilities());
        using var _c = client;

        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"pairing_psk":{"enabled":false}}}""");
        Assert.Equal("ok", LastResult(connection).Result);

        connection.RaiseTextMessageReceived("""{"type":"management/get-pairing-config","payload":{}}""");
        Assert.False(LastResult(connection).Data!.Value
            .GetProperty("pairing_psk").GetProperty("enabled").GetBoolean());

        connection.ConnectAsync(ServerUri).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        Assert.DoesNotContain(
            connection.SentMessages.OfType<ClientHelloMessage>().Last().Payload.SupportedPairMethods!,
            m => m.Method == "pairing_psk");

        // Positive control: re-enabling brings it back, so the test is not passing on a
        // client that simply never advertises the method.
        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"pairing_psk":{"enabled":true}}}""");
        connection.ConnectAsync(ServerUri).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        Assert.Contains(
            connection.SentMessages.OfType<ClientHelloMessage>().Last().Payload.SupportedPairMethods!,
            m => m.Method == "pairing_psk");
    }

    [Fact]
    public void SetPairingPskEnabled_RaisesEventExactlyOnce_AndNotOnANoOpSet()
    {
        // A prior review flagged a specific hazard in this handler: each config section
        // needs its changed-flag folded into the final event condition by hand, and it is
        // easy to add a new section and forget that third touch point, silently
        // suppressing PairingConfigChanged with nothing failing. This pins pairing_psk's
        // enabled flag against exactly that mistake.
        var capabilities = new ClientCapabilities();
        var (client, connection, _, _) = CreateManagementClient(capabilities);
        using var _c = client;
        var events = new List<PairingConfigChangedEventArgs>();
        client.PairingConfigChanged += (_, e) => events.Add(e);

        // No-op: pairing_psk.enabled already defaults to true, so re-asserting true
        // changes nothing and must not raise.
        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"pairing_psk":{"enabled":true}}}""");
        Assert.Equal("ok", LastResult(connection).Result);
        Assert.Empty(events);

        // A real change raises exactly once.
        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"pairing_psk":{"enabled":false}}}""");
        Assert.Equal("ok", LastResult(connection).Result);
        Assert.Single(events);

        // Positive control: re-enabling raises again, proving the single event above
        // reflects the flip and is not dead event-wiring that fires once regardless of
        // what changed.
        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"pairing_psk":{"enabled":true}}}""");
        Assert.Equal("ok", LastResult(connection).Result);
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void SetStaticPinEnabled_RaisesEventExactlyOnce_AndNotOnANoOpSet()
    {
        // static_pin's changed-flag guards the same final event condition as every other
        // section, but had no independent single-section test: the only existing test that
        // sends static_pin also sends record_mode in the same request, so recordModeChanged
        // alone was enough to keep that test's Assert.Single(events) green. This isolates
        // static_pin the way SetPairingPskEnabled_RaisesEventExactlyOnce_... does for
        // pairing_psk.
        var capabilities = new ClientCapabilities { PinPairingMethods = { "static_pin" }, StaticPin = "11111111" };
        var (client, connection, _, _) = CreateManagementClient(capabilities);
        using var _c = client;
        var events = new List<PairingConfigChangedEventArgs>();
        client.PairingConfigChanged += (_, e) => events.Add(e);

        // No-op: static_pin.enabled already defaults to true, so re-asserting true changes
        // nothing and must not raise.
        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"static_pin":{"enabled":true}}}""");
        Assert.Equal("ok", LastResult(connection).Result);
        Assert.Empty(events);

        // A real change raises exactly once.
        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"static_pin":{"enabled":false}}}""");
        Assert.Equal("ok", LastResult(connection).Result);
        Assert.Single(events);

        // Positive control: re-enabling raises again, proving the single event above
        // reflects the flip and is not dead event-wiring that fires once regardless of
        // what changed.
        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"static_pin":{"enabled":true}}}""");
        Assert.Equal("ok", LastResult(connection).Result);
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void RotatingThePairingPsk_ToAPskIdInAnotherCategory_IsAlreadyExists()
    {
        // management.md:98 — a rotation that collides with the Sentinel PSK or a stored
        // record would make one psk_id resolve to two different trust levels.
        var (client, connection, _, store) = CreateManagementClient(new ClientCapabilities());
        using var _c = client;

        // SessionPsk is already stored as this connection's LongTerm record.
        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"pairing_psk":{"psk":"PSK"}}}"""
                .Replace("PSK", ToBase64Url(SessionPsk)));
        Assert.Equal("already_exists", LastResult(connection).Result);

        // And nothing was written: the Pairing record is still absent.
        Assert.DoesNotContain(store.List(), r => r.Category == PskCategory.Pairing);

        // Positive control: an unused PSK rotates successfully.
        var fresh = Enumerable.Repeat((byte)9, 32).ToArray();
        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"pairing_psk":{"psk":"PSK"}}}"""
                .Replace("PSK", ToBase64Url(fresh)));
        Assert.Equal("ok", LastResult(connection).Result);
        Assert.Contains(store.List(), r => r.Category == PskCategory.Pairing);
    }

    [Fact]
    public void PairingConfigChanged_CarriesEveryEffectiveValue_SoTheAppCanPersistThem()
    {
        // The SDK holds effective config in memory only; without the values on the event the
        // app cannot persist them and every restart silently reverts a server's changes.
        var capabilities = new ClientCapabilities { PinPairingMethods = { "dynamic_pin" } };
        var (client, connection, _, _) = CreateManagementClient(capabilities);
        using var _c = client;
        var events = new List<PairingConfigChangedEventArgs>();
        client.PairingConfigChanged += (_, e) => events.Add(e);

        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"dynamic_pin":{"enabled":false,"min_pin_length":10}}}""");

        var change = Assert.Single(events);
        Assert.False(change.DynamicPinEnabled);
        Assert.Equal(10, change.MinPinLength);
        Assert.True(change.PairingPskEnabled); // untouched fields still report their current value
    }

    [Fact]
    public void PairingConfigChanged_CarriesStaticPinAndRecordModeDistinctly_SoASwapBetweenThemWouldFail()
    {
        // StaticPin and RecordModePskId sit on adjacent lines in CurrentPairingConfig and are
        // both string?; a copy-paste swap between them would compile clean and pass every
        // other test in the suite. Both are asserted here against different values a swap
        // would visibly cross, so a swap fails this test instead of shipping silently.
        var capabilities = new ClientCapabilities { PinPairingMethods = { "static_pin" } };
        var (client, connection, _, store) = CreateManagementClient(capabilities);
        using var _c = client;

        var shared = Enumerable.Repeat((byte)8, 32).ToArray();
        store.Upsert(new PairingRecord(shared, PskCategory.LongTerm)); // no ServerId => shared-PSK record
        string sharedId = NoiseConstants.DerivePskId(shared);
        var events = new List<PairingConfigChangedEventArgs>();
        client.PairingConfigChanged += (_, e) => events.Add(e);

        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"static_pin":{"enabled":true,"pin":"13579246"},"record_mode":{"psk_id":"PSKID"}}}"""
                .Replace("PSKID", sharedId));
        Assert.Equal("ok", LastResult(connection).Result);

        var change = Assert.Single(events);
        Assert.True(change.StaticPinEnabled);
        Assert.Equal("13579246", change.StaticPin);
        Assert.Equal(sharedId, change.RecordModePskId);
    }

    [Fact]
    public void RotatingThePairingPsk_ToItsOwnCurrentValue_Succeeds()
    {
        // The naive "does any record already have this psk_id?" check would find the
        // Pairing record itself and reject a same-value re-rotation as a collision. The
        // category exclusion (a stored record only collides when it is NOT the Pairing
        // record) exists precisely so this is a no-op, not a conflict.
        var (client, connection, _, store) = CreateManagementClient(new ClientCapabilities());
        using var _c = client;

        var pairingPsk = Enumerable.Repeat((byte)3, 32).ToArray();
        string request = """{"type":"management/set-pairing-config","payload":{"pairing_psk":{"psk":"PSK"}}}"""
            .Replace("PSK", ToBase64Url(pairingPsk));
        connection.RaiseTextMessageReceived(request);
        Assert.Equal("ok", LastResult(connection).Result);

        // Re-rotate to the exact same value already held by the Pairing record.
        connection.RaiseTextMessageReceived(request);
        Assert.Equal("ok", LastResult(connection).Result);
        Assert.Contains(
            store.List(),
            r => r.Category == PskCategory.Pairing && r.Psk.ToArray().SequenceEqual(pairingPsk));
    }
}
