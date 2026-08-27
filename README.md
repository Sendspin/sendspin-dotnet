# sendspin-dotnet

[![NuGet](https://img.shields.io/nuget/v/Sendspin.SDK.svg)](https://www.nuget.org/packages/Sendspin.SDK/)
[![Build](https://github.com/Sendspin/sendspin-dotnet/actions/workflows/build.yml/badge.svg)](https://github.com/Sendspin/sendspin-dotnet/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

Cross-platform .NET SDK implementing the [Sendspin Protocol](https://www.sendspin-audio.com) for clock-synchronized multi-room audio streaming. Build players that sync perfectly with [Music Assistant](https://music-assistant.io/) and other Sendspin-compatible players.

## Features

- **Microsecond-precision sync** - Kalman filter clock synchronization across devices
- **Built-in codecs** - PCM, FLAC, and Opus decoding out of the box
- **Server discovery** - mDNS-based automatic server finding
- **Sync correction built in** - `TimedAudioBuffer.Read()` applies the spec's strategy; `ReadRaw()` hands the error out for platforms with their own rate-control mechanism
- **NativeAOT & trimming** - Fully compatible with `PublishAot` for single-file native executables
- **Cross-platform** - Windows, Linux, macOS (.NET 8.0 / .NET 10.0)

## Installation

```bash
dotnet add package Sendspin.SDK
```

## Compatibility

The transport is encrypted end to end from 10.0.0 onward, and there is **no downgrade
negotiation** — a 10.x client cannot talk to a server that predates the encrypted protocol.
Pick the line that matches your server.

| SDK | Transport | Requires | Status |
|---|---|---|---|
| **10.x** | Encrypted (Noise `KKpsk2`) | `aiosendspin >= 7.0.0`, and `>= 9.0.0` to pair | Current |
| **9.x** | Plaintext | Any `aiosendspin` | Maintained for pre-encryption servers |

The floor differs by capability. Connecting and playing back, including over unpaired
access, works against `aiosendspin >= 7.0.0`. **Pairing needs `>= 9.0.0`**: that is the
first release carrying the current pairing wire shape, where `server/activate` names the
method in a `pairing` object rather than a flat `selected_pair_method` field. Against 7.0.0
or 8.0.0 a 10.x client refuses every pairing attempt with `method_not_supported`, because
the method it is offered reads as absent. The interop workflow runs against the latest
release (9.1.1) rather than either floor, so the 9.0.0 pairing floor and the 7.0.0
playback floor both rest on inspection rather than CI.

The 9.x line stays maintained for now; it is not end-of-life. If you are on 9.x and your
server supports the encrypted protocol, see
[MIGRATION-10.0.0.md](src/Sendspin.SDK/MIGRATION-10.0.0.md) — note especially that the client
identity must be persisted, which is the one breaking change that fails silently rather than
at compile time.

## Security

The encrypted transport protects a session's confidentiality and integrity, but what it
*authenticates* depends on how the client is paired and configured.

- **Pair before trusting.** An unpaired connection runs under the published Sentinel PSK,
  which authenticates nothing — its trust level is `none`. Pairing establishes a per-server
  pre-shared key and raises the session to trust `user`.
- **Unpaired access is off by default; leave it off unless you need it.**
  `ClientCapabilities.UnpairedAccessEnabled = true` lets a server play to the client with no
  pairing record. Because the Sentinel PSK is a published constant and neither peer's static
  key is bound to its identity by any out-of-band exchange, such a session is vulnerable to
  an **active man-in-the-middle** on the local network. It still protects against passive
  observers, and it says nothing about which peer you are actually talking to.
- **Store keys where the platform protects them.** The client identity and the pairing
  records are long-lived secrets. The shipped file-backed stores write atomically and
  restrict access where the platform supports it, but on Windows a file inherits its parent
  directory's ACL — put it somewhere already user-scoped such as `%LOCALAPPDATA%`. For
  hardware-backed protection, implement `ISendspinIdentityStore` and `IPairingRecordStore`
  over DPAPI, Keychain, or the Android keystore; the identity blob is opaque, so the raw
  private key never leaves the SDK.
- **A static PIN is a long-lived, low-entropy secret.** The X25519 implementation used by the
  PAKE is not constant-time, so a local attacker able to measure the client precisely enough
  may learn something from timing. This matters most for `static_pin`, where the same short
  secret is reused indefinitely; dynamic PIN derives a fresh per-session value.

## Example

A client's `client_id` **is** its Curve25519 public key, so its identity must persist across
restarts. Load it through an `ISendspinIdentityStore` — the SDK generates one on first run and
reuses it on every run after that, so there is no private-key persistence to hand-roll. See
[Persisting the client identity](src/Sendspin.SDK/README.md#persisting-the-client-identity) for
platform stores (DPAPI, Keychain, Android keystore) and the file-permission notes.

```csharp
using Microsoft.Extensions.Logging;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
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

client.GroupStateChanged += (sender, group) =>
{
    Console.WriteLine($"Now playing: {group.Metadata?.Title} - {group.Metadata?.Artist}");
};

// A permanent handshake failure throws rather than being reported only as a state change,
// so a client that never subscribes to ConnectionStateChanged still learns about it.
try
{
    await client.ConnectAsync(new Uri("ws://192.168.1.100:8927/sendspin"));
}
catch (SendspinHandshakeException ex) when (ex.Kind == HandshakeFailureKind.LegacyServer)
{
    // The server predates the encrypted protocol. Upgrade it to aiosendspin >= 7.0.0,
    // or pin this SDK to the 9.x line. Retrying cannot help.
    Console.Error.WriteLine(ex.Message);
    return;
}
catch (SendspinHandshakeException ex)
{
    // HandshakeRejected: no usable pairing record, unsupported cipher suite, or a version
    // mismatch. Pair again rather than retrying.
    Console.Error.WriteLine($"Handshake rejected: {ex.Message}");
    return;
}

// Send commands
await client.SendCommandAsync("play");
await client.SetVolumeAsync(75);
```

You provide the audio output by implementing `IAudioPlayer` for your platform (WASAPI, PulseAudio, CoreAudio, SDL2, etc.) and pulling samples through `TimedAudioBuffer.Read()`, which applies the spec's sync correction for you. On a desktop-class device, `SyncCorrectedSampleSource` applies the same correction by trimming playback speed through a built-in resampler instead of stepping whole frames — smoother, same policy, no correction code on your side. See the [NuGet package README](src/Sendspin.SDK/README.md) for the player quickstart, the full API reference, the sync correction system, and migration guides.

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
