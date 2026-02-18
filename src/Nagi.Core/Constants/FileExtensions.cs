namespace Nagi.Core.Constants;

/// <summary>
///     Provides centralized file extension constants for music files and cover art images.
/// </summary>
public static class FileExtensions
{
    /// <summary>
    ///     Supported audio and video file extensions for the music library.
    /// </summary>
    public static readonly HashSet<string> MusicFileExtensions = new(new[]
    {
        ".aa", ".aax", ".aac", ".aiff", ".ape", ".dsf", ".flac",
        ".m4a", ".m4b", ".m4p", ".mp3", ".mpc", ".mpp", ".ogg",
        ".oga", ".opus", ".wav", ".wma", ".wv", ".webm",
        ".asf", ".mp4", ".m4v",
        ".mpeg", ".mpg", ".mpe", ".mpv", ".m2v"
    }, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Supported image file extensions for cover art (WinUI 3 BitmapImage compatible).
    /// </summary>
    public static readonly HashSet<string> ImageFileExtensions = new(new[]
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif", ".ico"
    }, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Priority-ordered list of file names (without extension) for album cover art.
    ///     Index 0 = highest priority. "album" is kept as an extension beyond Navidrome's default.
    ///     NOTE: "front" deliberately precedes "album" to match Navidrome's documented order
    ///     (cover.*, folder.*, front.*). This differs from the legacy order (cover, folder, album, front).
    /// </summary>
    public static readonly IReadOnlyList<string> CoverArtFileNamePriority =
        new[] { "cover", "folder", "front", "album" };

    /// <summary>
    ///     Returns the scan priority for a cover art file name (lower = higher priority).
    ///     Returns <see cref="int.MaxValue"/> for unrecognised names.
    ///     Single source of truth consumed by all cover-art lookup code.
    /// </summary>
    public static int GetCoverArtPriority(string fileNameWithoutExt)
    {
        for (var i = 0; i < CoverArtFileNamePriority.Count; i++)
        {
            if (CoverArtFileNamePriority[i].Equals(fileNameWithoutExt, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return int.MaxValue;
    }

    /// <summary>
    ///     Common file names (without extension) used for album cover art.
    ///     Derived from <see cref="CoverArtFileNamePriority"/> for O(1) membership tests.
    /// </summary>
    public static readonly HashSet<string> CoverArtFileNames = new(
        CoverArtFileNamePriority, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Priority-ordered list of file names (without extension) for artist images.
    ///     Matches Navidrome's "artist.*" prefix scanning.
    /// </summary>
    public static readonly IReadOnlyList<string> ArtistImageFileNamePriority =
        new[] { "artist" };

    /// <summary>
    ///     Returns the scan priority for an artist image file name (lower = higher priority).
    ///     Returns <see cref="int.MaxValue"/> for unrecognised names.
    ///     Single source of truth consumed by all artist-art lookup code.
    /// </summary>
    public static int GetArtistArtPriority(string fileNameWithoutExt)
    {
        for (var i = 0; i < ArtistImageFileNamePriority.Count; i++)
        {
            if (ArtistImageFileNamePriority[i].Equals(fileNameWithoutExt, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return int.MaxValue;
    }

    /// <summary>
    ///     File names (without extension) used for artist images in local folders.
    ///     Derived from <see cref="ArtistImageFileNamePriority"/> for O(1) membership tests.
    /// </summary>
    public static readonly HashSet<string> ArtistImageFileNames = new(
        ArtistImageFileNamePriority, StringComparer.OrdinalIgnoreCase);
}
