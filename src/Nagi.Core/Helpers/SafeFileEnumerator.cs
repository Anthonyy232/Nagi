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
        pending.Push(new DirectoryInfo(rootPath));

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var file in EnumerateSafely(() => directory.EnumerateFiles(searchPattern)))
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

            foreach (var child in EnumerateSafely(() => directory.EnumerateDirectories()))
            {
                if (IsExcludedDirectory(child)) continue;
                pending.Push(child);
            }
        }
    }

    internal static bool IsExcludedDirectory(DirectoryInfo directory)
    {
        if (ExcludedDirectoryNames.Contains(directory.Name)) return true;
        if (directory.Name.StartsWith('$')) return true;

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

    private static IEnumerable<T> EnumerateSafely<T>(Func<IEnumerable<T>> enumerate)
    {
        IEnumerator<T> enumerator;
        try
        {
            enumerator = enumerate().GetEnumerator();
        }
        catch (Exception ex) when (IsRecoverableFileSystemException(ex))
        {
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = enumerator.MoveNext();
                }
                catch (Exception ex) when (IsRecoverableFileSystemException(ex))
                {
                    // An iterator that throws is not guaranteed to advance. Stop this directory
                    // instead of retrying the same failing MoveNext forever.
                    yield break;
                }

                if (!hasNext) yield break;
                yield return enumerator.Current;
            }
        }
    }

    private static bool IsRecoverableFileSystemException(Exception exception) =>
        exception is UnauthorizedAccessException
            or IOException
            or System.Security.SecurityException;
}
