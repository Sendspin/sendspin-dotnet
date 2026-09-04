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
/// The pairing activation carries the method and — for the dynamic method — the emission
/// format, validated on receipt. The gesture-gating policy turns on the method before
/// client/pair-init is sent, so reading it later is not an option.
/// </summary>
public class PairingGatingTests
{
    [Fact]
    public async Task DynamicPairingCodeActivation_WithAnUnofferedFormat_AbortsAtTheActivation()
    {
        // qr_code is a format the spec defines but this SDK never advertises, so an activation
        // naming it selects a flow the client cannot run — method_not_supported, at the
        // activation, before any attempt starts.
        await using var h = await PairingHarness.StartAsync();

        h.SendPairingActivate(method: "dynamic_pairing_code", format: "qr_code");

        var abort = await h.NextMessageAsync<PairAbortMessage>();
        Assert.Equal("method_not_supported", abort.Payload.Reason);
        // Aborted at the activation: no attempt was ever started.
        Assert.Empty(h.SentOfType<ClientPairInitMessage>());
    }

    [Fact]
    public async Task DynamicPairingCodeActivation_WithNoFormat_AbortsAtTheActivation()
    {
        // format is required for dynamic_pairing_code. Absent, there is nothing to emit.
        await using var h = await PairingHarness.StartAsync();

        h.SendPairingActivate(method: "dynamic_pairing_code", omitFormat: true);

        var abort = await h.NextMessageAsync<PairAbortMessage>();
        Assert.Equal("method_not_supported", abort.Payload.Reason);
    }

    [Fact]
    public async Task UnofferableMethod_WithBadFormat_ReportsMethodNotSupported()
    {
        await using var h = await PairingHarness.StartAsync(dynamicPairingCode: false);

        h.SendPairingActivate(method: "dynamic_pairing_code", format: "qr_code");

        var abort = await h.NextMessageAsync<PairAbortMessage>();
        Assert.Equal("method_not_supported", abort.Payload.Reason);
    }

    [Fact]
    public async Task DynamicPairingCodePresenter_ReceivesTheServerHelloLanguages()
    {
        // The hint is informational and never grounds for abort, but the spec asks the client
        // to emit in the best-matching language it supports. It cannot do that if the SDK
        // never hands the hint over. Matching itself stays with the app, which alone knows
        // which languages it can speak. Spec #178 moved the hint to server/hello, so it is
        // connection-scoped rather than per-activation.
        PairingCodePresentation? seen = null;
        await using var h = await PairingHarness.StartAsync(
            serverLanguages: ["ca", "es"],
            presentPairingCode: (p, _) => { seen = p; return ValueTask.CompletedTask; });

        h.SendPairingActivate(method: "dynamic_pairing_code");
        await h.CompleteDynamicPairingCodeToPresentationAsync();

        Assert.NotNull(seen);
        Assert.Equal(new[] { "ca", "es" }, seen!.Languages);
        Assert.Equal(6, seen.PairingCode.Length);
    }

    [Fact]
    public async Task StaticPairingCodeActivation_WithNoWindowOpen_SendsPairPendingAndWithholdsInit()
    {
        var window = new PairingWindow();
        await using var h = await PairingHarness.StartAsync(staticPairingCode: "12345678", window: window);

        h.SendPairingActivate(method: "static_pairing_code");

        var pending = await h.NextMessageAsync<ClientPairPendingMessage>();
        Assert.Equal(1, pending.Payload.PairingIndex);
        Assert.Empty(h.SentOfType<ClientPairInitMessage>());
    }

    [Fact]
    public async Task StaticPairingCodeActivation_WhenTheWindowOpens_SendsPairInit()
    {
        var window = new PairingWindow();
        await using var h = await PairingHarness.StartAsync(staticPairingCode: "12345678", window: window);
        h.SendPairingActivate(method: "static_pairing_code");
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

        h.SendPairingActivate(method: "static_pairing_code");

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
        h.SendPairingActivate(method: "static_pairing_code");
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
        await using var h = await PairingHarness.StartAsync(window: window);

        h.SendPairingActivate(method: "dynamic_pairing_code");

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
        await using var h = await PairingHarness.StartAsync(window: window);

        h.SendPairingActivate(method: "dynamic_pairing_code");

        await h.NextMessageAsync<ClientPairInitMessage>();
        Assert.True(window.IsOpen, "an ungated attempt must leave the opening for a gated one");
    }

    [Fact]
    public async Task DynamicPairingCodeActivation_WithNoEscalation_IsNotGated()
    {
        // The old "or the code is shorter than 6 digits" gating clause went with pin_length:
        // a digits code is always 6, so escalation is the only thing that gates this method.
        var window = new PairingWindow();
        await using var h = await PairingHarness.StartAsync(window: window);

        h.SendPairingActivate(method: "dynamic_pairing_code");

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
        lockouts.SetFailures("dynamic_pairing_code", 10);
        var window = new PairingWindow();
        await using var h = await PairingHarness.StartAsync(
            window: window, lockouts: lockouts);

        h.SendPairingActivate(method: "dynamic_pairing_code");

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
        lockouts.SetFailures("dynamic_pairing_code", 10);
        await using var h = await PairingHarness.StartAsync(
            window: new PairingWindow(), lockouts: lockouts);

        h.SendPairingActivate(method: "dynamic_pairing_code");
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

        h.SendPairingActivate(method: "static_pairing_code");
        await h.NextMessageAsync<ClientPairPendingMessage>();

        Assert.Single(raised);
        Assert.Equal("static_pairing_code", raised[0].Method);
        Assert.Equal(1, raised[0].PairingIndex);
    }

    [Fact]
    public async Task DynamicPairingCodeAttempt_SucceedingAfterEscalation_ResetsTheFailureCounter()
    {
        // Closes the other half of #127: it is not enough for escalation to gate an attempt
        // instead of refusing it -- a success once the gate opens must also de-escalate the
        // method, or it stays gesture-gated forever even after nothing is failing anymore.
        var lockouts = new InMemoryPairingCodeLockoutStore();
        lockouts.SetFailures("dynamic_pairing_code", 10);
        var window = new PairingWindow();
        await using var h = await PairingHarness.StartAsync(
            window: window, lockouts: lockouts);

        h.SendPairingActivate(method: "dynamic_pairing_code");
        await h.NextMessageAsync<ClientPairPendingMessage>();

        window.Open();
        await h.CompleteDynamicPairingCodeAsync();

        Assert.Equal(0, lockouts.GetFailures("dynamic_pairing_code"));
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
        string? staticPairingCode)
    {
        Client = client;
        _connection = connection;
        _session = session;
        _staticPairingCode = staticPairingCode;
    }

    public ISendspinClient Client { get; }

    /// <summary>
    /// All parameters optional. <paramref name="dynamicPairingCode"/>/
    /// <paramref name="staticPairingCode"/>/<paramref name="pairingPsk"/> control which methods are
    /// implemented and enabled: dynamic_pairing_code whenever <paramref name="dynamicPairingCode"/> is true (the
    /// default), with a no-op presenter substituted when <paramref name="presentPairingCode"/> is
    /// omitted so <c>CanOffer</c> can still admit it; static_pairing_code whenever
    /// <paramref name="staticPairingCode"/> is non-null; pairing_psk only when
    /// <paramref name="pairingPsk"/> is true, which also keys the session on the Pairing PSK
    /// (instead of Sentinel) and supplies a pairing record store — <paramref name="pairingStore"/>
    /// when given, so a test can assert on what pairing did or did not persist.
    /// <paramref name="lockouts"/>
    /// defaults to a fresh <see cref="InMemoryPairingCodeLockoutStore"/>. <paramref name="window"/> is
    /// passed straight to <c>SendspinClientOptions.PairingWindow</c>; omitted, gated attempts
    /// stay pending forever (the fail-closed default).
    /// </summary>
    /// <remarks>
    /// The two pairing-code methods are mutually exclusive (spec #189), so a caller that passes
    /// a static pairing code must also pass <c>dynamicPairingCode: false</c>.
    /// </remarks>
    public static Task<PairingHarness> StartAsync(
        bool dynamicPairingCode = true,
        string? staticPairingCode = null,
        bool pairingPsk = false,
        IPairingRecordStore? pairingStore = null,
        PairingWindow? window = null,
        IPairingCodeLockoutStore? lockouts = null,
        TimeSpan? attemptTimeout = null,
        IReadOnlyList<string>? serverLanguages = null,
        Func<PairingCodePresentation, CancellationToken, ValueTask>? presentPairingCode = null)
    {
        var caps = new ClientCapabilities();
        if (dynamicPairingCode && staticPairingCode is null)
        {
            caps.PairingCodeMethods.Add("dynamic_pairing_code");
        }

        if (staticPairingCode is not null)
        {
            caps.PairingCodeMethods.Add("static_pairing_code");
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

        var category = pairingPsk ? PskCategory.Pairing : PskCategory.Sentinel;

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

        connection.RaiseTextMessageReceived(
            MessageSerializer.Serialize(new ServerHelloMessage
            {
                Payload = new ServerHelloPayload
                {
                    Name = "srv",
                    Languages = serverLanguages?.ToList(),
                },
            }));

        harness = new PairingHarness(client, connection, session, staticPairingCode);
        return Task.FromResult(harness);
    }

    /// <summary>Feeds a pairing server/activate with the nested pairing object.</summary>
    /// <param name="method">The pair method the server selected.</param>
    /// <param name="format">
    /// The emission format, defaulted to <c>digits</c> for the dynamic method (the spec requires
    /// one there) and omitted otherwise. Pass an explicit value to exercise rejection.
    /// </param>
    /// <param name="omitFormat">Omits <c>format</c> entirely, whatever the method.</param>
    public void SendPairingActivate(string method, string? format = null, bool omitFormat = false)
    {
        var activities = new List<string> { Activities.Pairing };

        var activate = new ServerActivateMessage
        {
            Payload = new ServerActivatePayload
            {
                ActivitiesList = activities,
                ActiveRoles = new List<string>(),
                Pairing = new PairingActivation
                {
                    Method = method,
                    Format = omitFormat
                        ? null
                        : format ?? (method == PairMethods.DynamicPairingCode
                            ? PairingCodeFormats.Digits
                            : null),
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
