using System.Linq.Expressions;
using Nagi.Core.Library;
using Nagi.Core.Metadata;
using Nagi.Core.Platform;

namespace Nagi.Core.Artwork;

/// <summary>
///     Where a cached image came from. Its file name, <c>{id}{suffix}{extension}</c>, records the kind, so an online
///     lookup never replaces an image the user picked or one from the music folder.
/// </summary>
internal enum CachedImageKind
{
    Fetched,
    Folder,
    Custom
}

public static class ImageStorageHelper
{
    private const string SavedExtension = ".jpg";

    internal static string Suffix(this CachedImageKind kind) => kind switch
    {
        CachedImageKind.Fetched => ".fetched",
        CachedImageKind.Folder => ".local",
        CachedImageKind.Custom => ".custom",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    /// <summary>Whether a cached image is one the user picked, which online lookups must not replace.</summary>
    public static bool IsCustomImage(string? path) => IsOfKind(path, CachedImageKind.Custom);

    /// <summary>Whether a cached artist image came from the user or the music folder rather than an online provider.</summary>
    internal static bool IsUserOrFolderImage(string? path) =>
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
    internal static string? FindImage(IFileSystemService fs, string directory, string baseFileName, CachedImageKind kind) =>
        fs.DirectoryExists(directory) ? ImagePaths(directory, baseFileName, kind).FirstOrDefault(fs.FileExists) : null;

    internal static void DeleteImage(IFileSystemService fs, string directory, string baseFileName, CachedImageKind kind)
    {
        if (!fs.DirectoryExists(directory)) return;
        foreach (var path in ImagePaths(directory, baseFileName, kind).Where(fs.FileExists))
            fs.DeleteFile(path);
    }

    /// <summary>
    ///     Saves the image as <c>{baseFileName}{suffix}.jpg</c> and removes the previous one of that kind only after the
    ///     new one has been written.
    /// </summary>
    internal static async Task<string?> SaveImageBytesAsync(IFileSystemService fs, string directory,
        string baseFileName, CachedImageKind kind, byte[] imageBytes)
    {
        if (imageBytes.Length == 0) return null;

        fs.CreateDirectory(directory);

        var destinationPath = Path.Combine(directory, $"{baseFileName}{kind.Suffix()}{SavedExtension}");
        var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await fs.WriteAllBytesAsync(temporaryPath, imageBytes).ConfigureAwait(false);
            fs.MoveFile(temporaryPath, destinationPath, true);
            foreach (var path in ImagePaths(directory, baseFileName, kind)
                         .Where(path => path != destinationPath && fs.FileExists(path)))
                fs.DeleteFile(path);
            return destinationPath;
        }
        finally
        {
            if (fs.FileExists(temporaryPath)) fs.DeleteFile(temporaryPath);
        }
    }

    private static IEnumerable<string> ImagePaths(string directory, string baseFileName, CachedImageKind kind) =>
        FileExtensions.ImageFileExtensions.Select(extension =>
            Path.Combine(directory, $"{baseFileName}{kind.Suffix()}{extension}"));
}
