using Nagi.Core.Models;
using Nagi.Core.Models.Lyrics;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Service for loading, parsing, and interacting with .lrc lyric files.
/// </summary>
public interface ILrcService
{
    /// <summary>
    ///     Asynchronously loads and parses an LRC file from the path specified in the Song object.
    /// </summary>
    /// <param name="song">The song object containing the LrcFilePath.</param>
    /// <returns>A ParsedLrc object containing the timed lyrics, or null if parsing fails or the file doesn't exist.</returns>
    Task<ParsedLrc?> GetLyricsAsync(Song song);

    /// <summary>
    ///     Asynchronously loads and parses an LRC file from a direct file path.
    /// </summary>
    /// <param name="lrcFilePath">The full path to the .lrc file.</param>
    /// <returns>A ParsedLrc object containing the timed lyrics, or null if parsing fails or the file doesn't exist.</returns>
    Task<ParsedLrc?> GetLyricsAsync(string lrcFilePath);

    /// <summary>
    ///     Gets the current lyric line based on the playback time.
    /// </summary>
    /// <param name="parsedLrc">The parsed LRC data.</param>
    /// <param name="currentTime">The current playback time of the song.</param>
    /// <returns>The active LyricLine for the given time, or null if no line is active.</returns>
    LyricLine? GetCurrentLine(ParsedLrc parsedLrc, TimeSpan currentTime);

    /// <summary>
    ///     Gets the current lyric line using a search hint for optimal sequential performance.
    /// </summary>
    /// <param name="parsedLrc">The parsed LRC data.</param>
    /// <param name="currentTime">The current playback time.</param>
    /// <param name="searchStartIndex">A reference to the last known line index. This will be updated by the method.</param>
    /// <returns>The active LyricLine.</returns>
    LyricLine? GetCurrentLine(ParsedLrc parsedLrc, TimeSpan currentTime, ref int searchStartIndex);
}