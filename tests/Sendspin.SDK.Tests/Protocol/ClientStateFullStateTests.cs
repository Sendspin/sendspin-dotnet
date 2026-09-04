using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Protocol;

/// <summary>
/// Wire-format coverage for the spec PR #175 reshape of client/state: merging is gone, so every
/// message carries <c>available</c> plus the <b>full</b> state of each role object it includes,
/// and there is no delta form left to build.
/// </summary>
public class ClientStateFullStateTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EveryMessageCarriesAvailable_EvenWithRoleObjects(bool available)
    {
        var json = MessageSerializer.Serialize(ClientStateMessage.Create(
            available,
            player: new PlayerStatePayload { Volume = 50, Muted = false }));

        Assert.Contains($"\"available\":{(available ? "true" : "false")}", json);
        Assert.Contains("\"player\"", json);
    }

    [Fact]
    public void AvailableFalse_IsWritten_NotOmittedAsADefault()
    {
        // A JsonIgnore(WhenWritingDefault) on the non-nullable bool would silently swallow a
        // deliberate false, which is exactly the value that moves a client into a solo group.
        Assert.Contains("\"available\":false",
            MessageSerializer.Serialize(ClientStateMessage.Create(available: false)));
    }

    [Fact]
    public void NoRoleObjects_IsLegitimate_ForRolesThatDefineNoStateObject()
    {
        // Spec PR #181: a client whose active_roles are non-empty sends the initial client/state
        // even when none of its roles defines a state object — available alone unlocks its
        // streams.
        var json = MessageSerializer.Serialize(ClientStateMessage.Create(available: true));

        Assert.Contains("\"available\":true", json);
        Assert.DoesNotContain("\"player\"", json);
        Assert.DoesNotContain("\"source\"", json);
        Assert.DoesNotContain("\"artwork\"", json);
        Assert.DoesNotContain("\"visualizer\"", json);
    }

    [Fact]
    public void OldStateStringIsGone()
    {
        // Spec #115 replaced the "synchronized"/"error"/"external_source" string with the
        // boolean; no server released since 2026-07-07 understands the old shape.
        Assert.DoesNotContain("\"state\"",
            MessageSerializer.Serialize(ClientStateMessage.Create(available: true)));
    }

    [Fact]
    public void AllFourRoleObjects_SerializeTogether()
    {
        var json = MessageSerializer.Serialize(ClientStateMessage.Create(
            available: true,
            player: new PlayerStatePayload { Volume = 42, Muted = true, RequiredLeadTimeMs = 200, MinBufferMs = 150 },
            source: new SourceStatePayload { Signal = "present" },
            artwork: new ArtworkStatePayload
            {
                Channels = [new ArtworkChannelState { Source = "album", Format = "jpeg", Width = 512, Height = 512 }],
            },
            visualizer: new VisualizerStatePayload { Types = ["levels"], RateMax = 30 }));

        Assert.Contains("\"volume\":42", json);
        Assert.Contains("\"muted\":true", json);
        Assert.Contains("\"required_lead_time_ms\":200", json);
        Assert.Contains("\"min_buffer_ms\":150", json);
        Assert.Contains("\"source\":{\"signal\":\"present\"}", json);
        Assert.Contains("\"artwork\":{\"channels\":[", json);
        Assert.Contains("\"visualizer\":{\"types\":[\"levels\"],\"rate_max\":30}", json);
    }

    [Fact]
    public void SupportedCommands_IsAlwaysWritten_EmptyMeansAcceptsNone()
    {
        // Spec PR #175 dropped the '?': absence and [] said the same thing, and the redundant
        // encoding silently revoked set_output_delay for a reader treating a missing field as
        // unchanged.
        var json = MessageSerializer.Serialize(ClientStateMessage.Create(
            available: true, player: new PlayerStatePayload()));

        Assert.Contains("\"supported_commands\":[]", json);
    }

    [Fact]
    public void PlayerObject_WritesRequiredTimingFieldsEvenAtZero()
    {
        var json = MessageSerializer.Serialize(ClientStateMessage.Create(
            available: true, player: new PlayerStatePayload()));

        Assert.Contains("\"static_delay_ms\":0", json);
        Assert.Contains("\"required_lead_time_ms\":0", json);
        Assert.Contains("\"min_buffer_ms\":0", json);
    }
}
