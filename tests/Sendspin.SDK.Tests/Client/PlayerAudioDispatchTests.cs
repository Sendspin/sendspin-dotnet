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
        => ChunkWithSendAhead(type, ts, sendAhead: 0, audio);

    // Player audio chunk header: type(1) + timestamp int64 BE(8) + send_ahead uint32 BE(4),
    // audio from byte 13 (spec PR #167).
    private static byte[] ChunkWithSendAhead(byte type, long ts, uint sendAhead, params byte[] audio)
    {
        var buf = new byte[13 + audio.Length];
        buf[0] = type;
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(1, 8), ts);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(9, 4), sendAhead);
        audio.CopyTo(buf, 13);
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
        Assert.Equal(0u, chunk.SendAhead);
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

    [Fact]
    public void ParseAudioChunk_ReadsTimestampAndSendAhead_BigEndian()
    {
        // Hand-built 13-byte header with byte-asymmetric values, so a wrong byte order or a
        // swapped field would change the parsed number: type(1) + timestamp int64 BE(8) +
        // send_ahead uint32 BE(4), audio from byte 13 (spec PR #167).
        byte[] data =
        [
            BinaryMessageTypes.PlayerAudio0,
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,   // timestamp
            0x0A, 0x0B, 0x0C, 0x0D,                           // send_ahead
            0xDE, 0xAD,                                        // audio
        ];

        var chunk = BinaryMessageParser.ParseAudioChunk(data);

        Assert.NotNull(chunk);
        Assert.Equal(0x0102030405060708L, chunk.ServerTimestamp);
        Assert.Equal(0x0A0B0C0Du, chunk.SendAhead);
        Assert.Equal(new byte[] { 0xDE, 0xAD }, chunk.EncodedData);
    }

    [Fact]
    public void ParseAudioChunk_RejectsAChunkShorterThanTheHeader()
    {
        // 12 bytes: a type-4 chunk one byte short of the 13-byte header. Rejected rather than
        // read with a truncated send_ahead.
        byte[] tooShort = new byte[12];
        tooShort[0] = BinaryMessageTypes.PlayerAudio0;

        Assert.Null(BinaryMessageParser.ParseAudioChunk(tooShort));
    }

    [Fact]
    public void ParseAudioChunk_ExposesSaturatedSendAhead()
    {
        // Per spec send_ahead saturates: 0xFFFFFFFF is a real value the parser surfaces as-is,
        // not a sentinel it collapses — players use it only to measure arrival delay.
        var parsed = BinaryMessageParser.ParseAudioChunk(
            ChunkWithSendAhead(BinaryMessageTypes.PlayerAudio0, 1, uint.MaxValue, 7));

        Assert.NotNull(parsed);
        Assert.Equal(uint.MaxValue, parsed.SendAhead);
    }
}
