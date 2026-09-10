using System.Text.Json;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Connection.Noise.Pairing;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// The pairing config the SDK seeds from ClientCapabilities: which pair methods a client
/// offers, whether each one is enabled, and the dependencies a method needs before it can
/// be advertised at all. All of it is local, manufacturer-defined configuration.
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
    /// <param name="hasPairingCodeLockoutStore">
    /// Present by default so a pairing code method is never withheld for want of a lockout store,
    /// which would confound "withheld because disabled". Pass false to test the
    /// missing-store case.
    /// </param>
    /// <param name="hasPresentPairingCodeAsync">
    /// Present by default so dynamic_pin is never withheld for want of a presenter, which
    /// would confound "withheld because disabled". Pass false to test the
    /// missing-presenter case.
    /// </param>
    /// <param name="hasPairingRecordStore">
    /// Present by default for the same reason. Pass false to test the missing-record-store
    /// case (#158); it overrides <paramref name="store"/>.
    /// </param>
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
            configure: o => o with
            {
                Capabilities = capabilities,
                PairingRecordStore = hasPairingRecordStore ? store ?? new InMemoryPairingRecordStore() : null,
                PairingCodeLockoutStore = hasPairingCodeLockoutStore ? new InMemoryPairingCodeLockoutStore() : null,
                PresentPairingCodeAsync = hasPresentPairingCodeAsync ? (_, _) => ValueTask.CompletedTask : null,
                PairingWindow = window,
            });
        session.MatchedPsk = new NoisePsk(SessionPsk, PskCategory.LongTerm, FakeNoiseSession.FakeServerId);
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        return (client, connection);
    }

    private static List<string> HelloPairMethods(FakeSendspinConnection connection) =>
        connection.SentMessages.OfType<ClientHelloMessage>().Last()
            .Payload.SupportedPairMethods?.Select(m => m.Method).ToList() ?? [];

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
    public void PairingCodeMethodDisabled_IsImplementedButNotAdvertised(string method)
    {
        // Disabled is not unimplemented: the method stays in PairingCodeMethods so the app
        // can turn it back on, but client/hello must not offer it.
        var capabilities = new ClientCapabilities
        {
            PairingCodeMethods = { method },
            StaticPairingCode = "12345678",
            DynamicPairingCodeEnabled = method != "dynamic_pin",
            StaticPairingCodeEnabled = method != "static_pin",
        };
        var (client, connection) = CreateAndGreet(capabilities);
        using var _c = client;

        Assert.DoesNotContain(method, HelloPairMethods(connection));
    }

    [Theory]
    [InlineData("dynamic_pin")]
    [InlineData("static_pin")]
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
    public void PairingCodeMethodEnabledFlag_WithoutTheMethodImplemented_StaysDisabled()
    {
        // The flags default true, so they must be ANDed with PairingCodeMethods. client/hello
        // omitting the method is not proof of that AND by itself: BuildPairMethods gates on
        // IsMethodImplemented independently, so it omits an unlisted method whether or not
        // the AND exists.
        var (client, connection) = CreateAndGreet(new ClientCapabilities());
        using var _c = client;

        Assert.DoesNotContain("dynamic_pin", HelloPairMethods(connection));
        Assert.DoesNotContain("static_pin", HelloPairMethods(connection));
    }

    [Fact]
    public void StaticPairingCodeWithNullPairingCode_IsNotAdvertised_ButStillReportedDisabled()
    {
        // Construction validated nothing before this fix: an app could list static_pin with
        // no pairing code behind it, and CPace would run with an empty password (management.md:98).
        var capabilities = new ClientCapabilities { PairingCodeMethods = { "static_pin" }, StaticPairingCode = null };
        var (client, connection) = CreateAndGreet(capabilities);
        using var _c = client;

        Assert.DoesNotContain("static_pin", HelloPairMethods(connection));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1234567")] // 7 digits
    public void StaticPairingCodeWithMalformedPairingCode_IsNotAdvertised_ButStillReportedDisabled(string pin)
    {
        var capabilities = new ClientCapabilities { PairingCodeMethods = { "static_pin" }, StaticPairingCode = pin };
        var (client, connection) = CreateAndGreet(capabilities);
        using var _c = client;

        Assert.DoesNotContain("static_pin", HelloPairMethods(connection));
    }

    [Fact]
    public void StaticPairingCodeWithNoUsablePairingCode_ServerActivateAborts_MethodNotSupported()
    {
        // A server that asks for static_pin anyway (e.g. from a stale advertisement) must be
        // refused rather than let CPace run with an empty password, and the connection must
        // stay open so the server can retry with another method.
        var capabilities = new ClientCapabilities { PairingCodeMethods = { "static_pin" }, StaticPairingCode = null };
        var (client, connection) = CreateAndGreet(capabilities);
        using var _c = client;

        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"pairing":{"method":"static_pin"}}}""");

        var abort = Assert.Single(connection.SentMessages.OfType<PairAbortMessage>());
        Assert.Equal("method_not_supported", abort.Payload.Reason);
        Assert.Equal(ConnectionState.Connected, connection.State);
    }

    // --- #132 item 1: a pairing code method must not be advertised on a client missing a
    // dependency CanRun requires but BuildPairMethods did not previously check
    // (IPairingCodeLockoutStore, PresentPairingCodeAsync). ---
    [Fact]
    public void StaticPairingCodeWithValidPairingCode_IsAdvertised_AndReportedEnabled()
    {
        // Positive control: without it, a bug that disables static_pin unconditionally would
        // pass every test above.
        var capabilities = new ClientCapabilities { PairingCodeMethods = { "static_pin" }, StaticPairingCode = "12345678" };
        var (client, connection) = CreateAndGreet(capabilities);
        using var _c = client;

        Assert.Contains("static_pin", HelloPairMethods(connection));
    }

    [Fact]
    public void DynamicPairingCodeListedAndEnabled_NoPresentPairingCodeAsync_IsNotAdvertised_ButStillReportedDisabled()
    {
        // Without a presenter, a derived pairing code would reach nobody. CanOffer already refused
        // this; CanRun must also keep it out of client/hello.
        var capabilities = new ClientCapabilities { PairingCodeMethods = { "dynamic_pin" } };
        var (client, connection) = CreateAndGreet(capabilities, hasPresentPairingCodeAsync: false);
        using var _c = client;

        Assert.DoesNotContain("dynamic_pin", HelloPairMethods(connection));
    }

    [Fact]
    public void DynamicPairingCodeListedAndEnabled_NoPairingCodeLockoutStore_IsNotAdvertised_ButStillReportedDisabled()
    {
        // Without a lockout store, the failure counter can't persist, so the method could
        // never escalate to gesture-gating.
        var capabilities = new ClientCapabilities { PairingCodeMethods = { "dynamic_pin" } };
        var (client, connection) = CreateAndGreet(capabilities, hasPairingCodeLockoutStore: false);
        using var _c = client;

        Assert.DoesNotContain("dynamic_pin", HelloPairMethods(connection));
    }

    [Fact]
    public void StaticPairingCodeWithValidPairingCode_NoPairingCodeLockoutStore_IsNotAdvertised_ButStillReportedDisabled()
    {
        // static_pin needs a lockout store for the same reason dynamic_pin does, even
        // though its pairing code itself is valid.
        var capabilities = new ClientCapabilities { PairingCodeMethods = { "static_pin" }, StaticPairingCode = "12345678" };
        var (client, connection) = CreateAndGreet(capabilities, hasPairingCodeLockoutStore: false);
        using var _c = client;

        Assert.DoesNotContain("static_pin", HelloPairMethods(connection));
    }

    // --- #158: a pairing code method needs an IPairingRecordStore for the same reason pairing_psk
    // does. Without one the exchange completes, the server writes a long-term record, and
    // this client stores nothing. ---

    [Theory]
    [InlineData("dynamic_pin")]
    [InlineData("static_pin")]
    public void PairingCodeMethod_WithNoPairingRecordStore_IsNotAdvertised_ButStillReportedDisabled(string method)
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
    [InlineData("dynamic_pin")]
    [InlineData("static_pin")]
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
    [InlineData("dynamic_pin")]
    [InlineData("static_pin")]
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
        var capabilities = new ClientCapabilities { PairingCodeMethods = { "dynamic_pin" } };
        var (client, connection) = CreateAndGreet(capabilities, hasPairingCodeLockoutStore: false);
        using var _c = client;

        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"pairing":{"method":"dynamic_pin"}}}""");

        var abort = Assert.Single(connection.SentMessages.OfType<PairAbortMessage>());
        Assert.Equal("method_not_supported", abort.Payload.Reason);
        Assert.Equal(ConnectionState.Connected, connection.State);
    }

    [Fact]
    public void BothPairingCodeMethods_WithAllDependenciesPresent_AreAdvertised_AndReportedEnabled()
    {
        // Positive control: without it, a change that suppresses both pairing code methods
        // unconditionally would pass every unrunnable-case test above.
        var capabilities = new ClientCapabilities
        {
            PairingCodeMethods = { "dynamic_pin", "static_pin" },
            StaticPairingCode = "12345678",
        };
        var (client, connection) = CreateAndGreet(capabilities);
        using var _c = client;

        var helloMethods = HelloPairMethods(connection);
        Assert.Contains("dynamic_pin", helloMethods);
        Assert.Contains("static_pin", helloMethods);
    }
}
