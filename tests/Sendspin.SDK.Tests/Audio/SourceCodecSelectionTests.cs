using Sendspin.SDK.Audio.Source;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Tests.Client;

namespace Sendspin.SDK.Tests.Audio;

/// <summary>
/// Coverage for source@v1 encoder codec selection (#85 item 8): the encoder must be
/// created from the configured <see cref="SourceRoleSupport.Codec"/> when set, and fall
/// back to the capture device's own format when it is not.
/// </summary>
public class SourceCodecSelectionTests
{
    private const string ServerId = FakeNoiseSession.FakeServerId;

    /// <summary>Hands back a stub encoder that reports whatever codec it was asked to create.</summary>
    private sealed class StubEncoderFactory : ISourceAudioEncoderFactory
    {
        public ISourceAudioEncoder Create(string codec, AudioFormat format) => new StubEncoder(codec);
    }

    private sealed class StubEncoder(string codec) : ISourceAudioEncoder
    {
        public string Codec => codec;
        public string? CodecHeader => null;
        public byte[] Encode(ReadOnlySpan<byte> pcm) => pcm.ToArray();
        public void Dispose() { }
    }

    private static (SendspinClientService Client, FakeSendspinConnection Connection, FakeCaptureDevice Capture) CreateSourceClient(
        SourceRoleSupport? sourceSupport)
    {
        var capture = new FakeCaptureDevice();
        var (client, connection, session) = TestClient.Create(
            configure: options =>
            {
                options.Capabilities = new ClientCapabilities { Roles = { "source@v1" }, SourceRoleSupport = sourceSupport };
                options.CaptureDevice = capture;
                options.SourceEncoderFactory = new StubEncoderFactory();
            });

        // Bound to ServerId so the source trust gate (user trust) is satisfied, same as
        // SendspinClientServiceSourceTests.
        session.MatchedPsk = new NoisePsk(NoiseConstants.SentinelPsk.ToArray(), PskCategory.LongTerm, ServerId);
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["playback"],"active_roles":["source@v1"]}}""");
        connection.RaiseTextMessageReceived("""{"type":"server/command","payload":{"source":{"command":"start"}}}""");

        return (client, connection, capture);
    }

    [Fact]
    public void ConfiguredCodec_OverridesPcmCaptureFormat()
    {
        // The defect (#85 item 8): a PCM capture device with an explicit Codec="opus" must
        // produce an opus encoder, so client_stream/start announces "opus". Before the fix
        // the encoder is created from capture.Format.Codec ("pcm") no matter what
        // SourceRoleSupport.Codec says, so this fails on the current code. If this test were
        // the only one present, a factory that ignores SourceRoleSupport.Codec entirely (i.e.
        // the pre-fix behaviour) would simply keep failing here forever with no way to prove a
        // real fix was made rather than a hard-coded "always opus" shortcut.
        var (client, connection, capture) = CreateSourceClient(new SourceRoleSupport { Codec = "opus" });
        using var _c = client;

        Assert.Equal("pcm", capture.Format.Codec); // sanity: the capture device really is PCM
        var start = connection.SentMessages.OfType<ClientStreamStartMessage>().Single();
        Assert.Equal("opus", start.Payload.Source.Codec);
    }

    [Fact]
    public void UnsetCodec_FallsBackToCaptureFormat()
    {
        // The positive control: with Codec unset, the encoder must still match the capture
        // device's own PCM format — existing behaviour is the default, and only an explicit
        // choice changes it. Without this test, a "fix" for the test above that hard-codes
        // the encoder to opus regardless of SourceRoleSupport.Codec would still pass it; this
        // is the test that would catch that and only this one does.
        var (client, connection, capture) = CreateSourceClient(new SourceRoleSupport { Codec = null });
        using var _c = client;

        Assert.Equal("pcm", capture.Format.Codec);
        var start = connection.SentMessages.OfType<ClientStreamStartMessage>().Single();
        Assert.Equal("pcm", start.Payload.Source.Codec);
    }
}
