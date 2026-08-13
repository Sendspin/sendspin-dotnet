using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly ILogger _logger;
    private readonly Dictionary<string, int> _failures;

    /// <summary>Creates a store backed by the given file path, loading existing counters.</summary>
    /// <param name="path">File to hold the counters.</param>
    /// <param name="logger">
    /// Optional. Without one, a corrupt file is discarded and permissions are narrowed with no
    /// signal at all — the state this store was in before #103.
    /// </param>
    public FilePinLockoutStore(string path, ILogger? logger = null)
    {
        _path = path;
        _logger = logger ?? NullLogger.Instance;
        _failures = Read(path, _logger);
    }

    /// <inheritdoc/>
    public int GetFailures(string method) => _failures.GetValueOrDefault(method);

    /// <inheritdoc/>
    public void SetFailures(string method, int failures)
    {
        // Persist first, then take it in memory. The other order left a failed write with the
        // in-memory counter ahead of disk, so a restart silently rolled the count back — a
        // fail-open on the brute-force guard, small but in the wrong direction (#103).
        var updated = new Dictionary<string, int>(_failures) { [method] = failures };
        SecureFile.WriteAllTextAtomic(
            _path,
            JsonSerializer.Serialize(updated, PinLockoutStoreJsonContext.Default.DictionaryStringInt32));

        _failures[method] = failures;
    }

    private static Dictionary<string, int> Read(string path, ILogger logger)
    {
        string? text = SecureFile.ReadAllTextOrNull(path);
        if (text is null)
            return new Dictionary<string, int>();

        // Same reason as FilePairingRecordStore: a file from an earlier SDK version keeps the
        // platform-default mode until something replaces the inode, and only a failed PIN
        // attempt does that.
        if (SecureFile.NarrowExistingPermissions(path, logger))
        {
            logger.LogInformation(
                "Tightened permissions on PIN lockout store {Path} to owner-only; it was "
                + "readable by other users on this machine.", path);
        }

        try
        {
            return JsonSerializer.Deserialize(text, PinLockoutStoreJsonContext.Default.DictionaryStringInt32)
                ?? new Dictionary<string, int>();
        }
        catch (JsonException ex)
        {
            // Deliberately not quarantined the way FilePairingRecordStore does. That store
            // preserves its file because it holds unrecoverable secrets; these are counters,
            // and keeping a corrupt copy of them buys nothing. The reset is still worth an
            // Error: it is a brute-force guard silently returning to zero.
            logger.LogError(
                ex,
                "PIN lockout store at {Path} could not be parsed; starting with no recorded "
                + "failures. Every PIN method returns to its un-escalated state.", path);
            return new Dictionary<string, int>();
        }
    }
}
