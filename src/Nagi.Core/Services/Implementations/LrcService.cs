using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Nagi.Core.Data;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Models.Lyrics;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Service for loading, parsing, and interacting with .lrc lyric files,
///     optimized for high-performance playback synchronization.
/// </summary>
public class LrcService : ILrcService
{
    private readonly IFileSystemService _fileSystemService;
    private readonly ILogger<LrcService> _logger;
    private readonly IOnlineLyricsService _onlineLyricsService;
    private readonly ISettingsService _settingsService;
    private readonly IPathConfiguration _pathConfig;
    private readonly ILibraryWriter _libraryWriter;
    private readonly LrcParser.Parser.Lrc.LrcParser _parser = new();

    public LrcService(
        IFileSystemService fileSystemService,
        IOnlineLyricsService onlineLyricsService,
        ISettingsService settingsService,
        IPathConfiguration pathConfig,
        ILibraryWriter libraryWriter,
        ILogger<LrcService> logger)
    {
        _fileSystemService = fileSystemService;
        _onlineLyricsService = onlineLyricsService;
        _settingsService = settingsService;
        _pathConfig = pathConfig;
        _libraryWriter = libraryWriter;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ParsedLrc?> GetLyricsAsync(Song song)
    {
        // 1. Try local file path from Song object
        if (!string.IsNullOrWhiteSpace(song.LrcFilePath) && _fileSystemService.FileExists(song.LrcFilePath))
            return await GetLyricsAsync(song.LrcFilePath);

        // 2. Try online fallback if enabled
        if (await _settingsService.GetFetchOnlineLyricsEnabledAsync())
        {
            var lrcContent = await _onlineLyricsService.GetLyricsAsync(
                song.Title, song.Artist?.Name, song.Album?.Title, song.Duration);
            
            if (!string.IsNullOrWhiteSpace(lrcContent))
            {
                // Cache the lyrics for future use
                await CacheLyricsAsync(song, lrcContent);

                // Parse the downloaded lyrics string
                return ParseLrcContent(lrcContent);
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<ParsedLrc?> GetLyricsAsync(string lrcFilePath)
    {
        if (string.IsNullOrWhiteSpace(lrcFilePath) || !_fileSystemService.FileExists(lrcFilePath)) return null;

        try
        {
            var fileContent = await _fileSystemService.ReadAllTextAsync(lrcFilePath);
            return ParseLrcContent(fileContent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse LRC file {LrcFilePath}", lrcFilePath);
            return null;
        }
    }

    private async Task CacheLyricsAsync(Song song, string lrcContent)
    {
        try
        {
            string cacheKey;
            using (var sha256 = SHA256.Create())
            {
                var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(song.FilePath));
                cacheKey = Convert.ToBase64String(hashBytes).Replace('/', '_').Replace('+', '-');
            }

            var cachedLrcPath = _fileSystemService.Combine(_pathConfig.LrcCachePath, $"{cacheKey}.lrc");

            await _fileSystemService.WriteAllTextAsync(cachedLrcPath, lrcContent);
            _logger.LogInformation("Cached online lyrics for song {SongId} to {Path}", song.Id, cachedLrcPath);

            // Update the database only if the path has changed
            if (song.LrcFilePath != cachedLrcPath)
            {
                song.LrcFilePath = cachedLrcPath;
                // We don't persist the full text to 'Lyrics' column here to keep the DB light,
                // as we rely on the LRC file. The 'Lyrics' column is mostly for unsynced lyrics.
                await _libraryWriter.UpdateSongLrcPathAsync(song.Id, cachedLrcPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cache lyrics for song {SongId}", song.Id);
        }
    }

    private ParsedLrc ParseLrcContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return new ParsedLrc(Enumerable.Empty<LyricLine>());

        try
        {
            var parsedSongFromLrc = _parser.Decode(content);
            if (parsedSongFromLrc?.Lyrics == null) return new ParsedLrc(Enumerable.Empty<LyricLine>());

            var lyricLines = parsedSongFromLrc.Lyrics
                .Select(lyric => new LyricLine
                {
                    StartTime = TimeSpan.FromMilliseconds(lyric.StartTime),
                    Text = lyric.Text
                })
                .OrderBy(l => l.StartTime)
                .ToList();

            return new ParsedLrc(lyricLines);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing LRC content");
            return new ParsedLrc(Enumerable.Empty<LyricLine>());
        }
    }

    /// <inheritdoc />
    public LyricLine? GetCurrentLine(ParsedLrc parsedLrc, TimeSpan currentTime)
    {
        if (parsedLrc is null || parsedLrc.IsEmpty) return null;

        var index = FindBestMatchIndex(parsedLrc.Lines, currentTime);
        return index != -1 ? parsedLrc.Lines[index] : null;
    }

    /// <inheritdoc />
    public LyricLine? GetCurrentLine(ParsedLrc parsedLrc, TimeSpan currentTime, ref int searchStartIndex)
    {
        if (parsedLrc is null || parsedLrc.IsEmpty) return null;

        var lines = parsedLrc.Lines;
        var lineCount = lines.Count;

        // Check if the current or next line is the correct one, which is the most common case during playback.
        if (searchStartIndex >= 0 && searchStartIndex < lineCount)
        {
            var currentLine = lines[searchStartIndex];
            var nextLineStartTime = searchStartIndex + 1 < lineCount
                ? lines[searchStartIndex + 1].StartTime
                : TimeSpan.MaxValue;

            if (currentLine.StartTime <= currentTime && currentTime < nextLineStartTime) return currentLine;
        }

        // If the hint was wrong (e.g., due to seeking), perform a full binary search.
        var bestMatchIndex = FindBestMatchIndex(lines, currentTime);
        searchStartIndex = bestMatchIndex != -1 ? bestMatchIndex : 0;

        return bestMatchIndex != -1 ? lines[bestMatchIndex] : null;
    }

    /// <summary>
    ///     Performs a binary search to find the index of the lyric line that should be active at the given time.
    /// </summary>
    private int FindBestMatchIndex(IReadOnlyList<LyricLine> lines, TimeSpan currentTime)
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
}