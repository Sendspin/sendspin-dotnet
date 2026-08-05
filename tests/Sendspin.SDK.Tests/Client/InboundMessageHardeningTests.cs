using Sendspin.SDK.Connection;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Hostile-input coverage for the inbound-message path (#88 item 2). Every message here
/// has already been AEAD-authenticated by the framing layer, so a payload that fails to
/// parse means the peer is broken or hostile: the client closes the connection rather
/// than logging and continuing in an undefined state. Anything the narrowed catches do
/// not name is a bug in our own handling and must escape the receive callback rather
/// than being swallowed.
/// </summary>
public class InboundMessageHardeningTests
{
    private sealed class HandlerFaultException : Exception
    {
    }

    [Fact]
    public void MalformedPayload_OnAuthenticatedTextMessage_ClosesTheConnection()
    {
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        // Routes (the document parses and carries a known type) but the payload is not
        // the shape the type demands, so typed deserialization fails.
        connection.RaiseTextMessageReceived("""{"type":"server/state","payload":"not-an-object"}""");

        Assert.Equal(ConnectionState.Disconnected, connection.State);
        // The spec's goodbye reason list is closed and none denotes a protocol error, so
        // the close must not invent one: it reuses 'unauthorized', the reason this client
        // already sends for peer-violation closes (inadmissible activate, source trust gate).
        Assert.Equal("unauthorized", connection.LastDisconnectReason);
    }

    [Fact]
    public void MalformedStreamStartPayload_ClosesTheConnection()
    {
        // stream/start is handled on the fire-and-forget async path, so its parse failure
        // never reaches the dispatch catch: the handler itself must close.
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        connection.RaiseTextMessageReceived("""{"type":"stream/start","payload":"not-an-object"}""");

        Assert.Equal(ConnectionState.Disconnected, connection.State);
        Assert.Equal("unauthorized", connection.LastDisconnectReason);
    }

    [Fact]
    public void MalformedStreamEndPayload_ClosesTheConnection()
    {
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        connection.RaiseTextMessageReceived("""{"type":"stream/end","payload":"not-an-object"}""");

        Assert.Equal(ConnectionState.Disconnected, connection.State);
        Assert.Equal("unauthorized", connection.LastDisconnectReason);
    }

    [Fact]
    public void NonStringPskInManagementRequest_ClosesTheConnection()
    {
        // The management catches answer 'invalid' for JsonException/KeyNotFoundException/
        // FormatException, but a non-string psk throws InvalidOperationException from
        // JsonElement.GetString(), which escapes them. The dispatch filter must name it
        // as peer-triggerable and close — not let it escape the receive callback
        // unhandled (the condition carried from the previous change's review).
        var (client, connection, _) = SendspinClientServiceManagementTests.Create();
        using var _c = client;

        connection.RaiseTextMessageReceived(
            """{"type":"management/add-record","payload":{"psk":123}}""");

        Assert.Equal(ConnectionState.Disconnected, connection.State);
        Assert.Equal("unauthorized", connection.LastDisconnectReason);
    }

    [Fact]
    public void ThrowingRoleEventHandler_PropagatesOutOfTheReceiveCallback()
    {
        var (client, connection, _) = TestClient.Create();
        using var _c = client;
        client.GroupStateChanged += (_, _) => throw new HandlerFaultException();

        // A perfectly valid message: the failure is in our own handling, not the peer's
        // input, so it must escape the receive callback — in production the receive loop
        // surfaces it as a lost connection — rather than being logged and dropped.
        Assert.Throws<HandlerFaultException>(() => connection.RaiseTextMessageReceived(
            """{"type":"group/update","payload":{"group_id":"g1"}}"""));

        // And it was not misclassified as a peer protocol error: no close was initiated.
        Assert.Null(connection.LastDisconnectReason);
    }
}
