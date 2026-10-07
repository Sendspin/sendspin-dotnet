using Sendspin.SDK.Client;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// A server/activate that does not declare 'pairing' ends an attempt in progress (pairing.md,
/// "Entering and leaving pairing"): "it persists nothing and discards any received PSK", and
/// "on receipt the client abandons the attempt, discarding all pairing state".
/// </summary>
public class PairingCancellingActivateTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(250);

    [Fact]
    public async Task PairingPskAttempt_CancelledByActivate_PersistsNothingOnALaterPairFinalize()
    {
        var store = new InMemoryPairingRecordStore();
        await using var h = await PairingHarness.StartAsync(pairingPsk: true, pairingStore: store);
        bool completed = false;
        h.Client.PairingCompleted += (_, _) => completed = true;

        h.SendPairingActivate(method: "pairing_psk");
        await h.NextMessageAsync<ClientPairFinalizeMessage>();

        h.SendNonPairingActivate();
        h.SendServerPairFinalize();

        Assert.DoesNotContain(store.List(), r => r.Category == PskCategory.LongTerm);
        Assert.False(completed);
    }

    [Fact]
    public async Task StartedAttempt_CancelledByActivate_DoesNotTimeOutAfterwards()
    {
        // The server ended the attempt, so there is nothing left for the client to abort: not
        // on the cancelling activate itself, and not when the attempt's timer would have fired.
        var window = new PairingWindow();
        window.Open();
        await using var h = await PairingHarness.StartAsync(
            staticPairingCode: "12345678", window: window, attemptTimeout: Short);

        h.SendPairingActivate(method: "static_pairing_code");
        await h.NextMessageAsync<ClientPairInitMessage>();

        h.SendNonPairingActivate();

        await Task.Delay(Short + Short);
        Assert.Empty(h.SentOfType<PairAbortMessage>());
    }

    [Fact]
    public async Task StartedAttempt_CancelledByActivate_DoesNotAnswerALaterPairAuth()
    {
        var window = new PairingWindow();
        window.Open();
        await using var h = await PairingHarness.StartAsync(staticPairingCode: "12345678", window: window);

        h.SendPairingActivate(method: "static_pairing_code");
        await h.NextMessageAsync<ClientPairInitMessage>();

        h.SendNonPairingActivate();
        h.SendServerPairAuth();

        // No wait: the static flow has no presentation to await, so the reply is sent during
        // the dispatch of server/pair-auth, which has returned.
        Assert.Empty(h.SentOfType<ClientPairAuthMessage>());
    }

    [Fact]
    public async Task DisplayedCode_IsReleasedWhenAnActivateCancelsTheAttempt()
    {
        // The presenter is still showing the code of an attempt that is over; its token is the
        // only way the app learns to take it down.
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var h = await PairingHarness.StartAsync(
            presentPairingCode: async (_, ct) =>
            {
                // Observed here rather than through a registration on the token: one disposed
                // as this presenter unwinds can be removed before the cancellation reaches it.
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    cancelled.TrySetResult();
                    throw;
                }
            });

        h.SendPairingActivate(method: "dynamic_pairing_code");
        await h.CompleteDynamicPairingCodeToPresentationAsync();

        // The client now holds client/pair-auth back on the presentation. When that is cancelled
        // it must not report the presentation as its own failure with a pair/abort.
        h.SendServerPairAuth();

        h.SendNonPairingActivate();

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(200);
        Assert.Empty(h.SentOfType<ClientPairAuthMessage>());
        Assert.Empty(h.SentOfType<PairAbortMessage>());
    }
}
