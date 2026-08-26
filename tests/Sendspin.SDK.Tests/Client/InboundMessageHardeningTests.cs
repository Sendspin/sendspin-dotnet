using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// What a malformed message from an authenticated peer does to the client.
/// </summary>
/// <remarks>
/// System.Text.Json enforces neither nullable annotations nor <c>required</c> against an explicit
/// null, so <c>"payload": null</c> deserialized cleanly and every handler that read
/// <c>message.Payload</c> raised a NullReferenceException — logged and swallowed for the
/// synchronous handlers, and swallowed even more quietly at the fire-and-forget boundary for
/// <c>stream/start</c> and <c>stream/end</c>. Either way the client carried on having half
/// applied a message it could not read.
/// </remarks>
public class InboundMessageHardeningTests
{
    private static (SendspinClientService Client, FakeSendspinConnection Connection) Create(
        FakeAudioPipeline? pipeline = null)
    {
        var connection = new FakeSendspinConnection();
        var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection,
            audioPipeline: pipeline);
        return (client, connection);
    }

    private static async Task<bool> WaitForDisconnectAsync(FakeSendspinConnection connection)
    {
        for (var i = 0; i < 100 && connection.State != ConnectionState.Disconnected; i++)
        {
            await Task.Delay(10);
        }

        return connection.State == ConnectionState.Disconnected;
    }

    [Theory]
    [InlineData("""{"type":"server/hello","payload":null}""")]
    [InlineData("""{"type":"server/time","payload":null}""")]
    [InlineData("""{"type":"group/update","payload":null}""")]
    [InlineData("""{"type":"server/state","payload":null}""")]
    [InlineData("""{"type":"server/command","payload":null}""")]
    [InlineData("""{"type":"stream/clear","payload":null}""")]
    public async Task NullPayload_ClosesTheConnection(string json)
    {
        var (client, connection) = Create();
        using var _c = client;
        await connection.ConnectAsync(new Uri("ws://test"));

        connection.RaiseTextMessageReceived(json);

        Assert.True(await WaitForDisconnectAsync(connection),
            "a message the client cannot read must end the connection, not be half applied");
    }

    [Fact]
    public async Task NullStreamStartPayload_ClosesTheConnection_AndReachesNoSubscriber()
    {
        // The fire-and-forget handler: its NullReferenceException never reached the dispatch
        // catch at all. Worse, StreamStartReceived would have handed the null straight to every
        // app subscriber of an event whose argument is declared non-nullable.
        var (client, connection) = Create();
        using var _c = client;
        await connection.ConnectAsync(new Uri("ws://test"));

        var received = 0;
        client.StreamStartReceived += (_, _) => Interlocked.Increment(ref received);

        connection.RaiseTextMessageReceived("""{"type":"stream/start","payload":null}""");

        Assert.True(await WaitForDisconnectAsync(connection));
        Assert.Equal(0, Volatile.Read(ref received));
    }

    [Fact]
    public async Task NullStreamEndPayload_ClosesTheConnection()
    {
        var (client, connection) = Create();
        using var _c = client;
        await connection.ConnectAsync(new Uri("ws://test"));

        connection.RaiseTextMessageReceived("""{"type":"stream/end","payload":null}""");

        Assert.True(await WaitForDisconnectAsync(connection));
    }

    [Fact]
    public async Task AWellFormedMessage_LeavesTheConnectionUp()
    {
        // Positive control: closing on everything would satisfy every assertion above.
        var (client, connection) = Create();
        using var _c = client;
        await connection.ConnectAsync(new Uri("ws://test"));

        connection.RaiseTextMessageReceived("""
            { "type": "server/hello", "payload": { "server_id": "srv-1", "version": 1, "active_roles": ["player@v1"] } }
            """);

        await Task.Delay(50);

        Assert.Equal(ConnectionState.Connected, connection.State);
        Assert.Equal("srv-1", client.ServerId);
        Assert.Contains(connection.SentMessages, m => m is ClientStateMessage);
    }
}
