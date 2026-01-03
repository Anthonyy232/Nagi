using System.Linq;
using System.Text.RegularExpressions;
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
    private readonly INetEaseLyricsService _netEaseLyricsService;
    private readonly ISettingsService _settingsService;
    private readonly IPathConfiguration _pathConfig;
    private readonly ILibraryWriter _libraryWriter;
    private readonly LrcParser.Parser.Lrc.LrcParser _parser = new();

    public LrcService(
        IFileSystemService fileSystemService,
        IOnlineLyricsService onlineLyricsService,
        INetEaseLyricsService netEaseLyricsService,
        ISettingsService settingsService,
        IPathConfiguration pathConfig,
        ILibraryWriter libraryWriter,
        ILogger<LrcService> logger)
    {
        _fileSystemService = fileSystemService;
        _onlineLyricsService = onlineLyricsService;
        _netEaseLyricsService = netEaseLyricsService;
        _settingsService = settingsService;
        _pathConfig = pathConfig;
        _libraryWriter = libraryWriter;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ParsedLrc?> GetLyricsAsync(Song song, CancellationToken cancellationToken = default)
    {
        // 1. Try local file path from Song object
        if (!string.IsNullOrWhiteSpace(song.LrcFilePath) && _fileSystemService.FileExists(song.LrcFilePath))
            return await GetLyricsAsync(song.LrcFilePath);

        // 2. Try online fallback if enabled AND never checked before
        var neverChecked = song.LyricsLastCheckedUtc == null;
        if (neverChecked && await _settingsService.GetFetchOnlineLyricsEnabledAsync())
        {
            // Check for cancellation before making any online calls
            if (cancellationToken.IsCancellationRequested)
                return null;

            // 2a. Fire both requests in parallel for faster resolution
            var lrcLibTask = _onlineLyricsService.GetLyricsAsync(
                song.Title, song.Artist?.Name, song.Album?.Title, song.Duration, cancellationToken);
            var netEaseTask = _netEaseLyricsService.SearchLyricsAsync(
                song.Title, song.Artist?.Name, cancellationToken);
            
            // Wait for LRCLIB first (preferred source - community-curated, better quality)
            var lrcContent = await lrcLibTask;
            
            // 2b. Use LRCLIB result if available, otherwise wait for NetEase
            if (string.IsNullOrWhiteSpace(lrcContent) && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("LRCLIB returned no results for '{Title}', using NetEase result.", song.Title);
                lrcContent = await netEaseTask;
            }
            else
            {
                // Observe the NetEase task to prevent unobserved exceptions (handles both faults and cancellations)
                _ = netEaseTask.ContinueWith(
                    static (t, state) =>
                    {
                        var logger = (ILogger<LrcService>)state!;
                        if (t.IsFaulted)
                            logger.LogDebug(t.Exception?.InnerException, "NetEase task faulted (ignored, LRCLIB succeeded)");
                        // Cancelled tasks are silently observed - no logging needed
                    },
                    _logger,
                    TaskContinuationOptions.NotOnRanToCompletion);
            }
            
            // Only mark as checked if the operation wasn't cancelled - otherwise we'll retry next time
            if (!cancellationToken.IsCancellationRequested)
            {
                await _libraryWriter.UpdateSongLyricsLastCheckedAsync(song.Id);
                song.LyricsLastCheckedUtc = DateTime.UtcNow;
            }
            
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
            var cacheFileName = FileNameHelper.GenerateLrcCacheFileName(song.Artist?.Name, song.Album?.Title, song.Title);
            var cachedLrcPath = _fileSystemService.Combine(_pathConfig.LrcCachePath, cacheFileName);

            await _fileSystemService.WriteAllTextAsync(cachedLrcPath, lrcContent);
            _logger.LogDebug("Cached online lyrics for song {SongId} to {Path}", song.Id, cachedLrcPath);

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
            // Normalize NetEase-style 3-digit milliseconds ([mm:ss.fff]) to 2-digit ([mm:ss.ff]) 
            // for better compatibility with standard LRC parsers.
            content = Regex.Replace(content, @"(\[\d{2}:\d{2}\.\d{2})\d", "$1");

            var parsedSongFromLrc = _parser.Decode(content);
            if (parsedSongFromLrc?.Lyrics == null || !parsedSongFromLrc.Lyrics.Any())
                return new ParsedLrc(Enumerable.Empty<LyricLine>());

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