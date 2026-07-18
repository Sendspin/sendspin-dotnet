using Sendspin.SDK.Client;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Exhaustive coverage of the pure multi-server arbitration decision table (spec
/// "Multiple servers" section): incoming accepted on higher-or-equal priority
/// (management &gt; playback &gt; pairing &gt; empty), pairing attempts not displaced
/// by playback/pairing, empty-vs-empty ties gated on the last-playback server, a
/// displaced holder told 'another_server', and a rejected incoming told
/// 'concurrent_attempt'.
/// </summary>
public class ServerArbitrationTests
{
    [Fact]
    public void NoExistingConnection_AcceptsNewWithNoLoser()
    {
        var r = ServerArbitration.Decide("srv-new", ConnectionPriority.Empty, null, ConnectionPriority.Empty, null);
        Assert.True(r.AcceptNew);
        Assert.Null(r.LoserGoodbyeReason);
    }

    [Fact]
    public void SameServerReconnect_AcceptsAndDropsStaleWithUserRequest()
    {
        var r = ServerArbitration.Decide("srv-1", ConnectionPriority.Empty, "srv-1", ConnectionPriority.Empty, null);
        Assert.True(r.AcceptNew);
        Assert.Equal("user_request", r.LoserGoodbyeReason);
    }

    [Theory]

    // newId, newPrio, existingId, existingPrio, lastPlayed, expectAccept, expectLoserReason
    // Higher priority displaces:
    [InlineData("b", ConnectionPriority.Playback, "a", ConnectionPriority.Empty, null, true, "another_server")]
    [InlineData("b", ConnectionPriority.Management, "a", ConnectionPriority.Playback, null, true, "another_server")]
    // Lower priority rejected with concurrent_attempt:
    [InlineData("b", ConnectionPriority.Empty, "a", ConnectionPriority.Playback, null, false, "concurrent_attempt")]
    [InlineData("b", ConnectionPriority.Playback, "a", ConnectionPriority.Management, null, false, "concurrent_attempt")]
    // Equal non-empty priority: incoming accepted (spec: "higher or equal is accepted"):
    [InlineData("b", ConnectionPriority.Playback, "a", ConnectionPriority.Playback, null, true, "another_server")]
    [InlineData("b", ConnectionPriority.Playback, "a", ConnectionPriority.Playback, "a", true, "another_server")]
    [InlineData("b", ConnectionPriority.Management, "a", ConnectionPriority.Management, null, true, "another_server")]
    // Empty-vs-empty tie: incoming admitted only when it is the last-playback server:
    [InlineData("b", ConnectionPriority.Empty, "a", ConnectionPriority.Empty, "b", true, "another_server")]
    [InlineData("b", ConnectionPriority.Empty, "a", ConnectionPriority.Empty, "a", false, "concurrent_attempt")]
    [InlineData("b", ConnectionPriority.Empty, "a", ConnectionPriority.Empty, null, false, "concurrent_attempt")]
    // Pairing attempt is not displaced by incoming playback or pairing:
    [InlineData("b", ConnectionPriority.Playback, "a", ConnectionPriority.Pairing, null, false, "concurrent_attempt")]
    [InlineData("b", ConnectionPriority.Pairing, "a", ConnectionPriority.Pairing, null, false, "concurrent_attempt")]
    // ...but management may displace a pairing attempt:
    [InlineData("b", ConnectionPriority.Management, "a", ConnectionPriority.Pairing, null, true, "another_server")]
    // Pairing loses to a playback holder:
    [InlineData("b", ConnectionPriority.Pairing, "a", ConnectionPriority.Playback, null, false, "concurrent_attempt")]
    public void DecisionTable(
        string newId,
        ConnectionPriority newPriority,
        string existingId,
        ConnectionPriority existingPriority,
        string? lastPlayed,
        bool expectAccept,
        string expectLoserReason)
    {
        var r = ServerArbitration.Decide(newId, newPriority, existingId, existingPriority, lastPlayed);
        Assert.Equal(expectAccept, r.AcceptNew);
        Assert.Equal(expectLoserReason, r.LoserGoodbyeReason);
    }

    [Theory]
    [InlineData("playback", ConnectionPriority.Playback)]
    [InlineData("PLAYBACK", ConnectionPriority.Playback)]
    [InlineData("discovery", ConnectionPriority.Empty)]
    [InlineData(null, ConnectionPriority.Empty)]
    [InlineData("something-else", ConnectionPriority.Empty)]
    public void FromConnectionReason_MapsLegacyReasons(string? reason, ConnectionPriority expected)
    {
        Assert.Equal(expected, ServerArbitration.FromConnectionReason(reason));
    }

    [Theory]
    [InlineData(new string[0], ConnectionPriority.Empty)]
    [InlineData(new[] { "pairing" }, ConnectionPriority.Pairing)]
    [InlineData(new[] { "playback" }, ConnectionPriority.Playback)]
    [InlineData(new[] { "management" }, ConnectionPriority.Management)]
    [InlineData(new[] { "playback", "management" }, ConnectionPriority.Management)]
    [InlineData(new[] { "pairing", "playback" }, ConnectionPriority.Playback)]
    [InlineData(new[] { "unknown-future-activity" }, ConnectionPriority.Empty)]
    public void FromActivities_RanksByHighestDeclaredActivity(string[] activities, ConnectionPriority expected)
    {
        Assert.Equal(expected, ServerArbitration.FromActivities(activities));
    }

    [Fact]
    public void FromActivities_NullSet_IsEmpty()
    {
        Assert.Equal(ConnectionPriority.Empty, ServerArbitration.FromActivities(null));
    }
}
