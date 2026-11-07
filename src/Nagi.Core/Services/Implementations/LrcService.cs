using Microsoft.Extensions.Logging;
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

    public async Task<SongLyrics?> GetLyricsAsync(Song song, CancellationToken cancellationToken = default)
    {
        var forceOnlineRefresh = false;

        if (!string.IsNullOrWhiteSpace(song.LrcFilePath))
        {
            var isLegacyCachePath = LrcCache.IsLegacy(_pathConfig.LrcCachePath, song.LrcFilePath, LrcCache.Identity(song));
            if (!isLegacyCachePath && _fileSystemService.FileExists(song.LrcFilePath))
                return await ReadLyricsFileAsync(song.LrcFilePath).ConfigureAwait(false) ?? LyricsParser.Parse(song.Lyrics);

            forceOnlineRefresh = true;
            _logger.LogInformation(
                isLegacyCachePath
                    ? "Ignoring legacy collision-prone lyrics cache path while refreshing song {SongId}."
                    : "Ignoring missing lyrics path while refreshing song {SongId}.",
                song.Id);
        }

        if (LyricsParser.Parse(song.Lyrics) is { } embeddedLyrics) return embeddedLyrics;

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
                return LyricsParser.Parse(serverLyrics);
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
        SongLyrics? selectedLyrics = null;
        bool isFinal;
        try
        {
            isFinal = await ProviderPriority.TakeInOrderAsync(lookups, rawLyrics =>
            {
                if (LyricsParser.Parse(rawLyrics) is not { } lyrics) return false;
                if (!lyrics.IsSynced)
                {
                    selectedLyrics ??= lyrics;
                    return false;
                }

                selectedLyrics = lyrics;
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
            await CacheLyricsAsync(song, selectedLyrics.RawLyrics).ConfigureAwait(false);
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
        return LrcCache.Contains(_pathConfig.LrcCachePath, song.LrcFilePath);
    }

    private async Task<SongLyrics?> ReadLyricsFileAsync(string path)
    {
        try
        {
            return LyricsParser.Parse(await _fileSystemService.ReadAllTextAsync(path).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read lyrics file {LrcFilePath}", path);
            return null;
        }
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
        var cacheRoot = _pathConfig.LrcCachePath;
        var identity = LrcCache.Identity(song);
        var cachedLrcPath = isOverride
            ? LrcCache.OverridePath(cacheRoot, identity)
            : LrcCache.CachedPath(cacheRoot, identity, song.PrimaryArtistName, song.Album?.Title, song.Title);
        var previousPath = song.LrcFilePath;

        await LyricsStorageHelper.SaveAsync(_fileSystemService, cachedLrcPath, lrcContent).ConfigureAwait(false);
        _logger.LogDebug("Cached lyrics for song {SongId} to {Path}", song.Id, cachedLrcPath);

        if (previousPath == cachedLrcPath) return;
        await _libraryWriter.UpdateSongLrcPathAsync(song.Id, cachedLrcPath).ConfigureAwait(false);
        song.LrcFilePath = cachedLrcPath;

        if (LrcCache.Contains(cacheRoot, previousPath) && _fileSystemService.FileExists(previousPath!))
            _fileSystemService.DeleteFile(previousPath!);
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
