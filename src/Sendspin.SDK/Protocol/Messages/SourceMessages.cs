using System.Text.Json.Serialization;

namespace Sendspin.SDK.Protocol.Messages;

/// <summary>
/// Announces the active input stream format for a <c>source</c> client and provides any
/// required codec header. Sent before the first source audio chunk; re-sending while a
/// stream is open replaces the format in place.
/// </summary>
public sealed class ClientStreamStartMessage : IMessageWithPayload<ClientStreamStartPayload>
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.ClientStreamStart;

    [JsonPropertyName("payload")]
    public ClientStreamStartPayload Payload { get; set; } = new();
}

/// <summary>Payload of <c>client_stream/start</c>.</summary>
public sealed class ClientStreamStartPayload
{
    /// <summary>The source stream format.</summary>
    [JsonPropertyName("source")]
    public SourceStreamFormat Source { get; set; } = new();
}

/// <summary>The captured-input format announced in <c>client_stream/start</c>.</summary>
public sealed class SourceStreamFormat
{
    /// <summary>Codec: 'opus', 'flac', or 'pcm'.</summary>
    [JsonPropertyName("codec")]
    public string Codec { get; set; } = "pcm";

    /// <summary>Channel count.</summary>
    [JsonPropertyName("channels")]
    public int Channels { get; set; } = 2;

    /// <summary>Sample rate in Hz.</summary>
    [JsonPropertyName("sample_rate")]
    public int SampleRate { get; set; } = 48000;

    /// <summary>Bit depth.</summary>
    [JsonPropertyName("bit_depth")]
    public int BitDepth { get; set; } = 16;

    /// <summary>Base64 codec header, if the codec requires one (e.g. FLAC).</summary>
    [JsonPropertyName("codec_header")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CodecHeader { get; set; }
}

/// <summary>
/// Ends the current input stream. No more source audio chunks are sent until the next
/// <c>client_stream/start</c>.
/// </summary>
public sealed class ClientStreamEndMessage : IMessage
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.ClientStreamEnd;

    [JsonPropertyName("payload")]
    public object Payload { get; set; } = new();
}
