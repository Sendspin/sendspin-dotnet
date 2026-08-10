using System.Text;
using Sendspin.SDK.Connection.Framing;
using Sendspin.SDK.Connection.Noise;

namespace Sendspin.SDK.Tests.Connection;

/// <summary>
/// Pins the exact bytes of the handshake's JSON frames.
/// </summary>
/// <remarks>
/// <para>
/// These are not ordinary shape assertions. The prologue binds the literal wire bytes of
/// both init messages — <c>_clientInitBytes</c> and <c>_serverInitBytes</c> are concatenated
/// and hashed into the Noise handshake — so a change to key order, spacing, escaping, or
/// number formatting silently breaks every handshake against a conformant peer while every
/// round-trip test in this repo continues to pass, because both sides of a self-test change
/// together.
/// </para>
/// <para>
/// That makes these frames the one place in the codebase where "the JSON means the same
/// thing" is not good enough. They exist so the serializer underneath can be changed —
/// reflection to source generation for AOT (#89) — with proof the output did not move.
/// </para>
/// </remarks>
public class HandshakeByteFidelityTests
{
    [Fact]
    public void ClientInit_HasExactlyTheseBytes()
    {
        // Fixed key, so the frame is fully determined and can be written out literally.
        var identity = SendspinIdentity.FromKeys(
            privateKey: Enumerable.Repeat((byte)0x11, 32).ToArray(),
            publicKey: Enumerable.Repeat((byte)0x22, 32).ToArray());
        var framing = new NoiseWireFraming(identity, pskResolver: null, NoiseCipherSuite.ChaChaPoly);

        var frame = Assert.Single(framing.Start());

        Assert.Equal(
            """{"type":"client/init","payload":{"client_id":"IiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiI","version":1,"suite":"25519_ChaChaPoly_SHA256"}}""",
            frame.PayloadAsText());
    }

    [Fact]
    public void ClientInit_SuiteNameTracksTheChosenSuite()
    {
        // Positive control for the literal above: the suite is really read from the option,
        // not baked into a constant string that happens to match.
        var identity = SendspinIdentity.FromKeys(
            privateKey: Enumerable.Repeat((byte)0x11, 32).ToArray(),
            publicKey: Enumerable.Repeat((byte)0x22, 32).ToArray());
        var framing = new NoiseWireFraming(identity, pskResolver: null, NoiseCipherSuite.AesGcm);

        Assert.Contains(
            "\"suite\":\"25519_AESGCM_SHA256\"",
            Assert.Single(framing.Start()).PayloadAsText(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ClientInitBytes_AreTheFrameBytes_ByteForByte()
    {
        // The prologue is built from what was actually put on the wire. If the framing ever
        // re-serializes instead of capturing the frame it sent, this catches the drift.
        var identity = SendspinIdentity.Generate();
        var framing = new NoiseWireFraming(identity);

        var frame = Assert.Single(framing.Start());
        byte[] wire = Encoding.UTF8.GetBytes(frame.PayloadAsText());

        Assert.Equal(wire, frame.Payload.ToArray());
    }

    [Fact]
    public void HandshakeReply_HasExactlyThisShape()
    {
        // The reply's data value is key-dependent, so the literal is the envelope around it.
        // Key order and the absence of whitespace are the parts that must not move.
        var identity = SendspinIdentity.Generate();
        var framing = new NoiseWireFraming(identity);
        var server = new TestNoiseServer(identity.PublicKey, NoiseConstants.SentinelPsk.ToArray());

        var clientInit = Assert.Single(framing.Start());
        var (serverInit, msg1) = server.Respond(clientInit.PayloadAsText());
        Assert.Null(framing.ProcessInbound(WireFrame.FromText(serverInit)).FatalReason);
        var result = framing.ProcessInbound(WireFrame.FromText(msg1));
        Assert.Null(result.FatalReason);

        string reply = Assert.Single(result.Replies!).PayloadAsText();

        Assert.StartsWith("{\"type\":\"noise/handshake\",\"payload\":{\"data\":\"", reply, StringComparison.Ordinal);
        Assert.EndsWith("\"}}", reply, StringComparison.Ordinal);
        Assert.DoesNotContain(" ", reply, StringComparison.Ordinal);   // no pretty-printing
        Assert.DoesNotContain("\n", reply, StringComparison.Ordinal);
    }
}
