using System.Linq.Expressions;
using Nagi.Core.Library;
using Nagi.Core.Metadata;
using Nagi.Core.Platform;

namespace Nagi.Core.Artwork;

/// <summary>
///     Where a cached artist or playlist image came from. Its file name, <c>{id}{suffix}{extension}</c>, records the
///     kind, so an online lookup never replaces an image the user picked or one from the music folder.
/// </summary>
public enum CachedImageKind
{
    Fetched,
    Folder,
    Custom
}

public static class ImageStorageHelper
{
    public static string Suffix(this CachedImageKind kind) => kind switch
    {
        CachedImageKind.Fetched => ".fetched",
        CachedImageKind.Folder => ".local",
        CachedImageKind.Custom => ".custom",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    /// <summary>Whether a cached image is one the user picked, which online lookups must not replace.</summary>
    public static bool IsCustomImage(string? path) => IsOfKind(path, CachedImageKind.Custom);

    /// <summary>Whether a cached artist image came from the user or the music folder rather than an online provider.</summary>
    public static bool IsUserOrFolderImage(string? path) =>
        IsCustomImage(path) || IsOfKind(path, CachedImageKind.Folder);

    /// <summary>
    ///     <see cref="IsUserOrFolderImage" /> as a filter the database can run.
    /// </summary>
    internal static Expression<Func<Artist, bool>> HasNoUserOrFolderImage()
    {
        var custom = Marker(CachedImageKind.Custom);
        var folder = Marker(CachedImageKind.Folder);
        return artist => artist.LocalImageCachePath == null ||
                         (!artist.LocalImageCachePath.Contains(custom) && !artist.LocalImageCachePath.Contains(folder));
    }

    private static bool IsOfKind(string? path, CachedImageKind kind) =>
        path?.Contains(Marker(kind), StringComparison.OrdinalIgnoreCase) == true;

    // The suffix is followed by the extension's dot, which keeps an ID or folder name containing the suffix from matching.
    private static string Marker(CachedImageKind kind) => kind.Suffix() + ".";

    /// <returns>The image of that kind with any supported extension, or null if there is none.</returns>
    public static string? FindImage(IFileSystemService fs, string directory, string baseFileName, CachedImageKind kind) =>
        FindImage(fs, directory, baseFileName, kind.Suffix());

    /// <summary>
    ///     Finds the first existing image file matching the pattern {baseFileName}{suffix}.*
    ///     checks against all supported image extensions.
    /// </summary>
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

    public static void DeleteImage(IFileSystemService fs, string directory, string baseFileName, CachedImageKind kind) =>
        DeleteImage(fs, directory, baseFileName, kind.Suffix());

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

    public static Task<string?> SaveImageBytesAsync(IFileSystemService fs, string directory, string baseFileName,
        CachedImageKind kind, byte[] imageBytes) =>
        SaveImageBytesAsync(fs, directory, baseFileName, kind.Suffix(), imageBytes);

    /// <summary>
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
