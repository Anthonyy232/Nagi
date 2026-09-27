using Nagi.Core.Constants;
using Nagi.Core.Services.Abstractions;
using Microsoft.Extensions.Logging;

namespace Nagi.Core.Helpers;

/// <summary>
///     Provides standardized methods for managing entity images (Artists, Playlists, etc.)
///     with support for multiple file extensions and priority handling.
/// </summary>
public static class ImageStorageHelper
{
    internal static string? FindCoverArtInDirectory(IFileSystemService fileSystem, string directory, ILogger logger)
    {
        try
        {
            return fileSystem.GetFiles(directory, "*.*")
                .Where(path => !fileSystem.IsHiddenOrSystemFile(path) &&
                               FileExtensions.ImageFileExtensions.Contains(fileSystem.GetExtension(path)))
                .Select(path => (Path: path, Priority: FileExtensions.GetCoverArtPriority(fileSystem.GetFileNameWithoutExtension(path))))
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
            var path = fs.Combine(directory, $"{baseFileName}{suffix}{ext}");
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
            var path = fs.Combine(directory, $"{baseFileName}{suffix}{ext}");
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
    /// <param name="fs">File system service.</param>
    /// <param name="directory">Target directory.</param>
    /// <param name="baseFileName">Base file name (typically entity ID).</param>
    /// <param name="suffix">File suffix (e.g., ".custom", ".fetched").</param>
    /// <param name="imageBytes">Processed image bytes to save.</param>
    /// <param name="extension">File extension to use (default ".jpg").</param>
    public static async Task<string?> SaveImageBytesAsync(IFileSystemService fs, string directory,
        string baseFileName, string suffix, byte[] imageBytes, string extension = ".jpg")
    {
        if (imageBytes.Length == 0) return null;

        if (!fs.DirectoryExists(directory))
            fs.CreateDirectory(directory);

        var newFileName = $"{baseFileName}{suffix}{extension}";
        var destinationPath = fs.Combine(directory, newFileName);
        var temporaryPath = fs.Combine(directory, $"{newFileName}.{Guid.NewGuid():N}.tmp");
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
