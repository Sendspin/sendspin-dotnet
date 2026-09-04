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
    /// Decoded audio, in milliseconds, that this client's audio buffer holds. This is the
    /// single source of truth for buffering: <see cref="BufferCapacity"/> is derived from it,
    /// and the same value must be passed to <c>TimedAudioBuffer</c>'s <c>bufferCapacityMs</c>.
    /// Defaults to <see cref="PlayerBufferCapacity.DefaultDecodedBufferMilliseconds"/>, which
    /// is also that constructor's default, so leaving both alone keeps them in step.
    /// </summary>
    public int AudioBufferCapacityMs { get; set; } = PlayerBufferCapacity.DefaultDecodedBufferMilliseconds;

    /// <summary>
    /// Audio buffer capacity in compressed bytes, advertised in <c>client/hello</c>. Derived
    /// from <see cref="AudioBufferCapacityMs"/> and <see cref="AudioFormats"/> unless set
    /// explicitly.
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
    /// Setting this explicitly overrides the derivation, and hands you responsibility for the
    /// promise: the value must be one the audio buffer can actually hold for every format in
    /// <see cref="AudioFormats"/>. <see cref="PlayerBufferCapacity.HoldableMilliseconds"/>
    /// checks that. Prefer setting <see cref="AudioBufferCapacityMs"/> instead.
    /// </para>
    /// </remarks>
    public int BufferCapacity
    {
        get => _bufferCapacityOverride
            ?? PlayerBufferCapacity.AdvertisedBytes(AudioBufferCapacityMs, AudioFormats);
        set => _bufferCapacityOverride = value;
    }

    /// <summary>
    /// Artwork channels this client wants streamed, reported in the <c>client/state</c> artwork
    /// object. The Sendspin spec allows 1-4 independent channels (array index = channel number),
    /// each with its own source, format, and delivered dimensions. The default is a single
    /// album/jpeg channel at 512x512. Set a channel's <see cref="ArtworkChannelState.Source"/> to
    /// <c>"none"</c> to declare a channel the client does not currently want streamed. Entries
    /// beyond the first four are ignored. Remove <c>"artwork@v1"</c> from <see cref="Roles"/> to
    /// opt out of artwork entirely.
    /// </summary>
    /// <remarks>
    /// This is the connection's starting configuration only. Spec PR #195 made the channel
    /// declaration dynamic state rather than a <c>client/hello</c> capability, so change it at
    /// runtime through <see cref="ISendspinClient.SetArtworkChannelAsync"/> — which reconfigures
    /// the client's own copy of this list and re-reports the full client state — rather than by
    /// mutating it directly. The SDK never writes back here: each client copies the list at
    /// construction, so a host sharing one <see cref="ClientCapabilities"/> across connections
    /// does not let one connection's reconfiguration reach another.
    /// </remarks>
    public List<ArtworkChannelState> ArtworkChannels { get; set; } = new()
    {
        new ArtworkChannelState { Source = "album", Format = "jpeg", Width = 512, Height = 512 }
    };

    /// <summary>
    /// Visualizer configuration: buffer capacity for <c>client/hello</c>, plus the types, frame
    /// rate and spectrum layout reported in <c>client/state</c>. Opt-in: null by default, and the
    /// <c>visualizer@v1</c> role is not advertised unless this is set. To enable, set this AND add
    /// <c>"visualizer@v1"</c> to <see cref="Roles"/>. The client must be able to render the
    /// feature types it lists; subscribe to visualization frames to consume them.
    /// </summary>
    /// <remarks>
    /// Named for its type, not for either wire object, and for the same reason as
    /// <see cref="SourceRoleSupport"/>: a property called <c>VisualizerSupport</c> bound
    /// <see cref="Protocol.Messages.VisualizerSupport"/> — now the hello object alone — when both
    /// namespaces were imported. Change the dynamic half at runtime through
    /// <see cref="ISendspinClient.SetVisualizerConfigurationAsync"/>, which reconfigures the
    /// client's own copy of this object rather than writing back here.
    /// </remarks>
    public VisualizerRoleSupport? VisualizerRoleSupport { get; set; }

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
    /// Default (<see cref="PlayerBufferCapacity.DefaultMinBufferMilliseconds"/>, 150 ms) is a
    /// conservative LAN starting point. Tune per network: larger for remote or high-latency
    /// links, smaller for stable LAN. Do NOT include <c>static_delay_ms</c> here.
    /// </para>
    /// <para>
    /// The SDK forwards this to <see cref="IAudioPipeline.SetMinBufferMilliseconds"/>, which
    /// bounds the buffer's readiness gate: a live stream will never hold more than this before
    /// its scheduled start, so a client advertising a larger value must also wait for it.
    /// </para>
    /// </summary>
    public int MinBufferMs { get; set; } = PlayerBufferCapacity.DefaultMinBufferMilliseconds;

    /// <summary>
    /// Whether this client accepts the server's output-delay command. When true, the client
    /// advertises 'set_static_delay' in the client/state player object and applies inbound
    /// commands to its output delay — both that spelling and the <c>set_output_delay</c> one
    /// spec 168a677 renamed it to. Default is true.
    /// </summary>
    public bool SupportsSetOutputDelay { get; set; } = true;

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
    /// The pairing-code method this client offers in addition to the mandatory Pairing PSK
    /// method, in the encrypted protocol. Empty by default (Pairing PSK only).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A client may offer <b>at most one</b> pairing-code method (spec #189): either
    /// <see cref="PairMethods.DynamicPairingCode"/> or
    /// <see cref="PairMethods.StaticPairingCode"/>, never both. Listing both is a
    /// configuration error and is rejected when the client is constructed rather than
    /// silently resolved — which of two deliberately configured methods the app meant is not
    /// the SDK's to guess, and the two have very different security properties.
    /// </para>
    /// <para>
    /// Dynamic pairing code requires <see cref="SendspinClientOptions.PresentPairingCodeAsync"/>;
    /// without it the method is withheld.
    /// </para>
    /// </remarks>
    public List<string> PairingCodeMethods { get; set; } = new();

    /// <summary>
    /// Out-channels through which the dynamic pairing code is conveyed to the operator
    /// (informational hint: "display" or "speaker"). Default ["display"].
    /// </summary>
    /// <remarks>
    /// <para>
    /// "other" was a third permitted value until spec commit <c>3f8528a9</c> removed it. The
    /// hint is informational and never grounds for <c>pair/abort</c>, so an out-of-vocabulary
    /// entry does not break pairing — but it is no longer a value the spec defines.
    /// </para>
    /// <para>
    /// <c>"speaker"</c> is dropped from the advertised descriptor: the spec requires a speaker
    /// client to also advertise a <c>digit_audio</c> object and to consume the server's digit
    /// audio pack, which this SDK does not implement. Advertising it would invite a server to
    /// pick a flow the client cannot run.
    /// </para>
    /// </remarks>
    public List<string> PairingCodeOutChannels { get; set; } = new() { "display" };

    /// <summary>
    /// For static pairing code: the device-specific fixed pairing code (8 digits). Required if
    /// <see cref="PairMethods.StaticPairingCode"/> is offered.
    /// </summary>
    public string? StaticPairingCode { get; set; }

    /// <summary>
    /// Where an operator can find this device's static pairing code, advertised as the <c>locations</c>
    /// hint on the <c>static_pairing_code</c> pair-method descriptor. Values are <c>"device"</c>
    /// (printed on it), <c>"leaflet"</c> (in the box), and <c>"operator"</c> (they set it).
    /// Empty by default, which omits the hint — the SDK cannot know where your secret is
    /// printed, and a wrong hint is worse than none.
    /// </summary>
    /// <remarks>
    /// Purely informational: it drives server UX copy like "check the label on the device",
    /// and no pairing decision depends on it. Pairing configuration is local and
    /// manufacturer-defined, so nothing on the wire rewrites this hint (#129).
    /// </remarks>
    public List<string> StaticPairingCodeLocations { get; set; } = new();

    /// <summary>
    /// Where an operator can find this device's Pairing PSK, advertised as the
    /// <c>locations</c> hint on the <c>pairing_psk</c> descriptor. Same vocabulary as
    /// <see cref="StaticPairingCodeLocations"/>; empty by default.
    /// </summary>
    public List<string> PairingPskLocations { get; set; } = new();

    /// <summary>
    /// Whether the mandatory Pairing PSK method starts enabled. Default true.
    /// </summary>
    /// <remarks>
    /// <see cref="ISendspinClient.EnsurePairingPsk"/> and
    /// <see cref="ISendspinClient.RotatePairingPsk"/> do not check this flag: an app that
    /// sets it false and still calls either to render a pairing token (a QR code, say) hands
    /// the operator a token whose method the client will refuse, with no error raised.
    /// </remarks>
    public bool PairingPskEnabled { get; set; } = true;

    /// <summary>
    /// Whether <see cref="PairMethods.DynamicPairingCode"/> starts enabled, when it is listed
    /// in <see cref="PairingCodeMethods"/>. Default true, so listing the method is enough to
    /// offer it. Ignored when the method is not listed.
    /// </summary>
    /// <remarks>
    /// Distinct from removing the method from <see cref="PairingCodeMethods"/>, which means
    /// <em>not implemented</em>: an unimplemented method is never advertised at all, while a
    /// disabled-but-implemented one can be turned back on by reconfiguring the app.
    /// </remarks>
    public bool DynamicPairingCodeEnabled { get; set; } = true;

    /// <summary>
    /// Whether <see cref="PairMethods.StaticPairingCode"/> starts enabled, when it is listed
    /// in <see cref="PairingCodeMethods"/>. Default true. See
    /// <see cref="DynamicPairingCodeEnabled"/> for why this is not the same as omitting the method.
    /// </summary>
    public bool StaticPairingCodeEnabled { get; set; } = true;

    /// <summary>
    /// Rejects a <see cref="PairingCodeMethods"/> list that offers both pairing-code methods.
    /// </summary>
    /// <remarks>
    /// The spec lets a client implement at most one of them, and requires a server that
    /// nevertheless sees both to disregard <c>static_pairing_code</c>. That server-side
    /// tolerance is a safety net for a non-conformant peer, not a licence to emit one: an app
    /// that configured both said two contradictory things, and picking one for it would ship
    /// whichever the SDK guessed. Nothing on the wire can reach this — it is local
    /// configuration, checked once, where the app can still fix it.
    /// </remarks>
    /// <exception cref="ArgumentException">Both pairing-code methods are listed.</exception>
    internal void ValidatePairingCodeMethods()
    {
        bool dynamicListed = PairingCodeMethods.Contains(PairMethods.DynamicPairingCode, StringComparer.Ordinal);
        bool staticListed = PairingCodeMethods.Contains(PairMethods.StaticPairingCode, StringComparer.Ordinal);
        if (dynamicListed && staticListed)
        {
            throw new ArgumentException(
                $"ClientCapabilities.PairingCodeMethods lists both '{PairMethods.DynamicPairingCode}' "
                + $"and '{PairMethods.StaticPairingCode}'. A client may offer at most one "
                + "pairing-code method; remove one of them.",
                nameof(PairingCodeMethods));
        }
    }

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
