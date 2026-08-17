using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Connection.Noise.Pairing;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Spec 5b0e6469 moved pin_length out of server/pair-init and into the activation, to be
/// validated on receipt (pairing.md:149). The gesture-gating policy turns on it before
/// client/pair-init is sent, so reading it later is not an option.
/// </summary>
public class PairingGatingTests
{
    [Fact]
    public async Task DynamicPairingCodeActivation_WithPairingCodeLengthBelowClientMinimum_AbortsAtTheActivation()
    {
        await using var h = await PairingHarness.StartAsync(minPairingCodeLength: 6);

        h.SendPairingActivate(method: "dynamic_pin", pairingCodeLength: 4);

        var abort = await h.NextMessageAsync<PairAbortMessage>();
        Assert.Equal("pin_length_unacceptable", abort.Payload.Reason);
        // Aborted at the activation: no attempt was ever started.
        Assert.Empty(h.SentOfType<ClientPairInitMessage>());
    }

    [Fact]
    public async Task DynamicPairingCodeActivation_WithMissingPairingCodeLength_AbortsAtTheActivation()
    {
        await using var h = await PairingHarness.StartAsync(minPairingCodeLength: 6);

        h.SendPairingActivate(method: "dynamic_pin", pairingCodeLength: null);

        var abort = await h.NextMessageAsync<PairAbortMessage>();
        Assert.Equal("pin_length_unacceptable", abort.Payload.Reason);
    }

    [Fact]
    public async Task UnofferableMethod_WithBadPairingCodeLength_ReportsMethodNotSupportedFirst()
    {
        // Ordering is ours, not the spec's: a method the client does not offer is not a
        // pairing code-length question. Pinned so the two reasons cannot silently swap.
        await using var h = await PairingHarness.StartAsync(minPairingCodeLength: 6, dynamicPairingCode: false);

        h.SendPairingActivate(method: "dynamic_pin", pairingCodeLength: 4);

        var abort = await h.NextMessageAsync<PairAbortMessage>();
        Assert.Equal("method_not_supported", abort.Payload.Reason);
    }

    [Fact]
    public async Task DynamicPairingCodePresenter_ReceivesTheActivationLanguages()
    {
        // The hint is informational and never grounds for abort, but the spec asks the client
        // to emit in the best-matching language it supports. It cannot do that if the SDK
        // never hands the hint over. Matching itself stays with the app, which alone knows
        // which languages it can speak.
        PairingCodePresentation? seen = null;
        await using var h = await PairingHarness.StartAsync(
            minPairingCodeLength: 6,
            presentPairingCode: (p, _) => { seen = p; return ValueTask.CompletedTask; });

        h.SendPairingActivate(method: "dynamic_pin", pairingCodeLength: 8, languages: ["ca", "es"]);
        await h.CompleteDynamicPairingCodeToPresentationAsync();

        Assert.NotNull(seen);
        Assert.Equal(new[] { "ca", "es" }, seen!.Languages);
        Assert.Equal(8, seen.PairingCode.Length);
    }

    [Fact]
    public async Task StaticPairingCodeActivation_WithNoWindowOpen_SendsPairPendingAndWithholdsInit()
    {
        var window = new PairingWindow();
        await using var h = await PairingHarness.StartAsync(staticPairingCode: "12345678", window: window);

        h.SendPairingActivate(method: "static_pin");

        var pending = await h.NextMessageAsync<ClientPairPendingMessage>();
        Assert.Equal(1, pending.Payload.PairingIndex);
        Assert.Empty(h.SentOfType<ClientPairInitMessage>());
    }

    [Fact]
    public async Task StaticPairingCodeActivation_WhenTheWindowOpens_SendsPairInit()
    {
        var window = new PairingWindow();
        await using var h = await PairingHarness.StartAsync(staticPairingCode: "12345678", window: window);
        h.SendPairingActivate(method: "static_pin");
        await h.NextMessageAsync<ClientPairPendingMessage>();

        window.Open();

        var init = await h.NextMessageAsync<ClientPairInitMessage>();
        Assert.Equal(1, init.Payload.PairingIndex);
        Assert.False(window.IsOpen); // consumed by the attempt
    }

    [Fact]
    public async Task StaticPairingCodeActivation_WithAWindowAlreadyOpen_SendsPairInitImmediately()
    {
        var window = new PairingWindow();
        window.Open();
        await using var h = await PairingHarness.StartAsync(staticPairingCode: "12345678", window: window);

        h.SendPairingActivate(method: "static_pin");

        await h.NextMessageAsync<ClientPairInitMessage>();
        Assert.Empty(h.SentOfType<ClientPairPendingMessage>());
    }

    [Fact]
    public async Task PendingGatedAttempt_WhenTheConnectionLeavesPairing_IsDiscardedWithoutConsumingTheWindow()
    {
        // A pending attempt belongs to the activation that deferred it. Left standing, the next
        // opening makes this connection send client/pair-init outside any pairing activation
        // AND consume the shared window -- so the operator's gesture silently does nothing for
        // whichever connection is still legitimately pending.
        var window = new PairingWindow();
        await using var h = await PairingHarness.StartAsync(staticPairingCode: "12345678", window: window);
        h.SendPairingActivate(method: "static_pin");
        await h.NextMessageAsync<ClientPairPendingMessage>();

        h.SendNonPairingActivate();
        window.Open();

        // Nothing is expected to be sent, so there is no message to wait for: give the send
        // path (SafeFireAndForget) time to produce the init this test says must not exist.
        await Task.Delay(200);
        Assert.Empty(h.SentOfType<ClientPairInitMessage>());
        Assert.True(window.IsOpen);
    }

    [Fact]
    public async Task DynamicPairingCodeActivation_WithLongPairingCodeAndNoEscalation_IsNotGated()
    {
        var window = new PairingWindow();
        await using var h = await PairingHarness.StartAsync(minPairingCodeLength: 6, window: window);

        h.SendPairingActivate(method: "dynamic_pin", pairingCodeLength: 8);

        await h.NextMessageAsync<ClientPairInitMessage>();
        Assert.Empty(h.SentOfType<ClientPairPendingMessage>());
    }

    [Fact]
    public async Task UngatedActivation_DoesNotConsumeAnOpenWindow()
    {
        // The window is shared across every connection, and an opening is an operator gesture
        // spent on whoever actually needs one. An ungated attempt must not claim it in passing:
        // doing so would silently swallow the gesture another connection is waiting for.
        var window = new PairingWindow();
        window.Open();
        await using var h = await PairingHarness.StartAsync(minPairingCodeLength: 6, window: window);

        h.SendPairingActivate(method: "dynamic_pin", pairingCodeLength: 8);

        await h.NextMessageAsync<ClientPairInitMessage>();
        Assert.True(window.IsOpen, "an ungated attempt must leave the opening for a gated one");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public async Task DynamicPairingCodeActivation_WithShortPairingCode_IsGated(int pairingCodeLength)
    {
        // "short pairing codes are bought with a gesture" -- the boundary is below 6.
        var window = new PairingWindow();
        await using var h = await PairingHarness.StartAsync(minPairingCodeLength: 4, window: window);

        h.SendPairingActivate(method: "dynamic_pin", pairingCodeLength: pairingCodeLength);

        await h.NextMessageAsync<ClientPairPendingMessage>();
        Assert.Empty(h.SentOfType<ClientPairInitMessage>());
    }

    [Fact]
    public async Task DynamicPairingCodeActivation_AtSixDigits_IsNotGated()
    {
        var window = new PairingWindow();
        await using var h = await PairingHarness.StartAsync(minPairingCodeLength: 4, window: window);

        h.SendPairingActivate(method: "dynamic_pin", pairingCodeLength: 6);

        await h.NextMessageAsync<ClientPairInitMessage>();
        Assert.Empty(h.SentOfType<ClientPairPendingMessage>());
    }

    [Fact]
    public async Task DynamicPairingCodeActivation_WhenEscalated_IsGatedButStillRuns()
    {
        // The #127 deadlock. At 10 failures the old code refused the attempt outright with a
        // non-spec pair/abort reason, and the counter could only reset inside an attempt -- so
        // it never could. Escalation gates the attempt instead of refusing it.
        var lockouts = new InMemoryPairingCodeLockoutStore();
        lockouts.SetFailures("dynamic_pin", 10);
        var window = new PairingWindow();
        await using var h = await PairingHarness.StartAsync(
            minPairingCodeLength: 6, window: window, lockouts: lockouts);

        h.SendPairingActivate(method: "dynamic_pin", pairingCodeLength: 8);

        // Gated, not refused: a pending signal and no abort.
        await h.NextMessageAsync<ClientPairPendingMessage>();
        Assert.Empty(h.SentOfType<PairAbortMessage>());

        window.Open();
        await h.NextMessageAsync<ClientPairInitMessage>();
    }

    [Fact]
    public async Task EscalatedMethod_IsStillOffered_AndNeverAbortsWithLockedOut()
    {
        // "Escalation is not an error state - the method stays offered."
        var lockouts = new InMemoryPairingCodeLockoutStore();
        lockouts.SetFailures("dynamic_pin", 10);
        await using var h = await PairingHarness.StartAsync(
            minPairingCodeLength: 6, window: new PairingWindow(), lockouts: lockouts);

        h.SendPairingActivate(method: "dynamic_pin", pairingCodeLength: 8);
        await h.NextMessageAsync<ClientPairPendingMessage>();

        Assert.DoesNotContain(h.AllSentJson(), j => j.Contains("locked_out", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PairingPskActivation_IsNeverGated()
    {
        await using var h = await PairingHarness.StartAsync(pairingPsk: true, window: new PairingWindow());

        h.SendPairingActivate(method: "pairing_psk");

        await h.NextMessageAsync<ClientPairFinalizeMessage>();
        Assert.Empty(h.SentOfType<ClientPairPendingMessage>());
    }

    [Fact]
    public async Task GatedActivation_RaisesPairingGestureRequestedOnce()
    {
        var window = new PairingWindow();
        await using var h = await PairingHarness.StartAsync(staticPairingCode: "12345678", window: window);
        var raised = new List<PairingGestureRequestedEventArgs>();
        h.Client.PairingGestureRequested += (_, e) => raised.Add(e);

        h.SendPairingActivate(method: "static_pin");
        await h.NextMessageAsync<ClientPairPendingMessage>();

        Assert.Single(raised);
        Assert.Equal("static_pin", raised[0].Method);
        Assert.Equal(1, raised[0].PairingIndex);
    }

    [Fact]
    public async Task DynamicPairingCodeAttempt_SucceedingAfterEscalation_ResetsTheFailureCounter()
    {
        // Closes the other half of #127: it is not enough for escalation to gate an attempt
        // instead of refusing it -- a success once the gate opens must also de-escalate the
        // method, or it stays gesture-gated forever even after nothing is failing anymore.
        var lockouts = new InMemoryPairingCodeLockoutStore();
        lockouts.SetFailures("dynamic_pin", 10);
        var window = new PairingWindow();
        await using var h = await PairingHarness.StartAsync(
            minPairingCodeLength: 6, window: window, lockouts: lockouts);

        h.SendPairingActivate(method: "dynamic_pin", pairingCodeLength: 8);
        await h.NextMessageAsync<ClientPairPendingMessage>();

        window.Open();
        await h.CompleteDynamicPairingCodeAsync();

        Assert.Equal(0, lockouts.GetFailures("dynamic_pin"));
    }

    [Fact]
    public async Task OpenPairingWindow_OnAManagementSession_OpensTheWindow()
    {
        var window = new PairingWindow();
        await using var h = await PairingHarness.StartAsync(
            staticPairingCode: "12345678", window: window, management: true);

        h.SendManagement("management/open-pairing-window");

        var result = await h.NextMessageAsync<ManagementResultMessage>();
        Assert.Equal("ok", result.Payload.Result);
        Assert.True(window.IsOpen);
    }

    [Fact]
    public async Task OpenPairingWindow_WhenAlreadyOpen_IsANoOpOk()
    {
        var window = new PairingWindow();
        window.Open();
        await using var h = await PairingHarness.StartAsync(
            staticPairingCode: "12345678", window: window, management: true);

        h.SendManagement("management/open-pairing-window");

        var result = await h.NextMessageAsync<ManagementResultMessage>();
        Assert.Equal("ok", result.Payload.Result);
        Assert.True(window.IsOpen);
    }

    [Fact]
    public async Task OpenPairingWindow_WithNoPairingCodeMethodEnabled_IsInvalid()
    {
        var window = new PairingWindow();

        // dynamicPairingCode defaults to true in the harness, so it must be turned off explicitly to
        // get an implemented-methods set of zero.
        await using var h = await PairingHarness.StartAsync(
            dynamicPairingCode: false, window: window, management: true); // no pairing code methods configured

        h.SendManagement("management/open-pairing-window");

        var result = await h.NextMessageAsync<ManagementResultMessage>();
        Assert.Equal("invalid", result.Payload.Result);
        Assert.False(window.IsOpen);
    }

    [Fact]
    public async Task OpenPairingWindow_OutsideAManagementSession_IsPermissionDenied()
    {
        var window = new PairingWindow();
        await using var h = await PairingHarness.StartAsync(
            staticPairingCode: "12345678", window: window, management: false);

        h.SendManagement("management/open-pairing-window");

        var result = await h.NextMessageAsync<ManagementResultMessage>();
        Assert.Equal("permission_denied", result.Payload.Result);
        Assert.False(window.IsOpen);
    }
}

/// <summary>
/// Wires a <see cref="SendspinClientService"/> to a <see cref="FakeSendspinConnection"/> and
/// drives it to transport mode (server/hello answered), for pairing-flow tests. Construction
/// and the handshake sequence follow <see cref="SendspinClientServicePairingTests"/> — read
/// that file before changing this one.
/// </summary>
/// <remarks>
/// This surface is binding across the whole pairing-window plan, not just the tests in this
/// file: later tasks call every member here, including ones exercised by no test in this file
/// yet. <c>attemptTimeout</c> flows into <c>SendspinClientOptions.PairingAttemptTimeout</c> when
/// given; omitted, the option's own default (2 minutes) applies.
/// </remarks>
internal sealed class PairingHarness : IAsyncDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    private readonly FakeSendspinConnection _connection;
    private readonly FakeNoiseSession _session;
    private readonly string? _staticPairingCode;
    private readonly bool _management;
    private readonly Dictionary<Type, int> _consumed = new();

    // Set (under lock) the moment the dynamic-pairing code presenter fires, independent of whether the
    // caller's own presentPairingCode delegate (or the no-op default substituted when it is omitted)
    // has completed. CompleteDynamicPairingCodeToPresentationAsync polls this rather than the caller's
    // delegate so it observes "the presenter was invoked", not "the presentation finished".
    private readonly List<PairingCodePresentation> _presentations = new();
    private int _consumedPresentations;

    private PairingHarness(
        SendspinClientService client,
        FakeSendspinConnection connection,
        FakeNoiseSession session,
        string? staticPairingCode,
        bool management)
    {
        Client = client;
        _connection = connection;
        _session = session;
        _staticPairingCode = staticPairingCode;
        _management = management;
    }

    public ISendspinClient Client { get; }

    /// <summary>
    /// All parameters optional. <paramref name="minPairingCodeLength"/> seeds
    /// <see cref="ClientCapabilities.MinPairingCodeLength"/>. <paramref name="dynamicPairingCode"/>/
    /// <paramref name="staticPairingCode"/>/<paramref name="pairingPsk"/> control which methods are
    /// implemented and enabled: dynamic_pin whenever <paramref name="dynamicPairingCode"/> is true (the
    /// default), with a no-op presenter substituted when <paramref name="presentPairingCode"/> is
    /// omitted so <c>CanOffer</c> can still admit it; static_pin whenever
    /// <paramref name="staticPairingCode"/> is non-null; pairing_psk only when
    /// <paramref name="pairingPsk"/> is true, which also keys the session on the Pairing PSK
    /// (instead of Sentinel) and supplies a pairing record store — <paramref name="pairingStore"/>
    /// when given, so a test can assert on what pairing did or did not persist.
    /// <paramref name="lockouts"/>
    /// defaults to a fresh <see cref="InMemoryPairingCodeLockoutStore"/>. <paramref name="window"/> is
    /// passed straight to <c>SendspinClientOptions.PairingWindow</c>; omitted, gated attempts
    /// stay pending forever (the fail-closed default). <paramref name="management"/>, when true,
    /// both raises a server/activate granting the 'management' activity up front (so bare
    /// <see cref="SendManagement"/> requests need no pairing activation to ride in on) and makes
    /// <see cref="SendPairingActivate"/> include that activity on any activation it sends later.
    /// </summary>
    public static Task<PairingHarness> StartAsync(
        int minPairingCodeLength = 6,
        bool dynamicPairingCode = true,
        string? staticPairingCode = null,
        bool pairingPsk = false,
        IPairingRecordStore? pairingStore = null,
        PairingWindow? window = null,
        IPairingCodeLockoutStore? lockouts = null,
        TimeSpan? attemptTimeout = null,
        Func<PairingCodePresentation, CancellationToken, ValueTask>? presentPairingCode = null,
        bool management = false)
    {
        var caps = new ClientCapabilities { MinPairingCodeLength = minPairingCodeLength };
        if (dynamicPairingCode)
        {
            caps.PairingCodeMethods.Add("dynamic_pin");
        }

        if (staticPairingCode is not null)
        {
            caps.PairingCodeMethods.Add("static_pin");
            caps.StaticPairingCode = staticPairingCode;
        }

        PairingHarness? harness = null;

        // Wraps the app-supplied presenter so every invocation is recorded (see
        // _presentations), independent of whether the wrapped delegate ever completes.
        Func<PairingCodePresentation, CancellationToken, ValueTask>? adaptedPresenter = dynamicPairingCode
            ? async (presentation, ct) =>
              {
                  harness!.RecordPresentation(presentation);
                  if (presentPairingCode is not null)
                  {
                      await presentPairingCode(presentation, ct);
                  }
              }
            : null;

        // The activation admissibility table (SendSpinClient.IsAdmissible) never allows the
        // 'management' activity on a Sentinel-trust session — only a paired (LongTerm) one —
        // matching the spec: only an already-paired server may drive management/*.
        PskCategory category;
        if (pairingPsk)
        {
            category = PskCategory.Pairing;
        }
        else if (management)
        {
            category = PskCategory.LongTerm;
        }
        else
        {
            category = PskCategory.Sentinel;
        }

        var (client, connection, session) = TestClient.Create(
            category,
            configure: options =>
            {
                options = options with
                {
                    Capabilities = caps,
                    PairingCodeLockoutStore = lockouts ?? new InMemoryPairingCodeLockoutStore(),
                    PresentPairingCodeAsync = adaptedPresenter,
                    PairingWindow = window,
                };

                if (attemptTimeout is not null)
                {
                    options = options with { PairingAttemptTimeout = attemptTimeout.Value };
                }

                // Unconditional, not gated on pairingPsk: since #158 a record store is a
                // dependency of the pairing code methods too, so a pairing code-only harness without one would
                // find every method unrunnable and every activation aborted with
                // method_not_supported. It does not make pairing_psk offerable on its own --
                // that still needs a Pairing-keyed session, which only pairingPsk: true gives.
                options = options with { PairingRecordStore = pairingStore ?? new InMemoryPairingRecordStore() };

                return options;
            });

        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");

        // Bare management requests (SendManagement) have no pairing activation to ride in on,
        // so a management session is established directly here rather than through
        // SendPairingActivate's nested pairing object.
        if (management)
        {
            connection.RaiseTextMessageReceived(
                """{"type":"server/activate","payload":{"activities":["management"],"active_roles":[]}}""");
        }

        harness = new PairingHarness(client, connection, session, staticPairingCode, management);
        return Task.FromResult(harness);
    }

    /// <summary>Feeds a pairing server/activate with the nested pairing object.</summary>
    public void SendPairingActivate(string method, int? pairingCodeLength = null, IReadOnlyList<string>? languages = null)
    {
        var activities = new List<string> { Activities.Pairing };
        if (_management)
        {
            activities.Add(Activities.Management);
        }

        var activate = new ServerActivateMessage
        {
            Payload = new ServerActivatePayload
            {
                ActivitiesList = activities,
                ActiveRoles = new List<string>(),
                Pairing = new PairingActivation
                {
                    Method = method,
                    PairingCodeLength = pairingCodeLength,
                    Languages = languages?.ToList(),
                },
            },
        };
        _connection.RaiseTextMessageReceived(MessageSerializer.Serialize(activate));
    }

    /// <summary>
    /// Feeds a server/activate that does not declare the pairing activity — the connection
    /// leaving pairing. Empty activities are admissible on every PSK category the harness
    /// builds, so this needs no cooperation from the caller's session setup.
    /// </summary>
    public void SendNonPairingActivate() =>
        _connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":[],"active_roles":[]}}""");

    /// <summary>Feeds a bare server/pair-finalize, the message that persists the record.</summary>
    public void SendServerPairFinalize() =>
        _connection.RaiseTextMessageReceived("""{"type":"server/pair-finalize","payload":{}}""");

    /// <summary>Feeds a bare management request of the given type.</summary>
    public void SendManagement(string type) =>
        _connection.RaiseTextMessageReceived(
            $$$"""{"type":"{{{type}}}","payload":{}}""");

    /// <summary>Waits for the next sent message of type T, failing the test on timeout.</summary>
    public async Task<T> NextMessageAsync<T>(TimeSpan? timeout = null) where T : class
    {
        var effectiveTimeout = timeout ?? DefaultTimeout;
        var deadline = DateTime.UtcNow + effectiveTimeout;
        int skip = _consumed.GetValueOrDefault(typeof(T));
        while (true)
        {
            var match = _connection.SnapshotSentMessages().OfType<T>().Skip(skip).FirstOrDefault();
            if (match is not null)
            {
                _consumed[typeof(T)] = skip + 1;
                return match;
            }

            if (DateTime.UtcNow >= deadline)
            {
                // A clear assertion failure rather than a hang: a stuck pairing test is
                // otherwise indistinguishable from the pre-existing #143 arbitration flake.
                string sent = string.Join(", ", _connection.SnapshotSentMessages().Select(m => m.GetType().Name));
                Assert.Fail(
                    $"Timed out after {effectiveTimeout.TotalSeconds}s waiting for a {typeof(T).Name} " +
                    $"to be sent. Sent so far: [{sent}]");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Every message of type T sent so far.</summary>
    public IReadOnlyList<T> SentOfType<T>() where T : class =>
        _connection.SnapshotSentMessages().OfType<T>().ToList();

    /// <summary>Raw JSON of every message sent so far.</summary>
    public IReadOnlyList<string> AllSentJson() =>
        _connection.SnapshotSentMessages()
            .Select(m => JsonSerializer.Serialize(m, m.GetType(), MessageSerializerContext.Default))
            .ToList();

    /// <summary>Drives a dynamic-pairing code attempt as far as the pairing code presenter being invoked.</summary>
    public async Task CompleteDynamicPairingCodeToPresentationAsync()
    {
        await NextMessageAsync<ClientPairInitMessage>();

        _connection.RaiseTextMessageReceived(
            $$$"""{"type":"server/pair-init","payload":{"nonce_A":"{{{B64Url(RandomNumberGenerator.GetBytes(32))}}}"}}""");

        await WaitForNextPresentedPairingCodeAsync();
    }

    /// <summary>
    /// Drives a dynamic-pairing code attempt through a full CPace exchange to a successful
    /// server/pair-finalize, using the actual derived pairing code captured from the presenter
    /// (unlike <see cref="CompleteStaticPairingCodeAsync"/>'s fixed static pairing code, the dynamic pairing code
    /// is only known once the presenter fires). Verifies the client's confirmation tag the
    /// same way a real server would, so a broken PAKE would fail this method's own CPace
    /// verification before ever reaching a caller's assertions.
    /// </summary>
    public async Task CompleteDynamicPairingCodeAsync()
    {
        var init = await NextMessageAsync<ClientPairInitMessage>();

        _connection.RaiseTextMessageReceived(
            $$$"""{"type":"server/pair-init","payload":{"nonce_A":"{{{B64Url(RandomNumberGenerator.GetBytes(32))}}}"}}""");

        string pin = await WaitForNextPresentedPairingCodeAsync();

        byte[] handshakeHash = _session.HandshakeHash!.Value.ToArray();
        byte[] sid = PairingCodes.BuildSid(handshakeHash, (uint)init.Payload.PairingIndex);
        var server = CPace.Start(CPaceRole.Initiator, Encoding.ASCII.GetBytes(pin), sid, ad: PairingCodes.AdServer);

        _connection.RaiseTextMessageReceived(
            $$$"""{"type":"server/pair-auth","payload":{"pake_msg_1":"{{{B64Url(server.PublicShare)}}}"}}""");

        var auth = await NextMessageAsync<ClientPairAuthMessage>();
        server.Derive(Base64UrlText.Decode(auth.Payload.PakeMsg2), PairingCodes.AdClient);

        _connection.RaiseTextMessageReceived(
            $$$"""{"type":"server/pair-confirm","payload":{"server_kc":"{{{B64Url(server.Tag())}}}"}}""");

        var confirm = await NextMessageAsync<ClientPairConfirmMessage>();
        Assert.True(server.Verify(Base64UrlText.Decode(confirm.Payload.ClientKc)));
        await NextMessageAsync<ClientPairFinalizeMessage>();

        _connection.RaiseTextMessageReceived("""{"type":"server/pair-finalize","payload":{}}""");
    }

    /// <summary>Drives a static-pairing code attempt through to server/pair-finalize.</summary>
    public async Task CompleteStaticPairingCodeAsync()
    {
        if (_staticPairingCode is null)
        {
            throw new InvalidOperationException(
                "CompleteStaticPairingCodeAsync requires StartAsync's staticPairingCode parameter.");
        }

        var init = await NextMessageAsync<ClientPairInitMessage>();

        _connection.RaiseTextMessageReceived(
            $$$"""{"type":"server/pair-init","payload":{"nonce_A":"{{{B64Url(RandomNumberGenerator.GetBytes(32))}}}"}}""");

        byte[] handshakeHash = _session.HandshakeHash!.Value.ToArray();
        byte[] sid = PairingCodes.BuildSid(handshakeHash, (uint)init.Payload.PairingIndex);
        var server = CPace.Start(CPaceRole.Initiator, Encoding.ASCII.GetBytes(_staticPairingCode), sid, ad: PairingCodes.AdServer);

        _connection.RaiseTextMessageReceived(
            $$$"""{"type":"server/pair-auth","payload":{"pake_msg_1":"{{{B64Url(server.PublicShare)}}}"}}""");

        var auth = await NextMessageAsync<ClientPairAuthMessage>();
        server.Derive(Base64UrlText.Decode(auth.Payload.PakeMsg2), PairingCodes.AdClient);

        _connection.RaiseTextMessageReceived(
            $$$"""{"type":"server/pair-confirm","payload":{"server_kc":"{{{B64Url(server.Tag())}}}"}}""");

        await NextMessageAsync<ClientPairConfirmMessage>();
        await NextMessageAsync<ClientPairFinalizeMessage>();

        _connection.RaiseTextMessageReceived("""{"type":"server/pair-finalize","payload":{}}""");
    }

    private void RecordPresentation(PairingCodePresentation presentation)
    {
        lock (_presentations)
        {
            _presentations.Add(presentation);
        }
    }

    /// <summary>Waits for the next dynamic-pairing code presentation and returns its derived pairing code.</summary>
    private async Task<string> WaitForNextPresentedPairingCodeAsync()
    {
        var deadline = DateTime.UtcNow + DefaultTimeout;
        while (true)
        {
            lock (_presentations)
            {
                if (_presentations.Count > _consumedPresentations)
                {
                    return _presentations[_consumedPresentations++].PairingCode;
                }
            }

            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail("Timed out waiting for the dynamic-pairing code presenter to be invoked.");
            }

            await Task.Delay(10);
        }
    }

    public async ValueTask DisposeAsync() => await Client.DisposeAsync();

    private static string B64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
