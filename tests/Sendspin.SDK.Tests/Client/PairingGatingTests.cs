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
}

/// <summary>
/// Wires a <see cref="SendspinClientService"/> to a <see cref="FakeSendspinConnection"/> and
/// drives it to transport mode (server/hello answered), for pairing-flow tests. Construction
/// and the handshake sequence follow <see cref="SendspinClientServicePairingTests"/> — read
/// that file before changing this one.
/// </summary>
/// <remarks>
/// This surface is binding across the whole pairing-window plan, not just the three tests
/// above: later tasks call every member here, including ones exercised by no test in this
/// file yet. Two constructor parameters are accepted for forward signature-compatibility but
/// are not wired to anything yet, because there is nowhere for them to go: <c>window</c>
/// (nothing reads <see cref="PairingWindow"/> — it is itself a placeholder type — and
/// <c>SendspinClientOptions</c> has no <c>PairingWindow</c> property) and <c>attemptTimeout</c>
/// (<c>SendspinClientOptions</c> has no attempt-timeout option). A later task in the plan adds
/// both and should wire them through here at that point.
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
    /// defaults to a fresh <see cref="InMemoryPinLockoutStore"/>. <paramref name="management"/>
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

        // The SDK's own PresentPinAsync is still string-only until a later task changes it to
        // take PinPresentation directly; this bridges the two shapes so the harness's own
        // surface can already be PinPresentation-based. It also records every invocation
        // (see _presentations) independent of whether the wrapped delegate ever completes.
        Func<string, CancellationToken, ValueTask>? adaptedPresenter = dynamicPin
            ? async (pin, ct) =>
              {
                  var presentation = new PinPresentation(pin, null);
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

        var deadline = DateTime.UtcNow + DefaultTimeout;
        while (true)
        {
            lock (_presentations)
            {
                if (_presentations.Count > _consumedPresentations)
                {
                    _consumedPresentations++;
                    return;
                }
            }

            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail("Timed out waiting for the dynamic-PIN presenter to be invoked.");
            }

            await Task.Delay(10);
        }
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

    public async ValueTask DisposeAsync() => await Client.DisposeAsync();

    private static string B64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
