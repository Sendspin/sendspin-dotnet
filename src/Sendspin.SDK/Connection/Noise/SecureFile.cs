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
/// Permissions are set only on platforms that have them, and are applied at file-creation
/// time via <see cref="FileStreamOptions.UnixCreateMode"/> rather than after the fact, so
/// the temp file is never briefly readable at the platform default before being narrowed.
/// That property throws <see cref="PlatformNotSupportedException"/> on Windows, where the
/// file instead inherits its parent directory's ACL — so place these files somewhere
/// already user-scoped, such as <c>%LOCALAPPDATA%</c>.
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

        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
        };
        if (!OperatingSystem.IsWindows())
        {
            // Applied at creation time so the temp file never exists, even briefly, at the
            // platform-default (world-readable) permissions before being narrowed.
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(temp, options))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(contents);

            // Disposal only flushes to the kernel. The move below is a metadata operation and
            // can reach the journal before the data blocks do, so a power cut at the wrong
            // moment would leave the target name pointing at a zero-length file. Flush the
            // writer first: flushing the FileStream while the StreamWriter still buffers
            // accomplishes nothing.
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        // Move last: until this succeeds, the previous file is still the valid one.
        File.Move(temp, full, overwrite: true);
    }

    /// <summary>
    /// Narrows an existing file to owner-only access where the platform has permissions,
    /// returning <c>true</c> when it actually had to change something.
    /// </summary>
    /// <remarks>
    /// <see cref="WriteAllTextAtomic"/> only governs files this SDK version wrote. A store
    /// created by an earlier version is still at the platform default (0644 on Unix) and will
    /// stay there until something replaces the inode — which, for an already-paired client
    /// that never re-pairs, is never. Call this when loading such a file.
    /// </remarks>
    internal static bool NarrowExistingPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode groupOrOther =
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

            if ((File.GetUnixFileMode(path) & groupOrOther) != 0)
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the file's contents, or <c>null</c> when it does not exist. Genuine IO
    /// failures still throw.
    /// </summary>
    internal static string? ReadAllTextOrNull(string path) =>
        File.Exists(path) ? File.ReadAllText(path) : null;
}
