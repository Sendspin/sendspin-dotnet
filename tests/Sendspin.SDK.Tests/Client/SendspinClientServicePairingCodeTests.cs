using System.Text;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Connection.Noise.Pairing;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Drives the client service through the dynamic- and static-pairing code flows against
/// a CPace server counterpart, asserting the client emits the derived pairing code, completes
/// the PAKE, delivers a wrapped PSK the server can unwrap, and honors lockout.
/// </summary>
public class SendspinClientServicePinPairingTests
{
    private const string ServerId = FakeNoiseSession.FakeServerId;
    private static readonly byte[] HandshakeHash = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private static byte[] B64(string s) => Base64UrlText.Decode(s);
    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static (SendspinClientService, FakeSendspinConnection, InMemoryPairingCodeLockoutStore, InMemoryPairingRecordStore)
        CreateClient(
            ClientCapabilities caps,
            Func<PairingCodePresentation, CancellationToken, ValueTask>? presentPairingCode = null,
            NoiseCipherSuite suite = NoiseCipherSuite.ChaChaPoly)
    {
        var lockout = new InMemoryPairingCodeLockoutStore();
        var records = new InMemoryPairingRecordStore();

        // Pre-opened so a static_pairing_code attempt (always gesture-gated now) proceeds immediately,
        // exactly as it did before gating existed -- these tests are not about gating.
        var window = new PairingWindow();
        window.Open();
        var (client, connection, session) = TestClient.Create(
            PskCategory.Sentinel,
            configure: options => options with
            {
                Capabilities = caps,
                PairingRecordStore = records,
                PairingCodeLockoutStore = lockout,
                PresentPairingCodeAsync = presentPairingCode,
                PairingWindow = window,
                Suite = suite,
            });

        // The CPace exchange is bound to the Noise handshake hash, which the test's server
        // counterpart derives from the same constant.
        session.HandshakeHash = HandshakeHash;

        // Production wires one NoiseWireFraming in as both the framing and the session, so the
        // suite the options select and the suite the session reports are the same value; the
        // fake session has to be told separately.
        session.Suite = suite;
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        return (client, connection, lockout, records);
    }

    private static T Last<T>(FakeSendspinConnection c) where T : class => c.SentMessages.OfType<T>().Last();

    private static string ServerPairInit(string nonceA) =>
        $$$"""{"type":"server/pair-init","payload":{"nonce_A":"{{{nonceA}}}"}}""";

    private static string ServerPairAuth(string pakeMsg1) =>
        $$$"""{"type":"server/pair-auth","payload":{"pake_msg_1":"{{{pakeMsg1}}}"}}""";

    private static string ServerPairConfirm(string serverKc) =>
        $$$"""{"type":"server/pair-confirm","payload":{"server_kc":"{{{serverKc}}}"}}""";

    [Fact]
    public void DynamicPairingCode_PairAuthBeforePairInit_IsRejectedAsAProtocolError()
    {
        // The spec calls a mis-sequenced pairing message a protocol error: close, send no
        // application-level error, persist nothing. Without an explicit check the pairing code is still
        // null here and Encoding.ASCII.GetBytes(null) threw ArgumentNullException, which the
        // dispatch catch does not name -- so the connection died as an unexplained receive-loop
        // failure rather than a deliberate close (#106).
        var caps = new ClientCapabilities { PairingCodeMethods = { "dynamic_pairing_code" } };
        var (client, conn, _, _) = CreateClient(caps, (_, _) => ValueTask.CompletedTask);
        using var _c = client;

        conn.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"pairing":{"method":"dynamic_pairing_code","format":"digits"}}}""");
        Assert.NotEmpty(conn.SentMessages.OfType<ClientPairInitMessage>());

        // server/pair-auth without the server/pair-init that derives the pairing code.
        conn.RaiseTextMessageReceived(ServerPairAuth(B64(Enumerable.Repeat((byte)9, 32).ToArray())));

        Assert.Equal("unauthorized", conn.LastDisconnectReason);
        Assert.Empty(conn.SentMessages.OfType<ClientPairAuthMessage>());
    }

    [Fact]
    public void DynamicPairingCode_FullFlow_PresentsPairingCode_CompletesPake_DeliversUnwrappablePsk()
    {
        string? emittedPairingCode = null;
        var caps = new ClientCapabilities
        {
            PairingCodeMethods = { "dynamic_pairing_code" }
        };
        var (client, conn, _, _) = CreateClient(caps, (p, _) =>
        {
            emittedPairingCode = p.PairingCode;
            return ValueTask.CompletedTask;
        });
        using var _c = client;

        // Pairing activate selects dynamic_pairing_code and names the emission format.
        const int length = PairingCodes.DynamicPairingCodeLength;
        conn.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"pairing":{"method":"dynamic_pairing_code","format":"digits"}}}""");

        // Client sends pair-init with commit_B.
        var init = Last<ClientPairInitMessage>(conn);
        Assert.NotNull(init.Payload.CommitB);
        byte[] commitB = B64(init.Payload.CommitB!);

        // Server picks nonce_A, derives the same pairing code (always 6 digits).
        byte[] nonceA = Enumerable.Repeat((byte)0x42, 32).ToArray();
        conn.RaiseTextMessageReceived(
            ServerPairInit(B64(nonceA)));

        Assert.NotNull(emittedPairingCode);
        Assert.Equal(length, emittedPairingCode!.Length);

        // Server runs CPace as initiator with the operator-entered pairing code.
        byte[] sid = PairingCodes.BuildSid(HandshakeHash, 1);
        var server = CPace.Start(CPaceRole.Initiator, Encoding.ASCII.GetBytes(emittedPairingCode), sid, ad: PairingCodes.AdServer);
        conn.RaiseTextMessageReceived(
            ServerPairAuth(B64(server.PublicShare)));

        // Client replies with its share; server derives.
        var auth = Last<ClientPairAuthMessage>(conn);
        server.Derive(B64(auth.Payload.PakeMsg2), PairingCodes.AdClient);

        // Server sends its confirmation tag; client verifies and confirms + finalizes.
        conn.RaiseTextMessageReceived(
            ServerPairConfirm(B64(server.Tag())));

        var confirm = Last<ClientPairConfirmMessage>(conn);
        Assert.True(server.Verify(B64(confirm.Payload.ClientKc)));
        // Dynamic pairing code: client reveals nonce_B, and it opens the earlier commit.
        Assert.NotNull(confirm.Payload.NonceB);
        Assert.Equal(commitB, PairingCodes.CommitB(B64(confirm.Payload.NonceB!)));

        // Finalize carries a wrapped PSK the server unwraps with the shared ISK.
        var finalize = Last<ClientPairFinalizeMessage>(conn);
        Assert.NotNull(finalize.Payload.WrappedPsk);
        Assert.Null(finalize.Payload.LongTermPsk);
        byte[] wrapped = B64(finalize.Payload.WrappedPsk!);
        byte[] kWrap = System.Security.Cryptography.SHA256.HashData(
            [.. "sendspin-pair-psk-wrap-v1"u8.ToArray(), .. sid, .. server.Isk]);
        byte[] psk = new byte[32];
        using var chacha = new System.Security.Cryptography.ChaCha20Poly1305(kWrap);
        chacha.Decrypt(new byte[12], wrapped[..32], wrapped[32..], psk);
        Assert.Equal(32, psk.Length); // unwrap succeeded (would throw on a bad tag)
    }

    [Fact]
    public void DynamicPairingCode_UnrecognizedFormat_Aborts()
    {
        // The activation's format names how the client must emit the code. This SDK only
        // advertises (and only implements) digits, so any other format selects a flow it
        // cannot run -- caught at the activation, before any attempt starts.
        var caps = new ClientCapabilities { PairingCodeMethods = { "dynamic_pairing_code" } };
        var (client, conn, _, _) = CreateClient(caps, (_, _) => ValueTask.CompletedTask);
        using var _c = client;

        conn.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"pairing":{"method":"dynamic_pairing_code","format":"qr_code"}}}""");

        Assert.Equal("method_not_supported", Last<PairAbortMessage>(conn).Payload.Reason);
        Assert.Empty(conn.SentMessages.OfType<ClientPairInitMessage>());
    }

    [Fact]
    public void StaticPairingCode_FullFlow_UsesConfiguredPairingCode()
    {
        var caps = new ClientCapabilities { PairingCodeMethods = { "static_pairing_code" }, StaticPairingCode = "12345678" };
        var (client, conn, _, _) = CreateClient(caps);
        using var _c = client;

        conn.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"pairing":{"method":"static_pairing_code"}}}""");

        // Static pairing code: no commit_B in pair-init.
        var init = Last<ClientPairInitMessage>(conn);
        Assert.Null(init.Payload.CommitB);

        byte[] sid = PairingCodes.BuildSid(HandshakeHash, 1);
        var server = CPace.Start(CPaceRole.Initiator, Encoding.ASCII.GetBytes("12345678"), sid, ad: PairingCodes.AdServer);
        conn.RaiseTextMessageReceived(
            ServerPairAuth(B64(server.PublicShare)));
        var auth = Last<ClientPairAuthMessage>(conn);
        server.Derive(B64(auth.Payload.PakeMsg2), PairingCodes.AdClient);
        conn.RaiseTextMessageReceived(
            ServerPairConfirm(B64(server.Tag())));

        var confirm = Last<ClientPairConfirmMessage>(conn);
        Assert.True(server.Verify(B64(confirm.Payload.ClientKc)));
        Assert.Null(confirm.Payload.NonceB); // no reveal in static pairing code
        Assert.NotNull(Last<ClientPairFinalizeMessage>(conn).Payload.WrappedPsk);
    }

    [Theory]
    [InlineData(NoiseCipherSuite.ChaChaPoly)]
    [InlineData(NoiseCipherSuite.AesGcm)]
    public void WrappedPsk_IsSealedWithTheNegotiatedSuite(NoiseCipherSuite suite)
    {
        // pairing.md seals wrapped_psk with "the AEAD of the connection's negotiated cipher
        // suite". The call site hardcoded ChaCha20-Poly1305, so on an AES-GCM session -- chosen
        // explicitly, or by the automatic fallback on a platform without ChaCha20-Poly1305 --
        // the server's unwrap failed at the last step of every code-based pairing (#192).
        var caps = new ClientCapabilities { PairingCodeMethods = { "static_pairing_code" }, StaticPairingCode = "12345678" };
        var (client, conn, _, _) = CreateClient(caps, suite: suite);
        using var _c = client;

        conn.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"pairing":{"method":"static_pairing_code"}}}""");

        byte[] sid = PairingCodes.BuildSid(HandshakeHash, 1);
        var server = CPace.Start(CPaceRole.Initiator, Encoding.ASCII.GetBytes("12345678"), sid, ad: PairingCodes.AdServer);
        conn.RaiseTextMessageReceived(
            ServerPairAuth(B64(server.PublicShare)));
        server.Derive(B64(Last<ClientPairAuthMessage>(conn).Payload.PakeMsg2), PairingCodes.AdClient);
        conn.RaiseTextMessageReceived(
            ServerPairConfirm(B64(server.Tag())));

        byte[] wrapped = B64(Last<ClientPairFinalizeMessage>(conn).Payload.WrappedPsk!);
        byte[] kWrap = System.Security.Cryptography.SHA256.HashData(
            [.. "sendspin-pair-psk-wrap-v1"u8.ToArray(), .. sid, .. server.Isk]);

        // Unwraps under the negotiated suite, and only under it: without the second assertion a
        // suite-blind wrap still passes the ChaChaPoly case.
        Unwrap(suite, kWrap, wrapped);
        Assert.Throws<System.Security.Cryptography.AuthenticationTagMismatchException>(
            () => Unwrap(
                suite == NoiseCipherSuite.ChaChaPoly ? NoiseCipherSuite.AesGcm : NoiseCipherSuite.ChaChaPoly,
                kWrap,
                wrapped));
    }

    /// <summary>The server side of <c>PairingCodes.WrapPsk</c>: throws if the tag does not verify.</summary>
    private static void Unwrap(NoiseCipherSuite suite, byte[] kWrap, byte[] wrapped)
    {
        byte[] psk = new byte[32];
        if (suite == NoiseCipherSuite.AesGcm)
        {
            using var aes = new System.Security.Cryptography.AesGcm(kWrap, 16);
            aes.Decrypt(new byte[12], wrapped[..32], wrapped[32..], psk);
        }
        else
        {
            using var chacha = new System.Security.Cryptography.ChaCha20Poly1305(kWrap);
            chacha.Decrypt(new byte[12], wrapped[..32], wrapped[32..], psk);
        }
    }

    [Fact]
    public void WrongServerTag_AbortsWithPairingCodeMismatch_AndCountsFailure()
    {
        var caps = new ClientCapabilities { PairingCodeMethods = { "static_pairing_code" }, StaticPairingCode = "12345678" };
        var (client, conn, lockout, _) = CreateClient(caps);
        using var _c = client;

        conn.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"pairing":{"method":"static_pairing_code"}}}""");
        // Server runs CPace with the WRONG pin, so its confirmation tag won't verify.
        byte[] sid = PairingCodes.BuildSid(HandshakeHash, 1);
        var server = CPace.Start(CPaceRole.Initiator, Encoding.ASCII.GetBytes("00000000"), sid, ad: PairingCodes.AdServer);
        conn.RaiseTextMessageReceived(
            ServerPairAuth(B64(server.PublicShare)));
        var auth = Last<ClientPairAuthMessage>(conn);
        server.Derive(B64(auth.Payload.PakeMsg2), PairingCodes.AdClient);
        conn.RaiseTextMessageReceived(
            ServerPairConfirm(B64(server.Tag())));

        Assert.Equal("pairing_code_mismatch", Last<PairAbortMessage>(conn).Payload.Reason);
        Assert.Equal(1, lockout.GetFailures("static_pairing_code"));
        Assert.DoesNotContain(conn.SentMessages, m => m is ClientPairFinalizeMessage);
    }

    [Fact]
    public void EscalatedMethod_IsGatedNotAborted_AndStaysAdvertised()
    {
        // Escalation state is not part of the client/hello descriptor (no spec field for it);
        // it only shows up as a gesture gate once the server actually selects the escalated
        // method. Escalation is signalled that way, not by hiding the method: an escalated
        // method stays advertised, so the descriptor must still be there. Dynamic pairing code with a
        // The dynamic method isolates escalation as the gating cause -- static_pairing_code is
        // gated regardless of its failure count, so it cannot tell escalation apart from the
        // method's own baseline policy.
        var caps = new ClientCapabilities { PairingCodeMethods = { "dynamic_pairing_code" } };
        var lockout = new InMemoryPairingCodeLockoutStore();
        lockout.SetFailures("dynamic_pairing_code", 10);
        var (client, connection, _) = TestClient.Create(
            PskCategory.Sentinel,
            configure: options => options with
            {
                Capabilities = caps,
                PairingRecordStore = new InMemoryPairingRecordStore(),
                PairingCodeLockoutStore = lockout,
                PresentPairingCodeAsync = (_, _) => ValueTask.CompletedTask,
                PairingWindow = new PairingWindow(),
            });
        using var _c = client;
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");

        var hello = connection.SentMessages.OfType<ClientHelloMessage>().Single();
        Assert.Contains("dynamic_pairing_code", hello.Payload.SupportedPairMethods!.Keys);

        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"pairing":{"method":"dynamic_pairing_code","format":"digits"}}}""");

        // Gated, not refused: a pending signal, no abort of any kind, and no init -- gating
        // that signalled pending and then proceeded ungated anyway must fail this too.
        Assert.NotEmpty(connection.SentMessages.OfType<ClientPairPendingMessage>());
        Assert.DoesNotContain(connection.SentMessages, m => m is PairAbortMessage);
        Assert.Empty(connection.SentMessages.OfType<ClientPairInitMessage>());
    }

    [Fact]
    public void PairingCounter_RestartsAfterReHandshake()
    {
        // PairingCodes.BuildSid defines the counter as pairing activates *since the last
        // Noise handshake*. A client-lifetime counter diverges from a conformant
        // server's after any key rotation, and CPace then fails.
        var (client, connection, session) = TestClient.Create(
            PskCategory.Sentinel,
            configure: options => options with
            {
                Capabilities = new ClientCapabilities { PairingCodeMethods = ["dynamic_pairing_code"] },
                PairingRecordStore = new InMemoryPairingRecordStore(),
                PairingCodeLockoutStore = new InMemoryPairingCodeLockoutStore(),
                PresentPairingCodeAsync = (_, _) => ValueTask.CompletedTask,
            });
        using var _c = client;
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");

        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"pairing":{"method":"dynamic_pairing_code","format":"digits"}}}""");
        var first = connection.SentMessages.OfType<ClientPairInitMessage>().Last();
        Assert.Equal(1, first.Payload.PairingIndex);

        // Simulate an in-band re-handshake: the framing layer installs new transport
        // keys and a new handshake hash.
        session.HandshakeHash = new byte[32] { 9, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                                               0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"pairing":{"method":"dynamic_pairing_code","format":"digits"}}}""");
        var second = connection.SentMessages.OfType<ClientPairInitMessage>().Last();

        Assert.Equal(1, second.Payload.PairingIndex);
    }

    [Fact]
    public void PairingCounter_KeepsIncrementing_WhenHandshakeHashUnchanged()
    {
        // A conformant server may issue a second pairing server/activate under the SAME
        // Noise handshake (e.g. retrying method selection). The counter must keep
        // incrementing in that case, not reset — only a changed handshake hash is a
        // re-handshake signal. This guards the other half of the reset condition that
        // PairingCounter_RestartsAfterReHandshake does not: that one only ever asserts
        // PairingIndex == 1, so an "always reset" implementation would pass it too.
        var (client, connection, _) = TestClient.Create(
            PskCategory.Sentinel,
            configure: options => options with
            {
                Capabilities = new ClientCapabilities { PairingCodeMethods = ["dynamic_pairing_code"] },
                PairingRecordStore = new InMemoryPairingRecordStore(),
                PairingCodeLockoutStore = new InMemoryPairingCodeLockoutStore(),
                PresentPairingCodeAsync = (_, _) => ValueTask.CompletedTask,
            });
        using var _c = client;
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");

        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"pairing":{"method":"dynamic_pairing_code","format":"digits"}}}""");
        var first = connection.SentMessages.OfType<ClientPairInitMessage>().Last();
        Assert.Equal(1, first.Payload.PairingIndex);

        // No mutation of session.HandshakeHash here — that is the point of this test.
        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"pairing":{"method":"dynamic_pairing_code","format":"digits"}}}""");
        var second = connection.SentMessages.OfType<ClientPairInitMessage>().Last();

        Assert.Equal(2, second.Payload.PairingIndex);
    }

    [Fact]
    public void DynamicPairingCode_WithNoLockoutStore_IsRefused()
    {
        // With no store, IsMethodEscalated evaluates (null?.GetFailures() ?? 0) >= 10 —
        // always false — and RecordPairingCodeFailure returns early. So pairing code attempts are unlimited
        // and could never escalate to gesture-gating. Refuse the method instead.
        var (client, connection, _) = TestClient.Create(
            PskCategory.Sentinel,
            configure: options => options with
            {
                Capabilities = new ClientCapabilities { PairingCodeMethods = ["dynamic_pairing_code"] },

                // A presenter IS configured, so the refusal below is attributable to the
                // missing lockout store alone.
                PresentPairingCodeAsync = (_, _) => ValueTask.CompletedTask,
            });
        using var _c = client;
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");

        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"pairing":{"method":"dynamic_pairing_code"}}}""");

        Assert.DoesNotContain(connection.SentMessages, m => m is ClientPairInitMessage);
        var abort = Assert.Single(connection.SentMessages.OfType<PairAbortMessage>());
        Assert.Equal("method_not_supported", abort.Payload.Reason);
        Assert.Equal(ConnectionState.Connected, connection.State);
    }

    [Fact]
    public void DynamicPairingCode_WithALockoutStore_IsStillOffered()
    {
        // Positive control: the runnability clauses could refuse pairing code entirely and the test
        // above would still pass. Every dependency CanRun requires is present here — lockout
        // store, presenter, and (since #158) a record store.
        var (client, connection, _) = TestClient.Create(
            PskCategory.Sentinel,
            configure: options => options with
            {
                Capabilities = new ClientCapabilities { PairingCodeMethods = ["dynamic_pairing_code"] },
                PairingRecordStore = new InMemoryPairingRecordStore(),
                PairingCodeLockoutStore = new InMemoryPairingCodeLockoutStore(),
                PresentPairingCodeAsync = (_, _) => ValueTask.CompletedTask,
            });
        using var _c = client;
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");

        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"pairing":{"method":"dynamic_pairing_code","format":"digits"}}}""");

        Assert.Single(connection.SentMessages.OfType<ClientPairInitMessage>());
        Assert.DoesNotContain(connection.SentMessages, m => m is PairAbortMessage);
    }

    [Fact]
    public void FilePairingCodeLockoutStore_PersistsCountersAcrossInstances()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sendspin-lockout-" + Guid.NewGuid().ToString("N")[..8]);
        string path = Path.Combine(dir, "lockout.json");
        try
        {
            new FilePairingCodeLockoutStore(path).SetFailures("dynamic_pairing_code", 3);

            Assert.Equal(3, new FilePairingCodeLockoutStore(path).GetFailures("dynamic_pairing_code"));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FilePairingCodeLockoutStore_NarrowsALegacyWorldReadableFile()
    {
        // Counters are not secrets but they are security state: anyone who can rewrite this file
        // resets the lockout. A file from an earlier SDK version keeps its 0644 mode until a
        // failed pairing code attempt replaces the inode, so the load path narrows it.
        string dir = Path.Combine(Path.GetTempPath(), "sendspin-lockout-" + Guid.NewGuid().ToString("N")[..8]);
        string path = Path.Combine(dir, "lockout.json");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, """{"dynamic_pairing_code":2}""");

            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(2, new FilePairingCodeLockoutStore(path).GetFailures("dynamic_pairing_code"));
            }
            else
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.GroupRead | UnixFileMode.OtherRead);

                var store = new FilePairingCodeLockoutStore(path);

                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
                Assert.Equal(2, store.GetFailures("dynamic_pairing_code"));
            }
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
