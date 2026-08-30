using Sendspin.SDK.Client;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Exhaustive coverage of the pure multi-server arbitration decision table (spec
/// "Multiple servers" section): incoming accepted on higher-or-equal priority
/// (management &gt; playback &gt; pairing &gt; empty), pairing attempts not displaced
/// by playback/pairing, empty-vs-empty ties gated on the last-playback server, a
/// displaced holder told 'another_server', and a rejected incoming told
/// 'concurrent_attempt' — except that a loser which is a pairing handshake is told
/// pair/abort 'concurrent_attempt' instead of either (#203). Plus the one rule that is
/// not the spec's, because the spec leaves client-initiated multi-server behaviour to
/// the client: a client-initiated holder is not displaced by anything (#253).
/// </summary>
public class ServerArbitrationTests
{
    [Fact]
    public void NoExistingConnection_AcceptsNewWithNoLoser()
    {
        var r = ServerArbitration.Decide("srv-new", ConnectionPriority.Empty, null, ConnectionPriority.Empty, null);
        Assert.True(r.AcceptNew);
        Assert.Null(r.LoserReason);
    }

    [Fact]
    public void SameServerReconnect_AcceptsAndDropsStaleWithUserRequest()
    {
        var r = ServerArbitration.Decide("srv-1", ConnectionPriority.Empty, "srv-1", ConnectionPriority.Empty, null);
        Assert.True(r.AcceptNew);
        Assert.Equal("user_request", r.LoserReason);
        Assert.Equal(ArbitrationFarewell.Goodbye, r.LoserFarewell);
    }

    [Fact]
    public void SameServerReconnect_DisplacingAPairingHandshake_UsesPairAbort()
    {
        // user_request exists to avoid telling the SAME server 'another_server', a distinction
        // only client/goodbye draws. pair/abort has no such reason, and the stale socket is a
        // displaced pairing handshake however the successor identifies itself.
        var r = ServerArbitration.Decide("srv-1", ConnectionPriority.Empty, "srv-1", ConnectionPriority.Pairing, null);
        Assert.True(r.AcceptNew);
        Assert.Equal("concurrent_attempt", r.LoserReason);
        Assert.Equal(ArbitrationFarewell.PairAbort, r.LoserFarewell);
    }

    [Theory]

    // newId, newPrio, existingId, existingPrio, lastPlayed, expectAccept, expectLoserReason, expectPairAbort
    // (a bool rather than the ArbitrationFarewell itself: that enum is internal, and an xUnit
    // theory parameter has to be at least as accessible as the public test method.)
    // Higher priority displaces:
    [InlineData("b", ConnectionPriority.Playback, "a", ConnectionPriority.Empty, null, true, "another_server", false)]
    [InlineData("b", ConnectionPriority.Management, "a", ConnectionPriority.Playback, null, true, "another_server", false)]
    // Lower priority rejected with concurrent_attempt:
    [InlineData("b", ConnectionPriority.Empty, "a", ConnectionPriority.Playback, null, false, "concurrent_attempt", false)]
    [InlineData("b", ConnectionPriority.Playback, "a", ConnectionPriority.Management, null, false, "concurrent_attempt", false)]
    // Equal non-empty priority: incoming accepted (spec: "higher or equal is accepted"):
    [InlineData("b", ConnectionPriority.Playback, "a", ConnectionPriority.Playback, null, true, "another_server", false)]
    [InlineData("b", ConnectionPriority.Playback, "a", ConnectionPriority.Playback, "a", true, "another_server", false)]
    [InlineData("b", ConnectionPriority.Management, "a", ConnectionPriority.Management, null, true, "another_server", false)]
    // Empty-vs-empty tie: incoming admitted only when it is the last-playback server:
    [InlineData("b", ConnectionPriority.Empty, "a", ConnectionPriority.Empty, "b", true, "another_server", false)]
    [InlineData("b", ConnectionPriority.Empty, "a", ConnectionPriority.Empty, "a", false, "concurrent_attempt", false)]
    [InlineData("b", ConnectionPriority.Empty, "a", ConnectionPriority.Empty, null, false, "concurrent_attempt", false)]
    // Pairing attempt is not displaced by incoming playback or pairing. In the second case the
    // rejected incoming is itself a pairing handshake, so that loss goes out as pair/abort:
    [InlineData("b", ConnectionPriority.Playback, "a", ConnectionPriority.Pairing, null, false, "concurrent_attempt", false)]
    [InlineData("b", ConnectionPriority.Pairing, "a", ConnectionPriority.Pairing, null, false, "concurrent_attempt", true)]
    // ...but management may displace a pairing attempt, and that displaced pairing handshake
    // is told pair/abort concurrent_attempt rather than goodbye another_server:
    [InlineData("b", ConnectionPriority.Management, "a", ConnectionPriority.Pairing, null, true, "concurrent_attempt", true)]
    // Pairing loses to a playback holder:
    [InlineData("b", ConnectionPriority.Pairing, "a", ConnectionPriority.Playback, null, false, "concurrent_attempt", true)]
    public void DecisionTable(
        string newId,
        ConnectionPriority newPriority,
        string existingId,
        ConnectionPriority existingPriority,
        string? lastPlayed,
        bool expectAccept,
        string expectLoserReason,
        bool expectPairAbort)
    {
        var r = ServerArbitration.Decide(newId, newPriority, existingId, existingPriority, lastPlayed);
        Assert.Equal(expectAccept, r.AcceptNew);
        Assert.Equal(expectLoserReason, r.LoserReason);
        Assert.Equal(
            expectPairAbort ? ArbitrationFarewell.PairAbort : ArbitrationFarewell.Goodbye,
            r.LoserFarewell);
    }

    [Fact]
    public void ClientInitiatedHolder_IsNotDisplacedByAnEquallyRankedIncomingServer()
    {
        // Playback against playback is exactly #253's pairing of servers, and the general rule
        // ("higher or equal is accepted") takes the incoming one — which is what tore down the
        // live session. A client-initiated holder is refused against instead.
        var r = ServerArbitration.Decide(
            "srv-new",
            ConnectionPriority.Playback,
            "dialled",
            ConnectionPriority.Playback,
            null,
            existingIsClientInitiated: true);

        Assert.False(r.AcceptNew);
        Assert.Equal("concurrent_attempt", r.LoserReason);
        Assert.Equal(ArbitrationFarewell.Goodbye, r.LoserFarewell);
    }

    [Fact]
    public void ClientInitiatedHolder_IsNotDisplacedByManagementEither()
    {
        // The highest priority the table has, against the lowest the holder can declare: the
        // rule is "not displaced", not "wins ties".
        var r = ServerArbitration.Decide(
            "srv-new",
            ConnectionPriority.Management,
            "dialled",
            ConnectionPriority.Empty,
            null,
            existingIsClientInitiated: true);

        Assert.False(r.AcceptNew);
        Assert.Equal("concurrent_attempt", r.LoserReason);
    }

    [Fact]
    public void ClientInitiatedHolder_IsNotDisplacedByItsOwnServerDiallingIn()
    {
        // The same-server branch drops the previous socket as stale. A session this client
        // dialled is not a stale socket, so that branch must not be reached for one — which is
        // why the client-initiated rule sits above it.
        var r = ServerArbitration.Decide(
            "dialled",
            ConnectionPriority.Playback,
            "dialled",
            ConnectionPriority.Playback,
            null,
            existingIsClientInitiated: true);

        Assert.False(r.AcceptNew);
        Assert.Equal("concurrent_attempt", r.LoserReason);
    }

    [Fact]
    public void IncomingPairing_RejectedByAClientInitiatedHolder_IsStillToldPairAbort()
    {
        // The new rule changes the ranking, not how a loss is delivered: a rejected incoming
        // pairing handshake is told pair/abort, exactly as under the spec's own rules.
        var r = ServerArbitration.Decide(
            "srv-new",
            ConnectionPriority.Pairing,
            "dialled",
            ConnectionPriority.Playback,
            null,
            existingIsClientInitiated: true);

        Assert.False(r.AcceptNew);
        Assert.Equal("concurrent_attempt", r.LoserReason);
        Assert.Equal(ArbitrationFarewell.PairAbort, r.LoserFarewell);
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
