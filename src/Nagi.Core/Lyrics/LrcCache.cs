using System.Security.Cryptography;
using System.Text;
using Nagi.Core.Library;
using Nagi.Core.Platform;

namespace Nagi.Core.Lyrics;

/// <summary>
///     Names the lyrics files in Nagi's cache. A file belongs to its song through a hash of the audio file's identity,
///     so cached lyrics survive tag edits; the descriptive part of a name only helps people browsing the folder. An
///     override, the user's own edit, takes precedence over lyrics Nagi cached.
/// </summary>
internal static class LrcCache
{
    /// <summary>The audio file's path, or the song's id for a song without one.</summary>
    public static string Identity(Song song) =>
        string.IsNullOrWhiteSpace(song.FilePath) ? song.Id.ToString("N") : song.FilePath;

    public static string CachedPath(string cacheRoot, string audioIdentity, string? artist, string? album, string? title)
    {
        var sanitizedArtist = FileNameHelper.SanitizeFileName(artist ?? string.Empty, Artist.UnknownArtistName);
        var sanitizedAlbum = FileNameHelper.SanitizeFileName(album ?? string.Empty, Album.UnknownAlbumName);
        var sanitizedTitle = FileNameHelper.SanitizeFileName(title ?? string.Empty, Song.UnknownTitle);

        var descriptivePrefix = $"{sanitizedArtist} - {sanitizedAlbum} - {sanitizedTitle}";
        const int maxPrefixLength = 140;
        if (descriptivePrefix.Length > maxPrefixLength)
            descriptivePrefix = descriptivePrefix[..maxPrefixLength].TrimEnd();

        return Path.Combine(cacheRoot, $"{descriptivePrefix} - {IdentityHash(audioIdentity)}.lrc");
    }

    public static string OverridePath(string cacheRoot, string audioIdentity) =>
        Path.Combine(cacheRoot, $"{IdentityHash(audioIdentity)}.override.lrc");

    public static bool Contains(string cacheRoot, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(cacheRoot)) return false;

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheRoot)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Whether a path in the cache was written before names carried the identity hash. Such a file may belong to
    ///     another song with the same tags, so it isn't safe to reuse. Databases from those versions still hold them.
    /// </summary>
    public static bool IsLegacy(string cacheRoot, string path, string audioIdentity)
    {
        if (!Contains(cacheRoot, path)) return false;

        var hash = IdentityHash(audioIdentity);
        var fileName = Path.GetFileName(path);
        return !fileName.EndsWith($" - {hash}.lrc", StringComparison.OrdinalIgnoreCase)
               && !fileName.Equals($"{hash}.override.lrc", StringComparison.OrdinalIgnoreCase);
    }

    private static string IdentityHash(string audioIdentity)
    {
        var normalizedIdentity = PathCanonicalizer.Normalize(audioIdentity).ToUpperInvariant();
        var identityHash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedIdentity));
        return Convert.ToHexString(identityHash.AsSpan(0, 12));
    }
}
