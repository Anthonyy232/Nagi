using System.IO;

namespace Nagi.Core.Helpers;

/// <summary>
///     Enumerates files without allowing an inaccessible directory, transient filesystem error,
///     or directory reparse-point cycle to abort or hang the whole traversal.
/// </summary>
public static class SafeFileEnumerator
{
    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "$RECYCLE.BIN",
        "RECYCLED",
        "RECYCLER",
        "System Volume Information",
        "@eaDir",
        "@Recycle",
        "#recycle",
        ".Trashes",
        ".Trash",
        ".Trash-1000",
        "lost+found",
        ".fseventsd",
        ".Spotlight-V100"
    };

    public static IEnumerable<(string Path, DateTime LastWriteTimeUtc)> EnumerateFilesWithLastWriteTime(
        string rootPath,
        string searchPattern,
        SearchOption searchOption)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) yield break;

        var pending = new Stack<DirectoryInfo>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Push(new DirectoryInfo(rootPath));

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            var identity = TryGetDirectoryIdentity(directory);
            if (identity is null || !visited.Add(identity)) continue;

            foreach (var file in EnumerateFilesSafely(directory, searchPattern))
            {
                string path;
                DateTime lastWriteTimeUtc;
                try
                {
                    path = file.FullName;
                    lastWriteTimeUtc = file.LastWriteTimeUtc;
                }
                catch (Exception ex) when (IsRecoverableFileSystemException(ex))
                {
                    continue;
                }

                yield return (path, lastWriteTimeUtc);
            }

            if (searchOption == SearchOption.TopDirectoryOnly) continue;

            foreach (var child in EnumerateDirectoriesSafely(directory))
            {
                if (IsExcludedDirectory(child)) continue;
                pending.Push(child);
            }
        }
    }

    internal static bool IsExcludedDirectory(DirectoryInfo directory)
    {
        if (ExcludedDirectoryNames.Contains(directory.Name)) return true;
        if (directory.Name.Length > 0 && directory.Name[0] == '$') return true;

        try
        {
            return IsExcludedAttributes(directory.Attributes);
        }
        catch (Exception ex) when (IsRecoverableFileSystemException(ex))
        {
            // If a child cannot be classified safely, skip it for this scan. Treating an
            // unreadable reparse point as ordinary could reintroduce a traversal cycle.
            return true;
        }
    }

    internal static bool IsExcludedAttributes(FileAttributes attributes)
    {
        // Following junctions/symbolic links makes ancestor cycles possible. The selected root may
        // itself be a link, but nested links are deliberately skipped during traversal.
        if ((attributes & FileAttributes.ReparsePoint) != 0) return true;

        // Hidden alone is allowed for user-hidden music. Hidden + System is a strong OS-directory signal.
        return (attributes & (FileAttributes.Hidden | FileAttributes.System))
               == (FileAttributes.Hidden | FileAttributes.System);
    }

    private static string? TryGetDirectoryIdentity(DirectoryInfo directory)
    {
        try
        {
            return Path.GetFullPath(directory.FullName)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (IsRecoverableFileSystemException(ex) || ex is ArgumentException)
        {
            return null;
        }
    }

    private static IEnumerable<FileInfo> EnumerateFilesSafely(DirectoryInfo directory, string searchPattern)
    {
        IEnumerator<FileInfo>? enumerator = null;
        try
        {
            enumerator = directory.EnumerateFiles(searchPattern).GetEnumerator();
        }
        catch (Exception ex) when (IsRecoverableFileSystemException(ex))
        {
            // Handled below by returning an empty sequence.
        }

        if (enumerator is null) yield break;

        using (enumerator)
        {
            while (true)
            {
                var shouldStop = false;
                var hasNext = false;
                try
                {
                    hasNext = enumerator.MoveNext();
                }
                catch (Exception ex) when (IsRecoverableFileSystemException(ex))
                {
                    // An iterator that throws is not guaranteed to advance. Stop this directory
                    // instead of retrying the same failing MoveNext forever.
                    shouldStop = true;
                }

                if (shouldStop || !hasNext) yield break;
                if (enumerator.Current is { } file) yield return file;
            }
        }
    }

    private static IEnumerable<DirectoryInfo> EnumerateDirectoriesSafely(DirectoryInfo directory)
    {
        IEnumerator<DirectoryInfo>? enumerator = null;
        try
        {
            enumerator = directory.EnumerateDirectories().GetEnumerator();
        }
        catch (Exception ex) when (IsRecoverableFileSystemException(ex))
        {
            // Handled below by returning an empty sequence.
        }

        if (enumerator is null) yield break;

        using (enumerator)
        {
            while (true)
            {
                var shouldStop = false;
                var hasNext = false;
                try
                {
                    hasNext = enumerator.MoveNext();
                }
                catch (Exception ex) when (IsRecoverableFileSystemException(ex))
                {
                    shouldStop = true;
                }

                if (shouldStop || !hasNext) yield break;
                if (enumerator.Current is { } child) yield return child;
            }
        }
    }

    private static bool IsRecoverableFileSystemException(Exception exception) =>
        exception is UnauthorizedAccessException
            or DirectoryNotFoundException
            or IOException
            or System.Security.SecurityException;
}
