using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Data;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Http.Pipelines;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Mutates the music library: folder management, scanning, metadata enrichment, listen history,
///     and deduplication. Read queries live in <see cref="LibraryReader" /> and playlist edits in
///     <see cref="PlaylistService" />.
///     This service is designed to be a singleton and is internally thread-safe.
/// </summary>
public partial class LibraryService : ILibraryWriter, ILibraryScanner, IDisposable
{
    private readonly ConcurrentDictionary<Guid, Lazy<Task<string?>>> _artistImageProcessingTasks = new();

    private readonly IDbContextFactory<MusicDbContext> _contextFactory;
    private readonly ILibraryReader _libraryReader;
    private readonly IFileSystemService _fileSystem;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILastFmMetadataService _lastFmService;
    private readonly IMusicBrainzService _musicBrainzService;
    private readonly IFanartTvService _fanartTvService;
    private readonly ITheAudioDbService _theAudioDbService;
    private readonly IDeezerService _deezerService;
    private readonly IWikipediaService _wikipediaService;
    private readonly IAlbumCoverService _albumCoverService;
    private readonly ILogger<LibraryService> _logger;
    private readonly SemaphoreSlim _metadataFetchSemaphore = new(1, 1);
    private readonly IMetadataService _metadataService;
    private readonly IPathConfiguration _pathConfig;
    private readonly SemaphoreSlim _scanSemaphore = new(1, 1);
    private readonly ISettingsService _settingsService;
    private readonly IReplayGainService _replayGainService;
    private readonly IImageProcessor _imageProcessor;
    private readonly IProviderPipelineProvider _pipelines;
    private int _disposeState;
    private volatile CancellationTokenSource? _metadataFetchCts;
    private CancellationTokenSource? _activeScanCts;
    private readonly CancellationTokenSource _shutdownCts = new();

    public LibraryService(
        IDbContextFactory<MusicDbContext> contextFactory,
        ILibraryReader libraryReader,
        IFileSystemService fileSystem,
        IMetadataService metadataService,
        ILastFmMetadataService lastFmService,
        IMusicBrainzService musicBrainzService,
        IFanartTvService fanartTvService,
        ITheAudioDbService theAudioDbService,
        IDeezerService deezerService,
        IWikipediaService wikipediaService,
        IAlbumCoverService albumCoverService,
        IHttpClientFactory httpClientFactory,
        IPathConfiguration pathConfig,
        ISettingsService settingsService,
        IReplayGainService replayGainService,
        IImageProcessor imageProcessor,
        IProviderPipelineProvider pipelines,
        ILogger<LibraryService> logger)
    {
        _contextFactory = contextFactory;
        _libraryReader = libraryReader;
        _fileSystem = fileSystem;
        _metadataService = metadataService;
        _lastFmService = lastFmService;
        _musicBrainzService = musicBrainzService;
        _fanartTvService = fanartTvService;
        _theAudioDbService = theAudioDbService;
        _deezerService = deezerService;
        _wikipediaService = wikipediaService;
        _albumCoverService = albumCoverService;
        _httpClientFactory = httpClientFactory;
        _pathConfig = pathConfig;
        _settingsService = settingsService;
        _replayGainService = replayGainService;
        _imageProcessor = imageProcessor;
        _pipelines = pipelines;
        _logger = logger;
        _settingsService.FetchOnlineMetadataEnabledChanged += OnFetchOnlineMetadataEnabledChanged;
    }

    private void CancelActiveScan(string reason)
    {
        var activeScan = Volatile.Read(ref _activeScanCts);

        if (activeScan is null || activeScan.IsCancellationRequested)
            return;

        _logger.LogInformation("Cancelling active library scan before {Reason}.", reason);
        try
        {
            activeScan.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The scan finished while the caller was trying to cancel it.
        }
    }

    private void SetActiveScanCancellationSource(CancellationTokenSource cts) =>
        Volatile.Write(ref _activeScanCts, cts);

    private void ClearActiveScanCancellationSource(CancellationTokenSource cts) =>
        Interlocked.CompareExchange(ref _activeScanCts, null, cts);

    public event EventHandler<ArtistMetadataUpdatedEventArgs>? ArtistMetadataUpdated;

    public event EventHandler<IEnumerable<ArtistMetadataUpdatedEventArgs>>? ArtistMetadataBatchUpdated;

    public event EventHandler<LibraryContentChangedEventArgs>? LibraryContentChanged;

    public async Task ClearListenHistoryAsync()
    {
        _logger.LogInformation("Clearing all listen history.");
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        await context.ListenHistory.ExecuteDeleteAsync().ConfigureAwait(false);
        _logger.LogInformation("Listen history cleared.");
        LibraryContentChanged?.Invoke(this, new(LibraryChangeType.SongsUpdated));
    }

    public async Task ClearAllLibraryDataAsync()
    {
        CancelActiveScan("library data reset");
        await _scanSemaphore.WaitAsync(_shutdownCts.Token).ConfigureAwait(false);
        try
        {
            await ClearAllLibraryDataCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _scanSemaphore.Release();
        }
    }

    private async Task ClearAllLibraryDataCoreAsync()
    {
        _logger.LogInformation("Starting to clear all library data and cache files.");
        CancelMetadataFetch();
        await _metadataFetchSemaphore.WaitAsync(_shutdownCts.Token).ConfigureAwait(false);
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
            await using var transaction = await context.Database.BeginTransactionAsync().ConfigureAwait(false);

            try
            {
                await context.PlaylistSongs.ExecuteDeleteAsync().ConfigureAwait(false);
                await context.ListenHistory.ExecuteDeleteAsync().ConfigureAwait(false);
                await context.Songs.ExecuteDeleteAsync().ConfigureAwait(false);
                await context.Playlists.ExecuteDeleteAsync().ConfigureAwait(false);
                await context.SmartPlaylists.ExecuteDeleteAsync().ConfigureAwait(false);
                await context.Albums.ExecuteDeleteAsync().ConfigureAwait(false);
                await context.Artists.ExecuteDeleteAsync().ConfigureAwait(false);
                await context.Genres.ExecuteDeleteAsync().ConfigureAwait(false);
                await context.Folders.ExecuteDeleteAsync().ConfigureAwait(false);

                await transaction.CommitAsync().ConfigureAwait(false);
                _logger.LogInformation("Successfully deleted all data from the database.");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync().ConfigureAwait(false);
                _logger.LogError(ex, "Database reset failed and was rolled back.");
                throw;
            }

            try
            {
                foreach (var path in new[] { _pathConfig.AlbumArtCachePath, _pathConfig.ArtistImageCachePath,
                             _pathConfig.PlaylistImageCachePath, _pathConfig.LrcCachePath })
                {
                    if (_fileSystem.DirectoryExists(path)) _fileSystem.DeleteDirectory(path, true);
                    _fileSystem.CreateDirectory(path);
                }
                _logger.LogInformation("Successfully cleared and recreated cache directories.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear and recreate cache directories during library reset.");
            }
        }
        finally
        {
            _metadataFetchSemaphore.Release();
        }
    }

    public async Task<Folder?> AddFolderAsync(string path, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        // Canonicalize so that Z:\X and \\server\share\X resolve to the same identity.
        // Folder.Path is NOCASE-collated, but NOCASE only folds ASCII case — it doesn't
        // collapse mapped-drive vs UNC or Unicode equivalence classes.
        var normalizedPath = _fileSystem.NormalizePath(path);

        var existingFolder = await context.Folders.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Path == normalizedPath).ConfigureAwait(false);
        if (existingFolder is not null)
            return existingFolder;

        var folderName = Path.GetFileName(normalizedPath);
        var folder = new Folder
        {
            Path = normalizedPath,
            Name = name ?? (folderName.Length == 0 ? normalizedPath : folderName)
        };
        try
        {
            folder.LastModifiedDate = _fileSystem.GetLastWriteTimeUtc(normalizedPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not get LastWriteTimeUtc for folder {FolderPath}", normalizedPath);
            folder.LastModifiedDate = null;
        }

        context.Folders.Add(folder);
        await context.SaveChangesAsync().ConfigureAwait(false);
        LibraryContentChanged?.Invoke(this, new LibraryContentChangedEventArgs(LibraryChangeType.FolderAdded, folder.Id));
        return folder;
    }

    public async Task<bool> RemoveFolderAsync(Guid folderId)
    {
        CancelActiveScan("folder removal");
        await _scanSemaphore.WaitAsync(_shutdownCts.Token).ConfigureAwait(false);
        try
        {
            return await RemoveFolderCoreAsync(folderId, _shutdownCts.Token).ConfigureAwait(false);
        }
        finally
        {
            _scanSemaphore.Release();
        }
    }

    private async Task<bool> RemoveFolderCoreAsync(Guid folderId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var folder = await context.Folders.FindAsync(new object[] { folderId }, cancellationToken).ConfigureAwait(false);
        if (folder is null)
        {
            _logger.LogWarning("Could not remove folder: Folder with ID {FolderId} not found.", folderId);
            return false;
        }

        List<string> albumArtPathsToDelete;
        List<string> lrcPathsToDelete;
        List<Guid> orphanedArtistIds;

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (folder.ParentFolderId == null)
            {
                var songsInFolder = context.Songs.Where(s => s.FolderId == folderId);

                albumArtPathsToDelete = await songsInFolder
                    .Where(s => s.AlbumArtUriFromTrack != null)
                    .Select(s => s.AlbumArtUriFromTrack!)
                    .Distinct()
                    .ToListAsync(cancellationToken).ConfigureAwait(false);

                lrcPathsToDelete = await songsInFolder
                    .Where(s => s.LrcFilePath != null)
                    .Select(s => s.LrcFilePath!)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);

                await songsInFolder.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                albumArtPathsToDelete = new List<string>();
                lrcPathsToDelete = new List<string>();
            }

            context.Folders.Remove(folder);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            orphanedArtistIds = await CleanUpOrphanedEntitiesAsync(context, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Removed folder '{FolderName}' and associated data.", folder.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Folder removal for ID {FolderId} failed and was rolled back.", folderId);
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        DeleteOrphanedArtistImages(orphanedArtistIds);
        await DeleteUnreferencedCoverArtAsync(context, albumArtPathsToDelete, cancellationToken).ConfigureAwait(false);

        foreach (var lrcPath in lrcPathsToDelete)
            if (IsPathInLrcCache(lrcPath))
                try
                {
                    _fileSystem.DeleteFile(lrcPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete cached LRC file {LrcPath} during folder removal.",
                        lrcPath);
                }

        LibraryContentChanged?.Invoke(this, new LibraryContentChangedEventArgs(LibraryChangeType.FolderRemoved, folderId));

        return true;
    }

    public Task<bool> UpdateSongLrcPathAsync(Guid songId, string? lrcPath)
    {
        return UpdateSongPropertyAsync(songId, s => s.LrcFilePath = lrcPath);
    }

    public Task<bool> UpdateSongLyricsLastCheckedAsync(Guid songId)
    {
        return UpdateSongPropertyAsync(songId, s => s.LyricsLastCheckedUtc = DateTime.UtcNow);
    }

    public async Task<long?> StartListenSessionAsync(Guid songId, PlaybackContext context)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        if (!await dbContext.Songs.AnyAsync(s => s.Id == songId).ConfigureAwait(false)) return null;

        var historyEntry = new ListenHistory
        {
            SongId = songId,
            ListenTimestampUtc = DateTime.UtcNow,
            ContextType = context.Type,
            ContextId = context.ContextId,
            EndReason = PlaybackEndReason.PausedAndAbandoned // Default until finalized
        };

        dbContext.ListenHistory.Add(historyEntry);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        return historyEntry.Id;
    }

    public async Task FinalizeListenSessionAsync(long listenHistoryId, TimeSpan finalDuration, PlaybackEndReason endReason)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        // Serialize the read and counter updates with eligibility changes from playback callbacks.
        await using var transaction = await dbContext.Database.BeginTransactionAsync().ConfigureAwait(false);
        var historyEntry = await dbContext.ListenHistory
            .Include(lh => lh.Song)
            .FirstOrDefaultAsync(lh => lh.Id == listenHistoryId)
            .ConfigureAwait(false);

        if (historyEntry is null) return;

        // Guard against double-finalization races. Once a session is marked Finished,
        // it must not be downgraded to Skipped or PausedAndAbandoned by a late fire-and-forget.
        if (historyEntry.EndReason == PlaybackEndReason.Finished)
        {
            _logger.LogDebug("FinalizeListenSessionAsync: Session {Id} is already finalized as Finished. Skipping.", listenHistoryId);
            return;
        }

        var wasSkipped = historyEntry.EndReason == PlaybackEndReason.Skipped;
        historyEntry.Song.TotalListenTimeTicks += finalDuration.Ticks - historyEntry.ListenDurationTicks;
        historyEntry.ListenDurationTicks = finalDuration.Ticks;
        historyEntry.EndReason = endReason;

        if (!historyEntry.IsEligibleForScrobbling)
        {
            var isSkipped = endReason == PlaybackEndReason.Skipped;
            historyEntry.Song.SkipCount += (isSkipped ? 1 : 0) - (wasSkipped ? 1 : 0);
        }

        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
    }

    public async Task<bool> MarkListenAsEligibleForScrobblingAsync(long listenHistoryId)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        await using var transaction = await dbContext.Database.BeginTransactionAsync().ConfigureAwait(false);
        var historyEntry = await dbContext.ListenHistory
            .Include(lh => lh.Song)
            .FirstOrDefaultAsync(lh => lh.Id == listenHistoryId)
            .ConfigureAwait(false);

        if (historyEntry is null) return false;

        if (!historyEntry.IsEligibleForScrobbling)
        {
            historyEntry.IsEligibleForScrobbling = true;
            historyEntry.Song.PlayCount++;
            historyEntry.Song.LastPlayedDate = DateTime.UtcNow;
            if (historyEntry.EndReason == PlaybackEndReason.Skipped)
                historyEntry.Song.SkipCount--;
            await dbContext.SaveChangesAsync().ConfigureAwait(false);
        }

        await transaction.CommitAsync().ConfigureAwait(false);
        return true;
    }

    public async Task<bool> MarkListenAsScrobbledAsync(long listenHistoryId)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await dbContext.ListenHistory.Where(history => history.Id == listenHistoryId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(history => history.IsScrobbled, true))
            .ConfigureAwait(false) > 0;
    }

    public async Task<bool> MarkListenAsSubmittedToListenBrainzAsync(long listenHistoryId)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await dbContext.ListenHistory.Where(history => history.Id == listenHistoryId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(history => history.IsSubmittedToListenBrainz, true))
            .ConfigureAwait(false) > 0;
    }

    private bool IsPathInLrcCache(string? filePath) => IsExistingCacheFile(filePath, _pathConfig.LrcCachePath);

    private bool IsExistingCacheFile(string? filePath, string cachePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;

        try
        {
            var normalizedFilePath = Path.GetFullPath(filePath);
            var normalizedCachePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cachePath)) + Path.DirectorySeparatorChar;

            return normalizedFilePath.StartsWith(normalizedCachePath, StringComparison.OrdinalIgnoreCase)
                   && _fileSystem.FileExists(filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not validate cache path for {FilePath}", filePath);
            return false;
        }
    }

    private async Task DeleteUnreferencedCoverArtAsync(MusicDbContext context, IEnumerable<string> paths,
        CancellationToken token)
    {
        // Cover art is content-addressed and may be shared across songs and albums.
        foreach (var batch in paths.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(500))
        {
            var references = await context.Songs
                .Where(s => s.AlbumArtUriFromTrack != null && batch.Contains(s.AlbumArtUriFromTrack))
                .Select(s => s.AlbumArtUriFromTrack!)
                .Union(context.Albums
                    .Where(a => a.CoverArtUri != null && batch.Contains(a.CoverArtUri) && a.Songs.Any())
                    .Select(a => a.CoverArtUri!))
                .ToListAsync(token).ConfigureAwait(false);

            foreach (var path in batch.Except(references, StringComparer.OrdinalIgnoreCase))
            {
                if (!IsExistingCacheFile(path, _pathConfig.AlbumArtCachePath)) continue;
                try
                {
                    _fileSystem.DeleteFile(path);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete unreferenced cover art {FilePath}.", path);
                }
            }
        }
    }

    private async Task<bool> UpdateSongPropertyAsync(Guid songId, Action<Song> updateAction)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var song = await context.Songs.FindAsync(songId).ConfigureAwait(false);
        if (song is null) return false;

        updateAction(song);
        try
        {
            await context.SaveChangesAsync().ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Database update failed for song ID {SongId}.", songId);
            return false;
        }
    }

    private async Task<List<Guid>> CleanUpOrphanedEntitiesAsync(MusicDbContext context,
        CancellationToken cancellationToken = default)
    {
        var songDirectories = await context.Songs.Select(s => s.DirectoryPath).Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var occupiedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in songDirectories)
        {
            var path = PathCanonicalizer.NormalizeDirectoryPath(directory);
            while (!string.IsNullOrEmpty(path) && occupiedPaths.Add(path))
                path = Path.GetDirectoryName(path);
        }

        // Delete leaves first, preserving roots and ancestors of occupied directories.
        while (true)
        {
            var leaves = await context.Folders
                .Where(f => f.ParentFolderId != null && !f.SubFolders.Any())
                .Select(f => new { f.Id, f.Path })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var ids = leaves.Where(f => !occupiedPaths.Contains(PathCanonicalizer.NormalizeDirectoryPath(f.Path)))
                .Select(f => f.Id).ToArray();
            if (ids.Length == 0) break;
            await context.Folders.Where(f => ids.Contains(f.Id))
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        await context.Albums.Where(a => !a.Songs.Any()).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.Genres.Where(g => !g.Songs.Any()).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        var orphanedArtistIds = await context.Artists
            .Where(a => !a.SongArtists.Any() && !a.AlbumArtists.Any())
            .Select(a => a.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (orphanedArtistIds.Count > 0)
            await context.Artists.Where(a => orphanedArtistIds.Contains(a.Id))
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        return orphanedArtistIds;
    }

    private void DeleteOrphanedArtistImages(IEnumerable<Guid> artistIds)
    {
        foreach (var artistId in artistIds)
        {
            try
            {
                foreach (var suffix in new[] { ".fetched", ".custom", ".local", "" })
                    ImageStorageHelper.DeleteImage(_fileSystem, _pathConfig.ArtistImageCachePath, artistId.ToString(), suffix);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete images for removed artist {ArtistId}.", artistId);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0) return;
        _settingsService.FetchOnlineMetadataEnabledChanged -= OnFetchOnlineMetadataEnabledChanged;
        _shutdownCts.Cancel();
        // These managed gates have no wait handles. Keep them usable while cancelled runs unwind.
        GC.SuppressFinalize(this);
    }
}
