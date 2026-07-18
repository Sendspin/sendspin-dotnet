using System.Text.Json;

namespace Sendspin.SDK.Connection.Noise;

/// <summary>
/// A persisted pairing credential: a PSK, its category, and (for stored-pubkey
/// records) the server id it is bound to.
/// </summary>
/// <param name="Psk">The 32-byte PSK.</param>
/// <param name="Category">Long-term (from pairing) or Pairing (bootstrap secret).</param>
/// <param name="ServerId">Bound server id for stored-pubkey records; null for shared records.</param>
/// <param name="Used">True once a server has authenticated a session with this record.</param>
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
/// Implementations need not be thread-safe; the SDK serializes access.
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

    /// <inheritdoc/>
    public IReadOnlyList<PairingRecord> List() => _records.Values.ToList();

    /// <inheritdoc/>
    public void Upsert(PairingRecord record) => _records[record.PskId] = record;

    /// <inheritdoc/>
    public void Remove(string pskId) => _records.Remove(pskId);
}

/// <summary>
/// JSON-file-backed record store. The file contains raw PSKs: protect it with
/// filesystem permissions, or supply a platform-secure <see cref="IPairingRecordStore"/>
/// implementation instead (DPAPI, Keychain, keystore).
/// </summary>
public sealed class FilePairingRecordStore : IPairingRecordStore
{
    private sealed record Entry(string Psk, string Category, string? ServerId, bool Used);

    private readonly string _path;
    private readonly Dictionary<string, PairingRecord> _records = new();

    /// <summary>Creates a store backed by the given file, loading existing records.</summary>
    public FilePairingRecordStore(string path)
    {
        _path = path;
        if (!File.Exists(path))
            return;
        var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? [];
        foreach (var e in entries)
        {
            var record = new PairingRecord(
                Base64UrlText.Decode(e.Psk),
                Enum.Parse<PskCategory>(e.Category),
                e.ServerId,
                e.Used);
            _records[record.PskId] = record;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<PairingRecord> List() => _records.Values.ToList();

    /// <inheritdoc/>
    public void Upsert(PairingRecord record)
    {
        _records[record.PskId] = record;
        Save();
    }

    /// <inheritdoc/>
    public void Remove(string pskId)
    {
        if (_records.Remove(pskId))
            Save();
    }

    private void Save()
    {
        var entries = _records.Values
            .Select(r => new Entry(Base64UrlText.Encode(r.Psk.Span), r.Category.ToString(), r.ServerId, r.Used))
            .ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        File.WriteAllText(_path, JsonSerializer.Serialize(entries));
    }
}

/// <summary>
/// Resolves psk_ids against a record store, falling back to the published Sentinel PSK.
/// This is the resolver a paired client uses.
/// </summary>
public sealed class RecordPskResolver : INoisePskResolver
{
    private readonly IPairingRecordStore _store;

    /// <summary>Creates a resolver over the given record store.</summary>
    public RecordPskResolver(IPairingRecordStore store) => _store = store;

    /// <inheritdoc/>
    public NoisePsk? Resolve(string pskId)
    {
        foreach (var record in _store.List())
        {
            if (record.PskId == pskId)
            {
                if (!record.Used)
                    _store.Upsert(record with { Used = true });
                return new NoisePsk(record.Psk, record.Category, record.ServerId);
            }
        }

        return SentinelPskResolver.Instance.Resolve(pskId);
    }
}
