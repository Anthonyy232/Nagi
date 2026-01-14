namespace Nagi.Core.Models.Lyrics;

/// <summary>
///     Represents a fully parsed LRC file with a collection of timed lyric lines,
///     sorted by their start time.
/// </summary>
public class ParsedLrc
{
    public ParsedLrc(IEnumerable<LyricLine> lines, string? rawUnsyncedLyrics = null)
    {
        // Ensure lines are sorted by time, which is crucial for playback syncing.
        Lines = lines.OrderBy(l => l.StartTime).ToList().AsReadOnly();
        RawUnsyncedLyrics = rawUnsyncedLyrics;
    }

    /// <summary>
    ///     A sorted, read-only list of all lyric lines.
    /// </summary>
    public IReadOnlyList<LyricLine> Lines { get; }

    /// <summary>
    ///     The raw unsynchronized lyrics text, available if the source was a plain text file
    ///     or an LRC file without timestamps.
    /// </summary>
    public string? RawUnsyncedLyrics { get; }

    /// <summary>
    ///     Indicates if the parsed lyrics contain any lines.
    /// </summary>
    public bool IsEmpty => !Lines.Any();
}