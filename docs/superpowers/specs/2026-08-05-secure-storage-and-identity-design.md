# Design: Secure local storage and identity

*Closes [#84](https://github.com/Sendspin/sendspin-dotnet/issues/84) and [#86](https://github.com/Sendspin/sendspin-dotnet/issues/86). Slice A of the #84/#85/#86/#88 group; slices B (public API surface) and C (hostile-input hardening) follow.*

## 1. Why these two together

Both are about persisting secrets to disk, and #86's own text says so: "The identity store (#84) needs the same permissions + atomic-write treatment." Building that treatment once, as a shared primitive, is the reason to do them in one pass rather than two.

### Two findings from exploration that changed the shape of the work

**1. The SDK's other persistence seams ship interface-only.** `FileStaticDelayStore` and `FileLastPlayedServerStore` do not exist in `src/` — they are *example implementations in the NuGet README* (`src/Sendspin.SDK/README.md:305` and `:348`). `FilePairingRecordStore` is the single file-backed store the SDK actually ships, and it is the one #86 is about. The one time this codebase shipped a file store for secrets, it shipped it carelessly.

**Decision: ship a hardened file default for the identity anyway.** Unlike static delay, the identity is *mandatory* — `required` on `SendspinClientOptions` since #94 — and `client_id` **is** the Curve25519 public key, which the spec requires to survive reboots. Interface-only would mean every consumer hand-rolls private-key persistence. The mitigation for the precedent is not to ship nothing; it is to ship one that is correct, which is what the shared primitive is for.

**2. `InMemoryPinLockoutStore` is not the default, and the real gap is worse than #86 describes.** `_pinLockoutStore = options.PinLockoutStore` is nullable and nothing installs a default. With no store, `IsPinMethodLockedOut` evaluates `(null?.GetFailures(method) ?? 0) >= 10` — always false — and `RecordPinFailure` returns early. So a client that configures `PinPairingMethods` without a lockout store gets **unlimited PIN attempts**, not merely counters that reset on restart. `InMemoryPinLockoutStore`'s own docstring is already honest; the "counters survive reboots" text #86 quotes is on the *interface*, stating the spec requirement.

## 2. The shared primitive

```csharp
internal static class SecureFile
{
    /// <summary>
    /// Writes atomically: temp file, flush, restrict permissions, then move over the target.
    /// A crash mid-write leaves the previous file intact rather than a truncated one.
    /// </summary>
    internal static void WriteAllTextAtomic(string path, string contents);

    /// <summary>Reads the file, or returns null if it does not exist.</summary>
    internal static string? ReadAllTextOrNull(string path);
}
```

`WriteAllTextAtomic` creates the parent directory, writes `<path>.tmp`, flushes to disk, sets permissions, then `File.Move(tmp, path, overwrite: true)`.

### 2.1 The Windows constraint

`File.SetUnixFileMode` is attributed `[UnsupportedOSPlatform("windows")]` and **throws `PlatformNotSupportedException`** on Windows. #86's suggested fix — `File.SetUnixFileMode(UserRead | UserWrite)` — applied literally would crash every Windows client on its first save, and Windows is the primary consumer platform (windowsSpin).

The call therefore sits behind `if (!OperatingSystem.IsWindows())`, which also satisfies the CA1416 platform analyzer this repo has enabled.

On Windows the file inherits the parent directory's ACL. Rather than reaching for `FileSystemAclExtensions`, the README guidance becomes: place the file under `%LOCALAPPDATA%`, where the per-user ACL already restricts access. Documented, not silently weaker.

### 2.2 What this primitive does *not* do

It does not touch serialization. `FilePairingRecordStore.Save()` becomes `SecureFile.WriteAllTextAtomic(_path, JsonSerializer.Serialize(entries))` — the `JsonSerializer` call is unchanged, so the reflection-based JSON that trips IL2026/IL3050 stays wholly within #89's scope rather than being half-fixed here.

## 3. The identity seam (#84)

```csharp
public interface ISendspinIdentityStore
{
    /// <summary>
    /// The persisted identity blob, or null on first run. Opaque — the SDK owns its
    /// format; implementations only store and return the bytes.
    /// </summary>
    byte[]? Load();

    /// <summary>
    /// Persists the identity blob. Called once, when a new identity is generated.
    /// The blob contains a private key: protect it as a secret.
    /// </summary>
    void Save(byte[] identityBlob);
}

public sealed class FileSendspinIdentityStore : ISendspinIdentityStore   // built on SecureFile
```

Follows the established `IStaticDelayStore` / `ILastPlayedServerStore` idiom: `Load()`/`Save()`, embedder-supplied, non-throwing on the happy path.

**The blob is opaque deliberately, and it is what makes §3.1 possible.** An earlier shape of this interface passed `SendspinIdentity` directly — which contradicts making `PrivateKey` internal, because then an external implementation could not serialize what it was handed. A platform store (DPAPI, Keychain, Android keystore) wants to protect a byte blob, not understand a key format, so opaque bytes are both the enabling choice and the idiomatic one. The SDK also gets to version the blob format without touching the interface.

### 3.0 The store resolves *into* `Identity`, not beside it

`SendspinClientOptions.Identity` is `required` — #94 made it so deliberately, for a compile-time guarantee that no client exists without an identity. So an `IdentityStore` option sitting *beside* it would contradict that: a `required` property must be set at construction, making "neither supplied" unrepresentable and any store-fallback branch unreachable.

`SendspinClientOptions` therefore gains **no new property**. Instead a static factory resolves the store into an identity, used inside the initializer:

```csharp
/// <summary>
/// Loads the identity from <paramref name="store"/>, generating and persisting a new one
/// on first run. The returned identity's PeerId is stable across restarts.
/// </summary>
public static SendspinIdentity FromStore(ISendspinIdentityStore store);
```

```csharp
var options = new SendspinClientOptions
{
    Identity = SendspinIdentity.FromStore(new FileSendspinIdentityStore(path)),
    // ...
};
```

`Identity` stays `required`, nothing becomes nullable, and there is no resolution order to get wrong — a consumer either hands over an identity or hands over a store that produces one, and both paths end at the same required property.

### 3.1 `PrivateKey` becomes `internal`, `FromKeys` stays public

`SendspinIdentity.PrivateKey` (`SendspinIdentity.cs:13`) is `public`, which actively invites consumers to extract raw X25519 private key bytes to hand-roll persistence — exactly what #84 objects to. With the store seam and an opaque blob, no consumer needs it. `InternalsVisibleTo` already covers the test assembly and `NoiseWireFraming` is in the same assembly, so nothing else breaks.

`SendspinIdentity.FromKeys(privateKey, publicKey)` (`:41`) **stays public**, deliberately. The asymmetry is the point: you can bring your own key bytes *in*, but you cannot get the SDK's bytes *out*. That also preserves the migration path — a consumer who has been persisting `PrivateKey`/`PublicKey` by hand upgrades by reading their existing store and calling `FromKeys`, then hands the SDK an `IdentityStore` going forward. Removing `FromKeys` would strand them with persisted bytes and no way to load them.

Making `PrivateKey` internal is a breaking public-API change, correct for the v10 window.

## 4. `FilePairingRecordStore` hardening (#86 items 1–4)

**Items 1 and 2** are the primitive: `Save()` routes through `SecureFile.WriteAllTextAtomic`. Truncate-then-write and default 0644 both go away.

### 4.1 Item 3 — the corrupt-file brick, in two tiers

#86 offers "quarantine and log" or "fail with an actionable error". A typed exception is a better *message* for the same brick — a headless speaker still cannot start. So: **degrade, and degrade granularly.**

- A malformed **individual entry** (bad base64url, unrecognised `PskCategory`) is **skipped and logged at `Warning`**, keeping every other record. Today a single bad byte discards all of them, because `Enum.Parse` / `Base64UrlText.Decode` throw out of the constructor.
- An **unparseable document** is **quarantined** — moved to `<path>.corrupt-<utc-timestamp>` — logged at `Error`, and the store starts empty.

Both paths fail safe. Missing records mean trust drops to `none`, which after #101 means no `source@v1` streaming and no `pairing_psk` (that now requires a Pairing-keyed session). The user re-pairs; the device boots. The quarantined file is preserved for diagnosis rather than deleted.

This means `FilePairingRecordStore` needs an `ILogger` — it currently takes only a path. New optional constructor parameter defaulting to `NullLogger.Instance`.

### 4.2 Item 4 — no change, with the reasoning recorded

#86 describes "synchronous disk IO on the crypto receive path: `Resolve` → `Upsert` → `Save()` → `File.WriteAllText` runs inside `ProcessInbound`, stalling the receive loop."

**That is mostly already fixed by #101**, which made `RecordPskResolver.Resolve` a pure lookup. The write is no longer inside `ProcessInbound` at the framing layer. What remains is `MarkMatchedPskUsed` → `Upsert` → `Save` running synchronously in `OnTextMessageReceived` — one layer up, and now **once per session** (guarded by `_markedPskUsed`) plus once on pairing completion, rather than once per handshake.

A once-per-session write of a small file is not a receive-loop stall worth async machinery. **No change**, and #86 item 4 closes with this reasoning rather than silently.

## 5. The PIN lockout gate (#86 item 6)

### 5.1 Fail closed

`CanOffer`'s PIN arms — the seam #101 introduced — gain one clause each:

```csharp
"dynamic_pin" => _capabilities.PinPairingMethods.Contains("dynamic_pin")
                 && _pinLockoutStore is not null,
"static_pin"  => _capabilities.PinPairingMethods.Contains("static_pin")
                 && _pinLockoutStore is not null,
```

A client that opted into PIN pairing without a lockout store now refuses the method with `pair/abort { method_not_supported }`. The connection stays open (#76), the server may try another method, and the misconfiguration is visible in logs instead of silently granting unlimited attempts.

### 5.2 Make correct configuration easy

Ship `FilePinLockoutStore` on `SecureFile`. Lockout counters are not secrets, but they are security *state* — an attacker who can rewrite the file resets the counter — so the same restrictive permissions apply.

## 6. Testing

The Unix-permission assertions are genuinely exercised despite the dev platform being Windows: `.github/workflows/build.yml` runs on `ubuntu-latest`. A runtime-conditional assertion (`if (!OperatingSystem.IsWindows())` assert the mode, else assert no throw) runs on the platform that matters in CI, and avoids a skip attribute that would quietly never run.

| Area | Test | The assertion that matters |
|---|---|---|
| `SecureFile` | write then read back | content correct; **no `.tmp` left behind** |
| `SecureFile` | overwrite existing | old content replaced |
| `SecureFile` | permissions | Unix: mode is `UserRead\|UserWrite`. Windows: no throw |
| `SecureFile` | parent directory missing | created, no exception |
| Identity | `FromStore` against an empty store | identity generated **and persisted** |
| Identity | `FromStore` twice against one store | **same `PeerId`** — the whole point |
| Identity | `FromStore` against a store holding a corrupt blob | throws a typed, actionable error naming the store |
| Identity | round-trip through `FileSendspinIdentityStore` | `PeerId` and private key both survive |
| Records | one malformed entry among three | **the other two survive** |
| Records | unparseable document | store empty, `<path>.corrupt-*` **exists**, original gone |
| PIN gate | `PinPairingMethods` set, no lockout store | `pair/abort method_not_supported`, **no `client/pair-init`**, connection still open |
| PIN gate | **positive control**: same, with a store | `client/pair-init` **is** sent |
| `FilePinLockoutStore` | set 3, reload | reads 3 |

The PIN positive control is not optional: the two clauses added to `CanOffer` could refuse PIN entirely and every negative test would still pass. That exact failure mode cost two review rounds on the #101 branch.

## 7. Scope

**Closes #84 and #86.**

**Not in scope:**

| Deferred | Reason |
|---|---|
| Key zeroization in `NoiseWireFraming` / CPace | Memory hygiene, not storage; touches live handshake and PAKE code. Filed as **#102** |
| #89 AOT/trimming | §2.2 — serialization is untouched, so #89's scope stays whole |
| #85 public API surface | Slice B. This slice *unblocks* its item 4 (`ClientId` derived from the identity) but does not do it |
| #88 hostile-input hardening | Slice C |
| #79, #91, #92 | Upstream-blocked and release engineering |

Package version stays `9.1.0` (#91 owns the bump).

## 8. Success criteria

1. An identity survives a process restart through `SendspinIdentity.FromStore(new FileSendspinIdentityStore(path))` — same `PeerId` on the second call, without `SendspinClientOptions.Identity` ceasing to be `required`.
2. `SendspinIdentity.PrivateKey` is not publicly accessible.
3. A crash mid-save cannot leave a truncated record file — the write is atomic.
4. On Unix, both stores' files are mode `0600`; on Windows, saving does not throw.
5. One malformed record does not discard the others; an unparseable file is quarantined and the client still boots.
6. A client with PIN methods configured but no lockout store refuses PIN pairing rather than granting unlimited attempts, and the connection stays open.
7. Full suite green on `net10.0`; `dotnet build` clean on `net8.0` and `net10.0`; the interop workflow still passes against `aiosendspin[server]==7.0.0`.
