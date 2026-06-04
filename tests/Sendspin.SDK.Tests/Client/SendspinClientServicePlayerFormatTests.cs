using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// The client can emit stream/request-format for the player role to adapt the audio format to
/// changing network/CPU conditions (per the spec).
/// </summary>
public class SendspinClientServicePlayerFormatTests
{
    [Fact]
    public async Task RequestPlayerFormatAsync_SendsPlayerRequestFormat()
    {
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(NullLogger<SendspinClientService>.Instance, connection);

        await client.RequestPlayerFormatAsync(codec: "opus", sampleRate: 48000, channels: 2, bitDepth: 16);

        var msg = Assert.IsType<StreamRequestFormatMessage>(connection.SentMessages.Single());
        var player = msg.Payload.Player;
        Assert.NotNull(player);
        Assert.Equal("opus", player.Codec);
        Assert.Equal(48000, player.SampleRate);
        Assert.Equal(2, player.Channels);
        Assert.Equal(16, player.BitDepth);
    }

    [Fact]
    public async Task RequestPlayerFormatAsync_OmitsUnsetFields()
    {
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(NullLogger<SendspinClientService>.Instance, connection);

        await client.RequestPlayerFormatAsync(codec: "pcm");

        var msg = Assert.IsType<StreamRequestFormatMessage>(connection.SentMessages.Single());
        var json = Sendspin.SDK.Protocol.MessageSerializer.Serialize(msg);
        Assert.Contains("\"codec\":\"pcm\"", json);
        Assert.DoesNotContain("sample_rate", json);
        Assert.DoesNotContain("bit_depth", json);
        Assert.DoesNotContain("\"channels\"", json);
    }
}
