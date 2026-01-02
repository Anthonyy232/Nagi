using System.IO;

namespace Nagi.Core.Helpers;

/// <summary>
///     Provides utility methods for working with file names.
/// </summary>
public static class FileNameHelper
{
    /// <summary>
    ///     Sanitizes a string for use as a file name by removing characters that are
    ///     invalid on the file system (e.g., \ / : * ? " &lt; &gt; |).
    /// </summary>
    /// <param name="name">The string to sanitize.</param>
    /// <param name="fallback">The fallback name if the sanitized result is empty.</param>
    /// <returns>A sanitized string safe for use as a file name.</returns>
    public static string SanitizeFileName(string name, string fallback = "unknown")
    {
        if (string.IsNullOrWhiteSpace(name))
            return fallback;

        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Where(c => !invalidChars.Contains(c)).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized.Trim();
    }

    /// <summary>
    ///     Generates a cache file name for LRC files using the "Artist - Album - Title.lrc" format.
    /// </summary>
    /// <param name="artist">The artist name.</param>
    /// <param name="album">The album name.</param>
    /// <param name="title">The song title.</param>
    /// <returns>A sanitized file name in the format "Artist - Album - Title.lrc".</returns>
    public static string GenerateLrcCacheFileName(string? artist, string? album, string? title)
    {
        var sanitizedArtist = SanitizeFileName(artist ?? string.Empty, "Unknown Artist");
        var sanitizedAlbum = SanitizeFileName(album ?? string.Empty, "Unknown Album");
        var sanitizedTitle = SanitizeFileName(title ?? string.Empty, "Unknown Title");
        return $"{sanitizedArtist} - {sanitizedAlbum} - {sanitizedTitle}.lrc";
    }
}
