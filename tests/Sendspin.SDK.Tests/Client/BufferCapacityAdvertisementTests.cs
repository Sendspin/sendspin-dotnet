using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Client;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// What the client promises the server about how much audio it can hold.
/// </summary>
/// <remarks>
/// <c>buffer_capacity</c> is a hard per-player byte limit servers fill toward
/// (roles/player/v1.md:34-35), not a hint. The 9.2 default was a flat 32 MB with no
/// relationship to the real buffer, so a server behaving exactly as the spec allows could
/// queue minutes of Opus against a buffer holding a fraction of that, and everything past it
/// was discarded before it ever played.
/// </remarks>
public class BufferCapacityAdvertisementTests
{
    private const string ServerHelloJson = """
        { "type": "server/hello", "payload": { "server_id": "srv-1", "version": 1, "active_roles": ["player@v1"] } }
        """;

    [Fact]
    public async Task ClientHello_AdvertisesTheDerivedCapacity_NotAFlat32Mb()
    {
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection);

        var connectTask = client.ConnectAsync(new Uri("ws://test"));
        connection.RaiseTextMessageReceived(ServerHelloJson);
        await connectTask;

        var hello = connection.SentMessages.OfType<ClientHelloMessage>().Single();
        var advertised = hello.Payload.PlayerV1Support!.BufferCapacity;

        Assert.NotEqual(32_000_000, advertised);

        // Whatever format the server picks from what was advertised, the promise holds.
        foreach (var format in new ClientCapabilities().AudioFormats)
        {
            Assert.True(
                PlayerBufferCapacity.HoldableMilliseconds(advertised, format)
                    <= PlayerBufferCapacity.DefaultDecodedBufferMilliseconds,
                $"{format.Codec}: advertised {advertised} bytes overruns the decoded buffer");
        }
    }

    [Fact]
    public async Task ClientHello_ClampsAnOverLargeConfiguredCapacity_AndSaysSo()
    {
        var logger = new CapturingLogger<SendspinClientService>();
        var connection = new FakeSendspinConnection();
        var capabilities = new ClientCapabilities { BufferCapacity = 32_000_000 };
        using var client = new SendspinClientService(logger, connection, capabilities: capabilities);

        var connectTask = client.ConnectAsync(new Uri("ws://test"));
        connection.RaiseTextMessageReceived(ServerHelloJson);
        await connectTask;

        var hello = connection.SentMessages.OfType<ClientHelloMessage>().Single();
        Assert.Equal(capabilities.TruthfulBufferCapacityBytes, hello.Payload.PlayerV1Support!.BufferCapacity);

        // Silently clamping would leave an app believing a promise the SDK quietly withdrew.
        Assert.Contains(
            logger.MessagesAt(LogLevel.Warning),
            m => m.Contains("BufferCapacity", StringComparison.Ordinal)
                && m.Contains("32000000", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClientHello_LeavesASmallerConfiguredCapacityAlone()
    {
        // The positive control: under-advertising is always safe, so it must pass through
        // untouched and without a diagnostic.
        var logger = new CapturingLogger<SendspinClientService>();
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            logger,
            connection,
            capabilities: new ClientCapabilities { BufferCapacity = 65_536 });

        var connectTask = client.ConnectAsync(new Uri("ws://test"));
        connection.RaiseTextMessageReceived(ServerHelloJson);
        await connectTask;

        var hello = connection.SentMessages.OfType<ClientHelloMessage>().Single();
        Assert.Equal(65_536, hello.Payload.PlayerV1Support!.BufferCapacity);
        Assert.DoesNotContain(
            logger.MessagesAt(LogLevel.Warning),
            m => m.Contains("BufferCapacity", StringComparison.Ordinal));
    }
}
