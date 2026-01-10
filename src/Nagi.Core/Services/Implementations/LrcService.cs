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
public class LrcService : ILrcService, IDisposable
{
    private readonly IFileSystemService _fileSystemService;
    private readonly ILogger<LrcService> _logger;
    private readonly IOnlineLyricsService _onlineLyricsService;
    private readonly INetEaseLyricsService _netEaseLyricsService;
    private readonly ISettingsService _settingsService;
    private readonly IPathConfiguration _pathConfig;
    private readonly ILibraryWriter _libraryWriter;
    private readonly LrcParser.Parser.Lrc.LrcParser _parser = new();
    
    private readonly object _ctsLock = new();
    private CancellationTokenSource _settingsCts = new();
    private readonly List<CancellationTokenSource> _staleCts = new();
    private bool _disposed;

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
        _settingsService.FetchOnlineLyricsEnabledChanged += OnFetchOnlineLyricsEnabledChanged;
    }

    /// <inheritdoc />
    public async Task<ParsedLrc?> GetLyricsAsync(Song song, CancellationToken cancellationToken = default)
    {
        // 1. Try local file path from Song object
        if (!string.IsNullOrWhiteSpace(song.LrcFilePath) && _fileSystemService.FileExists(song.LrcFilePath))
            return await GetLyricsAsync(song.LrcFilePath).ConfigureAwait(false);

        // 2. Try online fallback if enabled AND never checked before
        var neverChecked = song.LyricsLastCheckedUtc == null;
        if (neverChecked && await _settingsService.GetFetchOnlineLyricsEnabledAsync().ConfigureAwait(false))
        {
            CancellationToken settingsToken;
            lock (_ctsLock)
            {
                if (_disposed) return null;
                settingsToken = _settingsCts.Token;
            }

            // Use a linked token source to handle both caller cancellation AND settings toggle
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, settingsToken);
            var token = linkedCts.Token;

            // Check for cancellation before making any online calls
            if (token.IsCancellationRequested)
                return null;

            // Get enabled providers sorted by priority
            var enabledProviders = await _settingsService.GetEnabledServiceProvidersAsync(Models.ServiceCategory.Lyrics).ConfigureAwait(false);

            if (enabledProviders.Count == 0)
            {
                _logger.LogDebug("No lyrics providers enabled. Skipping online fetch for '{Title}'.", song.Title);
            }
            else
            {
                _logger.LogDebug("Using lyrics providers for '{Title}': {Providers}", 
                    song.Title, string.Join(", ", enabledProviders.Select(p => p.Id)));
                // Start all enabled provider tasks in parallel for speed
                var tasks = new Dictionary<string, Task<string?>>();
                foreach (var provider in enabledProviders)
                {
                    try
                    {
                        tasks[provider.Id] = provider.Id switch
                        {
                            ServiceProviderIds.LrcLib => _onlineLyricsService.GetLyricsAsync(
                                song.Title, song.Artist?.Name, song.Album?.Title, song.Duration, token),
                            ServiceProviderIds.NetEase => _netEaseLyricsService.SearchLyricsAsync(
                                song.Title, song.Artist?.Name, token),
                             _ => LogUnknownProviderAndReturnNull(provider.Id)
                        };
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to initiate lyrics fetch for provider {Provider}", provider.DisplayName);
                    }
                }

                // Evaluate results in priority order (but all tasks already running in parallel)
                string? lrcContent = null;
                var anyProviderSuccess = false;
                foreach (var provider in enabledProviders)
                {
                    if (!tasks.TryGetValue(provider.Id, out var task)) continue;
                    
                    try
                    {
                        var result = await task.ConfigureAwait(false);
                        anyProviderSuccess = true;
                        if (!string.IsNullOrWhiteSpace(result))
                        {
                            lrcContent = result;
                            _logger.LogDebug("Found lyrics for '{Title}' from {Provider}.", song.Title, provider.DisplayName);
                            break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when token is cancelled
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Lyrics provider {Provider} failed for '{Title}'.", provider.DisplayName, song.Title);
                    }
                }

                // Fire-and-forget pattern: Observe remaining tasks to prevent UnobservedTaskException.
                // We use ContinueWith with a static lambda to avoid closure allocations. The logger is
                // passed via state parameter. NotOnRanToCompletion ensures we only handle faults/cancellations.
                foreach (var kvp in tasks)
                {
                    if (kvp.Value.IsCompleted) continue;
                    _ = kvp.Value.ContinueWith(
                        static (t, state) =>
                        {
                            if (t.IsFaulted)
                            {
                                var logger = (ILogger<LrcService>)state!;
                                logger.LogDebug(t.Exception?.InnerException, "Lyrics task faulted (ignored, already resolved)");
                            }
                        },
                        _logger,
                        TaskContinuationOptions.NotOnRanToCompletion);
                }

                if (!string.IsNullOrWhiteSpace(lrcContent))
                {
                    // Cache the lyrics for future use
                    await CacheLyricsAsync(song, lrcContent).ConfigureAwait(false);

                    // Parse the downloaded lyrics string
                    return ParseLrcContent(lrcContent);
                }
            }
            
            // Only mark as checked if providers were attempted AND operation wasn't cancelled.
            // This allows retry if providers are enabled later or if the fetch was cancelled.
            if (enabledProviders.Count > 0 && !token.IsCancellationRequested && anyProviderSuccess)
            {
                await _libraryWriter.UpdateSongLyricsLastCheckedAsync(song.Id).ConfigureAwait(false);
                song.LyricsLastCheckedUtc = DateTime.UtcNow;
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
            var fileContent = await _fileSystemService.ReadAllTextAsync(lrcFilePath).ConfigureAwait(false);
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

            await _fileSystemService.WriteAllTextAsync(cachedLrcPath, lrcContent).ConfigureAwait(false);
            _logger.LogDebug("Cached online lyrics for song {SongId} to {Path}", song.Id, cachedLrcPath);

            // Update the database only if the path has changed
            if (song.LrcFilePath != cachedLrcPath)
            {
                song.LrcFilePath = cachedLrcPath;
                // We don't persist the full text to 'Lyrics' column here to keep the DB light,
                // as we rely on the LRC file. The 'Lyrics' column is mostly for unsynced lyrics.
                await _libraryWriter.UpdateSongLrcPathAsync(song.Id, cachedLrcPath).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cache lyrics for song {SongId}", song.Id);
        }
    }

    private Task<string?> LogUnknownProviderAndReturnNull(string providerId)
    {
        _logger.LogWarning("Unknown lyrics provider ID: {ProviderId}. This provider will be skipped.", providerId);
        return Task.FromResult<string?>(null);
    }

    private ParsedLrc ParseLrcContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return new ParsedLrc(Enumerable.Empty<LyricLine>());

        try
        {
            // Normalize NetEase-style 3-digit milliseconds ([mm:ss.fff]) to 2-digit ([mm:ss.ff]) 
            // for better compatibility with standard LRC parsers.
            content = Regex.Replace(content, @"\[(\d{2}:\d{2}\.\d{2})\d\]", "[$1]");

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

    private void OnFetchOnlineLyricsEnabledChanged(bool isEnabled)
    {
        lock (_ctsLock)
        {
            if (_disposed) return;

            if (!isEnabled)
            {
                _logger.LogInformation("Fetch online lyrics disabled. Cancelling ongoing fetches.");
                if (!_settingsCts.IsCancellationRequested)
                {
                    _settingsCts.Cancel();
                }
            }
            else
            {
                if (_settingsCts.IsCancellationRequested)
                {
                    _staleCts.Add(_settingsCts);
                    _settingsCts = new CancellationTokenSource();
                }
            }
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        
        if (disposing)
        {
            _settingsService.FetchOnlineLyricsEnabledChanged -= OnFetchOnlineLyricsEnabledChanged;
            
            lock (_ctsLock)
            {
                _settingsCts.Cancel();
                _settingsCts.Dispose();
                
                foreach (var cts in _staleCts)
                {
                    cts.Dispose();
                }
                _staleCts.Clear();
                _disposed = true;
            }
        }
        else
        {
            _disposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}