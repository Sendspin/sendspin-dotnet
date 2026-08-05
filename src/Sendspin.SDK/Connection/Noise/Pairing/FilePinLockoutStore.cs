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
