using System.Text;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Connection.Noise.Pairing;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Covers <c>SendspinClientOptions.PresentPairingCodeAsync</c>: the derived dynamic pairing code reaches the
/// app through an awaited delegate whose completion gates client/pair-auth, dynamic_pin is
/// refused (fail closed) when no presenter is configured, and the delegate receives the
/// client's own cancellation token rather than a defaulted one.
/// </summary>
public class PairingCodePresentationTests
{
    private static readonly byte[] HandshakeHash = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static (SendspinClientService Client, FakeSendspinConnection Connection)
        CreateDynamicPairingCodeClient(Func<PairingCodePresentation, CancellationToken, ValueTask>? presentPairingCode)
    {
        var (client, connection, session) = TestClient.Create(
            PskCategory.Sentinel,
            configure: options => options with
            {
                Capabilities = new ClientCapabilities { PairingCodeMethods = ["dynamic_pin"] },
                PairingRecordStore = new InMemoryPairingRecordStore(),
                PairingCodeLockoutStore = new InMemoryPairingCodeLockoutStore(),
                PresentPairingCodeAsync = presentPairingCode,
            });

        // The CPace exchange is bound to the Noise handshake hash, which the test's server
        // counterpart derives from the same constant.
        session.HandshakeHash = HandshakeHash;
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        return (client, connection);
    }

    private static void ActivateDynamicPairingCode(FakeSendspinConnection conn) =>
        conn.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"pairing":{"method":"dynamic_pin","pin_length":6}}}""");

    private static void SendServerPairInit(FakeSendspinConnection conn, byte[] nonceA, int pairingCodeLength) =>
        conn.RaiseTextMessageReceived(
            $$$"""{"type":"server/pair-init","payload":{"nonce_A":"{{{B64(nonceA)}}}","pin_length":{{{pairingCodeLength}}}}}""");

    /// <summary>
    /// Polls for a sent message of type <typeparamref name="T"/>. The pair-auth reply is sent
    /// from the awaited presentation's continuation, which may run on a pool thread, so the
    /// fake's <c>SentMessages</c> list cannot always be asserted immediately.
    /// </summary>
    private static async Task<T> WaitForSentAsync<T>(FakeSendspinConnection conn) where T : class
    {
        for (int i = 0; i < 500; i++)
        {
            try
            {
                var msg = conn.SentMessages.OfType<T>().LastOrDefault();
                if (msg is not null)
                    return msg;
            }
            catch (InvalidOperationException)
            {
                // The sender's thread mutated the list mid-enumeration; retry.
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"Client never sent {typeof(T).Name}");
    }

    [Fact]
    public async Task DynamicPairingCode_PresentationIsAwaited_BeforePairAuthIsSent()
    {
        string? presentedPairingCode = null;
        bool presentationCompleted = false;
        var release = new TaskCompletionSource();
        var (client, conn) = CreateDynamicPairingCodeClient(async (presentation, _) =>
        {
            presentedPairingCode = presentation.PairingCode;
            await release.Task;
            await Task.Yield();
            presentationCompleted = true;
        });
        using var _c = client;

        ActivateDynamicPairingCode(conn);
        Assert.Contains(conn.SentMessages, m => m is ClientPairInitMessage);

        byte[] nonceA = Enumerable.Repeat((byte)0x42, 32).ToArray();
        SendServerPairInit(conn, nonceA, 6);

        Assert.NotNull(presentedPairingCode);
        Assert.Equal(6, presentedPairingCode!.Length);

        // Server side of the PAKE, keyed with the pairing code the presenter received.
        byte[] sid = PairingCodes.BuildSid(HandshakeHash, 1);
        var server = CPace.Start(CPaceRole.Initiator, Encoding.ASCII.GetBytes(presentedPairingCode), sid, ad: PairingCodes.AdServer);
        conn.RaiseTextMessageReceived(
            $$$"""{"type":"server/pair-auth","payload":{"pake_msg_1":"{{{B64(server.PublicShare)}}}"}}""");

        // The presentation has not completed, so the client must not have answered the
        // PAKE yet: a client/pair-auth here would mean the delegate was fired-and-forgotten,
        // not awaited.
        Assert.DoesNotContain(conn.SentMessages, m => m is ClientPairAuthMessage);

        release.SetResult();
        var auth = await WaitForSentAsync<ClientPairAuthMessage>(conn);

        // The flag flips only after the delegate's awaits, so its being set by the time
        // client/pair-auth went out proves the presentation was awaited to completion.
        Assert.True(presentationCompleted);

        // And the PAKE completes against the presented pairing code, proving the presenter received
        // the pairing code the server can verify.
        server.Derive(Base64UrlText.Decode(auth.Payload.PakeMsg2), PairingCodes.AdClient);
        conn.RaiseTextMessageReceived(
            $$$"""{"type":"server/pair-confirm","payload":{"server_kc":"{{{B64(server.Tag())}}}"}}""");
        var confirm = await WaitForSentAsync<ClientPairConfirmMessage>(conn);
        Assert.True(server.Verify(Base64UrlText.Decode(confirm.Payload.ClientKc)));
    }

    [Fact]
    public void DynamicPairingCode_OfferedWithoutPresenter_IsRefused()
    {
        // Fail closed: dynamic_pin is configured and a lockout store is present, but there is
        // no PresentPairingCodeAsync, so the derived pairing code could never reach the operator. The client
        // must refuse the method — pair/abort method_not_supported with the connection left
        // open, the same shape as the missing-lockout-store refusal — rather than running an
        // attempt whose pairing code nobody ever saw.
        var (client, conn) = CreateDynamicPairingCodeClient(presentPairingCode: null);
        using var _c = client;

        ActivateDynamicPairingCode(conn);

        Assert.DoesNotContain(conn.SentMessages, m => m is ClientPairInitMessage);
        var abort = Assert.Single(conn.SentMessages.OfType<PairAbortMessage>());
        Assert.Equal("method_not_supported", abort.Payload.Reason);
        Assert.Equal(ConnectionState.Connected, conn.State);
    }

    [Fact]
    public void PresentPairingCodeAsync_ReceivesClientToken_CancelledOnDispose()
    {
        CancellationToken observed = default;
        var (client, conn) = CreateDynamicPairingCodeClient((_, ct) =>
        {
            observed = ct;

            // Never completes on its own: a real presenter may hold the pairing code on screen until
            // told to stop, so releasing it must come from the token, not from the SDK
            // forgetting about it.
            return new ValueTask(new TaskCompletionSource().Task);
        });

        ActivateDynamicPairingCode(conn);
        SendServerPairInit(conn, new byte[32], 6);

        // The token is the client's own, not a defaulted CancellationToken.None.
        Assert.True(observed.CanBeCanceled);
        Assert.False(observed.IsCancellationRequested);

        client.Dispose();

        Assert.True(observed.IsCancellationRequested);
    }
}
