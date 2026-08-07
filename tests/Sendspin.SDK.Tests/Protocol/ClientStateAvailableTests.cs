using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Protocol;

/// <summary>
/// Wire-format coverage for the spec #115 reshape of client/state: a top-level boolean
/// <c>available</c> replaced the old <c>state</c> string ("synchronized" / "error" /
/// "external_source"). No server released since 2026-07-07 understands the old string, so these
/// assertions run against the serialized JSON, not the object graph.
/// </summary>
public class ClientStateAvailableTests
{
    [Fact]
    public void CreateInitial_CarriesAvailableTrue_AndNoStateKey()
    {
        var json = MessageSerializer.Serialize(ClientStateMessage.CreateInitial(available: true));

        Assert.Contains("\"available\":true", json);
        Assert.DoesNotContain("\"state\"", json);
    }

    [Fact]
    public void CreateInitial_IncludesPlayerObject()
    {
        var json = MessageSerializer.Serialize(ClientStateMessage.CreateInitial(
            available: true,
            volume: 42,
            muted: true,
            requiredLeadTimeMs: 200,
            minBufferMs: 150));

        Assert.Contains("\"volume\":42", json);
        Assert.Contains("\"muted\":true", json);
        Assert.Contains("\"required_lead_time_ms\":200", json);
        Assert.Contains("\"min_buffer_ms\":150", json);
    }

    [Fact]
    public void CreateAvailability_SendsAvailableOnly_NoPlayer()
    {
        var json = MessageSerializer.Serialize(ClientStateMessage.CreateAvailability(true));

        Assert.Contains("\"available\":true", json);
        Assert.DoesNotContain("\"player\"", json);
    }

    [Fact]
    public void CreatePlayerState_SendsPlayerOnly_NoAvailable()
    {
        // The §4 defect: a player-state delta (volume/mute changes) must never assert
        // availability — doing so would silently override the server's external_source view.
        var json = MessageSerializer.Serialize(ClientStateMessage.CreatePlayerState(
            volume: 50,
            muted: false,
            staticDelayMs: 0,
            requiredLeadTimeMs: 0,
            minBufferMs: 0));

        Assert.Contains("\"player\"", json);
        Assert.DoesNotContain("\"available\"", json);
    }

    [Fact]
    public void CreateAvailability_False_RoundTripsAsFalse_NotOmitted()
    {
        // available must be bool?, not bool: a JsonIgnore(WhenWritingDefault) on a non-nullable
        // bool would silently swallow a deliberate false.
        var json = MessageSerializer.Serialize(ClientStateMessage.CreateAvailability(false));

        Assert.Contains("\"available\":false", json);
    }
}
