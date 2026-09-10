using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Connection.Noise.Pairing;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Pairing configuration is local and manufacturer-defined: <see cref="ClientCapabilities"/> is
/// the only thing that decides which pairing methods a client offers, and the effective state it
/// seeds is what <c>client/hello</c> advertises and what a pairing activation is judged against.
/// </summary>
public class PairingConfigSeedingTests
{
    private static readonly byte[] SessionPsk = Enumerable.Repeat((byte)7, 32).ToArray();

    /// <summary>
    /// A client that has answered a server/hello, which is what makes it build a
    /// client/hello from the seeded effective state. TestClient.Create dials the fake but
    /// sends nothing on its own — client/hello is a reply to server/hello — so without this
    /// the advertisement never exists.
    /// </summary>
    /// <param name="hasPairingCodeLockoutStore">
    /// Present by default so a pairing code method is never withheld for want of a lockout store,
    /// which would confound "withheld because disabled". Pass false to test the
    /// missing-store case.
    /// </param>
    /// <param name="hasPresentPairingCodeAsync">
    /// Present by default so dynamic_pairing_code is never withheld for want of a presenter, which
    /// would confound "withheld because disabled". Pass false to test the
    /// missing-presenter case.
    /// </param>
    /// <param name="hasPairingRecordStore">
    /// Present by default for the same reason. Pass false to test the missing-record-store
    /// case (#158); it overrides <paramref name="store"/>.
    /// </param>
    /// <remarks>
    /// The session is Sentinel-keyed: since spec #183 a long-term (paired) PSK admits no
    /// pairing activity at all, so the activation tests here would be refused before the
    /// method check they are about ever ran.
    /// </remarks>
    private static (SendspinClientService Client, FakeSendspinConnection Connection)
        CreateAndGreet(
            ClientCapabilities capabilities,
            IPairingRecordStore? store = null,
            PairingWindow? window = null,
            bool hasPairingCodeLockoutStore = true,
            bool hasPresentPairingCodeAsync = true,
            bool hasPairingRecordStore = true)
    {
        var (client, connection, session) = TestClient.Create(
            PskCategory.Sentinel,
            configure: o => o with
            {
                Capabilities = capabilities,
                PairingRecordStore = hasPairingRecordStore ? store ?? new InMemoryPairingRecordStore() : null,
                PairingCodeLockoutStore = hasPairingCodeLockoutStore ? new InMemoryPairingCodeLockoutStore() : null,
                PresentPairingCodeAsync = hasPresentPairingCodeAsync ? (_, _) => ValueTask.CompletedTask : null,
                PairingWindow = window,
            });
        session.MatchedPsk = new NoisePsk(SessionPsk, PskCategory.Sentinel, FakeNoiseSession.FakeServerId);
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        return (client, connection);
    }

    private static Dictionary<string, PairMethodDescriptor> HelloPairMethodDescriptors(
        FakeSendspinConnection connection) =>
        connection.SentMessages.OfType<ClientHelloMessage>().Last()
            .Payload.SupportedPairMethods ?? [];

    private static List<string> HelloPairMethods(FakeSendspinConnection connection) =>
        HelloPairMethodDescriptors(connection).Keys.ToList();

    [Fact]
    public void PairingPskEnabledFalse_IsNotAdvertised()
    {
        // #131's security-shaped case: an app turned pairing_psk off, and the method must not
        // come back on restart.
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
    [InlineData("dynamic_pairing_code")]
    [InlineData("static_pairing_code")]
    public void PairingCodeMethodDisabled_IsNotAdvertised(string method)
    {
        // Disabled is not unimplemented: the method stays in PairingCodeMethods, so the app can
        // turn it back on without shipping a different build, but client/hello must not offer it.
        var capabilities = new ClientCapabilities
        {
            PairingCodeMethods = { method },
            StaticPairingCode = "12345678",
            DynamicPairingCodeEnabled = method != "dynamic_pairing_code",
            StaticPairingCodeEnabled = method != "static_pairing_code",
        };
        var (client, connection) = CreateAndGreet(capabilities);
        using var _c = client;

        Assert.DoesNotContain(method, HelloPairMethods(connection));
    }

    [Theory]
    [InlineData("dynamic_pairing_code")]
    [InlineData("static_pairing_code")]
    public void PairingCodeMethodListedAndEnabledByDefault_IsAdvertised(string method)
    {
        // The defaults must reproduce pre-#131 behaviour: listing a method is still enough
        // to offer it, with no new property to set.
        var capabilities = new ClientCapabilities
        {
            PairingCodeMethods = { method },
            StaticPairingCode = "12345678",
        };
        var (client, connection) = CreateAndGreet(capabilities);
        using var _c = client;

        Assert.Contains(method, HelloPairMethods(connection));
    }

    [Fact]
    public void PairingCodeMethodEnabledFlag_WithoutTheMethodImplemented_IsNotAdvertised()
    {
        // The flags default true, so they must be ANDed with PairingCodeMethods: a client that
        // implements neither pairing code method must advertise neither, whatever the flags say.
        var (client, connection) = CreateAndGreet(new ClientCapabilities());
        using var _c = client;

        Assert.DoesNotContain("dynamic_pairing_code", HelloPairMethods(connection));
        Assert.DoesNotContain("static_pairing_code", HelloPairMethods(connection));
    }

    [Fact]
    public void StaticPairingCodeWithNullPairingCode_IsNotAdvertised()
    {
        // Construction validated nothing before this fix: an app could list static_pairing_code with
        // no pairing code behind it, and CPace would run with an empty password.
        var capabilities = new ClientCapabilities { PairingCodeMethods = { "static_pairing_code" }, StaticPairingCode = null };
        var (client, connection) = CreateAndGreet(capabilities);
        using var _c = client;

        Assert.DoesNotContain("static_pairing_code", HelloPairMethods(connection));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1234567")] // 7 digits
    public void StaticPairingCodeWithMalformedPairingCode_IsNotAdvertised(string pin)
    {
        var capabilities = new ClientCapabilities { PairingCodeMethods = { "static_pairing_code" }, StaticPairingCode = pin };
        var (client, connection) = CreateAndGreet(capabilities);
        using var _c = client;

        Assert.DoesNotContain("static_pairing_code", HelloPairMethods(connection));
    }

    [Fact]
    public void StaticPairingCodeWithNoUsablePairingCode_ServerActivateAborts_MethodNotSupported()
    {
        // A server that asks for static_pairing_code anyway (e.g. from a stale advertisement) must be
        // refused rather than let CPace run with an empty password, and the connection must
        // stay open so the server can retry with another method.
        var capabilities = new ClientCapabilities { PairingCodeMethods = { "static_pairing_code" }, StaticPairingCode = null };
        var (client, connection) = CreateAndGreet(capabilities);
        using var _c = client;

        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"pairing":{"method":"static_pairing_code"}}}""");

        var abort = Assert.Single(connection.SentMessages.OfType<PairAbortMessage>());
        Assert.Equal("method_not_supported", abort.Payload.Reason);
        Assert.Equal(ConnectionState.Connected, connection.State);
    }

    [Fact]
    public void DynamicPairingCodeDescriptor_CarriesTheDigitsFormat_AndNoLegacyLength()
    {
        // Spec #179: the descriptor is keyed by method, and a dynamic_pairing_code descriptor
        // whose formats name nothing the server recognizes counts as no offer at all — so the
        // formats list is what makes the offer real. min_pin_length is gone with the
        // negotiable length.
        var capabilities = new ClientCapabilities { PairingCodeMethods = { "dynamic_pairing_code" } };
        var (client, connection) = CreateAndGreet(capabilities);
        using var _c = client;

        var descriptor = HelloPairMethodDescriptors(connection)["dynamic_pairing_code"];
        Assert.Equal(["digits"], descriptor.Formats);
        Assert.Equal(["display"], descriptor.OutChannels);
        Assert.Null(descriptor.Locations);
    }

    [Fact]
    public void SpeakerOutChannel_IsWithheld_BecauseDigitAudioIsNotImplemented()
    {
        // Advertising 'speaker' obliges the client to advertise a digit_audio object and play
        // the server's digit audio pack. This SDK does neither, so the channel is dropped
        // rather than inviting a server to pick a flow that reaches nobody.
        var capabilities = new ClientCapabilities
        {
            PairingCodeMethods = { "dynamic_pairing_code" },
            PairingCodeOutChannels = { "speaker" },
        };
        var (client, connection) = CreateAndGreet(capabilities);
        using var _c = client;

        var descriptor = HelloPairMethodDescriptors(connection)["dynamic_pairing_code"];
        Assert.Equal(["display"], descriptor.OutChannels);
    }

    [Fact]
    public void StaticPairingCodeWithValidPairingCode_IsAdvertised()
    {
        // Positive control: without it, a bug that disables static_pairing_code unconditionally would
        // pass every test above.
        var capabilities = new ClientCapabilities { PairingCodeMethods = { "static_pairing_code" }, StaticPairingCode = "12345678" };
        var (client, connection) = CreateAndGreet(capabilities);
        using var _c = client;

        Assert.Contains("static_pairing_code", HelloPairMethods(connection));
    }

    // --- #132 item 1: a pairing code method must not be advertised on a client missing a
    // dependency CanRun requires but BuildPairMethods did not previously check
    // (IPairingCodeLockoutStore, PresentPairingCodeAsync). ---

    [Fact]
    public void DynamicPairingCodeListedAndEnabled_NoPresentPairingCodeAsync_IsNotAdvertised()
    {
        // Without a presenter, a derived pairing code would reach nobody. CanOffer already refused
        // this; CanRun must also keep it out of client/hello.
        var capabilities = new ClientCapabilities { PairingCodeMethods = { "dynamic_pairing_code" } };
        var (client, connection) = CreateAndGreet(capabilities, hasPresentPairingCodeAsync: false);
        using var _c = client;

        Assert.DoesNotContain("dynamic_pairing_code", HelloPairMethods(connection));
    }

    [Fact]
    public void DynamicPairingCodeListedAndEnabled_NoPairingCodeLockoutStore_IsNotAdvertised()
    {
        // Without a lockout store, the failure counter can't persist, so the method could
        // never escalate to gesture-gating.
        var capabilities = new ClientCapabilities { PairingCodeMethods = { "dynamic_pairing_code" } };
        var (client, connection) = CreateAndGreet(capabilities, hasPairingCodeLockoutStore: false);
        using var _c = client;

        Assert.DoesNotContain("dynamic_pairing_code", HelloPairMethods(connection));
    }

    [Fact]
    public void StaticPairingCodeWithValidPairingCode_NoPairingCodeLockoutStore_IsNotAdvertised()
    {
        // static_pairing_code needs a lockout store for the same reason dynamic_pairing_code does, even
        // though its pairing code itself is valid.
        var capabilities = new ClientCapabilities { PairingCodeMethods = { "static_pairing_code" }, StaticPairingCode = "12345678" };
        var (client, connection) = CreateAndGreet(capabilities, hasPairingCodeLockoutStore: false);
        using var _c = client;

        Assert.DoesNotContain("static_pairing_code", HelloPairMethods(connection));
    }

    // --- #158: a pairing code method needs an IPairingRecordStore for the same reason pairing_psk
    // does. Without one the exchange completes, the server writes a long-term record, and
    // this client stores nothing. ---

    [Theory]
    [InlineData("dynamic_pairing_code")]
    [InlineData("static_pairing_code")]
    public void PairingCodeMethod_WithNoPairingRecordStore_IsNotAdvertised(string method)
    {
        var capabilities = new ClientCapabilities
        {
            PairingCodeMethods = { method },
            StaticPairingCode = "12345678",
        };
        var (client, connection) = CreateAndGreet(capabilities, hasPairingRecordStore: false);
        using var _c = client;

        Assert.DoesNotContain(method, HelloPairMethods(connection));
    }

    [Theory]
    [InlineData("dynamic_pairing_code")]
    [InlineData("static_pairing_code")]
    public void PairingCodeMethod_WithAPairingRecordStore_IsAdvertised(string method)
    {
        // Positive control for the pair above. Without it, a change that stopped advertising
        // pairing code methods altogether would pass every assertion in this block.
        var capabilities = new ClientCapabilities
        {
            PairingCodeMethods = { method },
            StaticPairingCode = "12345678",
        };
        var (client, connection) = CreateAndGreet(capabilities);
        using var _c = client;

        Assert.Contains(method, HelloPairMethods(connection));
    }

    [Theory]
    [InlineData("dynamic_pairing_code")]
    [InlineData("static_pairing_code")]
    public void PairingCodeMethod_WithNoPairingRecordStore_ServerActivateAborts_AndNeverClaimsPairing(string method)
    {
        var capabilities = new ClientCapabilities
        {
            PairingCodeMethods = { method },
            StaticPairingCode = "12345678",
        };
        var (client, connection) = CreateAndGreet(capabilities, hasPairingRecordStore: false);
        using var _c = client;

        bool paired = false;
        client.PairingCompleted += (_, _) => paired = true;

        connection.RaiseTextMessageReceived(
            $$$$"""{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"pairing":{"method":"{{{{method}}}}"}}}""");

        var abort = Assert.Single(connection.SentMessages.OfType<PairAbortMessage>());
        Assert.Equal("method_not_supported", abort.Payload.Reason);

        // Spec #123: the abort must leave the connection open so the server can retry with
        // another method.
        Assert.Equal(ConnectionState.Connected, connection.State);

        // A server that finalizes anyway — it never received a client/pair-finalize, but the
        // connection is deliberately still open — must not make this client claim a pairing it
        // has no record for. Mirrors PairingPsk_WithNoRecordStore_AbortsAndDoesNotClaimSuccess.
        connection.RaiseTextMessageReceived("""{"type":"server/pair-finalize","payload":{}}""");

        Assert.False(paired, "PairingCompleted must not fire when the record cannot be persisted");
    }

    [Fact]
    public void DynamicPairingCodeUnrunnableForMissingStore_ServerActivateAborts_MethodNotSupported()
    {
        // Behaviour that was already correct via CanOffer: pin it so generalising to
        // CanRun does not alter it. The server may retry with another method.
        var capabilities = new ClientCapabilities { PairingCodeMethods = { "dynamic_pairing_code" } };
        var (client, connection) = CreateAndGreet(capabilities, hasPairingCodeLockoutStore: false);
        using var _c = client;

        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"pairing":{"method":"dynamic_pairing_code","format":"digits"}}}""");

        var abort = Assert.Single(connection.SentMessages.OfType<PairAbortMessage>());
        Assert.Equal("method_not_supported", abort.Payload.Reason);
        Assert.Equal(ConnectionState.Connected, connection.State);
    }

    [Fact]
    public void BothPairingCodeMethods_AreRejectedAtConstruction()
    {
        // Spec #189: a client may implement at most one pairing-code method. The server-side
        // rule ("disregard static_pairing_code if both arrive") is a safety net for a
        // non-conformant peer, not licence to emit both — and picking one for the app would
        // silently ship whichever the SDK guessed, from two deliberate configuration entries.
        var capabilities = new ClientCapabilities
        {
            PairingCodeMethods = { "dynamic_pairing_code", "static_pairing_code" },
            StaticPairingCode = "12345678",
        };

        var ex = Assert.Throws<ArgumentException>(() =>
        {
            var (client, _) = CreateAndGreet(capabilities);
            client.Dispose();
        });

        Assert.Equal("PairingCodeMethods", ex.ParamName);
        Assert.Contains("at most one", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BothPairingCodeMethods_AreRejectedByTheHost_BeforeAnyServerConnects()
    {
        // The per-connection client is built lazily, so without this the app would only learn
        // its offer is non-conformant the first time a server dialled in — long after the
        // stack trace stopped naming the code that set it up.
        var options = new SendspinClientOptions
        {
            Identity = SendspinIdentity.Generate(),
            Capabilities = new ClientCapabilities
            {
                PairingCodeMethods = { "dynamic_pairing_code", "static_pairing_code" },
                StaticPairingCode = "12345678",
            },
        };

        var ex = Assert.Throws<ArgumentException>(() =>
            _ = new SendspinHostService(NullLoggerFactory.Instance, options));

        Assert.Equal("PairingCodeMethods", ex.ParamName);
    }

    [Fact]
    public void EitherPairingCodeMethodAlone_WithAllDependenciesPresent_IsAdvertised()
    {
        // Positive control for the rejection above and for every unrunnable-case test: one
        // pairing-code method with all its dependencies must still reach client/hello.
        var (dynamicClient, dynamicConnection) = CreateAndGreet(
            new ClientCapabilities { PairingCodeMethods = { "dynamic_pairing_code" } });
        using (dynamicClient)
        {
            Assert.Contains("dynamic_pairing_code", HelloPairMethods(dynamicConnection));
        }

        var (staticClient, staticConnection) = CreateAndGreet(new ClientCapabilities
        {
            PairingCodeMethods = { "static_pairing_code" },
            StaticPairingCode = "12345678",
        });
        using (staticClient)
        {
            Assert.Contains("static_pairing_code", HelloPairMethods(staticConnection));
        }
    }
}
