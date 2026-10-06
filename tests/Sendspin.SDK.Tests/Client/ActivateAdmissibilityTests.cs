using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Noise;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// The spec's table of activity sets a server may declare, by the PSK that matched the Noise
/// handshake (messaging.md, <c>server/activate</c>, spec 1.0.0-rc1): one case per cell.
/// </summary>
/// <remarks>
/// <code>
/// long-term PSK   []  ['playback']
/// pairing PSK     []  ['pairing']  ['playback']¹  ['playback','pairing']¹
/// Sentinel PSK    []  ['pairing']  ['playback']¹  ['playback','pairing']¹
/// ¹ Only when the client has unpaired access enabled.
/// </code>
/// A refused activation closes with <c>client/goodbye</c>: <c>pairing_required</c> when the
/// session is unpaired and enabling unpaired access would have admitted it, <c>unauthorized</c>
/// otherwise.
/// </remarks>
public class ActivateAdmissibilityTests
{
    [Theory]
    [InlineData(PskCategory.LongTerm, false, "")]
    [InlineData(PskCategory.LongTerm, false, "playback")]
    [InlineData(PskCategory.Pairing, false, "")]
    [InlineData(PskCategory.Pairing, false, "pairing")]
    [InlineData(PskCategory.Pairing, true, "")]
    [InlineData(PskCategory.Pairing, true, "pairing")]
    [InlineData(PskCategory.Pairing, true, "playback")]
    [InlineData(PskCategory.Pairing, true, "playback,pairing")]
    [InlineData(PskCategory.Sentinel, false, "")]
    [InlineData(PskCategory.Sentinel, false, "pairing")]
    [InlineData(PskCategory.Sentinel, true, "")]
    [InlineData(PskCategory.Sentinel, true, "pairing")]
    [InlineData(PskCategory.Sentinel, true, "playback")]
    [InlineData(PskCategory.Sentinel, true, "playback,pairing")]
    public void AllowedActivitySet_IsAdmitted(PskCategory category, bool unpairedAccess, string activities)
    {
        var (client, connection, _) = TestClient.Create(category, unpairedAccess);
        using var _c = client;

        Activate(connection, category, activities);

        Assert.Equal(ConnectionState.Connected, connection.State);
        Assert.NotNull(client.LastServerActivate);
    }

    [Theory]
    [InlineData(PskCategory.Pairing, "playback")]
    [InlineData(PskCategory.Pairing, "playback,pairing")]
    [InlineData(PskCategory.Sentinel, "playback")]
    [InlineData(PskCategory.Sentinel, "playback,pairing")]
    public void PlaybackOnAnUnpairedSession_WithUnpairedAccessOff_ClosesWithPairingRequired(
        PskCategory category, string activities)
    {
        var (client, connection, _) = TestClient.Create(category, unpairedAccess: false);
        using var _c = client;

        Activate(connection, category, activities);

        Assert.Equal(ConnectionState.Disconnected, connection.State);
        Assert.Equal("pairing_required", connection.LastDisconnectReason);
    }

    [Theory]
    [InlineData(PskCategory.Pairing)]
    [InlineData(PskCategory.Sentinel)]
    public void RolesOnAPairingOnlyActivation_WithUnpairedAccessOff_ClosesWithPairingRequired(PskCategory category)
    {
        // ['pairing'] is an allowed set, but a non-empty active_roles needs a playback-capable
        // connection — one whose activities extended with 'playback' are still allowed. Unpaired
        // access would make it so, which is the rule that selects 'pairing_required'.
        var (client, connection, _) = TestClient.Create(category, unpairedAccess: false);
        using var _c = client;

        Activate(connection, category, "pairing", activeRoles: "\"player@v1\"");

        Assert.Equal(ConnectionState.Disconnected, connection.State);
        Assert.Equal("pairing_required", connection.LastDisconnectReason);
    }

    [Theory]
    [InlineData(false, "pairing")]
    [InlineData(false, "playback,pairing")]
    [InlineData(true, "pairing")]
    [InlineData(true, "playback,pairing")]
    public void PairingOnALongTermPsk_ClosesUnauthorized(bool unpairedAccess, string activities)
    {
        // A paired session: 'pairing_required' is for unpaired ones, and no unpaired-access
        // setting admits a pairing activity over a credential the client already holds.
        var (client, connection, _) = TestClient.Create(PskCategory.LongTerm, unpairedAccess);
        using var _c = client;

        Activate(connection, PskCategory.LongTerm, activities);

        Assert.Equal(ConnectionState.Disconnected, connection.State);
        Assert.Equal("unauthorized", connection.LastDisconnectReason);
    }

    /// <summary>
    /// Sends server/hello and then a server/activate declaring <paramref name="activities"/>
    /// (comma-separated; empty for <c>[]</c>).
    /// </summary>
    private static void Activate(
        FakeSendspinConnection connection, PskCategory category, string activities, string activeRoles = "")
    {
        var set = activities.Split(',', StringSplitOptions.RemoveEmptyEntries);

        // 'pairing' requires the pairing object, and its method must be pairing_psk exactly
        // when the matched PSK is the pairing PSK. A method this default client cannot run is
        // answered with pair/abort and leaves the connection open, so it does not disturb what
        // is asserted here.
        string method = category == PskCategory.Pairing ? "pairing_psk" : "static_pairing_code";
        string pairing = set.Contains("pairing")
            ? $$$""","pairing":{"method":"{{{method}}}"}"""
            : string.Empty;
        string members = string.Join(",", set.Select(a => $"\"{a}\""));

        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        connection.RaiseTextMessageReceived(
            $$$"""{"type":"server/activate","payload":{"activities":[{{{members}}}],"active_roles":[{{{activeRoles}}}]{{{pairing}}}}}""");
    }
}
