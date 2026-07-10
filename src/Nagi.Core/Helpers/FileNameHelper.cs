using System.IO;
using System.Security.Cryptography;
using System.Text;
using Nagi.Core.Models;

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
    public static string SanitizeFileName(string name, string? fallback = null)
    {
        fallback ??= Resources.Strings.UnknownFilename;
        if (string.IsNullOrWhiteSpace(name))
            return fallback;

        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Where(c => !invalidChars.Contains(c)).ToArray());
        return ArtistNameHelper.NormalizeStringCore(sanitized) ?? fallback;
    }

    /// <summary>
    ///     Generates a deterministic, collision-resistant cache file name for an audio file's lyrics.
    /// </summary>
    /// <param name="audioFileIdentity">The audio file path, or another stable per-song identity.</param>
    /// <param name="artist">The artist name.</param>
    /// <param name="album">The album name.</param>
    /// <param name="title">The song title.</param>
    /// <returns>A sanitized filename with a stable identity hash.</returns>
    public static string GenerateLrcCacheFileName(
        string audioFileIdentity,
        string? artist,
        string? album,
        string? title)
    {
        var sanitizedArtist = SanitizeFileName(artist ?? string.Empty, Artist.UnknownArtistName);
        var sanitizedAlbum = SanitizeFileName(album ?? string.Empty, Album.UnknownAlbumName);
        var sanitizedTitle = SanitizeFileName(title ?? string.Empty, string.Format(Resources.Strings.Format_Unknown, Resources.Strings.Label_Title));

        var descriptivePrefix = $"{sanitizedArtist} - {sanitizedAlbum} - {sanitizedTitle}";
        const int maxPrefixLength = 140;
        if (descriptivePrefix.Length > maxPrefixLength)
            descriptivePrefix = descriptivePrefix[..maxPrefixLength].TrimEnd();

        var normalizedIdentity = PathCanonicalizer.Normalize(audioFileIdentity).ToUpperInvariant();
        var identityHash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedIdentity));
        var hashSuffix = Convert.ToHexString(identityHash.AsSpan(0, 12));

        return $"{descriptivePrefix} - {hashSuffix}.lrc";
    }
}
