using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Lyrics;

internal static class LyricsStorageHelper
{
    // Replace existing lyrics only after the complete new content has been written.
    public static async Task SaveAsync(IFileSystemService fileSystem, string path, string content)
    {
        fileSystem.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await fileSystem.WriteAllTextAsync(temporaryPath, content).ConfigureAwait(false);
            fileSystem.MoveFile(temporaryPath, path, true);
        }
        finally
        {
            if (fileSystem.FileExists(temporaryPath)) fileSystem.DeleteFile(temporaryPath);
        }
    }
}
