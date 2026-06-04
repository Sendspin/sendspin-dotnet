using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Protocol;

/// <summary>
/// A pure operational-state change is sent as a delta: client/state with only the state field and
/// no player object (the player object is full and is only sent when player state changes).
/// </summary>
public class ClientStateDeltaTests
{
    [Theory]
    [InlineData("external_source")]
    [InlineData("synchronized")]
    [InlineData("error")]
    public void CreateState_SendsStateOnly_NoPlayer(string state)
    {
        var json = MessageSerializer.Serialize(ClientStateMessage.CreateState(state));

        Assert.Contains($"\"state\":\"{state}\"", json);
        Assert.DoesNotContain("\"player\"", json);
    }

    [Fact]
    public void CreateError_IsStateOnly()
    {
        var msg = ClientStateMessage.CreateError("buffer underrun");

        Assert.Equal("error", msg.Payload.State);
        Assert.Null(msg.Payload.Player);

        var json = MessageSerializer.Serialize(msg);
        Assert.DoesNotContain("\"player\"", json);
        Assert.DoesNotContain("buffer underrun", json); // detail is for logging only, not the wire
    }

    [Fact]
    public void CreateSynchronized_StillIncludesFullPlayer()
    {
        // Player-state reports keep the full player object (the timing fields are always required).
        var msg = ClientStateMessage.CreateSynchronized(volume: 50, muted: false);

        Assert.NotNull(msg.Payload.Player);
        var json = MessageSerializer.Serialize(msg);
        Assert.Contains("\"player\"", json);
        Assert.Contains("\"required_lead_time_ms\"", json);
    }
}
