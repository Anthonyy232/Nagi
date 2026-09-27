using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Constants;
using Nagi.Core.Data;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Helpers;
using Nagi.Core.Http.Pipelines;
using Polly.CircuitBreaker;
using System.IO;
using System.Globalization;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Manages all aspects of the music library, including file scanning, metadata, and database operations.
///     This service is designed to be a singleton and is internally thread-safe.
/// </summary>
public class LibraryService : ILibraryService, IDisposable
{

    private readonly ConcurrentDictionary<Guid, Lazy<Task<string?>>> _artistImageProcessingTasks = new();

    private readonly IDbContextFactory<MusicDbContext> _contextFactory;
    private readonly IFileSystemService _fileSystem;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILastFmMetadataService _lastFmService;
    private readonly IMusicBrainzService _musicBrainzService;
    private readonly IFanartTvService _fanartTvService;
    private readonly ITheAudioDbService _theAudioDbService;
    private readonly ILogger<LibraryService> _logger;
    private readonly SemaphoreSlim _metadataFetchSemaphore = new(1, 1);
    private readonly IMetadataService _metadataService;
    private readonly IPathConfiguration _pathConfig;
    private readonly SemaphoreSlim _scanSemaphore = new(1, 1);
    private readonly SemaphoreSlim _replayGainSemaphore = new(1, 1);
    private readonly ISettingsService _settingsService;
    private readonly IReplayGainService _replayGainService;
    private readonly IImageProcessor _imageProcessor;
    private readonly IProviderPipelineProvider _pipelines;
    private int _disposeState;
    private volatile bool _ignoreArticlesOnSort = true;
    private volatile CancellationTokenSource? _metadataFetchCts;
    private CancellationTokenSource? _activeScanCts;
    private readonly CancellationTokenSource _shutdownCts = new();

    public LibraryService(
        IDbContextFactory<MusicDbContext> contextFactory,
        IFileSystemService fileSystem,
        IMetadataService metadataService,
        ILastFmMetadataService lastFmService,
        IMusicBrainzService musicBrainzService,
        IFanartTvService fanartTvService,
        ITheAudioDbService theAudioDbService,
        IHttpClientFactory httpClientFactory,
        IPathConfiguration pathConfig,
        ISettingsService settingsService,
        IReplayGainService replayGainService,
        IImageProcessor imageProcessor,
        IProviderPipelineProvider pipelines,
        ILogger<LibraryService> logger)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _lastFmService = lastFmService ?? throw new ArgumentNullException(nameof(lastFmService));
        _musicBrainzService = musicBrainzService ?? throw new ArgumentNullException(nameof(musicBrainzService));
        _fanartTvService = fanartTvService ?? throw new ArgumentNullException(nameof(fanartTvService));
        _theAudioDbService = theAudioDbService ?? throw new ArgumentNullException(nameof(theAudioDbService));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _pathConfig = pathConfig ?? throw new ArgumentNullException(nameof(pathConfig));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _replayGainService = replayGainService ?? throw new ArgumentNullException(nameof(replayGainService));
        _imageProcessor = imageProcessor ?? throw new ArgumentNullException(nameof(imageProcessor));
        _pipelines = pipelines ?? throw new ArgumentNullException(nameof(pipelines));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _settingsService.FetchOnlineMetadataEnabledChanged += OnFetchOnlineMetadataEnabledChanged;
        _settingsService.IgnoreLeadingArticlesOnSortEnabledChanged += OnIgnoreLeadingArticlesOnSortChanged;
        _ = InitializeIgnoreArticlesFlagAsync();
    }

    private async Task InitializeIgnoreArticlesFlagAsync()
    {
        try { _ignoreArticlesOnSort = await _settingsService.GetIgnoreLeadingArticlesOnSortEnabledAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to load IgnoreLeadingArticlesOnSort setting; using default ({Default}).", _ignoreArticlesOnSort); }
    }

    private void OnIgnoreLeadingArticlesOnSortChanged(bool value) => _ignoreArticlesOnSort = value;

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

    /// <summary>
    ///     Occurs when an artist's metadata (e.g., biography, image) has been successfully updated from a remote source.
    /// </summary>
    public event EventHandler<ArtistMetadataUpdatedEventArgs>? ArtistMetadataUpdated;

    /// <inheritdoc />
    public event EventHandler<IEnumerable<ArtistMetadataUpdatedEventArgs>>? ArtistMetadataBatchUpdated;

    /// <inheritdoc />
    public event EventHandler<PlaylistUpdatedEventArgs>? PlaylistUpdated;

    /// <inheritdoc />
    public event EventHandler? PlaylistsChanged;

    /// <inheritdoc />
    public event EventHandler<LibraryContentChangedEventArgs>? LibraryContentChanged;

    #region Data Reset

    /// <inheritdoc />
    public async Task ClearListenHistoryAsync()
    {
        _logger.LogInformation("Clearing all listen history.");
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        await context.ListenHistory.ExecuteDeleteAsync().ConfigureAwait(false);
        _logger.LogInformation("Listen history cleared.");
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

    #endregion

    #region Folder Management

    /// <inheritdoc />
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

        var folderName = _fileSystem.GetFileName(normalizedPath);
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

    /// <inheritdoc />
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

    /// <inheritdoc />
    public async Task<Folder?> GetFolderByIdAsync(Guid folderId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.Id == folderId).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Folder?> GetFolderByPathAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var normalizedPath = _fileSystem.NormalizePath(path);
        if (string.IsNullOrEmpty(normalizedPath)) return null;

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.Path == normalizedPath).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> HasAnyFolderAsync(CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Folders.AnyAsync(token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<FolderSummary>> GetRootFoldersWithCountsAsync(CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        return await context.Folders.AsNoTracking()
            .Where(f => f.ParentFolderId == null)
            .Select(f => new FolderSummary(f, f.Songs.Count))
            .ToListAsync(token).ConfigureAwait(false);
    }

    private async Task<IEnumerable<Folder>> GetRootFoldersAsync(CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        return await context.Folders.AsNoTracking()
            .Where(f => f.ParentFolderId == null)
            .OrderBy(f => f.Name)
            .ThenBy(f => f.Path)
            .ThenBy(f => f.Id)
            .ToListAsync(token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IEnumerable<Folder>> GetSubFoldersPagedAsync(Guid parentFolderId, int skip, int take, CancellationToken token = default) =>
        SearchSubFoldersPagedAsync(parentFolderId, string.Empty, skip, take, token);

    /// <inheritdoc />
    public async Task<int> GetSubFolderCountBySearchAsync(Guid parentFolderId, string searchTerm, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
            return await GetSubFolderCountAsync(parentFolderId, token).ConfigureAwait(false);

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var term = LikePatternHelper.CreateContainsPattern(NormalizeString(searchTerm) ?? string.Empty);
        return await context.Folders.AsNoTracking()
            .CountAsync(f => f.ParentFolderId == parentFolderId &&
                             EF.Functions.Like(f.Name, term, LikePatternHelper.EscapeCharacter), token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Folder>> SearchSubFoldersPagedAsync(Guid parentFolderId, string searchTerm, int skip, int take, CancellationToken token = default)
    {
        if (take <= 0) return Enumerable.Empty<Folder>();
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.Folders.AsNoTracking()
            .Where(f => f.ParentFolderId == parentFolderId);

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = LikePatternHelper.CreateContainsPattern(NormalizeString(searchTerm) ?? string.Empty);
            query = query.Where(f => EF.Functions.Like(f.Name, term, LikePatternHelper.EscapeCharacter));
        }

        return await query
            .OrderBy(f => f.Name)
            .ThenBy(f => f.Path)
            .ThenBy(f => f.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> GetSubFolderCountAsync(Guid parentFolderId, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Folders.CountAsync(f => f.ParentFolderId == parentFolderId, token).ConfigureAwait(false);
    }

    #endregion

    #region Library Scanning

    /// <inheritdoc />
    public async Task ScanFolderForMusicAsync(string folderPath, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var folder = await GetFolderByPathAsync(folderPath).ConfigureAwait(false) ?? await AddFolderAsync(folderPath).ConfigureAwait(false);
        if (folder is null)
        {
            _logger.LogWarning("Failed to add or find folder for path {FolderPath}, aborting scan.", folderPath);
            progress?.Report(new ScanProgress { StatusText = Resources.Strings.Error_FailedToAddFolder, Percentage = 100 });
            return;
        }

        await RescanFolderForMusicAsync(folder.Id, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<bool> RescanFolderForMusicAsync(Guid folderId, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return RescanFolderForMusicAsync(folderId, false, progress, cancellationToken);
    }

    /// <summary>
    ///     Rescans a specific folder for music files with optional force full scan.
    /// </summary>
    /// <param name="folderId">The unique identifier of the folder to rescan.</param>
    /// <param name="forceFullScan">If true, re-reads metadata for all files regardless of modification time.</param>
    /// <param name="progress">Optional progress reporter for scan status updates.</param>
    /// <param name="cancellationToken">Optional cancellation token to cancel the scan.</param>
    /// <returns>True if changes were made to the library; otherwise, false.</returns>
    public Task<bool> RescanFolderForMusicAsync(Guid folderId, bool forceFullScan, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        CancelActiveScan("manual folder rescan");
        return RescanFolderForMusicAsync(folderId, forceFullScan, allowFolderRemovalOnMissing: true, progress, cancellationToken);
    }

    private async Task<bool> RescanFolderForMusicAsync(Guid folderId, bool forceFullScan, bool allowFolderRemovalOnMissing,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool registerAsActiveScan = true,
        bool throwOnFailure = false)
    {
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        var operationToken = operationCts.Token;

        if (registerAsActiveScan)
            SetActiveScanCancellationSource(operationCts);

        try
        {
            // Wait to acquire the semaphore. If the operation is cancelled while waiting, it will throw.
            await _scanSemaphore.WaitAsync(operationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (registerAsActiveScan)
                ClearActiveScanCancellationSource(operationCts);
            _logger.LogDebug("Scan for folder ID {FolderId} was cancelled before acquiring semaphore.", folderId);
            progress?.Report(new ScanProgress { StatusText = Resources.Strings.Status_ScanCancelled, Percentage = 100 });
            return false;
        }

        try
        {
            var scanResult = await Task.Run(async () =>
            {
                var folder = await GetFolderByIdAsync(folderId).ConfigureAwait(false);
                if (folder is null)
                {
                    _logger.LogWarning("Cannot rescan folder: Folder with ID {FolderId} not found.", folderId);
                    progress?.Report(new ScanProgress { StatusText = string.Format(Resources.Strings.Format_NotFound, Resources.Strings.Label_Folder), Percentage = 100 });
                    if (throwOnFailure)
                        throw new InvalidOperationException($"Folder {folderId} was not found.");
                    return false;
                }

                var isNetwork = _fileSystem.IsNetworkPath(folder.Path);
                _logger.LogInformation(
                    "Scanning folder '{FolderName}' (path={FolderPath}, network={IsNetwork}, forceFull={ForceFull}).",
                    folder.Name, folder.Path, isNetwork, forceFullScan);

                try
                {
                    operationToken.ThrowIfCancellationRequested();

                    if (!_fileSystem.DirectoryExists(folder.Path))
                    {
                        if (!allowFolderRemovalOnMissing)
                        {
                            // Auto-refresh/batch path: folder being unreachable (e.g., NAS not mounted
                            // at app launch, removable drive not inserted) must NOT permanently remove
                            // the user's folder from the library. Just warn and skip.
                            _logger.LogWarning(
                                "Folder path '{FolderPath}' not reachable during auto-refresh. Skipping folder {FolderId}; no changes will be made.",
                                folder.Path, folder.Id);
                            progress?.Report(new ScanProgress
                            { StatusText = Resources.Strings.Status_ScanFailed, Percentage = 100 });
                            if (throwOnFailure)
                                throw new DirectoryNotFoundException($"Library folder was not found: {folder.Path}");
                            return false;
                        }

                        _logger.LogWarning(
                            "Folder path '{FolderPath}' no longer exists. Removing folder {FolderId} from library.",
                            folder.Path, folder.Id);
                        progress?.Report(new ScanProgress
                        { StatusText = Resources.Strings.Status_ScanFailed, Percentage = 100 });
                        return await RemoveFolderCoreAsync(folderId, operationToken).ConfigureAwait(false);
                    }

                    progress?.Report(new ScanProgress
                    { StatusText = Resources.Strings.Status_PreparingScanCaches, IsIndeterminate = true });
                    var (filesToAdd, filesToUpdate, filesRemovedFromDisk) =
                        await AnalyzeFolderChangesAsync(folderId, folder.Path, forceFullScan, operationToken).ConfigureAwait(false);

                    if (filesToAdd.Any() || filesToUpdate.Any() || filesRemovedFromDisk.Any())
                        _logger.LogInformation("Changes detected: +{New} ~{Updated} -{Removed}",
                            filesToAdd.Count, filesToUpdate.Count, filesRemovedFromDisk.Count);

                    operationToken.ThrowIfCancellationRequested();

                    var allFilePathsToDelete = filesRemovedFromDisk.Distinct().ToList();

                    // Safety guard: if the scan proposes to delete the majority of existing songs
                    // while barely adding any, something upstream failed (e.g., a transient NAS
                    // enumeration failure). Skip the delete pass to avoid data loss; the next
                    // successful scan will reconcile correctly.
                    if (allFilePathsToDelete.Count >= 50 || allFilePathsToDelete.Count * 2 > filesToAdd.Count + filesToUpdate.Count + 50)
                    {
                        await using var guardContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
                        var existingCount = await guardContext.Songs.AsNoTracking()
                            .CountAsync(s => s.FolderId == folderId, operationToken).ConfigureAwait(false);
                        if (existingCount > 0 &&
                            allFilePathsToDelete.Count > Math.Max(50, existingCount / 2) &&
                            filesToAdd.Count < allFilePathsToDelete.Count / 10)
                        {
                            _logger.LogError(
                                "Scan safety guard tripped for folder {FolderId}: proposed to delete {DeleteCount}/{ExistingCount} songs while only adding {AddCount}. Skipping delete pass; likely a transient enumeration failure (NAS blip / unmounted drive).",
                                folderId, allFilePathsToDelete.Count, existingCount, filesToAdd.Count);
                            allFilePathsToDelete = new List<string>();
                            filesRemovedFromDisk = new List<string>();
                        }
                    }

                    if (allFilePathsToDelete.Any())
                    {
                        progress?.Report(new ScanProgress
                        { StatusText = Resources.Strings.Status_CleaningUpLibrary, IsIndeterminate = true });
                        await using var deleteContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

                        var songsToDeleteQuery = deleteContext.Songs
                            .Where(s => s.FolderId == folderId && allFilePathsToDelete.Contains(s.FilePath));

                        var lrcPathsToDelete = await songsToDeleteQuery
                            .Where(s => s.LrcFilePath != null)
                            .Select(s => s.LrcFilePath!)
                            .ToListAsync(operationToken).ConfigureAwait(false);

                        var albumArtPathsToDelete = await songsToDeleteQuery
                            .Where(s => s.AlbumArtUriFromTrack != null)
                            .Select(s => s.AlbumArtUriFromTrack!)
                            .Distinct()
                            .ToListAsync(operationToken).ConfigureAwait(false);

                        await songsToDeleteQuery.ExecuteDeleteAsync(operationToken).ConfigureAwait(false);

                        foreach (var lrcPath in lrcPathsToDelete)
                            if (IsPathInLrcCache(lrcPath))
                                try
                                {
                                    _fileSystem.DeleteFile(lrcPath);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex,
                                        "Failed to delete cached LRC file {LrcPath} during rescan.", lrcPath);
                                }

                        await DeleteUnreferencedCoverArtAsync(deleteContext, albumArtPathsToDelete, operationToken).ConfigureAwait(false);
                    }

                    var filesToProcess = filesToAdd.Concat(filesToUpdate).ToList();

                    if (!filesToProcess.Any())
                    {
                        if (allFilePathsToDelete.Any())
                        {
                            progress?.Report(new ScanProgress
                            { StatusText = Resources.Strings.Status_Finalizing, IsIndeterminate = true });
                            await using var cleanupContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
                            DeleteOrphanedArtistImages(await CleanUpOrphanedEntitiesAsync(cleanupContext, operationToken).ConfigureAwait(false));
                        }

                        // Run all independent post-scan checks in parallel (each manages its own DbContext).
                        // No new files were added, so no new directories exist — skip EnsureSubFolders.
                        var lrcTask = UpdateMissingLrcPathsAsync(folderId, operationToken);
                        var coverArtTask = UpdateMissingCoverArtAsync(folderId, folder.Path, operationToken);
                        var artistImageTask = UpdateMissingArtistImagesFromFoldersAsync(folderId, operationToken);

                        await Task.WhenAll(lrcTask, coverArtTask, artistImageTask).ConfigureAwait(false);

                        var lrcUpdates = lrcTask.Result;
                        var coverArtUpdates = coverArtTask.Result;
                        var artistImageUpdates = artistImageTask.Result;

                        var hasChanges = allFilePathsToDelete.Any() || lrcUpdates > 0 || coverArtUpdates > 0 || artistImageUpdates > 0;

                        var updates = new List<string>();
                        if (lrcUpdates > 0)
                        {
                            var songLabel = lrcUpdates == 1 ? Resources.Strings.Label_Song : Resources.Strings.Label_Songs;
                            updates.Add($"{lrcUpdates} {songLabel.ToLower()} {Resources.Strings.Label_WithLyrics}");
                        }

                        if (coverArtUpdates > 0)
                        {
                            var songLabel = coverArtUpdates == 1 ? Resources.Strings.Label_Song : Resources.Strings.Label_Songs;
                            updates.Add($"{coverArtUpdates} {songLabel.ToLower()} {Resources.Strings.Label_WithCoverArt}");
                        }

                        if (artistImageUpdates > 0)
                        {
                            var artistLabel = artistImageUpdates == 1 ? Resources.Strings.Label_Artist : Resources.Strings.Label_Artists;
                            updates.Add($"{artistImageUpdates} {artistLabel.ToLower()} {Resources.Strings.Label_WithArtistImage}");
                        }

                        var andJoiner = $" {Resources.Strings.Label_And} ";
                        var statusMessage = updates.Any()
                            ? string.Format(Resources.Strings.Format_ScanCompleteResult, string.Join(andJoiner, updates), "")
                            : Resources.Strings.Status_ScanCompleteUpToDate;

                        progress?.Report(new ScanProgress
                        { StatusText = statusMessage, Percentage = 100 });
                        if (hasChanges)
                        {
                            LibraryContentChanged?.Invoke(this, new LibraryContentChangedEventArgs(LibraryChangeType.FolderRescanned, folderId));
                        }
                        return hasChanges;
                    }

                    // Use streaming extraction for better memory efficiency
                    var filePathsBeingUpdated = filesToUpdate.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var (newSongsFound, discoveredDirs) = await ExtractAndSaveMetadataStreamingAsync(
                        folderId, filesToProcess, folder.Path, filePathsBeingUpdated, progress, operationToken,
                        skipMediaAssetsForUpdates: forceFullScan).ConfigureAwait(false);

                    operationToken.ThrowIfCancellationRequested();

                    // For pure-add scans, LRC and cover art were already checked during metadata extraction
                    // (AtlMetadataService.GetLrcPathAsync / ProcessCoverArtFromDirectoryAsync) — skip redundant
                    // re-checks. Only run them for rescans where files may have changed since extraction.
                    bool isPureAddScan = filesToUpdate.Count == 0 && filesRemovedFromDisk.Count == 0;

                    // Run all independent post-scan checks in parallel (each manages its own DbContext).
                    // Pass discoveredDirs directly to skip the internal SELECT DISTINCT DirectoryPath query.
                    var postScanTasks = new List<Task>
                    {
                        UpdateMissingArtistImagesFromFoldersAsync(folderId, operationToken),
                        EnsureSubFoldersExistAsync(folderId, folder.Path, discoveredDirs, operationToken)
                    };
                    if (!isPureAddScan)
                    {
                        postScanTasks.Add(UpdateMissingLrcPathsAsync(folderId, operationToken));
                        postScanTasks.Add(UpdateMissingCoverArtAsync(folderId, folder.Path, operationToken));
                    }
                    await Task.WhenAll(postScanTasks).ConfigureAwait(false);

                    progress?.Report(new ScanProgress { StatusText = Resources.Strings.Status_Finalizing, IsIndeterminate = true });

                    // Only clean up orphaned entities if updates or removals may have created them.
                    // Pure adds (InitialScan) cannot produce orphans, so skip the expensive cleanup queries.
                    if (filesRemovedFromDisk.Count > 0 || filesToUpdate.Count > 0)
                    {
                        await using (var finalContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false))
                        {
                            DeleteOrphanedArtistImages(await CleanUpOrphanedEntitiesAsync(finalContext, operationToken).ConfigureAwait(false));
                        }
                    }

                    var labelSong = newSongsFound == 1 ? Resources.Strings.Label_Song : Resources.Strings.Label_Songs;
                    var summary = newSongsFound > 0
                        ? string.Format(Resources.Strings.Format_ScanCompleteResult, newSongsFound.ToString("N0"), labelSong.ToLower())
                        : Resources.Strings.Status_ScanCompleteUpToDate;
                    progress?.Report(new ScanProgress { StatusText = summary, Percentage = 100 });
                    LibraryContentChanged?.Invoke(this, new LibraryContentChangedEventArgs(LibraryChangeType.FolderRescanned, folderId));
                    return true;
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("Scan for folder ID {FolderId} was cancelled.", folderId);
                    progress?.Report(new ScanProgress { StatusText = Resources.Strings.Status_ScanCancelled, Percentage = 100 });
                    return false;
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex, "FATAL: Rescan for folder ID {FolderId} failed.", folderId);
                    progress?.Report(new ScanProgress
                    { StatusText = Resources.Strings.Status_ScanFailed, Percentage = 100 });
                    if (throwOnFailure)
                        throw;
                    return false;
                }
            }, operationToken);

            // Batch refreshes run ReplayGain once after their final folder.
            if (scanResult && registerAsActiveScan && await _settingsService.GetVolumeNormalizationEnabledAsync().ConfigureAwait(false))
            {
                await RunReplayGainAnalysisAsync(progress, operationToken).ConfigureAwait(false);
            }

            return scanResult;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Scan for folder ID {FolderId} was cancelled during execution.", folderId);
            progress?.Report(new ScanProgress { StatusText = Resources.Strings.Status_ScanCancelled, Percentage = 100 });
            return false;
        }
        finally
        {
            if (registerAsActiveScan)
                ClearActiveScanCancellationSource(operationCts);
            _scanSemaphore.Release();
        }
    }

    private static Task<Guid?> GetRandomEntityIdAsync<T>(IQueryable<T> query) where T : class =>
        query.OrderBy(_ => EF.Functions.Random())
            .Select(e => (Guid?)EF.Property<Guid>(e, "Id"))
            .FirstOrDefaultAsync();

    private async Task RunReplayGainAnalysisAsync(IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        if (!await _replayGainSemaphore.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogDebug("ReplayGain analysis is already running. Skipping trigger.");
            return;
        }

        _logger.LogInformation("Volume normalization is enabled. Starting ReplayGain analysis.");

        try
        {
            await _replayGainService.ScanLibraryAsync(progress, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("ReplayGain analysis completed.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("ReplayGain analysis was cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReplayGain analysis failed.");
            progress?.Report(new ScanProgress { StatusText = Resources.Strings.Status_NormalizationFailed, Percentage = 100 });
        }
        finally
        {
            _replayGainSemaphore.Release();
        }
    }

    /// <inheritdoc />
    public Task<bool> RefreshAllFoldersAsync(IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return RefreshAllFoldersAsync(false, progress, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> ForceRescanMetadataAsync(IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return RefreshAllFoldersCoreAsync(true, true, progress, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> RefreshAllFoldersAsync(bool forceFullScan, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return RefreshAllFoldersCoreAsync(forceFullScan, false, progress, cancellationToken);
    }

    private async Task<bool> RefreshAllFoldersCoreAsync(bool forceFullScan, bool throwOnFailure,
        IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        CancelActiveScan(forceFullScan ? "forced library refresh" : "library refresh");
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        var operationToken = operationCts.Token;

        try
        {
            SetActiveScanCancellationSource(operationCts);

            var folders = (await GetRootFoldersAsync(operationToken).ConfigureAwait(false)).ToList();
            var totalFolders = folders.Count;

            if (totalFolders == 0)
            {
                _logger.LogDebug("No folders found in the library to refresh.");
                progress?.Report(
                    new ScanProgress { StatusText = Resources.Strings.Status_NoFoldersToRefresh, Percentage = 100 });
                return false;
            }

            var anyChangesMade = false;

            foreach (var (folderIndex, folder) in folders.Index())
            {
                operationToken.ThrowIfCancellationRequested();

                var newProgress = new Progress<ScanProgress>(p =>
                {
                    var totalPercentage = (folderIndex + p.Percentage / 100.0) / totalFolders * 100.0;
                    var status = totalFolders > 1
                        ? string.Format(Resources.Strings.Format_ScanFolderProgress, folderIndex + 1, totalFolders, folder.Name, p.StatusText)
                        : p.StatusText;

                    progress?.Report(new ScanProgress
                    {
                        StatusText = status,
                        Percentage = totalPercentage,
                        IsIndeterminate = p.IsIndeterminate,
                    });
                });

                var changes = await RescanFolderForMusicAsync(folder.Id, forceFullScan,
                        allowFolderRemovalOnMissing: false, newProgress, operationToken, registerAsActiveScan: false,
                        throwOnFailure: throwOnFailure)
                    .ConfigureAwait(false);
                operationToken.ThrowIfCancellationRequested();
                anyChangesMade |= changes;
            }

            // Run ReplayGain analysis ONCE after all folders are scanned (if enabled and changes were made)
            if (anyChangesMade && await _settingsService.GetVolumeNormalizationEnabledAsync().ConfigureAwait(false))
            {
                await RunReplayGainAnalysisAsync(progress, operationToken).ConfigureAwait(false);
            }

            progress?.Report(new ScanProgress { StatusText = Resources.Strings.Status_LibraryRefreshComplete, Percentage = 100 });
            if (anyChangesMade)
            {
                LibraryContentChanged?.Invoke(this, new LibraryContentChangedEventArgs(LibraryChangeType.LibraryRescanned));
            }
            return anyChangesMade;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Library refresh was cancelled.");
            progress?.Report(new ScanProgress { StatusText = Resources.Strings.Status_ScanCancelled, Percentage = 100 });
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh all library folders.");
            if (throwOnFailure)
                throw;
            return false;
        }
        finally
        {
            ClearActiveScanCancellationSource(operationCts);
        }
    }

    /// <inheritdoc />
    public async Task<Artist?> GetArtistDetailsAsync(Guid artistId, bool allowOnlineFetch, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var artist = await context.Artists.AsTracking().FirstOrDefaultAsync(a => a.Id == artistId, cancellationToken).ConfigureAwait(false);

        if (artist is null) return null;

        var needsUpdate = string.IsNullOrWhiteSpace(artist.Biography) ||
                          string.IsNullOrWhiteSpace(artist.LocalImageCachePath);
        // Only fetch if never checked before. Once checked, respect the result.
        var neverChecked = artist.MetadataLastCheckedUtc == null;
        if (allowOnlineFetch && needsUpdate && neverChecked)
        {
            try
            {
                await FetchAndUpdateArtistFromRemoteAsync(context, artist, cancellationToken, saveChanges: true, suppressEvents: false).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to fetch online metadata for artist '{ArtistName}'. Proceeding with local data.", artist.Name);
            }
        }

        return await context.Artists.AsNoTracking()
            .Include(a => a.AlbumArtists).ThenInclude(aa => aa.Album)
            .FirstOrDefaultAsync(a => a.Id == artistId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task StartArtistMetadataBackgroundFetchAsync()
    {
        if (_shutdownCts.IsCancellationRequested) return;
        if (!await _metadataFetchSemaphore.WaitAsync(0).ConfigureAwait(false))
        {
            _logger.LogDebug("Artist metadata background fetch already running, skipping.");
            return;
        }

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
        _metadataFetchCts = runCts;
        try
        {
            var token = runCts.Token;
            await Task.Run(async () =>
            {
                var enabledProviders = await _settingsService
                    .GetEnabledServiceProvidersAsync(Models.ServiceCategory.Metadata)
                    .ConfigureAwait(false);

                if (enabledProviders.Count == 0)
                {
                    _logger.LogDebug("Artist metadata background fetch: no providers enabled, exiting.");
                    return;
                }

                const int batchSize = 50;
                Guid? afterId = null;
                while (!token.IsCancellationRequested)
                {
                    await using var batchContext = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
                    var query = batchContext.Artists.Where(a => a.MetadataLastCheckedUtc == null);
                    if (afterId.HasValue) query = query.Where(a => a.Id > afterId.Value);
                    var artistsInBatch = await query.OrderBy(a => a.Id).Take(batchSize)
                        .ToListAsync(token).ConfigureAwait(false);

                    if (artistsInBatch.Count == 0) break;
                    // A failed lookup stays retryable, but must not loop forever in this run.
                    afterId = artistsInBatch[^1].Id;
                    var artistIdsToUpdate = artistsInBatch.Select(a => a.Id).ToArray();

                    var directoryRows = await batchContext.SongArtists
                        .AsNoTracking()
                        .Where(sa => artistIdsToUpdate.Contains(sa.ArtistId))
                        .Select(sa => new { sa.ArtistId, sa.Song.DirectoryPath })
                        .Distinct()
                        .ToListAsync(token).ConfigureAwait(false);

                    var directoriesByArtist = directoryRows
                        .GroupBy(r => r.ArtistId)
                        .ToDictionary(g => g.Key, g => (IReadOnlyList<string?>)g.Select(r => r.DirectoryPath).ToList());

                    var pendingUpdates = new List<ArtistMetadataUpdatedEventArgs>();
                    var stopwatch = Stopwatch.StartNew();
                    int processedCount = 0;

                    foreach (var artist in artistsInBatch)
                    {
                        if (token.IsCancellationRequested) break;
                        try
                        {
                            if (!directoriesByArtist.TryGetValue(artist.Id, out var artistDirectories))
                                artistDirectories = Array.Empty<string?>();

                            var (updated, newImagePath) = await FetchAndUpdateArtistFromRemoteAsync(
                                batchContext, artist, token, saveChanges: false, suppressEvents: true,
                                preloadedEnabledProviders: enabledProviders,
                                preloadedSongDirectories: artistDirectories).ConfigureAwait(false);

                            if (updated)
                            {
                                pendingUpdates.Add(new ArtistMetadataUpdatedEventArgs(artist.Id, newImagePath));
                            }

                            if (++processedCount >= 10 || stopwatch.Elapsed.TotalSeconds >= 5)
                            {
                                await batchContext.SaveChangesAsync(token).ConfigureAwait(false);

                                if (pendingUpdates.Count > 0)
                                {
                                    _logger.LogInformation("Saved partial batch of {Count} artists metadata (Time: {Elapsed}s).", pendingUpdates.Count, stopwatch.Elapsed.TotalSeconds.ToString("F1"));
                                    ArtistMetadataBatchUpdated?.Invoke(this, pendingUpdates.ToList());
                                    pendingUpdates.Clear();
                                }

                                processedCount = 0;
                                stopwatch.Restart();
                            }
                        }
                        catch (DbUpdateConcurrencyException)
                        {
                            _logger.LogWarning(
                                "Concurrency conflict for artist {ArtistId} during background fetch. Ignoring.",
                                artist.Id);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger.LogError(ex, "Failed to update artist {ArtistId} in background.", artist.Id);
                        }

                        // Throttle: Yield to UI and other threads to prevent CPU starvation
                        await Task.Delay(50, token).ConfigureAwait(false);
                    }

                    if (token.IsCancellationRequested) break;

                    // Persist MetadataLastCheckedUtc even when no content was updated.
                    await batchContext.SaveChangesAsync(token).ConfigureAwait(false);

                    if (pendingUpdates.Count > 0)
                    {
                        _logger.LogInformation("Saved final batch of {Count} artists metadata.", pendingUpdates.Count);

                        ArtistMetadataBatchUpdated?.Invoke(this, pendingUpdates);
                    }
                }
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Artist metadata background fetch was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in artist metadata background fetch.");
        }
        finally
        {
            _metadataFetchCts = null;
            _metadataFetchSemaphore.Release();
        }
    }

    #endregion
    #region Song Management

    /// <inheritdoc />
    public async Task<Song?> GetSongByIdAsync(Guid songId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Songs.AsNoTracking()
            .Include(s => s.SongArtists).ThenInclude(sa => sa.Artist)
            .Include(s => s.Album).ThenInclude(a => a!.AlbumArtists).ThenInclude(aa => aa.Artist)
            .Include(s => s.Folder)
            .AsSplitQuery()
            .FirstOrDefaultAsync(s => s.Id == songId).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Song?> GetSongByFilePathAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Songs.AsNoTracking()
            .Include(s => s.Album)
            .Include(s => s.Folder)
            .AsSplitQuery()
            .FirstOrDefaultAsync(s => s.FilePath == filePath).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, Song>> GetSongsByIdsAsync(IEnumerable<Guid> songIds)
    {
        var result = new Dictionary<Guid, Song>();
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        foreach (var chunk in songIds.Distinct().Chunk(500))
        {

            var query = context.Songs.AsNoTracking()
                .Where(s => chunk.Contains(s.Id))
                .Include(s => s.Album);

            var batch = await SongQuery.ExcludeHeavyFields(query)
                .AsSplitQuery()
                .ToListAsync().ConfigureAwait(false);

            foreach (var song in batch)
            {
                result[song.Id] = song;
            }
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<Guid, Song>> GetQueueSongsAsync(IEnumerable<Guid> songIds,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<Guid, Song>();
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        foreach (var chunk in songIds.Distinct().Chunk(500))
        {
            var songs = await context.Songs.AsNoTracking().Where(s => chunk.Contains(s.Id))
                .Select(s => new Song
                {
                    Id = s.Id, Title = s.Title, ArtistName = s.ArtistName,
                    DurationTicks = s.DurationTicks, Genres = s.Genres,
                    SongArtists = s.SongArtists.Select(sa => new SongArtist
                    {
                        Order = sa.Order,
                        Artist = new Artist { Id = sa.Artist.Id, Name = sa.Artist.Name }
                    }).ToList()
                }).AsSplitQuery().ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var song in songs) result[song.Id] = song;
        }
        return result;
    }

    public async Task<TimeSpan> GetSongsDurationAsync(IEnumerable<Guid> songIds,
        CancellationToken cancellationToken = default)
    {
        long ticks = 0;
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        foreach (var chunk in songIds.Distinct().Chunk(500))
            ticks += await context.Songs.Where(s => chunk.Contains(s.Id))
                .SumAsync(s => s.DurationTicks, cancellationToken).ConfigureAwait(false);
        return TimeSpan.FromTicks(ticks);
    }

    #endregion
    #region Song Metadata Updates

    /// <inheritdoc />
    public Task<bool> UpdateSongLrcPathAsync(Guid songId, string? lrcPath)
    {
        return UpdateSongPropertyAsync(songId, s => s.LrcFilePath = lrcPath);
    }

    /// <inheritdoc />
    public Task<bool> UpdateSongLyricsLastCheckedAsync(Guid songId)
    {
        return UpdateSongPropertyAsync(songId, s => s.LyricsLastCheckedUtc = DateTime.UtcNow);
    }

    #endregion
    #region Artist Management

    /// <inheritdoc />
    public async Task<Artist?> GetArtistByNameAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var normalizedName = ArtistNameHelper.Normalize(name);
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Artists.AsNoTracking().FirstOrDefaultAsync(a => a.Name == normalizedName).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Album>> GetTopAlbumsForArtistAsync(Guid artistId, int limit)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.AlbumArtists.AsNoTracking()
            .Where(aa => aa.ArtistId == artistId)
            .Select(aa => aa.Album)
            .OrderByDescending(album => album.Songs.Sum(s => s.PlayCount))
            .ThenByDescending(album => album.Songs.Count)
            .ThenBy(album => album.Id)
            .Take(limit)
            .ToListAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Artist>> GetArtistsForSongAsync(Guid songId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.SongArtists.AsNoTracking()
            .Where(sa => sa.SongId == songId)
            .OrderBy(sa => sa.Order)
            .Select(sa => new Artist
            {
                Id = sa.Artist.Id,
                Name = sa.Artist.Name
            })
            .ToListAsync()
            .ConfigureAwait(false);
    }

    #endregion

    #region Album Management

    /// <inheritdoc />
    public async Task<Album?> GetAlbumByIdAsync(Guid albumId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Albums.AsNoTracking()
            .Include(al => al.AlbumArtists).ThenInclude(aa => aa.Artist)
            .AsSplitQuery()
            .FirstOrDefaultAsync(al => al.Id == albumId).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TimeSpan> GetSearchTotalDurationInAlbumAsync(Guid albumId, string searchTerm, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        var ticks = await FilterScopedSongs(context.Songs.Where(s => s.AlbumId == albumId), searchTerm)
            .SumAsync(s => s.DurationTicks, token).ConfigureAwait(false);

        return TimeSpan.FromTicks(ticks);
    }

    #endregion

    #region Playlist Management

    /// <inheritdoc />
    public async Task<Playlist?> CreatePlaylistAsync(string name, string? description = null,
        string? coverImageUri = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException(Resources.Strings.Error_PlaylistNameEmpty, nameof(name));

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var playlist = new Playlist
        {
            Name = NormalizeString(name) ?? name,
            Description = description,
            DateCreated = DateTime.UtcNow,
            DateModified = DateTime.UtcNow
        };

        if (!string.IsNullOrEmpty(coverImageUri) && _fileSystem.FileExists(coverImageUri))
        {
            var cachePath = _pathConfig.PlaylistImageCachePath;
            var originalBytes = await _fileSystem.ReadAllBytesAsync(coverImageUri).ConfigureAwait(false);
            var processedBytes = await _imageProcessor.ProcessImageBytesAsync(originalBytes).ConfigureAwait(false);
            playlist.CoverImageUri = await ImageStorageHelper.SaveImageBytesAsync(_fileSystem, cachePath,
                playlist.Id.ToString(), ".custom", processedBytes).ConfigureAwait(false);
        }

        context.Playlists.Add(playlist);
        await context.SaveChangesAsync().ConfigureAwait(false);
        PlaylistsChanged?.Invoke(this, EventArgs.Empty);
        return playlist;
    }

    /// <inheritdoc />
    public async Task<bool> DeletePlaylistAsync(Guid playlistId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var playlist = await context.Playlists.FindAsync(playlistId).ConfigureAwait(false);
        if (playlist is null) return false;

        context.Playlists.Remove(playlist);
        await context.SaveChangesAsync().ConfigureAwait(false);
        try
        {
            ImageStorageHelper.DeleteImage(_fileSystem, _pathConfig.PlaylistImageCachePath, playlistId.ToString(), ".custom");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete cached cover for deleted playlist {PlaylistId}.", playlistId);
        }
        PlaylistsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RenamePlaylistAsync(Guid playlistId, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException(Resources.Strings.Error_PlaylistNameEmpty, nameof(newName));

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var playlist = await context.Playlists.FindAsync(playlistId).ConfigureAwait(false);
        if (playlist is null) return false;

        playlist.Name = NormalizeString(newName) ?? newName;
        playlist.DateModified = DateTime.UtcNow;
        await context.SaveChangesAsync().ConfigureAwait(false);
        PlaylistUpdated?.Invoke(this, new PlaylistUpdatedEventArgs(playlist.Id, playlist.CoverImageUri, playlist.DateModified));
        PlaylistsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> UpdatePlaylistCoverAsync(Guid playlistId, string? newCoverImageUri)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var playlist = await context.Playlists.FindAsync(playlistId).ConfigureAwait(false);
        if (playlist is null) return false;

        if (!string.IsNullOrEmpty(newCoverImageUri))
        {
            if (!_fileSystem.FileExists(newCoverImageUri)) return false;
            var cachePath = _pathConfig.PlaylistImageCachePath;
            var originalBytes = await _fileSystem.ReadAllBytesAsync(newCoverImageUri).ConfigureAwait(false);
            var processedBytes = await _imageProcessor.ProcessImageBytesAsync(originalBytes).ConfigureAwait(false);
            var newPath = await ImageStorageHelper.SaveImageBytesAsync(_fileSystem, cachePath,
                playlistId.ToString(), ".custom", processedBytes).ConfigureAwait(false);
            if (newPath is null) return false;
            playlist.CoverImageUri = newPath;
        }
        else
        {
            var cachePath = _pathConfig.PlaylistImageCachePath;
            ImageStorageHelper.DeleteImage(_fileSystem, cachePath, playlistId.ToString(), ".custom");
            playlist.CoverImageUri = null;
        }

        playlist.DateModified = DateTime.UtcNow;
        await context.SaveChangesAsync().ConfigureAwait(false);
        PlaylistUpdated?.Invoke(this, new PlaylistUpdatedEventArgs(playlist.Id, playlist.CoverImageUri, playlist.DateModified));
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> AddSongsToPlaylistAsync(Guid playlistId, IEnumerable<Guid> songIds)
    {
        var uniqueSongIds = songIds.Distinct().ToArray();
        if (uniqueSongIds.Length == 0) return false;

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var playlist = await context.Playlists.FindAsync(playlistId).ConfigureAwait(false);
        if (playlist is null) return false;

        var existingSongIds = await context.PlaylistSongs
            .Where(ps => ps.PlaylistId == playlistId)
            .Select(ps => ps.SongId)
            .ToHashSetAsync().ConfigureAwait(false);

        var songIdsToAdd = uniqueSongIds.Except(existingSongIds).ToList();
        if (songIdsToAdd.Count == 0) return true;

        var maxOrder = await context.PlaylistSongs
            .Where(ps => ps.PlaylistId == playlistId)
            .MaxAsync(ps => (double?)ps.Order).ConfigureAwait(false) ?? 0;

        var nextOrder = Math.Floor(maxOrder) + 1.0;

        var playlistSongsToAdd = songIdsToAdd.Select((songId, index) => new PlaylistSong
        {
            PlaylistId = playlistId,
            SongId = songId,
            Order = nextOrder + index
        });

        context.PlaylistSongs.AddRange(playlistSongsToAdd);
        playlist.DateModified = DateTime.UtcNow;
        await context.SaveChangesAsync().ConfigureAwait(false);
        PlaylistsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveSongsFromPlaylistAsync(Guid playlistId, IEnumerable<Guid> songIds)
    {
        var uniqueSongIds = songIds.Distinct().ToArray();
        if (uniqueSongIds.Length == 0) return false;

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var playlist = await context.Playlists.FindAsync(playlistId).ConfigureAwait(false);
        if (playlist is null) return false;

        var rowsDeleted = await context.PlaylistSongs
            .Where(ps => ps.PlaylistId == playlistId && uniqueSongIds.Contains(ps.SongId))
            .ExecuteDeleteAsync().ConfigureAwait(false);

        playlist.DateModified = DateTime.UtcNow;
        await context.SaveChangesAsync().ConfigureAwait(false);
        if (rowsDeleted > 0)
        {
            PlaylistsChanged?.Invoke(this, EventArgs.Empty);
        }
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> UpdatePlaylistOrderAsync(Guid playlistId, IEnumerable<Guid> orderedSongIds)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var playlist = await context.Playlists.FindAsync(playlistId).ConfigureAwait(false);
        if (playlist is null) return false;

        var playlistSongs = await context.PlaylistSongs
            .Where(ps => ps.PlaylistId == playlistId)
            .ToListAsync().ConfigureAwait(false);

        var playlistSongMap = playlistSongs.ToDictionary(ps => ps.SongId);
        var orderedSongIdList = orderedSongIds.ToList();
        var updated = false;

        for (var i = 0; i < orderedSongIdList.Count; i++)
        {
            var songId = orderedSongIdList[i];
            if (playlistSongMap.TryGetValue(songId, out var playlistSong))
            {
                var newOrder = (double)i + 1.0;
                if (playlistSong.Order != newOrder)
                {
                    playlistSong.Order = newOrder;
                    updated = true;
                }
            }
        }

        if (updated)
        {
            playlist.DateModified = DateTime.UtcNow;
            await context.SaveChangesAsync().ConfigureAwait(false);
            _logger.LogInformation("Successfully normalized order for playlist {PlaylistId}", playlistId);
        }
        else
        {
            _logger.LogDebug("Normalization for playlist {PlaylistId} resulted in no changes.", playlistId);
        }

        return updated;
    }

    /// <inheritdoc />
    public async Task<bool> MovePlaylistSongAsync(Guid playlistId, Guid songId, double newOrder)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var playlistSong = await context.PlaylistSongs
            .Include(ps => ps.Playlist)
            .FirstOrDefaultAsync(ps => ps.PlaylistId == playlistId && ps.SongId == songId)
            .ConfigureAwait(false);

        if (playlistSong is null) return false;

        playlistSong.Order = newOrder;
        _logger.LogDebug("Saving new order {Order} for song {SongId} in playlist {PlaylistId}", newOrder, songId, playlistId);

        playlistSong.Playlist.DateModified = DateTime.UtcNow;

        await context.SaveChangesAsync().ConfigureAwait(false);
        _logger.LogDebug("Successfully moved song {SongId} in playlist {PlaylistId} to order {Order}", songId, playlistId, newOrder);
        return true;
    }

    /// <inheritdoc />
    public async Task<Playlist?> GetPlaylistByIdAsync(Guid playlistId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Playlists.AsNoTracking()
            .Include(p => p.PlaylistSongs).ThenInclude(ps => ps.Song).ThenInclude(s => s!.Album)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == playlistId).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Playlist>> GetAllPlaylistsAsync(CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Playlists.AsNoTracking()
            .Include(p => p.PlaylistSongs)
            .OrderBy(p => p.Name).ThenBy(p => p.Id)
            .ToListAsync(token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Song>> GetSongsInPlaylistOrderedAsync(Guid playlistId, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.PlaylistSongs.AsNoTracking()
            .Where(ps => ps.PlaylistId == playlistId)
            .OrderBy(ps => ps.Order).ThenBy(ps => ps.SongId)
            .Select(ps => ps.Song!);

        return await SongQuery.ExcludeHeavyFields(query)
            .AsSplitQuery()
            .ToListAsync(token).ConfigureAwait(false);
    }

    #endregion

    #region Genre Management

    /// <inheritdoc />
    public async Task<IEnumerable<GenreSummary>> GetGenreSummariesAsync(CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        return await context.Genres.AsNoTracking()
            .OrderBy(g => g.Name).ThenBy(g => g.Id)
            .Select(g => new GenreSummary(g.Id, g.Name, g.Songs.Count))
            .ToListAsync(token).ConfigureAwait(false);
    }

    #endregion

    #region Listen History

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
    public async Task<bool> MarkListenAsScrobbledAsync(long listenHistoryId)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await dbContext.ListenHistory.Where(history => history.Id == listenHistoryId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(history => history.IsScrobbled, true))
            .ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public async Task<bool> MarkListenAsSubmittedToListenBrainzAsync(long listenHistoryId)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await dbContext.ListenHistory.Where(history => history.Id == listenHistoryId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(history => history.IsSubmittedToListenBrainz, true))
            .ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public async Task<Guid?> GetRandomAlbumIdAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await GetRandomEntityIdAsync(context.Albums.AsNoTracking()).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Guid?> GetRandomArtistIdAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await GetRandomEntityIdAsync(context.Artists.AsNoTracking()).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Guid?> GetRandomGenreIdAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await GetRandomEntityIdAsync(context.Genres.AsNoTracking()).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Guid?> GetRandomPlaylistIdAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await GetRandomEntityIdAsync(context.Playlists.AsNoTracking()).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> GetPlaylistCountAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Playlists.CountAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Guid?> GetRandomFolderIdAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        // Only select folders that actually contain songs to avoid playing empty directory structures
        var query = context.Folders.AsNoTracking().Where(f => f.Songs.Any());
        return await GetRandomEntityIdAsync(query).ConfigureAwait(false);
    }

    #endregion

    #region Paged Loading

    /// <inheritdoc />
    public async Task<PagedResult<Song>> GetAllSongsPagedAsync(int pageNumber, int pageSize,
        SongSortOrder sortOrder = SongSortOrder.TitleAsc, CancellationToken token = default)
    {
        SanitizePaging(ref pageNumber, ref pageSize);
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        return await ReadSongPageAsync(context.Songs, pageNumber, pageSize, sortOrder, token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PagedResult<Song>> SearchSongsPagedAsync(string searchTerm, int pageNumber, int pageSize, CancellationToken token = default)
    {
        SanitizePaging(ref pageNumber, ref pageSize);
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        var query = string.IsNullOrWhiteSpace(searchTerm)
            ? context.Songs
            : BuildSongSearchQuery(context, NormalizeString(searchTerm) ?? searchTerm);
        return await ReadSongPageAsync(query, pageNumber, pageSize, SongSortOrder.TitleAsc, token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PagedResult<Song>> GetSongsByAlbumIdPagedAsync(Guid albumId, int pageNumber, int pageSize,
        SongSortOrder sortOrder, CancellationToken token = default)
    {
        SanitizePaging(ref pageNumber, ref pageSize);
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        return await ReadSongPageAsync(context.Songs.Where(s => s.AlbumId == albumId), pageNumber, pageSize, sortOrder, token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PagedResult<Song>> GetSongsByArtistIdPagedAsync(Guid artistId, int pageNumber, int pageSize,
        SongSortOrder sortOrder, CancellationToken token = default)
    {
        SanitizePaging(ref pageNumber, ref pageSize);
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        return await ReadSongPageAsync(context.Songs.Where(s => s.SongArtists.Any(sa => sa.ArtistId == artistId)), pageNumber, pageSize, sortOrder, token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PagedResult<Song>> GetSongsByGenreIdPagedAsync(Guid genreId, int pageNumber, int pageSize,
        SongSortOrder sortOrder, CancellationToken token = default)
    {
        SanitizePaging(ref pageNumber, ref pageSize);
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        return await ReadSongPageAsync(context.Songs.Where(s => s.Genres.Any(g => g.Id == genreId)), pageNumber, pageSize, sortOrder, token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PagedResult<Song>> GetSongsByPlaylistPagedAsync(Guid playlistId, int pageNumber, int pageSize, SongSortOrder sortOrder, CancellationToken token = default)
    {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.PlaylistSongs.AsNoTracking()
            .Where(ps => ps.PlaylistId == playlistId);

        return await ReadPlaylistPageAsync(context, query, pageNumber, pageSize, sortOrder, token).ConfigureAwait(false);
    }

    private async Task<PagedResult<Song>> ReadPlaylistPageAsync(MusicDbContext context, IQueryable<PlaylistSong> query,
        int pageNumber, int pageSize, SongSortOrder sortOrder, CancellationToken token)
    {
        if (sortOrder == SongSortOrder.PlaylistOrder)
        {
            var projectedQuery = query
                .Join(SongQuery.ExcludeHeavyFields(context.Songs.AsNoTracking()),
                    entry => entry.SongId, song => song.Id,
                    (entry, song) => new { Song = song, entry.Order })
                .OrderBy(row => row.Order).ThenBy(row => row.Song.Id);

            var totalCount = await query.CountAsync(token).ConfigureAwait(false);

            var pagedResults = await projectedQuery
                .Skip((pageNumber - 1) * pageSize).Take(pageSize)
                .AsSplitQuery()
                .ToListAsync(token).ConfigureAwait(false);

            foreach (var result in pagedResults)
            {
                result.Song.Order = result.Order;
            }

            var pagedSongs = pagedResults.Select(r => r.Song).ToList();

            return new PagedResult<Song>
            { Items = pagedSongs, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
        }
        return await ReadSongPageAsync(query.Select(ps => ps.Song), pageNumber, pageSize, sortOrder, token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PagedResult<Artist>> GetAllArtistsPagedAsync(int pageNumber, int pageSize,
        ArtistSortOrder sortOrder = ArtistSortOrder.NameAsc, CancellationToken token = default)
    {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.Artists.AsNoTracking();
        var totalCount = await query.CountAsync(token).ConfigureAwait(false);
        var ignoreArticles = _ignoreArticlesOnSort;

        // Apply sort order - for SongCountDesc, we need to join with songs and count
        IOrderedQueryable<Artist> orderedQuery = sortOrder switch
        {
            ArtistSortOrder.NameDesc => ignoreArticles
                ? query.OrderByDescending(a => a.SortName).ThenByDescending(a => a.Id)
                : query.OrderByDescending(a => a.Name).ThenByDescending(a => a.Id),
            ArtistSortOrder.SongCountDesc => ignoreArticles
                ? query.OrderByDescending(a => context.SongArtists.Count(sa => sa.ArtistId == a.Id))
                    .ThenByDescending(a => a.SortName).ThenByDescending(a => a.Id)
                : query.OrderByDescending(a => context.SongArtists.Count(sa => sa.ArtistId == a.Id))
                    .ThenByDescending(a => a.Name).ThenByDescending(a => a.Id),
            ArtistSortOrder.SongCountAsc => ignoreArticles
                ? query.OrderBy(a => context.SongArtists.Count(sa => sa.ArtistId == a.Id))
                    .ThenBy(a => a.SortName).ThenBy(a => a.Id)
                : query.OrderBy(a => context.SongArtists.Count(sa => sa.ArtistId == a.Id))
                    .ThenBy(a => a.Name).ThenBy(a => a.Id),
            _ => ignoreArticles
                ? query.OrderBy(a => a.SortName).ThenBy(a => a.Id)
                : query.OrderBy(a => a.Name).ThenBy(a => a.Id)
        };

        var pagedData = await orderedQuery
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).ToListAsync(token).ConfigureAwait(false);

        return new PagedResult<Artist>
        { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <inheritdoc />
    public async Task<PagedResult<Artist>> SearchArtistsPagedAsync(string searchTerm, int pageNumber, int pageSize, CancellationToken token = default)
    {
        SanitizePaging(ref pageNumber, ref pageSize);

        var normalizedTerm = NormalizeString(searchTerm) ?? string.Empty;
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = string.IsNullOrWhiteSpace(searchTerm)
            ? context.Artists
            : BuildArtistSearchQuery(context, normalizedTerm);
        var totalCount = await query.CountAsync(token).ConfigureAwait(false);
        var sortedQuery = _ignoreArticlesOnSort
            ? query.AsNoTracking().OrderBy(a => a.SortName).ThenBy(a => a.Id)
            : query.AsNoTracking().OrderBy(a => a.Name).ThenBy(a => a.Id);
        var pagedData = await sortedQuery
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).ToListAsync(token).ConfigureAwait(false);

        return new PagedResult<Artist>
        { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <inheritdoc />
    public async Task<PagedResult<Album>> GetAllAlbumsPagedAsync(int pageNumber, int pageSize,
        AlbumSortOrder sortOrder = AlbumSortOrder.ArtistAsc, CancellationToken token = default)
    {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.Albums.AsNoTracking().Include(al => al.AlbumArtists).ThenInclude(aa => aa.Artist);
        var totalCount = await query.CountAsync(token).ConfigureAwait(false);
        var orderedQuery = ApplyAlbumSortOrder(query, sortOrder);

        var pagedData = await orderedQuery
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync(token).ConfigureAwait(false);

        return new PagedResult<Album>
        { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <inheritdoc />
    public async Task<PagedResult<Album>> SearchAlbumsPagedAsync(string searchTerm, int pageNumber, int pageSize, CancellationToken token = default)
    {
        SanitizePaging(ref pageNumber, ref pageSize);
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        var query = string.IsNullOrWhiteSpace(searchTerm)
            ? context.Albums
            : BuildAlbumSearchQuery(context, NormalizeString(searchTerm) ?? string.Empty);
        var totalCount = await query.CountAsync(token).ConfigureAwait(false);
        var pagedData = await ApplyAlbumSortOrder(query.AsNoTracking().Include(al => al.AlbumArtists).ThenInclude(aa => aa.Artist),
                AlbumSortOrder.ArtistAsc)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync(token).ConfigureAwait(false);
        return new PagedResult<Album>
        { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <inheritdoc />
    public async Task<List<Guid>> GetAllSongIdsAsync(SongSortOrder sortOrder, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await ApplySongSortOrder(context.Songs.AsNoTracking(), sortOrder).Select(s => s.Id).ToListAsync(token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<List<Guid>> SearchAllSongIdsAsync(string searchTerm, SongSortOrder sortOrder, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetAllSongIdsAsync(sortOrder, token).ConfigureAwait(false);

        var normalizedTerm = NormalizeString(searchTerm) ?? string.Empty;
        var query = BuildSongSearchQuery(context, normalizedTerm);
        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync(token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<List<Guid>> GetAllSongIdsByFolderIdAsync(Guid folderId, SongSortOrder sortOrder, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.Songs.AsNoTracking().Where(s => s.FolderId == folderId);
        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync(token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<List<Guid>> GetAllSongIdsInDirectoryRecursiveAsync(Guid folderId, string directoryPath,
        SongSortOrder sortOrder, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        // Normalize the directory path for comparison
        var normalizedPath = NormalizeDirectoryPath(directoryPath);

        // Get all song IDs where DirectoryPath equals normalizedPath or starts with normalizedPath followed by a separator
        // This prevents false positives (e.g., "C:\\Music\\Rock" matching "C:\\Music\\Rockabilly")
        var query = context.Songs.AsNoTracking()
            .Where(s => s.FolderId == folderId &&
                        (s.DirectoryPath == normalizedPath ||
                         s.DirectoryPath.StartsWith(normalizedPath.TrimEnd('\\', '/') + "\\") ||
                         s.DirectoryPath.StartsWith(normalizedPath.TrimEnd('\\', '/') + "/")));

        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync(token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<PagedResult<Song>> GetSongsInDirectoryOffsetAsync(Guid folderId, string directoryPath,
        int skip, int take, SongSortOrder sortOrder, CancellationToken token = default)
    {
        return SearchSongsInFolderOffsetAsync(folderId, directoryPath, string.Empty, skip, take, sortOrder, token);
    }

    /// <inheritdoc />
    public async Task<PagedResult<Song>> SearchSongsInFolderOffsetAsync(Guid folderId, string directoryPath, string searchTerm, int skip, int take, SongSortOrder sortOrder, CancellationToken token = default)
    {
        skip = Math.Max(0, skip);
        take = Math.Max(0, take);
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var normalizedPath = NormalizeDirectoryPath(directoryPath);
        var baseQuery = context.Songs.AsNoTracking()
            .Where(s => s.FolderId == folderId && s.DirectoryPath == normalizedPath);

        baseQuery = FilterScopedSongs(baseQuery, searchTerm);

        var totalCount = await baseQuery.CountAsync(token).ConfigureAwait(false);
        IEnumerable<Song> items = Enumerable.Empty<Song>();
        if (take > 0 && totalCount > 0)
        {
            var queryWithIncludes = baseQuery
                .Include(s => s.SongArtists).ThenInclude(sa => sa.Artist)
                .Include(s => s.Album).ThenInclude(a => a!.AlbumArtists).ThenInclude(aa => aa.Artist);

            items = await ApplySongSortOrder(SongQuery.ExcludeHeavyFields(queryWithIncludes), sortOrder)
                .Skip(skip).Take(take).AsSplitQuery().ToListAsync(token).ConfigureAwait(false);
        }

        return new PagedResult<Song> { Items = items, TotalCount = totalCount, PageNumber = 0, PageSize = take };
    }

    /// <inheritdoc />
    public async Task<List<Guid>> GetAllSongIdsByArtistIdAsync(Guid artistId, SongSortOrder sortOrder, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.Songs.AsNoTracking().Where(s => s.SongArtists.Any(sa => sa.ArtistId == artistId));
        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync(token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<List<Guid>> GetAllSongIdsByAlbumIdAsync(Guid albumId, SongSortOrder sortOrder, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.Songs.AsNoTracking().Where(s => s.AlbumId == albumId);
        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync(token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<List<Guid>> GetAllSongIdsByPlaylistIdAsync(Guid playlistId, SongSortOrder sortOrder, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.PlaylistSongs.AsNoTracking()
            .Where(ps => ps.PlaylistId == playlistId);

        if (sortOrder == SongSortOrder.PlaylistOrder)
        {
            return await query.OrderBy(ps => ps.Order).ThenBy(ps => ps.SongId).Select(ps => ps.SongId).ToListAsync(token).ConfigureAwait(false);
        }

        var songQuery = query.Select(ps => ps.Song!);
        return await ApplySongSortOrder(songQuery, sortOrder).Select(s => s.Id).ToListAsync(token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<List<Guid>> GetAllSongIdsByGenreIdAsync(Guid genreId, SongSortOrder sortOrder, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.Songs.AsNoTracking().Where(s => s.Genres.Any(g => g.Id == genreId));
        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync(token).ConfigureAwait(false);
    }

    #endregion
    #region Scoped Search

    /// <inheritdoc />
    public async Task<PagedResult<Song>> SearchSongsInAlbumPagedAsync(Guid albumId, string searchTerm, int pageNumber,
        int pageSize, CancellationToken token = default)
    {
        SanitizePaging(ref pageNumber, ref pageSize);
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var baseQuery = context.Songs.AsNoTracking()
            .Where(s => s.AlbumId == albumId);

        baseQuery = FilterScopedSongs(baseQuery, searchTerm);

        return await ReadSongPageAsync(baseQuery.Include(s => s.Album).ThenInclude(a => a!.AlbumArtists).ThenInclude(aa => aa.Artist),
            pageNumber, pageSize, SongSortOrder.TrackNumberAsc, token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PagedResult<Song>> SearchSongsInArtistPagedAsync(Guid artistId, string searchTerm, int pageNumber,
        int pageSize, CancellationToken token = default)
    {
        SanitizePaging(ref pageNumber, ref pageSize);
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var baseQuery = context.Songs.AsNoTracking()
            .Where(s => s.SongArtists.Any(sa => sa.ArtistId == artistId));

        baseQuery = FilterScopedSongs(baseQuery, searchTerm);

        return await ReadSongPageAsync(baseQuery.Include(s => s.Album).ThenInclude(a => a!.AlbumArtists).ThenInclude(aa => aa.Artist),
            pageNumber, pageSize, SongSortOrder.AlbumAsc, token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PagedResult<Song>> SearchSongsInPlaylistPagedAsync(Guid playlistId, string searchTerm,
        int pageNumber, int pageSize, SongSortOrder sortOrder, CancellationToken token = default)
    {
        SanitizePaging(ref pageNumber, ref pageSize);
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var query = context.PlaylistSongs.AsNoTracking()
            .Where(ps => ps.PlaylistId == playlistId);

        query = FilterPlaylistSongs(query, searchTerm);
        return await ReadPlaylistPageAsync(context, query, pageNumber, pageSize, sortOrder, token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PagedResult<Song>> SearchSongsInGenrePagedAsync(Guid genreId, string searchTerm, int pageNumber,
        int pageSize, CancellationToken token = default)
    {
        SanitizePaging(ref pageNumber, ref pageSize);
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var baseQuery = context.Songs.AsNoTracking()
            .Where(s => s.Genres.Any(g => g.Id == genreId));

        baseQuery = FilterScopedSongs(baseQuery, searchTerm);

        return await ReadSongPageAsync(baseQuery.Include(s => s.Album).ThenInclude(a => a!.AlbumArtists).ThenInclude(aa => aa.Artist),
            pageNumber, pageSize, SongSortOrder.TitleAsc, token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<List<Guid>> SearchAllSongIdsInFolderAsync(Guid folderId, string directoryPath, string searchTerm,
        SongSortOrder sortOrder, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var normalizedPath = NormalizeDirectoryPath(directoryPath);
        var query = context.Songs.AsNoTracking()
            .Where(s => s.FolderId == folderId && s.DirectoryPath == normalizedPath);

        query = FilterScopedSongs(query, searchTerm);

        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync(token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<List<Guid>> SearchAllSongIdsInArtistAsync(Guid artistId, string searchTerm,
        SongSortOrder sortOrder, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.Songs.AsNoTracking().Where(s => s.SongArtists.Any(sa => sa.ArtistId == artistId));

        query = FilterScopedSongs(query, searchTerm);

        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync(token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<List<Guid>> SearchAllSongIdsInAlbumAsync(Guid albumId, string searchTerm, SongSortOrder sortOrder, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.Songs.AsNoTracking().Where(s => s.AlbumId == albumId);

        query = FilterScopedSongs(query, searchTerm);

        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync(token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<List<Guid>> SearchAllSongIdsInPlaylistAsync(Guid playlistId, string searchTerm, SongSortOrder sortOrder, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.PlaylistSongs.AsNoTracking()
            .Where(ps => ps.PlaylistId == playlistId);

        query = FilterPlaylistSongs(query, searchTerm);
        if (sortOrder == SongSortOrder.PlaylistOrder)
            return await query.OrderBy(ps => ps.Order).ThenBy(ps => ps.SongId)
                .Select(ps => ps.SongId).ToListAsync(token).ConfigureAwait(false);

        var songQuery = query.Select(ps => ps.Song!);

        return await ApplySongSortOrder(songQuery, sortOrder)
            .Select(s => s.Id)
            .ToListAsync(token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<List<Guid>> SearchAllSongIdsInGenreAsync(Guid genreId, string searchTerm, SongSortOrder sortOrder, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.Songs.AsNoTracking().Where(s => s.Genres.Any(g => g.Id == genreId));

        query = FilterScopedSongs(query, searchTerm);

        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync(token).ConfigureAwait(false);
    }

    #endregion

    #region Private Helpers

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

    private async Task EnsureSubFoldersExistAsync(Guid rootFolderId, string rootFolderPath,
        HashSet<string> discoveredDirectories, CancellationToken cancellationToken)
    {
        if (discoveredDirectories.Count == 0) return;

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var folders = await context.Folders.ToListAsync(cancellationToken).ConfigureAwait(false);
        var foldersByPath = folders.ToDictionary(f => NormalizeDirectoryPath(f.Path), StringComparer.OrdinalIgnoreCase);
        var normalizedRootPath = NormalizeDirectoryPath(rootFolderPath);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Include empty intermediate directories so the browser can reach nested albums.
        foreach (var directory in discoveredDirectories)
        {
            var currentPath = NormalizeDirectoryPath(directory);
            while (!string.IsNullOrEmpty(currentPath) &&
                   !currentPath.Equals(normalizedRootPath, StringComparison.OrdinalIgnoreCase))
            {
                paths.Add(currentPath);
                var parent = _fileSystem.GetDirectoryName(currentPath);
                if (string.IsNullOrEmpty(parent)) break;
                currentPath = NormalizeDirectoryPath(parent);
            }
        }

        // Parents are shorter than their descendants and must be inserted first.
        foreach (var path in paths.OrderBy(p => p.Length))
        {
            var parentPath = _fileSystem.GetDirectoryName(path);
            var parentId = !string.IsNullOrEmpty(parentPath) &&
                           foldersByPath.TryGetValue(NormalizeDirectoryPath(parentPath), out var parent)
                ? parent.Id
                : rootFolderId;

            if (foldersByPath.TryGetValue(path, out var existing))
            {
                existing.ParentFolderId = parentId;
                continue;
            }

            var folder = new Folder
            {
                Name = Path.GetFileName(path), Path = path,
                ParentFolderId = parentId, LastModifiedDate = DateTime.UtcNow
            };
            context.Folders.Add(folder);
            foldersByPath.Add(path, folder);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string NormalizeDirectoryPath(string path) =>
        Path.TrimEndingDirectorySeparator(path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));

    private async Task<(List<string> filesToAdd, List<string> filesToUpdate, List<string> filesRemovedFromDisk)>
        AnalyzeFolderChangesAsync(Guid folderId, string folderPath, bool forceFullScan, CancellationToken cancellationToken)
    {
        await using var analysisContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var dbFiles = await analysisContext.Songs
            .AsNoTracking()
            .Where(s => s.FolderId == folderId)
            .Select(s => new { s.FilePath, s.FileModifiedDate })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var diskFileMap = _fileSystem.EnumerateFilesWithLastWriteTime(folderPath, "*.*", SearchOption.AllDirectories)
            .Where(x => FileExtensions.MusicFileExtensions.Contains(_fileSystem.GetExtension(x.Path)))
            .ToDictionary(x => x.Path, x => x.LastWriteTimeUtc, StringComparer.OrdinalIgnoreCase);

        cancellationToken.ThrowIfCancellationRequested();

        var dbFileMap = dbFiles
            .ToDictionary(s => s.FilePath, s => s.FileModifiedDate, StringComparer.OrdinalIgnoreCase);

        var filesToAdd = new List<string>();
        var filesToUpdate = new List<string>();
        foreach (var (path, diskModTime) in diskFileMap)
        {
            if (dbFileMap.TryGetValue(path, out var dbModTime))
            {
                if (forceFullScan || diskModTime != dbModTime)
                    filesToUpdate.Add(path);
            }
            else
            {
                filesToAdd.Add(path);
            }
        }

        var filesRemovedFromDisk = dbFileMap.Keys
            .Where(path => !diskFileMap.ContainsKey(path))
            .ToList();

        return (filesToAdd, filesToUpdate, filesRemovedFromDisk);
    }

    /// <summary>
    ///     Scans for songs in the folder that are missing LRC file references but now have
    ///     matching .lrc files on disk. This handles the case where a user adds LRC files
    ///     to an already-scanned folder without modifying the music files themselves.
    /// </summary>
    private async Task<int> UpdateMissingLrcPathsAsync(Guid folderId, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        // Query songs in the folder that don't have an LRC file path set
        var songsWithoutLrc = await context.Songs
            .Where(s => s.FolderId == folderId && s.LrcFilePath == null)
            .Select(s => new { s.Id, s.FilePath })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (songsWithoutLrc.Count == 0)
            return 0;

        // First pass: find LRC files using per-directory caches to avoid redundant filesystem calls.
        // Multiple songs in the same directory share a single GetFiles enumeration.
        var lrcFilesByDir = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var txtFilesByDir = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var songLrcMappings = new Dictionary<Guid, string>();

        foreach (var song in songsWithoutLrc)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var directory = _fileSystem.GetDirectoryName(song.FilePath);
            if (string.IsNullOrEmpty(directory)) continue;

            var nameWithoutExt = _fileSystem.GetFileNameWithoutExtension(song.FilePath);

            // Lazy-populate LRC cache for this directory
            if (!lrcFilesByDir.TryGetValue(directory, out var lrcByName))
            {
                lrcByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var p in _fileSystem.GetFiles(directory, "*.lrc"))
                        lrcByName.TryAdd(_fileSystem.GetFileNameWithoutExtension(p), p);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error enumerating LRC files in '{Directory}'.", directory);
                }
                lrcFilesByDir[directory] = lrcByName;
            }

            if (lrcByName.TryGetValue(nameWithoutExt, out var lrcPath))
            {
                songLrcMappings[song.Id] = lrcPath;
                continue;
            }

            // Lazy-populate TXT cache for this directory (only when no .lrc found)
            if (!txtFilesByDir.TryGetValue(directory, out var txtByName))
            {
                txtByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var p in _fileSystem.GetFiles(directory, "*.txt"))
                        txtByName.TryAdd(_fileSystem.GetFileNameWithoutExtension(p), p);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error enumerating TXT files in '{Directory}'.", directory);
                }
                txtFilesByDir[directory] = txtByName;
            }

            if (txtByName.TryGetValue(nameWithoutExt, out var txtPath))
                songLrcMappings[song.Id] = txtPath;
        }

        if (songLrcMappings.Count == 0)
            return 0;

        // Process in batches to avoid SQL parameter limits and memory issues with large libraries
        const int batchSize = 500;
        var totalUpdated = 0;

        foreach (var batchIds in songLrcMappings.Keys.Chunk(batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var songsToUpdate = await context.Songs
                .Where(s => batchIds.Contains(s.Id))
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            foreach (var song in songsToUpdate)
            {
                song.LrcFilePath = songLrcMappings[song.Id];
            }

            totalUpdated += songsToUpdate.Count;
        }

        if (totalUpdated > 0)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Updated {Count} songs with newly discovered LRC files.", totalUpdated);
        }

        return totalUpdated;
    }

    /// <summary>
    ///     Scans for songs in the folder that are missing cover art but now have
    ///     matching cover art files (cover.jpg, folder.png, etc.) on disk in their directory hierarchy.
    ///     This handles the case where a user adds cover art files to an already-scanned folder
    ///     without modifying the music files themselves.
    /// </summary>
    private async Task<int> UpdateMissingCoverArtAsync(Guid folderId, string baseFolderPath, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        // Query songs in the folder that don't have cover art set
        var songsWithoutCoverArt = await context.Songs
            .Where(s => s.FolderId == folderId && s.AlbumArtUriFromTrack == null)
            .Select(s => new { s.Id, s.FilePath })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (songsWithoutCoverArt.Count == 0)
            return 0;

        _logger.LogDebug("Found {Count} songs without cover art in folder {FolderId}. Searching for cover art files...",
            songsWithoutCoverArt.Count, folderId);

        // Cache processed cover art by directory to avoid redundant processing for songs in same folder
        var directoryCache = new Dictionary<string, (string? uri, string? lightSwatch, string? darkSwatch)?>(StringComparer.OrdinalIgnoreCase);
        var songCoverArtMappings = new Dictionary<Guid, (string? uri, string? lightSwatch, string? darkSwatch)>();

        // Shared per-directory cache for FindCoverArtInDirectory results across all hierarchy walks.
        // Parent directories (e.g., artist folder, root folder) are shared by multiple song directories
        // and would otherwise be scanned once per album directory without this cache.
        var hierarchyDirCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var song in songsWithoutCoverArt)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var songDirectory = _fileSystem.GetDirectoryName(song.FilePath) ?? string.Empty;

            if (directoryCache.TryGetValue(songDirectory, out var cachedResult))
            {
                if (cachedResult.HasValue)
                {
                    songCoverArtMappings[song.Id] = cachedResult.Value;
                }
                continue;
            }

            var coverArtPath = FindCoverArtInDirectoryHierarchy(song.FilePath, baseFolderPath, hierarchyDirCache);
            if (coverArtPath != null)
            {
                try
                {
                    // Read and process the cover art file directly using the image processor
                    // This is MUCH faster than re-extracting full audio metadata
                    var imageBytes = await _fileSystem.ReadAllBytesAsync(coverArtPath).ConfigureAwait(false);
                    if (imageBytes.Length > 0)
                    {
                        var coverArtResult = await _imageProcessor.SaveCoverArtAndExtractColorsAsync(imageBytes).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(coverArtResult.uri))
                        {
                            var result = (coverArtResult.uri, coverArtResult.lightSwatchId, coverArtResult.darkSwatchId);
                            directoryCache[songDirectory] = result;
                            songCoverArtMappings[song.Id] = result;
                            continue;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process cover art file {CoverArtPath} for song {SongId}.",
                        coverArtPath, song.Id);
                }
            }

            // No cover art found for this directory
            directoryCache[songDirectory] = null;
        }

        if (songCoverArtMappings.Count == 0)
            return 0;

        // Update songs with cover art in batches
        const int batchSize = 500;
        var totalUpdated = 0;

        foreach (var batchIds in songCoverArtMappings.Keys.Chunk(batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var songsToUpdate = await context.Songs
                .Where(s => batchIds.Contains(s.Id))
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            foreach (var song in songsToUpdate)
            {
                var coverArtInfo = songCoverArtMappings[song.Id];
                song.AlbumArtUriFromTrack = coverArtInfo.uri;
                song.LightSwatchId = coverArtInfo.lightSwatch;
                song.DarkSwatchId = coverArtInfo.darkSwatch;
            }

            totalUpdated += songsToUpdate.Count;
        }

        if (totalUpdated > 0)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Updated {Count} songs with newly discovered cover art files.", totalUpdated);
        }

        return totalUpdated;
    }

    /// <summary>
    ///     Finds artists linked to songs in the given folder that have no local or custom image,
    ///     then searches their associated directories for artist image files (e.g., artist.jpg).
    ///     Updates each artist's <see cref="Artist.LocalImageCachePath"/> and fires
    ///     <see cref="ArtistMetadataUpdated"/> when an image is found.
    /// </summary>
    /// <returns>The number of artists updated with newly found local images.</returns>
    private async Task<int> UpdateMissingArtistImagesFromFoldersAsync(Guid folderId, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        // Find distinct artists linked to songs in this folder.
        // Skip if a custom image or a previously-found local folder image is already cached.
        // We DO NOT skip if they only have a .fetched (external) image, as local files take precedence.
        var artistsNeedingImages = await context.Artists
            .Where(a => a.SongArtists.Any(sa => sa.Song.FolderId == folderId))
            .Where(a => a.LocalImageCachePath == null ||
                        (!a.LocalImageCachePath.Contains(".local.") && !a.LocalImageCachePath.Contains(".custom.")))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (artistsNeedingImages.Count == 0) return 0;

        _logger.LogDebug("Checking local artist images for {Count} artists in folder {FolderId}.",
            artistsNeedingImages.Count, folderId);

        // Batch-load song directories for all target artists in a single query (avoids N+1 round-trips).
        var artistIds = artistsNeedingImages.Select(a => a.Id).ToList();
        var songDirPairs = await context.SongArtists
            .AsNoTracking()
            .Where(sa => artistIds.Contains(sa.ArtistId))
            .Select(sa => new { sa.ArtistId, sa.Song.DirectoryPath })
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var songDirsByArtistId = songDirPairs
            .GroupBy(x => x.ArtistId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string?>)g.Select(x => x.DirectoryPath).ToList());

        // Cache processed directories to avoid redundant GetFiles calls when multiple artists share a directory.
        var directoryCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        // Collect events to fire after SaveChangesAsync succeeds, so UI state and DB stay in sync.
        var pendingEvents = new List<ArtistMetadataUpdatedEventArgs>();
        foreach (var artist in artistsNeedingImages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dirs = songDirsByArtistId.TryGetValue(artist.Id, out var d) ? d : Array.Empty<string?>();
            var localImageBytes = await FindArtistImageInDirectoriesAsync(dirs, artist.Name, cancellationToken, directoryCache).ConfigureAwait(false);
            if (localImageBytes == null) continue;

            try
            {
                var processed = await _imageProcessor.ProcessImageBytesAsync(localImageBytes).ConfigureAwait(false);
                var localPath = await ImageStorageHelper.SaveImageBytesAsync(_fileSystem,
                    _pathConfig.ArtistImageCachePath, artist.Id.ToString(), ".local", processed).ConfigureAwait(false);
                if (string.IsNullOrEmpty(localPath)) continue;

                artist.LocalImageCachePath = localPath;
                pendingEvents.Add(new ArtistMetadataUpdatedEventArgs(artist.Id, localPath));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process local artist image for '{ArtistName}'.", artist.Name);
            }
        }

        if (pendingEvents.Count > 0)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Updated {Count} artists with local folder images.", pendingEvents.Count);
            // Fire events only after the DB write succeeds to keep UI state consistent with persisted data.
            foreach (var eventArgs in pendingEvents)
                ArtistMetadataUpdated?.Invoke(this, eventArgs);
        }

        return pendingEvents.Count;
    }

    /// <summary>
    ///     Searches for a cover art file in the directory hierarchy, starting from the song's
    ///     directory and walking up to the base folder path.
    /// </summary>
    private string? FindCoverArtInDirectoryHierarchy(string songFilePath, string? baseFolderPath,
        Dictionary<string, string?> hierarchyDirCache)
    {
        try
        {
            var currentDirectory = _fileSystem.GetDirectoryName(songFilePath);
            if (string.IsNullOrEmpty(currentDirectory)) return null;

            // Normalize the base folder path for comparison
            var normalizedBasePath = baseFolderPath?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            while (!string.IsNullOrEmpty(currentDirectory))
            {
                if (!hierarchyDirCache.TryGetValue(currentDirectory, out var coverArtPath))
                {
                    coverArtPath = ImageStorageHelper.FindCoverArtInDirectory(_fileSystem, currentDirectory, _logger);
                    hierarchyDirCache.Add(currentDirectory, coverArtPath);
                }

                if (coverArtPath != null) return coverArtPath;

                var normalizedCurrent = currentDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!string.IsNullOrEmpty(normalizedBasePath) &&
                    normalizedCurrent.Equals(normalizedBasePath, StringComparison.OrdinalIgnoreCase))
                {
                    // We've searched the base folder, stop here
                    break;
                }

                currentDirectory = _fileSystem.GetDirectoryName(currentDirectory);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while searching for cover art in directory hierarchy for '{SongFilePath}'.",
                songFilePath);
            return null;
        }
    }

    /// <summary>
    ///     Searches for an artist image file (e.g., artist.jpg) in a specific directory.
    /// </summary>
    private string? FindArtistImageInDirectory(string directory)
    {
        try
        {
            return _fileSystem.GetFiles(directory, "*.*").FirstOrDefault(filePath =>
                FileExtensions.ImageFileExtensions.Contains(_fileSystem.GetExtension(filePath)) &&
                _fileSystem.GetFileNameWithoutExtension(filePath)
                    .Equals(FileExtensions.ArtistImageFileName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate artist images in directory '{Directory}'.", directory);
            return null;
        }
    }

    /// <summary>
    ///     Searches for an artist image file in the folders associated with the artist's songs.
    ///     Queries the database for song directories, then delegates to
    ///     <see cref="FindArtistImageInDirectoriesAsync"/>. Used by single-artist code paths.
    /// </summary>
    private async Task<byte[]?> FindArtistImageInFoldersAsync(Artist artist, MusicDbContext context, CancellationToken ct)
    {
        var songDirectories = await context.Songs
            .AsNoTracking()
            .Where(s => s.SongArtists.Any(sa => sa.ArtistId == artist.Id))
            .Select(s => s.DirectoryPath)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return await FindArtistImageInDirectoriesAsync(songDirectories, artist.Name, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Core artist-image scanning logic. Takes a pre-loaded list of song directories and
    ///     searches them in two passes following Navidrome priority:
    ///     Pass A — parent directories ("artist folder", e.g. F:/Music/Daft Punk/);
    ///     Pass B — song directories themselves ("album folder", e.g. F:/Music/Daft Punk/Discovery/).
    ///     Root directories are excluded from pass A to avoid scanning drive roots.
    /// </summary>
    private Task<byte[]?> FindArtistImageInDirectoriesAsync(
        IReadOnlyList<string?> songDirectories, string artistName, CancellationToken ct, Dictionary<string, string?>? directoryCache = null)
    {
        var directories = songDirectories.OfType<string>().Where(dir => dir.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var parents = directories.Select(_fileSystem.GetDirectoryName).OfType<string>()
            .Where(parent => parent.Length > 0 && !string.IsNullOrEmpty(_fileSystem.GetDirectoryName(parent)));
        return ScanDirectoriesForArtistImageAsync(parents.Concat(directories).Distinct(StringComparer.OrdinalIgnoreCase),
            artistName, ct, directoryCache);
    }

    /// <summary>
    ///     Helper method to scan a set of directories for a valid artist image file.
    /// </summary>
    private async Task<byte[]?> ScanDirectoriesForArtistImageAsync(IEnumerable<string> directories, string artistName, CancellationToken ct, Dictionary<string, string?>? directoryCache = null)
    {
        foreach (var dir in directories)
        {
            ct.ThrowIfCancellationRequested();

            if (directoryCache == null || !directoryCache.TryGetValue(dir, out var imagePath))
            {
                imagePath = FindArtistImageInDirectory(dir);
                if (directoryCache != null)
                {
                    directoryCache[dir] = imagePath;
                }
            }

            if (imagePath == null) continue;

            try
            {
                var bytes = await _fileSystem.ReadAllBytesAsync(imagePath).ConfigureAwait(false);
                if (bytes.Length > 0)
                {
                    _logger.LogDebug("Found artist image for '{ArtistName}' at '{ImagePath}'.", artistName, imagePath);
                    return bytes;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read artist image at '{ImagePath}'.", imagePath);
            }
        }
        return null;
    }

    /// <summary>
    ///     Extracts metadata from files and writes to a Channel for streaming consumption.
    ///     This allows the database batch writer to start processing immediately rather than
    ///     waiting for all files to be extracted first, reducing peak memory usage.
    ///     Uses scan-scoped caches for Artists, Albums, and Genres across sequential batches.
    /// </summary>
    private async Task<(int SavedCount, HashSet<string> DiscoveredDirectories)> ExtractAndSaveMetadataStreamingAsync(
        Guid folderId,
        List<string> filesToProcess,
        string baseFolderPath,
        IReadOnlySet<string> filePathsBeingUpdated,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken,
        bool skipMediaAssetsForUpdates = false)
    {
        const int channelCapacity = 100; // ~2 batches of buffer for backpressure
        var channel = Channel.CreateBounded<SongFileMetadata>(new BoundedChannelOptions(channelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        using var pipelineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pipelineToken = pipelineCts.Token;

        var totalFiles = filesToProcess.Count;
        var processedCount = 0;
        const int progressReportingBatchSize = 25;
        var discoveredDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        progress?.Report(new ScanProgress
        { StatusText = Resources.Strings.Status_PreparingScanCaches, Percentage = 0 });

        // Pre-load scan-scoped caches (names -> IDs) to prevent duplicate entity creation across batches
        var artistIdCache = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var albumIdCache = new Dictionary<(string Title, string Artist), Guid>();
        var genreIdCache = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        // Create the batch context early so it can be reused for both pre-warm queries and batch writes,
        // avoiding a second connection-open + 5-PRAGMA overhead.
        await using var batchContext = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        batchContext.ChangeTracker.AutoDetectChangesEnabled = false;

        {
            // Pre-load all existing artists, albums, and genres into caches
            var existingArtists = await batchContext.Artists.Select(a => new { a.Id, a.Name })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var artist in existingArtists)
            {
                artistIdCache.TryAdd(artist.Name, artist.Id);
            }

            var existingAlbumsWithArtist = await batchContext.Albums
                .AsNoTracking()
                .Select(a => new
                {
                    a.Id,
                    a.Title,
                    PrimaryArtistName = a.AlbumArtists
                        .Where(aa => aa.Order == 0)
                        .Select(aa => aa.Artist.Name)
                        .FirstOrDefault() ?? string.Empty
                })
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            foreach (var album in existingAlbumsWithArtist)
            {
                var albumKey = AlbumKey(album.Title, album.PrimaryArtistName);
                albumIdCache.TryAdd(albumKey, album.Id);
            }

            var existingGenres = await batchContext.Genres.Select(g => new { g.Id, g.Name })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var genre in existingGenres)
            {
                genreIdCache.TryAdd(genre.Name, genre.Id);
            }
        }

        _logger.LogDebug("Pre-loaded {ArtistCount} artists, {AlbumCount} albums, {GenreCount} genres into scan caches.",
            artistIdCache.Count, albumIdCache.Count, genreIdCache.Count);

        progress?.Report(new ScanProgress
        { StatusText = Resources.Strings.Status_ReadingSongDetails, Percentage = 0 });

        var totalSaved = 0;

        // Producer: Extract metadata concurrently and write to channel using Parallel.ForEachAsync
        // This limits both concurrency AND task object allocation (unlike Task.WhenAll which creates all tasks upfront)
        var isNetworkFolder = _fileSystem.IsNetworkPath(baseFolderPath);
        var failureSummary = new ConcurrentDictionary<string, int>();
        var producerTask = Task.Run(async () =>
        {
            try
            {
                // NAS/SMB serializes I/O per session; hammering it with ProcessorCount concurrent
                // opens increases timeouts far more than it speeds up extraction. Cap at 4 for
                // network paths; local disks keep the full CPU-count concurrency.
                var degreeOfParallelism = isNetworkFolder
                    ? Math.Min(4, Environment.ProcessorCount)
                    : Environment.ProcessorCount;

                await Parallel.ForEachAsync(
                    filesToProcess,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = degreeOfParallelism,
                        CancellationToken = pipelineToken
                    },
                    async (filePath, ct) =>
                    {
                        try
                        {
                            var includeMediaAssets = !skipMediaAssetsForUpdates || !filePathsBeingUpdated.Contains(filePath);
                            var extractionTask = includeMediaAssets
                                ? _metadataService.ExtractMetadataAsync(filePath, baseFolderPath)
                                : _metadataService.ExtractMetadataAsync(filePath, baseFolderPath, false);
                            var metadata = await extractionTask.WaitAsync(ct).ConfigureAwait(false);

                            // Retry once for transient failure classes. UnsupportedFormat / CorruptFile
                            // are permanent — no point retrying. Timeouts and file-access errors are
                            // often transient on network storage (brief SMB stall, renegotiation).
                            if (metadata.ExtractionFailed &&
                                (metadata.ErrorMessage == "ExtractionTimeout" || metadata.ErrorMessage == "FileAccessError"))
                            {
                                await Task.Delay(500, ct).ConfigureAwait(false);
                                extractionTask = includeMediaAssets
                                    ? _metadataService.ExtractMetadataAsync(filePath, baseFolderPath)
                                    : _metadataService.ExtractMetadataAsync(filePath, baseFolderPath, false);
                                metadata = await extractionTask.WaitAsync(ct).ConfigureAwait(false);
                            }

                            if (!metadata.ExtractionFailed)
                            {
                                await channel.Writer.WriteAsync(metadata, ct).ConfigureAwait(false);
                            }
                            else
                            {
                                var code = metadata.ErrorMessage ?? "Unknown";
                                failureSummary.AddOrUpdate(code, 1, (_, n) => n + 1);
                                _logger.LogDebug("Failed to extract metadata ({ErrorCode}) from file: {FilePath}", code, filePath);
                            }
                        }
                        finally
                        {
                            var currentCount = Interlocked.Increment(ref processedCount);

                            if (currentCount % progressReportingBatchSize == 0 || currentCount == totalFiles)
                            {
                                _logger.LogInformation("Producer progress: {Count}/{Total}", currentCount, totalFiles);
                                progress?.Report(new ScanProgress
                                {
                                    StatusText = Resources.Strings.Status_ReadingSongDetails,
                                    Percentage = (currentCount + Volatile.Read(ref totalSaved)) / (double)(totalFiles * 2) * 100,
                                });
                            }
                        }
                    }).ConfigureAwait(false);

                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
                throw;
            }
        }, pipelineToken);

        // Consumer: Batch and save to database as metadata arrives
        var consumerTask = Task.Run(async () =>
        {
            try
            {
                var batchNumber = 0;

                await foreach (var batch in channel.Reader.ReadAllAsync(pipelineToken).Chunk(50).ConfigureAwait(false))
                {
                    foreach (var metadata in batch)
                    {
                        var dir = _fileSystem.GetDirectoryName(metadata.FilePath);
                        if (dir != null) discoveredDirectories.Add(dir);
                    }
                    batchNumber++;
                    progress?.Report(new ScanProgress
                    {
                        StatusText = Resources.Strings.Status_ReadingSongDetails,
                        Percentage = (Volatile.Read(ref processedCount) + totalSaved) / (double)(totalFiles * 2) * 100,
                    });

                    var saved = await ProcessSingleBatchAsync(folderId, batch, batchContext, artistIdCache,
                        albumIdCache, genreIdCache, filePathsBeingUpdated, skipMediaAssetsForUpdates,
                        pipelineToken).ConfigureAwait(false);
                    _logger.LogInformation("Consumer finished batch {BatchNumber}. Saved {Count} items.", batchNumber, saved);
                    totalSaved += saved;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                pipelineCts.Cancel();
                channel.Writer.TryComplete(ex);
                _logger.LogError(ex, "Consumer failed in streaming metadata extraction for folder {FolderId}", folderId);
                throw;
            }
        }, pipelineToken);

        await Task.WhenAll(producerTask, consumerTask).ConfigureAwait(false);

        if (!failureSummary.IsEmpty)
        {
            var breakdown = string.Join(", ", failureSummary.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}"));
            _logger.LogWarning("Metadata extraction completed with {TotalFailures} failures ({Breakdown}). Individual paths logged at Debug level.",
                failureSummary.Values.Sum(), breakdown);
        }

        return (totalSaved, discoveredDirectories);
    }

    /// <summary>
    ///     Processes a batch of song metadata, using scan-scoped caches for Artists, Albums, and Genres.
    ///     The single channel reader owns these caches and writes batches sequentially.
    /// </summary>
    private async Task<int> ProcessSingleBatchAsync(
        Guid folderId,
        SongFileMetadata[] metadataList,
        MusicDbContext context,
        Dictionary<string, Guid> artistIdCache,
        Dictionary<(string Title, string Artist), Guid> albumIdCache,
        Dictionary<string, Guid> genreIdCache,
        IReadOnlySet<string> filePathsBeingUpdated,
        bool preserveMediaAssetsForUpdates,
        CancellationToken cancellationToken)
    {
        // Only load existing song records for files that are known updates (not new additions).
        // For InitialScan all files are new, so this avoids issuing 3 empty SQL queries per batch.
        var updatePaths = filePathsBeingUpdated.Count > 0
            ? metadataList.Select(m => m.FilePath).Where(filePathsBeingUpdated.Contains).ToList()
            : null;

        Dictionary<string, Song> existingSongs;
        if (updatePaths is null or { Count: 0 })
        {
            existingSongs = new Dictionary<string, Song>(StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            existingSongs = await context.Songs
                .Include(s => s.Genres)
                .Include(s => s.SongArtists)
                .Where(s => updatePaths.Contains(s.FilePath))
                .AsSplitQuery()
                .ToDictionaryAsync(s => s.FilePath, StringComparer.OrdinalIgnoreCase, cancellationToken).ConfigureAwait(false);
        }

        var artistNames = metadataList.SelectMany(m =>
            m.Artists.Select(ArtistNameHelper.Normalize)
                .Concat(m.AlbumArtists.Select(ArtistNameHelper.Normalize)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (metadataList.Any(m => !m.AlbumArtists.Any() || !m.Artists.Any()))
        {
            artistNames.Add(Artist.UnknownArtistName);
        }

        var genreNames = metadataList.SelectMany(m => m.Genres)
            .Select(g => NormalizeString(g))
            .Where(g => !string.IsNullOrEmpty(g))
            .Select(g => g!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var artistLookup = ResolveArtistsForBatch(context, artistNames, artistIdCache);
        var genreLookup = ResolveGenresForBatch(context, genreNames, genreIdCache);

        var albumDefinitions = BuildAlbumDefinitions(metadataList);
        var albumLookup = await ResolveAlbumsForBatchAsync(context, albumDefinitions, artistLookup, albumIdCache, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Processing single batch for folder {FolderId}. Metadata count: {Count}. First file: {First}, Last file: {Last}",
            folderId, metadataList.Length,
            Path.GetFileName(metadataList[0].FilePath),
            Path.GetFileName(metadataList[^1].FilePath));

        foreach (var metadata in metadataList)
        {
            existingSongs.TryGetValue(metadata.FilePath, out var existingSong);
            AddSongWithDetailsCached(context, folderId, metadata, artistLookup, albumLookup, genreLookup, existingSong,
                preserveMediaAssetsForUpdates && existingSong != null);
        }

        _logger.LogInformation("Saving changes for batch...");
        // With AutoDetectChangesEnabled=false, collection operations (Clear/Add on navigation properties)
        // may not be reflected in entity states until DetectChanges is called explicitly.
        context.ChangeTracker.DetectChanges();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        context.ChangeTracker.Clear();

        _logger.LogInformation("Batch saved successfully.");

        return metadataList.Length;
    }

    private static (string Title, string Artist) AlbumKey(string title, string artist) =>
        (title.ToUpperInvariant(), artist.ToUpperInvariant());

    private static Dictionary<(string Title, string Artist), (string Title, List<string> ArtistNames, int? Year)> BuildAlbumDefinitions(
        IEnumerable<SongFileMetadata> metadataList)
    {
        var albumDefinitions = new Dictionary<(string Title, string Artist), (string Title, List<string> ArtistNames, int? Year)>();

        foreach (var metadata in metadataList)
        {
            if (string.IsNullOrWhiteSpace(metadata.Album))
                continue;

            var albumTitle = NormalizeString(metadata.Album) ?? string.Empty;
            if (string.IsNullOrEmpty(albumTitle))
                continue;
            var albumArtistNames = metadata.AlbumArtists.Select(ArtistNameHelper.Normalize).ToList();
            var trackArtistNames = metadata.Artists.Select(ArtistNameHelper.Normalize).ToList();

            if (albumArtistNames.Count == 0)
            {
                albumArtistNames = trackArtistNames.Count > 0
                    ? new List<string>(trackArtistNames)
                    : new List<string> { Artist.UnknownArtistName };
            }

            var albumKey = AlbumKey(albumTitle, albumArtistNames[0]);
            if (albumDefinitions.TryGetValue(albumKey, out var existing))
            {
                if (existing.Year is null && metadata.Year.HasValue)
                {
                    albumDefinitions[albumKey] = (existing.Title, existing.ArtistNames, metadata.Year);
                }
                continue;
            }

            albumDefinitions[albumKey] = (albumTitle, albumArtistNames, metadata.Year);
        }

        return albumDefinitions;
    }

    private Dictionary<string, Artist> ResolveArtistsForBatch(
        MusicDbContext context,
        IReadOnlyCollection<string> artistNames,
        Dictionary<string, Guid> artistIdCache)
    {
        var artistLookup = new Dictionary<string, Artist>(StringComparer.OrdinalIgnoreCase);
        // The scan cache contains every existing artist; batches are written sequentially.
        foreach (var name in artistNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (artistIdCache.TryGetValue(name, out var id))
            {
                var artist = new Artist { Id = id, Name = name };
                context.Attach(artist);
                artistLookup.Add(name, artist);
            }
            else
            {
                var artist = new Artist { Name = name };
                context.Artists.Add(artist);
                artistLookup.Add(name, artist);
                artistIdCache.Add(name, artist.Id);
            }
        }

        return artistLookup;
    }

    private Dictionary<string, Genre> ResolveGenresForBatch(
        MusicDbContext context,
        IReadOnlyCollection<string> genreNames,
        Dictionary<string, Guid> genreIdCache)
    {
        var genreLookup = new Dictionary<string, Genre>(StringComparer.OrdinalIgnoreCase);
        var trackedGenres = context.ChangeTracker.Entries<Genre>()
            .Select(entry => entry.Entity)
            .ToDictionary(genre => genre.Id);

        // Reuse genres loaded with existing songs before attaching stubs.
        foreach (var name in genreNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Genre genre;
            if (genreIdCache.TryGetValue(name, out var id))
            {
                if (!trackedGenres.TryGetValue(id, out genre!))
                {
                    genre = new Genre { Id = id, Name = name };
                    context.Attach(genre);
                }
            }
            else
            {
                genre = new Genre { Name = name };
                context.Genres.Add(genre);
                genreIdCache.Add(name, genre.Id);
            }
            genreLookup.Add(name, genre);
        }

        return genreLookup;
    }

    /// <summary>
    ///     Normalizes a string using shared normalization logic from ArtistNameHelper.
    ///     Returns null if the result is empty.
    /// </summary>
    private static string? NormalizeString(string? s) => ArtistNameHelper.NormalizeStringCore(s);

    private async Task<Dictionary<(string Title, string Artist), Album>> ResolveAlbumsForBatchAsync(
        MusicDbContext context,
        IReadOnlyDictionary<(string Title, string Artist), (string Title, List<string> ArtistNames, int? Year)> albumDefinitions,
        Dictionary<string, Artist> artistLookup,
        Dictionary<(string Title, string Artist), Guid> albumIdCache,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("ResolveAlbumsForBatchAsync entered. Definition count: {Count}", albumDefinitions.Count);
        var albumLookup = new Dictionary<(string Title, string Artist), Album>();

        if (albumDefinitions.Count == 0)
            return albumLookup;

        // Step 1: Check the case-insensitive cache first to partition into known vs unknown
        var cachedEntries = new List<(Guid Id, (string Title, string Artist) Key)>();
        var uncachedKeys = new List<(string Title, string Artist)>();

        foreach (var albumKey in albumDefinitions.Keys)
        {
            if (albumIdCache.TryGetValue(albumKey, out var id))
                cachedEntries.Add((id, albumKey));
            else
                uncachedKeys.Add(albumKey);
        }

        if (cachedEntries.Count > 0)
        {
            var cachedIds = cachedEntries.Select(e => e.Id).ToList();
            var loadedAlbums = await context.Albums
                .Include(a => a.AlbumArtists).ThenInclude(aa => aa.Artist)
                .Where(a => cachedIds.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id, cancellationToken).ConfigureAwait(false);

            foreach (var (albumId, albumKey) in cachedEntries)
            {
                var album = loadedAlbums[albumId];
                var definition = albumDefinitions[albumKey];
                albumLookup.Add(albumKey, album);
                SyncAlbumArtists(album, definition.ArtistNames, artistLookup);
                if (album.Year is null && definition.Year.HasValue)
                {
                    album.Year = definition.Year;
                    context.Entry(album).Property(a => a.Year).IsModified = true;
                }
            }
        }

        // Step 3: Create new albums directly — no DB double-check needed.
        // Pre-warm loaded all existing albums into the cache; cache miss = genuinely new album.
        // Sequential consumer means no concurrent batch can create the same album.
        if (uncachedKeys.Count == 0)
            return albumLookup;

        _logger.LogInformation("Creating {Count} new albums.", uncachedKeys.Count);
        foreach (var albumKey in uncachedKeys)
        {
            var definition = albumDefinitions[albumKey];
            var newAlbum = new Album { Title = definition.Title, Year = definition.Year };
            for (int i = 0; i < definition.ArtistNames.Count; i++)
            {
                newAlbum.AlbumArtists.Add(new AlbumArtist { Artist = artistLookup[definition.ArtistNames[i]], Order = i });
            }
            context.Albums.Add(newAlbum);
            albumLookup[albumKey] = newAlbum;
            albumIdCache.TryAdd(albumKey, newAlbum.Id);
        }

        return albumLookup;
    }

    private static void SyncAlbumArtists(Album album, IReadOnlyList<string> artistNames, Dictionary<string, Artist> artistLookup)
    {
        var currentArtistNames = album.AlbumArtists
            .OrderBy(aa => aa.Order)
            .Select(aa => aa.Artist.Name);

        if (currentArtistNames.SequenceEqual(artistNames, StringComparer.OrdinalIgnoreCase))
            return;

        album.AlbumArtists.Clear();
        for (int i = 0; i < artistNames.Count; i++)
        {
            album.AlbumArtists.Add(new AlbumArtist
            {
                Artist = artistLookup[artistNames[i]],
                Order = i
            });
        }
    }

    /// <summary>
    ///     Adds or updates a song using scan-scoped caches for artists, albums, and genres.
    /// </summary>
    private void AddSongWithDetailsCached(
        MusicDbContext context,
        Guid folderId,
        SongFileMetadata metadata,
        Dictionary<string, Artist> artistLookup,
        Dictionary<(string Title, string Artist), Album> albumLookup,
        Dictionary<string, Genre> genreLookup,
        Song? existingSong = null,
        bool preserveMediaAssets = false)
    {
        var trackArtistNames = metadata.Artists.Select(ArtistNameHelper.Normalize).ToList();
        var albumArtistNames = metadata.AlbumArtists.Select(ArtistNameHelper.Normalize).ToList();

        if (albumArtistNames.Count == 0)
        {
            albumArtistNames = trackArtistNames.Count > 0
                ? new List<string>(trackArtistNames)
                : new List<string> { Artist.UnknownArtistName };
        }

        Album? album = null;
        if (!string.IsNullOrWhiteSpace(metadata.Album))
        {
            var albumTitle = NormalizeString(metadata.Album) ?? string.Empty;
            var albumKey = AlbumKey(albumTitle, albumArtistNames[0]);
            albumLookup.TryGetValue(albumKey, out album);

        }

        var genres = metadata.Genres
            .Select(NormalizeString)
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => genreLookup[name!])
            .ToList();

        var directoryPath = _fileSystem.GetDirectoryName(metadata.FilePath) ?? string.Empty;

        var song = existingSong ?? new Song();

        song.FilePath = metadata.FilePath;
        song.DirectoryPath = directoryPath;
        song.Title = metadata.Title;
        song.DurationTicks = metadata.Duration.Ticks;
        if (!preserveMediaAssets)
        {
            song.AlbumArtUriFromTrack = metadata.CoverArtUri;
            song.LightSwatchId = metadata.LightSwatchId;
            song.DarkSwatchId = metadata.DarkSwatchId;
            song.LrcFilePath = metadata.LrcFilePath;
        }
        song.Year = metadata.Year;
        song.TrackNumber = metadata.TrackNumber;
        song.TrackCount = metadata.TrackCount;
        song.DiscNumber = metadata.DiscNumber;
        song.DiscCount = metadata.DiscCount;
        song.SampleRate = metadata.SampleRate;
        song.Bitrate = metadata.Bitrate;
        song.Channels = metadata.Channels;
        song.FileCreatedDate = metadata.FileCreatedDate;
        song.FileModifiedDate = metadata.FileModifiedDate;
        song.FolderId = folderId;
        song.Composer = metadata.Composer;
        song.Bpm = metadata.Bpm;
        song.Lyrics = metadata.Lyrics;

        song.Album = album;
        song.AlbumId = album?.Id;

        var newSongArtists = trackArtistNames
            .Select((name, index) => new SongArtist { Artist = artistLookup[name], Order = index })
            .ToList();
        if (!song.SongArtists.OrderBy(sa => sa.Order).Select(sa => (sa.ArtistId, sa.Order))
                .SequenceEqual(newSongArtists.Select(sa => (sa.Artist.Id, sa.Order))))
        {
            song.SongArtists.Clear();
            foreach (var songArtist in newSongArtists)
                song.SongArtists.Add(songArtist);
        }

        if (!song.Genres.SequenceEqual(genres))
        {
            song.Genres.Clear();
            foreach (var genre in genres)
                song.Genres.Add(genre);
        }

        song.Grouping = metadata.Grouping;
        song.Copyright = metadata.Copyright;
        song.Comment = metadata.Comment;
        song.Conductor = metadata.Conductor;
        song.MusicBrainzTrackId = metadata.MusicBrainzTrackId;
        song.MusicBrainzReleaseId = metadata.MusicBrainzReleaseId;
        song.ReplayGainTrackGain = metadata.ReplayGainTrackGain;
        song.ReplayGainTrackPeak = metadata.ReplayGainTrackPeak;

        if (existingSong == null)
        {
            song.DateAddedToLibrary = DateTime.UtcNow;
            context.Songs.Add(song);
        }

        if (album is not null && string.IsNullOrEmpty(album.CoverArtUri) && !string.IsNullOrEmpty(metadata.CoverArtUri))
        {
            album.CoverArtUri = metadata.CoverArtUri;
            // AutoDetectChangesEnabled=false: must explicitly mark the modified property.
            context.Entry(album).Property(a => a.CoverArtUri).IsModified = true;
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

    /// <inheritdoc />
    public async Task<bool> UpdateArtistImageAsync(Guid artistId, string localFilePath)
    {
        if (string.IsNullOrWhiteSpace(localFilePath) || !_fileSystem.FileExists(localFilePath)) return false;

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var artist = await context.Artists.FindAsync(artistId).ConfigureAwait(false);
        if (artist == null) return false;

        try
        {
            var cachePath = _pathConfig.ArtistImageCachePath;

            var originalBytes = await _fileSystem.ReadAllBytesAsync(localFilePath).ConfigureAwait(false);
            var processedBytes = await _imageProcessor.ProcessImageBytesAsync(originalBytes).ConfigureAwait(false);
            var newPath = await ImageStorageHelper.SaveImageBytesAsync(_fileSystem, cachePath,
                artistId.ToString(), ".custom", processedBytes).ConfigureAwait(false);
            if (newPath is null) return false;

            artist.LocalImageCachePath = newPath;
            await context.SaveChangesAsync().ConfigureAwait(false);

            ArtistMetadataUpdated?.Invoke(this, new ArtistMetadataUpdatedEventArgs(artist.Id, artist.LocalImageCachePath));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update custom image for artist {ArtistName} (Id: {ArtistId})", artist.Name, artistId);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> RemoveArtistImageAsync(Guid artistId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var artist = await context.Artists.FindAsync(artistId).ConfigureAwait(false);
        if (artist == null) return false;

        try
        {
            var cachePath = _pathConfig.ArtistImageCachePath;

            ImageStorageHelper.DeleteImage(_fileSystem, cachePath, artistId.ToString(), ".custom");
            artist.LocalImageCachePath = ImageStorageHelper.FindImage(_fileSystem, cachePath, artistId.ToString(), ".local")
                                         ?? ImageStorageHelper.FindImage(_fileSystem, cachePath, artistId.ToString(), ".fetched");

            await context.SaveChangesAsync().ConfigureAwait(false);
            ArtistMetadataUpdated?.Invoke(this, new ArtistMetadataUpdatedEventArgs(artist.Id, artist.LocalImageCachePath));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove image for artist {ArtistName} (Id: {ArtistId})", artist.Name, artistId);
            return false;
        }
    }

    /// <summary>
    ///     Fetches metadata from remote sources and updates availability status.
    /// </summary>
    private async Task<(bool Updated, string? NewImagePath)> FetchAndUpdateArtistFromRemoteAsync(
        MusicDbContext context,
        Artist artist,
        CancellationToken cancellationToken = default,
        bool saveChanges = true,
        bool suppressEvents = false,
        IReadOnlyList<ServiceProviderSetting>? preloadedEnabledProviders = null,
        IReadOnlyList<string?>? preloadedSongDirectories = null)
    {
        var wasMetadataFoundAndUpdated = false;

        // Navidrome priority: local folder images first (artist.*, album/artist.*), then external providers.
        // Skip if a custom image or a previously-found local folder image is already cached.
        // We DO NOT skip if they only have a .fetched (external) image, as local files take precedence.
        if (artist.LocalImageCachePath == null ||
            (!artist.LocalImageCachePath.Contains(".custom.") && !artist.LocalImageCachePath.Contains(".local.")))
        {
            var localImageBytes = preloadedSongDirectories is not null
                ? await FindArtistImageInDirectoriesAsync(preloadedSongDirectories, artist.Name, cancellationToken).ConfigureAwait(false)
                : await FindArtistImageInFoldersAsync(artist, context, cancellationToken).ConfigureAwait(false);
            if (localImageBytes != null)
            {
                try
                {
                    var processed = await _imageProcessor.ProcessImageBytesAsync(localImageBytes).ConfigureAwait(false);
                    var localPath = await ImageStorageHelper.SaveImageBytesAsync(_fileSystem,
                        _pathConfig.ArtistImageCachePath, artist.Id.ToString(), ".local", processed).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(localPath))
                    {
                        artist.LocalImageCachePath = localPath;
                        wasMetadataFoundAndUpdated = true;
                        _logger.LogInformation("Using local folder image for artist '{ArtistName}'.", artist.Name);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process local artist image for '{ArtistName}'.", artist.Name);
                }
            }
        }

        // Get enabled metadata providers in priority order. Callers in batch paths preload this
        // once for the whole run to avoid repeated settings-store reads + list rebuilds per artist.
        var enabledProviders = preloadedEnabledProviders
            ?? await _settingsService.GetEnabledServiceProvidersAsync(Models.ServiceCategory.Metadata).ConfigureAwait(false);

        if (enabledProviders.Count == 0)
        {
            _logger.LogDebug("No metadata providers enabled. Skipping remote fetch for artist '{ArtistName}'.", artist.Name);
            if (wasMetadataFoundAndUpdated && saveChanges)
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (!suppressEvents && wasMetadataFoundAndUpdated)
                ArtistMetadataUpdated?.Invoke(this, new ArtistMetadataUpdatedEventArgs(artist.Id, artist.LocalImageCachePath));
            return (wasMetadataFoundAndUpdated, artist.LocalImageCachePath);
        }

        _logger.LogDebug("Using metadata providers for '{ArtistName}': {Providers}",
            artist.Name, string.Join(", ", enabledProviders.Select(p => p.Id)));

        var enabledIds = enabledProviders.Select(p => p.Id).ToHashSet();

        var tasks = new Dictionary<string, Task<(bool IsConclusive, string? ImageUrl, string? Biography)>>();

        var mbid = artist.MusicBrainzId;
        Task<ServiceResult<string>>? musicBrainzTask = null;
        if (string.IsNullOrEmpty(mbid) && enabledIds.Contains(ServiceProviderIds.MusicBrainz))
        {
            musicBrainzTask = _musicBrainzService.SearchArtistAsync(artist.Name, cancellationToken);
        }

        // Last.fm can start before MusicBrainz resolves the ID needed by other providers.
        if (enabledIds.Contains(ServiceProviderIds.LastFm))
            tasks[ServiceProviderIds.LastFm] = FetchFromLastFmAsync(artist.Name, cancellationToken);

        var anyProviderConclusive = false;
        var allProvidersConclusive = true;

        string? finalImageUrl = null;
        string? finalBiography = null;
        try
        {
            if (musicBrainzTask != null)
            {
                try
                {
                    var result = await musicBrainzTask.ConfigureAwait(false);
                    anyProviderConclusive = result.IsConclusive;
                    allProvidersConclusive = result.IsConclusive;
                    var resolvedMbid = result.Data;
                    if (!string.IsNullOrEmpty(resolvedMbid))
                    {
                        mbid = resolvedMbid;
                        artist.MusicBrainzId = mbid;
                        wasMetadataFoundAndUpdated = true;
                        _logger.LogInformation("Resolved MusicBrainz ID {MBID} for artist '{ArtistName}'", mbid, artist.Name);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    allProvidersConclusive = false;
                    _logger.LogDebug(ex, "MusicBrainz lookup failed for '{ArtistName}'.", artist.Name);
                }
            }

            if (!string.IsNullOrEmpty(mbid))
            {
                if (enabledIds.Contains(ServiceProviderIds.TheAudioDb))
                    tasks[ServiceProviderIds.TheAudioDb] = FetchFromTheAudioDbAsync(mbid, cancellationToken);
                if (enabledIds.Contains(ServiceProviderIds.FanartTv))
                    tasks[ServiceProviderIds.FanartTv] = FetchFromFanartTvAsync(mbid, cancellationToken);
            }

            foreach (var provider in enabledProviders)
            {
                if (!tasks.TryGetValue(provider.Id, out var task)) continue;

                try
                {
                    // Sequential await in priority order. If a higher priority task is still running,
                    // we wait for it. If it finishes and has data, we break early and ignore lower priority ones.
                    var (isConclusive, imageUrl, biography) = await task.ConfigureAwait(false);
                    anyProviderConclusive |= isConclusive;
                    allProvidersConclusive &= isConclusive;

                    if (string.IsNullOrEmpty(finalImageUrl) && !string.IsNullOrEmpty(imageUrl))
                    {
                        finalImageUrl = imageUrl;
                        _logger.LogDebug("Using image from {Provider} for artist '{ArtistName}'.", provider.DisplayName, artist.Name);
                    }

                    if (string.IsNullOrEmpty(finalBiography) && !string.IsNullOrEmpty(biography))
                    {
                        finalBiography = biography;
                        _logger.LogDebug("Using biography from {Provider} for artist '{ArtistName}'.", provider.DisplayName, artist.Name);
                    }

                    // Early exit if we have both primary values
                    if (!string.IsNullOrEmpty(finalImageUrl) && !string.IsNullOrEmpty(finalBiography))
                        break;
                }
                catch (OperationCanceledException)
                {
                    allProvidersConclusive = false;
                    if (cancellationToken.IsCancellationRequested) break;
                }
                catch (Exception ex)
                {
                    allProvidersConclusive = false;
                    _logger.LogDebug(ex, "Metadata provider {Provider} failed for '{ArtistName}'.", provider.DisplayName, artist.Name);
                }
            }
        }
        finally
        {
            // Observe unfinished providers even when cancellation interrupts the identity lookup.
            foreach (var task in tasks.Values)
            {
                _ = task.ContinueWith(
                    static (t, state) => ((ILogger<LibraryService>)state!).LogDebug(
                        t.Exception!.InnerException, "Metadata provider task faulted after lookup stopped waiting"),
                    _logger,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrEmpty(finalBiography))
        {
            artist.Biography = finalBiography;
            wasMetadataFoundAndUpdated = true;
        }

        // Download and cache the best image found from external providers.
        // Skip if a local folder image or custom image is already set.
        if (!string.IsNullOrEmpty(finalImageUrl) &&
            (artist.LocalImageCachePath == null || (!artist.LocalImageCachePath.Contains(".custom.") && !artist.LocalImageCachePath.Contains(".local."))))
        {
            var downloadedPath =
                await DownloadAndCacheArtistImageAsync(artist, new Uri(finalImageUrl), cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(downloadedPath))
            {
                artist.LocalImageCachePath = downloadedPath;
                wasMetadataFoundAndUpdated = true;
            }
            else
            {
                allProvidersConclusive = false;
            }
        }

        // Incomplete metadata remains retryable after a provider or image-download failure.
        var stampChecked = (anyProviderConclusive && allProvidersConclusive) ||
            (!string.IsNullOrWhiteSpace(artist.Biography) && !string.IsNullOrWhiteSpace(artist.LocalImageCachePath));
        if (stampChecked)
        {
            artist.MetadataLastCheckedUtc = DateTime.UtcNow;
        }
        else
        {
            _logger.LogDebug(
                "Metadata lookup incomplete for '{ArtistName}'; leaving MetadataLastCheckedUtc null for retry.",
                artist.Name);
        }

        if (saveChanges && (wasMetadataFoundAndUpdated || stampChecked))
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!suppressEvents && wasMetadataFoundAndUpdated)
        {
            ArtistMetadataUpdated?.Invoke(this, new ArtistMetadataUpdatedEventArgs(artist.Id, artist.LocalImageCachePath));
        }

        return (wasMetadataFoundAndUpdated, artist.LocalImageCachePath);
    }

    private async Task<(bool IsConclusive, string? ImageUrl, string? Biography)> FetchFromTheAudioDbAsync(string mbid, CancellationToken ct)
    {
        var result = await _theAudioDbService.GetArtistMetadataAsync(mbid, CultureInfo.CurrentCulture.TwoLetterISOLanguageName, ct).ConfigureAwait(false);
        if (result.Status == ServiceResultStatus.Success && result.Data is not null)
        {
            // Prefer square thumbnail over landscape fanart for round frame display
            var url = result.Data.ThumbUrl ?? result.Data.FanartUrl;
            return (true, url, result.Data.Biography);
        }
        return (result.IsConclusive, null, null);
    }

    private async Task<(bool IsConclusive, string? ImageUrl, string? Biography)> FetchFromFanartTvAsync(string mbid, CancellationToken ct)
    {
        var result = await _fanartTvService.GetArtistImagesAsync(mbid, ct).ConfigureAwait(false);
        if (result.Status == ServiceResultStatus.Success && result.Data is not null)
        {
            // Prefer square thumbnail over landscape background for round frame display
            var url = result.Data.ThumbUrl ?? result.Data.BackgroundUrl;
            return (true, url, null);
        }
        return (result.IsConclusive, null, null);
    }

    private async Task<(bool IsConclusive, string? ImageUrl, string? Biography)> FetchFromLastFmAsync(string artistName, CancellationToken ct)
    {
        var result = await _lastFmService.GetArtistInfoAsync(artistName, CultureInfo.CurrentCulture.TwoLetterISOLanguageName, ct).ConfigureAwait(false);
        if (result.Status == ServiceResultStatus.Success && result.Data is not null)
        {
            return (true, result.Data.ImageUrl, result.Data.Biography);
        }
        return (result.IsConclusive, null, null);
    }

    private async Task<string?> DownloadAndCacheArtistImageAsync(Artist artist, Uri imageUrl, CancellationToken cancellationToken)
    {
        var artistId = artist.Id;
        var lazyTask = _artistImageProcessingTasks.GetOrAdd(artistId, _ =>
            new Lazy<Task<string?>>(() => DownloadAndWriteImageInternalAsync(artistId, imageUrl, cancellationToken)));
        try
        {
            return await lazyTask.Value.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Artist image download failed for artist '{ArtistName}'.", artist.Name);
            return null;
        }
        finally
        {
            _artistImageProcessingTasks.TryRemove(new KeyValuePair<Guid, Lazy<Task<string?>>>(artistId, lazyTask));
        }
    }

    private async Task<string?> DownloadAndWriteImageInternalAsync(Guid artistId, Uri imageUrl, CancellationToken cancellationToken)
    {
        var localPath = _fileSystem.Combine(_pathConfig.ArtistImageCachePath, $"{artistId}.fetched.jpg");
        if (_fileSystem.FileExists(localPath)) return localPath;

        using var httpClient = _httpClientFactory.CreateClient("ImageDownloader");

        return await _pipelines.ExecuteWithFallbackAsync<string?>(
            ServiceProviderIds.ImageDownload,
            async ct =>
            {
                _logger.LogDebug("Downloading artist image: {ImageUrl}", imageUrl);
                return await httpClient.GetAsync(imageUrl, ct).ConfigureAwait(false);
            },
            async (response, ct) =>
            {
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("Image download returned {Status} for {ImageUrl}", response.StatusCode, imageUrl);
                    return null;
                }

                var imageBytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                // Process image to standardized size (600px max, preserves aspect ratio).
                // Offload heavy image processing to a background thread to prevent blocking.
                var processedBytes = await Task.Run(() => _imageProcessor.ProcessImageBytesAsync(imageBytes), ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                return await ImageStorageHelper.SaveImageBytesAsync(_fileSystem, _pathConfig.ArtistImageCachePath,
                    artistId.ToString(), ".fetched", processedBytes).ConfigureAwait(false);
            },
            fallback: null,
            _logger,
            $"image download {imageUrl}",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<Guid>> CleanUpOrphanedEntitiesAsync(MusicDbContext context,
        CancellationToken cancellationToken = default)
    {
        var songDirectories = await context.Songs.Select(s => s.DirectoryPath).Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var occupiedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in songDirectories)
        {
            var path = NormalizeDirectoryPath(directory);
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
            var ids = leaves.Where(f => !occupiedPaths.Contains(NormalizeDirectoryPath(f.Path)))
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

    private async Task<PagedResult<Song>> ReadSongPageAsync(IQueryable<Song> query, int pageNumber,
        int pageSize, SongSortOrder sortOrder, CancellationToken token)
    {
        var totalCount = await query.CountAsync(token).ConfigureAwait(false);
        var items = await ApplySongSortOrder(SongQuery.ExcludeHeavyFields(query.AsNoTracking()), sortOrder)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync(token).ConfigureAwait(false);
        return new PagedResult<Song>
        { Items = items, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    private static IQueryable<Song> FilterScopedSongs(IQueryable<Song> query, string searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm)) return query;
        var term = LikePatternHelper.CreateContainsPattern(NormalizeString(searchTerm) ?? string.Empty);
        return query.Where(s => EF.Functions.Like(s.Title, term, LikePatternHelper.EscapeCharacter)
                                || EF.Functions.Like(s.ArtistName, term, LikePatternHelper.EscapeCharacter)
                                || (s.Album != null && (EF.Functions.Like(s.Album.Title, term, LikePatternHelper.EscapeCharacter)
                                    || EF.Functions.Like(s.Album.ArtistName, term, LikePatternHelper.EscapeCharacter))));
    }

    private IOrderedQueryable<Album> ApplyAlbumSortOrder(IQueryable<Album> query, AlbumSortOrder sortOrder)
    {
        var ignoreArticles = _ignoreArticlesOnSort;
        Expression<Func<Album, string>> title = ignoreArticles ? a => a.SortTitle : a => a.Title;
        Expression<Func<Album, string>> artist = ignoreArticles ? a => a.PrimaryArtistSortName : a => a.PrimaryArtistName;
        return sortOrder switch
        {
            AlbumSortOrder.ArtistDesc => query.OrderByDescending(artist).ThenByDescending(title).ThenByDescending(a => a.Id),
            AlbumSortOrder.AlbumTitleAsc => query.OrderBy(title).ThenBy(a => a.Id),
            AlbumSortOrder.AlbumTitleDesc => query.OrderByDescending(title).ThenByDescending(a => a.Id),
            AlbumSortOrder.YearDesc => query.OrderByDescending(a => a.Year ?? 0).ThenByDescending(artist).ThenByDescending(title).ThenByDescending(a => a.Id),
            AlbumSortOrder.YearAsc => query.OrderBy(a => a.Year ?? int.MaxValue).ThenBy(artist).ThenBy(title).ThenBy(a => a.Id),
            AlbumSortOrder.SongCountDesc => query.OrderByDescending(a => a.Songs.Count).ThenByDescending(title).ThenByDescending(a => a.Id),
            AlbumSortOrder.SongCountAsc => query.OrderBy(a => a.Songs.Count).ThenBy(title).ThenBy(a => a.Id),
            _ => query.OrderBy(artist).ThenBy(title).ThenBy(a => a.Id)
        };
    }

    private IOrderedQueryable<Song> ApplySongSortOrder(IQueryable<Song> query, SongSortOrder sortOrder) =>
        SongQuery.Sort(query, sortOrder, _ignoreArticlesOnSort);

    private static IQueryable<PlaylistSong> FilterPlaylistSongs(IQueryable<PlaylistSong> query, string searchTerm)
    {
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = LikePatternHelper.CreateContainsPattern(NormalizeString(searchTerm) ?? string.Empty);
            query = query.Where(ps => (EF.Functions.Like(ps.Song.Title, term, LikePatternHelper.EscapeCharacter)
                                       || EF.Functions.Like(ps.Song.ArtistName, term, LikePatternHelper.EscapeCharacter)
                                       || ps.Song.SongArtists.Any(sa => EF.Functions.Like(sa.Artist.Name, term, LikePatternHelper.EscapeCharacter))
                                       || (ps.Song.Album != null && (EF.Functions.Like(ps.Song.Album.Title, term, LikePatternHelper.EscapeCharacter) || EF.Functions.Like(ps.Song.Album.ArtistName, term, LikePatternHelper.EscapeCharacter)))));
        }

        return query;
    }

    private IQueryable<Song> BuildSongSearchQuery(MusicDbContext context, string searchTerm)
    {
        // Leading wildcards prevent index usage; consider full-text search for large datasets
        var term = LikePatternHelper.CreateContainsPattern(searchTerm);
        return context.Songs
            .Where(s =>
                EF.Functions.Like(s.Title, term, LikePatternHelper.EscapeCharacter)
                || EF.Functions.Like(s.ArtistName, term, LikePatternHelper.EscapeCharacter)
                || s.SongArtists.Any(sa => EF.Functions.Like(sa.Artist.Name, term, LikePatternHelper.EscapeCharacter))
                || (s.Album != null && (EF.Functions.Like(s.Album.Title, term, LikePatternHelper.EscapeCharacter) || EF.Functions.Like(s.Album.ArtistName, term, LikePatternHelper.EscapeCharacter) || s.Album.AlbumArtists.Any(aa => EF.Functions.Like(aa.Artist.Name, term, LikePatternHelper.EscapeCharacter))))
                || (s.Year != null && EF.Functions.Like(s.Year.ToString(), term, LikePatternHelper.EscapeCharacter))
                || s.Genres.Any(g => EF.Functions.Like(g.Name, term, LikePatternHelper.EscapeCharacter))
            );
    }

    private IQueryable<Artist> BuildArtistSearchQuery(MusicDbContext context, string searchTerm)
    {
        var term = LikePatternHelper.CreateContainsPattern(searchTerm);
        return context.Artists.Where(a => EF.Functions.Like(a.Name, term, LikePatternHelper.EscapeCharacter));
    }

    private IQueryable<Album> BuildAlbumSearchQuery(MusicDbContext context, string searchTerm)
    {
        var term = LikePatternHelper.CreateContainsPattern(searchTerm);
        return context.Albums
            .Where(al => EF.Functions.Like(al.Title, term, LikePatternHelper.EscapeCharacter)
                         || EF.Functions.Like(al.ArtistName, term, LikePatternHelper.EscapeCharacter)
                         || al.AlbumArtists.Any(aa => EF.Functions.Like(aa.Artist.Name, term, LikePatternHelper.EscapeCharacter)));
    }

    private static void SanitizePaging(ref int pageNumber, ref int pageSize)
    {
        pageNumber = Math.Max(1, pageNumber);
        pageSize = Math.Max(1, pageSize);
    }

    #endregion

    private void OnFetchOnlineMetadataEnabledChanged(bool isEnabled)
    {
        if (!isEnabled) CancelMetadataFetch();
    }

    private void CancelMetadataFetch()
    {
        var runCts = _metadataFetchCts;
        try
        {
            runCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run finished and disposed its token source after we captured it.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0) return;
        _settingsService.FetchOnlineMetadataEnabledChanged -= OnFetchOnlineMetadataEnabledChanged;
        _settingsService.IgnoreLeadingArticlesOnSortEnabledChanged -= OnIgnoreLeadingArticlesOnSortChanged;
        _shutdownCts.Cancel();
        // These managed gates have no wait handles. Keep them usable while cancelled runs unwind.
        GC.SuppressFinalize(this);
    }

    #region Deduplication

    /// <summary>
    ///     Detects and collapses library rows that represent the same physical folder or file
    ///     under different path representations (e.g., mapped drive vs UNC, pre-canonicalization
    ///     differences), preserving playlist membership and listening history.
    ///     Returns the number of duplicate rows removed.
    /// </summary>
    public async Task<int> DeduplicateLibraryAsync(CancellationToken cancellationToken = default)
    {
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        cancellationToken = operationCts.Token;
        await _scanSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            var foldersCollapsed = await CollapseDuplicateFoldersAsync(context, cancellationToken).ConfigureAwait(false);
            var songsCollapsed = await CollapseDuplicateSongsAsync(context, cancellationToken).ConfigureAwait(false);

            var removed = foldersCollapsed + songsCollapsed;
            var orphanedArtists = removed > 0
                ? await CleanUpOrphanedEntitiesAsync(context, cancellationToken).ConfigureAwait(false)
                : [];
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            if (removed > 0)
            {
                DeleteOrphanedArtistImages(orphanedArtists);
                LibraryContentChanged?.Invoke(this, new LibraryContentChangedEventArgs(LibraryChangeType.LibraryRescanned));
                _logger.LogInformation("Library deduplication merged {FolderDupes} folder duplicates and {SongDupes} song duplicates.",
                    foldersCollapsed, songsCollapsed);
            }

            return removed;
        }
        finally
        {
            _scanSemaphore.Release();
        }
    }

    private async Task<int> CollapseDuplicateFoldersAsync(MusicDbContext context, CancellationToken ct)
    {
        var folders = await context.Folders.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var groups = folders
            .GroupBy(f => _fileSystem.NormalizePath(f.Path), StringComparer.OrdinalIgnoreCase);

        var removed = 0;
        foreach (var group in groups)
        {
            var survivor = group
                .OrderBy(f => f.ParentFolderId.HasValue)
                .ThenBy(f => f.LastModifiedDate ?? DateTime.MaxValue)
                .ThenBy(f => f.Id)
                .First();

            foreach (var dup in group.Where(f => f.Id != survivor.Id))
            {
                await context.Folders
                    .Where(f => f.ParentFolderId == dup.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(f => f.ParentFolderId, survivor.Id), ct)
                    .ConfigureAwait(false);

                await context.Songs
                    .Where(s => s.FolderId == dup.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.FolderId, survivor.Id), ct)
                    .ConfigureAwait(false);

                await context.ListenHistory
                    .Where(history => history.ContextType == PlaybackContextType.Folder && history.ContextId == dup.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(history => history.ContextId, survivor.Id), ct)
                    .ConfigureAwait(false);

                removed += await context.Folders.Where(f => f.Id == dup.Id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            }

            // Remove aliases before renaming: the canonical spelling may belong to a deleted row.
            if (survivor.Path != group.Key)
                await context.Folders.Where(f => f.Id == survivor.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(f => f.Path, group.Key), ct).ConfigureAwait(false);
        }

        return removed;
    }

    private async Task<int> CollapseDuplicateSongsAsync(MusicDbContext context, CancellationToken ct)
    {
        var songs = await context.Songs
            .AsNoTracking()
            .Select(s => new
            {
                s.Id, s.FilePath, s.PlayCount, s.SkipCount, s.TotalListenTimeTicks,
                s.IsLoved, s.Rating, s.DateAddedToLibrary, s.LastPlayedDate
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var removed = 0;
        foreach (var group in songs.GroupBy(s => _fileSystem.NormalizePath(s.FilePath), StringComparer.OrdinalIgnoreCase))
        {
            var survivor = group
                .OrderByDescending(s => s.PlayCount)
                .ThenByDescending(s => s.IsLoved)
                .ThenByDescending(s => s.DateAddedToLibrary ?? DateTime.MinValue)
                .First();
            var duplicateIds = group.Where(s => s.Id != survivor.Id).Select(s => s.Id).ToArray();
            if (duplicateIds.Length > 0)
            {
                var ids = group.Select(s => s.Id).ToArray();
                var playlistEntries = await context.PlaylistSongs.AsNoTracking()
                    .Where(entry => ids.Contains(entry.SongId)).ToListAsync(ct).ConfigureAwait(false);
                await context.PlaylistSongs.Where(entry => ids.Contains(entry.SongId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
                context.PlaylistSongs.AddRange(playlistEntries.GroupBy(entry => entry.PlaylistId)
                    .Select(playlist => new PlaylistSong
                    {
                        PlaylistId = playlist.Key, SongId = survivor.Id, Order = playlist.Min(entry => entry.Order)
                    }));
                await context.SaveChangesAsync(ct).ConfigureAwait(false);
                context.ChangeTracker.Clear();

                await context.ListenHistory.Where(history => duplicateIds.Contains(history.SongId))
                    .ExecuteUpdateAsync(setters => setters.SetProperty(history => history.SongId, survivor.Id), ct)
                    .ConfigureAwait(false);
                removed += await context.Songs.Where(s => duplicateIds.Contains(s.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            }

            if (duplicateIds.Length > 0 || survivor.FilePath != group.Key)
            {
                var directory = Path.GetDirectoryName(group.Key) ?? string.Empty;
                var playCount = group.Sum(s => s.PlayCount);
                var skipCount = group.Sum(s => s.SkipCount);
                var listenTicks = group.Sum(s => s.TotalListenTimeTicks);
                var loved = group.Any(s => s.IsLoved);
                var rating = group.Max(s => s.Rating);
                var added = group.Min(s => s.DateAddedToLibrary);
                var lastPlayed = group.Max(s => s.LastPlayedDate);
                await context.Songs.Where(s => s.Id == survivor.Id).ExecuteUpdateAsync(setters => setters
                    .SetProperty(s => s.FilePath, group.Key)
                    .SetProperty(s => s.DirectoryPath, directory)
                    .SetProperty(s => s.PlayCount, playCount)
                    .SetProperty(s => s.SkipCount, skipCount)
                    .SetProperty(s => s.TotalListenTimeTicks, listenTicks)
                    .SetProperty(s => s.IsLoved, loved)
                    .SetProperty(s => s.Rating, rating)
                    .SetProperty(s => s.DateAddedToLibrary, added)
                    .SetProperty(s => s.LastPlayedDate, lastPlayed), ct).ConfigureAwait(false);
            }
        }

        return removed;
    }

    #endregion
}
