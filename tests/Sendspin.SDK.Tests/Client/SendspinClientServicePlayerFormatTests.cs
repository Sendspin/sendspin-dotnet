using Sendspin.SDK.Client;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// The player's format preference: spec PR #195 removed <c>stream/request-format</c> and moved the
/// preference into the <c>format</c> field of the client/state player object, where it must name a
/// complete entry from the client's advertised <c>supported_formats</c>.
/// </summary>
public class SendspinClientServicePlayerFormatTests
{
    private static (SendspinClientService Client, FakeSendspinConnection Connection) PlayerClient()
    {
        var (client, connection, _) = TestClient.Create(configure: options => options with
        {
            ClockSynchronizer = new ConvergedClockSynchronizer(),
        });
        return (client, connection);
    }

    [Fact]
    public async Task SetPlayerFormatPreferenceAsync_SendsCompleteFormatInThePlayerObject()
    {
        var (client, connection) = PlayerClient();
        using var _c = client;

        TestClient.CompleteHandshake(connection, "player@v1");
        int before = connection.SentMessages.OfType<ClientStateMessage>().Count();

        await client.SetPlayerFormatPreferenceAsync(
            new AudioFormat { Codec = "pcm", SampleRate = 48000, Channels = 2, BitDepth = 16 });

        var states = connection.SentMessages.OfType<ClientStateMessage>().ToList();
        Assert.Equal(before + 1, states.Count);

        var format = states[^1].Payload.Player!.Format;
        Assert.NotNull(format);
        Assert.Equal("pcm", format.Codec);
        Assert.Equal(48000, format.SampleRate);
        Assert.Equal(2, format.Channels);
        Assert.Equal(16, format.BitDepth);

        // Every sub-field is required: a partial preference has had no defined meaning since the
        // request-format message was removed.
        string json = Sendspin.SDK.Protocol.MessageSerializer.Serialize(states[^1]);
        Assert.Contains("\"format\":{\"codec\":\"pcm\",\"channels\":2,\"sample_rate\":48000,\"bit_depth\":16}", json);
    }

    [Fact]
    public async Task SetPlayerFormatPreferenceAsync_Null_ClearsThePreference()
    {
        var (client, connection) = PlayerClient();
        using var _c = client;

        TestClient.CompleteHandshake(connection, "player@v1");
        await client.SetPlayerFormatPreferenceAsync(
            new AudioFormat { Codec = "opus", SampleRate = 48000, Channels = 2 });
        await client.SetPlayerFormatPreferenceAsync(null);

        // No format field means "no override": the server picks by supported_formats priority.
        Assert.Null(connection.SentMessages.OfType<ClientStateMessage>().Last().Payload.Player!.Format);
    }

    [Fact]
    public async Task SetPlayerFormatPreferenceAsync_FormatNotInSupportedFormats_Throws()
    {
        // A preference MUST be one of the entries advertised in supported_formats, and a server
        // MAY close the connection over one it never saw. Rejecting here makes it a configuration
        // error the app can see rather than a wire deviation it cannot.
        var (client, connection) = PlayerClient();
        using var _c = client;

        TestClient.CompleteHandshake(connection, "player@v1");

        await Assert.ThrowsAsync<ArgumentException>(() => client.SetPlayerFormatPreferenceAsync(
            new AudioFormat { Codec = "opus", SampleRate = 96000, Channels = 2 }));
    }

    [Fact]
    public async Task PlayerFormatPreference_SetBeforeConnect_RidesOnTheInitialState()
    {
        var (client, connection) = PlayerClient();
        using var _c = client;

        await client.SetPlayerFormatPreferenceAsync(
            new AudioFormat { Codec = "flac", SampleRate = 48000, Channels = 2 });

        // Nothing on the wire yet: the connection has not sent its initial client/state.
        Assert.Empty(connection.SentMessages.OfType<ClientStateMessage>());

        TestClient.CompleteHandshake(connection, "player@v1");

        var format = connection.SentMessages.OfType<ClientStateMessage>().Single().Payload.Player!.Format;
        Assert.NotNull(format);
        Assert.Equal("flac", format.Codec);
    }
}
