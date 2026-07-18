using System.Text.Json.Serialization;
using Sendspin.SDK.Models;

namespace Sendspin.SDK.Protocol.Messages;

/// <summary>
/// Initial handshake message sent by the client to announce its capabilities.
/// Uses the envelope format: { "type": "client/hello", "payload": { ... } }
/// </summary>
public sealed class ClientHelloMessage : IMessageWithPayload<ClientHelloPayload>
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.ClientHello;

    [JsonPropertyName("payload")]
    required public ClientHelloPayload Payload { get; init; }

    /// <summary>
    /// Creates a ClientHelloMessage with the specified payload.
    /// </summary>
    public static ClientHelloMessage Create(
        string? clientId,
        string name,
        List<string> supportedRoles,
        PlayerSupport? playerSupport = null,
        ArtworkSupport? artworkSupport = null,
        DeviceInfo? deviceInfo = null,
        VisualizerSupport? visualizerSupport = null,
        string? trustLevel = null,
        UnpairedAccess? unpairedAccess = null)
    {
        return new ClientHelloMessage
        {
            Payload = new ClientHelloPayload
            {
                ClientId = clientId,
                Name = name,
                // Under the encrypted protocol client_id/version are omitted (they travel
                // in client/init); a null clientId marks that shape.
                Version = clientId is null ? null : 1,
                TrustLevel = trustLevel,
                UnpairedAccess = unpairedAccess,
                SupportedRoles = supportedRoles,
                PlayerV1Support = playerSupport,
                ArtworkV1Support = artworkSupport,
                VisualizerV1Support = visualizerSupport,
                DeviceInfo = deviceInfo
            }
        };
    }
}

/// <summary>
/// Payload for the client/hello message.
/// </summary>
public sealed class ClientHelloPayload
{
    /// <summary>
    /// Unique client identifier (persistent across sessions). Omitted under the
    /// encrypted protocol, where the identity travels in client/init instead.
    /// </summary>
    [JsonPropertyName("client_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClientId { get; init; }

    /// <summary>
    /// Human-readable client name.
    /// </summary>
    [JsonPropertyName("name")]
    required public string Name { get; init; }

    /// <summary>
    /// Protocol version (must be 1). Omitted under the encrypted protocol, where the
    /// version travels in client/init instead.
    /// </summary>
    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Version { get; init; } = 1;

    /// <summary>
    /// The trust level the client extends to this server ('user' when a pairing record
    /// exists, 'none' otherwise). Sent only under the encrypted protocol.
    /// </summary>
    [JsonPropertyName("trust_level")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TrustLevel { get; init; }

    /// <summary>
    /// Whether this client currently admits unpaired access. Sent only under the
    /// encrypted protocol.
    /// </summary>
    [JsonPropertyName("unpaired_access")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UnpairedAccess? UnpairedAccess { get; init; }

    /// <summary>
    /// List of roles the client supports, in priority order.
    /// Each role includes version (e.g., "player@v1", "controller@v1").
    /// </summary>
    [JsonPropertyName("supported_roles")]
    required public List<string> SupportedRoles { get; init; }

    /// <summary>
    /// Player role support details (per Sendspin spec).
    /// </summary>
    [JsonPropertyName("player@v1_support")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PlayerSupport? PlayerV1Support { get; init; }

    /// <summary>
    /// Artwork role support details.
    /// </summary>
    [JsonPropertyName("artwork@v1_support")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ArtworkSupport? ArtworkV1Support { get; init; }

    /// <summary>
    /// Visualizer role support details (types, rate, spectrum config).
    /// </summary>
    [JsonPropertyName("visualizer@v1_support")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VisualizerSupport? VisualizerV1Support { get; init; }

    /// <summary>
    /// Device information.
    /// </summary>
    [JsonPropertyName("device_info")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DeviceInfo? DeviceInfo { get; init; }
}

/// <summary>
/// Player role support details per the Sendspin spec.
/// </summary>
public sealed class PlayerSupport
{
    /// <summary>
    /// Supported audio formats.
    /// </summary>
    [JsonPropertyName("supported_formats")]
    public List<AudioFormatSpec> SupportedFormats { get; init; } = new();

    /// <summary>
    /// Audio buffer capacity in bytes.
    /// </summary>
    [JsonPropertyName("buffer_capacity")]
    public int BufferCapacity { get; init; } = 32_000_000; // 32MB like reference impl

    /// <summary>
    /// Supported player commands.
    /// </summary>
    [JsonPropertyName("supported_commands")]
    public List<string> SupportedCommands { get; init; } = new() { "volume", "mute" };
}

/// <summary>
/// Audio format specification for player support.
/// </summary>
public sealed class AudioFormatSpec
{
    [JsonPropertyName("codec")]
    required public string Codec { get; init; }

    [JsonPropertyName("channels")]
    public int Channels { get; init; } = 2;

    [JsonPropertyName("sample_rate")]
    public int SampleRate { get; init; } = 48000;

    [JsonPropertyName("bit_depth")]
    public int BitDepth { get; init; } = 16;
}

/// <summary>
/// Artwork role support details per the Sendspin spec.
/// </summary>
public sealed class ArtworkSupport
{
    /// <summary>
    /// Artwork channel specifications. Each element corresponds to a channel (0-3).
    /// </summary>
    [JsonPropertyName("channels")]
    public List<ArtworkChannelSpec> Channels { get; init; } = new();
}

/// <summary>
/// Specification for a single artwork channel.
/// </summary>
public sealed class ArtworkChannelSpec
{
    /// <summary>
    /// The source type for this artwork channel.
    /// </summary>
    [JsonPropertyName("source")]
    public string Source { get; init; } = "album";

    /// <summary>
    /// Preferred image format for this channel.
    /// </summary>
    [JsonPropertyName("format")]
    public string Format { get; init; } = "jpeg";

    /// <summary>
    /// Maximum image width in pixels.
    /// </summary>
    [JsonPropertyName("media_width")]
    public int MediaWidth { get; init; } = 512;

    /// <summary>
    /// Maximum image height in pixels.
    /// </summary>
    [JsonPropertyName("media_height")]
    public int MediaHeight { get; init; } = 512;
}

/// <summary>
/// Device information reported to the server.
/// All fields are optional and will be omitted from JSON if null.
/// </summary>
public sealed class DeviceInfo
{
    /// <summary>
    /// Product name (e.g., "Sendspin Windows Client", "My Custom Player").
    /// </summary>
    [JsonPropertyName("product_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProductName { get; init; }

    /// <summary>
    /// Manufacturer name (e.g., "Anthropic", "My Company").
    /// </summary>
    [JsonPropertyName("manufacturer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Manufacturer { get; init; }

    /// <summary>
    /// Software version string.
    /// </summary>
    [JsonPropertyName("software_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SoftwareVersion { get; init; }

    /// <summary>
    /// MAC address of the network interface the connection is opened on, in lowercase
    /// colon-separated form (e.g., "aa:bb:cc:dd:ee:ff"). Optional.
    /// </summary>
    [JsonPropertyName("mac_address")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MacAddress { get; init; }
}

/// <summary>
/// The client's unpaired-access setting, advertised in <c>client/hello</c> under the
/// encrypted protocol.
/// </summary>
public sealed class UnpairedAccess
{
    /// <summary>Whether the client admits servers with no pairing record.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }
}
