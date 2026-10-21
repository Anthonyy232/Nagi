using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Helpers;

internal static class LyricsStorageHelper
{
    public static string? FindSidecar(IFileSystemService fileSystem, string audioFilePath, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(audioFilePath)) return null;

        try
        {
            var directory = Path.GetDirectoryName(audioFilePath);
            if (string.IsNullOrEmpty(directory)) return null;

            var audioName = Path.GetFileNameWithoutExtension(audioFilePath);
            return fileSystem.GetFiles(directory, "*.lrc")
                       .FirstOrDefault(path => Path.GetFileNameWithoutExtension(path)
                           .Equals(audioName, StringComparison.OrdinalIgnoreCase))
                   ?? fileSystem.GetFiles(directory, "*.txt")
                       .FirstOrDefault(path => Path.GetFileNameWithoutExtension(path)
                           .Equals(audioName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to find external lyrics for {AudioFilePath}.", audioFilePath);
            return null;
        }
    }

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
