using System.Text;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Connection.Noise.Pairing;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Covers <c>SendspinClientOptions.PresentPinAsync</c>: the derived dynamic PIN reaches the
/// app through an awaited delegate whose completion gates client/pair-auth, dynamic_pin is
/// refused (fail closed) when no presenter is configured, and the delegate receives the
/// client's own cancellation token rather than a defaulted one.
/// </summary>
public class PinPresentationTests
{
    private static readonly byte[] HandshakeHash = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static (SendspinClientService Client, FakeSendspinConnection Connection)
        CreateDynamicPinClient(Func<string, CancellationToken, ValueTask>? presentPin)
    {
        var (client, connection, session) = TestClient.Create(
            PskCategory.Sentinel,
            configure: options =>
            {
                options.Capabilities = new ClientCapabilities { PinPairingMethods = ["dynamic_pin"] };
                options.PairingRecordStore = new InMemoryPairingRecordStore();
                options.PinLockoutStore = new InMemoryPinLockoutStore();
                options.PresentPinAsync = presentPin;
            });

        // The CPace exchange is bound to the Noise handshake hash, which the test's server
        // counterpart derives from the same constant.
        session.HandshakeHash = HandshakeHash;
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        return (client, connection);
    }

    private static void ActivateDynamicPin(FakeSendspinConnection conn) =>
        conn.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"pairing":{"method":"dynamic_pin","pin_length":6}}}""");

    private static void SendServerPairInit(FakeSendspinConnection conn, byte[] nonceA, int pinLength) =>
        conn.RaiseTextMessageReceived(
            $$$"""{"type":"server/pair-init","payload":{"nonce_A":"{{{B64(nonceA)}}}","pin_length":{{{pinLength}}}}}""");

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
    public async Task DynamicPin_PresentationIsAwaited_BeforePairAuthIsSent()
    {
        string? presentedPin = null;
        bool presentationCompleted = false;
        var release = new TaskCompletionSource();
        var (client, conn) = CreateDynamicPinClient(async (pin, _) =>
        {
            presentedPin = pin;
            await release.Task;
            await Task.Yield();
            presentationCompleted = true;
        });
        using var _c = client;

        ActivateDynamicPin(conn);
        Assert.Contains(conn.SentMessages, m => m is ClientPairInitMessage);

        byte[] nonceA = Enumerable.Repeat((byte)0x42, 32).ToArray();
        SendServerPairInit(conn, nonceA, 6);

        Assert.NotNull(presentedPin);
        Assert.Equal(6, presentedPin!.Length);

        // Server side of the PAKE, keyed with the PIN the presenter received.
        byte[] sid = PinPairing.BuildSid(HandshakeHash, 1);
        var server = CPace.Start(CPaceRole.Initiator, Encoding.ASCII.GetBytes(presentedPin), sid, ad: PinPairing.AdServer);
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

        // And the PAKE completes against the presented PIN, proving the presenter received
        // the PIN the server can verify.
        server.Derive(PinPairing.DecodeB64Url(auth.Payload.PakeMsg2), PinPairing.AdClient);
        conn.RaiseTextMessageReceived(
            $$$"""{"type":"server/pair-confirm","payload":{"server_kc":"{{{B64(server.Tag())}}}"}}""");
        var confirm = await WaitForSentAsync<ClientPairConfirmMessage>(conn);
        Assert.True(server.Verify(PinPairing.DecodeB64Url(confirm.Payload.ClientKc)));
    }

    [Fact]
    public void DynamicPin_OfferedWithoutPresenter_IsRefused()
    {
        // Fail closed: dynamic_pin is configured and a lockout store is present, but there is
        // no PresentPinAsync, so the derived PIN could never reach the operator. The client
        // must refuse the method — pair/abort method_not_supported with the connection left
        // open, the same shape as the missing-lockout-store refusal — rather than running an
        // attempt whose PIN nobody ever saw.
        var (client, conn) = CreateDynamicPinClient(presentPin: null);
        using var _c = client;

        ActivateDynamicPin(conn);

        Assert.DoesNotContain(conn.SentMessages, m => m is ClientPairInitMessage);
        var abort = Assert.Single(conn.SentMessages.OfType<PairAbortMessage>());
        Assert.Equal("method_not_supported", abort.Payload.Reason);
        Assert.Equal(ConnectionState.Connected, conn.State);
    }

    [Fact]
    public void PresentPinAsync_ReceivesClientToken_CancelledOnDispose()
    {
        CancellationToken observed = default;
        var (client, conn) = CreateDynamicPinClient((_, ct) =>
        {
            observed = ct;

            // Never completes on its own: a real presenter may hold the PIN on screen until
            // told to stop, so releasing it must come from the token, not from the SDK
            // forgetting about it.
            return new ValueTask(new TaskCompletionSource().Task);
        });

        ActivateDynamicPin(conn);
        SendServerPairInit(conn, new byte[32], 6);

        // The token is the client's own, not a defaulted CancellationToken.None.
        Assert.True(observed.CanBeCanceled);
        Assert.False(observed.IsCancellationRequested);

        client.Dispose();

        Assert.True(observed.IsCancellationRequested);
    }
}
