using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Tests.Client;

namespace Sendspin.SDK.Tests.Protocol;

/// <summary>
/// Binary decode coverage for the three artwork messages (types 8-11), mirroring the aiosendspin
/// 10.0.0 wire: an announce <c>[type][flags][timestamp][total_size]</c>, a part
/// <c>[type][flags][data]</c> and a cancel <c>[type][flags]</c>. Each message the spec lists as
/// malformed must fail to parse, which is what the client closes the connection over.
/// </summary>
public class ArtworkMessageParsingTests
{
    [Theory]
    [InlineData(BinaryMessageTypes.Artwork0, 0)]
    [InlineData(BinaryMessageTypes.Artwork1, 1)]
    [InlineData(BinaryMessageTypes.Artwork2, 2)]
    [InlineData(BinaryMessageTypes.Artwork3, 3)]
    public void Announce_Parses(byte type, byte expectedChannel)
    {
        // Every byte distinct, so a little-endian or misaligned read cannot pass.
        const long timestamp = 0x0102030405060708;
        const uint totalSize = 0x090A0B0C;

        Assert.True(BinaryMessageParser.TryParseArtwork(
            ArtworkWire.Announce(type, timestamp, totalSize), out var message, out var partData));

        Assert.Equal(ArtworkMessageKind.Announce, message.Kind);
        Assert.Equal(expectedChannel, message.Channel);
        Assert.Equal(timestamp, message.Timestamp);
        Assert.Equal(totalSize, message.TotalSize);
        Assert.True(partData.IsEmpty);
    }

    [Fact]
    public void MessagesPackedByAiosendspin_Parse()
    {
        // The output of aiosendspin 10.0.0's pack_artwork_announce(2, 0x0102030405060708, 5),
        // pack_artwork_parts(2, b"\x01\x02\x03\x04\x05") and pack_artwork_cancel(2), so the
        // helper the other tests build messages with cannot share a mistake with the parser.
        Assert.True(BinaryMessageParser.TryParseArtwork(
            Convert.FromHexString("0a02010203040506070800000005"), out var announce, out _));
        Assert.Equal(new ArtworkMessage(ArtworkMessageKind.Announce, 2, 0x0102030405060708, 5), announce);

        Assert.True(BinaryMessageParser.TryParseArtwork(
            Convert.FromHexString("0a000102030405"), out var part, out var partData));
        Assert.Equal(ArtworkMessageKind.Part, part.Kind);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, partData.ToArray());

        Assert.True(BinaryMessageParser.TryParseArtwork(
            Convert.FromHexString("0a01"), out var cancel, out _));
        Assert.Equal(new ArtworkMessage(ArtworkMessageKind.Cancel, 2, 0, 0), cancel);
    }

    [Fact]
    public void Part_Parses()
    {
        Assert.True(BinaryMessageParser.TryParseArtwork(
            ArtworkWire.Part(BinaryMessageTypes.Artwork2, 1, 2, 3), out var message, out var partData));

        Assert.Equal(ArtworkMessageKind.Part, message.Kind);
        Assert.Equal(2, message.Channel);
        Assert.Equal(new byte[] { 1, 2, 3 }, partData.ToArray());
    }

    [Fact]
    public void Part_StartingWithBytesThatLookLikeAnAnnounce_IsStillAPart()
    {
        // Only byte 1 is flags. Image data is opaque, and a 14-byte part is not an announce.
        var part = ArtworkWire.Part(BinaryMessageTypes.Artwork0, 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        Assert.True(BinaryMessageParser.TryParseArtwork(part, out var message, out var partData));

        Assert.Equal(ArtworkMessageKind.Part, message.Kind);
        Assert.Equal(12, partData.Length);
    }

    [Fact]
    public void Cancel_Parses()
    {
        Assert.True(BinaryMessageParser.TryParseArtwork(
            ArtworkWire.Cancel(BinaryMessageTypes.Artwork3), out var message, out var partData));

        Assert.Equal(ArtworkMessageKind.Cancel, message.Kind);
        Assert.Equal(3, message.Channel);
        Assert.True(partData.IsEmpty);
    }

    [Fact]
    public void MessageAtTheSizeCap_Parses()
    {
        var part = new byte[BinaryMessageParser.MaxArtworkMessageSize];
        part[0] = BinaryMessageTypes.Artwork0;

        Assert.True(BinaryMessageParser.TryParseArtwork(part, out _, out var partData));
        Assert.Equal(BinaryMessageParser.MaxArtworkMessageSize - 2, partData.Length);
    }

    [Fact]
    public void MessageShorterThanTwoBytes_IsMalformed()
    {
        Assert.False(BinaryMessageParser.TryParseArtwork(new byte[] { BinaryMessageTypes.Artwork0 }, out _, out _));
        Assert.False(BinaryMessageParser.TryParseArtwork(ReadOnlySpan<byte>.Empty, out _, out _));
    }

    [Fact]
    public void MessageExceedingTheSizeCap_IsMalformed()
    {
        var part = new byte[BinaryMessageParser.MaxArtworkMessageSize + 1];
        part[0] = BinaryMessageTypes.Artwork0;

        Assert.False(BinaryMessageParser.TryParseArtwork(part, out _, out _));
    }

    [Theory]
    [InlineData(13)]
    [InlineData(15)]
    [InlineData(2)]
    public void AnnounceWhoseLengthIsNotFourteenBytes_IsMalformed(int length)
    {
        var announce = new byte[length];
        announce[0] = BinaryMessageTypes.Artwork0;
        announce[1] = 0x02;

        Assert.False(BinaryMessageParser.TryParseArtwork(announce, out _, out _));
    }

    [Fact]
    public void CancelLongerThanTwoBytes_IsMalformed()
    {
        Assert.False(BinaryMessageParser.TryParseArtwork(
            new byte[] { BinaryMessageTypes.Artwork0, 0x01, 0 }, out _, out _));
    }

    [Theory]
    [InlineData(0x04)]
    [InlineData(0x80)]
    [InlineData(0x06)]
    [InlineData(0x05)]
    public void NonzeroReservedFlagBit_IsMalformed(byte flags)
    {
        // Sized as an announce, so nothing but the reserved bit can be what rejects it.
        var message = ArtworkWire.Announce(BinaryMessageTypes.Artwork0, 1, 1);
        message[1] = flags;

        Assert.False(BinaryMessageParser.TryParseArtwork(message, out _, out _));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(14)]
    public void AnnounceAndCancelBothSet_IsMalformed(int length)
    {
        // At the length of a cancel and at the length of an announce: valid as neither.
        var message = new byte[length];
        message[0] = BinaryMessageTypes.Artwork0;
        message[1] = 0x03;

        Assert.False(BinaryMessageParser.TryParseArtwork(message, out _, out _));
    }

    [Fact]
    public void NonArtworkType_DoesNotParse()
    {
        Assert.False(BinaryMessageParser.TryParseArtwork(
            ArtworkWire.Cancel(BinaryMessageTypes.PlayerAudio0), out _, out _));
    }
}
