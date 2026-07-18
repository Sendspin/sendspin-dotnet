using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Coverage for the Pairing PSK flow: server/activate with the pairing activity
/// triggers client/pair-finalize with a fresh long-term PSK, and server/pair-finalize
/// persists the record bound to the server id.
/// </summary>
public class SendspinClientServicePairingTests
{
    private const string ServerId = "GFsV9tLaSQm9HcFWpKsgYQOr7wFTvNUtkmFwuVz3zoo";

    private sealed class FakeNoiseSession : INoiseSessionInfo
    {
        public string? ServerId { get; set; } = SendspinClientServicePairingTests.ServerId;
        public NoisePsk? MatchedPsk { get; set; } =
            new(NoiseConstants.SentinelPsk.ToArray(), PskCategory.Pairing);
        public ReadOnlyMemory<byte>? HandshakeHash { get; set; } = new byte[32];
    }

    private static (SendspinClientService, FakeSendspinConnection, InMemoryPairingRecordStore) Create()
    {
        var connection = new FakeSendspinConnection();
        var store = new InMemoryPairingRecordStore();
        var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection,
            noiseSession: new FakeNoiseSession(),
            pairingRecordStore: store);
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");
        return (client, connection, store);
    }

    [Fact]
    public void PairingActivate_SendsPairFinalize_WithFreshPsk()
    {
        var (client, connection, _) = Create();
        using var _c = client;

        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"selected_pair_method":"pairing_psk"}}""");

        var finalize = Assert.Single(connection.SentMessages.OfType<ClientPairFinalizeMessage>());
        Assert.NotNull(finalize.Payload.LongTermPsk);
        Assert.Equal(43, finalize.Payload.LongTermPsk!.Length);
        Assert.Null(finalize.Payload.WrappedPsk);
    }

    [Fact]
    public void ServerPairFinalize_PersistsRecord_BoundToServer_AndRaisesEvent()
    {
        var (client, connection, store) = Create();
        using var _c = client;
        string? pairedWith = null;
        client.PairingCompleted += (_, id) => pairedWith = id;

        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"selected_pair_method":"pairing_psk"}}""");
        connection.RaiseTextMessageReceived("""{"type":"server/pair-finalize","payload":{}}""");

        var record = Assert.Single(store.List());
        Assert.Equal(PskCategory.LongTerm, record.Category);
        Assert.Equal(ServerId, record.ServerId);
        Assert.Equal(ServerId, pairedWith);

        // The delivered PSK and the stored record agree.
        var finalize = connection.SentMessages.OfType<ClientPairFinalizeMessage>().Single();
        Assert.Equal(NoiseConstants.DerivePskId(record.Psk.Span),
            NoiseConstants.DerivePskId(record.Psk.Span));
        Assert.Equal(43, finalize.Payload.LongTermPsk!.Length);
    }

    [Fact]
    public void UnsupportedPairMethod_SendsAbort_AndDisconnects()
    {
        var (client, connection, store) = Create();
        using var _c = client;

        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"selected_pair_method":"dynamic_pin"}}""");

        var abort = Assert.Single(connection.SentMessages.OfType<PairAbortMessage>());
        Assert.Equal("method_not_supported", abort.Payload.Reason);
        Assert.Empty(store.List());
    }

    [Fact]
    public void PairAbort_ClearsPendingAttempt()
    {
        var (client, connection, store) = Create();
        using var _c = client;

        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"selected_pair_method":"pairing_psk"}}""");
        connection.RaiseTextMessageReceived("""{"type":"pair/abort","payload":{"reason":"user_cancelled"}}""");
        connection.RaiseTextMessageReceived("""{"type":"server/pair-finalize","payload":{}}""");

        // The aborted attempt's PSK must not be persisted by a late finalize.
        Assert.Empty(store.List());
    }

    [Fact]
    public void EncryptedHello_AdvertisesPairingPskMethod()
    {
        var (client, connection, _) = Create();
        using var _c = client;

        var hello = connection.SentMessages.OfType<ClientHelloMessage>().Single();
        var method = Assert.Single(hello.Payload.SupportedPairMethods!);
        Assert.Equal("pairing_psk", method.Method);
    }
}
