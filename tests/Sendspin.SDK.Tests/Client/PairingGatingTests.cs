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
    public async Task DynamicPinActivation_WithPinLengthBelowClientMinimum_AbortsAtTheActivation()
    {
        await using var h = await PairingHarness.StartAsync(minPinLength: 6);

        h.SendPairingActivate(method: "dynamic_pin", pinLength: 4);

        var abort = await h.NextMessageAsync<PairAbortMessage>();
        Assert.Equal("pin_length_unacceptable", abort.Payload.Reason);
        // Aborted at the activation: no attempt was ever started.
        Assert.Empty(h.SentOfType<ClientPairInitMessage>());
    }

    [Fact]
    public async Task DynamicPinActivation_WithMissingPinLength_AbortsAtTheActivation()
    {
        await using var h = await PairingHarness.StartAsync(minPinLength: 6);

        h.SendPairingActivate(method: "dynamic_pin", pinLength: null);

        var abort = await h.NextMessageAsync<PairAbortMessage>();
        Assert.Equal("pin_length_unacceptable", abort.Payload.Reason);
    }

    [Fact]
    public async Task UnofferableMethod_WithBadPinLength_ReportsMethodNotSupportedFirst()
    {
        // Ordering is ours, not the spec's: a method the client does not offer is not a
        // PIN-length question. Pinned so the two reasons cannot silently swap.
        await using var h = await PairingHarness.StartAsync(minPinLength: 6, dynamicPin: false);

        h.SendPairingActivate(method: "dynamic_pin", pinLength: 4);

        var abort = await h.NextMessageAsync<PairAbortMessage>();
        Assert.Equal("method_not_supported", abort.Payload.Reason);
    }

    [Fact]
    public async Task DynamicPinPresenter_ReceivesTheActivationLanguages()
    {
        // The hint is informational and never grounds for abort, but the spec asks the client
        // to emit in the best-matching language it supports. It cannot do that if the SDK
        // never hands the hint over. Matching itself stays with the app, which alone knows
        // which languages it can speak.
        PinPresentation? seen = null;
        await using var h = await PairingHarness.StartAsync(
            minPinLength: 6,
            presentPin: (p, _) => { seen = p; return ValueTask.CompletedTask; });

        h.SendPairingActivate(method: "dynamic_pin", pinLength: 8, languages: ["ca", "es"]);
        await h.CompleteDynamicPinToPresentationAsync();

        Assert.NotNull(seen);
        Assert.Equal(new[] { "ca", "es" }, seen!.Languages);
        Assert.Equal(8, seen.Pin.Length);
    }

    [Fact]
    public async Task StaticPinActivation_WithNoWindowOpen_SendsPairPendingAndWithholdsInit()
    {
        var window = new PairingWindow();
        await using var h = await PairingHarness.StartAsync(staticPin: "12345678", window: window);

        h.SendPairingActivate(method: "static_pin");

        var pending = await h.NextMessageAsync<ClientPairPendingMessage>();
        Assert.Equal(1, pending.Payload.PairingIndex);
        Assert.Empty(h.SentOfType<ClientPairInitMessage>());
    }

    [Fact]
    public async Task StaticPinActivation_WhenTheWindowOpens_SendsPairInit()
    {
        var window = new PairingWindow();
        await using var h = await PairingHarness.StartAsync(staticPin: "12345678", window: window);
        h.SendPairingActivate(method: "static_pin");
        await h.NextMessageAsync<ClientPairPendingMessage>();

        window.Open();

        var init = await h.NextMessageAsync<ClientPairInitMessage>();
        Assert.Equal(1, init.Payload.PairingIndex);
        Assert.False(window.IsOpen); // consumed by the attempt
    }

    [Fact]
    public async Task StaticPinActivation_WithAWindowAlreadyOpen_SendsPairInitImmediately()
    {
        var window = new PairingWindow();
        window.Open();
        await using var h = await PairingHarness.StartAsync(staticPin: "12345678", window: window);

        h.SendPairingActivate(method: "static_pin");

        await h.NextMessageAsync<ClientPairInitMessage>();
        Assert.Empty(h.SentOfType<ClientPairPendingMessage>());
    }

    [Fact]
    public async Task DynamicPinActivation_WithLongPinAndNoEscalation_IsNotGated()
    {
        var window = new PairingWindow();
        await using var h = await PairingHarness.StartAsync(minPinLength: 6, window: window);

        h.SendPairingActivate(method: "dynamic_pin", pinLength: 8);

        await h.NextMessageAsync<ClientPairInitMessage>();
        Assert.Empty(h.SentOfType<ClientPairPendingMessage>());
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public async Task DynamicPinActivation_WithShortPin_IsGated(int pinLength)
    {
        // "short PINs are bought with a gesture" -- the boundary is below 6.
        var window = new PairingWindow();
        await using var h = await PairingHarness.StartAsync(minPinLength: 4, window: window);

        h.SendPairingActivate(method: "dynamic_pin", pinLength: pinLength);

        await h.NextMessageAsync<ClientPairPendingMessage>();
        Assert.Empty(h.SentOfType<ClientPairInitMessage>());
    }

    [Fact]
    public async Task DynamicPinActivation_AtSixDigits_IsNotGated()
    {
        var window = new PairingWindow();
        await using var h = await PairingHarness.StartAsync(minPinLength: 4, window: window);

        h.SendPairingActivate(method: "dynamic_pin", pinLength: 6);

        await h.NextMessageAsync<ClientPairInitMessage>();
        Assert.Empty(h.SentOfType<ClientPairPendingMessage>());
    }

    [Fact]
    public async Task DynamicPinActivation_WhenEscalated_IsGatedButStillRuns()
    {
        // The #127 deadlock. At 10 failures the old code refused the attempt outright with a
        // non-spec pair/abort reason, and the counter could only reset inside an attempt -- so
        // it never could. Escalation gates the attempt instead of refusing it.
        var lockouts = new InMemoryPinLockoutStore();
        lockouts.SetFailures("dynamic_pin", 10);
        var window = new PairingWindow();
        await using var h = await PairingHarness.StartAsync(
            minPinLength: 6, window: window, lockouts: lockouts);

        h.SendPairingActivate(method: "dynamic_pin", pinLength: 8);

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
        var lockouts = new InMemoryPinLockoutStore();
        lockouts.SetFailures("dynamic_pin", 10);
        await using var h = await PairingHarness.StartAsync(
            minPinLength: 6, window: new PairingWindow(), lockouts: lockouts);

        h.SendPairingActivate(method: "dynamic_pin", pinLength: 8);
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
        await using var h = await PairingHarness.StartAsync(staticPin: "12345678", window: window);
        var raised = new List<PairingGestureRequestedEventArgs>();
        h.Client.PairingGestureRequested += (_, e) => raised.Add(e);

        h.SendPairingActivate(method: "static_pin");
        await h.NextMessageAsync<ClientPairPendingMessage>();

        Assert.Single(raised);
        Assert.Equal("static_pin", raised[0].Method);
        Assert.Equal(1, raised[0].PairingIndex);
    }

    [Fact]
    public async Task DynamicPinAttempt_SucceedingAfterEscalation_ResetsTheFailureCounter()
    {
        // Closes the other half of #127: it is not enough for escalation to gate an attempt
        // instead of refusing it -- a success once the gate opens must also de-escalate the
        // method, or it stays gesture-gated forever even after nothing is failing anymore.
        var lockouts = new InMemoryPinLockoutStore();
        lockouts.SetFailures("dynamic_pin", 10);
        var window = new PairingWindow();
        await using var h = await PairingHarness.StartAsync(
            minPinLength: 6, window: window, lockouts: lockouts);

        h.SendPairingActivate(method: "dynamic_pin", pinLength: 8);
        await h.NextMessageAsync<ClientPairPendingMessage>();

        window.Open();
        await h.CompleteDynamicPinPairingAsync();

        Assert.Equal(0, lockouts.GetFailures("dynamic_pin"));
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
/// yet. <c>attemptTimeout</c> is accepted for forward signature-compatibility but not wired to
/// anything yet — <c>SendspinClientOptions</c> has no attempt-timeout option. A later task in
/// the plan adds it and should wire it through here at that point.
/// </remarks>
internal sealed class PairingHarness : IAsyncDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    private readonly FakeSendspinConnection _connection;
    private readonly FakeNoiseSession _session;
    private readonly string? _staticPin;
    private readonly bool _management;
    private readonly Dictionary<Type, int> _consumed = new();

    // Set (under lock) the moment the dynamic-PIN presenter fires, independent of whether the
    // caller's own presentPin delegate (or the no-op default substituted when it is omitted)
    // has completed. CompleteDynamicPinToPresentationAsync polls this rather than the caller's
    // delegate so it observes "the presenter was invoked", not "the presentation finished".
    private readonly List<PinPresentation> _presentations = new();
    private int _consumedPresentations;

    private PairingHarness(
        SendspinClientService client,
        FakeSendspinConnection connection,
        FakeNoiseSession session,
        string? staticPin,
        bool management)
    {
        Client = client;
        _connection = connection;
        _session = session;
        _staticPin = staticPin;
        _management = management;
    }

    public ISendspinClient Client { get; }

    /// <summary>
    /// All parameters optional. <paramref name="minPinLength"/> seeds
    /// <see cref="ClientCapabilities.MinPinLength"/>. <paramref name="dynamicPin"/>/
    /// <paramref name="staticPin"/>/<paramref name="pairingPsk"/> control which methods are
    /// implemented and enabled: dynamic_pin whenever <paramref name="dynamicPin"/> is true (the
    /// default), with a no-op presenter substituted when <paramref name="presentPin"/> is
    /// omitted so <c>CanOffer</c> can still admit it; static_pin whenever
    /// <paramref name="staticPin"/> is non-null; pairing_psk only when
    /// <paramref name="pairingPsk"/> is true, which also keys the session on the Pairing PSK
    /// (instead of Sentinel) and supplies a pairing record store. <paramref name="lockouts"/>
    /// defaults to a fresh <see cref="InMemoryPinLockoutStore"/>. <paramref name="window"/> is
    /// passed straight to <c>SendspinClientOptions.PairingWindow</c>; omitted, gated attempts
    /// stay pending forever (the fail-closed default). <paramref name="management"/>
    /// controls whether <see cref="SendPairingActivate"/> includes the 'management' activity.
    /// </summary>
    public static Task<PairingHarness> StartAsync(
        int minPinLength = 6,
        bool dynamicPin = true,
        string? staticPin = null,
        bool pairingPsk = false,
        PairingWindow? window = null,
        IPinLockoutStore? lockouts = null,
        TimeSpan? attemptTimeout = null,
        Func<PinPresentation, CancellationToken, ValueTask>? presentPin = null,
        bool management = false)
    {
        var caps = new ClientCapabilities { MinPinLength = minPinLength };
        if (dynamicPin)
        {
            caps.PinPairingMethods.Add("dynamic_pin");
        }

        if (staticPin is not null)
        {
            caps.PinPairingMethods.Add("static_pin");
            caps.StaticPin = staticPin;
        }

        PairingHarness? harness = null;

        // Wraps the app-supplied presenter so every invocation is recorded (see
        // _presentations), independent of whether the wrapped delegate ever completes.
        Func<PinPresentation, CancellationToken, ValueTask>? adaptedPresenter = dynamicPin
            ? async (presentation, ct) =>
              {
                  harness!.RecordPresentation(presentation);
                  if (presentPin is not null)
                  {
                      await presentPin(presentation, ct);
                  }
              }
            : null;

        var category = pairingPsk ? PskCategory.Pairing : PskCategory.Sentinel;
        var (client, connection, session) = TestClient.Create(
            category,
            configure: options =>
            {
                options.Capabilities = caps;
                options.PinLockoutStore = lockouts ?? new InMemoryPinLockoutStore();
                options.PresentPinAsync = adaptedPresenter;
                options.PairingWindow = window;
                if (pairingPsk)
                {
                    options.PairingRecordStore = new InMemoryPairingRecordStore();
                }
            });

        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");

        harness = new PairingHarness(client, connection, session, staticPin, management);
        return Task.FromResult(harness);
    }

    /// <summary>Feeds a pairing server/activate with the nested pairing object.</summary>
    public void SendPairingActivate(string method, int? pinLength = null, IReadOnlyList<string>? languages = null)
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
                    PinLength = pinLength,
                    Languages = languages?.ToList(),
                },
            },
        };
        _connection.RaiseTextMessageReceived(MessageSerializer.Serialize(activate));
    }

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

    /// <summary>Drives a dynamic-PIN attempt as far as the PIN presenter being invoked.</summary>
    public async Task CompleteDynamicPinToPresentationAsync()
    {
        await NextMessageAsync<ClientPairInitMessage>();

        _connection.RaiseTextMessageReceived(
            $$$"""{"type":"server/pair-init","payload":{"nonce_A":"{{{B64Url(RandomNumberGenerator.GetBytes(32))}}}"}}""");

        await WaitForNextPresentedPinAsync();
    }

    /// <summary>
    /// Drives a dynamic-PIN attempt through a full CPace exchange to a successful
    /// server/pair-finalize, using the actual derived PIN captured from the presenter
    /// (unlike <see cref="CompleteStaticPinPairingAsync"/>'s fixed static PIN, the dynamic PIN
    /// is only known once the presenter fires). Verifies the client's confirmation tag the
    /// same way a real server would, so a broken PAKE would fail this method's own CPace
    /// verification before ever reaching a caller's assertions.
    /// </summary>
    public async Task CompleteDynamicPinPairingAsync()
    {
        var init = await NextMessageAsync<ClientPairInitMessage>();

        _connection.RaiseTextMessageReceived(
            $$$"""{"type":"server/pair-init","payload":{"nonce_A":"{{{B64Url(RandomNumberGenerator.GetBytes(32))}}}"}}""");

        string pin = await WaitForNextPresentedPinAsync();

        byte[] handshakeHash = _session.HandshakeHash!.Value.ToArray();
        byte[] sid = PinPairing.BuildSid(handshakeHash, (uint)init.Payload.PairingIndex);
        var server = CPace.Start(CPaceRole.Initiator, Encoding.ASCII.GetBytes(pin), sid, ad: PinPairing.AdServer);

        _connection.RaiseTextMessageReceived(
            $$$"""{"type":"server/pair-auth","payload":{"pake_msg_1":"{{{B64Url(server.PublicShare)}}}"}}""");

        var auth = await NextMessageAsync<ClientPairAuthMessage>();
        server.Derive(PinPairing.DecodeB64Url(auth.Payload.PakeMsg2), PinPairing.AdClient);

        _connection.RaiseTextMessageReceived(
            $$$"""{"type":"server/pair-confirm","payload":{"server_kc":"{{{B64Url(server.Tag())}}}"}}""");

        var confirm = await NextMessageAsync<ClientPairConfirmMessage>();
        Assert.True(server.Verify(PinPairing.DecodeB64Url(confirm.Payload.ClientKc)));
        await NextMessageAsync<ClientPairFinalizeMessage>();

        _connection.RaiseTextMessageReceived("""{"type":"server/pair-finalize","payload":{}}""");
    }

    /// <summary>Drives a static-PIN attempt through to server/pair-finalize.</summary>
    public async Task CompleteStaticPinPairingAsync()
    {
        if (_staticPin is null)
        {
            throw new InvalidOperationException(
                "CompleteStaticPinPairingAsync requires StartAsync's staticPin parameter.");
        }

        var init = await NextMessageAsync<ClientPairInitMessage>();

        _connection.RaiseTextMessageReceived(
            $$$"""{"type":"server/pair-init","payload":{"nonce_A":"{{{B64Url(RandomNumberGenerator.GetBytes(32))}}}"}}""");

        byte[] handshakeHash = _session.HandshakeHash!.Value.ToArray();
        byte[] sid = PinPairing.BuildSid(handshakeHash, (uint)init.Payload.PairingIndex);
        var server = CPace.Start(CPaceRole.Initiator, Encoding.ASCII.GetBytes(_staticPin), sid, ad: PinPairing.AdServer);

        _connection.RaiseTextMessageReceived(
            $$$"""{"type":"server/pair-auth","payload":{"pake_msg_1":"{{{B64Url(server.PublicShare)}}}"}}""");

        var auth = await NextMessageAsync<ClientPairAuthMessage>();
        server.Derive(PinPairing.DecodeB64Url(auth.Payload.PakeMsg2), PinPairing.AdClient);

        _connection.RaiseTextMessageReceived(
            $$$"""{"type":"server/pair-confirm","payload":{"server_kc":"{{{B64Url(server.Tag())}}}"}}""");

        await NextMessageAsync<ClientPairConfirmMessage>();
        await NextMessageAsync<ClientPairFinalizeMessage>();

        _connection.RaiseTextMessageReceived("""{"type":"server/pair-finalize","payload":{}}""");
    }

    private void RecordPresentation(PinPresentation presentation)
    {
        lock (_presentations)
        {
            _presentations.Add(presentation);
        }
    }

    /// <summary>Waits for the next dynamic-PIN presentation and returns its derived PIN.</summary>
    private async Task<string> WaitForNextPresentedPinAsync()
    {
        var deadline = DateTime.UtcNow + DefaultTimeout;
        while (true)
        {
            lock (_presentations)
            {
                if (_presentations.Count > _consumedPresentations)
                {
                    return _presentations[_consumedPresentations++].Pin;
                }
            }

            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail("Timed out waiting for the dynamic-PIN presenter to be invoked.");
            }

            await Task.Delay(10);
        }
    }

    public async ValueTask DisposeAsync() => await Client.DisposeAsync();

    private static string B64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
