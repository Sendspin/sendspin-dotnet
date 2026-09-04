# Sendspin SDK

A cross-platform .NET SDK for the Sendspin synchronized multi-room audio protocol. Build players that sync perfectly with Music Assistant and other Sendspin-compatible players.

[![NuGet](https://img.shields.io/nuget/v/Sendspin.SDK.svg)](https://www.nuget.org/packages/Sendspin.SDK/)
[![GitHub](https://img.shields.io/github/license/Sendspin/sendspin-dotnet)](https://github.com/Sendspin/sendspin-dotnet/blob/main/LICENSE)

## Features

- **Multi-room Audio Sync**: Microsecond-precision clock synchronization using Kalman filtering
- **Sync Correction Built In**: `TimedAudioBuffer.Read()` applies the spec's full correction strategy — a conformant player writes no correction code
- **Platform Flexibility**: `ReadRaw()` hands the error out instead, for platforms with their own rate-control mechanism (hardware rate adjust, playback rate, an existing resampler)
- **Fast Startup**: Audio plays within ~300ms of connection
- **Protocol Support**: Full Sendspin WebSocket protocol implementation
- **Server Discovery**: mDNS-based automatic server discovery
- **Audio Decoding**: Built-in PCM, FLAC, and Opus codec support
- **Cross-Platform**: Works on Windows, Linux, and macOS (.NET 8.0 / .NET 10.0)
- **NativeAOT & Trimming**: Fully compatible with `PublishAot` and IL trimming for single-file native executables with no .NET runtime dependency
- **Audio Device Switching**: Hot-switch audio output devices without interrupting playback

## Installation

```bash
dotnet add package Sendspin.SDK
```

## Quick Start

Under the encrypted Sendspin protocol, a client's `client_id` **is** its Curve25519 public
key, so its identity must persist across restarts. Load it through an
`ISendspinIdentityStore` (see [Persisting the client identity](#persisting-the-client-identity)
below) so the SDK generates one on first run and reuses it on every run after that.

```csharp
using Microsoft.Extensions.Logging;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection.Noise;

// Generates and persists an identity on first run; loads the same one afterwards.
var identityStore = new FileSendspinIdentityStore(
    Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "MyApp", "identity.key"));
var identity = SendspinIdentity.FromStore(identityStore);

var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());

// CreateForDial wires the identity, wire framing, and Noise session together so they
// can't drift apart. `await using`, not `using`: the client owns the connection it built,
// and only DisposeAsync closes it — a synchronous dispose leaves the socket open and the
// server reconnecting to an app that has exited.
await using var client = SendspinClientService.CreateForDial(
    loggerFactory,
    new SendspinClientOptions
    {
        Identity = identity,
        Capabilities = new ClientCapabilities
        {
            ClientName = "My Player",
            ProductName = "My Awesome Player",
            Manufacturer = "My Company",
            SoftwareVersion = "1.0.0"
        }
    });

// Handle events
client.GroupStateChanged += (sender, group) =>
{
    Console.WriteLine($"Now playing: {group.Metadata?.Title}");
};

// Connect to server. A permanent handshake failure throws — see "Handling handshake
// failures" below.
await client.ConnectAsync(new Uri("ws://192.168.1.100:8927/sendspin"));

// Send commands
await client.SendCommandAsync("play");
await client.SetVolumeAsync(75);
```

### Playing audio

A player is three pieces: a clock synchronizer, an `IAudioPipeline` built around
`TimedAudioBuffer`, and an `IAudioPlayer` for your platform's output device (see
[Platform-Specific Audio](#platform-specific-audio)). Hand the pipeline to the client and the SDK
drives it from the protocol — `stream/start`, chunks, `stream/end`. Give the client and the
pipeline the **same** `IClockSynchronizer`, or the buffer schedules against a clock nothing is
updating.

```csharp
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;
using Sendspin.SDK.Synchronization;

var clockSync = new KalmanClockSynchronizer();

var pipeline = new AudioPipeline(
    loggerFactory.CreateLogger<AudioPipeline>(),
    new AudioDecoderFactory(loggerFactory),
    clockSync,
    bufferFactory: (format, sync) => new TimedAudioBuffer(format, sync),
    playerFactory: () => new MyAudioPlayer(),
    sourceFactory: (buffer, nowMicroseconds) => new MySampleSource(buffer, nowMicroseconds));

await using var client = SendspinClientService.CreateForDial(
    loggerFactory,
    new SendspinClientOptions
    {
        Identity = identity,
        ClockSynchronizer = clockSync,
        AudioPipeline = pipeline,
        Capabilities = new ClientCapabilities { ClientName = "My Player" },
    });
```

The pull loop is the whole player. `sourceFactory` hands you the buffer and a `Func<long>`
returning the current local time in microseconds; fill whatever block of floats your backend asks
for, straight from `Read`:

```csharp
public sealed class MySampleSource : IAudioSampleSource
{
    private readonly ITimedAudioBuffer _buffer;
    private readonly Func<long> _nowMicroseconds;

    public MySampleSource(ITimedAudioBuffer buffer, Func<long> nowMicroseconds)
        => (_buffer, _nowMicroseconds) = (buffer, nowMicroseconds);

    public AudioFormat Format => _buffer.Format;

    // Called on the audio thread — must not block or allocate.
    public int Read(float[] buffer, int offset, int count)
        => _buffer.Read(buffer.AsSpan(offset, count), _nowMicroseconds());
}
```

That is the complete correction story for most players. `Read` applies the spec's ladder itself —
nothing below a ~100 µs dead band, whole-frame drop/duplicate capped at ±0.5% up to 5 ms, a
one-shot snap above that, a re-anchor above 500 ms — and it always fills the span you give it,
padding with silence on an underrun, so the audio thread is never handed a short block. The
return value is how many of those samples were real audio.

On a desktop-class device, swap that source for `SyncCorrectedSampleSource` and you get the same
correction more smoothly. Instead of dropping and duplicating whole frames it trims playback speed
continuously through a built-in resampler, which is inaudible where frame stepping is faintly
granular. Nothing about the policy changes — the same dead band, the same ±0.5% cap, the same
one-shot snap, all still applied by the buffer — so it is a drop-in swap with no correction code
on your side, and it costs a few hundred microseconds of CPU per callback plus one small buffer:

```csharp
// Replaces MySampleSource entirely; the same (buffer, nowMicroseconds) the factory hands you.
sourceFactory: (buffer, nowMicroseconds) => new SyncCorrectedSampleSource(buffer, nowMicroseconds)
```

It fills every block, holding the last frame over a brief shortfall rather than punching a silent
hole in continuous audio, and reports its applied rate into `GetStats()` for you. It also
implements `IPlaybackLifecycleAware`, so the pipeline resets it on `stream/clear` and tells it to
stand down while the clock re-converges after a reconnect. Implement that interface on your own
source if it keeps correction state of its own; a source that only reads the buffer has nothing
to invalidate and should leave it alone. If your host cannot carry a resampler in its output
chain, set `SyncCorrectionOptions.Mechanism = SyncCorrectionMechanism.FrameStepping` and the
same class corrects by splicing frames instead, with no resampler constructed.

Reach for `ReadRaw` directly only if your platform owns a rate-control mechanism of its own that
the SDK cannot drive — ALSA hardware rate adjust, a browser's `playbackRate`, a resampler already
in your output chain. The thresholds and the ±0.5 % cap are spec constants either way, so there is
no policy to win back; see [Sync Correction System](#sync-correction-system) for that seam.

### Handling handshake failures

`ConnectAsync` **throws** `SendspinHandshakeException` when a handshake fails permanently, so
an application that never subscribes to `ConnectionStateChanged` still finds out. Without
handling it the call previously returned as though it had succeeded, and the problem surfaced
when the first command threw *"WebSocket is not connected"*.

`Kind` separates the two cases, and they call for different responses:

```csharp
try
{
    await client.ConnectAsync(serverUri);
}
catch (SendspinHandshakeException ex) when (ex.Kind == HandshakeFailureKind.LegacyServer)
{
    // The server predates the encrypted protocol. Upgrade it to aiosendspin >= 7.0.0, or
    // pin this SDK to the 9.x line. Retrying cannot help, and the SDK does not retry.
}
catch (SendspinHandshakeException ex)   // HandshakeRejected
{
    // The server speaks the encrypted protocol but refused this handshake: no usable
    // pairing record, an unsupported cipher suite, a version mismatch, or malformed input.
    // Pair again rather than retrying.
}
catch (TimeoutException)
{
    // The socket opened but the hello exchange did not finish inside the spec's 30 s
    // handshake window. Not permanent — retrying is reasonable.
}
```

A transport-level failure to reach the server at all (`WebSocketException` and friends)
propagates only when `ConnectionOptions.AutoReconnect` is false. With auto-reconnect enabled
the connection enters its reconnect loop and `ConnectAsync` returns.

### Persisting the client identity

`client_id` **is** the client's Curve25519 public key, and the spec requires it to survive
reboots — a client that regenerates its identity looks like a brand-new client to every
server it has paired with. Supply an `ISendspinIdentityStore` and let the SDK manage it:

```csharp
var options = new SendspinClientOptions
{
    Identity = SendspinIdentity.FromStore(
        new FileSendspinIdentityStore(
            Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "MyApp", "identity.key"))),
};
```

`FromStore` generates and persists an identity on first run, and loads the same one
afterwards. The blob is opaque — the SDK owns its format — so a platform store (DPAPI,
Keychain, Android keystore) only needs to protect bytes:

```csharp
public sealed class DpapiIdentityStore : ISendspinIdentityStore
{
    public byte[]? Load() => /* unprotect from your storage */;
    public void Save(byte[] identityBlob) => /* protect and store */;
}
```

**Security note.** The identity blob contains a private key, and `FilePairingRecordStore`
holds raw PSKs. All three file stores — identity, pairing records, pairing code lockout — are written
atomically and set to owner-only (`0600`) on Unix; a file left at looser permissions, whether by
an earlier SDK version or by however you provisioned it, is narrowed the first time this version
loads it. Where the process cannot chmod the file at all — owned by another uid on a bind mount,
or a mount that rejects chmod such as CIFS or exFAT — the store logs a warning and carries on
rather than refusing to start, so pass an `ILogger` if you want to hear about it. Windows has no
Unix file mode, so those files inherit their parent directory's
ACL — place them under `%LOCALAPPDATA%`, which is already user-scoped, or supply a platform
store.

If you enable the optional pairing code methods via `ClientCapabilities.PairingCodeMethods`,
you must also supply an `IPairingCodeLockoutStore` — `FilePairingCodeLockoutStore` is provided. Without one
the failure counter cannot survive a restart, so a method could never escalate to
gesture-gating; the SDK refuses to offer the pairing code methods rather than granting unlimited,
ungated attempts. They equally need an `IPairingRecordStore`: without one the pairing code exchange
completes, the server writes a long-term record, and the client stores nothing — so it fails
to authenticate on the next connection having reported success. Offering `dynamic_pairing_code`
additionally requires `SendspinClientOptions.PresentPairingCodeAsync` (the callback that shows the
derived pairing code to the operator, taking a `PairingCodePresentation` — the derived pairing code plus the server's
language hint); without it the SDK refuses that method with `method_not_supported` rather than
pairing with a pairing code nobody can see. Show `PairingCodePresentation.Groups` rather than `PairingCode` to display
the pairing code in the spec's recommended grouping (`123456` → `123 456`); grouping is presentation-only
and separators never enter derivation or operator entry.

**A `PairingWindow` is required to complete a gesture-gated attempt** — every `static_pairing_code`
attempt, and a `dynamic_pairing_code` attempt once the method has escalated. The window is device-level: construct one, share it across every connection (pass it
to `SendspinClientOptions.PairingWindow`, which `SendspinHostService` forwards to each
connection it accepts), and `Open()` it from a deliberate operator gesture. Leaving it null is
the fail-closed default and does not fail loudly: the client answers with `client/pair-pending`
and waits forever, so pairing simply never completes. Subscribe to
`ISendspinClient.PairingGestureRequested` to prompt the operator. See
[MIGRATION-10.0.0.md](MIGRATION-10.0.0.md#a-pairingwindow-is-required-for-the-gesture-gated-methods)
for the full migration note.

**Pairing configuration is local.** `ClientCapabilities` is the whole of it: which pair
methods the client offers, the static pairing code's value, unpaired access, and the two
`locations` hints below. A client offers **at most one** pairing-code method — listing both
`dynamic_pairing_code` and `static_pairing_code` throws at construction rather than silently
picking one for you. No server can read or change any of it —
pairing config and the pairing window are manufacturer-defined, so the values you construct the
client with are the values it advertises and enforces for the life of the process.

Note that `DynamicPairingCodeEnabled`/`StaticPairingCodeEnabled` are not the same as listing the method in
`PairingCodeMethods`. That list means *implemented*: a method omitted from it is reported to
the server as absent, whereas a listed-but-disabled method is simply withheld from
`client/hello` until your app turns it back on.

### Telling servers where the operator can find a secret

`StaticPairingCodeLocations` and `PairingPskLocations` advertise the spec's `locations` hint on those
two pair-method descriptors: where an operator should look for the configured secret —
`"device"` (printed on it), `"leaflet"` (in the box), or `"operator"` (they set it themselves).
It drives server UX copy such as *"check the label on the device"*, and nothing about pairing
depends on it.

```csharp
var caps = new ClientCapabilities
{
    PairingCodeMethods = { "static_pairing_code" },
    StaticPairingCode = "12345678",
    StaticPairingCodeLocations = { PairMethodLocations.Device },
};
```

Both default to empty, which omits the hint entirely — the SDK cannot know where your secret
is printed, and a wrong hint is worse than none. Set `["operator"]` yourself when the operator
chooses the secret through your own UI, since any printed copy is then stale. A Pairing PSK
your own client mints (`EnsurePairingPsk`, `RotatePairingPsk`) does **not** change the hint:
the client generated that one, so it is still found wherever your app renders it.

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                     Your Application                            │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  IAudioSampleSource        │  Optional: your own        │   │
│  │  (pull loop calls Read)    │  corrector via ReadRaw     │   │
│  └─────────────────────────────────────────────────────────┘   │
├─────────────────────────────────────────────────────────────────┤
│  SendspinClientService    │  AudioPipeline    │  IAudioPlayer   │
│  (protocol handling)      │  (orchestration)  │  (your impl)    │
├─────────────────────────────────────────────────────────────────┤
│  SendspinConnection  │  KalmanClockSync  │  TimedAudioBuffer    │
│  (WebSocket)         │  (timing)         │  (corrects to spec)  │
├─────────────────────────────────────────────────────────────────┤
│  OpusDecoder  │  FlacDecoder  │  PcmDecoder                     │
└─────────────────────────────────────────────────────────────────┘
```

**Namespaces:**
- `Sendspin.SDK.Client` - Client services and capabilities
- `Sendspin.SDK.Connection` - WebSocket connection management
- `Sendspin.SDK.Protocol` - Message types and serialization
- `Sendspin.SDK.Synchronization` - Clock sync (Kalman filter)
- `Sendspin.SDK.Audio` - Pipeline, buffer, decoders, and sync correction
- `Sendspin.SDK.Discovery` - mDNS server discovery
- `Sendspin.SDK.Models` - Data models (GroupState, TrackMetadata)

## Sync Correction System

Correction is built in: `TimedAudioBuffer.Read()` implements the spec's full suggested strategy,
so the pull loop in [Playing audio](#playing-audio) is conformant on its own. Every threshold in
that strategy is a spec constant, so there is no correction *policy* left for an application to
choose — only the *mechanism*.

`ReadRaw()` is the seam for platforms whose mechanism is smoother than stepping whole frames:

- **Linux**: ALSA hardware rate adjustment, PipeWire rate
- **Browser**: Native `playbackRate` (WSOLA time-stretching)
- **Windows**: a WDL or SoundTouch resampler already in the output chain
- **Embedded**: Platform-specific DSP

### How It Works

```
Default: the SDK corrects              Advanced: you correct
────────────────────────────────────────────────────────────────
TimedAudioBuffer.Read()                TimedAudioBuffer.ReadRaw()
├─ dead band → drop/insert → snap      ├─ SyncErrorMicroseconds
├─ bounded by the ±0.5% cap            ├─ SmoothedSyncErrorMicroseconds
└─ no correction code in your app      └─ NotifyExternalCorrection()
                                           ↓
                                       SyncCorrectionCalculator
                                       ├─ UpdateFromSyncError()
                                       └─ TargetPlaybackRate  ← the only currency
```

### Tiered Correction Strategy

Both read paths follow the same ladder — they differ only in who applies the continuous tier:

| Sync Error | Correction | Applied by |
|------------|------------|------------|
| < 100 µs | None (dead band) | — |
| 100 µs – 5 ms | Continuous, bounded by the ±0.5% cap | `Read`: whole-frame drop/duplicate. `SyncCorrectedSampleSource`: a resampler trimming playback speed. `ReadRaw`: your own mechanism, from `ISyncCorrectionProvider` |
| 5 ms – 500 ms | One-shot hard sync (single discontinuity) | `TimedAudioBuffer`, on **both** paths |
| > 500 ms | Re-anchor (clear buffer, restart sync) | `TimedAudioBuffer`, on **both** paths |

`ResamplingThresholdMicroseconds` (100 ms) marks where an error stops being worth trimming
smoothly, so with the default 5 ms hard-sync threshold below it that mark is never reached — it
applies only if you lower the hard-sync threshold or disable that tier.

A correction is always expressed as a **playback rate**, in every tier. That is the single
currency between a provider and whoever applies it: a provider cannot see whether its caller has
a resampler, so it never chooses the mechanism. A caller without one realizes the same speed as
one dropped or inserted frame every `1 / |rate - 1|` frames — one frame in N is a speed change of
1/N — which is exactly what `TimedAudioBuffer.Read` and `SyncCorrectedSampleSource` under
`FrameStepping` do.

### Advanced: correcting externally with `ReadRaw`

Only for platforms with their own rate-control mechanism. The buffer still performs the one-shot
snap itself, because skipping buffered content is something only it can do — stand down while
`ITimedAudioBuffer.IsHardSyncPending` is true, rather than while a provider reports
`SyncCorrectionMode.HardSync`. The mode is a forecast from the smoothed error; the flag is the
buffer actually doing it, and the two disagree in both directions.

If what you want is smooth resampling rather than a mechanism peculiar to your platform, use
`SyncCorrectedSampleSource` (see [Playing audio](#playing-audio)) instead of writing the loop
below. It is this composition, already assembled and tested, and it carries the fixes for two
artefacts that are easy to reintroduce: bypassing the resampler when the rate returns to 1.0
(which strands the resampler's buffered input and clicks on re-entry), and padding a mid-callback
shortfall with silence (a bit-exact zero in continuous audio is a broadband click, not a gap).

```csharp
using Sendspin.SDK.Audio;

// Create the correction calculator
var correctionProvider = new SyncCorrectionCalculator(
    SyncCorrectionOptions.Default,  // or SyncCorrectionOptions.CliDefaults
    sampleRate: 48000,
    channels: 2
);

// Subscribe to correction changes
correctionProvider.CorrectionChanged += provider =>
{
    // One currency: a speed. Apply it however your platform can.
    myResampler.Rate = provider.TargetPlaybackRate;

    // With no resampler, the same speed is one frame every N:
    //   var deviation = provider.TargetPlaybackRate - 1.0;
    //   var everyN = (int)Math.Ceiling(1.0 / Math.Abs(deviation));   // drop if > 0, insert if < 0
};

// In your IAudioSampleSource, in place of the Read() call from "Playing audio":
public int Read(float[] buffer, int offset, int count)
{
    var span = buffer.AsSpan(offset, count);

    // Read without the continuous correction
    int read = _buffer.ReadRaw(span, _nowMicroseconds());

    // Update correction provider with current error
    correctionProvider.UpdateFromSyncError(
        _buffer.SyncErrorMicroseconds,
        _buffer.SmoothedSyncErrorMicroseconds);

    // Apply your correction strategy to `span`, then report what you did.
    // Drop or insert, never both in the same cycle. This feeds the stats only — ReadRaw has
    // already credited every sample it handed you, so size the read to the correction rather
    // than expecting this call to account for it:
    _buffer.NotifyExternalCorrection(samplesDropped, samplesInserted);
    _buffer.ReportExternalPlaybackRate(correctionProvider.TargetPlaybackRate);

    return read;
}
```

`ReportExternalPlaybackRate` is what puts your applied rate into `GetStats()`; report every
change, including the reset back to 1.0, or the stats latch on the last value you sent.

### Configuring Sync Behavior

The correction ladder follows the spec: nothing below a ~100 µs dead band, a continuous
correction capped at ±0.5% above it, and a single one-shot snap above ~5 ms — which the spec
exempts from the speed cap, and which the buffer applies itself on both read paths. A
`MaxSpeedCorrection` above the cap is clamped where it is applied, with a warning, rather than
rejected.

Which read path you use decides who applies the continuous tier. `Read` — the default — corrects
end to end, realizing that tier as capped frame drop/duplicate and holding `TargetPlaybackRate`
at 1.0; do not also drive a resampler from that rate, or the same error is corrected twice.
`ReadRaw` hands you the error instead, for correction through an `ISyncCorrectionProvider`.

```csharp
// Spec-conformant defaults (0.5% cap, 100µs dead band, 3s target)
var options = SyncCorrectionOptions.Default;

// CLI-compatible settings (same caps, faster convergence: 2s target, 15ms resampling band)
var options = SyncCorrectionOptions.CliDefaults;

// Custom options
var options = new SyncCorrectionOptions
{
    CorrectionTargetSeconds = 2.0,                // Time to eliminate drift
    HardSyncThresholdMicroseconds = 5_000,        // One-shot snap above this
    ResamplingThresholdMicroseconds = 15_000,     // Resampling vs drop/insert
    ReanchorThresholdMicroseconds = 500_000,      // Clear buffer threshold
    StartupGracePeriodMicroseconds = 500_000,     // No correction during startup

    // How an external corrector realizes the continuous tier. SmoothResampling (the default)
    // trims playback speed; FrameStepping splices whole frames and constructs no resampler.
    // Read by SyncCorrectedSampleSource and SyncCorrectionCalculator; TimedAudioBuffer.Read
    // always steps frames regardless.
    Mechanism = SyncCorrectionMechanism.SmoothResampling,
};

var calculator = new SyncCorrectionCalculator(options, sampleRate, channels);
```

### Buffer capacity

`buffer_capacity` in `client/hello` is a hard byte limit the server may fill toward, so it must
be a figure your audio buffer can actually hold. Set the duration once and let the SDK derive
the advertisement from it and your advertised codecs:

```csharp
var capabilities = new ClientCapabilities { AudioBufferCapacityMs = 60_000 };

// ...and give TimedAudioBuffer the same number.
var buffer = new TimedAudioBuffer(format, clockSync, capabilities.AudioBufferCapacityMs);
```

## Platform-Specific Audio

The SDK handles decoding, buffering, and sync correction. You implement `IAudioPlayer` for audio output — it owns the device, not the audio data; the pull loop lives in your `IAudioSampleSource` (see [Playing audio](#playing-audio)):

```csharp
public class MyAudioPlayer : IAudioPlayer
{
    public int OutputLatencyMs { get; private set; }

    public Task InitializeAsync(AudioFormat format, CancellationToken ct = default)
    {
        // Initialize your audio backend (WASAPI, PulseAudio, CoreAudio, etc.)
    }

    public void SetSampleSource(IAudioSampleSource source)
    {
        // Keep it; pull from it on the audio thread once Play() is called
    }

    public void Play() { /* start the device */ }

    // ... State, Volume, IsMuted, Pause, Stop, SwitchDeviceAsync, StateChanged, ErrorOccurred
}
```

**Platform suggestions:**
- **Windows**: NAudio with WASAPI (`WasapiOut`)
- **Linux**: OpenAL, PulseAudio, or PipeWire
- **macOS**: AudioToolbox or AVAudioEngine
- **Cross-platform**: SDL2

## Server Discovery

Automatically discover Sendspin servers on your network:

```csharp
var discovery = new MdnsServerDiscovery(logger);
discovery.ServerDiscovered += (sender, server) =>
{
    Console.WriteLine($"Found: {server.Name} at {server.Uri}");
};
await discovery.StartAsync();
```

## Device Info

Identify your player to servers:

```csharp
var capabilities = new ClientCapabilities
{
    ClientName = "Living Room",              // Display name
    ProductName = "MySpeaker Pro",           // Product identifier
    Manufacturer = "Acme Audio",             // Your company
    SoftwareVersion = "2.1.0",               // App version
    MacAddress = "aa:bb:cc:dd:ee:ff"         // NIC MAC, lowercase colon-separated
};
```

All fields are optional and omitted from the protocol if null.

## Player Timing & Output Delay

Players report timing requirements so the server can schedule audio far enough ahead to avoid
buffer underruns and start-of-stream truncation (per the Sendspin spec's player timing
capabilities). These are advertised in every `client/state` message:

```csharp
var capabilities = new ClientCapabilities
{
    // Minimum startup lead time: codec init, decode warmup, backend buffering, DAC latency.
    // The server schedules the first chunk at least this far ahead after a stream start/restart.
    RequiredLeadTimeMs = 200,   // default: 200 ms (conservative LAN starting point)

    // Minimum ongoing buffer to absorb network jitter (primarily for live streams). The SDK
    // forwards this to the pipeline, so the buffer's readiness gate waits for the same depth
    // the server was asked to keep queued.
    MinBufferMs = 150,          // default: 150 ms

    // Whether to accept the server's set_static_delay command (advertised in client/state).
    SupportsSetOutputDelay = true,
};
```

Report the **lowest** values that reliably avoid truncation/underruns for your device and network —
larger for remote or high-latency links, smaller for stable LAN. Do **not** fold `static_delay_ms`
into these values; the server applies output delay separately. For empirical tuning, the audio
pipeline exposes measured latency (e.g. `AudioPipeline.DetectedOutputLatencyMs`).

If conditions change at runtime (e.g. a link-type change, or a measured lead time after warmup),
update the values and the SDK re-reports `client/state`:

```csharp
await client.UpdateTimingAsync(requiredLeadTimeMs: 120, minBufferMs: 80);
```

Debounce these updates yourself — report only sustained changes, not transient fluctuations.

### Persisting output delay across restarts

`static_delay_ms` compensates for hardware delay beyond the audio port (external speakers,
amplifiers) and must persist across reboots and reconnections. Because the SDK is a library and
cannot choose where to store it, implement `IOutputDelayStore` and pass it to the client. The SDK
loads on connect (before the first `client/state`) and saves whenever the delay changes (via a
`set_static_delay` command or a GroupSync offset):

To change the delay from the app (a calibration measurement, or a new audio output), pass it to
`SendPlayerStateAsync(volume, muted, outputDelayMs)` — that applies it, persists it through the
store, and reports it. Leave the argument off for ordinary volume and mute changes: spec PR #175
removed merging, so every `client/state` carries the **full** state of each role object it
includes and the SDK rebuilds the player object from its current values on every send. Nothing
you omit from a call is dropped from the message.

A server changes it with the `set_static_delay` command, which the SDK advertises in
`client/state`'s player `supported_commands` (never in `client/hello` — the spec restricts
`player@v1_support.supported_commands` to `volume` and `mute`, so `client/state` is the only
place any conformant client can offer it). Set `ClientCapabilities.SupportsSetOutputDelay = false`
to decline it.

> `IClockSynchronizer.OutputDelayMs` is a `double` over −5000…5000: fractional values come from
> calibration, and negative values schedule audio *later*. The spec's wire field is an integer
> 0–5000 and states negatives are unsupported, so what the client **reports** is rounded and
> clamped into that range while playback keeps using the value you set. The SDK logs a warning
> naming both numbers when they diverge — at that point the server's group calibration is working
> from a different delay than your playback is.

```csharp
public sealed class FileOutputDelayStore : IOutputDelayStore
{
    // Use InvariantCulture so the value round-trips regardless of the host's locale.
    public double? Load() => File.Exists(path)
        ? double.Parse(File.ReadAllText(path), CultureInfo.InvariantCulture)
        : null;

    public void Save(double outputDelayMs)
        => File.WriteAllText(path, outputDelayMs.ToString(CultureInfo.InvariantCulture));
}

await using var client = SendspinClientService.CreateForDial(
    loggerFactory,
    new SendspinClientOptions
    {
        Identity = identity,          // the persisted identity from Quick Start
        AudioPipeline = pipeline,
        OutputDelayStore = new FileOutputDelayStore(),
    });
```

When no store is supplied, behavior is unchanged: the embedder re-supplies the delay on each connect.

## Player format preference

Spec PR #195 removed `stream/request-format` from the protocol. A player that wants the server to
pick a particular format now reports it in the `format` field of the `client/state` player object,
which must match one of the entries the client advertised in `player@v1_support.supported_formats`:

```csharp
// Ask for a specific format; the server replies with a new stream/start.
await client.SetPlayerFormatPreferenceAsync(new AudioFormat
{
    Codec = "flac", Channels = 2, SampleRate = 48000, BitDepth = 24,
});

// Withdraw the preference and let the server choose:
await client.SetPlayerFormatPreferenceAsync(null);
```

All four fields are required by the spec, so a preference is reported only when it matches a
supported format; otherwise the call throws rather than putting an unusable object on the wire.

## Client state is always full state

Spec PR #175 removed server-side merging of `client/state`. Every message the SDK sends carries
`available` plus the **complete** state of each role object it includes, and it includes an object
for every currently active role that defines one. Omitting a role object means "this role is
unchanged", never "clear these fields" — so the SDK never sends a partial object. All state sends
(initial state, availability changes, player/source updates, command acknowledgements, artwork and
visualizer reconfiguration, role reactivation and the post-pairing resend) go through one
construction path, so the shape cannot drift between them.

Two consequences worth knowing:

- A client whose `active_roles` are non-empty still sends its initial `client/state` even when
  none of its roles defines a state object (spec PR #181) — `available` alone is what opens the
  server's streams for it.
- A role's binary data is only in play once the server has received that role's state object
  (spec PR #204). When `server/activate` adds a role mid-connection, the SDK reannounces the full
  state — now carrying the new role's object — before that role's streams can be used.

## Multi-server arbitration & last-played persistence

When multiple servers can reach a player (server-initiated mode via `SendspinHostService`), the host
arbitrates which one is active. It completes each server's `client/hello` ↔ `server/hello` handshake
first, then applies the spec's decision:

- connections rank by priority class, from the highest-priority activity declared in the
  connection's `server/activate`: `playback` > `pairing` > empty (no
  recognized activity declared);
- the incoming server is accepted when its priority is **higher than or equal to** the holder's,
  with two exceptions: a pairing attempt is never displaced by incoming playback/pairing, and an
  empty-vs-empty tie admits the incoming server only when it is the persisted **last-playback**
  server;
- a displaced holder is sent `client/goodbye` reason `another_server`; a rejected incoming server
  gets `concurrent_attempt`; a same-server reconnect drops the stale socket with `user_request`.

So the last-playback tie-break survives restarts, implement `ILastPlayedServerStore` (the host loads it
once at construction and saves whenever a server becomes the last-played one):

```csharp
public sealed class FileLastPlayedServerStore : ILastPlayedServerStore
{
    public string? Load() => File.Exists(path) ? File.ReadAllText(path) : null;
    public void Save(string serverId) => File.WriteAllText(path, serverId);
}

await using var host = new SendspinHostService(
    loggerFactory,
    new SendspinClientOptions
    {
        // The spec requires client_id to survive reboots, so the host loads its identity
        // from a store — see "Persisting the client identity" above.
        Identity = SendspinIdentity.FromStore(new FileSendspinIdentityStore(identityPath)),
    },
    lastPlayedServerStore: new FileLastPlayedServerStore());
```

The store is optional and best-effort: a throwing implementation is logged and never breaks
arbitration. The existing `LastPlayedServerIdChanged` event and `lastPlayedServerId` seed parameter
continue to work (the seed wins over the store when both are supplied).

## Artwork

Artwork clients support **1–4 independent channels** (e.g. album art on one display, artist photos on another). Each channel has its own source, format, and maximum size. Configure them in capabilities:

```csharp
var capabilities = new ClientCapabilities
{
    Roles = { "artwork@v1" },
    ArtworkChannels = new()
    {
        new() { Source = ArtworkSources.Album,  Format = "jpeg", Width = 512, Height = 512 }, // channel 0
        new() { Source = ArtworkSources.Artist, Format = "png",  Width = 256, Height = 256 }, // channel 1
    }
};
```

The channels are reported in the `artwork` object of `client/state` — spec PR #195 removed `artwork@v1_support` from `client/hello` and `stream/request-format` from the wire entirely, so the state object is the only place the channel configuration lives. The list is positional from channel 0, holds 1–4 entries, and any channel you do not cover is reported as `ArtworkSources.None`.

Images arrive per channel, with the display timestamp and channel number:

```csharp
client.ArtworkReceived += (_, e) =>
{
    // e.Channel (0-3), e.Timestamp (server clock, microseconds), e.ImageData (jpeg/png/bmp bytes)
    displays[e.Channel].Show(e.ImageData);
};

client.ArtworkCleared += (_, e) => displays[e.Channel].Clear(); // empty binary message = clear that channel
```

Change or disable a channel at runtime without reconnecting. The SDK updates that connection's own channel configuration — `ClientCapabilities` is yours and is left untouched, so a host sharing one across connections keeps them independent — and resends the whole `client/state` (the server replies with a new `stream/start`):

```csharp
// Switch channel 1 to artist art at a new size:
await client.SetArtworkChannelAsync(channel: 1, source: ArtworkSources.Artist, width: 400, height: 400);

// Disable channel 1 (server stops sending it); re-enable later by setting a real source again:
await client.SetArtworkChannelAsync(channel: 1, source: ArtworkSources.None);
```

> The server MUST NOT send a role's binary data until it has received that role's `client/state` object (spec PR #204). The SDK enforces the same gate on the receive side, so artwork frames that arrive before the `artwork` object has gone out are dropped with a single warning per role.

## Color

Clients with the `color` role receive a palette derived from the current audio — useful for ambient lighting, screen backgrounds, or UI theming. Colors arrive via `server/state` and are merged onto `GroupState.Colors`; subscribe to `ColorChanged` to react:

```csharp
client.ColorChanged += (_, palette) =>
{
    // RgbColor? per role; null until the server provides it (or after it clears it).
    if (palette.BackgroundDark is { } bg) lights.SetBackground(bg.R, bg.G, bg.B);
    if (palette.Primary is { } primary) ui.Accent = primary;
};
```

Available colors: `BackgroundDark`, `BackgroundLight`, `Primary`, `Accent`, `OnDark`, `OnLight`, plus a `Timestamp` (server clock, µs). The server guarantees WCAG 4.5:1 contrast ratios between the background/on-color pairs — clients use the values directly and do no contrast math.

Updates are deltas: a color absent from an update is left unchanged, an explicit `null` clears it, and a value updates it. The role is enabled by default (`color@v1` in `ClientCapabilities.Roles`); remove it to opt out.

## Visualizer

Clients with the `visualizer@v1` role receive real-time audio features for music visualization. Five feature types are available: **`loudness`**, **`f_peak`** (dominant frequency + amplitude), **`spectrum`** (display-binned FFT), **`beat`**, and **`peak`** (energy onsets). The role is **opt-in** — set `VisualizerRoleSupport` *and* add `visualizer@v1` to `Roles`:

```csharp
var capabilities = new ClientCapabilities
{
    Roles = { "player@v1", "visualizer@v1" },
    VisualizerRoleSupport = new VisualizerRoleSupport
    {
        BufferCapacity = 65536,           // client/hello: visualizer@v1_support
        RateMax = 30,                     // client/state: max frames/sec
        Types = new() { VisualizerTypes.Loudness, VisualizerTypes.Spectrum, VisualizerTypes.Beat },
        // Required when Spectrum is requested:
        Spectrum = new VisualizerSpectrum { NDispBins = 32, Scale = "log", FMin = 20, FMax = 16000 },
    },
};
```

`buffer_capacity` is the only field left in the `visualizer@v1_support` object of `client/hello`; `types`, `rate_max` and `spectrum` are reported in the `visualizer` object of `client/state` (spec PR #195).

Each binary message carries one feature type; subscribe to `VisualizationReceived` and read the populated field:

```csharp
client.VisualizationReceived += (_, frame) =>
{
    if (frame.Loudness is { } loud)      meter.Level = loud / 65535.0;
    if (frame.Spectrum is { } bins)      bars.Update(bins);          // NDispBins values
    if (frame.IsDownbeat is { } down)    pulse.Beat(strong: down);
    if (frame.PeakStrength is { } onset) flash.Pulse(onset / 255.0);      // 0-255 energy onset
};
```

`Spectrum` frames are validated against the negotiated `NDispBins` from the latest `stream/start`; malformed frames are dropped (no event). Reconfigure at runtime with `SetVisualizerConfigurationAsync(types, rateMax, spectrum)`, which updates that connection's own visualizer configuration (not the `ClientCapabilities` you supplied) and resends the full `client/state`.

> **Note:** `visualizer@v1` follows the [aiosendspin](https://github.com/Sendspin/aiosendspin) reference implementation, which is ahead of the formal protocol spec. The wire format may still evolve. The role degrades gracefully while it matures: it is **opt-in** (off by default), frames that don't match the negotiated/expected format are **dropped** (logged at `Trace`) rather than throwing, and a misbehaving `VisualizationReceived` handler is isolated so it can't disrupt audio or artwork.

## Stream teardown

`stream/end` ends a stream and `stream/clear` flushes its buffers (a seek or track jump). Both may target specific roles, and the SDK drives the audio pipeline only when the message reaches the `player` role — an end or clear aimed at `artwork` or `visualizer` leaves playback untouched. Role-targeted teardown is routine: dropping a stream role from `active_roles` makes the server end that role's output first.

Roles the SDK does not own are reported so the surface that owns them can react:

```csharp
client.StreamEndReceived += (_, payload) =>
{
    // payload.Roles == null means every active stream ended.
    if (payload.Roles is null || payload.Roles.Contains("artwork")) displays.ClearAll();
};

client.StreamClearReceived += (_, payload) => { /* same shape; a seek or track jump */ };
```

Application-specific roles (names starting with `_`) are passed through untouched. Both events are also forwarded by `SendspinHostService`.

## Source role (line-in)

A client with the `source@v1` role captures audio from a local input (AUX/line-in,
turntable preamp, microphone, loopback) and streams it to the server, which mixes and
distributes it to players. Provide an `IAudioCaptureDevice` (your platform's capture
implementation) and add `source@v1` to `Roles`:

```csharp
var caps = new ClientCapabilities
{
    Roles = { "player@v1", "source@v1" },   // a device can be both
    SourceRoleSupport = new SourceRoleSupport { LineSense = true },   // optional: report signal presence
};

await using var client = SendspinClientService.CreateForDial(
    loggerFactory,
    new SendspinClientOptions
    {
        Identity = identity,                 // the persisted identity from Quick Start
        Capabilities = caps,
        CaptureDevice = myCaptureDevice,     // IAudioCaptureDevice
    });
```

Streaming is **server-driven**: the source never streams until the server sends
`server/command { source: { command: "start" } }`. On start the SDK sends
`client_stream/start` (announcing the capture format), then encodes each captured
buffer and streams it as a binary type-12 chunk timestamped in the **server** time
domain (local capture time mapped through the clock filter's offset+drift). On `stop`,
role deactivation, or disposal it sends `client_stream/end` and stops capturing.

**Trust required.** A source streams potentially sensitive audio, so `source@v1` MUST
run on a paired (`user`-trust) connection. The SDK enforces this in two places, because
one is not enough: a `server/activate` that activates the role at trust `none` is
refused and the connection is closed, per spec — and, independently, the capture device
is never opened unless the connection is at trust `user` *and* the source role is
currently in `active_roles`. The second check is what stops a `server/command
{ source: { command: "start" } }` that skips activation entirely.

**Encoders.** PCM is built in (and always accepted by servers). Supply a custom
`ISourceAudioEncoderFactory` for Opus/FLAC. The encoder is created from the capture
device's own format by default; set `SourceRoleSupport.Codec` to encode as something else
(e.g. a PCM capture device streaming as Opus). A device implementing both `source` and
`player` never plays its own captured input locally — it outputs only what the server
distributes, staying in sync with the group.

**Line sensing.** When `SourceRoleSupport.LineSense` is set, call
`SetSourceSignalAsync(present)` to report `signal: present|absent` in `client/state`; the
server may use it as a hint for when to start/stop.

## NativeAOT Support

Since v7.0.0, the SDK is fully compatible with [NativeAOT deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/) and IL trimming. This means you can publish your Sendspin player as a single native executable with no .NET runtime dependency — ideal for embedded devices, containers, or minimal Linux installations.

```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
</PropertyGroup>
```

```bash
dotnet publish -c Release -r linux-x64
# Produces a single native binary (~15-25MB depending on dependencies)
```

**How it works**: The SDK uses source-generated `System.Text.Json` serialization (no runtime reflection) and built-in .NET WebSocket APIs. All public types are annotated with `IsAotCompatible` and `IsTrimmable` to ensure the .NET build analyzers catch any regressions.

**Your code**: If your `IAudioPlayer` implementation also avoids reflection, the entire stack will be AOT-safe. Most audio libraries (SDL2, OpenAL, PipeWire bindings) work fine with NativeAOT.

## Migration Guide

### Upgrading to v7.0.0

**Breaking change**: `SendspinListener.ServerConnected` event parameter type changed.

```csharp
// Before (v6.x):
listener.ServerConnected += (sender, fleckConnection) => { /* Fleck.IWebSocketConnection */ };

// After (v7.0+):
listener.ServerConnected += (sender, wsConnection) => { /* WebSocketClientConnection */ };
```

No changes needed if you only use `SendspinHostService` or `SendspinClientService` (most consumers).

### Upgrading to v5.0.0

**Breaking change**: Sync correction is now external. The SDK reports error; you apply correction.

> **Superseded in v10.0.0.** `Read` is no longer deprecated — it applies the spec's full strategy
> itself and is the default again. The external path below still works and is still supported; it
> is now the advanced seam described in [Sync Correction System](#sync-correction-system).

**Before (v4.x and earlier):**
```csharp
// SDK applied correction internally
var read = buffer.Read(samples, currentTime);
buffer.TargetPlaybackRateChanged += rate => resampler.Rate = rate;
```

**After (v5.0+):**
```csharp
// Create correction provider
var correctionProvider = new SyncCorrectionCalculator(
    SyncCorrectionOptions.Default, sampleRate, channels);

// Read raw samples (no internal correction)
var read = buffer.ReadRaw(samples.AsSpan(offset, count), currentTime);

// Update and apply correction externally
correctionProvider.UpdateFromSyncError(
    buffer.SyncErrorMicroseconds,
    buffer.SmoothedSyncErrorMicroseconds);

// Subscribe to rate changes
correctionProvider.CorrectionChanged += p => resampler.Rate = p.TargetPlaybackRate;

// Notify buffer of any drops/inserts for accurate tracking
buffer.NotifyExternalCorrection(droppedCount, insertedCount);
```

**Benefits:**
- Browser apps can use native `playbackRate` (WSOLA)
- Windows apps can choose WDL resampler, SoundTouch, or drop/insert
- Linux apps can use ALSA hardware rate adjustment
- Testability: correction logic is isolated

### Upgrading to v3.0.0

**Breaking change**: `IClockSynchronizer` requires `HasMinimalSync` property.

```csharp
// Add to custom IClockSynchronizer implementations:
public bool HasMinimalSync => MeasurementCount >= 2;
```

### Upgrading to v2.0.0

1. **`HardwareLatencyMs` removed** - No action needed, latency handled automatically
2. **`IAudioPipeline.SwitchDeviceAsync()` required** - Implement for device switching
3. **`IAudioPlayer.SwitchDeviceAsync()` required** - Implement in your audio player

## Example Projects

See the [Windows client](https://github.com/chrisuthe/windowsSpin/tree/master/src/SendspinClient) for a complete WPF implementation using NAudio/WASAPI with external sync correction.

## License

MIT License - see [LICENSE](https://github.com/Sendspin/sendspin-dotnet/blob/main/LICENSE) for details.
