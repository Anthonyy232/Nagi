using System.Text.RegularExpressions;
using ModernLrc;
using ModernLrc.Model;
using Nagi.Core.Models.Lyrics;

namespace Nagi.Core.Helpers;

public static partial class LyricsParser
{
    /// <returns>Timed lines when the text is LRC, otherwise its plain lines; null for blank text.</returns>
    public static SongLyrics? Parse(string? rawLyrics)
    {
        if (string.IsNullOrWhiteSpace(rawLyrics)) return null;

        var document = LrcParser.Parse(rawLyrics).Document;
        // LRC has no translation syntax, so files commonly repeat the original line's timestamp for it.
        var timedLines = document.Lines
            .GroupBy(line => document.GetEffectiveTime(line.Timestamp))
            .Select(group => CreateLine(group.Key, group.Select(line => line.GetText())))
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

    private static LyricLine CreateLine(TimeSpan startTime, IEnumerable<string> texts)
    {
        var nonEmpty = texts.Where(text => !string.IsNullOrWhiteSpace(text)).ToList();
        return new LyricLine(startTime, nonEmpty.FirstOrDefault() ?? string.Empty)
        {
            TranslatedText = nonEmpty.Count > 1 ? string.Join('\n', nonEmpty.Skip(1)) : null
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
