using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Protocol;

/// <summary>
/// A pure availability change is sent as a delta: client/state with only the available field and
/// no player object (the player object is full and is only sent when player state changes).
/// </summary>
public class ClientStateDeltaTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CreateAvailability_SendsAvailableOnly_NoPlayer(bool available)
    {
        var json = MessageSerializer.Serialize(ClientStateMessage.CreateAvailability(available));

        Assert.Contains($"\"available\":{(available ? "true" : "false")}", json);
        Assert.DoesNotContain("\"player\"", json);
    }

    [Fact]
    public void CreateAvailability_ObjectGraph_IsAvailableOnly()
    {
        var msg = ClientStateMessage.CreateAvailability(false);

        Assert.Equal(false, msg.Payload.Available);
        Assert.Null(msg.Payload.Player);

        var json = MessageSerializer.Serialize(msg);
        Assert.DoesNotContain("\"player\"", json);
    }

    [Fact]
    public void CreatePlayerState_StillIncludesFullPlayer()
    {
        // Player-state reports keep the full player object (the timing fields are always required).
        var msg = ClientStateMessage.CreatePlayerState(
            volume: 50, muted: false, staticDelayMs: 0, requiredLeadTimeMs: 0, minBufferMs: 0);

        Assert.NotNull(msg.Payload.Player);
        var json = MessageSerializer.Serialize(msg);
        Assert.Contains("\"player\"", json);
        Assert.Contains("\"required_lead_time_ms\"", json);
    }
}
