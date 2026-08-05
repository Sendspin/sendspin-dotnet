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
