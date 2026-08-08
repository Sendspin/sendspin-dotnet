# Typed `server/hello` and `stream/start` Events Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Expose the already-deserialized `server/hello` and `stream/start` protocol payloads as first-class typed events and properties on `ISendspinClient`, so downstream apps (starting with the Sendspin conformance .NET adapter) don't have to re-parse raw JSON frames that the SDK just finished deserializing.

**Architecture:** The SDK already deserializes both messages inside `SendspinClientService.HandleServerHello` (line 396) and `HandleStreamStartAsync` (line 905). The fix is purely additive: retain the deserialized payload on a new public property, raise a new typed event with the same payload object, and forward both through `SendspinHostService`. **No re-parsing** — the event payload is the very object `MessageSerializer.Deserialize` already produced. The `stream/start` payload model needs one non-breaking extension: add the `artwork` sub-object (currently dropped by the deserializer).

**Tech Stack:** C# 13, .NET 8 / .NET 10, `System.Text.Json` source-generated serialization (AOT-safe via `MessageSerializerContext`), xUnit 2.9.

**Issue:** https://github.com/Sendspin/conformance/issues/39

**Non-goals:** Renaming `TextMessageReceived`, deprecating the scalar `ServerId`/`ServerName`/`ConnectionReason` properties, or any broader refactor of message handling.

---

## File Structure

**Create:**
- `src/Sendspin.SDK/Protocol/Messages/StreamStartArtwork.cs` — new types `StreamStartArtwork` and `ArtworkStreamChannel` matching the server's wire format (`source`, `format`, `width`, `height`). Kept in the same namespace as existing protocol messages for discoverability.
- `tests/Sendspin.SDK.Tests/Client/FakeSendspinConnection.cs` — in-memory `ISendspinConnection` test double for driving `SendspinClientService` via synthetic text frames without a WebSocket.
- `tests/Sendspin.SDK.Tests/Client/SendspinClientServiceEventTests.cs` — event/property tests for `ServerHelloReceived` and `StreamStartReceived`.

**Modify:**
- `src/Sendspin.SDK/Protocol/Messages/StreamStartMessage.cs` — add `Artwork` property to `StreamStartPayload`.
- `src/Sendspin.SDK/Client/ISendSpinClient.cs` — add `LastServerHello` property, `LastStreamStart` property, `ServerHelloReceived` event, `StreamStartReceived` event.
- `src/Sendspin.SDK/Client/SendSpinClient.cs` — add backing properties, raise events from the existing deserialization sites (no re-parsing).
- `src/Sendspin.SDK/Client/SendSpinHostService.cs` — forward the new events and surface the new properties.
- `src/Sendspin.SDK/Sendspin.SDK.csproj` — bump `<Version>` to 7.4.0 and add release notes entry.
- `tests/Sendspin.SDK.Tests/Protocol/MessageSerializerTests.cs` — add round-trip test for `StreamStartPayload.Artwork`.

**Why these boundaries:** the new artwork types live next to `StreamStartMessage.cs` because they are only used as nested payload shapes of that message. The fake connection lives under `tests/.../Client/` because the only consumer is the client-service event test file alongside it. No new public namespaces.

---

## Task 1: Model the `stream/start` artwork sub-object

**Files:**
- Create: `src/Sendspin.SDK/Protocol/Messages/StreamStartArtwork.cs`
- Modify: `src/Sendspin.SDK/Protocol/Messages/StreamStartMessage.cs`
- Modify: `tests/Sendspin.SDK.Tests/Protocol/MessageSerializerTests.cs`

**Context:** the conformance Go adapter sends this shape:
```json
"artwork": {
  "channels": [
    { "source": "album", "format": "jpeg", "width": 512, "height": 512 }
  ]
}
```
Today `StreamStartPayload` only models `player` (`Format`), so the entire `artwork` object is silently dropped. Do **not** reuse `ArtworkSupport` / `ArtworkChannelSpec` from `ClientHelloMessage.cs` — those are client→server capability advertisements with `media_width`/`media_height` fields, a different contract from the server→client stream dimensions. Conflating them would force ugly JSON shape coupling and hurt both sides.

- [ ] **Step 1.1: Write the failing round-trip test**

Append to `tests/Sendspin.SDK.Tests/Protocol/MessageSerializerTests.cs` (inside the existing `MessageSerializerTests` class, after the last `[Fact]`):

```csharp
[Fact]
public void Deserialize_StreamStartMessage_ParsesArtworkChannels()
{
    var json = """
    {
        "type": "stream/start",
        "payload": {
            "artwork": {
                "channels": [
                    { "source": "album", "format": "jpeg", "width": 512, "height": 512 }
                ]
            }
        }
    }
    """;

    var msg = MessageSerializer.Deserialize(json) as StreamStartMessage;

    Assert.NotNull(msg);
    Assert.NotNull(msg.Payload.Artwork);
    var channel = Assert.Single(msg.Payload.Artwork.Channels);
    Assert.Equal("album", channel.Source);
    Assert.Equal("jpeg", channel.Format);
    Assert.Equal(512, channel.Width);
    Assert.Equal(512, channel.Height);
}

[Fact]
public void Deserialize_StreamStartMessage_ArtworkAbsent_YieldsNull()
{
    var json = """
    {
        "type": "stream/start",
        "payload": {
            "player": { "codec": "pcm", "sample_rate": 48000, "channels": 2, "bit_depth": 16 }
        }
    }
    """;

    var msg = MessageSerializer.Deserialize(json) as StreamStartMessage;

    Assert.NotNull(msg);
    Assert.Null(msg.Payload.Artwork);
    Assert.NotNull(msg.Payload.Format);
}
```

- [ ] **Step 1.2: Run the tests to confirm they fail**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj --filter "FullyQualifiedName~MessageSerializerTests.Deserialize_StreamStartMessage"`

Expected: both tests fail — compile error on `msg.Payload.Artwork` (property does not exist).

- [ ] **Step 1.3: Create the artwork model file**

Create `src/Sendspin.SDK/Protocol/Messages/StreamStartArtwork.cs` with exact contents:

```csharp
// <copyright file="StreamStartArtwork.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using System.Text.Json.Serialization;

namespace Sendspin.SDK.Protocol.Messages;

/// <summary>
/// Artwork metadata carried inside a <c>stream/start</c> payload.
/// Describes which artwork channels the server is about to stream (one or more album/etc. images).
/// Distinct from <see cref="ArtworkSupport"/>, which is the client's capability advertisement in <c>client/hello</c>.
/// </summary>
public sealed class StreamStartArtwork
{
    /// <summary>
    /// Channels the server is streaming. Each channel corresponds to a binary artwork chunk
    /// delivered on its own channel index (0-3 per the Sendspin spec).
    /// </summary>
    [JsonPropertyName("channels")]
    public List<ArtworkStreamChannel> Channels { get; set; } = new();
}

/// <summary>
/// One artwork channel the server is about to stream.
/// </summary>
public sealed class ArtworkStreamChannel
{
    /// <summary>
    /// Semantic source of the artwork (e.g. <c>"album"</c>, <c>"artist"</c>).
    /// </summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Image format of the bytes that will follow on the binary channel (e.g. <c>"jpeg"</c>).
    /// </summary>
    [JsonPropertyName("format")]
    public string Format { get; set; } = string.Empty;

    /// <summary>
    /// Actual pixel width of the image the server will send. 0 if unspecified.
    /// </summary>
    [JsonPropertyName("width")]
    public int Width { get; set; }

    /// <summary>
    /// Actual pixel height of the image the server will send. 0 if unspecified.
    /// </summary>
    [JsonPropertyName("height")]
    public int Height { get; set; }
}
```

- [ ] **Step 1.4: Extend `StreamStartPayload`**

In `src/Sendspin.SDK/Protocol/Messages/StreamStartMessage.cs`, replace the existing `StreamStartPayload` class (lines 29-41) with:

```csharp
/// <summary>
/// Payload for stream/start message per Sendspin spec.
/// </summary>
public sealed class StreamStartPayload
{
    /// <summary>
    /// Gets or sets the audio format for the incoming stream.
    /// The "player" object contains codec, channels, sample_rate, bit_depth, and codec_header.
    /// Null when the stream/start only carries artwork info (no player key).
    /// </summary>
    [JsonPropertyName("player")]
    public AudioFormat? Format { get; set; }

    /// <summary>
    /// Gets or sets the artwork channels the server is about to stream.
    /// Null when the stream/start only carries player/audio info (no artwork key).
    /// </summary>
    [JsonPropertyName("artwork")]
    public StreamStartArtwork? Artwork { get; set; }
}
```

No change needed to `MessageSerializerContext.cs` — `StreamStartMessage` is already registered (line 29) and `System.Text.Json`'s source generator walks transitive reference types automatically.

- [ ] **Step 1.5: Run the tests to confirm they pass**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj --filter "FullyQualifiedName~MessageSerializerTests"`

Expected: all `MessageSerializerTests` pass, including the two new ones.

- [ ] **Step 1.6: Commit**

```bash
git add src/Sendspin.SDK/Protocol/Messages/StreamStartArtwork.cs \
        src/Sendspin.SDK/Protocol/Messages/StreamStartMessage.cs \
        tests/Sendspin.SDK.Tests/Protocol/MessageSerializerTests.cs
git commit -m "Model stream/start artwork sub-object in StreamStartPayload"
```

---

## Task 2: Create a fake `ISendspinConnection` test double

**Files:**
- Create: `tests/Sendspin.SDK.Tests/Client/FakeSendspinConnection.cs`

**Context:** `SendspinClientService` is normally driven by a real WebSocket connection. For event tests we need to push synthetic text frames into its `OnTextMessageReceived` handler. The cleanest route is a fake that implements `ISendspinConnection` and exposes helpers for raising `TextMessageReceived`. This fake will be reused by every test added in Tasks 3 and 4.

- [ ] **Step 2.1: Create the fake connection**

Create `tests/Sendspin.SDK.Tests/Client/FakeSendspinConnection.cs`:

```csharp
using Sendspin.SDK.Connection;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// In-memory <see cref="ISendspinConnection"/> test double.
/// Tests drive the <c>SendspinClientService</c> by calling <see cref="RaiseTextMessageReceived"/>
/// instead of running a real WebSocket.
/// </summary>
internal sealed class FakeSendspinConnection : ISendspinConnection
{
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public Uri? ServerUri { get; private set; }
    public List<IMessage> SentMessages { get; } = new();

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;
    public event EventHandler<string>? TextMessageReceived;
    public event EventHandler<ReadOnlyMemory<byte>>? BinaryMessageReceived;

    public Task ConnectAsync(Uri serverUri, CancellationToken cancellationToken = default)
    {
        ServerUri = serverUri;
        SetState(ConnectionState.Connected);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(string reason = "user_request", CancellationToken cancellationToken = default)
    {
        SetState(ConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    public Task SendMessageAsync<T>(T message, CancellationToken cancellationToken = default)
        where T : IMessage
    {
        SentMessages.Add(message);
        return Task.CompletedTask;
    }

    public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void RaiseTextMessageReceived(string json)
        => TextMessageReceived?.Invoke(this, json);

    public void RaiseBinaryMessageReceived(ReadOnlyMemory<byte> data)
        => BinaryMessageReceived?.Invoke(this, data);

    private void SetState(ConnectionState newState)
    {
        var old = State;
        State = newState;
        StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(old, newState));
    }
}
```

- [ ] **Step 2.2: Verify it compiles**

Run: `dotnet build tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj`

Expected: build succeeds, no warnings about unused type.

- [ ] **Step 2.3: Commit**

```bash
git add tests/Sendspin.SDK.Tests/Client/FakeSendspinConnection.cs
git commit -m "Add FakeSendspinConnection test double for client-service event tests"
```

---

## Task 3: Add typed `ServerHelloReceived` event and `LastServerHello` property

**Files:**
- Modify: `src/Sendspin.SDK/Client/ISendSpinClient.cs`
- Modify: `src/Sendspin.SDK/Client/SendSpinClient.cs` (class is `SendspinClientService`, file is misnamed historically — do not rename the file)
- Create: `tests/Sendspin.SDK.Tests/Client/SendspinClientServiceEventTests.cs`

**Context:** `HandleServerHello` at `src/Sendspin.SDK/Client/SendSpinClient.cs:396` already deserializes the full `ServerHelloMessage` (line 398). Today it copies three scalars onto the client (`ServerId`, `ServerName`, `ConnectionReason`, lines 406-408) and discards the rest — including `ActiveRoles` and `Version`, which are load-bearing for conformance tests. Keep the scalars for backward compatibility; additionally stash the whole `ServerHelloPayload` and raise an event with the same object. **Do not re-call `MessageSerializer.Deserialize`** — the event payload must be `message.Payload` from the existing call.

- [ ] **Step 3.1: Write the failing event test**

Create `tests/Sendspin.SDK.Tests/Client/SendspinClientServiceEventTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

public class SendspinClientServiceEventTests
{
    [Fact]
    public void ServerHello_RaisesTypedEventAndPopulatesLastServerHello()
    {
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection);

        ServerHelloPayload? received = null;
        client.ServerHelloReceived += (_, payload) => received = payload;

        const string helloJson = """
        {
            "type": "server/hello",
            "payload": {
                "server_id": "srv-abc",
                "name": "Kitchen",
                "version": 1,
                "active_roles": ["player@v1", "artwork@v1"],
                "connection_reason": "playback"
            }
        }
        """;

        connection.RaiseTextMessageReceived(helloJson);

        Assert.NotNull(received);
        Assert.Equal("srv-abc", received.ServerId);
        Assert.Equal("Kitchen", received.Name);
        Assert.Equal(1, received.Version);
        Assert.Equal(new[] { "player@v1", "artwork@v1" }, received.ActiveRoles);
        Assert.Equal("playback", received.ConnectionReason);

        Assert.NotNull(client.LastServerHello);
        Assert.Same(received, client.LastServerHello);

        // Scalar backcompat accessors still set:
        Assert.Equal("srv-abc", client.ServerId);
        Assert.Equal("Kitchen", client.ServerName);
        Assert.Equal("playback", client.ConnectionReason);
    }

    [Fact]
    public void ServerHello_EventFiresBeforeHandshakeCompletes()
    {
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection);

        ServerHelloPayload? seenPayload = null;
        string? serverIdAtEventTime = null;
        client.ServerHelloReceived += (_, payload) =>
        {
            seenPayload = payload;
            serverIdAtEventTime = client.ServerId;
        };

        connection.RaiseTextMessageReceived("""
            { "type": "server/hello", "payload": { "server_id": "srv-1", "version": 1, "active_roles": [] } }
            """);

        Assert.NotNull(seenPayload);
        // Subscribers observe the scalar property already set when the event fires.
        Assert.Equal("srv-1", serverIdAtEventTime);
    }
}
```

- [ ] **Step 3.2: Run tests to confirm they fail**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj --filter "FullyQualifiedName~SendspinClientServiceEventTests"`

Expected: both tests fail — compile error on `ServerHelloReceived` / `LastServerHello` (members do not exist).

- [ ] **Step 3.3: Extend the interface**

In `src/Sendspin.SDK/Client/ISendSpinClient.cs`, add two `using` lines at the top if not present:

```csharp
using Sendspin.SDK.Protocol.Messages;
```

Then add these members inside the interface (place after `ServerName` property for properties, and after `SyncOffsetApplied` event for events):

```csharp
    /// <summary>
    /// The most recent <c>server/hello</c> payload received from the server,
    /// or <c>null</c> if the handshake has not yet completed.
    /// </summary>
    /// <remarks>
    /// Exposes fields that the scalar <see cref="ServerId"/>/<see cref="ServerName"/> properties
    /// don't surface, notably <see cref="ServerHelloPayload.ActiveRoles"/> and
    /// <see cref="ServerHelloPayload.Version"/>. Re-set on every reconnect handshake.
    /// </remarks>
    ServerHelloPayload? LastServerHello { get; }
```

```csharp
    /// <summary>
    /// Raised when a <c>server/hello</c> message is received and parsed.
    /// Fires once per successful handshake (including reconnects). The payload is the
    /// same object cached on <see cref="LastServerHello"/>.
    /// </summary>
    event EventHandler<ServerHelloPayload>? ServerHelloReceived;
```

- [ ] **Step 3.4: Implement on `SendspinClientService`**

In `src/Sendspin.SDK/Client/SendSpinClient.cs`:

**(a)** In the property region near the existing scalar properties (after `public string? ConnectionReason { get; private set; }` at line 75), add:

```csharp
    /// <inheritdoc />
    public ServerHelloPayload? LastServerHello { get; private set; }
```

**(b)** In the event region (after `public event EventHandler<SyncOffsetEventArgs>? SyncOffsetApplied;` at line 88), add:

```csharp
    public event EventHandler<ServerHelloPayload>? ServerHelloReceived;
```

**(c)** Modify `HandleServerHello` (line 396). Replace the body from the `ServerId = message.ServerId;` line through `ConnectionReason = message.Payload.ConnectionReason;` with:

```csharp
        var payload = message.Payload;
        LastServerHello = payload;
        ServerId = payload.ServerId;
        ServerName = payload.Name;
        ConnectionReason = payload.ConnectionReason;
```

Then, **before** `_handshakeTcs?.TrySetResult(true);` at line 439, add:

```csharp
        // Raise the typed event after state is populated but before awaiters of
        // ConnectAsync wake up, so handlers see a fully initialized client.
        ServerHelloReceived?.Invoke(this, payload);
```

No other changes to this method — the existing `_logger.LogInformation`, `MarkConnected`, `Reset`, `NotifyReconnect`, and `SendInitialClientStateAsync` calls stay exactly where they are.

**Important:** do **not** add a second `MessageSerializer.Deserialize` call. The whole point is that `message.Payload` is already the typed object; we just retain and publish it.

- [ ] **Step 3.5: Run tests to confirm they pass**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj --filter "FullyQualifiedName~SendspinClientServiceEventTests"`

Expected: both new tests pass. No other tests should break — run the full suite:

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj`

Expected: full suite green.

- [ ] **Step 3.6: Commit**

```bash
git add src/Sendspin.SDK/Client/ISendSpinClient.cs \
        src/Sendspin.SDK/Client/SendSpinClient.cs \
        tests/Sendspin.SDK.Tests/Client/SendspinClientServiceEventTests.cs
git commit -m "Expose typed ServerHelloReceived event and LastServerHello property"
```

---

## Task 4: Add typed `StreamStartReceived` event and `LastStreamStart` property

**Files:**
- Modify: `src/Sendspin.SDK/Client/ISendSpinClient.cs`
- Modify: `src/Sendspin.SDK/Client/SendSpinClient.cs`
- Modify: `tests/Sendspin.SDK.Tests/Client/SendspinClientServiceEventTests.cs`

**Context:** `HandleStreamStartAsync` at `src/Sendspin.SDK/Client/SendSpinClient.cs:905` already deserializes `StreamStartMessage` (line 907). Three cases to cover:

1. **Audio + artwork:** both `player` and `artwork` present → raise event with full payload.
2. **Audio only:** `player` present, `artwork` null → raise event, pipeline starts as today.
3. **Artwork-only:** `player` null, `artwork` present → method currently returns early at line 917. This is exactly the case the conformance adapter needs for `server-initiated-artwork`. **Raise the event before the early return.**

The event must fire once per `stream/start` frame, with the already-deserialized `StreamStartPayload`. No re-parsing.

- [ ] **Step 4.1: Write the failing tests**

Append to `tests/Sendspin.SDK.Tests/Client/SendspinClientServiceEventTests.cs`:

```csharp
    [Fact]
    public void StreamStart_WithPlayerAndArtwork_RaisesEventAndCachesPayload()
    {
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection);

        StreamStartPayload? received = null;
        client.StreamStartReceived += (_, payload) => received = payload;

        const string json = """
        {
            "type": "stream/start",
            "payload": {
                "player": { "codec": "pcm", "sample_rate": 48000, "channels": 2, "bit_depth": 16 },
                "artwork": { "channels": [ { "source": "album", "format": "jpeg", "width": 512, "height": 512 } ] }
            }
        }
        """;

        connection.RaiseTextMessageReceived(json);

        Assert.NotNull(received);
        Assert.NotNull(received.Format);
        Assert.Equal("pcm", received.Format.Codec);
        Assert.NotNull(received.Artwork);
        Assert.Single(received.Artwork.Channels);
        Assert.Same(received, client.LastStreamStart);
    }

    [Fact]
    public void StreamStart_ArtworkOnly_StillRaisesEvent()
    {
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection);

        StreamStartPayload? received = null;
        client.StreamStartReceived += (_, payload) => received = payload;

        const string json = """
        {
            "type": "stream/start",
            "payload": {
                "artwork": { "channels": [ { "source": "album", "format": "jpeg", "width": 256, "height": 256 } ] }
            }
        }
        """;

        connection.RaiseTextMessageReceived(json);

        Assert.NotNull(received);
        Assert.Null(received.Format);
        Assert.NotNull(received.Artwork);
        Assert.Equal(256, received.Artwork.Channels[0].Width);
    }

    [Fact]
    public void StreamStart_PlayerOnly_ArtworkNullOnPayload()
    {
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection);

        StreamStartPayload? received = null;
        client.StreamStartReceived += (_, payload) => received = payload;

        const string json = """
        {
            "type": "stream/start",
            "payload": {
                "player": { "codec": "pcm", "sample_rate": 44100, "channels": 2, "bit_depth": 16 }
            }
        }
        """;

        connection.RaiseTextMessageReceived(json);

        Assert.NotNull(received);
        Assert.NotNull(received.Format);
        Assert.Null(received.Artwork);
    }
```

- [ ] **Step 4.2: Run tests to confirm they fail**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj --filter "FullyQualifiedName~StreamStart"`

Expected: the three new tests fail — compile error on `StreamStartReceived` / `LastStreamStart`.

- [ ] **Step 4.3: Extend the interface**

In `src/Sendspin.SDK/Client/ISendSpinClient.cs`, add after the `LastServerHello` property added in Task 3:

```csharp
    /// <summary>
    /// The most recent <c>stream/start</c> payload received from the server,
    /// or <c>null</c> if no stream has started on this connection yet.
    /// </summary>
    /// <remarks>
    /// Includes both <see cref="StreamStartPayload.Format"/> (player audio format) and
    /// <see cref="StreamStartPayload.Artwork"/>. Either may be null depending on the stream type.
    /// Replaced on every <c>stream/start</c>, including artwork-only updates.
    /// </remarks>
    StreamStartPayload? LastStreamStart { get; }
```

And after the `ServerHelloReceived` event:

```csharp
    /// <summary>
    /// Raised when a <c>stream/start</c> message is received and parsed.
    /// Fires for every <c>stream/start</c>, whether it carries audio format, artwork metadata, or both.
    /// The payload is the same object cached on <see cref="LastStreamStart"/>.
    /// </summary>
    event EventHandler<StreamStartPayload>? StreamStartReceived;
```

- [ ] **Step 4.4: Implement on `SendspinClientService`**

In `src/Sendspin.SDK/Client/SendSpinClient.cs`:

**(a)** After the `LastServerHello` property added in Task 3, add:

```csharp
    /// <inheritdoc />
    public StreamStartPayload? LastStreamStart { get; private set; }
```

**(b)** After the `ServerHelloReceived` event added in Task 3, add:

```csharp
    public event EventHandler<StreamStartPayload>? StreamStartReceived;
```

**(c)** Modify `HandleStreamStartAsync` at line 905. Replace the current opening of the method (up to and including the artwork-only early return) with:

```csharp
    private async Task HandleStreamStartAsync(string json)
    {
        var message = MessageSerializer.Deserialize<StreamStartMessage>(json);
        if (message is null)
        {
            return;
        }

        var payload = message.Payload;
        LastStreamStart = payload;
        StreamStartReceived?.Invoke(this, payload);

        // stream/start with no "player" key is artwork-only — skip pipeline start
        if (payload.Format is null)
        {
            _logger.LogDebug("Stream start is artwork-only (no player key), skipping pipeline start");
            return;
        }

        _logger.LogInformation("Stream starting: {Format}", payload.Format);
```

The rest of the method body (from `// Clear any stale chunks from previous streams` through the end) is unchanged, but update any remaining references from `message.Format` to `payload.Format`. Specifically, in the `_audioPipeline.StartAsync(message.Format)` call (currently line 946), change to:

```csharp
                await _audioPipeline.StartAsync(payload.Format);
```

That reuses the already-read local instead of hitting the `Format` convenience accessor (which just forwards to `payload.Format` anyway). Minor, but keeps a single source of truth within the method.

**Important:** event fires before the artwork-only early return, so artwork-only streams still notify subscribers. And there is still only one `Deserialize` call — the new property and event both use the `payload` local.

- [ ] **Step 4.5: Run tests to confirm they pass**

Run: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj --filter "FullyQualifiedName~SendspinClientServiceEventTests"`

Expected: all five tests in this file pass.

Run full suite: `dotnet test tests/Sendspin.SDK.Tests/Sendspin.SDK.Tests.csproj`

Expected: green.

- [ ] **Step 4.6: Commit**

```bash
git add src/Sendspin.SDK/Client/ISendSpinClient.cs \
        src/Sendspin.SDK/Client/SendSpinClient.cs \
        tests/Sendspin.SDK.Tests/Client/SendspinClientServiceEventTests.cs
git commit -m "Expose typed StreamStartReceived event and LastStreamStart property"
```

---

## Task 5: Forward new events through `SendspinHostService`

**Files:**
- Modify: `src/Sendspin.SDK/Client/SendSpinHostService.cs`

**Context:** `SendspinHostService` is the façade most apps consume when running in listener mode. It manages **multiple concurrent server connections** indexed by `serverId` in the `_connections` dictionary (see `ActiveServerConnection` at `SendSpinHostService.cs:748`); each entry owns its own `SendspinClientService`. It re-exposes per-client events (see existing `ArtworkReceived`/`ArtworkCleared` forwarding around `SendSpinHostService.cs:328`).

**Important scope decision:** we forward only the *events*, not `LastServerHello` / `LastStreamStart` as host-service properties. Since multiple servers can be connected simultaneously, there is no single "last hello" to return. Consumers that need per-server state should subscribe to the event and key their own state on `ServerHelloPayload.ServerId` (or correlate via the existing `ConnectedServerInfo` from `ServerConnected`). Per-server retrieval helpers can be added later if demand arises; they are out of scope for #39.

- [ ] **Step 5.1: Add forwarded events to `SendspinHostService`**

In `src/Sendspin.SDK/Client/SendSpinHostService.cs`:

**(a)** Near the existing forwarded events (the block containing `ArtworkReceived` around line 94), add:

```csharp
    /// <summary>
    /// Raised when any connected client receives a <c>server/hello</c>.
    /// Fires once per server handshake (including reconnects). Multiple concurrent
    /// connections will each raise this event independently — consumers that care
    /// about per-server state should key off <see cref="ServerHelloPayload.ServerId"/>.
    /// </summary>
    public event EventHandler<ServerHelloPayload>? ServerHelloReceived;

    /// <summary>
    /// Raised when any connected client receives a <c>stream/start</c>.
    /// Fires once per stream/start frame (audio, artwork, or both).
    /// </summary>
    public event EventHandler<StreamStartPayload>? StreamStartReceived;
```

**(b)** Near the existing client-event wiring (the block around lines 317-329 where `GroupStateChanged`, `PlayerStateChanged`, `ArtworkReceived`, `ArtworkCleared` are subscribed), add two more subscriptions:

```csharp
            client.ServerHelloReceived += (s, payload) => ServerHelloReceived?.Invoke(this, payload);
            client.StreamStartReceived += (s, payload) => StreamStartReceived?.Invoke(this, payload);
```

**(c)** Confirm the file's `using` directives include `Sendspin.SDK.Protocol.Messages;` (needed for `ServerHelloPayload` and `StreamStartPayload`). Add it if missing.

- [ ] **Step 5.2: Build and run full test suite**

Run: `dotnet build`

Expected: clean build.

Run: `dotnet test`

Expected: full suite green, including the client-service event tests from Tasks 3 and 4.

- [ ] **Step 5.3: Commit**

```bash
git add src/Sendspin.SDK/Client/SendSpinHostService.cs
git commit -m "Forward ServerHelloReceived and StreamStartReceived through host service"
```

---

## Task 6: Version bump and release notes

**Files:**
- Modify: `src/Sendspin.SDK/Sendspin.SDK.csproj`

**Context:** the current `<Version>` is 7.3.0 (line 11). This change is additive (no breaking API changes), so a minor bump to 7.4.0 is correct. Release notes sit in `<PackageReleaseNotes>` as a literal multi-line string; prepend a new section at the top of the existing notes so the most recent version stays first.

- [ ] **Step 6.1: Bump version**

In `src/Sendspin.SDK/Sendspin.SDK.csproj`, change line 11:

```xml
    <Version>7.3.0</Version>
```

to:

```xml
    <Version>7.4.0</Version>
```

- [ ] **Step 6.2: Prepend release notes**

In the same file, find the `<PackageReleaseNotes>` element (line 22) and insert this block immediately after the opening tag, before the existing `v7.2.1` section:

```
v7.4.0 - Typed Protocol Event Surface:

New Features:
- ISendspinClient.ServerHelloReceived event and LastServerHello property. Exposes the full
  server/hello payload (ServerId, Name, Version, ActiveRoles, ConnectionReason) so apps don't
  have to subscribe to the raw TextMessageReceived event and re-parse JSON.
- ISendspinClient.StreamStartReceived event and LastStreamStart property. Fires for every
  stream/start frame (audio, artwork, or both) with the fully deserialized payload.
- SendspinHostService.ServerHelloReceived and SendspinHostService.StreamStartReceived forward
  the corresponding per-client events to host-service consumers.
- StreamStartPayload.Artwork: models the previously-dropped "artwork" sub-object sent by servers
  alongside or instead of "player". Exposes channel source, format, width, and height.
- Addresses Sendspin/conformance#39: the .NET conformance adapter no longer needs to
  JsonDocument.Parse raw protocol frames to satisfy the peer_hello / stream contracts.

No breaking changes. Existing TextMessageReceived, scalar ServerId/ServerName/ConnectionReason
properties, and all pre-existing events behave identically.

```

(Leave a blank line at the end of the inserted block so the existing `v7.2.1` heading stays visually separated.)

- [ ] **Step 6.3: Verify pack still succeeds**

Run: `dotnet pack src/Sendspin.SDK/Sendspin.SDK.csproj -c Release --output ./artifacts-verify`

Expected: `Sendspin.SDK.7.4.0.nupkg` produced in `./artifacts-verify`. Delete the directory afterward — no need to commit the artifact:

```bash
rm -rf ./artifacts-verify
```

- [ ] **Step 6.4: Commit**

```bash
git add src/Sendspin.SDK/Sendspin.SDK.csproj
git commit -m "Bump to 7.4.0 with typed hello/stream-start events release notes"
```

---

## Task 7: Final end-to-end verification

**Files:** (none — verification only)

- [ ] **Step 7.1: Run the full test suite one more time on a clean build**

Run: `dotnet clean && dotnet test`

Expected: all tests pass on both `net8.0` and `net10.0` target frameworks.

- [ ] **Step 7.2: Manually scan for stray re-parsing**

Run: `grep -rn "JsonDocument.Parse\|MessageSerializer.Deserialize" src/Sendspin.SDK/Client/`

Expected: the only hits are the pre-existing `MessageSerializer.Deserialize<T>(json)` calls at the top of each `Handle...` method. **There must be exactly one deserialize per handler.** If any handler has two, the event wiring is doing redundant parsing — fix before proceeding.

- [ ] **Step 7.3: Confirm the issue is addressed**

Re-read Sendspin/conformance#39. The three "Proposed SDK additions" map to this plan as:

1. ✅ Typed `ServerHelloReceived` event and `ServerInfo`-equivalent object (`ServerHelloPayload` via `LastServerHello`) — Task 3.
2. ✅ Typed `StreamStartReceived` events with player and artwork sub-objects — Tasks 1 + 4.
3. ✅ `TextMessageReceived` still available on `ISendspinConnection` as the secondary power-user escape hatch — unchanged by this plan.

- [ ] **Step 7.4: Final summary commit (if needed)**

If steps 7.1-7.3 revealed any last fixes, commit them. Otherwise, no commit needed.

---

## Notes for reviewers

- **Ordering guarantee in `HandleServerHello`:** the event fires after scalar properties (`ServerId`, `ServerName`, `ConnectionReason`, `LastServerHello`) are set but before `_handshakeTcs?.TrySetResult(true)`. Subscribers inside `ConnectAsync` awaiters therefore see a consistent client snapshot.
- **Thread safety:** events fire on whichever thread delivered the text frame (WebSocket receive loop). This matches every other event on `SendspinClientService` — no new concurrency concerns.
- **Reconnects:** `LastServerHello` is overwritten on each handshake, and the event fires again. Subscribers should treat `LastServerHello` as "current," not "first ever."
- **AOT safety:** the new `StreamStartArtwork` / `ArtworkStreamChannel` types are reached transitively through `StreamStartMessage`, which is already registered in `MessageSerializerContext`. No `[JsonSerializable]` additions needed. Confirm by running `dotnet publish -r win-x64 -c Release /p:PublishAot=true` on any AOT-enabled sample app post-merge, but this is not required for the SDK tests themselves.
