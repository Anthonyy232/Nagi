using Nagi.Core.Constants;
using Nagi.Core.Services.Abstractions;
using Microsoft.Extensions.Logging;

namespace Nagi.Core.Helpers;

public static class ImageStorageHelper
{
    internal static string? FindCoverArtInDirectory(IFileSystemService fileSystem, string directory, ILogger logger)
    {
        try
        {
            return fileSystem.GetFiles(directory, "*.*")
                .Where(path => !fileSystem.IsHiddenOrSystemFile(path) &&
                               FileExtensions.ImageFileExtensions.Contains(Path.GetExtension(path)))
                .Select(path => (Path: path, Priority: FileExtensions.GetCoverArtPriority(Path.GetFileNameWithoutExtension(path))))
                .Where(candidate => candidate.Priority < int.MaxValue)
                .OrderBy(candidate => candidate.Priority)
                .FirstOrDefault().Path;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to enumerate cover art in directory '{Directory}'.", directory);
            return null;
        }
    }

    /// <summary>
    ///     Finds the first existing image file matching the pattern {baseFileName}{suffix}.*
    ///     checks against all supported image extensions.
    /// </summary>
    /// <returns>The full path to the image file, or null if not found.</returns>
    public static string? FindImage(IFileSystemService fs, string directory, string baseFileName, string suffix)
    {
        if (!fs.DirectoryExists(directory)) return null;

        foreach (var ext in FileExtensions.ImageFileExtensions)
        {
            var path = Path.Combine(directory, $"{baseFileName}{suffix}{ext}");
            if (fs.FileExists(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    ///     Deletes all image files matching the pattern {baseFileName}{suffix}.*
    /// </summary>
    public static void DeleteImage(IFileSystemService fs, string directory, string baseFileName, string suffix,
        string? exceptExtension = null)
    {
        if (!fs.DirectoryExists(directory)) return;

        foreach (var ext in FileExtensions.ImageFileExtensions)
        {
            if (string.Equals(ext, exceptExtension, StringComparison.OrdinalIgnoreCase)) continue;
            var path = Path.Combine(directory, $"{baseFileName}{suffix}{ext}");
            if (fs.FileExists(path))
            {
                fs.DeleteFile(path);
            }
        }
    }

    /// <summary>
    ///     Saves processed image bytes to the destination directory.
    ///     Replaces the destination only after the new image has been written successfully.
    /// </summary>
    public static async Task<string?> SaveImageBytesAsync(IFileSystemService fs, string directory,
        string baseFileName, string suffix, byte[] imageBytes, string extension = ".jpg")
    {
        if (imageBytes.Length == 0) return null;

        fs.CreateDirectory(directory);

        var newFileName = $"{baseFileName}{suffix}{extension}";
        var destinationPath = Path.Combine(directory, newFileName);
        var temporaryPath = Path.Combine(directory, $"{newFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            await fs.WriteAllBytesAsync(temporaryPath, imageBytes).ConfigureAwait(false);
            fs.MoveFile(temporaryPath, destinationPath, true);
            DeleteImage(fs, directory, baseFileName, suffix, exceptExtension: extension);
            return destinationPath;
        }
        finally
        {
            if (fs.FileExists(temporaryPath)) fs.DeleteFile(temporaryPath);
        }
    }
}
