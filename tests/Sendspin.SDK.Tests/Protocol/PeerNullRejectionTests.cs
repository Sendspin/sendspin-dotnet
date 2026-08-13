using System.Text.Json;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Protocol;

/// <summary>
/// A peer-supplied message carrying JSON <c>null</c> where the model declares a non-nullable
/// reference member is rejected at deserialization, so the annotations are load-bearing rather
/// than decorative (#106).
/// </summary>
/// <remarks>
/// The hole is not obvious from the models: every one of these members has an initializer, which
/// covers an <em>absent</em> property but is overwritten by an explicit null. The first test here
/// pins that distinction, because it is the thing that makes the rest necessary.
/// </remarks>
public class PeerNullRejectionTests
{
    [Fact]
    public void AbsentMember_KeepsItsInitializer_AndIsAccepted()
    {
        // The control the whole issue turns on. If absent members were also rejected, every
        // optional field in the protocol would break, so this must stay permitted.
        var msg = MessageSerializer.Deserialize<StreamStartMessage>("""
            {"type":"stream/start","payload":{"artwork":{}}}
            """);

        Assert.NotNull(msg);
        Assert.NotNull(msg.Payload.Artwork!.Channels);
        Assert.Empty(msg.Payload.Artwork.Channels);
    }

    [Theory]
    // The four members #106 lists, which the SDK never dereferences itself — an app subscriber
    // does, so before this the fault landed in the subscriber as a NullReferenceException.
    [InlineData("""{"type":"stream/start","payload":{"artwork":{"channels":null}}}""")]
    [InlineData("""{"type":"stream/start","payload":{"artwork":{"channels":[{"source":null}]}}}""")]
    [InlineData("""{"type":"stream/start","payload":{"artwork":{"channels":[{"format":null}]}}}""")]
    [InlineData("""{"type":"stream/start","payload":{"visualizer":{"types":null,"rate_max":30}}}""")]
    // The cases #105 already guarded in the handler, now enforced centrally too.
    [InlineData("""{"type":"stream/start","payload":null}""")]
    [InlineData("""{"type":"stream/start","payload":{"player":{"codec":null}}}""")]
    public void NullWhereNonNullable_IsRejected(string json)
    {
        Assert.Throws<JsonException>(() => MessageSerializer.Deserialize<StreamStartMessage>(json));
    }

    [Theory]
    [InlineData("""{"type":"server/hello","payload":null}""")]
    [InlineData("""{"type":"server/hello","payload":{"server_id":null}}""")]
    [InlineData("""{"type":"server/hello","payload":{"active_roles":null}}""")]
    [InlineData("""{"type":"server/activate","payload":{"activities":null}}""")]
    [InlineData("""{"type":"server/activate","payload":{"activities":[],"pairing":{"method":null}}}""")]
    [InlineData("""{"type":"pair/abort","payload":{"reason":null}}""")]
    [InlineData("""{"type":"server/pair-init","payload":{"nonce_A":null}}""")]
    [InlineData("""{"type":"server/pair-auth","payload":{"pake_msg_1":null}}""")]
    [InlineData("""{"type":"server/pair-confirm","payload":{"server_kc":null}}""")]
    [InlineData("""{"type":"group/update","payload":{"group_id":null}}""")]
    [InlineData("""{"type":"server/command","payload":null}""")]
    [InlineData("""{"type":"stream/end","payload":null}""")]
    [InlineData("""{"type":"stream/clear","payload":null}""")]
    [InlineData("""{"type":"server/time","payload":null}""")]
    public void NullWhereNonNullable_IsRejected_AcrossTheInboundSurface(string json)
    {
        // Deserialize(string) is the receive path's entry point; the typed overload above is
        // what the individual handlers call. Both validate, so neither is a way around it.
        Assert.Throws<JsonException>(() => MessageSerializer.Deserialize(json));
    }

    [Fact]
    public void AWellFormedMessage_IsStillAccepted()
    {
        // Positive control for the theories: rejecting everything would satisfy them all.
        var msg = MessageSerializer.Deserialize("""
            {"type":"stream/start","payload":{"player":{"codec":"opus","channels":2,"sample_rate":48000},
             "artwork":{"channels":[{"source":"album","format":"jpeg"}]},
             "visualizer":{"types":["beat"],"rate_max":30}}}
            """);

        var start = Assert.IsType<StreamStartMessage>(msg);
        Assert.Equal("opus", start.Payload.Format!.Codec);
        Assert.Equal("album", start.Payload.Artwork!.Channels[0].Source);
        Assert.Equal(new[] { "beat" }, start.Payload.Visualizer!.Types);
    }
}
