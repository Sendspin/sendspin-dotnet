using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sendspin.SDK.Connection.Noise;

/// <summary>
/// A persisted pairing credential: a PSK, its category, and — for a long-term record, which
/// is what the spec calls a <em>pairing record</em> — the <c>server_id</c> it is bound to.
/// </summary>
/// <param name="Psk">The 32-byte PSK.</param>
/// <param name="Category">Long-term (produced by a pairing) or Pairing (this client's own bootstrap secret).</param>
/// <param name="ServerId">
/// The server this record pairs with. Required for <see cref="PskCategory.LongTerm"/>: every
/// pairing record stores the server's <c>server_id</c>, and a handshake that matches the
/// record's <c>psk_id</c> is failed when the id in <c>server/init</c> differs. Null for the
/// client's own Pairing PSK, which is not bound to any server.
/// </param>
/// <param name="LastUsedUtc">
/// When a server last authenticated a session with this record, or null if it never has.
/// Local bookkeeping, not wire state: it orders the least-recently-used choice made when a
/// pairing completes at capacity and a record must be evicted. The spec leaves that choice to
/// the implementation and names LRU as an example.
/// </param>
public sealed record PairingRecord(
    ReadOnlyMemory<byte> Psk,
    PskCategory Category,
    string? ServerId = null,
    DateTimeOffset? LastUsedUtc = null)
{
    /// <summary>The record's psk_id, derived from its PSK.</summary>
    public string PskId => NoiseConstants.DerivePskId(Psk.Span);
}

/// <summary>
/// Stores the client's pairing records (long-term PSKs and the staged Pairing PSK).
/// The SDK may call an implementation from an app thread (<c>EnsurePairingPsk</c>,
/// <c>RotatePairingPsk</c>) concurrently with a connection's receive thread, so an
/// implementation must be safe for concurrent use. Both implementations shipped in this
/// package are.
/// </summary>
/// <remarks>
/// <para>
/// <b>Capacity and eviction.</b> The spec requires a client to hold at least
/// <see cref="PairingRecords.MinimumCapacity"/> pairing records and requires a pairing that
/// completes at capacity to succeed anyway, by evicting an existing record — a pairing never
/// fails for lack of record storage. A bounded store therefore reports its limit through
/// <see cref="Capacity"/> and the SDK frees a slot before it writes; <see cref="Upsert"/> must
/// not refuse a record because the store is full.
/// </para>
/// <para>
/// This replaces the earlier <c>bool Upsert</c> contract, whose <c>false</c> return meant
/// "full, nothing stored" — an outcome the spec no longer permits.
/// </para>
/// </remarks>
public interface IPairingRecordStore
{
    /// <summary>All stored records.</summary>
    IReadOnlyList<PairingRecord> List();

    /// <summary>
    /// Adds or replaces the record with the same psk_id.
    /// </summary>
    /// <remarks>
    /// Must not refuse for lack of capacity: the SDK evicts to make room before calling, and a
    /// pairing that reaches this point has already been agreed with the server. A failure of
    /// the underlying medium — including running out of disk — is a fault, not exhaustion, and
    /// is reported by throwing.
    /// </remarks>
    void Upsert(PairingRecord record);

    /// <summary>Removes the record with the given psk_id (no-op if absent).</summary>
    void Remove(string pskId);

    /// <summary>
    /// How many records this store can hold. Defaults to unbounded. Override it only if your
    /// medium really is bounded, and never below <see cref="PairingRecords.MinimumCapacity"/>:
    /// the client caps its concurrently open paired connections below this so an evictable
    /// record always exists.
    /// </summary>
    int Capacity => int.MaxValue;
}

/// <summary>
/// The record-store rules the spec puts on the client rather than on the store: unique
/// <c>psk_id</c> generation, one record per <c>server_id</c>, and eviction at capacity.
/// </summary>
/// <remarks>
/// Shared by every call site that mints or persists a PSK so the rules cannot drift between
/// the Pairing PSK operations and the pairing flows.
/// </remarks>
internal static class PairingRecords
{
    /// <summary>The smallest record capacity the spec permits.</summary>
    internal const int MinimumCapacity = 5;

    // Bounded rather than a while(true): a 32-byte CSPRNG draw colliding even once is already
    // beyond astronomical, so more attempts than this means the RNG is broken or the store is
    // lying about what it holds, and spinning forever would hang the receive path.
    internal const int PskGenerationAttempts = 8;

    /// <summary>
    /// Draws a fresh 32-byte PSK whose <c>psk_id</c> no stored record already uses. The spec
    /// enforces <c>psk_id</c> uniqueness at generation time, because the client selects a PSK
    /// by that identifier alone: two records sharing one would make the selection ambiguous.
    /// </summary>
    /// <param name="store">The store whose <c>psk_id</c>s are already spoken for.</param>
    /// <param name="draw">
    /// The source of candidate PSKs, defaulting to the CSPRNG. A collision is unreachable with
    /// real random draws, so only a test can supply one and see the retry happen.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Every attempt collided, which no working CSPRNG produces.
    /// </exception>
    internal static byte[] GenerateUniquePsk(IPairingRecordStore store, Func<byte[]>? draw = null)
    {
        draw ??= static () => System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var taken = store.List().Select(r => r.PskId).ToHashSet(StringComparer.Ordinal);
        for (int attempt = 0; attempt < PskGenerationAttempts; attempt++)
        {
            byte[] psk = draw();
            if (!taken.Contains(NoiseConstants.DerivePskId(psk)))
            {
                return psk;
            }
        }

        throw new InvalidOperationException(
            $"Could not generate a PSK with an unused psk_id in {PskGenerationAttempts} attempts; " +
            "the random number generator or the pairing record store is misbehaving.");
    }

    /// <summary>
    /// Persists the long-term record a successful pairing produced, replacing whatever record
    /// this client already held for <paramref name="serverId"/> and, when the store is at
    /// capacity, evicting the least recently used record that is not backing an open
    /// connection.
    /// </summary>
    /// <param name="store">The record store to write to.</param>
    /// <param name="psk">The 32-byte long-term PSK.</param>
    /// <param name="serverId">The server the record pairs with.</param>
    /// <param name="livePskIds">
    /// The psk_ids of records backing a currently-open connection, which must not be evicted.
    /// </param>
    /// <param name="logger">Logger for the eviction decision.</param>
    internal static void PersistLongTerm(
        IPairingRecordStore store,
        ReadOnlyMemory<byte> psk,
        string serverId,
        IReadOnlyCollection<string> livePskIds,
        ILogger logger)
    {
        var record = new PairingRecord(psk, PskCategory.LongTerm, serverId, DateTimeOffset.UtcNow);

        // Replace this server's own record first, so a re-pair is a like-for-like swap that
        // frees its slot before the capacity check rather than counting as a second record.
        foreach (var existing in store.List())
        {
            if (existing.Category == PskCategory.LongTerm
                && string.Equals(existing.ServerId, serverId, StringComparison.Ordinal)
                && existing.PskId != record.PskId)
            {
                store.Remove(existing.PskId);
                logger.LogDebug("Replaced the existing pairing record for {ServerId}", serverId);
            }
        }

        EvictIfAtCapacity(store, record, livePskIds, logger);
        store.Upsert(record);
    }

    private static void EvictIfAtCapacity(
        IPairingRecordStore store,
        PairingRecord incoming,
        IReadOnlyCollection<string> livePskIds,
        ILogger logger)
    {
        int capacity = store.Capacity;
        if (capacity == int.MaxValue)
        {
            return;
        }

        if (capacity < MinimumCapacity)
        {
            logger.LogWarning(
                "The pairing record store reports a capacity of {Capacity}; the spec requires at "
                + "least {Minimum} pairing records.", capacity, MinimumCapacity);
        }

        var records = store.List();
        if (records.Count < capacity || records.Any(r => r.PskId == incoming.PskId))
        {
            return;
        }

        // Only long-term records are pairing records; the client's own Pairing PSK is its
        // bootstrap secret, not something a server pairing may drop. A record backing an open
        // connection is never a candidate, however old it is.
        var victim = records
            .Where(r => r.Category == PskCategory.LongTerm && !livePskIds.Contains(r.PskId))
            .OrderBy(r => r.LastUsedUtc ?? DateTimeOffset.MinValue)
            .FirstOrDefault();

        if (victim is null)
        {
            // Every record is either live or the Pairing PSK. The spec makes this the client's
            // own error - it must cap concurrent paired connections below its record capacity -
            // so the Upsert that follows is left to overflow the store's own limit rather than
            // silently dropping a pairing the server has already persisted.
            logger.LogError(
                "The pairing record store is at capacity ({Capacity}) and every record is either "
                + "backing an open connection or the Pairing PSK; the new record for {ServerId} "
                + "may not persist.", capacity, incoming.ServerId);
            return;
        }

        store.Remove(victim.PskId);
        logger.LogInformation(
            "Evicted the least recently used pairing record ({ServerId}, last used {LastUsed}) to "
            + "make room for {NewServerId}.",
            victim.ServerId,
            victim.LastUsedUtc,
            incoming.ServerId);
    }
}

/// <summary>In-memory record store (no persistence). Suitable for tests and ephemeral clients.</summary>
public sealed class InMemoryPairingRecordStore : IPairingRecordStore
{
    private readonly Dictionary<string, PairingRecord> _records = new();
    private readonly object _lock = new();

    /// <summary>Creates an unbounded store.</summary>
    public InMemoryPairingRecordStore()
        : this(int.MaxValue)
    {
    }

    /// <summary>
    /// Creates a store bounded to <paramref name="capacity"/> records, which is what a device
    /// with real storage limits looks like to the SDK's eviction logic.
    /// </summary>
    /// <param name="capacity">Records this store can hold.</param>
    public InMemoryPairingRecordStore(int capacity)
    {
        Capacity = capacity;
    }

    /// <inheritdoc/>
    public int Capacity { get; }

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
    /// <summary>
    /// One persisted record. <c>LastUsed</c> is an ISO-8601 instant, or null for a record no
    /// server has authenticated with yet.
    /// </summary>
    /// <remarks>
    /// <c>Used</c> is the pre-#183 boolean this replaced, read for migration and never
    /// written: a file from an earlier SDK version must keep authenticating its servers, and a
    /// record marked used there is treated as used-at-an-unknown-time, which sorts it before
    /// every dated record when a slot has to be freed. It is dropped from the file on the next
    /// write.
    /// </remarks>
    internal sealed record Entry(
        string Psk,
        string Category,
        string? ServerId,
        DateTimeOffset? LastUsed = null,
        [property: System.Text.Json.Serialization.JsonIgnore(
            Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
        bool Used = false);

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
        : this(path, int.MaxValue, logger)
    {
    }

    /// <summary>
    /// Creates a store bounded to <paramref name="capacity"/> records. See
    /// <see cref="IPairingRecordStore.Capacity"/>: the SDK evicts to stay within it rather
    /// than failing a pairing.
    /// </summary>
    /// <param name="path">File backing the store.</param>
    /// <param name="capacity">Records this store can hold.</param>
    /// <param name="logger">Optional logger for load-time problems.</param>
    public FilePairingRecordStore(string path, int capacity, ILogger? logger = null)
    {
        _path = path;
        _logger = logger ?? NullLogger.Instance;
        Capacity = capacity;

        string? text = SecureFile.ReadAllTextOrNull(path);
        if (text is null)
            return;

        // A file written by an earlier SDK version is still at the platform default (0644 on
        // Unix), and Save() — the only thing that would replace the inode with a 0600 one — is
        // never reached by an already-paired client that does not re-pair. Narrow it here or it
        // stays world-readable, with raw PSKs in it, indefinitely.
        if (SecureFile.NarrowExistingPermissions(path, _logger))
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

    /// <inheritdoc/>
    public int Capacity { get; }

    private void Quarantine(Exception cause)
    {
        string target = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMddTHHmmssZ}";
        try
        {
            File.Move(_path, target, overwrite: true);

            // File.Move keeps the source inode's permissions, so a pre-narrowing 0644 file full
            // of raw PSKs would become a permanent world-readable .corrupt-* artifact. The load
            // path narrows before parsing, so this is belt-and-braces for a file that arrived
            // between the two — but the quarantined copy outlives everything else here (#103).
            SecureFile.NarrowExistingPermissions(target, _logger);

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
                // A long-term record with no server_id predates the one-record-per-server model
                // (spec #183), when a long-term PSK was not bound to one server. Kept as-is
                // rather than dropped or invented: the PSK still authenticates the server that
                // knows it, and discarding it would unpair a working device on upgrade. It
                // simply never matches the replace-this-server's-record rule, so the first
                // re-pair with that server writes a properly bound record alongside it.
                entry.ServerId,
                // A pre-#183 file carries a bare used flag with no instant. Treated as used at
                // an unknown (earliest) time so it still sorts ahead of dated records for
                // eviction, rather than being promoted to "never used".
                entry.LastUsed ?? (entry.Used ? DateTimeOffset.MinValue : null));
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
            .Select(r => new Entry(
                Base64UrlText.Encode(r.Psk.Span), r.Category.ToString(), r.ServerId, r.LastUsedUtc))
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
    private readonly Func<bool> _isPairingPskEnabled;

    /// <summary>Creates a resolver over the given record store.</summary>
    /// <param name="store">The records to resolve psk_ids against.</param>
    /// <param name="isPairingPskEnabled">
    /// Reports whether the <c>pairing_psk</c> method is currently enabled in the client's
    /// pairing config. Called on every resolve rather than snapshotted, so the very next
    /// handshake sees the current value. Defaults to always-enabled for callers with no
    /// pairing config of their own.
    /// </param>
    public RecordPskResolver(IPairingRecordStore store, Func<bool>? isPairingPskEnabled = null)
    {
        _store = store;
        _isPairingPskEnabled = isPairingPskEnabled ?? (static () => true);
    }

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
            if (record.PskId == pskId && IsCandidate(record))
            {
                return new NoisePsk(record.Psk, record.Category, record.ServerId);
            }
        }

        return SentinelPskResolver.Instance.Resolve(pskId);
    }

    /// <summary>
    /// Whether the record belongs in the handshake's candidate set. Per connection.md, a PSK
    /// for a disabled pairing method is excluded, so a handshake referencing it misses
    /// outright instead of authenticating a channel that would only be refused later, at the
    /// pairing activation (#202).
    /// </summary>
    /// <remarks>
    /// Only the <c>pairing_psk</c> method's own bootstrap secret is affected. A long-term
    /// record from a completed pairing is not a pairing-method PSK — it is the credential that
    /// pairing produced — so it keeps resolving however the method is configured.
    /// </remarks>
    private bool IsCandidate(PairingRecord record) =>
        record.Category != PskCategory.Pairing || _isPairingPskEnabled();
}
