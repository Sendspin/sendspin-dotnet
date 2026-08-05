# Secure Storage and Identity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the SDK a safe way to persist secrets to disk — a Curve25519 identity that survives reboots, pairing records that survive a crash mid-write, and PIN lockout counters that actually enforce the spec's terminal lockout.

**Architecture:** One internal `SecureFile` primitive does atomic, permission-restricted writes; three stores are built on it. `SendspinIdentity.FromStore` resolves a store into the `required` `Identity` property rather than sitting beside it, so #94's compile-time guarantee survives. `SendspinIdentity.PrivateKey` becomes `internal`, which is possible only because the store interface persists an opaque blob.

**Tech Stack:** C# / .NET (`net8.0;net10.0` multi-target), xUnit, source-generated `System.Text.Json`.

## Global Constraints

- Design of record: `docs/superpowers/specs/2026-08-05-secure-storage-and-identity-design.md`. Where this plan and the design disagree, **the design governs** — report the discrepancy rather than silently picking one.
- Target frameworks `net8.0;net10.0`. Code must compile on both.
- Nullable reference types are enabled and these are errors: `CS8600;CS8601;CS8602;CS8603;CS8604;CS8618;CS8625`.
- Package version stays `9.1.0`. The bump is issue #91, out of scope.
- **`File.SetUnixFileMode` throws `PlatformNotSupportedException` on Windows** and is attributed `[UnsupportedOSPlatform("windows")]`. Every call must sit behind `if (!OperatingSystem.IsWindows())` or the CA1416 analyzer fails and Windows clients crash on first save. Windows is the primary consumer platform.
- **Do not touch `JsonSerializer` calls.** The atomic-write work changes how bytes reach disk, not how objects become JSON. The reflection-based JSON in `FilePairingRecordStore` belongs to issue #89; leaving it alone keeps #89's scope whole.
- **Zeroization is out of scope** — filed as #102. Do not add `CryptographicOperations.ZeroMemory` calls to `NoiseWireFraming` or `CPace`.
- Commit messages must contain no AI attribution, no `Co-Authored-By`, and no self-reference. Write as the repo owner.
- Baseline entering this plan: **348 tests passing, 0 failing** on `net10.0`.
- **Express test-count gates as deltas, not absolute totals**, and record the absolute figure observed. Zero failing is the invariant.
- Full test command: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo`
- Filtered: append `--filter "FullyQualifiedName~<ClassName>"`
- Run `dotnet test` in the **foreground**. Backgrounded test processes have held file locks in this repo and blocked later rebuilds.
- Branch `feat/secure-storage-and-identity`, already created from `main` @ `12b0e4b`.

## File Structure

**Created:**

| File | Responsibility |
|---|---|
| `src/Sendspin.SDK/Connection/Noise/SecureFile.cs` | `internal static` — atomic, permission-restricted file write and a tolerant read |
| `src/Sendspin.SDK/Connection/Noise/ISendspinIdentityStore.cs` | The identity persistence seam plus its file-backed implementation |
| `src/Sendspin.SDK/Connection/Noise/Pairing/FilePinLockoutStore.cs` | File-backed PIN lockout counters |
| `tests/Sendspin.SDK.Tests/Connection/SecureFileTests.cs` | Primitive behavior, including the platform-conditional permission assertion |
| `tests/Sendspin.SDK.Tests/Connection/IdentityStoreTests.cs` | `FromStore` semantics and file round-trip |
| `tests/Sendspin.SDK.Tests/Connection/PairingRecordStoreResilienceTests.cs` | Malformed-entry and corrupt-document handling |

**Modified:**

| File | Change |
|---|---|
| `src/Sendspin.SDK/Connection/Noise/SendspinIdentity.cs` | `PrivateKey` → `internal`; add `FromStore` |
| `src/Sendspin.SDK/Connection/Noise/PairingRecordStore.cs` | Atomic writes; two-tier corruption handling; `ILogger` parameter |
| `src/Sendspin.SDK/Client/SendSpinClient.cs` | `CanOffer` PIN arms require a lockout store |
| `src/Sendspin.SDK/README.md` | Identity-store usage; the Windows ACL guidance |
| `tests/Sendspin.SDK.Tests/Client/SendspinClientServicePinPairingTests.cs` | Lockout-store gate tests |

---

### Task 1: The `SecureFile` primitive

**Files:**
- Create: `src/Sendspin.SDK/Connection/Noise/SecureFile.cs`
- Test: `tests/Sendspin.SDK.Tests/Connection/SecureFileTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `internal static class SecureFile` with `internal static void WriteAllTextAtomic(string path, string contents)` and `internal static string? ReadAllTextOrNull(string path)`. Tasks 2, 3 and 4 all build on it.

- [ ] **Step 1: Write the failing tests**

Create `tests/Sendspin.SDK.Tests/Connection/SecureFileTests.cs`:

```csharp
using Sendspin.SDK.Connection.Noise;

namespace Sendspin.SDK.Tests.Connection;

public class SecureFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "sendspin-securefile-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void WriteAllTextAtomic_RoundTrips_AndLeavesNoTempFile()
    {
        string path = Path.Combine(_dir, "data.json");

        SecureFile.WriteAllTextAtomic(path, """{"hello":"world"}""");

        Assert.Equal("""{"hello":"world"}""", File.ReadAllText(path));
        // A lingering .tmp means the move did not happen and a later run could read a stale half-write.
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void WriteAllTextAtomic_Overwrites_RatherThanAppending()
    {
        string path = Path.Combine(_dir, "data.json");

        SecureFile.WriteAllTextAtomic(path, "first");
        SecureFile.WriteAllTextAtomic(path, "second");

        Assert.Equal("second", File.ReadAllText(path));
    }

    [Fact]
    public void WriteAllTextAtomic_CreatesMissingParentDirectory()
    {
        string path = Path.Combine(_dir, "nested", "deeper", "data.json");

        SecureFile.WriteAllTextAtomic(path, "x");

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void WriteAllTextAtomic_RestrictsPermissions_WhereThePlatformSupportsIt()
    {
        string path = Path.Combine(_dir, "secret.json");

        SecureFile.WriteAllTextAtomic(path, "psk");

        if (OperatingSystem.IsWindows())
        {
            // File.SetUnixFileMode throws PlatformNotSupportedException on Windows, so the
            // only thing to assert here is that the write completed without one. The file
            // inherits the parent directory's ACL.
            Assert.True(File.Exists(path));
        }
        else
        {
            // CI runs ubuntu-latest, so this branch is the one that actually gates.
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    [Fact]
    public void ReadAllTextOrNull_ReturnsNull_WhenAbsent()
    {
        Assert.Null(SecureFile.ReadAllTextOrNull(Path.Combine(_dir, "missing.json")));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo --filter "FullyQualifiedName~SecureFileTests"`

Expected: FAIL to compile — `SecureFile` does not exist.

- [ ] **Step 3: Implement the primitive**

Create `src/Sendspin.SDK/Connection/Noise/SecureFile.cs`:

```csharp
namespace Sendspin.SDK.Connection.Noise;

/// <summary>
/// File persistence for local secrets: atomic replacement plus restrictive permissions
/// where the platform supports them.
/// </summary>
/// <remarks>
/// Atomic replacement matters because these files hold credentials. A truncate-then-write
/// that is interrupted leaves a corrupt file and loses every record in it; writing to a
/// temp file and moving it over the target leaves the previous contents intact instead.
/// <para>
/// Permissions are set only on platforms that have them. <c>File.SetUnixFileMode</c> throws
/// <see cref="PlatformNotSupportedException"/> on Windows, where the file instead inherits
/// its parent directory's ACL — so place these files somewhere already user-scoped, such as
/// <c>%LOCALAPPDATA%</c>.
/// </para>
/// </remarks>
internal static class SecureFile
{
    /// <summary>
    /// Writes <paramref name="contents"/> to <paramref name="path"/>, replacing any existing
    /// file atomically and restricting the result to owner-only access where supported.
    /// Creates the parent directory if needed.
    /// </summary>
    internal static void WriteAllTextAtomic(string path, string contents)
    {
        string full = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(full)
            ?? throw new ArgumentException($"path has no directory component: {path}", nameof(path));
        Directory.CreateDirectory(directory);

        string temp = full + ".tmp";
        File.WriteAllText(temp, contents);
        RestrictToOwner(temp);

        // Move last: until this succeeds, the previous file is still the valid one.
        File.Move(temp, full, overwrite: true);
    }

    /// <summary>
    /// Returns the file's contents, or <c>null</c> when it does not exist. Genuine IO
    /// failures still throw.
    /// </summary>
    internal static string? ReadAllTextOrNull(string path) =>
        File.Exists(path) ? File.ReadAllText(path) : null;

    private static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // No Unix modes here; the file inherits the parent directory's ACL.
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo --filter "FullyQualifiedName~SecureFileTests"`

Expected: PASS, 5 tests.

- [ ] **Step 5: Run the full suite and commit**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo`

Expected: 0 failing, total up by 5.

```bash
git add src/Sendspin.SDK/Connection/Noise/SecureFile.cs tests/Sendspin.SDK.Tests/Connection/SecureFileTests.cs
git commit -m "feat(storage): add an atomic, permission-restricted file primitive

Local secrets need replacement that survives an interrupted write and
owner-only permissions where the platform has them. File.SetUnixFileMode throws
on Windows, so that call is guarded and the Windows path relies on the parent
directory's ACL instead.

Groundwork for #84 and #86."
```

---

### Task 2: The identity store (#84)

**Files:**
- Create: `src/Sendspin.SDK/Connection/Noise/ISendspinIdentityStore.cs`
- Modify: `src/Sendspin.SDK/Connection/Noise/SendspinIdentity.cs` — `PrivateKey` (`:13`), add `FromStore`
- Test: `tests/Sendspin.SDK.Tests/Connection/IdentityStoreTests.cs`

**Interfaces:**
- Consumes: `SecureFile.WriteAllTextAtomic` / `ReadAllTextOrNull` from Task 1.
- Produces: `public interface ISendspinIdentityStore { byte[]? Load(); void Save(byte[] identityBlob); }`; `public sealed class FileSendspinIdentityStore(string path) : ISendspinIdentityStore`; `public static SendspinIdentity SendspinIdentity.FromStore(ISendspinIdentityStore store)`. No later task depends on these.

- [ ] **Step 1: Write the failing tests**

Create `tests/Sendspin.SDK.Tests/Connection/IdentityStoreTests.cs`:

```csharp
using Sendspin.SDK.Connection.Noise;

namespace Sendspin.SDK.Tests.Connection;

public class IdentityStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "sendspin-identity-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Records what the SDK handed it, so the blob stays opaque to the test too.</summary>
    private sealed class MemoryIdentityStore : ISendspinIdentityStore
    {
        internal byte[]? Blob;
        internal int SaveCount;

        public byte[]? Load() => Blob;

        public void Save(byte[] identityBlob)
        {
            Blob = identityBlob;
            SaveCount++;
        }
    }

    [Fact]
    public void FromStore_OnFirstRun_GeneratesAndPersists()
    {
        var store = new MemoryIdentityStore();

        var identity = SendspinIdentity.FromStore(store);

        Assert.NotNull(identity.PeerId);
        Assert.Equal(43, identity.PeerId.Length);
        Assert.Equal(1, store.SaveCount);
        Assert.NotNull(store.Blob);
    }

    [Fact]
    public void FromStore_Twice_YieldsTheSameIdentity()
    {
        // This is the whole point of the seam: client_id must survive a restart.
        var store = new MemoryIdentityStore();

        string first = SendspinIdentity.FromStore(store).PeerId;
        string second = SendspinIdentity.FromStore(store).PeerId;

        Assert.Equal(first, second);
        Assert.Equal(1, store.SaveCount);   // the second call loads, it does not re-save
    }

    [Fact]
    public void FromStore_OnCorruptBlob_ThrowsSomethingActionable()
    {
        var store = new MemoryIdentityStore { Blob = [1, 2, 3] };

        var ex = Assert.ThrowsAny<Exception>(() => SendspinIdentity.FromStore(store));

        // The message must name what failed, not surface a bare FormatException from a decoder.
        Assert.Contains("identity", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FileSendspinIdentityStore_RoundTripsAcrossInstances()
    {
        string path = Path.Combine(_dir, "identity.json");

        string first = SendspinIdentity.FromStore(new FileSendspinIdentityStore(path)).PeerId;
        string second = SendspinIdentity.FromStore(new FileSendspinIdentityStore(path)).PeerId;

        Assert.Equal(first, second);
    }

    [Fact]
    public void FileSendspinIdentityStore_Load_ReturnsNull_WhenAbsent()
    {
        Assert.Null(new FileSendspinIdentityStore(Path.Combine(_dir, "nope.json")).Load());
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo --filter "FullyQualifiedName~IdentityStoreTests"`

Expected: FAIL to compile — `ISendspinIdentityStore`, `FileSendspinIdentityStore` and `FromStore` do not exist.

- [ ] **Step 3: Create the interface and file implementation**

Create `src/Sendspin.SDK/Connection/Noise/ISendspinIdentityStore.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sendspin.SDK.Connection.Noise;

/// <summary>
/// Persistence seam for the client's long-lived Curve25519 identity. Mirrors
/// <see cref="Sendspin.SDK.Client.IStaticDelayStore"/> and
/// <see cref="Sendspin.SDK.Client.ILastPlayedServerStore"/>.
/// </summary>
/// <remarks>
/// The spec requires <c>client_id</c> — which IS the base64url public key — to survive
/// reboots, so an identity that is not persisted changes the client's identity on every
/// restart. Because the SDK is a library and cannot choose a storage location, the embedder
/// supplies this (file, DPAPI, Keychain, Android keystore) and passes the result to
/// <see cref="SendspinIdentity.FromStore"/>.
/// <para>
/// The blob is opaque: the SDK owns its format, and an implementation only stores and
/// returns bytes. That is what platform key stores want, and it is what allows the raw
/// private key to stay internal to the SDK.
/// </para>
/// <para><b>The blob contains a private key. Protect it as a secret.</b></para>
/// </remarks>
public interface ISendspinIdentityStore
{
    /// <summary>The persisted identity blob, or <c>null</c> on first run.</summary>
    byte[]? Load();

    /// <summary>Persists the identity blob. Called once, when a new identity is generated.</summary>
    void Save(byte[] identityBlob);
}

/// <summary>
/// JSON-file-backed identity store, written atomically and restricted to owner-only access
/// where the platform supports it.
/// </summary>
/// <remarks>
/// On Windows the file inherits its parent directory's ACL, so place it somewhere already
/// user-scoped such as <c>%LOCALAPPDATA%</c>. For hardware-backed protection, supply a
/// platform implementation of <see cref="ISendspinIdentityStore"/> instead.
/// </remarks>
public sealed class FileSendspinIdentityStore : ISendspinIdentityStore
{
    private readonly string _path;

    /// <summary>Creates a store backed by the given file path.</summary>
    public FileSendspinIdentityStore(string path) => _path = path;

    /// <inheritdoc/>
    public byte[]? Load()
    {
        string? text = SecureFile.ReadAllTextOrNull(_path);
        return text is null ? null : Convert.FromBase64String(text);
    }

    /// <inheritdoc/>
    public void Save(byte[] identityBlob) =>
        SecureFile.WriteAllTextAtomic(_path, Convert.ToBase64String(identityBlob));
}
```

- [ ] **Step 4: Add `FromStore` and make `PrivateKey` internal**

In `src/Sendspin.SDK/Connection/Noise/SendspinIdentity.cs`, change line 13 from `public` to `internal`:

```csharp
    /// <summary>
    /// Raw 32-byte X25519 private key. Internal by design: persist an identity through
    /// <see cref="ISendspinIdentityStore"/> rather than extracting key bytes.
    /// </summary>
    internal ReadOnlyMemory<byte> PrivateKey { get; }
```

Add the factory below `FromKeys`:

```csharp
    /// <summary>
    /// Loads the identity from <paramref name="store"/>, generating and persisting a new one
    /// on first run. The returned identity's <see cref="PeerId"/> is stable across restarts,
    /// which the spec requires of <c>client_id</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The store returned a blob this SDK cannot read.
    /// </exception>
    public static SendspinIdentity FromStore(ISendspinIdentityStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        if (store.Load() is { } blob)
        {
            // A blob is private + public key concatenated, in that order.
            if (blob.Length != NoiseConstants.KeySize * 2)
            {
                throw new InvalidOperationException(
                    $"stored Sendspin identity is {blob.Length} bytes; expected " +
                    $"{NoiseConstants.KeySize * 2}. The identity store may be corrupt.");
            }

            return FromKeys(
                blob.AsSpan(0, NoiseConstants.KeySize),
                blob.AsSpan(NoiseConstants.KeySize, NoiseConstants.KeySize));
        }

        var generated = Generate();
        byte[] fresh = new byte[NoiseConstants.KeySize * 2];
        generated.PrivateKey.Span.CopyTo(fresh);
        generated.PublicKey.Span.CopyTo(fresh.AsSpan(NoiseConstants.KeySize));
        store.Save(fresh);
        return generated;
    }
```

> `FromKeys` stays `public` deliberately — it is the migration path for a consumer who has been persisting key bytes by hand. The asymmetry is intended: bytes can come in, they cannot come out.

- [ ] **Step 5: Run the tests, then the full suite**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo --filter "FullyQualifiedName~IdentityStoreTests"`
Then: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo`

Expected: 0 failing, total up by 5 from Task 1's figure.

If making `PrivateKey` internal breaks a test or `tools/interop`, fix the caller to use `FromStore` or `FromKeys` — do **not** revert the visibility change, which is the point of #84.

- [ ] **Step 6: Commit**

```bash
git add src/Sendspin.SDK/Connection/Noise/ tests/Sendspin.SDK.Tests/Connection/IdentityStoreTests.cs
git commit -m "feat(identity)!: add ISendspinIdentityStore and make PrivateKey internal

The spec requires client_id — which is the Curve25519 public key — to survive
reboots, and the SDK previously offered no seam, so every consumer hand-rolled
private-key persistence. FromStore resolves a store into the required Identity
property, so the compile-time guarantee that a client always has an identity is
unaffected.

The store persists an opaque blob rather than a SendspinIdentity, which is what
platform key stores want and what allows PrivateKey to stop being public.

Closes #84."
```

---

### Task 3: Harden `FilePairingRecordStore` (#86 items 1-3)

**Files:**
- Modify: `src/Sendspin.SDK/Connection/Noise/PairingRecordStore.cs` — constructor (`~:73-88`), `Save` (`~:107-114`)
- Test: `tests/Sendspin.SDK.Tests/Connection/PairingRecordStoreResilienceTests.cs`

**Interfaces:**
- Consumes: `SecureFile` from Task 1.
- Produces: `FilePairingRecordStore(string path, ILogger? logger = null)`. No later task depends on it.

- [ ] **Step 1: Write the failing tests**

Create `tests/Sendspin.SDK.Tests/Connection/PairingRecordStoreResilienceTests.cs`:

```csharp
using Sendspin.SDK.Connection.Noise;

namespace Sendspin.SDK.Tests.Connection;

public class PairingRecordStoreResilienceTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "sendspin-records-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string WriteStoreFile(string json)
    {
        string path = Path.Combine(_dir, "records.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void OneMalformedEntry_DoesNotDiscardTheOthers()
    {
        // Previously Enum.Parse / Base64UrlText.Decode threw out of the constructor, so a
        // single bad byte lost every pairing the client had.
        string goodA = Base64UrlText.Encode(Enumerable.Repeat((byte)0x11, 32).ToArray());
        string goodB = Base64UrlText.Encode(Enumerable.Repeat((byte)0x22, 32).ToArray());
        string path = WriteStoreFile($$"""
            [
              {"Psk":"{{goodA}}","Category":"LongTerm","ServerId":"srv-a","Used":false},
              {"Psk":"not-valid-base64url!!","Category":"LongTerm","ServerId":"srv-bad","Used":false},
              {"Psk":"{{goodB}}","Category":"Pairing","ServerId":null,"Used":false}
            ]
            """);

        var store = new FilePairingRecordStore(path);

        Assert.Equal(2, store.List().Count);
    }

    [Fact]
    public void UnrecognisedCategory_SkipsOnlyThatEntry()
    {
        string good = Base64UrlText.Encode(Enumerable.Repeat((byte)0x33, 32).ToArray());
        string bad = Base64UrlText.Encode(Enumerable.Repeat((byte)0x44, 32).ToArray());
        string path = WriteStoreFile($$"""
            [
              {"Psk":"{{good}}","Category":"LongTerm","ServerId":"srv-a","Used":false},
              {"Psk":"{{bad}}","Category":"Telepathy","ServerId":"srv-b","Used":false}
            ]
            """);

        var store = new FilePairingRecordStore(path);

        Assert.Single(store.List());
    }

    [Fact]
    public void UnparseableDocument_IsQuarantined_AndTheStoreStillOpens()
    {
        // A device that cannot construct its store cannot boot. Quarantine, log, continue
        // empty: trust drops to 'none', which fails closed, and the user re-pairs.
        string path = WriteStoreFile("this is not json at all");

        var store = new FilePairingRecordStore(path);

        Assert.Empty(store.List());
        Assert.NotEmpty(Directory.GetFiles(_dir, "records.json.corrupt-*"));
        Assert.False(File.Exists(path), "the corrupt file should have been moved, not copied");
    }

    [Fact]
    public void Upsert_WritesAtomically_LeavingNoTempFile()
    {
        string path = Path.Combine(_dir, "records.json");

        var store = new FilePairingRecordStore(path);
        store.Upsert(new PairingRecord(Enumerable.Repeat((byte)0x55, 32).ToArray(), PskCategory.LongTerm, "srv"));

        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.Single(new FilePairingRecordStore(path).List());
    }
}
```

> Verified: `Base64UrlText` is `internal static` (`Base64UrlText.cs:6`), reachable from the test assembly through the existing `InternalsVisibleTo` — no change needed. The JSON property names are `Psk`, `Category`, `ServerId`, `Used`: `Entry` is the positional record `Entry(string Psk, string Category, string? ServerId, bool Used)`, `JsonSerializer` is called with no options so names are preserved as declared, and STJ deserialization is case-sensitive by default — so the PascalCase above is what actually round-trips.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo --filter "FullyQualifiedName~PairingRecordStoreResilienceTests"`

Expected: the three resilience tests FAIL — today the constructor throws on all three inputs. The atomic-write test may pass already (the file is written), but must still show no `.tmp`.

- [ ] **Step 3: Rewrite loading with two-tier tolerance**

In `src/Sendspin.SDK/Connection/Noise/PairingRecordStore.cs`, add `using Microsoft.Extensions.Logging;` and `using Microsoft.Extensions.Logging.Abstractions;`, then replace the constructor:

```csharp
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly Dictionary<string, PairingRecord> _records = new();

    /// <summary>
    /// Creates a store backed by the given file, loading existing records. A malformed
    /// individual record is skipped; a file that cannot be parsed at all is quarantined
    /// alongside itself and the store opens empty, so a single bad byte cannot stop the
    /// client from starting.
    /// </summary>
    public FilePairingRecordStore(string path, ILogger? logger = null)
    {
        _path = path;
        _logger = logger ?? NullLogger.Instance;

        string? text = SecureFile.ReadAllTextOrNull(path);
        if (text is null)
            return;

        List<Entry>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<Entry>>(text);
        }
        catch (JsonException ex)
        {
            Quarantine(ex);
            return;
        }

        foreach (var e in entries ?? [])
        {
            if (TryParse(e, out var record))
            {
                _records[record.PskId] = record;
            }
        }
    }

    private void Quarantine(Exception cause)
    {
        string target = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMddTHHmmssZ}";
        try
        {
            File.Move(_path, target, overwrite: true);
            _logger.LogError(cause,
                "Pairing record store at {Path} could not be parsed; moved to {Target}. " +
                "Starting with no records — the client will need to re-pair.", _path, target);
        }
        catch (IOException moveFailure)
        {
            _logger.LogError(moveFailure,
                "Pairing record store at {Path} could not be parsed and could not be moved aside. " +
                "Starting with no records.", _path);
        }
    }

    private bool TryParse(Entry entry, out PairingRecord record)
    {
        record = default!;
        try
        {
            record = new PairingRecord(
                Base64UrlText.Decode(entry.Psk),
                Enum.Parse<PskCategory>(entry.Category),
                entry.ServerId,
                entry.Used);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            _logger.LogWarning(ex,
                "Skipping a malformed pairing record for server {ServerId} in {Path}.",
                entry.ServerId ?? "(none)", _path);
            return false;
        }
    }
```

> Verified: `Enum.Parse<PskCategory>` throws `ArgumentException` for an unrecognised name, and `Base64UrlText.Decode` throws `FormatException` on both target frameworks — `Base64Url.DecodeFromChars` on net9+ and `Convert.FromBase64String` on net8 (`Base64UrlText.cs:17-26`). So `when (ex is FormatException or ArgumentException)` covers both cases exactly. Do not widen it to bare `Exception`: an `OutOfMemoryException` must not be silently reclassified as a malformed record.

- [ ] **Step 4: Route `Save` through the primitive**

Replace `Save`:

```csharp
    private void Save()
    {
        var entries = _records.Values
            .Select(r => new Entry(Base64UrlText.Encode(r.Psk.Span), r.Category.ToString(), r.ServerId, r.Used))
            .ToList();
        SecureFile.WriteAllTextAtomic(_path, JsonSerializer.Serialize(entries));
    }
```

The `JsonSerializer.Serialize` call is deliberately unchanged — the reflection-based JSON belongs to #89.

- [ ] **Step 5: Update the class doc comment**

The `<summary>` above `FilePairingRecordStore` says "protect it with filesystem permissions" without doing so. Replace it:

```csharp
/// <summary>
/// JSON-file-backed record store. The file contains raw PSKs; it is written atomically and
/// restricted to owner-only access where the platform supports it. On Windows it inherits
/// the parent directory's ACL, so place it somewhere already user-scoped such as
/// <c>%LOCALAPPDATA%</c>. For hardware-backed protection, supply a platform
/// <see cref="IPairingRecordStore"/> implementation instead (DPAPI, Keychain, keystore).
/// </summary>
```

- [ ] **Step 6: Run the tests, then the full suite**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo --filter "FullyQualifiedName~PairingRecordStore"`
Then: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo`

Expected: 0 failing, total up by 4 from Task 2's figure.

- [ ] **Step 7: Commit**

```bash
git add src/Sendspin.SDK/Connection/Noise/PairingRecordStore.cs tests/Sendspin.SDK.Tests/Connection/PairingRecordStoreResilienceTests.cs
git commit -m "fix(pairing): harden the file-backed record store

PSKs were written with a truncate-then-write and default permissions, and a
single malformed byte threw out of the constructor and lost every pairing. Writes
are now atomic and owner-only where the platform allows; a malformed record is
skipped and an unparseable file is quarantined so the client still starts.

Closes #86."
```

---

### Task 4: The PIN lockout gate and its file store (#86 item 6)

**Files:**
- Create: `src/Sendspin.SDK/Connection/Noise/Pairing/FilePinLockoutStore.cs`
- Modify: `src/Sendspin.SDK/Client/SendSpinClient.cs` — `CanOffer`'s PIN arms
- Test: `tests/Sendspin.SDK.Tests/Client/SendspinClientServicePinPairingTests.cs`

**Interfaces:**
- Consumes: `SecureFile` from Task 1; `CanOffer(string? method)` (private, added by #101).
- Produces: `public sealed class FilePinLockoutStore(string path) : IPinLockoutStore`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Sendspin.SDK.Tests/Client/SendspinClientServicePinPairingTests.cs`:

```csharp
    [Fact]
    public void DynamicPin_WithNoLockoutStore_IsRefused()
    {
        // With no store, IsPinMethodLockedOut evaluates (null?.GetFailures() ?? 0) >= 10 —
        // always false — and RecordPinFailure returns early. So PIN attempts are unlimited
        // and the spec's terminal lockout is silently inert. Refuse the method instead.
        var (client, connection, _) = TestClient.Create(
            PskCategory.Sentinel,
            configure: options => options.Capabilities = new ClientCapabilities
            {
                PinPairingMethods = ["dynamic_pin"],
            });
        using var _c = client;
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");

        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"selected_pair_method":"dynamic_pin"}}""");

        Assert.DoesNotContain(connection.SentMessages, m => m is ClientPairInitMessage);
        var abort = Assert.Single(connection.SentMessages.OfType<PairAbortMessage>());
        Assert.Equal("method_not_supported", abort.Payload.Reason);
        Assert.Equal(ConnectionState.Connected, connection.State);
    }

    [Fact]
    public void DynamicPin_WithALockoutStore_IsStillOffered()
    {
        // Positive control: the two new clauses could refuse PIN entirely and the test above
        // would still pass.
        var (client, connection, _) = TestClient.Create(
            PskCategory.Sentinel,
            configure: options =>
            {
                options.Capabilities = new ClientCapabilities { PinPairingMethods = ["dynamic_pin"] };
                options.PinLockoutStore = new InMemoryPinLockoutStore();
            });
        using var _c = client;
        connection.ConnectAsync(new Uri("ws://test.local:8927/sendspin")).GetAwaiter().GetResult();
        connection.RaiseTextMessageReceived("""{"type":"server/hello","payload":{"name":"srv"}}""");

        connection.RaiseTextMessageReceived(
            """{"type":"server/activate","payload":{"activities":["pairing"],"selected_pair_method":"dynamic_pin"}}""");

        Assert.Single(connection.SentMessages.OfType<ClientPairInitMessage>());
        Assert.DoesNotContain(connection.SentMessages, m => m is PairAbortMessage);
    }

    [Fact]
    public void FilePinLockoutStore_PersistsCountersAcrossInstances()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sendspin-lockout-" + Guid.NewGuid().ToString("N")[..8]);
        string path = Path.Combine(dir, "lockout.json");
        try
        {
            new FilePinLockoutStore(path).SetFailures("dynamic_pin", 3);

            Assert.Equal(3, new FilePinLockoutStore(path).GetFailures("dynamic_pin"));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
```

> Check which `using` directives that file already has — `Sendspin.SDK.Connection.Noise.Pairing` and `Sendspin.SDK.Connection` may need adding for `InMemoryPinLockoutStore`, `FilePinLockoutStore` and `ConnectionState`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo --filter "FullyQualifiedName~SendspinClientServicePinPairingTests"`

Expected: `DynamicPin_WithNoLockoutStore_IsRefused` FAILS (a `client/pair-init` is sent today), `FilePinLockoutStore_PersistsCountersAcrossInstances` fails to compile, and the positive control PASSES already.

- [ ] **Step 3: Add the two clauses to `CanOffer`**

In `src/Sendspin.SDK/Client/SendSpinClient.cs`, in `CanOffer`:

```csharp
        // A PIN method without a lockout store cannot enforce the spec's terminal lockout at
        // 10 failures — IsPinMethodLockedOut would always report false — so offering it would
        // grant unlimited attempts. Refuse rather than fail open.
        "dynamic_pin" => _capabilities.PinPairingMethods.Contains("dynamic_pin")
                         && _pinLockoutStore is not null,
        "static_pin" => _capabilities.PinPairingMethods.Contains("static_pin")
                        && _pinLockoutStore is not null,
```

- [ ] **Step 4: Create the file-backed lockout store**

Create `src/Sendspin.SDK/Connection/Noise/Pairing/FilePinLockoutStore.cs`:

```csharp
using System.Text.Json;

namespace Sendspin.SDK.Connection.Noise.Pairing;

/// <summary>
/// JSON-file-backed PIN lockout store, written atomically and restricted to owner-only
/// access where the platform supports it.
/// </summary>
/// <remarks>
/// The counters are not secrets, but they are security state: anyone who can rewrite this
/// file resets the lockout, so it gets the same protection as the record store. A corrupt
/// file is treated as "no failures recorded" — the conservative reading is the one that
/// keeps the client usable, and a reset counter is the same position a fresh install is in.
/// </remarks>
public sealed class FilePinLockoutStore : IPinLockoutStore
{
    private readonly string _path;
    private readonly Dictionary<string, int> _failures;

    /// <summary>Creates a store backed by the given file path, loading existing counters.</summary>
    public FilePinLockoutStore(string path)
    {
        _path = path;
        _failures = Read(path);
    }

    /// <inheritdoc/>
    public int GetFailures(string method) => _failures.GetValueOrDefault(method);

    /// <inheritdoc/>
    public void SetFailures(string method, int failures)
    {
        _failures[method] = failures;
        SecureFile.WriteAllTextAtomic(_path, JsonSerializer.Serialize(_failures));
    }

    private static Dictionary<string, int> Read(string path)
    {
        string? text = SecureFile.ReadAllTextOrNull(path);
        if (text is null)
            return new Dictionary<string, int>();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, int>>(text) ?? new Dictionary<string, int>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, int>();
        }
    }
}
```

- [ ] **Step 5: Run the tests, then the full suite**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo --filter "FullyQualifiedName~PinPairing"`
Then: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo`

Expected: 0 failing, total up by 3 from Task 3's figure.

**Two pre-existing tests will break, and they are named here so you do not have to guess.** `PairingCounter_RestartsAfterReHandshake` and `PairingCounter_KeepsIncrementing_WhenHandshakeHashUnchanged` (added by the #101 branch, at roughly `:218` and `:250`) configure `PinPairingMethods = ["dynamic_pin"]` with **no** lockout store, then read `connection.SentMessages.OfType<ClientPairInitMessage>().Last()`. Once the gate refuses PIN there is no `pair-init`, so `.Last()` throws `InvalidOperationException` ("Sequence contains no elements").

Fix their setup by adding a store — their subject is the CPace counter, not the lockout gate:

```csharp
            configure: options =>
            {
                options.Capabilities = new ClientCapabilities { PinPairingMethods = ["dynamic_pin"] };
                options.PinLockoutStore = new InMemoryPinLockoutStore();
            });
```

Do **not** weaken the gate to accommodate a test, and do not change what those two tests assert — their `PairingIndex` expectations are load-bearing and were each proven by mutation on the #101 branch.

The helper at `:22` already supplies a lockout store, so tests going through it are unaffected. Check `:122`, `:137`, `:166` — they build their own `ClientCapabilities` and may or may not route through the helper.

- [ ] **Step 6: Commit**

```bash
git add src/Sendspin.SDK/Connection/Noise/Pairing/FilePinLockoutStore.cs src/Sendspin.SDK/Client/SendSpinClient.cs tests/Sendspin.SDK.Tests/Client/SendspinClientServicePinPairingTests.cs
git commit -m "fix(pairing)!: refuse PIN methods without a lockout store

With no store, IsPinMethodLockedOut always reported false and RecordPinFailure
returned early, so a client that configured PIN pairing granted unlimited
attempts and the spec's terminal lockout was silently inert. The methods are now
refused with pair/abort method_not_supported, leaving the connection open, and a
file-backed store makes correct configuration a one-liner.

Part of #86."
```

---

### Task 5: Document the storage seams

**Files:**
- Modify: `src/Sendspin.SDK/README.md`

**Interfaces:**
- Consumes: everything above.
- Produces: nothing.

- [ ] **Step 1: Add an identity-persistence section**

`src/Sendspin.SDK/README.md`'s Quick Start currently shows `Identity = SendspinIdentity.Generate()` with a note that a real host must persist it, but offers no mechanism. Update that snippet to use the store, and add a short section after it:

```markdown
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
                Environment.SpecialFolder.LocalApplicationData), "MyApp", "identity.json"))),
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
holds raw PSKs. Both are written atomically and set to owner-only (`0600`) on Unix. Windows
has no Unix file mode, so those files inherit their parent directory's ACL — place them
under `%LOCALAPPDATA%`, which is already user-scoped, or supply a platform store.
```

Verify the snippets compile against the real signatures rather than eyeballing them.

- [ ] **Step 2: Note the PIN lockout requirement inside the security note**

`src/Sendspin.SDK/README.md` mentions PIN pairing **nowhere** — verified, zero matches for `PinPairingMethods`, `dynamic_pin` or `static_pin`. Writing a PIN-pairing section from scratch is outside this slice (the README's encryption and pairing coverage belongs to #91), so do not add one.

Instead add two sentences to the security note from Step 1:

```markdown
If you enable the optional PIN pairing methods via `ClientCapabilities.PinPairingMethods`,
you must also supply an `IPinLockoutStore` — `FilePinLockoutStore` is provided. Without one
the spec's terminal lockout after 10 failed attempts cannot be enforced, so the SDK refuses
to offer the PIN methods rather than granting unlimited attempts.
```

- [ ] **Step 3: Run the full suite and commit**

Run: `dotnet test c:\CodeProjects\SendspinSDK\SendspinSDK.slnx -f net10.0 --nologo`

Expected: unchanged from Task 4 — this is documentation only.

```bash
git add src/Sendspin.SDK/README.md
git commit -m "docs: document the identity store and the storage security model

The NuGet README told hosts to persist the identity without offering a mechanism.
Also records what the file stores do and do not guarantee per platform, since
Windows has no Unix file mode and relies on the parent directory's ACL."
```

---

## Verification Checklist

Run before opening the PR. Each maps to a success criterion in the design.

- [ ] `SendspinIdentity.FromStore` twice against one store yields the same `PeerId`.
- [ ] `git grep -n "public ReadOnlyMemory<byte> PrivateKey"` returns nothing.
- [ ] `SendspinClientOptions.Identity` is still `required` — `git grep -n "required public SendspinIdentity Identity"` matches.
- [ ] `git grep -n "File.WriteAllText" src/Sendspin.SDK/Connection/Noise/` returns only `SecureFile.cs`.
- [ ] `git grep -n "SetUnixFileMode" src/` shows every call guarded by `OperatingSystem.IsWindows()`.
- [ ] A store file containing one malformed record still loads the others.
- [ ] An unparseable store file is quarantined and the store opens empty.
- [ ] A client with `PinPairingMethods` set and no lockout store refuses PIN pairing; with a store, it offers it.
- [ ] `dotnet test ... -f net10.0` passes with 0 failures.
- [ ] `dotnet build` clean for both `net8.0` and `net10.0` — no CA1416 platform warnings.
- [ ] `src/Sendspin.SDK/Sendspin.SDK.csproj` still says `<Version>9.1.0</Version>`.
- [ ] No `CryptographicOperations.ZeroMemory` added to `NoiseWireFraming` or `CPace` (that is #102).
- [ ] No async or queued-write machinery added for #86 item 4. The design (§4.2) closes it as already-mostly-fixed by the pure-`Resolve` change in #101: the write is no longer inside `ProcessInbound`, and `MarkMatchedPskUsed` guards it to once per session. Record that reasoning when closing #86 rather than leaving the item unaddressed.
