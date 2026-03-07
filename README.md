# sendspin-dotnet

[![NuGet](https://img.shields.io/nuget/v/Sendspin.SDK.svg)](https://www.nuget.org/packages/Sendspin.SDK/)
[![CI](https://github.com/Sendspin/sendspin-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/Sendspin/sendspin-dotnet/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

Cross-platform .NET SDK implementing the [Sendspin Protocol](https://www.sendspin-audio.com) for clock-synchronized multi-room audio streaming. Build players that sync perfectly with [Music Assistant](https://music-assistant.io/) and other Sendspin-compatible players.

## Features

- **Microsecond-precision sync** - Kalman filter clock synchronization across devices
- **Built-in codecs** - PCM, FLAC, and Opus decoding out of the box
- **Server discovery** - mDNS-based automatic server finding
- **External sync correction** - SDK reports error, your app chooses the correction strategy
- **NativeAOT & trimming** - Fully compatible with `PublishAot` for single-file native executables
- **Cross-platform** - Windows, Linux, macOS (.NET 8.0 / .NET 10.0)

## Installation

```bash
dotnet add package Sendspin.SDK
```

## Example

```csharp
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Synchronization;

var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
var connection = new SendspinConnection(loggerFactory.CreateLogger<SendspinConnection>());
var clockSync = new KalmanClockSynchronizer(loggerFactory.CreateLogger<KalmanClockSynchronizer>());

var client = new SendspinClientService(
    loggerFactory.CreateLogger<SendspinClientService>(),
    connection,
    clockSync,
    new ClientCapabilities
    {
        ClientName = "My Player",
        ProductName = "My Awesome Player",
        Manufacturer = "My Company",
        SoftwareVersion = "1.0.0"
    }
);

// Connect and listen for state changes
await client.ConnectAsync(new Uri("ws://192.168.1.100:8927/sendspin"));

client.GroupStateChanged += (sender, group) =>
{
    Console.WriteLine($"Now playing: {group.Metadata?.Title} - {group.Metadata?.Artist}");
};

// Send commands
await client.SendCommandAsync("play");
await client.SetVolumeAsync(75);
```

You provide the audio output by implementing `IAudioPlayer` for your platform (WASAPI, PulseAudio, CoreAudio, SDL2, etc.). See the [NuGet package README](src/Sendspin.SDK/README.md) for the full API reference, sync correction system, and migration guides.

## Example Projects

| Project | Platform | Audio Backend |
|---------|----------|---------------|
| [WindowsSpin](https://github.com/chrisuthe/windowsSpin) | Windows (WPF) | NAudio / WASAPI |

## Development

```bash
# Build
dotnet build

# Run tests
dotnet test

# Pack NuGet package
dotnet pack src/Sendspin.SDK/Sendspin.SDK.csproj -c Release
```

### Releasing

Push a version tag to trigger the release workflow:

```bash
git tag v7.2.1
git push origin v7.2.1
```

This builds, tests, packs, and publishes to [nuget.org](https://www.nuget.org/packages/Sendspin.SDK/).

## License

MIT

## Contributing

Contributions welcome! Please open an issue or PR.
