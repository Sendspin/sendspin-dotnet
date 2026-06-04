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

```csharp
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Synchronization;

// Create dependencies
var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
var connection = new SendspinConnection(loggerFactory.CreateLogger<SendspinConnection>());
var clockSync = new KalmanClockSynchronizer(loggerFactory.CreateLogger<KalmanClockSynchronizer>());

// Create client with device info
var capabilities = new ClientCapabilities
{
    ClientName = "My Player",
    ProductName = "My Awesome Player",
    Manufacturer = "My Company",
    SoftwareVersion = "1.0.0"
};

var client = new SendspinClientService(
    loggerFactory.CreateLogger<SendspinClientService>(),
    connection,
    clockSync,
    capabilities
);

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
    ClientName = "Living Room",           // Display name
    ProductName = "MySpeaker Pro",        // Product identifier
    Manufacturer = "Acme Audio",          // Your company
    SoftwareVersion = "2.1.0"             // App version
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
    public double? Load() => File.Exists(path) ? double.Parse(File.ReadAllText(path)) : null;
    public void Save(double staticDelayMs) => File.WriteAllText(path, staticDelayMs.ToString());
}

var client = new SendspinClientService(
    logger, connection, clockSync, capabilities,
    audioPipeline: pipeline,
    staticDelayStore: new FileStaticDelayStore());
```

When no store is supplied, behavior is unchanged: the embedder re-supplies the delay on each connect.

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
