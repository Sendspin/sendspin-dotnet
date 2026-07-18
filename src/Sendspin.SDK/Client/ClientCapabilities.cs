using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Client;

/// <summary>
/// Defines the capabilities this client advertises to the server.
/// </summary>
public sealed class ClientCapabilities
{
    /// <summary>
    /// Unique client identifier (persisted across sessions).
    /// Format follows reference implementation: sendspin-windows-{hostname}
    /// </summary>
    public string ClientId { get; set; } = $"sendspin-windows-{Environment.MachineName.ToLowerInvariant()}";

    /// <summary>
    /// Human-readable client name.
    /// </summary>
    public string ClientName { get; set; } = Environment.MachineName;

    /// <summary>
    /// Roles this client supports, in priority order.
    /// </summary>
    public List<string> Roles { get; set; } = new()
    {
        "controller@v1",
        "player@v1",
        "metadata@v1",
        "artwork@v1",
        "color@v1"
    };

    /// <summary>
    /// Audio formats the client can decode.
    /// Order matters - server picks the first format it supports.
    /// </summary>
    public List<AudioFormat> AudioFormats { get; set; } = new()
    {
        new AudioFormat { Codec = "opus", SampleRate = 48000, Channels = 2, Bitrate = 256 },
        new AudioFormat { Codec = "pcm", SampleRate = 48000, Channels = 2, BitDepth = 16 },
        new AudioFormat { Codec = "flac", SampleRate = 48000, Channels = 2 },  // Last - server prefers earlier formats
    };

    /// <summary>
    /// Audio buffer capacity in compressed bytes. The server uses this to limit how much
    /// audio it sends ahead. Should be derived from your PCM buffer duration and the
    /// highest-bitrate codec you support. Default is 32MB (reference implementation fallback).
    /// </summary>
    public int BufferCapacity { get; set; } = 32_000_000;

    /// <summary>
    /// Artwork channels this client can display, advertised in <c>client/hello</c>. The Sendspin
    /// spec allows 1-4 independent channels (array index = channel number), each with its own
    /// source, format, and maximum dimensions. The default is a single album/jpeg channel at
    /// 512x512. Set a channel's <see cref="ArtworkChannelSpec.Source"/> to <c>"none"</c> to advertise
    /// a channel the client does not currently want streamed. Entries beyond the first four are
    /// ignored. Remove <c>"artwork@v1"</c> from <see cref="Roles"/> to opt out of artwork entirely.
    /// </summary>
    /// <remarks>
    /// Deliberately reuses the wire type <see cref="ArtworkChannelSpec"/> as config: the capability
    /// and hello shapes are identical today. Introduce a separate config type only if they diverge.
    /// </remarks>
    public List<ArtworkChannelSpec> ArtworkChannels { get; set; } = new()
    {
        new ArtworkChannelSpec { Source = "album", Format = "jpeg", MediaWidth = 512, MediaHeight = 512 }
    };

    /// <summary>
    /// Visualizer support advertised in <c>client/hello</c> (types, rate, spectrum config). Opt-in:
    /// null by default, and the <c>visualizer@v1</c> role is not advertised unless this is set. To
    /// enable, set this AND add <c>"visualizer@v1"</c> to <see cref="Roles"/>. The client must be
    /// able to render the feature types it lists; subscribe to visualization frames to consume them.
    /// </summary>
    public VisualizerSupport? VisualizerSupport { get; set; }

    /// <summary>
    /// Product name reported to the server (e.g., "Sendspin Windows Client", "My Custom Player").
    /// </summary>
    public string? ProductName { get; set; }

    /// <summary>
    /// Manufacturer name reported to the server (e.g., "Anthropic", "My Company").
    /// </summary>
    public string? Manufacturer { get; set; }

    /// <summary>
    /// Software version reported to the server.
    /// If null, will not be included in the device info.
    /// </summary>
    public string? SoftwareVersion { get; set; }

    /// <summary>
    /// MAC address of the network interface used for the connection, reported to the server in the
    /// device info. Use lowercase colon-separated form (e.g., "aa:bb:cc:dd:ee:ff"). If null, it is
    /// omitted from the device info.
    /// </summary>
    public string? MacAddress { get; set; }
    /// Minimum startup lead time in milliseconds reported to the server (codec init, decode
    /// warmup, audio backend buffering, DAC latency). The server schedules the first audio chunk
    /// at least this far ahead after a stream start/restart, preventing start-of-stream truncation.
    /// <para>
    /// Default (200 ms) is a conservative LAN starting point. Tune per device/network: report the
    /// lowest value that reliably avoids truncation for the lowest latency. Do NOT include
    /// <c>static_delay_ms</c> here — the server applies that separately. For empirical tuning, the
    /// audio pipeline exposes measured output/startup latency (e.g. DetectedOutputLatencyMs).
    /// </para>
    /// </summary>
    public int RequiredLeadTimeMs { get; set; } = 200;

    /// <summary>
    /// Requested minimum ongoing buffer duration in milliseconds reported to the server, used to
    /// absorb network jitter and decode/playback timing variance (primarily for live streams,
    /// where the queue cannot grow after playback begins).
    /// <para>
    /// Default (150 ms) is a conservative LAN starting point. Tune per network: larger for remote
    /// or high-latency links, smaller for stable LAN. Do NOT include <c>static_delay_ms</c> here.
    /// </para>
    /// </summary>
    public int MinBufferMs { get; set; } = 150;

    /// <summary>
    /// Whether this client accepts the server's <c>set_static_delay</c> command. When true, the
    /// client advertises 'set_static_delay' in the client/state player object and applies inbound
    /// set_static_delay commands to its static delay. Default is true.
    /// </summary>
    public bool SupportsSetStaticDelay { get; set; } = true;

    /// <summary>
    /// Whether this client admits servers with no pairing record over the encrypted
    /// protocol (spec "unpaired access"). Off by default: unpaired playback sessions
    /// are vulnerable to man-in-the-middle attacks on the local network. Only
    /// meaningful when the connection uses the Noise transport.
    /// </summary>
    public bool UnpairedAccessEnabled { get; set; }

    /// <summary>
    /// PIN pairing methods this client offers in addition to the mandatory Pairing PSK
    /// method, in the encrypted protocol. Empty by default (Pairing PSK only). Add
    /// "dynamic_pin" and/or "static_pin". Dynamic PIN requires <see cref="EmitPin"/>.
    /// </summary>
    public List<string> PinPairingMethods { get; set; } = new();

    /// <summary>Shortest dynamic PIN length in digits this client accepts (4-12). Default 6.</summary>
    public int MinPinLength { get; set; } = 6;

    /// <summary>
    /// Out-channels through which the dynamic PIN is conveyed to the operator
    /// (informational hint: "display", "speaker", "other"). Default ["display"].
    /// </summary>
    public List<string> PinOutChannels { get; set; } = new() { "display" };

    /// <summary>
    /// For static PIN: the device-specific fixed PIN (8 digits). Required if
    /// "static_pin" is offered.
    /// </summary>
    public string? StaticPin { get; set; }

    /// <summary>
    /// Callback invoked with the derived dynamic PIN so the app can emit it via its
    /// out-channel (display/speaker) for the operator to enter into the server.
    /// Required when "dynamic_pin" is offered.
    /// </summary>
    public Action<string>? EmitPin { get; set; }

    /// <summary>
    /// Initial volume level (0-100) to report to the server after connection.
    /// This is sent in the initial client/state message after handshake.
    /// Default is 100 for backwards compatibility.
    /// </summary>
    public int InitialVolume { get; set; } = 100;

    /// <summary>
    /// Initial mute state to report to the server after connection.
    /// This is sent in the initial client/state message after handshake.
    /// </summary>
    public bool InitialMuted { get; set; }
}
