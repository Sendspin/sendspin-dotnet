using System.Buffers.Binary;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Protocol;

/// <summary>
/// Parses binary protocol messages (audio chunks, artwork, visualizer data).
/// </summary>
public static class BinaryMessageParser
{
    /// <summary>
    /// Minimum binary message size (1 byte type + 8 bytes timestamp). The visualizer role's
    /// header; the player audio chunk adds <c>send_ahead</c> on top of it (see
    /// <see cref="PlayerAudioHeaderSize"/>), and artwork has a framing of its own (see
    /// <see cref="TryParseArtwork"/>).
    /// </summary>
    public const int MinimumMessageSize = 9;

    /// <summary>
    /// Player audio chunk header size: 1 byte type + 8 bytes timestamp + 4 bytes
    /// <c>send_ahead</c> (spec roles/player/v1.md). Only the player role carries
    /// <c>send_ahead</c>; the visualizer keeps the <see cref="MinimumMessageSize"/> header.
    /// </summary>
    public const int PlayerAudioHeaderSize = 13;

    /// <summary>
    /// Size of the <c>[type][flags]</c> prefix every artwork message starts with (spec
    /// roles/artwork/v1.md): the whole of a cancel, and the header of a part.
    /// </summary>
    public const int ArtworkPrefixSize = 2;

    /// <summary>
    /// Exact size of an artwork announce: <c>[type][flags][timestamp int64 BE][total_size uint32 BE]</c>.
    /// </summary>
    public const int ArtworkAnnounceSize = 14;

    /// <summary>
    /// Largest artwork message the spec allows, so that one fits in a single Noise transport
    /// message without fragmentation.
    /// </summary>
    public const int MaxArtworkMessageSize = 65519;

    private const byte ArtworkCancelFlag = 0x01;
    private const byte ArtworkAnnounceFlag = 0x02;

    /// <summary>
    /// Parses a binary message header.
    /// </summary>
    /// <param name="data">Raw binary message data.</param>
    /// <param name="messageType">The message type identifier.</param>
    /// <param name="timestamp">Server timestamp in microseconds.</param>
    /// <param name="payload">The payload data after the header.</param>
    /// <returns>True if parsing succeeded.</returns>
    public static bool TryParse(
        ReadOnlySpan<byte> data,
        out byte messageType,
        out long timestamp,
        out ReadOnlySpan<byte> payload)
    {
        messageType = 0;
        timestamp = 0;
        payload = default;

        if (data.Length < MinimumMessageSize)
        {
            return false;
        }

        messageType = data[0];
        timestamp = BinaryPrimitives.ReadInt64BigEndian(data.Slice(1, 8));
        payload = data.Slice(MinimumMessageSize);

        return true;
    }

    /// <summary>
    /// Parses a binary audio message. Only <see cref="BinaryMessageTypes.PlayerAudio0"/> is an
    /// audio chunk: <c>player@v1</c> defines a single slot, so types 5-7 — allocated to the role
    /// but undefined — return null rather than a chunk claiming to be playable.
    /// </summary>
    /// <remarks>
    /// The player chunk header is <c>[type][timestamp int64 BE][send_ahead uint32 BE]</c> with
    /// audio from byte 13 (spec PR #167), so it does not go through <see cref="TryParse"/> (the
    /// 9-byte visualizer header). A chunk shorter than the header is rejected.
    /// </remarks>
    public static AudioChunk? ParseAudioChunk(ReadOnlySpan<byte> data)
    {
        if (data.Length < PlayerAudioHeaderSize)
        {
            return null;
        }

        if (data[0] != BinaryMessageTypes.PlayerAudio0)
        {
            return null;
        }

        return new AudioChunk
        {
            ServerTimestamp = BinaryPrimitives.ReadInt64BigEndian(data.Slice(1, 8)),
            SendAhead = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(9, 4)),
            EncodedData = data.Slice(PlayerAudioHeaderSize).ToArray()
        };
    }

    /// <summary>
    /// Parses a binary artwork message: an announce, a part, or a cancel (spec
    /// roles/artwork/v1.md, "Artwork (Binary)").
    /// </summary>
    /// <remarks>
    /// Returns false for exactly the messages the spec lists as malformed, which the receiver
    /// MUST close the connection over: "a message shorter than 2 bytes or exceeding the size cap
    /// above, an announce whose length is not 14 bytes, a cancel message longer than 2 bytes, a
    /// nonzero reserved flag bit, and a message with bit 0 and bit 1 both set".
    /// </remarks>
    /// <param name="data">Raw binary message data.</param>
    /// <param name="message">The parsed message.</param>
    /// <param name="partData">The image bytes a part carries; empty for an announce or a cancel.</param>
    /// <returns>True if <paramref name="data"/> is a well-formed artwork message.</returns>
    public static bool TryParseArtwork(
        ReadOnlySpan<byte> data,
        out ArtworkMessage message,
        out ReadOnlySpan<byte> partData)
    {
        message = default;
        partData = default;

        if (data.Length < ArtworkPrefixSize || data.Length > MaxArtworkMessageSize)
        {
            return false;
        }

        if (!BinaryMessageTypes.IsArtwork(data[0]))
        {
            return false;
        }

        var channel = (byte)(data[0] - BinaryMessageTypes.Artwork0);

        switch (data[1])
        {
            case ArtworkAnnounceFlag:
                if (data.Length != ArtworkAnnounceSize)
                {
                    return false;
                }

                message = new ArtworkMessage(
                    ArtworkMessageKind.Announce,
                    channel,
                    BinaryPrimitives.ReadInt64BigEndian(data.Slice(2, 8)),
                    BinaryPrimitives.ReadUInt32BigEndian(data.Slice(10, 4)));
                return true;

            case ArtworkCancelFlag:
                if (data.Length != ArtworkPrefixSize)
                {
                    return false;
                }

                message = new ArtworkMessage(ArtworkMessageKind.Cancel, channel, 0, 0);
                return true;

            case 0:
                message = new ArtworkMessage(ArtworkMessageKind.Part, channel, 0, 0);
                partData = data.Slice(ArtworkPrefixSize);
                return true;

            default:
                // A reserved bit, or announce and cancel together.
                return false;
        }
    }

    /// <summary>
    /// Parses a binary visualizer message into a <see cref="Sendspin.SDK.Models.VisualizerFrame"/>.
    /// Each visualizer binary carries exactly one feature type; the payload width is fixed per type
    /// (and, for spectrum, derived from the negotiated bin count). Returns null for a non-visualizer
    /// type or any malformed/wrong-length payload, so a bad frame is dropped rather than throwing.
    /// </summary>
    /// <param name="data">Raw binary message data (including the 9-byte header).</param>
    /// <param name="spectrumBinCount">The negotiated spectrum display-bin count, or null if spectrum was not negotiated.</param>
    public static Sendspin.SDK.Models.VisualizerFrame? ParseVisualizerFrame(ReadOnlySpan<byte> data, int? spectrumBinCount)
    {
        if (!TryParse(data, out var type, out var timestamp, out var payload))
        {
            return null;
        }

        switch (type)
        {
            case BinaryMessageTypes.VisualizerLoudness:
                return payload.Length == 2
                    ? new Sendspin.SDK.Models.VisualizerFrame { Timestamp = timestamp, Loudness = BinaryPrimitives.ReadUInt16BigEndian(payload) }
                    : null;

            case BinaryMessageTypes.VisualizerBeat:
                return payload.Length == 1
                    ? new Sendspin.SDK.Models.VisualizerFrame { Timestamp = timestamp, IsDownbeat = (payload[0] & 0x01) != 0 }
                    : null;

            case BinaryMessageTypes.VisualizerFPeak:
                return payload.Length == 4
                    ? new Sendspin.SDK.Models.VisualizerFrame
                    {
                        Timestamp = timestamp,
                        FPeakFrequency = BinaryPrimitives.ReadUInt16BigEndian(payload),
                        FPeakAmplitude = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(2, 2))
                    }
                    : null;

            case BinaryMessageTypes.VisualizerSpectrum:
                if (spectrumBinCount is not int bins || bins <= 0 || payload.Length != bins * 2)
                {
                    return null;
                }

                var spectrum = new int[bins];
                for (var i = 0; i < bins; i++)
                {
                    spectrum[i] = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(i * 2, 2));
                }

                return new Sendspin.SDK.Models.VisualizerFrame { Timestamp = timestamp, Spectrum = spectrum };

            case BinaryMessageTypes.VisualizerPeak:
                return payload.Length == 1
                    ? new Sendspin.SDK.Models.VisualizerFrame { Timestamp = timestamp, PeakStrength = payload[0] }
                    : null;

            default:
                return null;
        }
    }

    /// <summary>
    /// Gets the message category from a binary message type byte.
    /// </summary>
    public static BinaryMessageCategory GetCategory(byte messageType)
    {
        if (BinaryMessageTypes.IsPlayerAudio(messageType))
            return BinaryMessageCategory.PlayerAudio;
        if (BinaryMessageTypes.IsArtwork(messageType))
            return BinaryMessageCategory.Artwork;
        if (BinaryMessageTypes.IsVisualizer(messageType))
            return BinaryMessageCategory.Visualizer;
        if (messageType >= 192)
            return BinaryMessageCategory.ApplicationSpecific;

        return BinaryMessageCategory.Unknown;
    }
}

/// <summary>
/// Categories of binary messages.
/// </summary>
public enum BinaryMessageCategory
{
    Unknown,
    PlayerAudio,
    Artwork,
    Visualizer,
    ApplicationSpecific
}

/// <summary>
/// Represents a parsed audio chunk.
/// </summary>
public sealed class AudioChunk
{
    /// <summary>
    /// Server timestamp when this audio should be played (microseconds).
    /// </summary>
    public long ServerTimestamp { get; init; }

    /// <summary>
    /// Microseconds from the server's transmission of this chunk to its
    /// <see cref="ServerTimestamp"/> (spec <c>send_ahead</c>). Saturates rather than wrapping:
    /// <c>0</c> when the server transmits at or after the timestamp, <c>0xFFFFFFFF</c> when the
    /// true lead exceeds the field. Per spec it carries no scheduling meaning — players use it
    /// only to measure arrival delay — so it is exposed here and fed to nothing else.
    /// </summary>
    public uint SendAhead { get; init; }

    /// <summary>
    /// Encoded audio data (Opus/FLAC/PCM).
    /// </summary>
    required public byte[] EncodedData { get; init; }

    /// <summary>
    /// Decoded PCM samples (set after decoding).
    /// </summary>
    public float[]? DecodedSamples { get; set; }

    /// <summary>
    /// Playback position within decoded samples.
    /// </summary>
    public int PlaybackPosition { get; set; }
}

/// <summary>
/// The three messages an artwork image transfer is made of.
/// </summary>
public enum ArtworkMessageKind
{
    /// <summary>The next bytes of the image the channel's announce opened.</summary>
    Part,

    /// <summary>Opens a transfer: carries the image's timestamp and total size, and no image data.</summary>
    Announce,

    /// <summary>Discards the channel's pending image.</summary>
    Cancel,
}

/// <summary>
/// A parsed artwork binary message. A part's image bytes are returned alongside it by
/// <see cref="BinaryMessageParser.TryParseArtwork"/> rather than copied in here.
/// </summary>
/// <param name="Kind">Which of the three messages this is.</param>
/// <param name="Channel">Artwork channel (0-3).</param>
/// <param name="Timestamp">
/// Announce only: server clock time in microseconds when the image should be displayed.
/// </param>
/// <param name="TotalSize">
/// Announce only: size in bytes of the encoded image; <c>0</c> clears the channel.
/// </param>
public readonly record struct ArtworkMessage(ArtworkMessageKind Kind, byte Channel, long Timestamp, uint TotalSize);

/// <summary>
/// A complete artwork image, reassembled from the parts of its transfer.
/// </summary>
public sealed class ArtworkChunk
{
    /// <summary>
    /// Artwork channel (0-3).
    /// </summary>
    public byte Channel { get; init; }

    /// <summary>
    /// Timestamp for this artwork, from the transfer's announce.
    /// </summary>
    public long Timestamp { get; init; }

    /// <summary>
    /// Raw image data (JPEG/PNG). Empty when the transfer announced a size of zero, which
    /// clears the channel.
    /// </summary>
    required public byte[] ImageData { get; init; }
}
