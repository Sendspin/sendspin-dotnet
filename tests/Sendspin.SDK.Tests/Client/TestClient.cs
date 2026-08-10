using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Audio.Source;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Connection.Noise.Pairing;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Tests.Client;

/// <summary>Mutable <see cref="INoiseSessionInfo"/> stand-in for tests.</summary>
internal sealed class FakeNoiseSession : INoiseSessionInfo
{
    internal const string FakeServerId = "GFsV9tLaSQm9HcFWpKsgYQOr7wFTvNUtkmFwuVz3zoo";

    public string? ServerId { get; set; } = FakeServerId;
    public NoisePsk? MatchedPsk { get; set; }
    public ReadOnlyMemory<byte>? HandshakeHash { get; set; } = new byte[32];
}

/// <summary>
/// Mutable mirror of <see cref="SendspinClientOptions"/> for the
/// <see cref="TestClient.Create"/> configure callback.
/// </summary>
/// <remarks>
/// <see cref="SendspinClientOptions"/> uses <c>init</c> accessors, which a callback cannot
/// assign (CS8852), so tests mutate this draft and <see cref="TestClient.Create"/> copies it
/// into the real options in an object initializer. Property names match one-for-one.
/// </remarks>
internal sealed class TestClientOptions
{
    public SendspinIdentity Identity { get; set; } = SendspinIdentity.Generate();

    public IPairingRecordStore? PairingRecordStore { get; set; }

    public ClientCapabilities Capabilities { get; set; } = new();

    public NoiseCipherSuite Suite { get; set; } = NoiseCipherSuite.ChaChaPoly;

    public IClockSynchronizer? ClockSynchronizer { get; set; }

    public IAudioPipeline? AudioPipeline { get; set; }

    public IStaticDelayStore? StaticDelayStore { get; set; }

    public IPinLockoutStore? PinLockoutStore { get; set; }

    public Func<PinPresentation, CancellationToken, ValueTask>? PresentPinAsync { get; set; }

    public IAudioCaptureDevice? CaptureDevice { get; set; }

    public ISourceAudioEncoderFactory? SourceEncoderFactory { get; set; }
}

/// <summary>
/// Builds a client over the encrypted protocol with a fake Noise session.
/// </summary>
/// <remarks>
/// The default is a paired, long-term-keyed session (trust <c>user</c>) — the normal
/// operating state — so role tests are not incidentally blocked by the server/activate
/// admissibility table or the source@v1 trust gate. Tests that exercise gating pass
/// <see cref="PskCategory.Sentinel"/> explicitly.
/// </remarks>
internal static class TestClient
{
    /// <param name="category">Category of the PSK the fake session reports as matched.</param>
    /// <param name="unpairedAccess">
    /// Advertised unpaired-access flag. Applied after <paramref name="configure"/> runs, so a
    /// callback that replaces <see cref="TestClientOptions.Capabilities"/> cannot drop it.
    /// </param>
    /// <param name="configure">Mutates the options draft before the client is constructed.</param>
    /// <param name="connected">
    /// Whether the fake connection is dialled before the client is built (the default). Pass
    /// false only for tests whose subject is behavior while disconnected — the client drops
    /// received frames in that state, so no message can be delivered to an unconnected fake.
    /// </param>
    internal static (SendspinClientService Client, FakeSendspinConnection Connection, FakeNoiseSession Session)
        Create(
            PskCategory category = PskCategory.LongTerm,
            bool unpairedAccess = false,
            Action<TestClientOptions>? configure = null,
            bool connected = true)
    {
        var connection = new FakeSendspinConnection();
        var session = new FakeNoiseSession
        {
            MatchedPsk = new NoisePsk(NoiseConstants.SentinelPsk.ToArray(), category),
        };

        var draft = new TestClientOptions
        {
            Capabilities = new ClientCapabilities { UnpairedAccessEnabled = unpairedAccess },
        };
        configure?.Invoke(draft);

        if (unpairedAccess)
        {
            draft.Capabilities.UnpairedAccessEnabled = true;
        }

        var options = new SendspinClientOptions
        {
            Identity = draft.Identity,
            PairingRecordStore = draft.PairingRecordStore,
            Capabilities = draft.Capabilities,
            Suite = draft.Suite,
            ClockSynchronizer = draft.ClockSynchronizer,
            AudioPipeline = draft.AudioPipeline,
            StaticDelayStore = draft.StaticDelayStore,
            PinLockoutStore = draft.PinLockoutStore,
            PresentPinAsync = draft.PresentPinAsync,
            CaptureDevice = draft.CaptureDevice,
            SourceEncoderFactory = draft.SourceEncoderFactory,
        };

        // Connected before the client subscribes, so no state-changed event is delivered and
        // tests that dial explicitly are unaffected. The client drops frames received while
        // the connection is Disconnected/Disconnecting, so a fake that never connected would
        // silently swallow every RaiseTextMessageReceived.
        if (connected)
        {
            connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        }

        var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection,
            session,
            options);

        return (client, connection, session);
    }

    /// <summary>
    /// Drives the encrypted handshake to completion: server/hello, then a server/activate
    /// granting playback and the given roles. Use in tests that need a connected client.
    /// </summary>
    internal static void CompleteHandshake(
        FakeSendspinConnection connection,
        params string[] activeRoles)
    {
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""
            {"type":"server/hello","payload":{"name":"srv"}}
            """);

        string roles = string.Join(",", activeRoles.Select(r => $"\"{r}\""));
        connection.RaiseTextMessageReceived(
            $$$"""
            {"type":"server/activate","payload":{"activities":["playback"],"active_roles":[{{{roles}}}]}}
            """);
    }
}

/// <summary>
/// Clock synchronizer that reports converged from construction and stays converged across the
/// per-connection <see cref="Reset"/> in the client's activate handler. For tests whose subject
/// is not the clock-sync gate on the initial client/state: injecting this makes the client see
/// sync as already established at activate, so the initial state is sent immediately —
/// <c>InitialClientStateGatingTests</c> covers the deferred path itself.
/// </summary>
internal sealed class ConvergedClockSynchronizer : IClockSynchronizer
{
    public bool IsConverged => true;

    public bool HasMinimalSync => true;

    public double StaticDelayMs { get; set; }

    /// <summary>
    /// Offset applied in conversions, following KalmanClockSynchronizer's convention:
    /// offset = server_time − client_time. Defaults to 0, which maps the two domains
    /// identically — convenient, but it makes a caller that forgets to convert
    /// indistinguishable from one that converts correctly. A test whose subject IS the
    /// conversion should set a non-zero value so the difference is observable.
    /// </summary>
    public long OffsetMicroseconds { get; set; }

    // Deliberately does NOT apply StaticDelayMs, unlike FakeClockSynchronizer. Existing users
    // of this fake schedule playback through it, and folding the static delay in here shifts
    // every scheduled start — enough to hang tests waiting on audio that now arrives at a
    // different time. The offset alone is what this fake needed to become useful.
    public long ServerToClientTime(long serverTime) => serverTime - OffsetMicroseconds;

    public long ClientToServerTime(long clientTime) => clientTime + OffsetMicroseconds;

    public void ProcessMeasurement(long t1, long t2, long t3, long t4)
    {
    }

    public void Reset()
    {
    }

    public ClockSyncStatus GetStatus() => new() { IsConverged = true };
}
