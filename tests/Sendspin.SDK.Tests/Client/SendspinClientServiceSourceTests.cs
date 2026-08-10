using System.Buffers.Binary;
using Sendspin.SDK.Audio.Source;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Coverage for the source (line-in) role: server-driven start/stop, client_stream
/// framing, server-domain chunk timestamps (type 12), line-sense reporting, trust
/// gating, and role deactivation.
/// </summary>
public class SendspinClientServiceSourceTests
{
    private const string ServerId = FakeNoiseSession.FakeServerId;

    /// <summary>
    /// Deliberately non-zero, and deliberately not a round number. The source spec requires
    /// chunk timestamps in the SERVER time domain; a clock that maps client to server
    /// identically cannot distinguish a pipeline that converts from one that ships the raw
    /// capture time, so the fixture injects a real offset.
    /// </summary>
    private const long ClockOffsetUs = 1_234_567;

    private static (SendspinClientService, FakeSendspinConnection, FakeCaptureDevice) CreateSourceClient(
        bool lineSense = false, PskCategory trust = PskCategory.LongTerm, bool unpairedAccess = false)
    {
        var capture = new FakeCaptureDevice();
        var caps = new ClientCapabilities
        {
            Roles = { "source@v1" },
            SourceRoleSupport = new SourceRoleSupport { LineSense = lineSense },
            UnpairedAccessEnabled = unpairedAccess,
        };
        var (client, connection, session) = TestClient.Create(
            trust,
            unpairedAccess,
            configure: options =>
            {
                options.Capabilities = caps;
                options.CaptureDevice = capture;

                // Clock already converged: source@v1 requires sync, so the initial client/state
                // (and with it the line-sense reporting gate) would otherwise stay deferred —
                // InitialClientStateGatingTests owns the deferred path. The offset is what
                // makes the server-domain conversion observable; see ClockOffsetUs.
                options.ClockSynchronizer = new ConvergedClockSynchronizer
                {
                    OffsetMicroseconds = ClockOffsetUs,
                };
            });

        // The source trust gate reads the matched PSK, which is bound to the server id.
        session.MatchedPsk = new NoisePsk(NoiseConstants.SentinelPsk.ToArray(), trust, ServerId);
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        return (client, connection, capture);
    }

    private static void Activate(FakeSendspinConnection c, string roles = """["source@v1"]""") =>
        c.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["playback"],"active_roles":ROLES}}"""
                .Replace("ROLES", roles));

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {because}");
            await Task.Delay(10);
        }
    }

    [Fact]
    public void Hello_AdvertisesSourceSupport_WithLineSense()
    {
        var (client, connection, _) = CreateSourceClient(lineSense: true);
        using var _c = client;

        var hello = connection.SentMessages.OfType<ClientHelloMessage>().Single();
        Assert.Contains("source@v1", hello.Payload.SupportedRoles);
        Assert.NotNull(hello.Payload.SourceV1Support);
        Assert.True(hello.Payload.SourceV1Support!.Features!.LineSense);
    }

    [Fact]
    public async Task SourceStart_SendsClientStreamStart_ThenTimestampedChunks()
    {
        var (client, connection, capture) = CreateSourceClient();
        using var _c = client;
        Activate(connection);

        // Default is stop: no streaming until the server says start.
        Assert.False(capture.Capturing);
        capture.Emit([1, 2, 3, 4], 1000);
        Assert.DoesNotContain(connection.SentMessages, m => m is ClientStreamStartMessage);

        connection.RaiseTextMessageReceived("""{"type":"server/command","payload":{"source":{"command":"start"}}}""");
        Assert.True(capture.Capturing);
        var start = connection.SentMessages.OfType<ClientStreamStartMessage>().Single();
        Assert.Equal("pcm", start.Payload.Source.Codec);
        Assert.Equal(48000, start.Payload.Source.SampleRate);

        // A captured buffer becomes a type-12 chunk: [12][int64 BE server ts][pcm].
        // Chunks are framed by the pipeline's consumer task, so the send is asynchronous.
        byte[] pcm = [0x11, 0x22, 0x33, 0x44];
        capture.Emit(pcm, captureTimeUs: 5000);
        await WaitUntilAsync(() => connection.SnapshotSentBinary().Count == 1, "the captured buffer to be framed and sent");
        byte[] chunk = connection.SnapshotSentBinary().Last();
        Assert.Equal(12, chunk[0]);
        // The chunk timestamp is the capture instant mapped into the SERVER domain. The old
        // assertion here compared serverTs against itself through a ternary whose branches
        // were identical, so it passed unconditionally and this spec MUST was untested. With
        // a non-zero clock offset, a pipeline that shipped the raw capture time now fails.
        // (The offset+drift arithmetic itself belongs to KalmanClockSynchronizer and is
        // pinned by ClientToServerTime_AppliesOffsetAndDrift.)
        long serverTs = BinaryPrimitives.ReadInt64BigEndian(chunk.AsSpan(1, 8));
        Assert.Equal(5000 + ClockOffsetUs, serverTs);

        Assert.Equal(pcm, chunk[9..]);
    }

    [Fact]
    public async Task SourceStop_EndsStream_AndCeasesChunks()
    {
        var (client, connection, capture) = CreateSourceClient();
        using var _c = client;
        Activate(connection);
        connection.RaiseTextMessageReceived("""{"type":"server/command","payload":{"source":{"command":"start"}}}""");

        connection.RaiseTextMessageReceived("""{"type":"server/command","payload":{"source":{"command":"stop"}}}""");
        Assert.False(capture.Capturing);

        // The stop drains the chunk consumer before ending the stream, so the end message
        // lands asynchronously.
        await WaitUntilAsync(
            () => connection.SnapshotSentMessages().Any(m => m is ClientStreamEndMessage),
            "client_stream/end after the stop");

        int binaryBefore = connection.SnapshotSentBinary().Count;
        capture.Emit([9, 9], 6000);
        Assert.Equal(binaryBefore, connection.SnapshotSentBinary().Count); // no chunk after stop
    }

    [Fact]
    public async Task AvailabilityGoingFalseWhileStreaming_EndsTheStreamBeforeReportingUnavailable()
    {
        // The server rejects chunks whenever the client is not available, and it treats
        // client_stream/end as an implicit stop. So losing availability has to close the
        // input stream first: a client/state carrying available: false ahead of the end
        // leaves the server holding an open stream it has already decided to reject audio
        // for, and the chunks still in flight behind the drain are exactly that audio.
        var (client, connection, capture) = CreateSourceClient();
        using var _c = client;
        Activate(connection);
        connection.RaiseTextMessageReceived("""{"type":"server/command","payload":{"source":{"command":"start"}}}""");
        Assert.True(capture.Capturing);

        await client.EnterExternalSourceAsync();

        var wire = connection.SnapshotSentMessages().ToList();
        int end = wire.FindIndex(m => m is ClientStreamEndMessage);
        int unavailable = wire.FindIndex(m => m is ClientStateMessage { Payload.Available: false });

        Assert.True(end >= 0, "losing availability must end the open input stream");
        Assert.True(unavailable >= 0, "the availability drop must still be reported");
        Assert.True(end < unavailable, "client_stream/end must precede the client/state carrying available: false");
        Assert.False(capture.Capturing, "the capture device must actually close, not merely be announced closed");

        // Regaining availability is not a start: the server is the only initiator, so
        // nothing re-announces and no second end goes out. Without this an implementation
        // that ended the stream on every availability publish would pass the ordering above.
        await client.ExitExternalSourceAsync();
        Assert.Single(connection.SnapshotSentMessages().OfType<ClientStreamEndMessage>());
        Assert.Single(connection.SnapshotSentMessages().OfType<ClientStreamStartMessage>());
        Assert.False(capture.Capturing);
    }

    [Fact]
    public async Task AvailabilityGoingFalseWithNoOpenStream_SendsNoClientStreamEnd()
    {
        // Nothing was started, so there is no stream to end. An unconditional end here
        // would be an end for a stream the server never saw opened.
        var (client, connection, capture) = CreateSourceClient();
        using var _c = client;
        Activate(connection);
        Assert.False(capture.Capturing);

        await client.EnterExternalSourceAsync();

        Assert.DoesNotContain(connection.SnapshotSentMessages(), m => m is ClientStreamEndMessage);
        Assert.Contains(connection.SnapshotSentMessages(), m => m is ClientStateMessage { Payload.Available: false });
    }

    [Fact]
    public async Task RoleDeactivation_StopsStreaming()
    {
        var (client, connection, capture) = CreateSourceClient();
        using var _c = client;
        Activate(connection);
        connection.RaiseTextMessageReceived("""{"type":"server/command","payload":{"source":{"command":"start"}}}""");
        Assert.True(capture.Capturing);

        // A later activate that drops source@v1 ends streaming.
        Activate(connection, roles: "[]");
        Assert.False(capture.Capturing);
        await WaitUntilAsync(
            () => connection.SnapshotSentMessages().Any(m => m is ClientStreamEndMessage),
            "client_stream/end after the role deactivation");
    }

    [Fact]
    public async Task ReconnectAfterStart_DoesNotResumeStreaming_UntilTheServerStartsAgain()
    {
        var (client, connection, capture) = CreateSourceClient();
        using var _c = client;
        Activate(connection);
        connection.RaiseTextMessageReceived("""{"type":"server/command","payload":{"source":{"command":"start"}}}""");
        Assert.True(capture.Capturing);

        // The connection drops and the socket auto-reconnects. Streaming state is
        // per-connection (spec): the old connection's start must not survive into
        // the new one — and given #123, a post-pairing promote looks exactly like
        // this reconnect.
        connection.SimulateConnectionLoss();
        await WaitUntilAsync(() => !capture.Capturing, "the per-connection streaming reset after the connection drop");

        // Encrypted flow: the server speaks first, the client answers with its hello,
        // and the reconnect handshake completes on the activate.
        connection.SimulateReconnected();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        await WaitUntilAsync(
            () => connection.SnapshotSentMessages().OfType<ClientHelloMessage>().Count() == 2,
            "the reconnect handshake's client/hello");
        int announcesBefore = connection.SnapshotSentMessages().OfType<ClientStreamStartMessage>().Count();
        Activate(connection);

        // Nothing streams on the strength of the previous connection's start: no
        // capture, no unsolicited client_stream/start (a protocol error the server
        // should close on), and a captured buffer goes nowhere.
        Assert.False(capture.Capturing);
        Assert.Equal(announcesBefore, connection.SnapshotSentMessages().OfType<ClientStreamStartMessage>().Count());
        int binaryBefore = connection.SnapshotSentBinary().Count;
        capture.Emit([5, 5], 7000);
        Assert.Equal(binaryBefore, connection.SnapshotSentBinary().Count);

        // No client_stream/end either: the old stream died with its connection, and
        // this connection never opened one to end.
        Assert.DoesNotContain(connection.SnapshotSentMessages(), m => m is ClientStreamEndMessage);

        // Positive control: a fresh start on the new connection streams. Without
        // this, an implementation that never streams would pass everything above.
        connection.RaiseTextMessageReceived("""{"type":"server/command","payload":{"source":{"command":"start"}}}""");
        await WaitUntilAsync(() => capture.Capturing, "capture to open after the fresh start");
        Assert.Equal(announcesBefore + 1, connection.SnapshotSentMessages().OfType<ClientStreamStartMessage>().Count());
        capture.Emit([6, 6], 8000);
        await WaitUntilAsync(
            () => connection.SnapshotSentBinary().Count == binaryBefore + 1,
            "a chunk to flow after the fresh start");
    }

    [Fact]
    public async Task StreamingPermittedBeforeReconnect_IsNotHonouredBeforeTheNewSessionsHelloArrives()
    {
        // HandleServerActivate mirrors active_roles into LastServerHello.ActiveRoles, which
        // is the other half of IsSourceStreamingPermitted's gate (the trust half is covered
        // by the tests below). SendHandshakeAsync clears LastServerActivate as soon as the
        // reconnect handshake begins, but before this fix left the mirror standing until the
        // new session's own server/hello happened to replace LastServerHello wholesale.
        // OnTextMessageReceived drops nothing while Handshaking (only on Disconnected or
        // Disconnecting), so a peer that sends server/command before its own server/hello —
        // a spec violation this client must not reward — could ride the stale mirror to
        // stream on the strength of the PREVIOUS session's grant. Deliberately sends no
        // server/hello before the server/command: unlike
        // ReconnectAfterStart_DoesNotResumeStreaming_UntilTheServerStartsAgain above, whose
        // server/hello (with no active_roles) would itself replace LastServerHello and so
        // could not distinguish a correct implementation from a broken one here.
        var (client, connection, capture) = CreateSourceClient();
        using var _c = client;
        Activate(connection);

        // Positive control: streaming is genuinely permitted on this session.
        connection.RaiseTextMessageReceived("""{"type":"server/command","payload":{"source":{"command":"start"}}}""");
        Assert.True(capture.Capturing);

        connection.SimulateConnectionLoss();
        await WaitUntilAsync(() => !capture.Capturing, "the per-connection streaming reset after the connection drop");

        // The reconnect handshake begins synchronously here — SendHandshakeAsync resets
        // LastServerActivate (and, with the fix, the ActiveRoles mirror) before any message
        // from the new session has arrived.
        connection.SimulateReconnected();

        // No server/hello on the new session yet — the command lands straight into the gap
        // the finding describes.
        connection.RaiseTextMessageReceived("""{"type":"server/command","payload":{"source":{"command":"start"}}}""");

        // The source command dispatch is fire-and-forget (HandleServerCommand ->
        // SafeFireAndForget), so give a wrongly-honoured start ample time to actually open
        // the capture device before asserting it never did.
        await Task.Delay(300);
        Assert.False(capture.Capturing,
            "streaming must not resume from a stale LastServerHello.ActiveRoles before this session's own server/hello, let alone its activate");
    }

    [Fact]
    public async Task StreamingPermittedBeforeAnInBandRekey_IsNotHonouredAfterItWithNoActivate()
    {
        // The in-band twin of the test above: after a re-key there is no bounding
        // server/hello at all (unlike a reconnect), so without DetectSessionRekey also
        // clearing the ActiveRoles mirror, a LongTerm-to-LongTerm re-key would carry the
        // source@v1 grant forward indefinitely rather than just until the next reconnect.
        var capture = new FakeCaptureDevice();
        var (client, connection, session) = TestClient.Create(
            PskCategory.LongTerm,
            configure: options =>
            {
                options.Capabilities = new ClientCapabilities { Roles = ["source@v1"] };
                options.CaptureDevice = capture;
                options.ClockSynchronizer = new ConvergedClockSynchronizer();
            });
        using var _c = client;
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        Activate(connection);

        // Positive control: streaming is genuinely permitted on the pre-rekey session.
        connection.RaiseTextMessageReceived("""{"type":"server/command","payload":{"source":{"command":"start"}}}""");
        Assert.True(capture.Capturing);
        connection.RaiseTextMessageReceived("""{"type":"server/command","payload":{"source":{"command":"stop"}}}""");

        // The stop's own drain is fire-and-forget too; wait for its client_stream/end so the
        // pipeline's serial command chain is settled before the next command is enqueued —
        // otherwise a stray "start" queued right behind an unsettled "stop" could still be
        // pending, not yet refused, when the assertion below runs.
        await WaitUntilAsync(
            () => connection.SnapshotSentMessages().Any(m => m is ClientStreamEndMessage),
            "client_stream/end after the stop");
        Assert.False(capture.Capturing);

        // An in-band re-key installs a fresh handshake hash. Nothing else about the
        // connection changes — this is the same WebSocket.
        session.HandshakeHash = Enumerable.Repeat((byte)0xAB, 32).ToArray();

        connection.RaiseTextMessageReceived("""{"type":"server/command","payload":{"source":{"command":"start"}}}""");

        // Fire-and-forget dispatch again: give a wrongly-honoured start ample time to
        // actually open the capture device before asserting it never did.
        await Task.Delay(300);
        Assert.False(capture.Capturing,
            "streaming must not resume from a stale LastServerHello.ActiveRoles after an in-band re-key with no new activate");
    }

    [Fact]
    public void SourceActivatedWithoutUserTrust_ClosesUnauthorized()
    {
        // Unpaired access on: playback+roles is otherwise admissible, so the source
        // trust gate (not the general PSK gate) is what refuses source@v1 at trust none.
        var (client, connection, _) = CreateSourceClient(trust: PskCategory.Sentinel, unpairedAccess: true);
        using var _c = client;

        Activate(connection);

        Assert.Equal(Sendspin.SDK.Connection.ConnectionState.Disconnected, connection.State);
        Assert.Equal("unauthorized", connection.LastDisconnectReason);
    }

    [Fact]
    public async Task LineSense_ReportsSignalInClientState()
    {
        var (client, connection, _) = CreateSourceClient(lineSense: true);
        using var _c = client;
        Activate(connection);

        // Through the interface: #112 — an app coded against ISendspinClient could already
        // advertise SourceRoleSupport.LineSense in hello, but had no way to report the
        // signal itself.
        ISendspinClient asInterface = client;
        await asInterface.SetSourceSignalAsync(present: true);

        var state = connection.SentMessages.OfType<ClientStateMessage>()
            .Last(m => m.Payload.Source is not null);
        Assert.Equal("present", state.Payload.Source!.Signal);
    }

    /// <summary>
    /// Builds a source-capable client, drives the encrypted handshake, and optionally
    /// activates the source role — so a test can reach the server/command path with the
    /// role either active or inactive, at either trust level.
    /// </summary>
    private static (SendspinClientService Client, FakeSendspinConnection Connection, FakeCaptureDevice Capture)
        CreateSourceClient(PskCategory category, bool activateSourceRole)
    {
        var capture = new FakeCaptureDevice();
        var (client, connection, _) = TestClient.Create(
            category,
            configure: options =>
            {
                options.CaptureDevice = capture;
                options.Capabilities = new ClientCapabilities { Roles = ["source@v1"] };
            });
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");

        // Empty activities deliberately. A Sentinel-keyed session granted ["playback"]
        // is INADMISSIBLE without unpaired access, so the client would disconnect with
        // 'pairing_required' before the server/command ever arrived and the test would
        // fail for the wrong reason. An empty set is admissible on both PSK categories
        // (and on LongTerm with an active role, via the withPlayback extension in
        // IsAdmissible) — and it is the more faithful attack shape, since #75 describes
        // a server that omits the role entirely and just sends the command.
        string roles = activateSourceRole ? "\"source@v1\"" : string.Empty;
        connection.RaiseTextMessageReceived(
            $$$"""
            {"type":"server/activate","payload":{"activities":[],"active_roles":[{{{roles}}}]}}
            """);

        return (client, connection, capture);
    }

    private static void SendSourceStart(FakeSendspinConnection connection) =>
        connection.RaiseTextMessageReceived(
            """{"type":"server/command","payload":{"source":{"command":"start"}}}""");

    [Fact]
    public void SourceStart_AtTrustNone_NeverOpensTheCaptureDevice()
    {
        // #75: a Sentinel-keyed server must not be able to start capture via
        // server/command, bypassing the activate-time trust gate entirely.
        var (client, connection, capture) = CreateSourceClient(PskCategory.Sentinel, activateSourceRole: false);
        using var _c = client;

        SendSourceStart(connection);

        Assert.False(capture.Capturing, "the capture device must never open at trust 'none'");
        Assert.DoesNotContain(connection.SentMessages, m => m is ClientStreamStartMessage);
    }

    [Fact]
    public void SourceStart_WithSourceRoleInactive_NeverOpensTheCaptureDevice()
    {
        // Even at user trust, a source that was never activated must not stream.
        var (client, connection, capture) = CreateSourceClient(PskCategory.LongTerm, activateSourceRole: false);
        using var _c = client;

        SendSourceStart(connection);

        Assert.False(capture.Capturing, "the capture device must never open with the role inactive");
    }

    [Fact]
    public void SourceStart_AtUserTrustWithActiveRole_OpensTheCaptureDevice()
    {
        // Positive control: a gate that refuses everything would pass both tests above.
        var (client, connection, capture) = CreateSourceClient(PskCategory.LongTerm, activateSourceRole: true);
        using var _c = client;

        SendSourceStart(connection);

        Assert.True(capture.Capturing, "the legitimate case must still stream");
    }

    [Fact]
    public void SourceStart_AtSentinelTrustWithRoleGrantedOnlyInHello_NeverOpensTheCaptureDevice()
    {
        // The three tests above never exercise the trust half of IsSourceStreamingPermitted
        // independently of the role half — a predicate that dropped the trust check and kept
        // only the role check would still pass all three. This test reaches (Sentinel trust,
        // source role active) by a route that never touches server/activate's role list:
        // ServerHelloPayload.ActiveRoles is a plain deserialized field, so a Sentinel-keyed
        // server can grant source@v1 in server/hello, then send an activate that OMITS
        // active_roles entirely. HandleServerActivate only overwrites LastServerHello.ActiveRoles
        // when the activate payload carries the field, so the hello's grant survives, and the
        // activate-time admissibility check (keyed off the activate payload's own active_roles,
        // not the mirrored hello) never sees it either.
        var capture = new FakeCaptureDevice();
        var (client, connection, _) = TestClient.Create(
            PskCategory.Sentinel,
            configure: options =>
            {
                options.CaptureDevice = capture;
                options.Capabilities = new ClientCapabilities { Roles = ["source@v1"] };
            });
        using var _c = client;
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived(
            """{"type":"server/hello","payload":{"name":"srv","active_roles":["source@v1"]}}""");
        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":[]}}""");

        // Confirms the route actually reaches server/command rather than disconnecting first.
        Assert.NotEqual(Sendspin.SDK.Connection.ConnectionState.Disconnected, connection.State);

        SendSourceStart(connection);

        Assert.False(capture.Capturing,
            "the capture device must never open at Sentinel trust, even when source@v1 was granted via server/hello rather than server/activate");
    }
}
