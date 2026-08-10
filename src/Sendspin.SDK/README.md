# Sendspin SDK

A cross-platform .NET SDK for the Sendspin synchronized multi-room audio protocol. Build players that sync perfectly with Music Assistant and other Sendspin-compatible players.

[![NuGet](https://img.shields.io/nuget/v/Sendspin.SDK.svg)](https://www.nuget.org/packages/Sendspin.SDK/)
[![GitHub](https://img.shields.io/github/license/Sendspin/sendspin-dotnet)](https://github.com/Sendspin/sendspin-dotnet/blob/main/LICENSE)

## Features

- **Multi-room Audio Sync**: Microsecond-precision clock synchronization using Kalman filtering
- **External Sync Correction** (v5.0+): SDK reports sync error, your app applies correction
- **Platform Flexibility**: Use playback rate, drop/insert, or hardware rate adjustment
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
// can't drift apart.
var client = SendspinClientService.CreateForDial(
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

// Connect to server
await client.ConnectAsync(new Uri("ws://192.168.1.100:8927/sendspin"));

// Handle events
client.GroupStateChanged += (sender, group) =>
{
    Console.WriteLine($"Now playing: {group.Metadata?.Title}");
};

// Send commands
await client.SendCommandAsync("play");
await client.SetVolumeAsync(75);
```

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
holds raw PSKs. Both are written atomically and set to owner-only (`0600`) on Unix; a file
left at looser permissions by an earlier SDK version is narrowed the first time this version
loads it. Windows has no Unix file mode, so those files inherit their parent directory's
ACL — place them under `%LOCALAPPDATA%`, which is already user-scoped, or supply a platform
store.

If you enable the optional PIN pairing methods via `ClientCapabilities.PinPairingMethods`,
you must also supply an `IPinLockoutStore` — `FilePinLockoutStore` is provided. Without one
the failure counter cannot survive a restart, so a method could never escalate to
gesture-gating; the SDK refuses to offer the PIN methods rather than granting unlimited,
ungated attempts. Offering `dynamic_pin`
additionally requires `SendspinClientOptions.PresentPinAsync` (the callback that shows the
derived PIN to the operator, taking a `PinPresentation` — the derived PIN plus the server's
language hint); without it the SDK refuses that method with `method_not_supported` rather than
pairing with a PIN nobody can see.

**A `PairingWindow` is required to complete a gesture-gated attempt** — every `static_pin`
attempt, and a `dynamic_pin` attempt once the method has escalated or its PIN is shorter than
6 digits. The window is device-level: construct one, share it across every connection (pass it
to `SendspinClientOptions.PairingWindow`, which `SendspinHostService` forwards to each
connection it accepts), and `Open()` it from a deliberate operator gesture. Leaving it null is
the fail-closed default and does not fail loudly: the client answers with `client/pair-pending`
and waits forever, so pairing simply never completes. Subscribe to
`ISendspinClient.PairingGestureRequested` to prompt the operator. See
[MIGRATION-10.0.0.md](MIGRATION-10.0.0.md#a-pairingwindow-is-required-for-the-gesture-gated-methods)
for the full migration note.

**Runtime reconfiguration.** `ClientCapabilities` only seeds the client's *initial* pairing
config. Once paired, a management-activated server can enable, disable, and reconfigure each
pairing method at runtime via `management/set-pairing-config` — the Pairing PSK, dynamic PIN
(including its minimum length), static PIN (including its value), unpaired access, and the
record-mode fallback record. The SDK tracks this effective state itself and never writes it
back to your `ClientCapabilities` instance, so it lives in memory only: subscribe to
`ISendspinClient.PairingConfigChanged` to observe every change.

Only three of the six values the event reports have a `ClientCapabilities` property to seed
them back on the next startup — `UnpairedAccessEnabled`, `MinPinLength`, and `StaticPin`.
Reapply those three and that part of the server's change survives a restart. The other
three — whether Pairing PSK, dynamic PIN, or static PIN is *enabled*, and the record-mode
`psk_id` — have no `ClientCapabilities` counterpart today, so a server-side change to any of
them is always lost on restart no matter what your app persists.

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                     Your Application                            │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  SyncCorrectionCalculator  │  Your Resampler/Drop Logic │   │
│  │  (correction decisions)    │  (applies correction)      │   │
│  └─────────────────────────────────────────────────────────┘   │
├─────────────────────────────────────────────────────────────────┤
│  SendspinClientService    │  AudioPipeline    │  IAudioPlayer   │
│  (protocol handling)      │  (orchestration)  │  (your impl)    │
├─────────────────────────────────────────────────────────────────┤
│  SendspinConnection  │  KalmanClockSync  │  TimedAudioBuffer    │
│  (WebSocket)         │  (timing)         │  (reports error)     │
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

## Sync Correction System (v5.0+)

Starting with v5.0.0, sync correction is **external** - the SDK reports sync error and your application decides how to correct it. This enables platform-specific correction strategies:

- **Windows**: WDL resampler, SoundTouch, or drop/insert
- **Browser**: Native `playbackRate` (WSOLA time-stretching)
- **Linux**: ALSA hardware rate adjustment, PipeWire rate
- **Embedded**: Platform-specific DSP

### How It Works

```
SDK (reports error only)              App (applies correction)
────────────────────────────────────────────────────────────────
TimedAudioBuffer                      SyncCorrectionCalculator
├─ ReadRaw() - no correction          ├─ UpdateFromSyncError()
├─ SyncErrorMicroseconds              ├─ DropEveryNFrames
├─ SmoothedSyncErrorMicroseconds      ├─ InsertEveryNFrames
└─ NotifyExternalCorrection()         └─ TargetPlaybackRate
```

### Tiered Correction Strategy

The `SyncCorrectionCalculator` implements the same tiered strategy as the reference CLI:

| Sync Error | Correction Method | Description |
|------------|-------------------|-------------|
| < 1ms | None (deadband) | Error too small to matter |
| 1-15ms | Playback rate adjustment | Smooth resampling (imperceptible) |
| 15-500ms | Frame drop/insert | Faster correction for larger drift |
| > 500ms | Re-anchor | Clear buffer and restart sync |

### Usage Example

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
    // Update your resampler rate
    myResampler.Rate = provider.TargetPlaybackRate;

    // Or handle drop/insert
    if (provider.CurrentMode == SyncCorrectionMode.Dropping)
    {
        dropEveryN = provider.DropEveryNFrames;
    }
};

// In your audio callback:
public int Read(float[] buffer, int offset, int count)
{
    // Read raw samples (no internal correction)
    int read = timedAudioBuffer.ReadRaw(buffer, offset, count, currentTimeMicroseconds);

    // Update correction provider with current error
    correctionProvider.UpdateFromSyncError(
        timedAudioBuffer.SyncErrorMicroseconds,
        timedAudioBuffer.SmoothedSyncErrorMicroseconds
    );

    // Apply your correction strategy...
    // If dropping/inserting, notify the buffer:
    timedAudioBuffer.NotifyExternalCorrection(samplesDropped, samplesInserted);

    return outputCount;
}
```

### Configuring Sync Behavior

```csharp
// Use default settings (conservative: 2% max, 3s target)
var options = SyncCorrectionOptions.Default;

// Use CLI-compatible settings (aggressive: 4% max, 2s target)
var options = SyncCorrectionOptions.CliDefaults;

// Custom options
var options = new SyncCorrectionOptions
{
    MaxSpeedCorrection = 0.04,                    // 4% max rate adjustment
    CorrectionTargetSeconds = 2.0,                // Time to eliminate drift
    ResamplingThresholdMicroseconds = 15_000,     // Resampling vs drop/insert
    ReanchorThresholdMicroseconds = 500_000,      // Clear buffer threshold
    StartupGracePeriodMicroseconds = 500_000,     // No correction during startup
};

var calculator = new SyncCorrectionCalculator(options, sampleRate, channels);
```

## Platform-Specific Audio

The SDK handles decoding, buffering, and sync error reporting. You implement `IAudioPlayer` for audio output:

```csharp
public class MyAudioPlayer : IAudioPlayer
{
    public long OutputLatencyMicroseconds { get; private set; }

    public Task InitializeAsync(AudioFormat format, CancellationToken ct)
    {
        // Initialize your audio backend (WASAPI, PulseAudio, CoreAudio, etc.)
    }

    public int Read(float[] buffer, int offset, int count)
    {
        // Called by audio thread - read from TimedAudioBuffer.ReadRaw()
        // Apply sync correction externally
    }

    // ... other methods
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

## Player Timing & Static Delay

Players report timing requirements so the server can schedule audio far enough ahead to avoid
buffer underruns and start-of-stream truncation (per the Sendspin spec's player timing
capabilities). These are advertised in every `client/state` message:

```csharp
var capabilities = new ClientCapabilities
{
    // Minimum startup lead time: codec init, decode warmup, backend buffering, DAC latency.
    // The server schedules the first chunk at least this far ahead after a stream start/restart.
    RequiredLeadTimeMs = 200,   // default: 200 ms (conservative LAN starting point)

    // Minimum ongoing buffer to absorb network jitter (primarily for live streams).
    MinBufferMs = 150,          // default: 150 ms

    // Whether to accept the server's set_static_delay command (advertised in client/state).
    SupportsSetStaticDelay = true,
};
```

Report the **lowest** values that reliably avoid truncation/underruns for your device and network —
larger for remote or high-latency links, smaller for stable LAN. Do **not** fold `static_delay_ms`
into these values; the server applies static delay separately. For empirical tuning, the audio
pipeline exposes measured latency (e.g. `AudioPipeline.DetectedOutputLatencyMs`).

If conditions change at runtime (e.g. a link-type change, or a measured lead time after warmup),
update the values and the SDK re-reports `client/state`:

```csharp
await client.UpdateTimingAsync(requiredLeadTimeMs: 120, minBufferMs: 80);
```

Debounce these updates yourself — report only sustained changes, not transient fluctuations.

### Persisting static delay across restarts

`static_delay_ms` compensates for hardware delay beyond the audio port (external speakers,
amplifiers) and must persist across reboots and reconnections. Because the SDK is a library and
cannot choose where to store it, implement `IStaticDelayStore` and pass it to the client. The SDK
loads on connect (before the first `client/state`) and saves whenever the delay changes (via a
`set_static_delay` command or a GroupSync offset):

```csharp
public sealed class FileStaticDelayStore : IStaticDelayStore
{
    // Use InvariantCulture so the value round-trips regardless of the host's locale.
    public double? Load() => File.Exists(path)
        ? double.Parse(File.ReadAllText(path), CultureInfo.InvariantCulture)
        : null;

    public void Save(double staticDelayMs)
        => File.WriteAllText(path, staticDelayMs.ToString(CultureInfo.InvariantCulture));
}

var client = SendspinClientService.CreateForDial(
    loggerFactory,
    new SendspinClientOptions
    {
        Identity = identity,          // the persisted identity from Quick Start
        AudioPipeline = pipeline,
        StaticDelayStore = new FileStaticDelayStore(),
    });
```

When no store is supplied, behavior is unchanged: the embedder re-supplies the delay on each connect.

## Multi-server arbitration & last-played persistence

When multiple servers can reach a player (server-initiated mode via `SendspinHostService`), the host
arbitrates which one is active. It completes each server's `client/hello` ↔ `server/hello` handshake
first, then applies the spec's decision:

- connections rank by priority class, from the highest-priority activity declared in the
  connection's `server/activate`: `management` > `playback` > `pairing` > empty (no
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
    ArtworkChannels = new()
    {
        new() { Source = ArtworkSources.Album,  Format = "jpeg", MediaWidth = 512, MediaHeight = 512 }, // channel 0
        new() { Source = ArtworkSources.Artist, Format = "png",  MediaWidth = 256, MediaHeight = 256 }, // channel 1
    }
};
```

Images arrive per channel, with the display timestamp and channel number:

```csharp
client.ArtworkReceived += (_, e) =>
{
    // e.Channel (0-3), e.Timestamp (server clock, microseconds), e.ImageData (jpeg/png/bmp bytes)
    displays[e.Channel].Show(e.ImageData);
};

client.ArtworkCleared += (_, e) => displays[e.Channel].Clear(); // empty binary message = clear that channel
```

Change or disable a channel at runtime without reconnecting (server replies with a new `stream/start`):

```csharp
// Switch channel 1 to artist art at a new size:
await client.RequestArtworkFormatAsync(channel: 1, source: ArtworkSources.Artist, mediaWidth: 400, mediaHeight: 400);

// Disable channel 1 (server stops sending it); re-enable later by requesting a real source again:
await client.RequestArtworkFormatAsync(channel: 1, source: ArtworkSources.None);
```

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

Clients with the `visualizer@v1` role receive real-time audio features for music visualization. Six feature types are available: **`loudness`**, **`f_peak`** (dominant frequency + amplitude), **`spectrum`** (display-binned FFT), **`beat`**, **`peak`** (energy onsets), and **`pitch`**. The role is **opt-in** — set `VisualizerSupport` *and* add `visualizer@v1` to `Roles`:

```csharp
var capabilities = new ClientCapabilities
{
    Roles = { "player@v1", "visualizer@v1" },
    VisualizerSupport = new VisualizerSupport
    {
        BufferCapacity = 65536,
        RateMax = 30, // max frames/sec
        Types = new() { VisualizerTypes.Loudness, VisualizerTypes.Spectrum, VisualizerTypes.Beat },
        // Required when Spectrum is requested:
        Spectrum = new VisualizerSpectrum { NDispBins = 32, Scale = "log", FMin = 20, FMax = 16000 },
    },
};
```

Each binary message carries one feature type; subscribe to `VisualizationReceived` and read the populated field:

```csharp
client.VisualizationReceived += (_, frame) =>
{
    if (frame.Loudness is { } loud)      meter.Level = loud / 65535.0;
    if (frame.Spectrum is { } bins)      bars.Update(bins);          // NDispBins values
    if (frame.IsDownbeat is { } down)    pulse.Beat(strong: down);
    if (frame.PitchMidi is { } note)     label.Text = $"MIDI {note:F1}"; // pitch is Q8.8 → fractional MIDI
};
```

`Spectrum` frames are validated against the negotiated `NDispBins` from the latest `stream/start`; malformed frames are dropped (no event). Renegotiate at runtime with `RequestVisualizerFormatAsync(...)`.

> **Note:** `visualizer@v1` follows the [aiosendspin](https://github.com/Sendspin/aiosendspin) reference implementation, which is ahead of the formal protocol spec. The wire format may still evolve. The role degrades gracefully while it matures: it is **opt-in** (off by default), frames that don't match the negotiated/expected format are **dropped** (logged at `Trace`) rather than throwing, and a misbehaving `VisualizationReceived` handler is isolated so it can't disrupt audio or artwork.

## Source role (line-in)

A client with the `source@v1` role captures audio from a local input (AUX/line-in,
turntable preamp, microphone, loopback) and streams it to the server, which mixes and
distributes it to players. Provide an `IAudioCaptureDevice` (your platform's capture
implementation) and add `source@v1` to `Roles`:

```csharp
var caps = new ClientCapabilities
{
    Roles = { "player@v1", "source@v1" },   // a device can be both
    SourceSupport = new SourceRoleSupport { LineSense = true },   // optional: report signal presence
};

var client = SendspinClientService.CreateForDial(
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
device's own format by default; set `SourceSupport.Codec` to encode as something else
(e.g. a PCM capture device streaming as Opus). A device implementing both `source` and
`player` never plays its own captured input locally — it outputs only what the server
distributes, staying in sync with the group.

**Line sensing.** When `SourceSupport.LineSense` is set, call
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
var read = buffer.ReadRaw(samples, offset, count, currentTime);

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
