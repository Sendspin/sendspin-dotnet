using System.Buffers.Binary;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Sendspin.SDK.Client;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Tests.Audio;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// player@v1 defines a single audio slot — binary type 4 — even though the role's ID allocation
/// spans 4-7. Types 5-7 carry no defined payload, so the client must drop them with a warning
/// rather than interleave them into the one audio pipeline (#205), which is what the C++
/// reference client does.
/// </summary>
public class PlayerAudioDispatchTests
{
    private static byte[] Chunk(byte type, long ts, params byte[] audio)
    {
        var buf = new byte[9 + audio.Length];
        buf[0] = type;
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(1, 8), ts);
        audio.CopyTo(buf, 9);
        return buf;
    }

    private static (SendspinClientService Client, FakeSendspinConnection Connection, FakeAudioPipeline Pipeline)
        PlayerClient(ILogger<SendspinClientService>? logger = null)
    {
        var pipe = new FakeAudioPipeline();
        var (client, connection, _) = TestClient.Create(
            configure: options => options with
            {
                AudioPipeline = pipe,
                ClockSynchronizer = new FakeClockSynchronizer { IsConverged = true },
            },
            logger: logger);
        return (client, connection, pipe);
    }

    [Fact]
    public void Type4Chunk_ReachesThePipeline()
    {
        var (client, connection, pipe) = PlayerClient();
        using var _c = client;

        connection.RaiseBinaryMessageReceived(Chunk(BinaryMessageTypes.PlayerAudio0, 5_000, 1, 2, 3));

        var chunk = Assert.Single(pipe.Chunks);
        Assert.Equal(5_000, chunk.ServerTimestamp);
        Assert.Equal(new byte[] { 1, 2, 3 }, chunk.EncodedData);
    }

    [Theory]
    [InlineData(BinaryMessageTypes.PlayerAudio1)]
    [InlineData(BinaryMessageTypes.PlayerAudio2)]
    [InlineData(BinaryMessageTypes.PlayerAudio3)]
    public void UndefinedSlot_IsDroppedAndWarnedAbout(byte type)
    {
        var logger = new CapturingLogger<SendspinClientService>();
        var (client, connection, pipe) = PlayerClient(logger);
        using var _c = client;

        connection.RaiseBinaryMessageReceived(Chunk(type, 5_000, 1, 2, 3));

        Assert.Empty(pipe.Chunks);
        Assert.Single(
            logger.MessagesAt(LogLevel.Warning),
            m => m.Contains(type.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
    }

    [Fact]
    public void UndefinedSlot_WarnsOncePerType_NotOncePerChunk()
    {
        var logger = new CapturingLogger<SendspinClientService>();
        var (client, connection, pipe) = PlayerClient(logger);
        using var _c = client;

        // A server emitting an undefined slot emits it at chunk rate, so the warning has to be
        // latched per type or it buries every other diagnostic.
        for (int i = 0; i < 5; i++)
        {
            connection.RaiseBinaryMessageReceived(Chunk(BinaryMessageTypes.PlayerAudio1, i, 0xAA));
            connection.RaiseBinaryMessageReceived(Chunk(BinaryMessageTypes.PlayerAudio2, i, 0xBB));
        }

        Assert.Empty(pipe.Chunks);
        Assert.Equal(2, logger.MessagesAt(LogLevel.Warning).Count);
    }

    [Fact]
    public void ParseAudioChunk_AcceptsOnlyTheDefinedType()
    {
        // The parser layer stays honest on its own: an undefined slot never becomes an AudioChunk
        // that a caller could hand to a pipeline.
        Assert.NotNull(BinaryMessageParser.ParseAudioChunk(Chunk(BinaryMessageTypes.PlayerAudio0, 1, 9)));
        Assert.Null(BinaryMessageParser.ParseAudioChunk(Chunk(BinaryMessageTypes.PlayerAudio1, 1, 9)));
        Assert.Null(BinaryMessageParser.ParseAudioChunk(Chunk(BinaryMessageTypes.PlayerAudio2, 1, 9)));
        Assert.Null(BinaryMessageParser.ParseAudioChunk(Chunk(BinaryMessageTypes.PlayerAudio3, 1, 9)));
    }
}
