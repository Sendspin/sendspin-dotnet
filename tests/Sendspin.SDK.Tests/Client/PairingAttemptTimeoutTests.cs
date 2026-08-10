using Sendspin.SDK.Client;
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
    public async Task PinAttempt_ThatStalls_AbortsWithAttemptTimeout()
    {
        var window = new PairingWindow();
        window.Open();
        await using var h = await PairingHarness.StartAsync(
            staticPin: "12345678", window: window, attemptTimeout: Short);

        h.SendPairingActivate(method: "static_pin");
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
            staticPin: "12345678", window: new PairingWindow(), attemptTimeout: Short);

        h.SendPairingActivate(method: "static_pin");
        await h.NextMessageAsync<ClientPairPendingMessage>();

        await Task.Delay(Short + Short);
        Assert.Empty(h.SentOfType<PairAbortMessage>());
    }

    [Fact]
    public async Task CompletedAttempt_DoesNotAbortAfterwards()
    {
        var window = new PairingWindow();
        window.Open();
        await using var h = await PairingHarness.StartAsync(
            staticPin: "12345678", window: window, attemptTimeout: Short);

        h.SendPairingActivate(method: "static_pin");
        await h.CompleteStaticPinPairingAsync();

        await Task.Delay(Short + Short);
        Assert.Empty(h.SentOfType<PairAbortMessage>());
    }
}
