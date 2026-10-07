using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Connection.Noise.Pairing;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// A dynamic pairing code attempt runs rounds against the same pairing code (pairing.md
/// "Rounds"): a failed server_kc is answered with client/pair-retry, the code stays put and the
/// round number in the CPace sid advances. Rounds count toward the hold-back from the moment
/// their code is emitted, whether or not they are ever confirmed.
/// </summary>
public class PairingRoundTests
{
    [Fact]
    public async Task FailedServerKc_SendsPairRetry_AndTheNextRoundRunsAsRound2WithTheSameCode()
    {
        var lockouts = new InMemoryPairingCodeLockoutStore();
        await using var h = await PairingHarness.StartAsync(lockouts: lockouts);
        h.SendPairingActivate(method: "dynamic_pairing_code");
        var init = await h.NextMessageAsync<ClientPairInitMessage>();

        // Round 1: the operator mistypes the code.
        h.SendServerPairInit();
        string code = await h.WaitForNextPresentedPairingCodeAsync();
        string wrong = code == "000000" ? "000001" : "000000";
        await h.RunServerPakeAsync(wrong, init.Payload.PairingIndex, round: 1);

        await h.NextMessageAsync<ClientPairRetryMessage>();
        Assert.Empty(h.SentOfType<PairAbortMessage>());
        Assert.Empty(h.SentOfType<ClientPairConfirmMessage>());

        // Round 2: no nonce_A, the same code emitted again, and a sid carrying round 2 -- a
        // client still on round 1 would fail this server's tag and never confirm.
        h.SendServerPairInit(withNonce: false);
        Assert.Equal(code, await h.WaitForNextPresentedPairingCodeAsync());
        var server = await h.RunServerPakeAsync(code, init.Payload.PairingIndex, round: 2);

        var confirm = await h.NextMessageAsync<ClientPairConfirmMessage>();
        Assert.True(server.Verify(Base64UrlText.Decode(confirm.Payload.ClientKc)));
        await h.NextMessageAsync<ClientPairFinalizeMessage>();
        Assert.Equal(0, lockouts.GetFailures("dynamic_pairing_code"));
    }

    [Fact]
    public async Task ServerPairAuth_AfterARetry_WithoutANewServerPairInit_IsAProtocolError()
    {
        // Each CPace run is one guess at the pairing code, and the round is counted toward the
        // hold-back at server/pair-init. A peer that skipped it after a retry would guess for
        // the rest of the attempt without the counter ever moving.
        var lockouts = new InMemoryPairingCodeLockoutStore();
        await using var h = await PairingHarness.StartAsync(lockouts: lockouts);
        h.SendPairingActivate(method: "dynamic_pairing_code");
        var init = await h.NextMessageAsync<ClientPairInitMessage>();
        h.SendServerPairInit();
        string code = await h.WaitForNextPresentedPairingCodeAsync();
        string wrong = code == "000000" ? "000001" : "000000";
        await h.RunServerPakeAsync(wrong, init.Payload.PairingIndex, round: 1);
        await h.NextMessageAsync<ClientPairRetryMessage>();

        h.SendServerPairAuth();

        Assert.Equal(ConnectionState.Disconnected, h.Client.ConnectionState);
        Assert.Null(h.LastDisconnectReason);
        Assert.Single(h.SentOfType<ClientPairAuthMessage>());
        Assert.Equal(1, lockouts.GetFailures("dynamic_pairing_code"));
    }

    [Fact]
    public async Task SecondServerPairAuth_InOneRound_IsAProtocolError()
    {
        await using var h = await PairingHarness.StartAsync();
        h.SendPairingActivate(method: "dynamic_pairing_code");
        await h.CompleteDynamicPairingCodeToPresentationAsync();
        h.SendServerPairAuth();
        await h.NextMessageAsync<ClientPairAuthMessage>();

        h.SendServerPairAuth();

        Assert.Equal(ConnectionState.Disconnected, h.Client.ConnectionState);
        Assert.Null(h.LastDisconnectReason);
        Assert.Single(h.SentOfType<ClientPairAuthMessage>());
    }

    [Fact]
    public async Task FailedServerKc_WhenHeldBack_AbortsRatherThanRetries()
    {
        // "MUST abort instead at the round limit": this round is the one that reaches it.
        var lockouts = new InMemoryPairingCodeLockoutStore();
        lockouts.SetFailures("dynamic_pairing_code", 9);
        await using var h = await PairingHarness.StartAsync(lockouts: lockouts);
        h.SendPairingActivate(method: "dynamic_pairing_code");
        var init = await h.NextMessageAsync<ClientPairInitMessage>();

        h.SendServerPairInit();
        string code = await h.WaitForNextPresentedPairingCodeAsync();
        string wrong = code == "000000" ? "000001" : "000000";
        await h.RunServerPakeAsync(wrong, init.Payload.PairingIndex, round: 1);

        var abort = await h.NextMessageAsync<PairAbortMessage>();
        Assert.Equal("pairing_code_mismatch", abort.Payload.Reason);
        Assert.Empty(h.SentOfType<ClientPairRetryMessage>());
        Assert.Equal(10, lockouts.GetFailures("dynamic_pairing_code"));
    }

    [Fact]
    public async Task FailedServerKc_InTheStaticFlow_AbortsRatherThanRetries()
    {
        // The Static Pairing Code Flow has no client/pair-retry: it is always round 1.
        var window = new PairingWindow();
        window.Open();
        await using var h = await PairingHarness.StartAsync(staticPairingCode: "12345678", window: window);
        h.SendPairingActivate(method: "static_pairing_code");
        var init = await h.NextMessageAsync<ClientPairInitMessage>();

        await h.RunServerPakeAsync("00000000", init.Payload.PairingIndex, round: 1);

        var abort = await h.NextMessageAsync<PairAbortMessage>();
        Assert.Equal("pairing_code_mismatch", abort.Payload.Reason);
        Assert.Empty(h.SentOfType<ClientPairRetryMessage>());
    }

    [Fact]
    public async Task RoundAbandonedAfterTheCodeWasEmitted_CountsTowardTheHoldBack()
    {
        // An abandoned attempt "counts toward the round limit only when the code was already
        // being emitted". Counting at the failed server_kc alone let a peer collect codes and
        // cancel before confirming, for free.
        var lockouts = new InMemoryPairingCodeLockoutStore();
        await using var h = await PairingHarness.StartAsync(lockouts: lockouts);
        h.SendPairingActivate(method: "dynamic_pairing_code");
        await h.CompleteDynamicPairingCodeToPresentationAsync();

        h.SendNonPairingActivate();

        Assert.Equal(1, lockouts.GetFailures("dynamic_pairing_code"));
    }

    [Fact]
    public async Task AttemptAbandonedBeforeTheCodeWasEmitted_DoesNotCountTowardTheHoldBack()
    {
        var lockouts = new InMemoryPairingCodeLockoutStore();
        await using var h = await PairingHarness.StartAsync(lockouts: lockouts);
        h.SendPairingActivate(method: "dynamic_pairing_code");
        await h.NextMessageAsync<ClientPairInitMessage>();

        h.SendNonPairingActivate();

        Assert.Equal(0, lockouts.GetFailures("dynamic_pairing_code"));
    }

    [Fact]
    public async Task FirstServerPairInit_WithoutNonceA_IsAProtocolError()
    {
        // nonce_A is optional on the wire because later rounds omit it, but the first round
        // cannot derive a pairing code without one: a protocol error, not a crash.
        int presented = 0;
        await using var h = await PairingHarness.StartAsync(
            presentPairingCode: (_, _) => { presented++; return ValueTask.CompletedTask; });
        h.SendPairingActivate(method: "dynamic_pairing_code");
        await h.NextMessageAsync<ClientPairInitMessage>();

        h.SendServerPairInit(withNonce: false);

        Assert.Equal(0, presented);
        Assert.Equal(ConnectionState.Disconnected, h.Client.ConnectionState);
        Assert.Null(h.LastDisconnectReason);
    }
}
