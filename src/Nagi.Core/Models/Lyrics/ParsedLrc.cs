namespace Nagi.Core.Models.Lyrics;

/// <summary>
///     Represents a fully parsed LRC file with a collection of timed lyric lines,
///     sorted by their start time.
/// </summary>
public class ParsedLrc
{
    public ParsedLrc(IEnumerable<LyricLine> lines, string? rawLyrics = null)
    {
        Lines = lines.OrderBy(l => l.StartTime).ToList().AsReadOnly();
        RawLyrics = rawLyrics;
    }

    /// <summary>
    ///     A sorted, read-only list of all lyric lines.
    /// </summary>
    public IReadOnlyList<LyricLine> Lines { get; }

    /// <summary>
    ///     Original source text retained for plain-text display and caching, including any timestamps.
    /// </summary>
    public string? RawLyrics { get; }

    /// <summary>
    ///     Indicates if the parsed lyrics contain any lines.
    /// </summary>
    public bool IsEmpty => Lines.Count == 0;
}
