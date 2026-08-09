using System.Text;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Connection.Noise.Pairing;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Drives the client service through the dynamic- and static-PIN pairing flows against
/// a CPace server counterpart, asserting the client emits the derived PIN, completes
/// the PAKE, delivers a wrapped PSK the server can unwrap, and honors lockout.
/// </summary>
public class SendspinClientServicePinPairingTests
{
    private const string ServerId = FakeNoiseSession.FakeServerId;
    private static readonly byte[] HandshakeHash = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private static byte[] B64(string s) => PinPairing.DecodeB64Url(s);
    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static (SendspinClientService, FakeSendspinConnection, InMemoryPinLockoutStore, InMemoryPairingRecordStore)
        CreateClient(ClientCapabilities caps, Func<string, CancellationToken, ValueTask>? presentPin = null)
    {
        var lockout = new InMemoryPinLockoutStore();
        var records = new InMemoryPairingRecordStore();
        var (client, connection, session) = TestClient.Create(
            PskCategory.Sentinel,
            configure: options =>
            {
                options.Capabilities = caps;
                options.PairingRecordStore = records;
                options.PinLockoutStore = lockout;
                options.PresentPinAsync = presentPin;
            });

        // The CPace exchange is bound to the Noise handshake hash, which the test's server
        // counterpart derives from the same constant.
        session.HandshakeHash = HandshakeHash;
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        return (client, connection, lockout, records);
    }

    private static T Last<T>(FakeSendspinConnection c) where T : class => c.SentMessages.OfType<T>().Last();

    private static string ServerPairInit(string nonceA, int pinLength) =>
        $$$"""{"type":"server/pair-init","payload":{"nonce_A":"{{{nonceA}}}","pin_length":{{{pinLength}}}}}""";

    private static string ServerPairAuth(string pakeMsg1) =>
        $$$"""{"type":"server/pair-auth","payload":{"pake_msg_1":"{{{pakeMsg1}}}"}}""";

    private static string ServerPairConfirm(string serverKc) =>
        $$$"""{"type":"server/pair-confirm","payload":{"server_kc":"{{{serverKc}}}"}}""";

    [Fact]
    public void DynamicPin_FullFlow_PresentsPin_CompletesPake_DeliversUnwrappablePsk()
    {
        string? emittedPin = null;
        var caps = new ClientCapabilities
        {
            PinPairingMethods = { "dynamic_pin" },
            MinPinLength = 6,
        };
        var (client, conn, _, _) = CreateClient(caps, (p, _) =>
        {
            emittedPin = p;
            return ValueTask.CompletedTask;
        });
        using var _c = client;

        // Pairing activate selects dynamic_pin.
        conn.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"selected_pair_method":"dynamic_pin"}}""");

        // Client sends pair-init with commit_B.
        var init = Last<ClientPairInitMessage>(conn);
        Assert.NotNull(init.Payload.CommitB);
        byte[] commitB = B64(init.Payload.CommitB!);

        // Server picks nonce_A and pin_length, derives the same PIN.
        byte[] nonceA = Enumerable.Repeat((byte)0x42, 32).ToArray();
        const int length = 6;
        conn.RaiseTextMessageReceived(
            ServerPairInit(B64(nonceA), length));

        Assert.NotNull(emittedPin);
        Assert.Equal(length, emittedPin!.Length);

        // Server runs CPace as initiator with the operator-entered PIN.
        byte[] sid = PinPairing.BuildSid(HandshakeHash, 1);
        var server = CPace.Start(CPaceRole.Initiator, Encoding.ASCII.GetBytes(emittedPin), sid, ad: PinPairing.AdServer);
        conn.RaiseTextMessageReceived(
            ServerPairAuth(B64(server.PublicShare)));

        // Client replies with its share; server derives.
        var auth = Last<ClientPairAuthMessage>(conn);
        server.Derive(B64(auth.Payload.PakeMsg2), PinPairing.AdClient);

        // Server sends its confirmation tag; client verifies and confirms + finalizes.
        conn.RaiseTextMessageReceived(
            ServerPairConfirm(B64(server.Tag())));

        var confirm = Last<ClientPairConfirmMessage>(conn);
        Assert.True(server.Verify(B64(confirm.Payload.ClientKc)));
        // Dynamic PIN: client reveals nonce_B, and it opens the earlier commit.
        Assert.NotNull(confirm.Payload.NonceB);
        Assert.Equal(commitB, PinPairing.CommitB(B64(confirm.Payload.NonceB!)));

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
    public void DynamicPin_PinLengthBelowMinimum_Aborts()
    {
        var caps = new ClientCapabilities { PinPairingMethods = { "dynamic_pin" }, MinPinLength = 8 };
        var (client, conn, _, _) = CreateClient(caps, (_, _) => ValueTask.CompletedTask);
        using var _c = client;

        conn.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"selected_pair_method":"dynamic_pin"}}""");
        conn.RaiseTextMessageReceived(
            ServerPairInit(B64(new byte[32]), 6));

        Assert.Equal("pin_length_unacceptable", Last<PairAbortMessage>(conn).Payload.Reason);
    }

    [Fact]
    public void StaticPin_FullFlow_UsesConfiguredPin()
    {
        var caps = new ClientCapabilities { PinPairingMethods = { "static_pin" }, StaticPin = "12345678" };
        var (client, conn, _, _) = CreateClient(caps);
        using var _c = client;

        conn.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"selected_pair_method":"static_pin"}}""");

        // Static PIN: no commit_B in pair-init.
        var init = Last<ClientPairInitMessage>(conn);
        Assert.Null(init.Payload.CommitB);

        byte[] sid = PinPairing.BuildSid(HandshakeHash, 1);
        var server = CPace.Start(CPaceRole.Initiator, Encoding.ASCII.GetBytes("12345678"), sid, ad: PinPairing.AdServer);
        conn.RaiseTextMessageReceived(
            ServerPairAuth(B64(server.PublicShare)));
        var auth = Last<ClientPairAuthMessage>(conn);
        server.Derive(B64(auth.Payload.PakeMsg2), PinPairing.AdClient);
        conn.RaiseTextMessageReceived(
            ServerPairConfirm(B64(server.Tag())));

        var confirm = Last<ClientPairConfirmMessage>(conn);
        Assert.True(server.Verify(B64(confirm.Payload.ClientKc)));
        Assert.Null(confirm.Payload.NonceB); // no reveal in static PIN
        Assert.NotNull(Last<ClientPairFinalizeMessage>(conn).Payload.WrappedPsk);
    }

    [Fact]
    public void WrongServerTag_AbortsWithPinMismatch_AndCountsFailure()
    {
        var caps = new ClientCapabilities { PinPairingMethods = { "static_pin" }, StaticPin = "12345678" };
        var (client, conn, lockout, _) = CreateClient(caps);
        using var _c = client;

        conn.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"selected_pair_method":"static_pin"}}""");
        // Server runs CPace with the WRONG pin, so its confirmation tag won't verify.
        byte[] sid = PinPairing.BuildSid(HandshakeHash, 1);
        var server = CPace.Start(CPaceRole.Initiator, Encoding.ASCII.GetBytes("00000000"), sid, ad: PinPairing.AdServer);
        conn.RaiseTextMessageReceived(
            ServerPairAuth(B64(server.PublicShare)));
        var auth = Last<ClientPairAuthMessage>(conn);
        server.Derive(B64(auth.Payload.PakeMsg2), PinPairing.AdClient);
        conn.RaiseTextMessageReceived(
            ServerPairConfirm(B64(server.Tag())));

        Assert.Equal("pin_mismatch", Last<PairAbortMessage>(conn).Payload.Reason);
        Assert.Equal(1, lockout.GetFailures("static_pin"));
        Assert.DoesNotContain(conn.SentMessages, m => m is ClientPairFinalizeMessage);
    }

    [Fact]
    public void LockedOutMethod_AbortsImmediately()
    {
        // Lockout state is not part of the client/hello descriptor (no spec field for it);
        // it only shows up as the pair/abort reason once the server actually selects the
        // locked-out method.
        var caps = new ClientCapabilities { PinPairingMethods = { "static_pin" }, StaticPin = "12345678" };
        var lockout = new InMemoryPinLockoutStore();
        lockout.SetFailures("static_pin", 10);
        var (client, connection, _) = TestClient.Create(
            PskCategory.Sentinel,
            configure: options =>
            {
                options.Capabilities = caps;
                options.PairingRecordStore = new InMemoryPairingRecordStore();
                options.PinLockoutStore = lockout;
            });
        using var _c = client;
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");

        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"selected_pair_method":"static_pin"}}""");
        Assert.Equal("locked_out", Last<PairAbortMessage>(connection).Payload.Reason);
    }

    [Fact]
    public void PairingCounter_RestartsAfterReHandshake()
    {
        // PinPairing.BuildSid defines the counter as pairing activates *since the last
        // Noise handshake*. A client-lifetime counter diverges from a conformant
        // server's after any key rotation, and CPace then fails.
        var (client, connection, session) = TestClient.Create(
            PskCategory.Sentinel,
            configure: options =>
            {
                options.Capabilities = new ClientCapabilities { PinPairingMethods = ["dynamic_pin"] };
                options.PinLockoutStore = new InMemoryPinLockoutStore();
                options.PresentPinAsync = (_, _) => ValueTask.CompletedTask;
            });
        using var _c = client;
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");

        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"selected_pair_method":"dynamic_pin"}}""");
        var first = connection.SentMessages.OfType<ClientPairInitMessage>().Last();
        Assert.Equal(1, first.Payload.PairingIndex);

        // Simulate an in-band re-handshake: the framing layer installs new transport
        // keys and a new handshake hash.
        session.HandshakeHash = new byte[32] { 9, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                                               0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"selected_pair_method":"dynamic_pin"}}""");
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
            configure: options =>
            {
                options.Capabilities = new ClientCapabilities { PinPairingMethods = ["dynamic_pin"] };
                options.PinLockoutStore = new InMemoryPinLockoutStore();
                options.PresentPinAsync = (_, _) => ValueTask.CompletedTask;
            });
        using var _c = client;
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");

        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"selected_pair_method":"dynamic_pin"}}""");
        var first = connection.SentMessages.OfType<ClientPairInitMessage>().Last();
        Assert.Equal(1, first.Payload.PairingIndex);

        // No mutation of session.HandshakeHash here — that is the point of this test.
        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"selected_pair_method":"dynamic_pin"}}""");
        var second = connection.SentMessages.OfType<ClientPairInitMessage>().Last();

        Assert.Equal(2, second.Payload.PairingIndex);
    }

    [Fact]
    public void DynamicPin_WithNoLockoutStore_IsRefused()
    {
        // With no store, IsPinMethodLockedOut evaluates (null?.GetFailures() ?? 0) >= 10 —
        // always false — and RecordPinFailure returns early. So PIN attempts are unlimited
        // and the spec's terminal lockout is silently inert. Refuse the method instead.
        var (client, connection, _) = TestClient.Create(
            PskCategory.Sentinel,
            configure: options =>
            {
                options.Capabilities = new ClientCapabilities { PinPairingMethods = ["dynamic_pin"] };

                // A presenter IS configured, so the refusal below is attributable to the
                // missing lockout store alone.
                options.PresentPinAsync = (_, _) => ValueTask.CompletedTask;
            });
        using var _c = client;
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");

        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"selected_pair_method":"dynamic_pin"}}""");

        Assert.DoesNotContain(connection.SentMessages, m => m is ClientPairInitMessage);
        var abort = Assert.Single(connection.SentMessages.OfType<PairAbortMessage>());
        Assert.Equal("method_not_supported", abort.Payload.Reason);
        Assert.Equal(ConnectionState.Connected, connection.State);
    }

    [Fact]
    public void DynamicPin_WithALockoutStore_IsStillOffered()
    {
        // Positive control: the two new clauses could refuse PIN entirely and the test above
        // would still pass.
        var (client, connection, _) = TestClient.Create(
            PskCategory.Sentinel,
            configure: options =>
            {
                options.Capabilities = new ClientCapabilities { PinPairingMethods = ["dynamic_pin"] };
                options.PinLockoutStore = new InMemoryPinLockoutStore();
                options.PresentPinAsync = (_, _) => ValueTask.CompletedTask;
            });
        using var _c = client;
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");

        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"selected_pair_method":"dynamic_pin"}}""");

        Assert.Single(connection.SentMessages.OfType<ClientPairInitMessage>());
        Assert.DoesNotContain(connection.SentMessages, m => m is PairAbortMessage);
    }

    [Fact]
    public void FilePinLockoutStore_PersistsCountersAcrossInstances()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sendspin-lockout-" + Guid.NewGuid().ToString("N")[..8]);
        string path = Path.Combine(dir, "lockout.json");
        try
        {
            new FilePinLockoutStore(path).SetFailures("dynamic_pin", 3);

            Assert.Equal(3, new FilePinLockoutStore(path).GetFailures("dynamic_pin"));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FilePinLockoutStore_NarrowsALegacyWorldReadableFile()
    {
        // Counters are not secrets but they are security state: anyone who can rewrite this file
        // resets the lockout. A file from an earlier SDK version keeps its 0644 mode until a
        // failed PIN attempt replaces the inode, so the load path narrows it.
        string dir = Path.Combine(Path.GetTempPath(), "sendspin-lockout-" + Guid.NewGuid().ToString("N")[..8]);
        string path = Path.Combine(dir, "lockout.json");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, """{"dynamic_pin":2}""");

            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(2, new FilePinLockoutStore(path).GetFailures("dynamic_pin"));
            }
            else
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.GroupRead | UnixFileMode.OtherRead);

                var store = new FilePinLockoutStore(path);

                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
                Assert.Equal(2, store.GetFailures("dynamic_pin"));
            }
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
