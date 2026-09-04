using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Store double with a fixed capacity, for exercising the eviction paths of spec #183 without
/// filling a real store. Delegates the eviction policy itself to the shipped helper by simply
/// honouring <see cref="Capacity"/>; the SDK is what must make room before it writes.
/// </summary>
internal sealed class BoundedPairingRecordStore : IPairingRecordStore
{
    private readonly Dictionary<string, PairingRecord> _records;

    /// <param name="capacity">The number of records the store holds.</param>
    /// <param name="seed">Records the store starts out holding.</param>
    public BoundedPairingRecordStore(int capacity, params PairingRecord[] seed)
    {
        Capacity = capacity;
        _records = seed.ToDictionary(r => r.PskId);
    }

    public int Capacity { get; }

    /// <summary>Every <c>psk_id</c> ever passed to <see cref="Upsert"/>, in order.</summary>
    public List<string> UpsertedPskIds { get; } = new();

    public IReadOnlyList<PairingRecord> List() => _records.Values.ToList();

    public void Upsert(PairingRecord record)
    {
        UpsertedPskIds.Add(record.PskId);

        // Deliberately throws rather than silently overflowing: the SDK is required to make
        // room first, and a test that stopped exercising that should fail loudly.
        if (!_records.ContainsKey(record.PskId) && _records.Count >= Capacity)
        {
            throw new InvalidOperationException(
                $"Upsert of a new record at capacity {Capacity}; the caller did not evict first.");
        }

        _records[record.PskId] = record;
    }

    public void Remove(string pskId) => _records.Remove(pskId);
}

/// <summary>Mutable <see cref="INoiseSessionInfo"/> stand-in for tests.</summary>
internal sealed class FakeNoiseSession : INoiseSessionInfo
{
    internal const string FakeServerId = "GFsV9tLaSQm9HcFWpKsgYQOr7wFTvNUtkmFwuVz3zoo";

    public string? ServerId { get; set; } = FakeServerId;
    public NoisePsk? MatchedPsk { get; set; }
    public ReadOnlyMemory<byte>? HandshakeHash { get; set; } = new byte[32];
    public NoiseCipherSuite Suite { get; set; } = NoiseCipherSuite.ChaChaPoly;
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
    /// callback that replaces <see cref="SendspinClientOptions.Capabilities"/> cannot drop it —
    /// which <c>PairingQuiescenceTests</c> depends on, since it does exactly that.
    /// <para>
    /// It is applied by <b>writing to</b> that <see cref="ClientCapabilities"/> instance, not to
    /// a copy. Pass a fresh instance from <paramref name="configure"/> (every call site does)
    /// rather than one held in a local and shared between two <c>Create</c> calls — the second
    /// call would otherwise turn the flag on for the first client too. Copying instead was
    /// considered and rejected: <see cref="ClientCapabilities"/> is a class with no copy
    /// mechanism, so it would mean either hand-mirroring twenty-odd properties — the failure
    /// mode <see cref="SendspinClientOptions"/> was made a record to end (#95) — or changing a
    /// public type's semantics for a test helper's benefit (#99).
    /// </para>
    /// </param>
    /// <param name="configure">
    /// Returns the options to build the client from, given the defaults. Take a variant with
    /// <c>with</c>: <c>o => o with { PairingWindow = window }</c>.
    /// </param>
    /// <param name="connected">
    /// Whether the fake connection is dialled before the client is built (the default). Pass
    /// false only for tests whose subject is behavior while disconnected — the client drops
    /// received frames in that state, so no message can be delivered to an unconnected fake.
    /// </param>
    /// <param name="logger">
    /// Logger the client is built with; <see cref="NullLogger{T}"/> by default. Pass a
    /// <see cref="CapturingLogger{T}"/> for a test whose subject is a diagnostic's text or
    /// level — several client rejections are reported to the server as a bare "invalid",
    /// so the log line is the only place the reason is observable (#110).
    /// </param>
    internal static (SendspinClientService Client, FakeSendspinConnection Connection, FakeNoiseSession Session)
        Create(
            PskCategory category = PskCategory.LongTerm,
            bool unpairedAccess = false,
            Func<SendspinClientOptions, SendspinClientOptions>? configure = null,
            bool connected = true,
            ILogger<SendspinClientService>? logger = null)
    {
        var connection = new FakeSendspinConnection();

        // The default pairs the SENTINEL PSK bytes with whatever category is asked for, which
        // for the LongTerm default is a combination production can never produce — a long-term
        // PSK is a paired secret, never the sentinel. Harmless because the category is all any
        // current test reads, and every test that derives a psk_id overrides MatchedPsk with a
        // real one. A future test deriving a psk_id from this default would be asserting
        // against a fiction, so give it real bytes rather than reusing this (#99).
        var session = new FakeNoiseSession
        {
            MatchedPsk = new NoisePsk(NoiseConstants.SentinelPsk.ToArray(), category),
        };

        var options = new SendspinClientOptions
        {
            Identity = SendspinIdentity.Generate(),
            // Pinned rather than platform-selected: these tests assert on wire bytes.
            Suite = NoiseCipherSuite.ChaChaPoly,
            Capabilities = new ClientCapabilities { UnpairedAccessEnabled = unpairedAccess },
        };

        options = configure?.Invoke(options) ?? options;

        if (unpairedAccess)
        {
            // Mutates whichever ClientCapabilities is in effect, which is the caller's own
            // instance when `configure` supplied one — see this parameter's doc comment for
            // why that is left as it is rather than copied (#99).
            options.Capabilities.UnpairedAccessEnabled = true;
        }

        // Connected before the client subscribes, so no state-changed event is delivered and
        // tests that dial explicitly are unaffected. The client drops frames received while
        // the connection is Disconnected/Disconnecting, so a fake that never connected would
        // silently swallow every RaiseTextMessageReceived.
        if (connected)
        {
            connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        }

        var client = new SendspinClientService(
            logger ?? NullLogger<SendspinClientService>.Instance,
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

    public double OutputDelayMs { get; set; }

    /// <summary>
    /// Offset applied in conversions, following KalmanClockSynchronizer's convention:
    /// offset = server_time − client_time. Defaults to 0, which maps the two domains
    /// identically — convenient, but it makes a caller that forgets to convert
    /// indistinguishable from one that converts correctly. A test whose subject IS the
    /// conversion should set a non-zero value so the difference is observable.
    /// </summary>
    public long OffsetMicroseconds { get; set; }

    // Deliberately does NOT apply OutputDelayMs, unlike FakeClockSynchronizer. Existing users
    // of this fake schedule playback through it, and folding the output delay in here shifts
    // every scheduled start — enough to hang tests waiting on audio that now arrives at a
    // different time. The offset alone is what this fake needed to become useful — which also
    // leaves the uncompensated conversion identical to it, there being no delay to leave out.
    public long ServerToClientTime(long serverTime) => serverTime - OffsetMicroseconds;

    public long ServerToClientTimeUncompensated(long serverTime) => serverTime - OffsetMicroseconds;

    public long ClientToServerTime(long clientTime) => clientTime + OffsetMicroseconds;

    public void ProcessMeasurement(long t1, long t2, long t3, long t4)
    {
    }

    /// <summary>
    /// Calls to <see cref="Reset"/>. This fake stays converged across a reset, so the count is
    /// the only way to see one — which matters when the synchronizer is shared between
    /// connections and a reset belonging to one of them would corrupt the other (#253).
    /// </summary>
    public int ResetCount { get; private set; }

    public void Reset() => ResetCount++;

    public ClockSyncStatus GetStatus() => new() { IsConverged = true };
}
