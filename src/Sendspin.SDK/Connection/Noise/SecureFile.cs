using Microsoft.Extensions.Logging;

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

        // Unique per write, and created rather than opened. POSIX applies open()'s mode only on
        // creation, so a fixed name that already exists is opened and truncated at whatever mode
        // it already carries — a planted 0666 file, or a symlink pointing somewhere else, would
        // receive the plaintext private key. CreateNew refuses an existing path outright, and the
        // random suffix also removes the shared name two concurrent writers used to race on.
        string temp = $"{full}.{Guid.NewGuid():N}.tmp";

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
        };
        if (!OperatingSystem.IsWindows())
        {
            // Applied at creation time so the temp file never exists, even briefly, at the
            // platform-default (world-readable) permissions before being narrowed.
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
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
        finally
        {
            // A write that threw between creation and the move would otherwise leave a uniquely
            // named file holding the secret behind forever, since nothing ever revisits that
            // name. Delete is a no-op once the move has consumed it.
            try
            {
                File.Delete(temp);
            }
            catch (Exception cleanupFailure) when (cleanupFailure is IOException or UnauthorizedAccessException)
            {
                // Nothing useful to do: the original failure is the one worth propagating, and
                // this store has no logger to report a stray temp file to.
            }
        }
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
    internal static bool NarrowExistingPermissions(string path, ILogger logger)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        const UnixFileMode groupOrOther =
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

        try
        {
            if ((File.GetUnixFileMode(path) & groupOrOther) == 0)
            {
                return false;
            }

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            // Chmod is not always available even on Unix: a file owned by another uid on a
            // Docker bind mount, or any mount that rejects it (CIFS, exFAT). Letting that throw
            // made an un-narrowable file abort client startup, which runs against the record
            // store's own principle that a single bad byte cannot stop the client from starting.
            // Report and carry on with the file as it is (#103).
            logger.LogWarning(
                ex,
                "Could not narrow permissions on {Path}; it stays readable by other users on "
                + "this machine. Move it somewhere the client owns, or supply a platform store.",
                path);
            return false;
        }
    }

    /// <summary>
    /// Returns the file's contents, or <c>null</c> when it does not exist.
    /// </summary>
    /// <remarks>
    /// "Does not exist" is <see cref="File.Exists"/>'s answer, which is also what it returns for
    /// a path it cannot stat at all. On Unix that is not reachable for these stores — stat needs
    /// only directory traversal, so a root-owned 0600 file still reports Exists and the read
    /// below throws rather than silently reporting absence — but the distinction is worth stating
    /// plainly in a security primitive rather than claiming that every IO failure throws.
    /// </remarks>
    internal static string? ReadAllTextOrNull(string path) =>
        File.Exists(path) ? File.ReadAllText(path) : null;
}
