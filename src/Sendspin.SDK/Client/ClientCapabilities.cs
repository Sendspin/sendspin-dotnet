using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Client;

/// <summary>
/// Defines the capabilities this client advertises to the server.
/// </summary>
public sealed class ClientCapabilities
{
    private int? _bufferCapacityOverride;
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
    /// Audio buffer capacity in compressed bytes, advertised in <c>client/hello</c>. Derived
    /// from the SDK's decoded-buffer duration and <see cref="AudioFormats"/> unless set
    /// explicitly, and clamped to that derived ceiling if it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The spec makes this a hard per-player byte limit that servers fill toward
    /// (roles/player/v1.md:34-35), so it is a promise about what this client can hold, not a
    /// hint. It used to default to a flat 32 MB with no relationship to the actual buffer —
    /// which meant a server behaving exactly as the spec allows could send minutes of Opus to
    /// a client holding a fraction of a second of it, and everything past the buffer was
    /// discarded before it played.
    /// </para>
    /// <para>
    /// The derivation assumes <c>TimedAudioBuffer</c>'s default decoded capacity, which is
    /// also what a caller gets from <c>new TimedAudioBuffer(format, clockSync)</c>. A value set
    /// here is honoured only up to that ceiling: over-advertising is the bug this exists to
    /// close, so a larger figure is clamped and reported once at <c>client/hello</c> time. A
    /// client that really does hold more can only say so on the 10.x line, which has an
    /// explicit decoded-capacity property; 9.x's surface is frozen.
    /// </para>
    /// </remarks>
    public int BufferCapacity
    {
        get => _bufferCapacityOverride is { } configured
            ? Math.Min(configured, TruthfulBufferCapacityBytes)
            : TruthfulBufferCapacityBytes;
        set => _bufferCapacityOverride = value;
    }

    /// <summary>
    /// The largest <c>buffer_capacity</c> this client can honour: what the SDK's default
    /// decoded buffer holds, for whichever of <see cref="AudioFormats"/> packs the most audio
    /// into a byte.
    /// </summary>
    internal int TruthfulBufferCapacityBytes =>
        PlayerBufferCapacity.AdvertisedBytes(
            PlayerBufferCapacity.DefaultDecodedBufferMilliseconds, AudioFormats);

    /// <summary>
    /// Whether a caller set <see cref="BufferCapacity"/> above what the buffer can hold, and
    /// is therefore being clamped.
    /// </summary>
    internal bool BufferCapacityWasClamped =>
        _bufferCapacityOverride > TruthfulBufferCapacityBytes;

    /// <summary>The value a caller set, before clamping. Null when none was set.</summary>
    internal int? ConfiguredBufferCapacity => _bufferCapacityOverride;

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
