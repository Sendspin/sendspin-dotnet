using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// A pairing protocol error closes the connection with no <c>client/goodbye</c> (pairing.md
/// "Protocol Errors": "the detecting side closes the WebSocket without sending any
/// application-level error message, and persists nothing"). It used to say goodbye with
/// reason 'unauthorized', which tells the server its activation was refused (#318).
/// </summary>
public class PairingProtocolErrorCloseTests
{
    private const string StaticCode = "12345678";

    private static async Task<(PairingHarness Harness, ClientPairInitMessage Init)> StartStaticAttemptAsync(
        IPairingRecordStore? store = null)
    {
        var window = new PairingWindow();
        window.Open();
        var h = await PairingHarness.StartAsync(
            dynamicPairingCode: false, staticPairingCode: StaticCode, pairingStore: store, window: window);
        h.SendPairingActivate(method: "static_pairing_code");
        return (h, await h.NextMessageAsync<ClientPairInitMessage>());
    }

    private static void AssertClosedWithoutGoodbye(PairingHarness h)
    {
        Assert.Equal(ConnectionState.Disconnected, h.Client.ConnectionState);
        Assert.Null(h.LastDisconnectReason);
    }

    [Fact]
    public async Task LowOrderPakeShare_ClosesWithoutGoodbye()
    {
        var (h, _) = await StartStaticAttemptAsync();
        await using var _h = h;

        // The all-zero share encodes a low-order point.
        h.Receive($$$"""{"type":"server/pair-auth","payload":{"pake_msg_1":"{{{new string('A', 43)}}}"}}""");

        AssertClosedWithoutGoodbye(h);
        Assert.Empty(h.SentOfType<ClientPairAuthMessage>());
    }

    [Fact]
    public async Task PakeShareThatIsNotBase64Url_ClosesWithoutGoodbye()
    {
        var (h, _) = await StartStaticAttemptAsync();
        await using var _h = h;

        h.Receive("""{"type":"server/pair-auth","payload":{"pake_msg_1":"+/+/"}}""");

        AssertClosedWithoutGoodbye(h);
        Assert.Empty(h.SentOfType<ClientPairAuthMessage>());
    }

    [Theory]
    [InlineData("server/pair-init")]
    [InlineData("server/pair-auth")]
    [InlineData("server/pair-confirm")]
    [InlineData("pair/abort")]
    public async Task PairingMessageWithAMalformedPayload_ClosesWithoutGoodbye(string type)
    {
        var (h, _) = await StartStaticAttemptAsync();
        await using var _h = h;

        h.Receive($$$"""{"type":"{{{type}}}","payload":"not-an-object"}""");

        AssertClosedWithoutGoodbye(h);
    }

    [Fact]
    public async Task ProtocolErrorAfterThePskWasWrapped_PersistsNothing()
    {
        var store = new InMemoryPairingRecordStore();
        var (h, init) = await StartStaticAttemptAsync(store);
        await using var _h = h;

        // A whole exchange: the client has sent client/pair-finalize and holds the PSK it
        // would persist on server/pair-finalize.
        await h.RunServerPakeAsync(StaticCode, init.Payload.PairingIndex, round: 1);
        await h.NextMessageAsync<ClientPairFinalizeMessage>();

        h.Receive("""{"type":"server/pair-confirm","payload":{"server_kc":"+/+/"}}""");
        h.SendServerPairFinalize();

        AssertClosedWithoutGoodbye(h);
        Assert.DoesNotContain(store.List(), r => r.Category == PskCategory.LongTerm);
    }

    [Fact]
    public async Task MalformedNonPairingMessage_DuringPairing_StillSaysGoodbye()
    {
        // The silent close is the pairing rule. The spec gives other malformed messages no
        // such rule, and this client's close for them is unchanged.
        var (h, _) = await StartStaticAttemptAsync();
        await using var _h = h;

        h.Receive("""{"type":"server/activate","payload":"not-an-object"}""");

        Assert.Equal(ConnectionState.Disconnected, h.Client.ConnectionState);
        Assert.Equal("unauthorized", h.LastDisconnectReason);
    }
}
