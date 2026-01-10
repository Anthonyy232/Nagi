using Nagi.Core.Constants;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Helpers;

/// <summary>
///     Provides standardized methods for managing entity images (Artists, Playlists, etc.)
///     with support for multiple file extensions and priority handling.
/// </summary>
public static class ImageStorageHelper
{
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
    ///     Saves a source image to the destination directory with the specified base name and suffix.
    ///     Preserves the extension of the source file.
    ///     Automatically deletes any OTHER existing images with the same base name and suffix (to prevent duplicates like .jpg AND .png).
    /// </summary>
    public static void SaveImage(IFileSystemService fs, string directory, string baseFileName, string suffix, string sourceFilePath)
    {
        if (!fs.FileExists(sourceFilePath)) return;

        if (!fs.DirectoryExists(directory))
        {
            fs.CreateDirectory(directory);
        }

        // 1. Determine new extension
        var extension = fs.GetExtension(sourceFilePath).ToLowerInvariant();
        if (!FileExtensions.ImageFileExtensions.Contains(extension))
        {
            // Fallback or error? For now, we'll proceed or maybe default to allowed one.
            // But strict checking might be better. enforcing checks from caller.
        }

        var newFileName = $"{baseFileName}{suffix}{extension}";
        var destinationPath = fs.Combine(directory, newFileName);

        // 2. Delete ALL existing variants to ensure we don't have partial duplicates
        DeleteImage(fs, directory, baseFileName, suffix);

        // 3. Copy the file
        fs.CopyFile(sourceFilePath, destinationPath, true);
    }

    /// <summary>
    ///     Deletes all image files matching the pattern {baseFileName}{suffix}.*
    /// </summary>
    public static void DeleteImage(IFileSystemService fs, string directory, string baseFileName, string suffix)
    {
        if (!fs.DirectoryExists(directory)) return;

        foreach (var ext in FileExtensions.ImageFileExtensions)
        {
            var path = fs.Combine(directory, $"{baseFileName}{suffix}{ext}");
            if (fs.FileExists(path))
            {
                fs.DeleteFile(path);
            }
        }
    }

    /// <summary>
    ///     Saves processed image bytes to the destination directory.
    ///     Deletes any existing images with the same base name and suffix.
    /// </summary>
    /// <param name="fs">File system service.</param>
    /// <param name="directory">Target directory.</param>
    /// <param name="baseFileName">Base file name (typically entity ID).</param>
    /// <param name="suffix">File suffix (e.g., ".custom", ".fetched").</param>
    /// <param name="imageBytes">Processed image bytes to save.</param>
    /// <param name="extension">File extension to use (default ".jpg").</param>
    public static async Task SaveImageBytesAsync(IFileSystemService fs, string directory,
        string baseFileName, string suffix, byte[] imageBytes, string extension = ".jpg")
    {
        if (imageBytes.Length == 0) return;

        if (!fs.DirectoryExists(directory))
            fs.CreateDirectory(directory);

        // Delete all existing variants to prevent duplicates
        DeleteImage(fs, directory, baseFileName, suffix);

        // Save new file
        var newFileName = $"{baseFileName}{suffix}{extension}";
        var destinationPath = fs.Combine(directory, newFileName);
        await fs.WriteAllBytesAsync(destinationPath, imageBytes).ConfigureAwait(false);
    }
}
