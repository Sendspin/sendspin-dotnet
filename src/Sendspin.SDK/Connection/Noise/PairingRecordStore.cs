using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sendspin.SDK.Connection.Noise;

/// <summary>
/// A persisted pairing credential: a PSK, its category, and (for stored-pubkey
/// records) the server id it is bound to.
/// </summary>
/// <param name="Psk">The 32-byte PSK.</param>
/// <param name="Category">Long-term (from pairing) or Pairing (bootstrap secret).</param>
/// <param name="ServerId">Bound server id for stored-pubkey records; null for shared records.</param>
/// <param name="Used">
/// True once a server has authenticated a session with this record. Reported to
/// servers in management/list-records; nothing in the SDK gates on it. Per spec
/// #122 the Pairing PSK is NOT consumed by a successful pairing — it persists and
/// may pair this client with any number of servers — so a used Pairing PSK record
/// is deliberately retained, not retired.
/// </param>
public sealed record PairingRecord(
    ReadOnlyMemory<byte> Psk,
    PskCategory Category,
    string? ServerId = null,
    bool Used = false)
{
    /// <summary>The record's psk_id, derived from its PSK.</summary>
    public string PskId => NoiseConstants.DerivePskId(Psk.Span);
}

/// <summary>
/// Stores the client's pairing records (long-term PSKs and staged Pairing PSKs).
/// The SDK may call an implementation from an app thread (<c>EnsurePairingPsk</c>,
/// <c>RotatePairingPsk</c>) concurrently with a connection's receive thread, so an
/// implementation must be safe for concurrent use. Both implementations shipped in this
/// package are.
/// </summary>
public interface IPairingRecordStore
{
    /// <summary>All stored records.</summary>
    IReadOnlyList<PairingRecord> List();

    /// <summary>Adds or replaces the record with the same psk_id.</summary>
    void Upsert(PairingRecord record);

    /// <summary>Removes the record with the given psk_id (no-op if absent).</summary>
    void Remove(string pskId);
}

/// <summary>In-memory record store (no persistence). Suitable for tests and ephemeral clients.</summary>
public sealed class InMemoryPairingRecordStore : IPairingRecordStore
{
    private readonly Dictionary<string, PairingRecord> _records = new();
    private readonly object _lock = new();

    /// <inheritdoc/>
    public IReadOnlyList<PairingRecord> List()
    {
        lock (_lock)
            return _records.Values.ToList();
    }

    /// <inheritdoc/>
    public void Upsert(PairingRecord record)
    {
        lock (_lock)
            _records[record.PskId] = record;
    }

    /// <inheritdoc/>
    public void Remove(string pskId)
    {
        lock (_lock)
            _records.Remove(pskId);
    }
}

/// <summary>
/// JSON-file-backed record store. The file contains raw PSKs; it is written atomically and
/// restricted to owner-only access where the platform supports it. On Windows it inherits
/// the parent directory's ACL, so place it somewhere already user-scoped such as
/// <c>%LOCALAPPDATA%</c>. For hardware-backed protection, supply a platform
/// <see cref="IPairingRecordStore"/> implementation instead (DPAPI, Keychain, keystore).
/// </summary>
public sealed class FilePairingRecordStore : IPairingRecordStore
{
    internal sealed record Entry(string Psk, string Category, string? ServerId, bool Used);

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly Dictionary<string, PairingRecord> _records = new();
    private readonly object _lock = new();

    /// <summary>
    /// Creates a store backed by the given file, loading existing records. A malformed
    /// individual record is skipped; a file that cannot be parsed at all is quarantined
    /// alongside itself and the store opens empty, so a single bad byte cannot stop the
    /// client from starting. A file left at looser permissions by an earlier SDK version is
    /// narrowed to owner-only here, since nothing else will rewrite it.
    /// </summary>
    public FilePairingRecordStore(string path, ILogger? logger = null)
    {
        _path = path;
        _logger = logger ?? NullLogger.Instance;

        string? text = SecureFile.ReadAllTextOrNull(path);
        if (text is null)
            return;

        // A file written by an earlier SDK version is still at the platform default (0644 on
        // Unix), and Save() — the only thing that would replace the inode with a 0600 one — is
        // never reached by an already-paired client that does not re-pair. Narrow it here or it
        // stays world-readable, with raw PSKs in it, indefinitely.
        if (SecureFile.NarrowExistingPermissions(path))
        {
            _logger.LogInformation(
                "Tightened permissions on pairing record store {Path} to owner-only; it was " +
                "readable by other users on this machine.", path);
        }

        List<Entry>? entries;
        try
        {
            entries = JsonSerializer.Deserialize(text, PairingRecordStoreJsonContext.Default.ListEntry);
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
        catch (Exception moveFailure) when (moveFailure is IOException or UnauthorizedAccessException)
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

    /// <inheritdoc/>
    public IReadOnlyList<PairingRecord> List()
    {
        lock (_lock)
            return _records.Values.ToList();
    }

    /// <inheritdoc/>
    public void Upsert(PairingRecord record)
    {
        lock (_lock)
        {
            _records[record.PskId] = record;
            Save();
        }
    }

    /// <inheritdoc/>
    public void Remove(string pskId)
    {
        lock (_lock)
        {
            if (_records.Remove(pskId))
                Save();
        }
    }

    // Called only from Upsert/Remove above, always under _lock — it must not take the lock
    // itself, or re-entrancy would obscure who owns it. SecureFile.WriteAllTextAtomic is
    // synchronous, so nothing awaits while the lock is held.
    private void Save()
    {
        var entries = _records.Values
            .Select(r => new Entry(Base64UrlText.Encode(r.Psk.Span), r.Category.ToString(), r.ServerId, r.Used))
            .ToList();
        SecureFile.WriteAllTextAtomic(
            _path,
            JsonSerializer.Serialize(entries, PairingRecordStoreJsonContext.Default.ListEntry));
    }
}

/// <summary>
/// Resolves psk_ids against a record store, falling back to the published Sentinel PSK.
/// This is the resolver a paired client uses.
/// </summary>
internal sealed class RecordPskResolver : INoisePskResolver
{
    private readonly IPairingRecordStore _store;

    /// <summary>Creates a resolver over the given record store.</summary>
    public RecordPskResolver(IPairingRecordStore store) => _store = store;

    /// <inheritdoc/>
    /// <remarks>
    /// A pure lookup. This runs inside the framing layer's inbound path, before the
    /// AEAD has verified anything, so it must not mutate the store — the record is
    /// marked used by the client once a decrypted message proves the session
    /// authenticated.
    /// </remarks>
    public NoisePsk? Resolve(string pskId)
    {
        foreach (var record in _store.List())
        {
            if (record.PskId == pskId)
            {
                return new NoisePsk(record.Psk, record.Category, record.ServerId);
            }
        }

        return SentinelPskResolver.Instance.Resolve(pskId);
    }
}
