using System.Text;
using Sendspin.SDK.Connection.Framing;

namespace Sendspin.SDK.Tests.Connection;

/// <summary>
/// Pins the passthrough contract of <see cref="PlaintextWireFraming"/>: the plaintext
/// protocol's wire frames are exactly its application frames, with no handshake,
/// no transformation, and no reply traffic. The encrypted framing implementation will
/// get its own tests; these exist so a regression in the passthrough (or an accidental
/// behavior change while wiring framing into the connections) fails loudly.
/// </summary>
public class PlaintextWireFramingTests
{
    private readonly PlaintextWireFraming _framing = PlaintextWireFraming.Instance;

    [Fact]
    public void IsTransportReady_AlwaysTrue()
    {
        Assert.True(_framing.IsTransportReady);
    }

    [Fact]
    public void Start_SendsNothing()
    {
        Assert.Empty(_framing.Start());
    }

    [Fact]
    public void EncodeText_YieldsSingleTextFrame_WithUtf8Payload()
    {
        const string json = """{"type":"client/time","payload":{"client_transmitted":123}}""";

        var frames = _framing.EncodeText(json).ToList();

        var frame = Assert.Single(frames);
        Assert.Equal(WireFrameKind.Text, frame.Kind);
        Assert.Equal(Encoding.UTF8.GetBytes(json), frame.Payload.ToArray());
        Assert.Equal(json, frame.PayloadAsText());
    }

    [Fact]
    public void EncodeBinary_YieldsSingleBinaryFrame_SamePayload()
    {
        byte[] data = [4, 0, 0, 0, 0, 0, 0, 0, 42, 0xAB, 0xCD];

        var frames = _framing.EncodeBinary(data).ToList();

        var frame = Assert.Single(frames);
        Assert.Equal(WireFrameKind.Binary, frame.Kind);
        Assert.Equal(data, frame.Payload.ToArray());
    }

    [Fact]
    public void ProcessInbound_TextFrame_SurfacesSameText_NoReplies()
    {
        const string json = """{"type":"server/state","payload":{}}""";

        var result = _framing.ProcessInbound(WireFrame.FromText(json));

        Assert.Equal(json, result.Text);
        Assert.Null(result.Binary);
        Assert.Null(result.Replies);
    }

    [Fact]
    public void ProcessInbound_TextFrame_FromRawBytes_DecodesUtf8()
    {
        const string json = """{"type":"server/time","payload":{"server_received":1}}""";
        var frame = new WireFrame(WireFrameKind.Text, Encoding.UTF8.GetBytes(json));

        var result = _framing.ProcessInbound(frame);

        Assert.Equal(json, result.Text);
    }

    [Fact]
    public void ProcessInbound_BinaryFrame_SurfacesSameBytes_NoReplies()
    {
        byte[] data = [8, 0, 0, 0, 0, 0, 0, 0, 7, 0xFF];

        var result = _framing.ProcessInbound(new WireFrame(WireFrameKind.Binary, data));

        Assert.NotNull(result.Binary);
        Assert.Equal(data, result.Binary.Value.ToArray());
        Assert.Null(result.Text);
        Assert.Null(result.Replies);
    }

    [Fact]
    public void Reset_IsIdempotentNoOp()
    {
        _framing.Reset();
        _framing.Reset();
        Assert.True(_framing.IsTransportReady);
    }
}
