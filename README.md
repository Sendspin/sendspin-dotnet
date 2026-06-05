# sendspin-dotnet

[![NuGet](https://img.shields.io/nuget/v/Sendspin.SDK.svg)](https://www.nuget.org/packages/Sendspin.SDK/)
[![Build](https://github.com/Sendspin/sendspin-dotnet/actions/workflows/build.yml/badge.svg)](https://github.com/Sendspin/sendspin-dotnet/actions/workflows/build.yml)
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

## Multi-server arbitration & last-played persistence

When multiple servers can reach a player (server-initiated mode), `SendspinHostService` arbitrates
which one is active, completing each server's handshake first and then applying the spec's decision:

- a new `connection_reason: playback` server takes over from a `discovery` one;
- a `discovery` server cannot displace a `playback` one;
- when both are equal, the **last-played** server wins, else the existing connection stays.

The loser is sent `client/goodbye` with reason `another_server`; a same-server reconnect drops the
stale socket with `user_request`.

To let the tie-break survive restarts, supply an `ILastPlayedServerStore`:

```csharp
public sealed class FileLastPlayedServerStore : ILastPlayedServerStore
{
    private readonly string _path;
    public FileLastPlayedServerStore(string path) => _path = path;
    public string? Load() => File.Exists(_path) ? File.ReadAllText(_path) : null;
    public void Save(string serverId) => File.WriteAllText(_path, serverId);
}

await using var host = new SendspinHostService(
    loggerFactory,
    lastPlayedServerStore: new FileLastPlayedServerStore("last-played.txt"));
```

The store is optional and best-effort: a throwing implementation is logged and never breaks
arbitration. The existing `LastPlayedServerIdChanged` event and `lastPlayedServerId` seed parameter
continue to work (the seed wins over the store when both are supplied).

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

### Branching & Releases

- **`dev`** — development branch. PRs merge here. Pushes produce `7.2.1-dev.abc1234` pre-release packages (uploaded as build artifacts).
- **`main`** — production branch. PRs from `dev` merge here. Merges build and test but do not publish.
- **Tags** (`v*.*.*`) — pushing a version tag triggers the publish to [nuget.org](https://www.nuget.org/packages/Sendspin.SDK/) and [GitHub Packages](https://github.com/orgs/Sendspin/packages) via [NuGet Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing).

To release a new version:

```bash
git tag v7.3.0
git push origin v7.3.0
```

## License

MIT

## Contributing

Contributions welcome! Please open an issue or PR.
