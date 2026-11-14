using System.Text.RegularExpressions;
using ModernLrc;
using ModernLrc.Model;

namespace Nagi.Core.Lyrics;

public static partial class LyricsParser
{
    /// <returns>Timed lines when the text is LRC or TTML, otherwise its plain lines; null for blank text.</returns>
    public static SongLyrics? Parse(string? rawLyrics)
    {
        if (string.IsNullOrWhiteSpace(rawLyrics)) return null;
        if (TtmlLyricsParser.Parse(rawLyrics) is { } ttmlLyrics) return ttmlLyrics;

        var document = LrcParser.Parse(rawLyrics).Document;
        // LRC has no translation syntax, so files commonly repeat the original line's timestamp for it.
        var timedLines = document.Lines
            .GroupBy(line => document.GetEffectiveTime(line.Timestamp))
            .Select(group => CreateLine(document, group.Key, group))
            .OrderBy(line => line.StartTime)
            .ToList();
        if (timedLines.Count > 0) return new SongLyrics(rawLyrics, timedLines, IsSynced: true);

        // Blank lines separate verses, but a run of them only adds space.
        var plainLines = MultipleNewlines().Replace(rawLyrics.ReplaceLineEndings("\n"), "\n\n")
            .Split('\n', StringSplitOptions.TrimEntries)
            .Select(text => new LyricLine(TimeSpan.Zero, text))
            .ToList();
        return new SongLyrics(rawLyrics, plainLines, IsSynced: false);
    }

    /// <summary>Finds the line playing at <paramref name="currentTime" /> in timed lines sorted by start time.</summary>
    /// <param name="searchStartIndex">
    ///     The index of the line found last time, which makes sequential playback a constant-time check. Updated to the
    ///     line found.
    /// </param>
    public static LyricLine? GetCurrentLine(IReadOnlyList<LyricLine> lines, TimeSpan currentTime, ref int searchStartIndex)
    {
        if (lines.Count == 0) return null;

        if (searchStartIndex >= 0 && searchStartIndex < lines.Count)
        {
            var currentLine = lines[searchStartIndex];
            var nextLineStartTime = searchStartIndex + 1 < lines.Count
                ? lines[searchStartIndex + 1].StartTime
                : TimeSpan.MaxValue;

            if (currentLine.StartTime <= currentTime && currentTime < nextLineStartTime) return currentLine;
        }

        // The hint was wrong, for example after a seek.
        var bestMatchIndex = FindLastStartedIndex(lines, currentTime);
        searchStartIndex = bestMatchIndex != -1 ? bestMatchIndex : 0;

        return bestMatchIndex != -1 ? lines[bestMatchIndex] : null;
    }

    private static LyricLine CreateLine(LrcDocument document, TimeSpan startTime, IEnumerable<LrcLine> sourceLines)
    {
        var nonEmpty = sourceLines.Where(line => !string.IsNullOrWhiteSpace(line.GetText())).ToList();
        var original = nonEmpty.FirstOrDefault();
        return new LyricLine(startTime, original?.GetText() ?? string.Empty)
        {
            TranslatedText = nonEmpty.Count > 1 ? string.Join('\n', nonEmpty.Skip(1).Select(line => line.GetText())) : null,
            Words = original is LrcEnhancedLine enhanced
                ? enhanced.Words.Select(word => new LyricWord(document.GetEffectiveTime(word.Timestamp), word.Text)).ToList()
                : []
        };
    }

    private static int FindLastStartedIndex(IReadOnlyList<LyricLine> lines, TimeSpan currentTime)
    {
        var low = 0;
        var high = lines.Count - 1;
        var latestMatchIndex = -1;

        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            if (lines[mid].StartTime <= currentTime)
            {
                latestMatchIndex = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return latestMatchIndex;
    }

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MultipleNewlines();
}
