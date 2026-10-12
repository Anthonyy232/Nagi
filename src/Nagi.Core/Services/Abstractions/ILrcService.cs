using Nagi.Core.Models;
using Nagi.Core.Models.Lyrics;

namespace Nagi.Core.Services.Abstractions;

public interface ILrcService
{
    /// <summary>
    ///     Loads local lyrics or fetches them from the enabled providers.
    /// </summary>
    /// <returns>Parsed lyrics, or null if no lyrics could be loaded.</returns>
    Task<ParsedLrc?> GetLyricsAsync(Song song, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Asynchronously loads and parses an LRC file from a direct file path.
    /// </summary>
    /// <returns>A ParsedLrc object containing the timed lyrics, or null if parsing fails or the file doesn't exist.</returns>
    Task<ParsedLrc?> GetLyricsAsync(string lrcFilePath);

    /// <summary>Saves a lyrics override in Nagi's cache.</summary>
    Task SaveLyricsAsync(Song song, string lrcContent);

    /// <summary>Removes cached lyrics without deleting external sidecars.</summary>
    Task<bool> RemoveCachedLyricsAsync(Song song);

    /// <summary>Returns whether Nagi manages the song's lyrics file.</summary>
    bool HasCachedLyrics(Song song);

    /// <summary>
    ///     Parses a raw LRC string into a ParsedLrc object, which may contain synced or unsynced lyrics.
    /// </summary>
    ParsedLrc ParseLyrics(string? lrcContent);

    /// <summary>
    ///     Gets the current lyric line using a search hint for optimal sequential performance.
    /// </summary>
    /// <param name="searchStartIndex">A reference to the last known line index. This will be updated by the method.</param>
    LyricLine? GetCurrentLine(ParsedLrc parsedLrc, TimeSpan currentTime, ref int searchStartIndex);
}
