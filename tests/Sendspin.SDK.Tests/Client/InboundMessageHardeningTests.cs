using Sendspin.SDK.Connection;
using Sendspin.SDK.Protocol.Messages;

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
    public void NullStreamStartPayload_ClosesTheConnection()
    {
        // System.Text.Json does not enforce non-nullable reference annotations, so
        // "payload": null deserializes into a message whose Payload is null despite the
        // model's promise. The handler must detect the hole before dereferencing it: the
        // NullReferenceException a dereference produces is not JsonException, so it would
        // escape the handler's catch and die in the fire-and-forget swallow with the
        // connection still up.
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        connection.RaiseTextMessageReceived("""{"type":"stream/start","payload":null}""");

        Assert.Equal(ConnectionState.Disconnected, connection.State);
        Assert.Equal("unauthorized", connection.LastDisconnectReason);
    }

    [Fact]
    public void NullStreamEndPayload_ClosesTheConnection()
    {
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        connection.RaiseTextMessageReceived("""{"type":"stream/end","payload":null}""");

        Assert.Equal(ConnectionState.Disconnected, connection.State);
        Assert.Equal("unauthorized", connection.LastDisconnectReason);
    }

    [Fact]
    public void NullStreamClearPayload_ClosesTheConnection()
    {
        // stream/clear is the one member of the stream trio whose handler dereferences its
        // payload with no guard of its own, so this pins that the central null-member
        // validation reaches it: the deliberate malformed-payload close, not the
        // NullReferenceException that would tear the receive loop down as an internal fault.
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        connection.RaiseTextMessageReceived("""{"type":"stream/clear","payload":null}""");

        Assert.Equal(ConnectionState.Disconnected, connection.State);
        Assert.Equal("unauthorized", connection.LastDisconnectReason);
    }

    [Fact]
    public void NullServerStatePayload_ClosesTheConnection()
    {
        // server/state is the highest-traffic peer-supplied type and had no PeerMessageValidation
        // arm (#206), so "payload": null reached HandleServerState and died dereferencing it. A
        // NullReferenceException is not in the dispatch catch filter, so the connection tore down
        // as an internal bug instead of via the deliberate malformed-peer close.
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        connection.RaiseTextMessageReceived("""{"type":"server/state","payload":null}""");

        Assert.Equal(ConnectionState.Disconnected, connection.State);
        Assert.Equal("unauthorized", connection.LastDisconnectReason);
    }

    [Fact]
    public void NullServerStateRoleObjects_AreAccepted()
    {
        // Positive control for the arm above: null role objects are the spec's "clear this
        // role" signal, so the arm must reject a null payload without rejecting these.
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        connection.RaiseTextMessageReceived(
            """{"type":"server/state","payload":{"metadata":null,"controller":null,"color":null}}""");

        Assert.Equal(ConnectionState.Connected, connection.State);
        Assert.Null(connection.LastDisconnectReason);
    }

    [Fact]
    public void NullCodecInStreamStartPlayer_ClosesTheConnection()
    {
        // The same annotation hole one level down: "player" is present, so the
        // artwork-only skip does not apply, but its "codec" — declared non-nullable and
        // dereferenced by the decoder factory when the pipeline starts — is null.
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        connection.RaiseTextMessageReceived(
            """{"type":"stream/start","payload":{"player":{"codec":null,"sample_rate":48000,"channels":2}}}""");

        Assert.Equal(ConnectionState.Disconnected, connection.State);
        Assert.Equal("unauthorized", connection.LastDisconnectReason);
    }

    /// <summary>
    /// A wrong-kind management field is answered, not disconnected on (#132).
    /// </summary>
    /// <remarks>
    /// This test previously asserted the opposite, and the reversal is deliberate.
    /// <c>management.md</c> opens by stating that <b>all</b> <c>management/*</c> requests are
    /// answered by a single <c>management/result</c>, and defines <c>invalid</c> as covering a
    /// malformed payload — so closing the socket silently was a conformance gap, not a
    /// hardening measure. The original concern was that <c>InvalidOperationException</c> from
    /// <c>JsonElement.GetString()</c> must not escape the receive callback unhandled, and that
    /// still holds: the management reads are kind-checked and raise <c>JsonException</c>, which
    /// the handler's own filter turns into this answer. Everything outside the management
    /// surface still closes, as the tests above it assert.
    /// </remarks>
    [Fact]
    public void NonStringPskInManagementRequest_IsAnsweredInvalid_WithoutClosing()
    {
        var (client, connection, _, _) = SendspinClientServiceManagementTests.Create();
        using var _c = client;

        connection.RaiseTextMessageReceived(
            """{"type":"management/add-record","payload":{"psk":123}}""");

        var result = Assert.Single(connection.SentMessages.OfType<ManagementResultMessage>());
        Assert.Equal("invalid", result.Payload.Result);

        Assert.NotEqual(ConnectionState.Disconnected, connection.State);
        Assert.Null(connection.LastDisconnectReason);
    }

    /// <summary>
    /// The same for a wrong-kind field on <c>set-pairing-config</c>, which is where #132 found
    /// the widened surface: six reads added by one change, each previously able to drop the
    /// connection on its own.
    /// </summary>
    [Fact]
    public void NonBooleanEnabledInSetPairingConfig_IsAnsweredInvalid_WithoutClosing()
    {
        var (client, connection, _, _) = SendspinClientServiceManagementTests.Create();
        using var _c = client;

        connection.RaiseTextMessageReceived(
            """{"type":"management/set-pairing-config","payload":{"unpaired_access":{"enabled":"yes"}}}""");

        var result = Assert.Single(connection.SentMessages.OfType<ManagementResultMessage>());
        Assert.Equal("invalid", result.Payload.Result);
        Assert.NotEqual(ConnectionState.Disconnected, connection.State);
    }

    [Fact]
    public void UnparseableFrame_ClosesTheConnection()
    {
        // A frame that is not JSON at all never reaches a handler, so the narrowed handler
        // catches cannot see it: GetMessageType itself must report it rather than swallow
        // its own JsonException and let the frame fall to the unhandled-type Debug log.
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        connection.RaiseTextMessageReceived("{{{");

        Assert.Equal(ConnectionState.Disconnected, connection.State);
        Assert.Equal("unauthorized", connection.LastDisconnectReason);
    }

    [Fact]
    public void FrameWithNoTypeMember_ClosesTheConnection()
    {
        // Parses, but carries no "type" member: malformed protocol input, not an
        // unrecognised message, so it closes like unparseable JSON does.
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        connection.RaiseTextMessageReceived("""{"foo":"bar"}""");

        Assert.Equal(ConnectionState.Disconnected, connection.State);
        Assert.Equal("unauthorized", connection.LastDisconnectReason);
    }

    [Fact]
    public void NullTypeMember_ClosesTheConnection()
    {
        // #107: JsonElement.GetString() returns null for a JSON null rather than throwing,
        // so this used to reach the unhandled-type branch with the connection left up --
        // the one row in the malformed-input table that failed open instead of closing.
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        connection.RaiseTextMessageReceived("""{"type":null}""");

        Assert.Equal(ConnectionState.Disconnected, connection.State);
        Assert.Equal("unauthorized", connection.LastDisconnectReason);
    }

    [Fact]
    public void UnrecognisedMessageType_IsTolerated()
    {
        // Positive control for the malformed/unrecognised distinction: a well-formed
        // message whose type this SDK does not know must be logged and ignored, so a newer
        // server can add message types without killing older clients. Without this test, a
        // fix that closes on everything unrecognised would pass the two tests above.
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        // The connection is genuinely up before delivery, so the still-up assertion below
        // can only pass if the unknown type was tolerated, not because nothing was wired.
        Assert.Equal(ConnectionState.Connected, connection.State);

        connection.RaiseTextMessageReceived("""{"type":"server/some-future-thing","x":1}""");

        Assert.Equal(ConnectionState.Connected, connection.State);
        Assert.Null(connection.LastDisconnectReason);
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
