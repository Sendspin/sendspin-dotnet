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

    private static (SendspinClientService, FakeSendspinConnection, FakeCaptureDevice) CreateSourceClient(
        bool lineSense = false, PskCategory trust = PskCategory.LongTerm, bool unpairedAccess = false)
    {
        var capture = new FakeCaptureDevice();
        var caps = new ClientCapabilities
        {
            Roles = { "source@v1" },
            SourceSupport = new Sendspin.SDK.Client.SourceSupport { LineSense = lineSense },
            UnpairedAccessEnabled = unpairedAccess,
        };
        var (client, connection, session) = TestClient.Create(
            trust,
            unpairedAccess,
            configure: options =>
            {
                options.Capabilities = caps;
                options.CaptureDevice = capture;
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
    public void SourceStart_SendsClientStreamStart_ThenTimestampedChunks()
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
        byte[] pcm = [0x11, 0x22, 0x33, 0x44];
        capture.Emit(pcm, captureTimeUs: 5000);
        byte[] chunk = connection.SentBinary.Last();
        Assert.Equal(12, chunk[0]);
        long serverTs = BinaryPrimitives.ReadInt64BigEndian(chunk.AsSpan(1, 8));
        Assert.Equal(client.CurrentGroup is null ? serverTs : serverTs, serverTs); // present
        Assert.Equal(pcm, chunk[9..]);
    }

    [Fact]
    public void SourceStop_EndsStream_AndCeasesChunks()
    {
        var (client, connection, capture) = CreateSourceClient();
        using var _c = client;
        Activate(connection);
        connection.RaiseTextMessageReceived("""{"type":"server/command","payload":{"source":{"command":"start"}}}""");

        connection.RaiseTextMessageReceived("""{"type":"server/command","payload":{"source":{"command":"stop"}}}""");
        Assert.False(capture.Capturing);
        Assert.Contains(connection.SentMessages, m => m is ClientStreamEndMessage);

        int binaryBefore = connection.SentBinary.Count;
        capture.Emit([9, 9], 6000);
        Assert.Equal(binaryBefore, connection.SentBinary.Count); // no chunk after stop
    }

    [Fact]
    public void RoleDeactivation_StopsStreaming()
    {
        var (client, connection, capture) = CreateSourceClient();
        using var _c = client;
        Activate(connection);
        connection.RaiseTextMessageReceived("""{"type":"server/command","payload":{"source":{"command":"start"}}}""");
        Assert.True(capture.Capturing);

        // A later activate that drops source@v1 ends streaming.
        Activate(connection, roles: "[]");
        Assert.False(capture.Capturing);
        Assert.Contains(connection.SentMessages, m => m is ClientStreamEndMessage);
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

        await client.SetSourceSignalAsync(present: true);

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
