using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Client;

/// <summary>
/// Defines the capabilities this client advertises to the server.
/// </summary>
public sealed class ClientCapabilities
{
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

    /// <summary>
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
    /// How this client's source role behaves (line sensing, encoded codec). Null means no
    /// source support configured. Only meaningful when the 'source@v1' role and a capture
    /// device are configured.
    /// </summary>
    /// <remarks>
    /// Named for its type, not for the wire field: <see cref="Protocol.Messages.SourceSupport"/>
    /// is the <c>source@v1_support</c> payload, and a property called <c>SourceSupport</c>
    /// here made the obvious assignment bind that type instead when both namespaces were
    /// imported.
    /// </remarks>
    public SourceRoleSupport? SourceRoleSupport { get; set; }

    /// <summary>
    /// PIN pairing methods this client offers in addition to the mandatory Pairing PSK
    /// method, in the encrypted protocol. Empty by default (Pairing PSK only). Add
    /// "dynamic_pin" and/or "static_pin". Dynamic PIN requires
    /// <see cref="SendspinClientOptions.PresentPinAsync"/>; without it the method is refused.
    /// </summary>
    public List<string> PinPairingMethods { get; set; } = new();

    /// <summary>Shortest dynamic PIN length in digits this client accepts (4-12). Default 6.</summary>
    public int MinPinLength { get; set; } = 6;

    /// <summary>
    /// Out-channels through which the dynamic PIN is conveyed to the operator
    /// (informational hint: "display" or "speaker"). Default ["display"].
    /// </summary>
    /// <remarks>
    /// "other" was a third permitted value until spec commit <c>3f8528a9</c> removed it. The
    /// hint is informational and never grounds for <c>pair/abort</c>, so an out-of-vocabulary
    /// entry does not break pairing — but it is no longer a value the spec defines.
    /// </remarks>
    public List<string> PinOutChannels { get; set; } = new() { "display" };

    /// <summary>
    /// For static PIN: the device-specific fixed PIN (8 digits). Required if
    /// "static_pin" is offered.
    /// </summary>
    public string? StaticPin { get; set; }

    /// <summary>
    /// Where an operator can find this device's static PIN, advertised as the <c>locations</c>
    /// hint on the <c>static_pin</c> pair-method descriptor. Values are <c>"device"</c>
    /// (printed on it), <c>"leaflet"</c> (in the box), and <c>"operator"</c> (they set it).
    /// Empty by default, which omits the hint — the SDK cannot know where your secret is
    /// printed, and a wrong hint is worse than none.
    /// </summary>
    /// <remarks>
    /// Purely informational: it drives server UX copy like "check the label on the device",
    /// and no pairing decision depends on it. <b>The SDK overrides this to
    /// <c>["operator"]</c> once a server sets the PIN through
    /// <c>management/set-pairing-config</c></b> — at that point the operator chose the secret
    /// and any printed copy is stale, which is what the spec's "the client updates the hint
    /// accordingly" requires. The new value arrives on
    /// <see cref="PairingConfigChangedEventArgs.StaticPinLocations"/> for the app to persist
    /// alongside the rotated PIN (#129).
    /// </remarks>
    public List<string> StaticPinLocations { get; set; } = new();

    /// <summary>
    /// Where an operator can find this device's Pairing PSK, advertised as the
    /// <c>locations</c> hint on the <c>pairing_psk</c> descriptor. Same vocabulary and same
    /// server-rotation override as <see cref="StaticPinLocations"/>; empty by default.
    /// </summary>
    /// <remarks>
    /// A Pairing PSK the client mints itself (<see cref="ISendspinClient.EnsurePairingPsk"/>,
    /// <see cref="ISendspinClient.RotatePairingPsk"/>) does <em>not</em> flip the hint: the
    /// client generated it, so it is still found wherever the app renders it — typically the
    /// device's own display. Only a server supplying the PSK does.
    /// </remarks>
    public List<string> PairingPskLocations { get; set; } = new();

    /// <summary>
    /// Whether the mandatory Pairing PSK method starts enabled. Default true. Set false only
    /// to restore a server's <c>management/set-pairing-config</c> change: a server that
    /// disabled this method expects it to stay disabled across a restart, and leaving the
    /// default would silently re-offer Pairing-PSK pairing.
    /// </summary>
    /// <remarks>
    /// <see cref="ISendspinClient.EnsurePairingPsk"/> and
    /// <see cref="ISendspinClient.RotatePairingPsk"/> do not check this flag: an app that
    /// sets it false and still calls either to render a pairing token (a QR code, say) hands
    /// the operator a token whose method the client will refuse, with no error raised.
    /// </remarks>
    public bool PairingPskEnabled { get; set; } = true;

    /// <summary>
    /// Whether <c>"dynamic_pin"</c> starts enabled, when it is listed in
    /// <see cref="PinPairingMethods"/>. Default true, so listing the method is enough to
    /// offer it. Ignored when the method is not listed.
    /// </summary>
    /// <remarks>
    /// Distinct from removing the method from <see cref="PinPairingMethods"/>, which means
    /// <em>not implemented</em>. A disabled-but-implemented method still reports itself to a
    /// managing server with <c>enabled: false</c> and can be turned back on with
    /// <c>set-pairing-config</c>; an unimplemented one is omitted entirely and can never be
    /// re-enabled.
    /// </remarks>
    public bool DynamicPinEnabled { get; set; } = true;

    /// <summary>
    /// Whether <c>"static_pin"</c> starts enabled, when it is listed in
    /// <see cref="PinPairingMethods"/>. Default true. See
    /// <see cref="DynamicPinEnabled"/> for why this is not the same as omitting the method.
    /// </summary>
    public bool StaticPinEnabled { get; set; } = true;

    /// <summary>
    /// The shared-PSK record this client falls back to when its stored-pubkey record space
    /// is exhausted, as last set by a server's <c>management/set-pairing-config</c>. Null by
    /// default. Ignored unless it names a shared-PSK record still present in the pairing
    /// record store — a server may have removed that record while the app was down.
    /// </summary>
    /// <remarks>
    /// When an id you persisted here is ignored, the client logs a warning rather than
    /// raising — a store the app doesn't control is not the client's error to throw. Treat
    /// that warning as a signal to clear the persisted value: left in place, it is relogged
    /// and re-ignored on every subsequent start.
    /// </remarks>
    public string? RecordModePskId { get; set; }

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
