using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Coverage for repeat/shuffle handling in server/state. Per the Sendspin spec these states live in
/// the controller object; this verifies the client reads them there and not from the metadata object.
/// </summary>
public class SendspinClientServiceServerStateTests
{
    [Fact]
    public void RepeatAndShuffle_ReadFromControllerObject()
    {
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection);

        connection.RaiseTextMessageReceived("""
            {
                "type": "server/state",
                "payload": {
                    "controller": { "volume": 40, "muted": false, "repeat": "all", "shuffle": true }
                }
            }
            """);

        Assert.NotNull(client.CurrentGroup);
        Assert.Equal("all", client.CurrentGroup.Repeat);
        Assert.True(client.CurrentGroup.Shuffle);
    }

    [Fact]
    public void RepeatAndShuffle_InMetadataObject_AreIgnored()
    {
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection);

        // Old wire layout: repeat/shuffle under metadata. They moved to the controller object,
        // so the client must not pick them up here.
        connection.RaiseTextMessageReceived("""
            {
                "type": "server/state",
                "payload": {
                    "metadata": { "title": "Song", "repeat": "one", "shuffle": true }
                }
            }
            """);

        Assert.NotNull(client.CurrentGroup);
        Assert.Equal("Song", client.CurrentGroup.Metadata?.Title);
        Assert.Null(client.CurrentGroup.Repeat);
        Assert.False(client.CurrentGroup.Shuffle);
    }
}
