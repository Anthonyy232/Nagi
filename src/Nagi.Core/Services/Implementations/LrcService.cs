using Microsoft.Extensions.Logging;
using ModernLrc;
using ModernLrc.Model;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Models.Lyrics;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Helpers;

namespace Nagi.Core.Services.Implementations;

public class LrcService : ILrcService, IDisposable
{
    private readonly IFileSystemService _fileSystemService;
    private readonly ILogger<LrcService> _logger;
    private readonly Dictionary<string, ILyricsProvider> _providers;
    private readonly ISettingsService _settingsService;
    private readonly IPathConfiguration _pathConfig;
    private readonly ILibraryWriter _libraryWriter;
    private readonly IMusicServerService _musicServers;

    private readonly OnlineLookupCancellation _onlineLookups = new();

    public LrcService(
        IFileSystemService fileSystemService,
        IEnumerable<ILyricsProvider> providers,
        ISettingsService settingsService,
        IPathConfiguration pathConfig,
        ILibraryWriter libraryWriter,
        IMusicServerService musicServers,
        ILogger<LrcService> logger)
    {
        _fileSystemService = fileSystemService;
        _providers = providers.ToDictionary(provider => provider.Id);
        _settingsService = settingsService;
        _pathConfig = pathConfig;
        _libraryWriter = libraryWriter;
        _musicServers = musicServers;
        _logger = logger;
        _settingsService.FetchOnlineLyricsEnabledChanged += OnFetchOnlineLyricsEnabledChanged;
    }

    public async Task<ParsedLrc?> GetLyricsAsync(Song song, CancellationToken cancellationToken = default)
    {
        var forceOnlineRefresh = false;

        // Cache files created before identity hashes are not safe to reuse: two songs may already
        // point at the same metadata-derived file.
        if (!string.IsNullOrWhiteSpace(song.LrcFilePath))
        {
            var isLegacyCachePath = IsLegacyCachePath(song);
            if (!isLegacyCachePath && _fileSystemService.FileExists(song.LrcFilePath))
                return await GetLyricsAsync(song.LrcFilePath).ConfigureAwait(false);

            forceOnlineRefresh = true;
            _logger.LogInformation(
                isLegacyCachePath
                    ? "Ignoring legacy collision-prone lyrics cache path while refreshing song {SongId}."
                    : "Ignoring missing lyrics path while refreshing song {SongId}.",
                song.Id);
        }

        // Like lyrics embedded in a file, a server song's own lyrics come first and don't need online lyrics turned on.
        if (song.IsRemote && (forceOnlineRefresh || song.LyricsLastCheckedUtc == null))
        {
            string? serverLyrics;
            try
            {
                serverLyrics = await _musicServers.GetLyricsAsync(song, cancellationToken).ConfigureAwait(false);
            }
            catch (MusicServerException ex)
            {
                // Not marked as checked, so the server is asked again once it's back.
                _logger.LogDebug(ex, "Couldn't ask the server for the lyrics of song {SongId}.", song.Id);
                return null;
            }

            if (serverLyrics is not null)
            {
                await CacheLyricsAsync(song, serverLyrics).ConfigureAwait(false);
                return ParseLyrics(serverLyrics);
            }
        }

        // Look online only if never checked before, unless the local file is missing or unsafe to reuse.
        var turnedOff = _onlineLookups.Token;
        if ((!forceOnlineRefresh && song.LyricsLastCheckedUtc != null)
            || !await _settingsService.GetFetchOnlineLyricsEnabledAsync().ConfigureAwait(false))
            return null;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, turnedOff);
        var token = linkedCts.Token;

        if (token.IsCancellationRequested)
            return null;

        var enabledProviders = await _settingsService.GetEnabledServiceProvidersAsync(ServiceCategory.Lyrics).ConfigureAwait(false);
        var lookups = enabledProviders
            .Where(provider => _providers.ContainsKey(provider.Id))
            .Select(provider => _providers[provider.Id].GetLyricsAsync(song, token))
            .ToList();
        if (lookups.Count == 0) return null;

        // Provider order breaks ties; plain text must not hide another provider's timed lyrics.
        ParsedLrc? selectedLyrics = null;
        bool isFinal;
        try
        {
            isFinal = await ProviderPriority.TakeInOrderAsync(lookups, lyrics =>
            {
                if (string.IsNullOrWhiteSpace(lyrics)) return false;
                var parsed = ParseLyrics(lyrics);
                if (parsed.IsEmpty)
                {
                    selectedLyrics ??= parsed;
                    return false;
                }

                selectedLyrics = parsed;
                return true;
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return null;
        }

        if (token.IsCancellationRequested) return null;

        if (selectedLyrics is not null)
        {
            await CacheLyricsAsync(song, selectedLyrics.RawLyrics!).ConfigureAwait(false);
            return selectedLyrics;
        }

        // A negative result is conclusive only when every enabled provider completed.
        // Failed requests remain retryable on the next playback.
        if (isFinal)
        {
            // All providers completed successfully but returned no lyrics. Remove the invalid local
            // reference now; successful lyric results already replace it in CacheLyricsAsync.
            if (forceOnlineRefresh && !string.IsNullOrWhiteSpace(song.LrcFilePath))
            {
                song.LrcFilePath = null;
                await _libraryWriter.UpdateSongLrcPathAsync(song.Id, null).ConfigureAwait(false);
            }

            await _libraryWriter.UpdateSongLyricsLastCheckedAsync(song.Id).ConfigureAwait(false);
            song.LyricsLastCheckedUtc = DateTime.UtcNow;
        }

        return null;
    }

    public async Task<ParsedLrc?> GetLyricsAsync(string lrcFilePath)
    {
        if (string.IsNullOrWhiteSpace(lrcFilePath) || !_fileSystemService.FileExists(lrcFilePath)) return null;

        try
        {
            var fileContent = await _fileSystemService.ReadAllTextAsync(lrcFilePath).ConfigureAwait(false);
            return ParseLyrics(fileContent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse LRC file {LrcFilePath}", lrcFilePath);
            return null;
        }
    }

    public Task SaveLyricsAsync(Song song, string lrcContent)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentException.ThrowIfNullOrWhiteSpace(lrcContent);
        return WriteLyricsCacheAsync(song, lrcContent, true);
    }

    public async Task<bool> RemoveCachedLyricsAsync(Song song)
    {
        ArgumentNullException.ThrowIfNull(song);
        if (!HasCachedLyrics(song)) return false;

        var cachedPath = song.LrcFilePath!;
        // A server song has no folder of its own to hold an .lrc file.
        var sidecarPath = song.IsRemote ? null : LyricsStorageHelper.FindSidecar(_fileSystemService, song.FilePath, _logger);
        if (_fileSystemService.FileExists(cachedPath)) _fileSystemService.DeleteFile(cachedPath);

        song.LrcFilePath = sidecarPath;
        song.LyricsLastCheckedUtc = DateTime.UtcNow;
        await _libraryWriter.UpdateSongLrcPathAsync(song.Id, sidecarPath).ConfigureAwait(false);
        await _libraryWriter.UpdateSongLyricsLastCheckedAsync(song.Id).ConfigureAwait(false);
        return true;
    }

    public bool HasCachedLyrics(Song song)
    {
        ArgumentNullException.ThrowIfNull(song);
        return IsCachePath(song.LrcFilePath);
    }

    private async Task CacheLyricsAsync(Song song, string lrcContent)
    {
        try
        {
            await WriteLyricsCacheAsync(song, lrcContent, false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cache lyrics for song {SongId}", song.Id);
        }
    }

    private async Task WriteLyricsCacheAsync(Song song, string lrcContent, bool isOverride)
    {
        var cacheIdentity = string.IsNullOrWhiteSpace(song.FilePath) ? song.Id.ToString("N") : song.FilePath;
        var cacheFileName = isOverride
            ? FileNameHelper.GenerateLrcOverrideFileName(cacheIdentity)
            : FileNameHelper.GenerateLrcCacheFileName(
                cacheIdentity, song.PrimaryArtistName, song.Album?.Title, song.Title);
        var cachedLrcPath = Path.Combine(_pathConfig.LrcCachePath, cacheFileName);
        var previousPath = song.LrcFilePath;

        await LyricsStorageHelper.SaveAsync(_fileSystemService, cachedLrcPath, lrcContent).ConfigureAwait(false);
        _logger.LogDebug("Cached lyrics for song {SongId} to {Path}", song.Id, cachedLrcPath);

        if (previousPath == cachedLrcPath) return;
        await _libraryWriter.UpdateSongLrcPathAsync(song.Id, cachedLrcPath).ConfigureAwait(false);
        song.LrcFilePath = cachedLrcPath;

        if (previousPath is not null && IsCachePath(previousPath) && _fileSystemService.FileExists(previousPath))
            _fileSystemService.DeleteFile(previousPath);
    }

    private bool IsLegacyCachePath(Song song)
    {
        if (!IsCachePath(song.LrcFilePath)) return false;

        var cacheIdentity = string.IsNullOrWhiteSpace(song.FilePath) ? song.Id.ToString("N") : song.FilePath;
        var candidate = Path.GetFullPath(song.LrcFilePath!);
        return !FileNameHelper.MatchesLrcCacheIdentity(candidate, cacheIdentity);
    }

    private bool IsCachePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(_pathConfig.LrcCachePath)) return false;

        var cacheRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_pathConfig.LrcCachePath)) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(path);
        return candidate.StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase);
    }

    public ParsedLrc ParseLyrics(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return new ParsedLrc(Enumerable.Empty<LyricLine>());

        try
        {
            var document = LrcParser.Parse(content).Document;
            // LRC has no translation syntax, so files commonly repeat the original line's timestamp for it.
            var lyricLines = document.Lines
                .GroupBy(line => document.GetEffectiveTime(line.Timestamp))
                .Select(group => CreateLine(group.Key, group.Select(line => line.GetText())));
            return new ParsedLrc(lyricLines, content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing LRC content");
            return new ParsedLrc(Enumerable.Empty<LyricLine>(), content);
        }
    }

    private static LyricLine CreateLine(TimeSpan startTime, IEnumerable<string> texts)
    {
        var nonEmpty = texts.Where(text => !string.IsNullOrWhiteSpace(text)).ToList();
        return new LyricLine(startTime, nonEmpty.FirstOrDefault() ?? string.Empty)
        {
            TranslatedText = nonEmpty.Count > 1 ? string.Join('\n', nonEmpty.Skip(1)) : null
        };
    }

    public LyricLine? GetCurrentLine(ParsedLrc parsedLrc, TimeSpan currentTime, ref int searchStartIndex)
    {
        if (parsedLrc.IsEmpty) return null;

        var lines = parsedLrc.Lines;
        var lineCount = lines.Count;

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

    private static int FindBestMatchIndex(IReadOnlyList<LyricLine> lines, TimeSpan currentTime)
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
        if (isEnabled) return;
        _logger.LogInformation("Fetch online lyrics disabled. Cancelling ongoing fetches.");
        _onlineLookups.CancelRunning();
    }

    public void Dispose()
    {
        _settingsService.FetchOnlineLyricsEnabledChanged -= OnFetchOnlineLyricsEnabledChanged;
        _onlineLookups.CancelRunning();
    }
}
