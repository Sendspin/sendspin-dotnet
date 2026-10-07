using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Noise;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// <c>ConnectAsync</c> returns normally only when the handshake completed. A connection that
/// closed before its first <c>server/activate</c> was admitted is thrown to the caller, as the
/// refusals raised by the connection layer already were (#325).
/// </summary>
public class ConnectAsyncHandshakeFailureTests
{
    private static readonly Uri ServerUri = new("ws://test.local:8927/sendspin");
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private const string ServerHello = """{"type":"server/hello","payload":{"name":"srv"}}""";

    [Theory]
    [InlineData(PskCategory.Sentinel)]
    [InlineData(PskCategory.Pairing)]
    public async Task PlaybackOfferedToAnUnpairedClient_ThrowsPairingRequired(PskCategory category)
    {
        // The shipped default on first contact with a new server: unpaired access is off, the
        // server activates playback, and the client closes with 'pairing_required'.
        var (client, connection, _) = TestClient.Create(category, unpairedAccess: false);
        using var _c = client;

        var connecting = client.ConnectAsync(ServerUri);
        connection.RaiseTextMessageReceived(ServerHello);
        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["playback"],"active_roles":["player@v1"]}}""");

        var ex = await Assert.ThrowsAsync<SendspinHandshakeException>(() => connecting.WaitAsync(Wait));
        Assert.Equal(HandshakeFailureKind.PairingRequired, ex.Kind);
        Assert.Equal("pairing_required", connection.LastDisconnectReason);
    }

    [Fact]
    public async Task InadmissibleActivation_ThrowsActivationRefused()
    {
        // A pairing activity on a session that is already paired: closed with 'unauthorized'.
        var (client, connection, _) = TestClient.Create(PskCategory.LongTerm);
        using var _c = client;

        var connecting = client.ConnectAsync(ServerUri);
        connection.RaiseTextMessageReceived(ServerHello);
        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"active_roles":[],"pairing":{"method":"static_pairing_code"}}}""");

        var ex = await Assert.ThrowsAsync<SendspinHandshakeException>(() => connecting.WaitAsync(Wait));
        Assert.Equal(HandshakeFailureKind.ActivationRefused, ex.Kind);
        Assert.Equal("unauthorized", connection.LastDisconnectReason);
    }

    [Fact]
    public async Task SourceRoleActivatedWithoutUserTrust_ThrowsActivationRefused()
    {
        var (client, connection, _) = TestClient.Create(PskCategory.Sentinel, unpairedAccess: true);
        using var _c = client;

        var connecting = client.ConnectAsync(ServerUri);
        connection.RaiseTextMessageReceived(ServerHello);
        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["playback"],"active_roles":["source@v1"]}}""");

        var ex = await Assert.ThrowsAsync<SendspinHandshakeException>(() => connecting.WaitAsync(Wait));
        Assert.Equal(HandshakeFailureKind.ActivationRefused, ex.Kind);
        Assert.Equal("unauthorized", connection.LastDisconnectReason);
    }

    [Fact]
    public async Task MalformedHandshakeMessage_ThrowsConnectionClosed()
    {
        // A server/hello whose payload is not an object: the client closes on it.
        var (client, connection, _) = TestClient.Create(PskCategory.LongTerm);
        using var _c = client;

        var connecting = client.ConnectAsync(ServerUri);
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":[]}""");

        var ex = await Assert.ThrowsAsync<SendspinHandshakeException>(() => connecting.WaitAsync(Wait));
        Assert.Equal(HandshakeFailureKind.ConnectionClosed, ex.Kind);
        Assert.Equal(ConnectionState.Disconnected, connection.State);
    }

    [Fact]
    public async Task ConnectionClosedBeforeTheFirstActivate_ThrowsConnectionClosed()
    {
        // What a SendspinConnection with AutoReconnect off publishes when the socket drops
        // during the hello exchange: Disconnected, with no exception attached.
        var (client, connection, _) = TestClient.Create(PskCategory.LongTerm);
        using var _c = client;

        var connecting = client.ConnectAsync(ServerUri);
        connection.RaiseTextMessageReceived(ServerHello);
        await connection.CloseWithoutGoodbyeAsync("Connection lost");

        var ex = await Assert.ThrowsAsync<SendspinHandshakeException>(() => connecting.WaitAsync(Wait));
        Assert.Equal(HandshakeFailureKind.ConnectionClosed, ex.Kind);
    }

    // The three below hold the dial's return: a real one resumes on the caller's context,
    // which for an app connecting from a busy UI thread is after the receive loop has already
    // run the whole exchange. Whatever happened by then must still be the call's outcome.

    [Fact]
    public async Task ActivateAdmittedBeforeTheCallerResumes_CompletesTheConnect()
    {
        var (client, connection, _) = TestClient.Create(PskCategory.LongTerm);
        using var _c = client;
        connection.HoldConnectReturn = new TaskCompletionSource();

        var connecting = client.ConnectAsync(ServerUri);
        connection.RaiseTextMessageReceived(ServerHello);
        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["playback"],"active_roles":["player@v1"]}}""");
        Assert.False(connecting.IsCompleted);

        connection.HoldConnectReturn.SetResult();

        await connecting.WaitAsync(Wait);
        Assert.Equal(ConnectionState.Connected, connection.State);
    }

    [Fact]
    public async Task ConnectionLostBeforeTheCallerResumes_ThrowsConnectionClosed()
    {
        var (client, connection, _) = TestClient.Create(PskCategory.LongTerm);
        using var _c = client;
        connection.HoldConnectReturn = new TaskCompletionSource();

        var connecting = client.ConnectAsync(ServerUri);
        connection.RaiseTextMessageReceived(ServerHello);
        await connection.CloseWithoutGoodbyeAsync("Connection lost");

        connection.HoldConnectReturn.SetResult();

        var ex = await Assert.ThrowsAsync<SendspinHandshakeException>(() => connecting.WaitAsync(Wait));
        Assert.Equal(HandshakeFailureKind.ConnectionClosed, ex.Kind);
    }

    [Fact]
    public async Task RefusalBeforeTheCallerResumes_IsNotThrownUntilTheGoodbyeHasGoneOut()
    {
        var (client, connection, _) = TestClient.Create(PskCategory.Sentinel, unpairedAccess: false);
        using var _c = client;
        connection.HoldConnectReturn = new TaskCompletionSource();
        connection.HoldDisconnect = new TaskCompletionSource();

        var connecting = client.ConnectAsync(ServerUri);
        connection.RaiseTextMessageReceived(ServerHello);
        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["playback"],"active_roles":["player@v1"]}}""");
        Assert.Equal(ConnectionState.Disconnecting, connection.State);

        // The caller resumes while the goodbye is still being written. Thrown now, its catch
        // could dispose the client ahead of that goodbye.
        connection.HoldConnectReturn.SetResult();
        Assert.False(connecting.IsCompleted);

        connection.HoldDisconnect.SetResult();

        var ex = await Assert.ThrowsAsync<SendspinHandshakeException>(() => connecting.WaitAsync(Wait));
        Assert.Equal(HandshakeFailureKind.PairingRequired, ex.Kind);
        Assert.Equal(ConnectionState.Disconnected, connection.State);
    }

    [Fact]
    public void ActivationRefusedWithNoConnectInFlight_ClosesWithoutThrowing()
    {
        // The listen path, and any activation after the first: nothing awaits the handshake
        // through this client, so the refusal is the close alone.
        var (client, connection, _) = TestClient.Create(PskCategory.Sentinel, unpairedAccess: false);
        using var _c = client;

        connection.RaiseTextMessageReceived(ServerHello);
        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["playback"],"active_roles":["player@v1"]}}""");

        Assert.Equal("pairing_required", connection.LastDisconnectReason);
    }
}
