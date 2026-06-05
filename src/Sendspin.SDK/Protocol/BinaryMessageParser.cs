using System.Buffers.Binary;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Protocol;

/// <summary>
/// Parses binary protocol messages (audio chunks, artwork, visualizer data).
/// </summary>
public static class BinaryMessageParser
{
    /// <summary>
    /// Minimum binary message size (1 byte type + 8 bytes timestamp).
    /// </summary>
    public const int MinimumMessageSize = 9;

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
    /// Parses a binary audio message.
    /// </summary>
    public static AudioChunk? ParseAudioChunk(ReadOnlySpan<byte> data)
    {
        if (!TryParse(data, out var type, out var timestamp, out var payload))
        {
            return null;
        }

        if (!BinaryMessageTypes.IsPlayerAudio(type))
        {
            return null;
        }

        return new AudioChunk
        {
            Slot = (byte)(type - BinaryMessageTypes.PlayerAudio0),
            ServerTimestamp = timestamp,
            EncodedData = payload.ToArray()
        };
    }

    /// <summary>
    /// Parses a binary artwork message.
    /// </summary>
    public static ArtworkChunk? ParseArtworkChunk(ReadOnlySpan<byte> data)
    {
        if (!TryParse(data, out var type, out var timestamp, out var payload))
        {
            return null;
        }

        if (!BinaryMessageTypes.IsArtwork(type))
        {
            return null;
        }

        return new ArtworkChunk
        {
            Channel = (byte)(type - BinaryMessageTypes.Artwork0),
            Timestamp = timestamp,
            ImageData = payload.ToArray()
        };
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

            case BinaryMessageTypes.VisualizerPitch:
                return payload.Length == 3
                    ? new Sendspin.SDK.Models.VisualizerFrame
                    {
                        Timestamp = timestamp,
                        PitchMidiQ88 = BinaryPrimitives.ReadUInt16BigEndian(payload),
                        PitchConfidence = payload[2]
                    }
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
    /// Audio slot (0-3, for multi-stream).
    /// </summary>
    public byte Slot { get; init; }

    /// <summary>
    /// Server timestamp when this audio should be played (microseconds).
    /// </summary>
    public long ServerTimestamp { get; init; }

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
/// Represents a parsed artwork chunk.
/// </summary>
public sealed class ArtworkChunk
{
    /// <summary>
    /// Artwork channel (0-3).
    /// </summary>
    public byte Channel { get; init; }

    /// <summary>
    /// Timestamp for this artwork.
    /// </summary>
    public long Timestamp { get; init; }

    /// <summary>
    /// Raw image data (JPEG/PNG).
    /// </summary>
    required public byte[] ImageData { get; init; }
}
