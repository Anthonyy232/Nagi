using LrcParser.Parser.Lrc;
using Nagi.Core.Models;
using Nagi.Core.Models.Lyrics;
using Nagi.Core.Services.Abstractions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Nagi.Core.Services.Implementations;

/// <summary>
/// Service for loading, parsing, and interacting with .lrc lyric files,
/// optimized for high-performance playback synchronization.
/// </summary>
public class LrcService : ILrcService {
    private readonly LrcParser.Parser.Lrc.LrcParser _parser = new();

    /// <inheritdoc />
    public async Task<ParsedLrc?> GetLyricsAsync(Song song) {
        if (song is null || string.IsNullOrWhiteSpace(song.LrcFilePath)) {
            return null;
        }
        return await GetLyricsAsync(song.LrcFilePath);
    }

    /// <inheritdoc />
    public async Task<ParsedLrc?> GetLyricsAsync(string lrcFilePath) {
        if (string.IsNullOrWhiteSpace(lrcFilePath) || !File.Exists(lrcFilePath)) {
            return null;
        }

        try {
            var fileContent = await File.ReadAllTextAsync(lrcFilePath);
            if (string.IsNullOrWhiteSpace(fileContent)) {
                return new ParsedLrc(Enumerable.Empty<LyricLine>());
            }

            var parsedSongFromLrc = _parser.Decode(fileContent);
            if (parsedSongFromLrc?.Lyrics == null) {
                return new ParsedLrc(Enumerable.Empty<LyricLine>());
            }

            var lyricLines = parsedSongFromLrc.Lyrics
                .Select(lyric => new LyricLine {
                    StartTime = TimeSpan.FromMilliseconds(lyric.StartTime),
                    Text = lyric.Text,
                })
                .OrderBy(l => l.StartTime)
                .ToList();

            return new ParsedLrc(lyricLines);
        }
        catch (Exception ex) {
            Debug.WriteLine($"[LrcService] ERROR: Failed to parse LRC file '{lrcFilePath}'. {ex.Message}");
            return null;
        }
    }

    /// <inheritdoc />
    public LyricLine? GetCurrentLine(ParsedLrc parsedLrc, TimeSpan currentTime) {
        if (parsedLrc is null || parsedLrc.IsEmpty) return null;

        int index = FindBestMatchIndex(parsedLrc.Lines, currentTime);
        return index != -1 ? parsedLrc.Lines[index] : null;
    }

    /// <inheritdoc />
    public LyricLine? GetCurrentLine(ParsedLrc parsedLrc, TimeSpan currentTime, ref int searchStartIndex) {
        if (parsedLrc is null || parsedLrc.IsEmpty) {
            return null;
        }

        var lines = parsedLrc.Lines;
        int lineCount = lines.Count;

        // Check if the current or next line is the correct one, which is the most common case during playback.
        if (searchStartIndex >= 0 && searchStartIndex < lineCount) {
            var currentLine = lines[searchStartIndex];
            var nextLineStartTime = (searchStartIndex + 1 < lineCount)
                ? lines[searchStartIndex + 1].StartTime
                : TimeSpan.MaxValue;

            if (currentLine.StartTime <= currentTime && currentTime < nextLineStartTime) {
                return currentLine;
            }
        }

        // If the hint was wrong (e.g., due to seeking), perform a full binary search.
        int bestMatchIndex = FindBestMatchIndex(lines, currentTime);
        searchStartIndex = bestMatchIndex != -1 ? bestMatchIndex : 0;

        return bestMatchIndex != -1 ? lines[bestMatchIndex] : null;
    }

    /// <summary>
    /// Performs a binary search to find the index of the lyric line that should be active at the given time.
    /// </summary>
    private int FindBestMatchIndex(IReadOnlyList<LyricLine> lines, TimeSpan currentTime) {
        int low = 0;
        int high = lines.Count - 1;
        int latestMatchIndex = -1;

        while (low <= high) {
            int mid = low + (high - low) / 2;
            if (lines[mid].StartTime <= currentTime) {
                latestMatchIndex = mid;
                low = mid + 1;
            }
            else {
                high = mid - 1;
            }
        }
        return latestMatchIndex;
    }
}