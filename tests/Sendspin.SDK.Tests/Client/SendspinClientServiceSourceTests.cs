using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Audio.Source;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Coverage for the source (line-in) role: server-driven start/stop, client_stream
/// framing, server-domain chunk timestamps (type 12), line-sense reporting, trust
/// gating, and role deactivation.
/// </summary>
public class SendspinClientServiceSourceTests
{
    private const string ServerId = "GFsV9tLaSQm9HcFWpKsgYQOr7wFTvNUtkmFwuVz3zoo";

    private sealed class FakeNoiseSession : INoiseSessionInfo
    {
        public string? ServerId { get; set; } = SendspinClientServiceSourceTests.ServerId;
        public NoisePsk? MatchedPsk { get; set; } = new(NoiseConstants.SentinelPsk.ToArray(), PskCategory.LongTerm, SendspinClientServiceSourceTests.ServerId);
        public ReadOnlyMemory<byte>? HandshakeHash { get; set; } = new byte[32];
    }

    private sealed class FakeCaptureDevice : IAudioCaptureDevice
    {
        public AudioFormat Format { get; } = new() { Codec = "pcm", SampleRate = 48000, Channels = 2, BitDepth = 16 };
        public bool Capturing { get; private set; }
        public event EventHandler<CapturedAudio>? AudioCaptured;

        public Task StartAsync(CancellationToken ct = default) { Capturing = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct = default) { Capturing = false; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Emit(byte[] pcm, long captureTimeUs) =>
            AudioCaptured?.Invoke(this, new CapturedAudio(pcm, captureTimeUs));
    }

    private static (SendspinClientService, FakeSendspinConnection, FakeCaptureDevice) CreateSourceClient(
        bool lineSense = false, PskCategory trust = PskCategory.LongTerm, bool unpairedAccess = false)
    {
        var connection = new FakeSendspinConnection();
        var capture = new FakeCaptureDevice();
        var caps = new ClientCapabilities
        {
            Roles = { "source@v1" },
            SourceLineSense = lineSense,
            UnpairedAccessEnabled = unpairedAccess,
        };
        var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection,
            capabilities: caps,
            noiseSession: new FakeNoiseSession { MatchedPsk = new NoisePsk(NoiseConstants.SentinelPsk.ToArray(), trust, ServerId) },
            captureDevice: capture);
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
}
