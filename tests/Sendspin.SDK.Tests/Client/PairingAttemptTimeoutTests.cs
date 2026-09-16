using Sendspin.SDK.Client;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// The client bounds each pairing attempt from its first message (pairing.md:26) and aborts
/// with attempt_timeout on expiry. Nothing implemented this before; the pairing window also
/// closes on attempt-timeout expiry, so without it that close condition could never fire.
/// </summary>
public class PairingAttemptTimeoutTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(250);

    [Fact]
    public async Task PairingCodeAttempt_ThatStalls_AbortsWithAttemptTimeout()
    {
        var window = new PairingWindow();
        window.Open();
        await using var h = await PairingHarness.StartAsync(
            staticPairingCode: "12345678", window: window, attemptTimeout: Short);

        h.SendPairingActivate(method: "static_pairing_code");
        await h.NextMessageAsync<ClientPairInitMessage>();

        // Server never replies. The attempt must not hang forever.
        var abort = await h.NextMessageAsync<PairAbortMessage>(TimeSpan.FromSeconds(5));
        Assert.Equal("attempt_timeout", abort.Payload.Reason);
    }

    [Fact]
    public async Task PairPending_DoesNotArmTheAttemptTimeout()
    {
        // pair-pending precedes an attempt and does not start it, so a client waiting on a
        // gesture must not abort itself.
        await using var h = await PairingHarness.StartAsync(
            staticPairingCode: "12345678", window: new PairingWindow(), attemptTimeout: Short);

        h.SendPairingActivate(method: "static_pairing_code");
        await h.NextMessageAsync<ClientPairPendingMessage>();

        await Task.Delay(Short + Short);
        Assert.Empty(h.SentOfType<PairAbortMessage>());
    }

    [Fact]
    public async Task PairingPskAttempt_ThatTimesOut_PersistsNothingOnALaterPairFinalize()
    {
        // The timeout is armed for the Pairing PSK flow too, so its expiry has to end that
        // flow's state as well. HandleServerPairFinalize's only gate is "the pending PSK is
        // not null" -- no activity, trust or session check -- so a PSK left armed by an
        // aborted attempt still persists a permanent record on any later bare pair-finalize.
        var store = new InMemoryPairingRecordStore();
        await using var h = await PairingHarness.StartAsync(
            pairingPsk: true, pairingStore: store, attemptTimeout: Short);

        h.SendPairingActivate(method: "pairing_psk");
        await h.NextMessageAsync<ClientPairFinalizeMessage>();

        var abort = await h.NextMessageAsync<PairAbortMessage>(TimeSpan.FromSeconds(5));
        Assert.Equal("attempt_timeout", abort.Payload.Reason);

        h.SendServerPairFinalize();

        await Task.Delay(200);
        Assert.DoesNotContain(store.List(), r => r.Category == PskCategory.LongTerm);
    }

    [Fact]
    public async Task CompletedAttempt_DoesNotAbortAfterwards()
    {
        var window = new PairingWindow();
        window.Open();
        await using var h = await PairingHarness.StartAsync(
            staticPairingCode: "12345678", window: window, attemptTimeout: Short);

        h.SendPairingActivate(method: "static_pairing_code");
        await h.CompleteStaticPairingCodeAsync();

        await Task.Delay(Short + Short);
        Assert.Empty(h.SentOfType<PairAbortMessage>());
    }
}
