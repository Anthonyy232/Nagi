namespace Nagi.Core.Lyrics;

/// <param name="RawLyrics">The source text, including any timestamps, as it is edited and cached.</param>
/// <param name="Lines">The timed lines in time order when <paramref name="IsSynced" />, otherwise the lines of the text.</param>
public sealed record SongLyrics(string RawLyrics, IReadOnlyList<LyricLine> Lines, bool IsSynced);
