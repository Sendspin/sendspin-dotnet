# Design: Encrypted-only clean break (v10.0.0)

*Resolves [#78](https://github.com/Sendspin/sendspin-dotnet/issues/78) and [#83](https://github.com/Sendspin/sendspin-dotnet/issues/83). Implements Option A from `docs/encryption-and-linein-plan.md` §6.*

## 1. Decision

**v10.0.0 speaks only the encrypted Sendspin protocol.** The v9.x line remains available for
deployments whose servers predate encryption. Applications move to 10.x when their user base is
ready for encryption.

The plan named Option A as the baseline pending confirmation at Phase 1 exit. Phase 1 exited
without recording the decision, and the code shipped defaulting to plaintext. This design records
the decision and makes the code match it.

### Why Option A

1. The reference **client** (aiosendspin 7.0.0) is encrypted-only. Only its **server** dual-stacks,
   behind an operator `allow_unencrypted` flag — so upgrade pressure is one-directional. Old
   clients keep working against new servers; Option A strands nobody who upgrades their server.
2. The spec has no downgrade negotiation. A dual-stack client conforms to nothing.
3. Upstream is moving toward strictness: aiosendspin
   [#298](https://github.com/Sendspin/aiosendspin/pull/298) adds `allow_noncompliant_clients`,
   documented as defaulting to `False` in a future version and being removed with all
   backwards-compat paths in a later one.
4. This is a SemVer major, and v9.x remains available for pinned deployments.

## 2. Problem being fixed

The current state is neither Option A nor Option B: it carries Option B's dual-stack surface area
without Option B's two mandatory anti-downgrade rules, and Option A's documentation.

A default-constructed client speaks the legacy plaintext protocol, because `INoiseSessionInfo` is
an optional constructor argument and `_noiseSession is null` selects the legacy path:

| Site | Behavior when session/identity is absent |
|---|---|
| `Connection/SendSpinConnection.cs:44` | `_framing = framing ?? PlaintextWireFraming.Instance` |
| `Connection/IncomingConnection.cs:37` | same |
| `Client/SendSpinHostService.cs:395-401` | `_identity is null` → null framing → plaintext |
| `Client/SendSpinClient.cs:224-231` | legacy `client/hello`-first flow |
| `Client/SendSpinClient.cs:239` | 10 s handshake timeout instead of the spec's 30 s |
| `Client/SendSpinClient.cs:815-819` | `ValidateActivateAdmissibility` returns `true` unconditionally |
| `Client/ServerArbitration.cs:43-46` | `FromConnectionReason`, the pre-encryption ranking |

The admissibility bypass is the security-relevant one: with no session info, the spec's
`server/activate` admissibility table never runs.

### The deeper defect on the dial path

`NoiseWireFraming` implements both `IWireFraming` and `INoiseSessionInfo`, which is why
`SendSpinHostService` passes `noiseSession: framing`. `SendspinConnection` already calls
`_framing.Reset()` and `_framing.Start()` on connect, so the client-dials-server direction is
mechanically ready for Noise.

But **no production code ever constructs a `NoiseWireFraming` for that direction.** The only
production construction site is `SendSpinHostService`. On the dial path an application must
hand-assemble identity → resolver → framing → connection → client service, and separately remember
to pass `noiseSession: framing`. Omitting that last step silently yields the legacy protocol.

Removing the plaintext default is therefore not sufficient on its own. The construction seam has to
make the correct assembly the only assembly.

## 3. Architecture

### 3.1 `SendspinClientOptions`

`SendspinClientService`'s constructor currently takes eleven optional parameters. Replace the tail
with one required options object:

```csharp
public sealed class SendspinClientOptions
{
    /// <summary>Persistent Curve25519 identity. client_id is derived from it.</summary>
    public required SendspinIdentity Identity { get; init; }

    public IPairingRecordStore? PairingRecordStore { get; init; }
    public ClientCapabilities Capabilities { get; init; } = new();
    public NoiseCipherSuite Suite { get; init; } = NoiseCipherSuite.ChaChaPoly;

    // Carried over verbatim, still optional:
    public IClockSynchronizer? ClockSynchronizer { get; init; }
    public IAudioPipeline? AudioPipeline { get; init; }
    public IStaticDelayStore? StaticDelayStore { get; init; }
    public IPinLockoutStore? PinLockoutStore { get; init; }
    public IAudioCaptureDevice? CaptureDevice { get; init; }
    public ISourceAudioEncoderFactory? SourceEncoderFactory { get; init; }
}
```

`SendspinClientService`'s signature becomes:

```csharp
public SendspinClientService(
    ILogger<SendspinClientService> logger,
    ISendspinConnection connection,
    INoiseSessionInfo session,          // non-nullable
    SendspinClientOptions options)
```

A static factory assembles the dial path in one call so the identity, framing, and session cannot
drift apart. It constructs the `NoiseWireFraming` from `options.Identity`, `options.Suite`, and a
`RecordPskResolver` over `options.PairingRecordStore`, hands that same instance to
`SendspinConnection` as its framing and to `SendspinClientService` as its session:

```csharp
public static SendspinClientService CreateForDial(
    ILoggerFactory loggerFactory,
    SendspinClientOptions options,
    ConnectionOptions? connectionOptions = null);
```

This is the seam [#85](https://github.com/Sendspin/sendspin-dotnet/issues/85) needs for pairing
initiation and trust-level surfacing. Those API additions are not in this change; only the seam is.

### 3.1.1 `SendSpinHostService`

The listen path takes the same options object. `SendSpinHostService`'s `identity`, `capabilities`,
`pairingRecordStore`, `pinLockoutStore`, `audioPipeline`, and `clockSynchronizer` parameters collapse
into a required `SendspinClientOptions`. Its host-specific parameters — `listenerOptions`,
`advertiserOptions`, `lastPlayedServerId`, `lastPlayedServerStore` — stay as they are. It continues
to build one `NoiseWireFraming` per incoming connection — the per-connection Noise state cannot be
shared — but now does so unconditionally rather than only when `_identity is not null`.

This is a breaking change to the constructor used by `tools/interop/InteropClient`, which is updated
with it.

### 3.2 Deletions

| Target | Rationale |
|---|---|
| `Connection/Framing/PlaintextWireFraming.cs` and `tests/.../PlaintextWireFramingTests.cs` | Only two production references, both `??` defaults |
| Optional `IWireFraming? framing` parameters on `SendSpinConnection` and `IncomingConnection` | Framing is required |
| `SendSpinClient.cs:224-231` legacy `client/hello`-first branch | The encrypted flow is server-driven, always |
| `SendSpinClient.cs:239` `_noiseSession is null ? 10 : 30` timeout fork | Always 30 s, per spec |
| `ValidateActivateAdmissibility`'s `psk is null → return true` bypass | The gate must always run |
| `ServerArbitration.FromConnectionReason` and its call sites | Activities ranking only |
| `SendSpinHostService.cs:395-401` `_identity is null` → null framing branch | Identity is required |

`ClientHelloMessage`'s dual shape (a null `clientId` marking the encrypted shape) collapses to the
single encrypted shape: `client_id` and `version` are always omitted, because they travel in
`client/init`.

`IWireFraming`'s doc comment loses its `PlaintextWireFraming` reference; the interface itself is
unchanged and remains the encryption seam.

## 4. Handshake failure handling (#83)

The two connection directions fail differently, and the reconnect storm exists on only one:

- **Dial path** (`SendspinConnection`): owns the `AutoReconnect` loop. A framing `FatalReason`
  currently calls `HandleConnectionLostAsync()`, identical to a dropped socket, so a permanent
  condition retries forever at 1 s → 30 s.
- **Listen path** (`IncomingConnection` via `SendSpinHostService`): no reconnect loop. This is the
  path Music Assistant and windowsSpin use. A legacy server that dials us receives `client/init`
  from `Start()` and closes. No storm, but today no diagnostic either.

### 4.1 Classification

Plan §6 measured the legacy signature against aiosendspin 6.1.1: a pre-7.0 server that receives
`client/init` closes with code **1000 and no reply**.

| Signal | Kind | Behavior |
|---|---|---|
| Clean close (1000) after `client/init`, before `server/init` | `LegacyServer` — permanent | Never retry; surface typed exception |
| Framing `FatalReason` (bad PSK, unsupported suite, version mismatch, malformed handshake) | `HandshakeRejected` — permanent | Never retry; surface typed exception |
| Socket drop or timeout mid-handshake, any other close code | Ambiguous | Retry on the handshake backoff |

### 4.2 New public surface

```csharp
public enum HandshakeFailureKind { LegacyServer, HandshakeRejected }

public sealed class SendspinHandshakeException : Exception
{
    public HandshakeFailureKind Kind { get; }
}
```

```csharp
// ConnectionOptions, one new property:
/// <summary>
/// Delay before retrying after an ambiguous handshake failure. Handshake failures are not
/// ordinary socket drops, so they back off separately from ReconnectDelayMs.
/// </summary>
public int HandshakeFailureBackoffMs { get; set; } = 30000;
```

The `LegacyServer` message is the diagnostic plan §6 costed Option A on:

> Server closed the connection during `client/init` and does not support Sendspin encryption.
> Upgrade the server to `aiosendspin >= 7.0.0`, or pin Sendspin SDK 9.x.

Permanent failures set state to `Disconnected`, raise the exception through the existing error
channel, and do not re-enter `AutoReconnect`. On the listen path the same classification logs the
same message at `Warning`, per connection.

### 4.3 Deliberately not added

No `MaxHandshakeAttempts`, no `EncryptionMode`, no fallback policy. Each would be a partial
reintroduction of Option B. A configurable retry ceiling on a permanent failure is a setting whose
only correct value is "do not retry", so that is encoded as behavior rather than configuration. The
single surviving knob covers the genuinely ambiguous case.

## 5. Testing

### 5.1 Migration

`FakeNoiseSession`, currently private to `SendspinClientServiceEncryptedFlowTests`, is promoted to a
shared helper:

```csharp
internal static class TestClient
{
    internal static (SendspinClientService Client, FakeSendspinConnection Connection, FakeNoiseSession Session)
        Create(
            PskCategory category = PskCategory.LongTerm,   // trust "user" by default
            bool unpairedAccess = false,
            Action<SendspinClientOptions>? configure = null);
}
```

The default is a paired, `LongTerm`-keyed session at trust `user`: the normal operating state, so
role tests are not incidentally blocked by admissibility or the `source@v1` trust gate. Tests that
exercise gating opt into `PskCategory.Sentinel` explicitly.

Approximately fifty client constructions across ten test files migrate to this helper. The four
`new SendspinConnection(...)` sites in `SendspinConnectionReconnectTests.cs` gain a framing argument.
`PlaintextWireFramingTests.cs` is deleted.

### 5.2 New coverage

1. `client/init` followed by a clean 1000 close before `server/init` yields
   `SendspinHandshakeException(LegacyServer)`, **and `AutoReconnect` does not fire**.
2. A framing `FatalReason` yields `HandshakeRejected` with no retry.
3. An ambiguous mid-handshake drop retries after `HandshakeFailureBackoffMs`, not
   `ReconnectDelayMs`.
4. **Admissibility cannot be bypassed**: with the null-session branch gone, a Sentinel-keyed session
   receiving an inadmissible `server/activate` always disconnects with the spec's reason. This is
   the regression test for the security hole #78 identified.
5. Arbitration ranks purely on activities; `FromConnectionReason` no longer exists.

### 5.3 Execution risk

The test migration is not purely mechanical. Those fifty constructions currently run with
`_noiseSession is null`, which both skips the admissibility gate and takes the
client-sends-`client/hello`-first flow. Under the encrypted flow the handshake is server-driven and
completes on `server/activate`, so tests that never send one may sit in a different handshake state.
Expect a tail of genuine behavioral fixes, not only constructor edits.

Mitigation: migrate file by file, with the full suite green at each step, rather than one big-bang
commit.

## 6. Scope

**Closes:** #78, #83.

**Also in scope:** updating `docs/encryption-and-linein-plan.md` §6 to record the confirmed
decision, replacing "no decision made yet".

**Not in scope:**

| Deferred | Reason |
|---|---|
| #85's API additions — pairing initiation, `TrustLevel`, `ClientRoles`, `ClientId` naming | This change provides their seam; the additions are their own PR |
| #74, #75, #87 security fixes | Independent of the clean break |
| #77 `client/state` → boolean `available` | Independent conformance fix |
| #79 pairing token format | Blocked on the upstream version-0-vs-1 answer |
| #91 version bump, `MIGRATION-10.0.0.md`, README compatibility matrix | Release engineering lands as one coherent change |
| #92 spec #122 audit | Separate; its re-handshake deltas belong with #81 |

The package version stays at `9.1.0` in this change. #91 owns the bump to `10.0.0`.

## 7. Success criteria

1. `PlaintextWireFraming` does not exist, and no production type can be constructed into a
   plaintext-speaking state.
2. `SendspinClientService` cannot be constructed without an identity and a Noise session.
3. `ValidateActivateAdmissibility` has no bypass path.
4. Pointing the SDK at an aiosendspin 6.1.1 server produces `SendspinHandshakeException(LegacyServer)`
   with the documented message, and exactly one connection attempt.
5. The full test suite is green, including the five new cases in §5.2.
6. The interop workflow still passes against `aiosendspin[server]==7.0.0`.
