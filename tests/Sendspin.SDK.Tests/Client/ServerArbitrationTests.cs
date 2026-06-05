using Sendspin.SDK.Client;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Exhaustive coverage of the pure multi-server arbitration decision table (spec §multi-server).
/// </summary>
public class ServerArbitrationTests
{
    [Fact]
    public void NoExistingConnection_AcceptsNewWithNoLoser()
    {
        var r = ServerArbitration.Decide("srv-new", "discovery", null, null, null);
        Assert.True(r.AcceptNew);
        Assert.Null(r.LoserGoodbyeReason);
    }

    [Fact]
    public void SameServerReconnect_AcceptsAndDropsStaleWithUserRequest()
    {
        var r = ServerArbitration.Decide("srv-1", "discovery", "srv-1", "discovery", null);
        Assert.True(r.AcceptNew);
        Assert.Equal("user_request", r.LoserGoodbyeReason);
    }

    [Theory]

    // newId, newReason, existingId, existingReason, lastPlayed, expectAccept, expectLoserReason
    [InlineData("b", "playback", "a", "discovery", null, true, "another_server")]  // new playback wins
    [InlineData("b", "discovery", "a", "playback", null, false, "another_server")] // existing playback wins
    [InlineData("b", "discovery", "a", "discovery", "b", true, "another_server")]  // tie, last-played = new
    [InlineData("b", "discovery", "a", "discovery", "a", false, "another_server")] // tie, last-played = existing
    [InlineData("b", "discovery", "a", "discovery", null, false, "another_server")] // tie, no last-played
    [InlineData("b", "playback", "a", "playback", "b", true, "another_server")]    // E1 both playback, lp = new
    [InlineData("b", "playback", "a", "playback", "a", false, "another_server")]   // E1 both playback, lp = existing
    [InlineData("b", null, "a", "playback", null, false, "another_server")]        // null reason = discovery
    [InlineData("b", "PLAYBACK", "a", "discovery", null, true, "another_server")]  // case-insensitive
    public void DecisionTable(
        string newId,
        string? newReason,
        string existingId,
        string? existingReason,
        string? lastPlayed,
        bool expectAccept,
        string expectLoserReason)
    {
        var r = ServerArbitration.Decide(newId, newReason, existingId, existingReason, lastPlayed);
        Assert.Equal(expectAccept, r.AcceptNew);
        Assert.Equal(expectLoserReason, r.LoserGoodbyeReason);
    }
}
