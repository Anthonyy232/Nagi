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
        ".asf", ".mp4", ".m4v"
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
    ///     Common file names (without extension) used for album cover art.
    ///     Uses case-insensitive comparison for matching file names.
    /// </summary>
    public static readonly HashSet<string> CoverArtFileNames = new(new[]
    {
        "cover", "folder", "album", "front"
    }, StringComparer.OrdinalIgnoreCase);
}
