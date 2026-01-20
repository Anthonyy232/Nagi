using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nagi.Core.Constants;
using Nagi.Core.Data;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Http;
using System.IO;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Manages all aspects of the music library, including file scanning, metadata, and database operations.
///     This service is designed to be a singleton and is internally thread-safe.
/// </summary>
public class LibraryService : ILibraryService, ILibraryReader, IDisposable
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
    private readonly SemaphoreSlim _artistCreationLock = new(1, 1);
    private readonly SemaphoreSlim _albumCreationLock = new(1, 1);
    private readonly IMetadataService _metadataService;
    private readonly IPathConfiguration _pathConfig;
    private readonly SemaphoreSlim _scanSemaphore = new(1, 1);
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ISpotifyService _spotifyService;
    private readonly ISettingsService _settingsService;
    private readonly IReplayGainService _replayGainService;
    private readonly IApiKeyService _apiKeyService;
    private readonly IImageProcessor _imageProcessor;
    private bool _disposed;
    private volatile bool _isMetadataFetchRunning;
    private volatile bool _isBatchScanning; // Prevents ReplayGain trigger during batch operations
    private CancellationTokenSource _metadataFetchCts;
    private CancellationTokenSource? _replayGainScanCts;

    public LibraryService(
        IDbContextFactory<MusicDbContext> contextFactory,
        IFileSystemService fileSystem,
        IMetadataService metadataService,
        ILastFmMetadataService lastFmService,
        ISpotifyService spotifyService,
        IMusicBrainzService musicBrainzService,
        IFanartTvService fanartTvService,
        ITheAudioDbService theAudioDbService,
        IHttpClientFactory httpClientFactory,
        IServiceScopeFactory serviceScopeFactory,
        IPathConfiguration pathConfig,
        ISettingsService settingsService,
        IReplayGainService replayGainService,
        IApiKeyService apiKeyService,
        IImageProcessor imageProcessor,
        ILogger<LibraryService> logger)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _lastFmService = lastFmService ?? throw new ArgumentNullException(nameof(lastFmService));
        _spotifyService = spotifyService ?? throw new ArgumentNullException(nameof(spotifyService));
        _musicBrainzService = musicBrainzService ?? throw new ArgumentNullException(nameof(musicBrainzService));
        _fanartTvService = fanartTvService ?? throw new ArgumentNullException(nameof(fanartTvService));
        _theAudioDbService = theAudioDbService ?? throw new ArgumentNullException(nameof(theAudioDbService));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
        _pathConfig = pathConfig ?? throw new ArgumentNullException(nameof(pathConfig));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _replayGainService = replayGainService ?? throw new ArgumentNullException(nameof(replayGainService));
        _apiKeyService = apiKeyService ?? throw new ArgumentNullException(nameof(apiKeyService));
        _imageProcessor = imageProcessor ?? throw new ArgumentNullException(nameof(imageProcessor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metadataFetchCts = new CancellationTokenSource();
        _settingsService.FetchOnlineMetadataEnabledChanged += OnFetchOnlineMetadataEnabledChanged;
    }

    /// <summary>
    ///     Occurs when an artist's metadata (e.g., biography, image) has been successfully updated from a remote source.
    /// </summary>
    public event EventHandler<ArtistMetadataUpdatedEventArgs>? ArtistMetadataUpdated;

    /// <inheritdoc />
    public event EventHandler<PlaylistUpdatedEventArgs>? PlaylistUpdated;

    /// <inheritdoc />
    public event EventHandler<bool>? ScanCompleted;

    #region Data Reset

    /// <inheritdoc />
    public async Task ClearAllLibraryDataAsync()
    {
        _logger.LogInformation("Starting to clear all library data and cache files.");
        _metadataFetchCts.Cancel();
        await Task.Delay(250).ConfigureAwait(false);

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync().ConfigureAwait(false);

        try
        {
            await context.PlaylistSongs.ExecuteDeleteAsync().ConfigureAwait(false);
            await context.ListenHistory.ExecuteDeleteAsync().ConfigureAwait(false);
            await context.Songs.ExecuteDeleteAsync().ConfigureAwait(false);
            await context.Playlists.ExecuteDeleteAsync().ConfigureAwait(false);
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

        var albumArtPath = _pathConfig.AlbumArtCachePath;
        var artistImagePath = _pathConfig.ArtistImageCachePath;
        var lrcCachePath = _pathConfig.LrcCachePath;

        try
        {
            if (_fileSystem.DirectoryExists(albumArtPath)) _fileSystem.DeleteDirectory(albumArtPath, true);
            if (_fileSystem.DirectoryExists(artistImagePath)) _fileSystem.DeleteDirectory(artistImagePath, true);
            if (_fileSystem.DirectoryExists(lrcCachePath)) _fileSystem.DeleteDirectory(lrcCachePath, true);

            _fileSystem.CreateDirectory(albumArtPath);
            _fileSystem.CreateDirectory(artistImagePath);
            _fileSystem.CreateDirectory(lrcCachePath);
            _logger.LogInformation("Successfully cleared and recreated cache directories.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear and recreate cache directories during library reset.");
        }
    }

    #endregion

    #region Folder Management

    /// <inheritdoc />
    public async Task<Folder?> AddFolderAsync(string path, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var normalizedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var existingFolder = await context.Folders.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Path == normalizedPath).ConfigureAwait(false);
        if (existingFolder is not null)
            return existingFolder;

        var folder = new Folder
        {
            Path = normalizedPath,
            Name = name ?? _fileSystem.GetFileNameWithoutExtension(normalizedPath) ?? ""
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
        return folder;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveFolderAsync(Guid folderId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var folder = await context.Folders.FindAsync(folderId).ConfigureAwait(false);
        if (folder is null)
        {
            _logger.LogWarning("Could not remove folder: Folder with ID {FolderId} not found.", folderId);
            return false;
        }

        List<string> albumArtPathsToDelete;
        List<string> lrcPathsToDelete;

        await using var transaction = await context.Database.BeginTransactionAsync().ConfigureAwait(false);
        try
        {
            if (folder.ParentFolderId == null)
            {
                var songsInFolder = context.Songs.Where(s => s.FolderId == folderId);

                albumArtPathsToDelete = await songsInFolder
                    .Where(s => s.AlbumArtUriFromTrack != null)
                    .Select(s => s.AlbumArtUriFromTrack!)
                    .Distinct()
                    .ToListAsync().ConfigureAwait(false);

                lrcPathsToDelete = await songsInFolder
                    .Where(s => s.LrcFilePath != null)
                    .Select(s => s.LrcFilePath!)
                    .ToListAsync().ConfigureAwait(false);

                await songsInFolder.ExecuteDeleteAsync().ConfigureAwait(false);
            }
            else
            {
                albumArtPathsToDelete = new List<string>();
                lrcPathsToDelete = new List<string>();
            }

            context.Folders.Remove(folder);
            await context.SaveChangesAsync().ConfigureAwait(false);

            await CleanUpOrphanedEntitiesAsync(context).ConfigureAwait(false);

            await transaction.CommitAsync().ConfigureAwait(false);
            _logger.LogInformation("Removed folder '{FolderName}' and associated data.", folder.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Folder removal for ID {FolderId} failed and was rolled back.", folderId);
            await transaction.RollbackAsync().ConfigureAwait(false);
            return false;
        }

        foreach (var artPath in albumArtPathsToDelete)
            try
            {
                if (_fileSystem.FileExists(artPath)) _fileSystem.DeleteFile(artPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete album art file {AlbumArtPath} during folder removal.",
                    artPath);
            }

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

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateFolderAsync(Folder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        try
        {
            folder.LastModifiedDate = _fileSystem.GetLastWriteTimeUtc(folder.Path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not get LastWriteTimeUtc for folder {FolderPath}", folder.Path);
        }

        context.Folders.Update(folder);
        await context.SaveChangesAsync().ConfigureAwait(false);
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

        var normalizedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.Path == normalizedPath).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Folder>> GetAllFoldersAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Folders.AsNoTracking().OrderBy(f => f.Name).ThenBy(f => f.Path).ToListAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> GetSongCountForFolderAsync(Guid folderId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Songs.CountAsync(s => s.FolderId == folderId).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Folder>> GetRootFoldersAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Folders.AsNoTracking()
            .Where(f => f.ParentFolderId == null)
            .OrderBy(f => f.Name)
            .ThenBy(f => f.Path)
            .ThenBy(f => f.Id)
            .ToListAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Folder>> GetSubFoldersAsync(Guid parentFolderId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Folders.AsNoTracking()
            .Where(f => f.ParentFolderId == parentFolderId)
            .OrderBy(f => f.Name)
            .ThenBy(f => f.Path)
            .ThenBy(f => f.Id)
            .ToListAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Folder?> GetFolderByDirectoryPathAsync(Guid rootFolderId, string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath)) return null;

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var folder = await context.Folders.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Path == directoryPath).ConfigureAwait(false);

        if (folder != null) return folder;

        return null;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Song>> GetSongsInDirectoryAsync(Guid folderId, string directoryPath)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var normalizedPath = directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return await ExcludeHeavyFields(
            context.Songs.AsNoTracking()
                .Where(s => s.FolderId == folderId && s.DirectoryPath == normalizedPath)
                .Include(s => s.Album))
            .OrderBy(s => s.Album != null ? s.Album.Title : string.Empty)
            .ThenBy(s => s.TrackNumber)
            .ThenBy(s => s.Title)
            .AsSplitQuery()
            .ToListAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> GetSongCountInDirectoryAsync(Guid folderId, string directoryPath)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var normalizedPath = directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return await context.Songs.CountAsync(s => s.FolderId == folderId && s.DirectoryPath == normalizedPath).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Song>> GetSongsInDirectoryRecursiveAsync(Guid folderId, string directoryPath)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var normalizedPath = directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return await ExcludeHeavyFields(
            context.Songs.AsNoTracking()
                .Where(s => s.FolderId == folderId &&
                            (s.DirectoryPath == normalizedPath ||
                             s.DirectoryPath.StartsWith(normalizedPath + "\\") ||
                             s.DirectoryPath.StartsWith(normalizedPath + "/")))
                .Include(s => s.Album))
            .OrderBy(s => s.DirectoryPath)
            .ThenBy(s => s.Album != null ? s.Album.Title : string.Empty)
            .ThenBy(s => s.TrackNumber)
            .ThenBy(s => s.Title)
            .AsSplitQuery()
            .ToListAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> GetSubFolderCountAsync(Guid parentFolderId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Folders.CountAsync(f => f.ParentFolderId == parentFolderId).ConfigureAwait(false);
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
            progress?.Report(new ScanProgress { StatusText = "Failed to add folder.", Percentage = 100 });
            return;
        }

        await RescanFolderForMusicAsync(folder.Id, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> RescanFolderForMusicAsync(Guid folderId, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await RescanFolderForMusicAsync(folderId, false, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Rescans a specific folder for music files with optional force full scan.
    /// </summary>
    /// <param name="folderId">The unique identifier of the folder to rescan.</param>
    /// <param name="forceFullScan">If true, re-reads metadata for all files regardless of modification time.</param>
    /// <param name="progress">Optional progress reporter for scan status updates.</param>
    /// <param name="cancellationToken">Optional cancellation token to cancel the scan.</param>
    /// <returns>True if changes were made to the library; otherwise, false.</returns>
    public async Task<bool> RescanFolderForMusicAsync(Guid folderId, bool forceFullScan, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Wait to acquire the semaphore. If the operation is cancelled while waiting, it will throw.
            await _scanSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Scan for folder ID {FolderId} was cancelled before acquiring semaphore.", folderId);
            progress?.Report(new ScanProgress { StatusText = "Scan cancelled by user.", Percentage = 100 });
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
                    progress?.Report(new ScanProgress { StatusText = "Folder not found.", Percentage = 100 });
                    return false;
                }

                _logger.LogInformation("Scanning folder '{FolderName}'...", folder.Name);

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!_fileSystem.DirectoryExists(folder.Path))
                    {
                        _logger.LogWarning(
                            "Folder path '{FolderPath}' no longer exists. Removing folder {FolderId} from library.",
                            folder.Path, folder.Id);
                        progress?.Report(new ScanProgress
                            { StatusText = "Folder path no longer exists. Removing from library.", Percentage = 100 });
                        return await RemoveFolderAsync(folderId).ConfigureAwait(false);
                    }

                    progress?.Report(new ScanProgress
                        { StatusText = $"Analyzing '{folder.Name}'...", IsIndeterminate = true });
                    var (filesToAdd, filesToUpdate, filesRemovedFromDisk) =
                        await AnalyzeFolderChangesAsync(folderId, folder.Path, forceFullScan, cancellationToken).ConfigureAwait(false);

                    if (filesToAdd.Any() || filesToUpdate.Any() || filesRemovedFromDisk.Any())
                        _logger.LogInformation("Changes detected: +{New} ~{Updated} -{Removed}",
                            filesToAdd.Count, filesToUpdate.Count, filesRemovedFromDisk.Count);

                    cancellationToken.ThrowIfCancellationRequested();

                    var allFilePathsToDelete = filesRemovedFromDisk.Distinct().ToList();

                    if (allFilePathsToDelete.Any())
                    {
                        progress?.Report(new ScanProgress
                            { StatusText = "Cleaning up your library...", IsIndeterminate = true });
                        await using var deleteContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

                        var songsToDeleteQuery = deleteContext.Songs
                            .Where(s => s.FolderId == folderId && allFilePathsToDelete.Contains(s.FilePath));

                        var lrcPathsToDelete = await songsToDeleteQuery
                            .Where(s => s.LrcFilePath != null)
                            .Select(s => s.LrcFilePath!)
                            .ToListAsync(cancellationToken).ConfigureAwait(false);

                        var albumArtPathsToDelete = await songsToDeleteQuery
                            .Where(s => s.AlbumArtUriFromTrack != null)
                            .Select(s => s.AlbumArtUriFromTrack!)
                            .Distinct()
                            .ToListAsync(cancellationToken).ConfigureAwait(false);

                        await songsToDeleteQuery.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

                        // Clean up associated files
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

                        foreach (var artPath in albumArtPathsToDelete)
                            try
                            {
                                if (_fileSystem.FileExists(artPath)) _fileSystem.DeleteFile(artPath);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex,
                                    "Failed to delete orphaned album art file {AlbumArtPath} during rescan.", artPath);
                            }
                    }

                    var filesToProcess = filesToAdd.Concat(filesToUpdate).ToList();

                    if (!filesToProcess.Any())
                    {
                        if (allFilePathsToDelete.Any())
                        {
                            progress?.Report(new ScanProgress
                                { StatusText = "Finalizing cleanup...", IsIndeterminate = true });
                            await using var cleanupContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
                            await CleanUpOrphanedEntitiesAsync(cleanupContext, cancellationToken).ConfigureAwait(false);
                        }

                        // Check for newly available LRC files for songs that don't currently have lyrics
                        var lrcUpdates = await UpdateMissingLrcPathsAsync(folderId, cancellationToken).ConfigureAwait(false);
                        
                        // Check for newly available cover art files for songs that don't have cover art
                        var coverArtUpdates = await UpdateMissingCoverArtAsync(folderId, folder.Path, cancellationToken).ConfigureAwait(false);
                        
                        var hasChanges = allFilePathsToDelete.Any() || lrcUpdates > 0 || coverArtUpdates > 0;

                        var updates = new List<string>();
                        if (lrcUpdates > 0) updates.Add($"{lrcUpdates} song(s) with lyrics");
                        if (coverArtUpdates > 0) updates.Add($"{coverArtUpdates} song(s) with cover art");
                        
                        var statusMessage = updates.Any()
                            ? $"Scan complete. Updated {string.Join(" and ", updates)}."
                            : "Scan complete. Library is up to date.";
                        
                        // Ensure subfolder hierarchy exists for existing songs.
                        // This handles the case where songs existed before subfolder records were implemented.
                        await using (var subfolderContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false))
                        {
                            var allDirectoryPaths = await subfolderContext.Songs
                                .AsNoTracking()
                                .Where(s => s.FolderId == folderId)
                                .Select(s => s.DirectoryPath)
                                .Distinct()
                                .ToListAsync(cancellationToken).ConfigureAwait(false);
                            
                            if (allDirectoryPaths.Count > 0)
                            {
                                var discoveredDirectories = allDirectoryPaths
                                    .Where(d => !string.IsNullOrEmpty(d))
                                    .ToHashSet(StringComparer.OrdinalIgnoreCase)!;
                                
                                await EnsureSubFoldersExistAsync(folderId, folder.Path, discoveredDirectories!, cancellationToken).ConfigureAwait(false);
                            }
                        }
                        
                        progress?.Report(new ScanProgress
                            { StatusText = statusMessage, Percentage = 100 });
                        return hasChanges;
                    }

                    // Use streaming extraction for better memory efficiency
                    var newSongsFound = await ExtractAndSaveMetadataStreamingAsync(
                        folderId, filesToProcess, folder.Path, progress, cancellationToken).ConfigureAwait(false);

                    cancellationToken.ThrowIfCancellationRequested();

                    // Check for newly available LRC files for songs that don't currently have lyrics
                    await UpdateMissingLrcPathsAsync(folderId, cancellationToken).ConfigureAwait(false);
                    
                    // Check for newly available cover art files for songs that don't have cover art
                    await UpdateMissingCoverArtAsync(folderId, folder.Path, cancellationToken).ConfigureAwait(false);

                    progress?.Report(new ScanProgress { StatusText = "Finalizing...", IsIndeterminate = true });
                    
                    // Ensure subfolder hierarchy exists for ALL songs in this folder.
                    // This handles both newly added songs and existing songs from before this fix was added.
                    await using (var subfolderContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false))
                    {
                        var allDirectoryPaths = await subfolderContext.Songs
                            .AsNoTracking()
                            .Where(s => s.FolderId == folderId)
                            .Select(s => s.DirectoryPath)
                            .Distinct()
                            .ToListAsync(cancellationToken).ConfigureAwait(false);
                        
                        var discoveredDirectories = allDirectoryPaths
                            .Where(d => !string.IsNullOrEmpty(d))
                            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;
                        
                        await EnsureSubFoldersExistAsync(folderId, folder.Path, discoveredDirectories!, cancellationToken).ConfigureAwait(false);
                    }
                    
                    await using (var finalContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false))
                    {
                        await CleanUpOrphanedEntitiesAsync(finalContext, cancellationToken).ConfigureAwait(false);
                    }

                    // Trigger LOH compaction for large scans to release fragmented memory from image processing
                    if (newSongsFound > 100)
                    {
                        _logger.LogDebug("Triggering LOH compaction after adding {Count} songs.", newSongsFound);
                        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                    }

                    var pluralSong = newSongsFound == 1 ? "song" : "songs";
                    var summary = newSongsFound > 0
                        ? $"Scan complete. Added or updated {newSongsFound:N0} {pluralSong}."
                        : "Scan complete. Library is up to date.";
                    progress?.Report(new ScanProgress
                        { StatusText = summary, Percentage = 100, NewSongsFound = newSongsFound });
                    return true;
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("Scan for folder ID {FolderId} was cancelled.", folderId);
                    progress?.Report(new ScanProgress { StatusText = "Scan cancelled by user.", Percentage = 100 });
                    return false;
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex, "FATAL: Rescan for folder ID {FolderId} failed.", folderId);
                    progress?.Report(new ScanProgress
                        { StatusText = "An error occurred during the scan. Please check the logs.", Percentage = 100 });
                    return false;
                }
            }, cancellationToken);
            
            // Run ReplayGain analysis if enabled, but only for single folder scans
            // RefreshAllFoldersAsync sets _isBatchScanning=true and triggers once at the end
            if (scanResult && !_isBatchScanning && await _settingsService.GetVolumeNormalizationEnabledAsync().ConfigureAwait(false))
            {
                await RunReplayGainAnalysisAsync(progress, cancellationToken).ConfigureAwait(false);
            }

            return scanResult;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Scan for folder ID {FolderId} was cancelled during execution.", folderId);
            progress?.Report(new ScanProgress { StatusText = "Scan cancelled by user.", Percentage = 100 });
            return false;
        }
        finally
        {
            // CRITICAL: Release the semaphore so other scans can proceed.
            _scanSemaphore.Release();
        }
    }
    
    /// <summary>
    /// Runs ReplayGain analysis synchronously with progress reporting. Only starts if no analysis is currently running.
    /// </summary>
    private async Task RunReplayGainAnalysisAsync(IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        // Only start a new scan if one isn't currently active
        if (_replayGainScanCts != null && !_replayGainScanCts.IsCancellationRequested)
        {
            _logger.LogDebug("ReplayGain analysis is already running. Skipping trigger.");
            return;
        }
        
        _replayGainScanCts?.Dispose();
        _replayGainScanCts = new CancellationTokenSource();
        
        // Link to the parent cancellation token so cancelling the folder scan also cancels ReplayGain
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _replayGainScanCts.Token);
        
        _logger.LogInformation("Volume normalization is enabled. Starting ReplayGain analysis.");
        
        try 
        {
            await _replayGainService.ScanLibraryAsync(progress, linkedCts.Token).ConfigureAwait(false);
            _logger.LogInformation("ReplayGain analysis completed.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("ReplayGain analysis was cancelled.");
            throw; // Re-throw so caller knows it was cancelled
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReplayGain analysis failed.");
            progress?.Report(new ScanProgress { StatusText = "Volume normalization failed. Check logs for details.", Percentage = 100 });
        }
        finally
        {
            _replayGainScanCts?.Dispose();
            _replayGainScanCts = null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> RefreshAllFoldersAsync(IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await RefreshAllFoldersAsync(false, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<bool> ForceRescanMetadataAsync(IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return RefreshAllFoldersAsync(true, progress, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> RefreshAllFoldersAsync(bool forceFullScan, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var folders = (await GetRootFoldersAsync().ConfigureAwait(false)).ToList();
            var totalFolders = folders.Count;

            if (totalFolders == 0)
            {
                _logger.LogDebug("No folders found in the library to refresh.");
                progress?.Report(
                    new ScanProgress { StatusText = "No folders in the library to refresh.", Percentage = 100 });
                ScanCompleted?.Invoke(this, false);
                return false;
            }

            var foldersProcessed = 0;
            var anyChangesMade = false;

            // Set batch scanning mode to suppress individual ReplayGain runs
            _isBatchScanning = true;

            try
            {
                foreach (var folder in folders)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var newProgress = new Progress<ScanProgress>(p =>
                    {
                        // Scale progress: (foldersProcessed + p.Percentage/100) / totalFolders * 100
                        var totalPercentage = (foldersProcessed + p.Percentage / 100.0) / totalFolders * 100.0;
                        
                        // Allow individual steps to show meaningful status text (e.g. "Reading songs...")
                        // but prefix with folder info if useful
                        var status = totalFolders > 1
                            ? $"Scanning folder {foldersProcessed + 1} of {totalFolders}: {folder.Name} - {p.StatusText}"
                            : p.StatusText;
                            
                        progress?.Report(new ScanProgress
                        {
                            StatusText = status,
                            Percentage = totalPercentage,
                            CurrentFilePath = p.CurrentFilePath,
                            IsIndeterminate = p.IsIndeterminate,
                            NewSongsFound = p.NewSongsFound, // This might be cumulative or not, UI handles it
                            TotalFiles = p.TotalFiles 
                        });
                    });

                    var changes = await RescanFolderForMusicAsync(folder.Id, forceFullScan, newProgress, cancellationToken)
                        .ConfigureAwait(false);
                    if (changes) anyChangesMade = true;

                    foldersProcessed++;
                }
            }
            finally
            {
                // Always clear the batch flag
                _isBatchScanning = false;
            }

            // Run ReplayGain analysis ONCE after all folders are scanned (if enabled and changes were made)
            if (anyChangesMade && await _settingsService.GetVolumeNormalizationEnabledAsync().ConfigureAwait(false))
            {
                await RunReplayGainAnalysisAsync(progress, cancellationToken).ConfigureAwait(false);
            }

            progress?.Report(new ScanProgress { StatusText = "Library refresh complete.", Percentage = 100 });
            ScanCompleted?.Invoke(this, anyChangesMade);
            return anyChangesMade;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh all library folders.");
            ScanCompleted?.Invoke(this, false);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<Artist?> GetArtistDetailsAsync(Guid artistId, bool allowOnlineFetch, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var artist = await context.Artists.AsTracking().FirstOrDefaultAsync(a => a.Id == artistId).ConfigureAwait(false);

        if (artist is null) return null;

        var needsUpdate = string.IsNullOrWhiteSpace(artist.Biography) ||
                          string.IsNullOrWhiteSpace(artist.LocalImageCachePath);
        // Only fetch if never checked before. Once checked, respect the result.
        var neverChecked = artist.MetadataLastCheckedUtc == null;
        if (allowOnlineFetch && needsUpdate && neverChecked)
        {
            try
            {
                await FetchAndUpdateArtistFromRemoteAsync(context, artist, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch online metadata for artist '{ArtistName}'. Proceeding with local data.", artist.Name);
            }
        }

        return await context.Artists.AsNoTracking()
            .Include(a => a.AlbumArtists).ThenInclude(aa => aa.Album)
            .FirstOrDefaultAsync(a => a.Id == artistId).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task StartArtistMetadataBackgroundFetchAsync()
    {
        // Try to acquire semaphore without waiting - if already running, return immediately
        if (!await _metadataFetchSemaphore.WaitAsync(0).ConfigureAwait(false))
        {
            _logger.LogDebug("Artist metadata background fetch already running, skipping.");
            return;
        }

        try
        {
            // Recreate CTS if it was cancelled from a previous run
            if (_metadataFetchCts.IsCancellationRequested)
            {
                _metadataFetchCts.Dispose();
                _metadataFetchCts = new CancellationTokenSource();
            }

            _isMetadataFetchRunning = true;
            var token = _metadataFetchCts.Token;

            // Run the actual fetch work - note: we keep holding the semaphore during the entire operation
            try
            {
                const int batchSize = 50;
                while (!token.IsCancellationRequested)
                {
                    List<Guid> artistIdsToUpdate;
                    await using (var idContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false))
                    {
                        artistIdsToUpdate = await idContext.Artists
                            .AsNoTracking()
                            .Where(a => a.MetadataLastCheckedUtc == null)
                            .OrderBy(a => a.Name)
                            .Select(a => a.Id)
                            .Take(batchSize)
                            .ToListAsync(token).ConfigureAwait(false);
                    }

                    if (artistIdsToUpdate.Count == 0 || token.IsCancellationRequested) break;

                    using var scope = _serviceScopeFactory.CreateScope();
                    var scopedContextFactory =
                        scope.ServiceProvider.GetRequiredService<IDbContextFactory<MusicDbContext>>();
                    await using var batchContext = await scopedContextFactory.CreateDbContextAsync().ConfigureAwait(false);

                    var artistsInBatch = await batchContext.Artists.Where(a => artistIdsToUpdate.Contains(a.Id))
                        .ToListAsync(token).ConfigureAwait(false);
                    foreach (var artist in artistsInBatch)
                    {
                        if (token.IsCancellationRequested) break;
                        try
                        {
                            await FetchAndUpdateArtistFromRemoteAsync(batchContext, artist, token).ConfigureAwait(false);
                        }
                        catch (DbUpdateConcurrencyException)
                        {
                            _logger.LogWarning(
                                "Concurrency conflict for artist {ArtistId} during background fetch. Ignoring.",
                                artist.Id);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to update artist {ArtistId} in background.", artist.Id);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Artist metadata background fetch was cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in artist metadata background fetch.");
            }
        }
        finally
        {
            _isMetadataFetchRunning = false;
            _metadataFetchSemaphore.Release();
        }
    }

    #endregion

    #region Song Management

    /// <inheritdoc />
    public async Task<Song?> AddSongAsync(Song songData)
    {
        ArgumentNullException.ThrowIfNull(songData);
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var existingSong = await context.Songs.AsNoTracking().FirstOrDefaultAsync(s => s.FilePath == songData.FilePath).ConfigureAwait(false);
        if (existingSong is not null) return existingSong;

        context.Songs.Add(songData);
        await context.SaveChangesAsync().ConfigureAwait(false);
        return songData;
    }

    /// <inheritdoc />
    public async Task<Song?> AddSongWithDetailsAsync(Guid folderId, SongFileMetadata metadata)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var song = await AddSongWithDetailsAsync(context, folderId, metadata).ConfigureAwait(false);
        if (song is not null) await context.SaveChangesAsync().ConfigureAwait(false);
        return song;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveSongAsync(Guid songId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var song = await context.Songs.FindAsync(songId).ConfigureAwait(false);
        if (song is null) return false;

        var albumArtPathToDelete = song.AlbumArtUriFromTrack;
        var lrcPathToDelete = song.LrcFilePath;

        context.Songs.Remove(song);
        await context.SaveChangesAsync().ConfigureAwait(false);

        if (IsPathInLrcCache(lrcPathToDelete))
            try
            {
                _fileSystem.DeleteFile(lrcPathToDelete!);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete cached LRC file {LrcPath} for song {SongId}.", lrcPathToDelete,
                    songId);
            }

        if (!string.IsNullOrWhiteSpace(albumArtPathToDelete) && _fileSystem.FileExists(albumArtPathToDelete))
            try
            {
                _fileSystem.DeleteFile(albumArtPathToDelete);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete album art file {AlbumArtPath} for song {SongId}.",
                    albumArtPathToDelete, songId);
            }

        await CleanUpOrphanedEntitiesAsync(context).ConfigureAwait(false);
        return true;
    }

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
    public async Task<Song?> GetSongWithFullDataAsync(Guid songId)
    {
        // Currently identical to GetSongByIdAsync as it already includes all fields.
        // This provides an explicit API for full-data requirements.
        return await GetSongByIdAsync(songId).ConfigureAwait(false);
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
        if (songIds is null) return new Dictionary<Guid, Song>();
        var uniqueIds = songIds.Distinct().ToList();
        if (!uniqueIds.Any()) return new Dictionary<Guid, Song>();

        const int chunkSize = 500;
        var result = new Dictionary<Guid, Song>();

        for (var i = 0; i < uniqueIds.Count; i += chunkSize)
        {
            var count = Math.Min(chunkSize, uniqueIds.Count - i);
            var chunk = uniqueIds.GetRange(i, count);
            await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
            
            var query = context.Songs.AsNoTracking()
                .Where(s => chunk.Contains(s.Id))
                .Include(s => s.Album);

            var batch = await ExcludeHeavyFields(query)
                .AsSplitQuery()
                .ToListAsync().ConfigureAwait(false);

            foreach (var song in batch)
            {
                result[song.Id] = song;
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateSongAsync(Song songToUpdate)
    {
        ArgumentNullException.ThrowIfNull(songToUpdate);
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        context.Songs.Update(songToUpdate);
        await context.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Song>> GetAllSongsAsync(SongSortOrder sortOrder = SongSortOrder.TitleAsc)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = ExcludeHeavyFields(
            context.Songs.AsNoTracking()
                .Include(s => s.Album));

        return await ApplySongSortOrder(query, sortOrder).AsSplitQuery().ToListAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Song>> GetSongsByAlbumIdAsync(Guid albumId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await ExcludeHeavyFields(
            context.Songs.AsNoTracking()
                .Where(s => s.AlbumId == albumId)
                .Include(s => s.Album))
            .OrderBy(s => s.TrackNumber).ThenBy(s => s.Title).ThenBy(s => s.Id)
            .ToListAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Song>> GetSongsByArtistIdAsync(Guid artistId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await ExcludeHeavyFields(
            context.Songs.AsNoTracking()
                .Where(s => s.SongArtists.Any(sa => sa.ArtistId == artistId))
                .Include(s => s.Album))
            .OrderBy(s => s.Album != null ? s.Album.Title : string.Empty)
            .ThenBy(s => s.TrackNumber)
            .ThenBy(s => s.Id)
            .AsSplitQuery()
            .ToListAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Song>> GetSongsByFolderIdAsync(Guid folderId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await ExcludeHeavyFields(
            context.Songs.AsNoTracking()
                .Where(s => s.FolderId == folderId)
                .Include(s => s.Album))
            .OrderBy(s => s.Album != null ? s.Album.Title : string.Empty)
            .ThenBy(s => s.TrackNumber).ThenBy(s => s.Title)
            .AsSplitQuery()
            .ToListAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Song>> SearchSongsAsync(string searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetAllSongsAsync().ConfigureAwait(false);

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await ExcludeHeavyFields(BuildSongSearchQuery(context, searchTerm.Trim()))
            .OrderBy(s => s.Title).ThenBy(s => s.Id)
            .AsSplitQuery()
            .ToListAsync().ConfigureAwait(false);
    }

    #endregion

    #region Song Metadata Updates

    /// <inheritdoc />
    public Task<bool> SetSongRatingAsync(Guid songId, int? rating)
    {
        if (rating.HasValue && (rating < 1 || rating > 5))
            throw new ArgumentOutOfRangeException(nameof(rating), "Rating must be between 1 and 5.");

        return UpdateSongPropertyAsync(songId, s => s.Rating = rating);
    }

    /// <inheritdoc />
    public Task<bool> SetSongLovedStatusAsync(Guid songId, bool isLoved)
    {
        return UpdateSongPropertyAsync(songId, s => s.IsLoved = isLoved);
    }

    /// <inheritdoc />
    public Task<bool> UpdateSongLyricsAsync(Guid songId, string? lyrics)
    {
        return UpdateSongPropertyAsync(songId, s => s.Lyrics = lyrics);
    }

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
    public async Task<Artist?> GetArtistByIdAsync(Guid artistId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Artists.AsNoTracking().FirstOrDefaultAsync(a => a.Id == artistId).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Artist?> GetArtistByNameAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Artists.AsNoTracking().FirstOrDefaultAsync(a => a.Name == name.Trim()).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Artist>> GetAllArtistsAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Artists.AsNoTracking().OrderBy(a => a.Name).ThenBy(a => a.Id).ToListAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Artist>> SearchArtistsAsync(string searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetAllArtistsAsync().ConfigureAwait(false);

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await BuildArtistSearchQuery(context, searchTerm.Trim()).AsNoTracking().OrderBy(a => a.Name).ThenBy(a => a.Id)
            .ToListAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Album>> GetTopAlbumsForArtistAsync(Guid artistId, int limit)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        // Select logic: Order by number of songs in the album (or associated with the artist in that album)
        // Since AlbumArtists is M:N, we want albums where this artist participates.
        // We order by the total song count of the album (assuming popular albums have more songs/are main albums).
        // Alternatively, order by PlayCount sum if available. Song has PlayCount.
        
        return await context.AlbumArtists.AsNoTracking()
            .Where(aa => aa.ArtistId == artistId)
            .Select(aa => new 
            { 
                Album = aa.Album, 
                // Order by Popularity (PlayCount) first, then Song Count
                PlayCount = aa.Album!.Songs.Sum(s => s.PlayCount),
                SongCount = aa.Album!.Songs.Count 
            })
            .OrderByDescending(x => x.PlayCount)
            .ThenByDescending(x => x.SongCount)
            .Take(limit)
            .Select(x => x.Album)
            .ToListAsync().ConfigureAwait(false);
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
    public async Task<TimeSpan> GetAlbumTotalDurationAsync(Guid albumId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var ticks = await context.Songs
            .Where(s => s.AlbumId == albumId)
            .SumAsync(s => s.DurationTicks)
            .ConfigureAwait(false);
        return TimeSpan.FromTicks(ticks);
    }

    /// <inheritdoc />
    public async Task<TimeSpan> GetSearchTotalDurationInAlbumAsync(Guid albumId, string searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetAlbumTotalDurationAsync(albumId).ConfigureAwait(false);

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var ticks = await BuildSongSearchQuery(context, searchTerm.Trim())
            .Where(s => s.AlbumId == albumId)
            .SumAsync(s => s.DurationTicks)
            .ConfigureAwait(false);
        return TimeSpan.FromTicks(ticks);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Album>> GetAllAlbumsAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Albums.AsNoTracking()
            .Include(al => al.AlbumArtists).ThenInclude(aa => aa.Artist)
            .OrderBy(al => al.PrimaryArtistName)
            .ThenBy(al => al.Title)
            .ThenBy(al => al.Id)
            .AsSplitQuery()
            .ToListAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Album>> SearchAlbumsAsync(string searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetAllAlbumsAsync().ConfigureAwait(false);

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await BuildAlbumSearchQuery(context, searchTerm.Trim())
            .OrderBy(al => al.PrimaryArtistName)
            .ThenBy(al => al.Title)
            .ThenBy(al => al.Id)
            .AsSplitQuery()
            .ToListAsync().ConfigureAwait(false);
    }

    #endregion

    #region Playlist Management

    /// <inheritdoc />
    public async Task<Playlist?> CreatePlaylistAsync(string name, string? description = null,
        string? coverImageUri = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Playlist name cannot be empty.", nameof(name));

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var playlist = new Playlist
        {
            Name = name.Trim(),
            Description = description,
            DateCreated = DateTime.UtcNow,
            DateModified = DateTime.UtcNow
        };

        if (!string.IsNullOrEmpty(coverImageUri) && _fileSystem.FileExists(coverImageUri))
        {
            var cachePath = _pathConfig.PlaylistImageCachePath;
            var originalBytes = await _fileSystem.ReadAllBytesAsync(coverImageUri).ConfigureAwait(false);
            var processedBytes = await _imageProcessor.ProcessImageBytesAsync(originalBytes).ConfigureAwait(false);
            await ImageStorageHelper.SaveImageBytesAsync(_fileSystem, cachePath, playlist.Id.ToString(), ".custom", processedBytes).ConfigureAwait(false);
            playlist.CoverImageUri = ImageStorageHelper.FindImage(_fileSystem, cachePath, playlist.Id.ToString(), ".custom");
        }

        context.Playlists.Add(playlist);
        await context.SaveChangesAsync().ConfigureAwait(false);
        return playlist;
    }

    /// <inheritdoc />
    public async Task<bool> DeletePlaylistAsync(Guid playlistId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var rowsAffected = await context.Playlists.Where(p => p.Id == playlistId).ExecuteDeleteAsync().ConfigureAwait(false);
        return rowsAffected > 0;
    }

    /// <inheritdoc />
    public async Task<bool> RenamePlaylistAsync(Guid playlistId, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("New playlist name cannot be empty.", nameof(newName));

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var playlist = await context.Playlists.FindAsync(playlistId).ConfigureAwait(false);
        if (playlist is null) return false;

        playlist.Name = newName.Trim();
        playlist.DateModified = DateTime.UtcNow;
        await context.SaveChangesAsync().ConfigureAwait(false);
        PlaylistUpdated?.Invoke(this, new PlaylistUpdatedEventArgs(playlist.Id, playlist.CoverImageUri));
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> UpdatePlaylistCoverAsync(Guid playlistId, string? newCoverImageUri)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var playlist = await context.Playlists.FindAsync(playlistId).ConfigureAwait(false);
        if (playlist is null) return false;

        if (!string.IsNullOrEmpty(newCoverImageUri) && _fileSystem.FileExists(newCoverImageUri))
        {
            // Read, process, and save the image
            var cachePath = _pathConfig.PlaylistImageCachePath;
            var originalBytes = await _fileSystem.ReadAllBytesAsync(newCoverImageUri).ConfigureAwait(false);
            var processedBytes = await _imageProcessor.ProcessImageBytesAsync(originalBytes).ConfigureAwait(false);
            await ImageStorageHelper.SaveImageBytesAsync(_fileSystem, cachePath, playlistId.ToString(), ".custom", processedBytes).ConfigureAwait(false);
            
            var newPath = ImageStorageHelper.FindImage(_fileSystem, cachePath, playlistId.ToString(), ".custom");
            playlist.CoverImageUri = newPath;
        }
        else
        {
            // Remove custom image if setting to null/empty
            var cachePath = _pathConfig.PlaylistImageCachePath;
            ImageStorageHelper.DeleteImage(_fileSystem, cachePath, playlistId.ToString(), ".custom");
            playlist.CoverImageUri = null;
        }

        playlist.DateModified = DateTime.UtcNow;
        await context.SaveChangesAsync().ConfigureAwait(false);
        PlaylistUpdated?.Invoke(this, new PlaylistUpdatedEventArgs(playlist.Id, playlist.CoverImageUri));
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> AddSongsToPlaylistAsync(Guid playlistId, IEnumerable<Guid> songIds)
    {
        if (songIds is null || !songIds.Any()) return false;

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var playlist = await context.Playlists.FindAsync(playlistId).ConfigureAwait(false);
        if (playlist is null) return false;

        var existingSongIds = await context.PlaylistSongs
            .Where(ps => ps.PlaylistId == playlistId)
            .Select(ps => ps.SongId)
            .ToHashSetAsync().ConfigureAwait(false);

        var songIdsToAdd = songIds.Distinct().Except(existingSongIds).ToList();
        if (songIdsToAdd.Count == 0) return true;

        var maxOrder = await context.PlaylistSongs
            .Where(ps => ps.PlaylistId == playlistId)
            .Select(ps => ps.Order)
            .OrderByDescending(o => o)
            .FirstOrDefaultAsync().ConfigureAwait(false);

        var nextOrder = Math.Floor(maxOrder) + 1.0;

        var playlistSongsToAdd = songIdsToAdd.Select((songId, index) => new PlaylistSong
        {
            PlaylistId = playlistId,
            SongId = songId,
            Order = nextOrder + (double)index
        });

        context.PlaylistSongs.AddRange(playlistSongsToAdd);
        playlist.DateModified = DateTime.UtcNow;
        await context.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveSongsFromPlaylistAsync(Guid playlistId, IEnumerable<Guid> songIds)
    {
        if (songIds is null || !songIds.Any()) return false;

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var playlist = await context.Playlists.FindAsync(playlistId).ConfigureAwait(false);
        if (playlist is null) return false;

        await context.PlaylistSongs
            .Where(ps => ps.PlaylistId == playlistId && songIds.Contains(ps.SongId))
            .ExecuteDeleteAsync().ConfigureAwait(false);



        playlist.DateModified = DateTime.UtcNow;
        await context.SaveChangesAsync().ConfigureAwait(false);
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
                if (Math.Abs(playlistSong.Order - newOrder) > 1e-10)
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
            .FirstOrDefaultAsync(ps => ps.PlaylistId == playlistId && ps.SongId == songId)
            .ConfigureAwait(false);

        if (playlistSong is null) return false;

        playlistSong.Order = newOrder;
        _logger.LogDebug("Saving new order {Order} for song {SongId} in playlist {PlaylistId}", newOrder, songId, playlistId);
        
        var playlist = await context.Playlists.FindAsync(playlistId).ConfigureAwait(false);
        if (playlist != null) playlist.DateModified = DateTime.UtcNow;

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
    public async Task<IEnumerable<Playlist>> GetAllPlaylistsAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Playlists.AsNoTracking()
            .Include(p => p.PlaylistSongs)
            .OrderBy(p => p.Name).ThenBy(p => p.Id)
            .ToListAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Song>> GetSongsInPlaylistOrderedAsync(Guid playlistId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.PlaylistSongs.AsNoTracking()
            .Where(ps => ps.PlaylistId == playlistId)
            .Include(ps => ps.Song).ThenInclude(s => s!.Album)
            .Select(ps => ps.Song!);

        return await ApplySongSortOrder(ExcludeHeavyFields(query), SongSortOrder.TrackNumberAsc)
            .AsSplitQuery()
            .ToListAsync().ConfigureAwait(false);
    }

    #endregion

    #region Genre Management

    /// <inheritdoc />
    public async Task<IEnumerable<Genre>> GetAllGenresAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Genres.AsNoTracking()
            .Include(g => g.Songs)
            .OrderBy(g => g.Name).ThenBy(g => g.Id)
            .ToListAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Song>> GetSongsByGenreIdAsync(Guid genreId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await ExcludeHeavyFields(
            context.Songs.AsNoTracking()
                .Where(s => s.Genres.Any(g => g.Id == genreId))
                .Include(s => s.Album))
            .OrderBy(s => s.Title).ThenBy(s => s.Id)
            .AsSplitQuery()
            .ToListAsync().ConfigureAwait(false);
    }

    #endregion

    #region Listen History

    /// <inheritdoc />
    public async Task<long?> CreateListenHistoryEntryAsync(Guid songId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var song = await context.Songs.FindAsync(songId).ConfigureAwait(false);
        if (song is null) return null;

        song.PlayCount++;
        song.LastPlayedDate = DateTime.UtcNow;

        var historyEntry = new ListenHistory
            { SongId = songId, ListenTimestampUtc = DateTime.UtcNow, IsScrobbled = false };
        context.ListenHistory.Add(historyEntry);

        await context.SaveChangesAsync().ConfigureAwait(false);
        return historyEntry.Id;
    }

    /// <inheritdoc />
    public async Task<bool> MarkListenAsEligibleForScrobblingAsync(long listenHistoryId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var historyEntry = await context.ListenHistory.FindAsync(listenHistoryId).ConfigureAwait(false);
        if (historyEntry is null) return false;

        historyEntry.IsEligibleForScrobbling = true;
        await context.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> MarkListenAsScrobbledAsync(long listenHistoryId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var historyEntry = await context.ListenHistory.FindAsync(listenHistoryId).ConfigureAwait(false);
        if (historyEntry is null) return false;

        historyEntry.IsScrobbled = true;
        await context.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task LogSkipAsync(Guid songId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var song = await context.Songs.FindAsync(songId).ConfigureAwait(false);
        if (song is null) return;

        song.SkipCount++;
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> GetListenCountForSongAsync(Guid songId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.ListenHistory.CountAsync(lh => lh.SongId == songId).ConfigureAwait(false);
    }

    #endregion

    #region Paged Loading

    /// <inheritdoc />
    public async Task<PagedResult<Song>> GetAllSongsPagedAsync(int pageNumber, int pageSize,
        SongSortOrder sortOrder = SongSortOrder.TitleAsc)
    {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var baseQuery = context.Songs.AsNoTracking().Include(s => s.Album);
        var totalCount = await baseQuery.CountAsync().ConfigureAwait(false);
        var pagedData = await ApplySongSortOrder(ExcludeHeavyFields(baseQuery), sortOrder)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync().ConfigureAwait(false);

        return new PagedResult<Song>
            { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <inheritdoc />
    public async Task<PagedResult<Song>> SearchSongsPagedAsync(string searchTerm, int pageNumber, int pageSize)
    {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetAllSongsPagedAsync(pageNumber, pageSize).ConfigureAwait(false);

        var query = BuildSongSearchQuery(context, searchTerm.Trim());

        var totalCount = await query.CountAsync().ConfigureAwait(false);
        var pagedData = await ApplySongSortOrder(ExcludeHeavyFields(query), SongSortOrder.TitleAsc)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync().ConfigureAwait(false);

        return new PagedResult<Song>
            { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <inheritdoc />
    public async Task<PagedResult<Song>> GetSongsByAlbumIdPagedAsync(Guid albumId, int pageNumber, int pageSize,
        SongSortOrder sortOrder)
    {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var baseQuery = context.Songs.AsNoTracking().Where(s => s.AlbumId == albumId);
        var totalCount = await baseQuery.CountAsync().ConfigureAwait(false);
        var pagedData = await ApplySongSortOrder(ExcludeHeavyFields(baseQuery), sortOrder)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync().ConfigureAwait(false);

        return new PagedResult<Song>
            { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <inheritdoc />
    public async Task<PagedResult<Song>> GetSongsByArtistIdPagedAsync(Guid artistId, int pageNumber, int pageSize,
        SongSortOrder sortOrder)
    {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var baseQuery = context.Songs.AsNoTracking().Where(s => s.SongArtists.Any(sa => sa.ArtistId == artistId));
        var totalCount = await baseQuery.CountAsync().ConfigureAwait(false);
        var pagedData = await ApplySongSortOrder(ExcludeHeavyFields(baseQuery), sortOrder)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync().ConfigureAwait(false);

        return new PagedResult<Song>
            { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <inheritdoc />
    public async Task<PagedResult<Song>> GetSongsByGenreIdPagedAsync(Guid genreId, int pageNumber, int pageSize,
        SongSortOrder sortOrder)
    {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var baseQuery = context.Songs.AsNoTracking().Where(s => s.Genres.Any(g => g.Id == genreId));
        var totalCount = await baseQuery.CountAsync().ConfigureAwait(false);
        var pagedData = await ApplySongSortOrder(ExcludeHeavyFields(baseQuery), sortOrder)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync().ConfigureAwait(false);

        return new PagedResult<Song>
            { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <inheritdoc />
    public async Task<PagedResult<Song>> GetSongsByPlaylistPagedAsync(Guid playlistId, int pageNumber, int pageSize, SongSortOrder sortOrder)
    {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.PlaylistSongs.AsNoTracking()
            .Where(ps => ps.PlaylistId == playlistId);

        if (sortOrder == SongSortOrder.PlaylistOrder)
        {
            query = query.OrderBy(ps => ps.Order).ThenBy(ps => ps.SongId);

            var projectedQuery = query
                .Select(ps => new 
                { 
                    ps.Order,
                    Song = new Song
                    {
                        Id = ps.Song!.Id,
                        Title = ps.Song.Title,
                        AlbumId = ps.Song.AlbumId,
                        Album = ps.Song.Album,
                        // SongArtists = ps.Song.SongArtists,
                        Composer = ps.Song.Composer,
                        FolderId = ps.Song.FolderId,
                        Folder = ps.Song.Folder,
                        DurationTicks = ps.Song.DurationTicks,
                        AlbumArtUriFromTrack = ps.Song.AlbumArtUriFromTrack,
                        FilePath = ps.Song.FilePath,
                        DirectoryPath = ps.Song.DirectoryPath,
                        Year = ps.Song.Year,
                        TrackNumber = ps.Song.TrackNumber,
                        TrackCount = ps.Song.TrackCount,
                        DiscNumber = ps.Song.DiscNumber,
                        DiscCount = ps.Song.DiscCount,
                        SampleRate = ps.Song.SampleRate,
                        Bitrate = ps.Song.Bitrate,
                        Channels = ps.Song.Channels,
                        DateAddedToLibrary = ps.Song.DateAddedToLibrary,
                        FileCreatedDate = ps.Song.FileCreatedDate,
                        FileModifiedDate = ps.Song.FileModifiedDate,
                        LightSwatchId = ps.Song.LightSwatchId,
                        DarkSwatchId = ps.Song.DarkSwatchId,
                        Rating = ps.Song.Rating,
                        IsLoved = ps.Song.IsLoved,
                        PlayCount = ps.Song.PlayCount,
                        SkipCount = ps.Song.SkipCount,
                        LastPlayedDate = ps.Song.LastPlayedDate,
                        LrcFilePath = ps.Song.LrcFilePath,
                        LyricsLastCheckedUtc = ps.Song.LyricsLastCheckedUtc,
                        Bpm = ps.Song.Bpm,
                        ReplayGainTrackGain = ps.Song.ReplayGainTrackGain,
                        ReplayGainTrackPeak = ps.Song.ReplayGainTrackPeak,
                        Conductor = ps.Song.Conductor,
                        MusicBrainzTrackId = ps.Song.MusicBrainzTrackId,
                        MusicBrainzReleaseId = ps.Song.MusicBrainzReleaseId,
                        ArtistName = ps.Song.ArtistName,
                        PrimaryArtistName = ps.Song.PrimaryArtistName
                    }
                });

            var totalCount = await projectedQuery.CountAsync().ConfigureAwait(false);

            var pagedResults = await projectedQuery
                .Skip((pageNumber - 1) * pageSize).Take(pageSize)
                .AsSplitQuery()
                .ToListAsync().ConfigureAwait(false);

            foreach (var result in pagedResults)
            {
                result.Song.Order = result.Order;
            }

            var pagedSongs = pagedResults.Select(r => r.Song).ToList();

            return new PagedResult<Song>
                { Items = pagedSongs, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
        }
        else
        {
            var songQuery = query.Select(ps => ps.Song!);

            var totalCount = await songQuery.CountAsync().ConfigureAwait(false);

            var pagedSongs = await ApplySongSortOrder(ExcludeHeavyFields(songQuery), sortOrder)
                .Skip((pageNumber - 1) * pageSize).Take(pageSize)
                .AsSplitQuery()
                .ToListAsync().ConfigureAwait(false);

            return new PagedResult<Song>
                { Items = pagedSongs, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
        }
    }

    /// <inheritdoc />
    public async Task<PagedResult<Artist>> GetAllArtistsPagedAsync(int pageNumber, int pageSize,
        ArtistSortOrder sortOrder = ArtistSortOrder.NameAsc)
    {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.Artists.AsNoTracking();
        var totalCount = await query.CountAsync().ConfigureAwait(false);

        // Apply sort order - for SongCountDesc, we need to join with songs and count
        IOrderedQueryable<Artist> orderedQuery = sortOrder switch
        {
            ArtistSortOrder.NameDesc => query.OrderByDescending(a => a.Name).ThenByDescending(a => a.Id),
            ArtistSortOrder.SongCountDesc => query
                .OrderByDescending(a => context.SongArtists.Count(sa => sa.ArtistId == a.Id))
                .ThenByDescending(a => a.Name)
                .ThenByDescending(a => a.Id),
            ArtistSortOrder.SongCountAsc => query
                .OrderBy(a => context.SongArtists.Count(sa => sa.ArtistId == a.Id))
                .ThenBy(a => a.Name)
                .ThenBy(a => a.Id),
            _ => query.OrderBy(a => a.Name).ThenBy(a => a.Id)
        };

        var pagedData = await orderedQuery
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).ToListAsync().ConfigureAwait(false);

        return new PagedResult<Artist>
            { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <inheritdoc />
    public async Task<PagedResult<Artist>> SearchArtistsPagedAsync(string searchTerm, int pageNumber, int pageSize)
    {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = string.IsNullOrWhiteSpace(searchTerm)
            ? context.Artists
            : BuildArtistSearchQuery(context, searchTerm.Trim());
        var totalCount = await query.CountAsync().ConfigureAwait(false);
        var pagedData = await query.AsNoTracking().OrderBy(a => a.Name).ThenBy(a => a.Id)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).ToListAsync().ConfigureAwait(false);

        return new PagedResult<Artist>
            { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <inheritdoc />
    public async Task<PagedResult<Album>> GetAllAlbumsPagedAsync(int pageNumber, int pageSize,
        AlbumSortOrder sortOrder = AlbumSortOrder.ArtistAsc)
    {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.Albums.AsNoTracking().Include(al => al.AlbumArtists).ThenInclude(aa => aa.Artist);
        var totalCount = await query.CountAsync().ConfigureAwait(false);

        IOrderedQueryable<Album> orderedQuery = sortOrder switch
        {
            AlbumSortOrder.ArtistDesc => query.OrderByDescending(al => al.PrimaryArtistName).ThenByDescending(al => al.Title).ThenByDescending(al => al.Id),
            AlbumSortOrder.ArtistAsc => query.OrderBy(al => al.PrimaryArtistName).ThenBy(al => al.Title).ThenBy(al => al.Id),
            AlbumSortOrder.AlbumTitleAsc => query.OrderBy(al => al.Title).ThenBy(al => al.Id),
            AlbumSortOrder.AlbumTitleDesc => query.OrderByDescending(al => al.Title).ThenByDescending(al => al.Id),
            AlbumSortOrder.YearDesc => query.OrderByDescending(al => al.Year ?? 0).ThenByDescending(al => al.PrimaryArtistName).ThenByDescending(al => al.Title).ThenByDescending(al => al.Id),
            AlbumSortOrder.YearAsc => query.OrderBy(al => al.Year ?? int.MaxValue).ThenBy(al => al.PrimaryArtistName).ThenBy(al => al.Title).ThenBy(al => al.Id),
            AlbumSortOrder.SongCountDesc => query.OrderByDescending(al => context.Songs.Count(s => s.AlbumId == al.Id)).ThenByDescending(al => al.Title).ThenByDescending(al => al.Id),
            AlbumSortOrder.SongCountAsc => query.OrderBy(al => context.Songs.Count(s => s.AlbumId == al.Id)).ThenBy(al => al.Title).ThenBy(al => al.Id),
            _ => query.OrderBy(al => al.PrimaryArtistName).ThenBy(al => al.Title).ThenBy(al => al.Id)
        };

        var pagedData = await orderedQuery
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync().ConfigureAwait(false);

        return new PagedResult<Album>
            { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <inheritdoc />
    public async Task<PagedResult<Album>> SearchAlbumsPagedAsync(string searchTerm, int pageNumber, int pageSize)
    {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = string.IsNullOrWhiteSpace(searchTerm)
            ? context.Albums.Include(al => al.AlbumArtists).ThenInclude(aa => aa.Artist)
            : BuildAlbumSearchQuery(context, searchTerm.Trim());
        var totalCount = await query.CountAsync().ConfigureAwait(false);
        var pagedData = await query.AsNoTracking()
            .OrderBy(al => al.PrimaryArtistName).ThenBy(al => al.Title).ThenBy(al => al.Id)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync().ConfigureAwait(false);

        return new PagedResult<Album>
            { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <inheritdoc />
    public async Task<PagedResult<Playlist>> GetAllPlaylistsPagedAsync(int pageNumber, int pageSize)
    {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.Playlists.AsNoTracking().Include(p => p.PlaylistSongs);
        var totalCount = await query.CountAsync().ConfigureAwait(false);
        var pagedData = await query
            .OrderBy(p => p.Name).ThenBy(p => p.Id)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).ToListAsync().ConfigureAwait(false);

        return new PagedResult<Playlist>
            { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <inheritdoc />
    public async Task<PagedResult<Song>> GetSongsByFolderIdPagedAsync(Guid folderId, int pageNumber, int pageSize,
        SongSortOrder sortOrder = SongSortOrder.TitleAsc)
    {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var baseQuery = context.Songs.AsNoTracking().Where(s => s.FolderId == folderId).Include(s => s.SongArtists).ThenInclude(sa => sa.Artist)
            .Include(s => s.Album).ThenInclude(a => a!.AlbumArtists).ThenInclude(aa => aa.Artist);
        var totalCount = await baseQuery.CountAsync().ConfigureAwait(false);
        var pagedData = await ApplySongSortOrder(ExcludeHeavyFields(baseQuery), sortOrder)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync().ConfigureAwait(false);

        return new PagedResult<Song>
            { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <inheritdoc />
    public async Task<List<Guid>> GetAllSongIdsAsync(SongSortOrder sortOrder)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await ApplySongSortOrder(context.Songs.AsNoTracking(), sortOrder).Select(s => s.Id).ToListAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<List<Guid>> SearchAllSongIdsAsync(string searchTerm, SongSortOrder sortOrder)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetAllSongIdsAsync(sortOrder).ConfigureAwait(false);

        var query = BuildSongSearchQuery(context, searchTerm.Trim());
        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<List<Guid>> GetAllSongIdsByFolderIdAsync(Guid folderId, SongSortOrder sortOrder)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.Songs.AsNoTracking().Where(s => s.FolderId == folderId);
        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<List<Guid>> GetAllSongIdsInDirectoryRecursiveAsync(Guid folderId, string directoryPath,
        SongSortOrder sortOrder)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        // Normalize the directory path for comparison
        var normalizedPath = directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Get all song IDs where DirectoryPath equals normalizedPath or starts with normalizedPath followed by a separator
        // This prevents false positives (e.g., "C:\\Music\\Rock" matching "C:\\Music\\Rockabilly")
        var query = context.Songs.AsNoTracking()
            .Where(s => s.FolderId == folderId &&
                        (s.DirectoryPath == normalizedPath ||
                         s.DirectoryPath.StartsWith(normalizedPath + "\\") ||
                         s.DirectoryPath.StartsWith(normalizedPath + "/")));

        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PagedResult<Song>> GetSongsInDirectoryPagedAsync(Guid folderId, string directoryPath,
        int pageNumber, int pageSize, SongSortOrder sortOrder)
    {
        SanitizePaging(ref pageNumber, ref pageSize);
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var normalizedPath = directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var baseQuery = context.Songs.AsNoTracking()
            .Where(s => s.FolderId == folderId && s.DirectoryPath == normalizedPath)
            .Include(s => s.SongArtists).ThenInclude(sa => sa.Artist)
            .Include(s => s.Album).ThenInclude(a => a!.AlbumArtists).ThenInclude(aa => aa.Artist);

        var totalCount = await baseQuery.CountAsync().ConfigureAwait(false);
        var pagedData = await ApplySongSortOrder(ExcludeHeavyFields(baseQuery), sortOrder)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync().ConfigureAwait(false);

        return new PagedResult<Song>
            { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <inheritdoc />
    public async Task<List<Guid>> GetSongIdsInDirectoryAsync(Guid folderId, string directoryPath,
        SongSortOrder sortOrder)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var normalizedPath = directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var query = context.Songs.AsNoTracking()
            .Where(s => s.FolderId == folderId && s.DirectoryPath == normalizedPath);

        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<List<Guid>> GetAllSongIdsByArtistIdAsync(Guid artistId, SongSortOrder sortOrder)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.Songs.AsNoTracking().Where(s => s.SongArtists.Any(sa => sa.ArtistId == artistId));
        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<List<Guid>> GetAllSongIdsByAlbumIdAsync(Guid albumId, SongSortOrder sortOrder)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.Songs.AsNoTracking().Where(s => s.AlbumId == albumId);
        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<List<Guid>> GetAllSongIdsByPlaylistIdAsync(Guid playlistId, SongSortOrder sortOrder)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.PlaylistSongs.AsNoTracking()
            .Where(ps => ps.PlaylistId == playlistId);

        if (sortOrder == SongSortOrder.PlaylistOrder)
        {
            return await query.OrderBy(ps => ps.Order).ThenBy(ps => ps.SongId).Select(ps => ps.SongId).ToListAsync().ConfigureAwait(false);
        }

        var songQuery = query.Select(ps => ps.Song!);
        return await ApplySongSortOrder(songQuery, sortOrder).Select(s => s.Id).ToListAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<List<Guid>> GetAllSongIdsByGenreIdAsync(Guid genreId, SongSortOrder sortOrder)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.Songs.AsNoTracking().Where(s => s.Genres.Any(g => g.Id == genreId));
        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync().ConfigureAwait(false);
    }

    #endregion

    #region Scoped Search

    /// <inheritdoc />
    public async Task<IEnumerable<Song>> SearchSongsInFolderAsync(Guid folderId, string searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetSongsByFolderIdAsync(folderId).ConfigureAwait(false);

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var term = $"%{searchTerm.Trim()}%";
        var query = context.Songs.AsNoTracking().Where(s => s.FolderId == folderId
                                                             && (EF.Functions.Like(s.Title, term)
                                                                 || EF.Functions.Like(s.ArtistName, term)
                                                                 || (s.Album != null &&
                                                                     (EF.Functions.Like(s.Album.Title, term) || EF.Functions.Like(s.Album.ArtistName, term)))))
            .Include(s => s.SongArtists).ThenInclude(sa => sa.Artist)
            .Include(s => s.Album).ThenInclude(a => a!.AlbumArtists).ThenInclude(aa => aa.Artist)
            .OrderBy(s => s.Title).ThenBy(s => s.Id);

        return await ExcludeHeavyFields(query).AsSplitQuery().ToListAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Song>> SearchSongsInAlbumAsync(Guid albumId, string searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetSongsByAlbumIdAsync(albumId).ConfigureAwait(false);

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var term = $"%{searchTerm.Trim()}%";
        var query = context.Songs.AsNoTracking().Where(s => s.AlbumId == albumId
                                                             && (EF.Functions.Like(s.Title, term) ||
                                                                 EF.Functions.Like(s.ArtistName, term)))
            .Include(s => s.SongArtists).ThenInclude(sa => sa.Artist)
            .Include(s => s.Album).ThenInclude(a => a!.AlbumArtists).ThenInclude(aa => aa.Artist)
            .OrderBy(s => s.TrackNumber).ThenBy(s => s.Id);

        return await ExcludeHeavyFields(query).AsSplitQuery().ToListAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Song>> SearchSongsInArtistAsync(Guid artistId, string searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetSongsByArtistIdAsync(artistId).ConfigureAwait(false);

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var term = $"%{searchTerm.Trim()}%";
        var query = context.Songs.AsNoTracking().Where(s => s.SongArtists.Any(sa => sa.ArtistId == artistId)
                                                             && (EF.Functions.Like(s.Title, term) ||
                                                                 EF.Functions.Like(s.ArtistName, term) ||
                                                                 (s.Album != null &&
                                                                  (EF.Functions.Like(s.Album.Title, term) || EF.Functions.Like(s.Album.ArtistName, term)))))
            .Include(s => s.SongArtists).ThenInclude(sa => sa.Artist)
            .Include(s => s.Album).ThenInclude(a => a!.AlbumArtists).ThenInclude(aa => aa.Artist)
            .OrderBy(s => s.Album != null ? s.Album.Title : string.Empty).ThenBy(s => s.TrackNumber).ThenBy(s => s.Id);

        return await ExcludeHeavyFields(query).AsSplitQuery().ToListAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Song>> SearchSongsInPlaylistAsync(Guid playlistId, string searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetSongsInPlaylistOrderedAsync(playlistId).ConfigureAwait(false);

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var term = $"%{searchTerm.Trim()}%";
        var query = context.PlaylistSongs.AsNoTracking()
            .Where(ps => ps.PlaylistId == playlistId && ps.Song != null &&
                         (EF.Functions.Like(ps.Song.Title, term)
                          || EF.Functions.Like(ps.Song.ArtistName, term)
                          || (ps.Song.Album != null && (EF.Functions.Like(ps.Song.Album.Title, term) || EF.Functions.Like(ps.Song.Album.ArtistName, term)))));

        var songsQuery = ApplySongSortOrder(ExcludeHeavyFields(query
            .Include(ps => ps.Song).ThenInclude(s => s!.SongArtists).ThenInclude(sa => sa.Artist)
            .Include(ps => ps.Song).ThenInclude(s => s!.Album).ThenInclude(a => a!.AlbumArtists).ThenInclude(aa => aa.Artist)
            .Select(ps => ps.Song!)), SongSortOrder.TrackNumberAsc);

        return await songsQuery.AsSplitQuery().ToListAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Song>> SearchSongsInGenreAsync(Guid genreId, string searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetSongsByGenreIdAsync(genreId).ConfigureAwait(false);

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var term = $"%{searchTerm.Trim()}%";
        var query = context.Songs.AsNoTracking()
            .Where(s => s.Genres.Any(g => g.Id == genreId)
                        && (EF.Functions.Like(s.Title, term)
                            || EF.Functions.Like(s.ArtistName, term)
                            || (s.Album != null && (EF.Functions.Like(s.Album.Title, term) || EF.Functions.Like(s.Album.ArtistName, term)))))
            .Include(s => s.SongArtists).ThenInclude(sa => sa.Artist)
            .Include(s => s.Album).ThenInclude(a => a!.AlbumArtists).ThenInclude(aa => aa.Artist)
            .OrderBy(s => s.Title).ThenBy(s => s.Id);

        return await ExcludeHeavyFields(query).AsSplitQuery().ToListAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PagedResult<Song>> SearchSongsInFolderPagedAsync(Guid folderId, string searchTerm, int pageNumber,
        int pageSize)
    {
        SanitizePaging(ref pageNumber, ref pageSize);
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var query = context.Songs.AsNoTracking().Where(s => s.FolderId == folderId);
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = $"%{searchTerm.Trim()}%";
            query = query.Where(s => EF.Functions.Like(s.Title, term)
                                     || EF.Functions.Like(s.ArtistName, term)
                                     || (s.Album != null && (EF.Functions.Like(s.Album.Title, term) || EF.Functions.Like(s.Album.ArtistName, term))));
        }

        var totalCount = await query.CountAsync().ConfigureAwait(false);
        var pagedData = await ApplySongSortOrder(ExcludeHeavyFields(query), SongSortOrder.TitleAsc)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync().ConfigureAwait(false);

        return new PagedResult<Song>
            { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <inheritdoc />
    public async Task<PagedResult<Song>> SearchSongsInAlbumPagedAsync(Guid albumId, string searchTerm, int pageNumber,
        int pageSize)
    {
        SanitizePaging(ref pageNumber, ref pageSize);
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var query = context.Songs.AsNoTracking().Where(s => s.AlbumId == albumId);
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = $"%{searchTerm.Trim()}%";
            query = query.Where(s =>
                EF.Functions.Like(s.Title, term) || EF.Functions.Like(s.ArtistName, term));
        }

        var totalCount = await query.CountAsync().ConfigureAwait(false);
        var pagedData = await ApplySongSortOrder(ExcludeHeavyFields(query), SongSortOrder.TrackNumberAsc)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync().ConfigureAwait(false);

        return new PagedResult<Song>
            { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <inheritdoc />
    public async Task<PagedResult<Song>> SearchSongsInArtistPagedAsync(Guid artistId, string searchTerm, int pageNumber,
        int pageSize)
    {
        SanitizePaging(ref pageNumber, ref pageSize);
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var query = context.Songs.AsNoTracking().Where(s => s.SongArtists.Any(sa => sa.ArtistId == artistId));
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = $"%{searchTerm.Trim()}%";
            query = query.Where(s =>
                EF.Functions.Like(s.Title, term) || EF.Functions.Like(s.ArtistName, term) || (s.Album != null && (EF.Functions.Like(s.Album.Title, term) || EF.Functions.Like(s.Album.ArtistName, term))));
        }

        var totalCount = await query.CountAsync().ConfigureAwait(false);
        var pagedData = await ApplySongSortOrder(ExcludeHeavyFields(query), SongSortOrder.AlbumAsc)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync().ConfigureAwait(false);

        return new PagedResult<Song>
            { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <inheritdoc />
    public async Task<PagedResult<Song>> SearchSongsInPlaylistPagedAsync(Guid playlistId, string searchTerm,
        int pageNumber, int pageSize, SongSortOrder sortOrder)
    {
        SanitizePaging(ref pageNumber, ref pageSize);
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var query = context.PlaylistSongs.AsNoTracking()
            .Where(ps => ps.PlaylistId == playlistId);

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = $"%{searchTerm.Trim()}%";
            query = query.Where(ps => ps.Song != null &&
                                      (EF.Functions.Like(ps.Song.Title, term)
                                       || EF.Functions.Like(ps.Song.ArtistName, term)
                                       || ps.Song.SongArtists.Any(sa => EF.Functions.Like(sa.Artist.Name, term))
                                       || (ps.Song.Album != null && (EF.Functions.Like(ps.Song.Album.Title, term) || EF.Functions.Like(ps.Song.Album.ArtistName, term)))));
        }

        var songQuery = query.Select(ps => ps.Song!);

        var totalCount = await songQuery.CountAsync().ConfigureAwait(false);

        var pagedData = await ApplySongSortOrder(ExcludeHeavyFields(songQuery), sortOrder)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize)
            .AsSplitQuery()
            .ToListAsync().ConfigureAwait(false);

        return new PagedResult<Song>
            { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <inheritdoc />
    public async Task<PagedResult<Song>> SearchSongsInGenrePagedAsync(Guid genreId, string searchTerm, int pageNumber,
        int pageSize)
    {
        SanitizePaging(ref pageNumber, ref pageSize);
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var query = context.Songs.AsNoTracking()
            .Where(s => s.Genres.Any(g => g.Id == genreId));

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = $"%{searchTerm.Trim()}%";
            query = query.Where(s => EF.Functions.Like(s.Title, term)
                                     || EF.Functions.Like(s.ArtistName, term)
                                     || (s.Album != null && (EF.Functions.Like(s.Album.Title, term) || EF.Functions.Like(s.Album.ArtistName, term))));
        }

        var totalCount = await query.CountAsync().ConfigureAwait(false);
        var pagedData = await ApplySongSortOrder(ExcludeHeavyFields(query), SongSortOrder.TitleAsc)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize)
            .AsSplitQuery()
            .ToListAsync().ConfigureAwait(false);

        return new PagedResult<Song>
            { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <inheritdoc />
    public async Task<List<Guid>> SearchAllSongIdsInFolderAsync(Guid folderId, string searchTerm,
        SongSortOrder sortOrder)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.Songs.AsNoTracking().Where(s => s.FolderId == folderId);

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = $"%{searchTerm.Trim()}%";
            query = query.Where(s => EF.Functions.Like(s.Title, term)
                                     || EF.Functions.Like(s.ArtistName, term)
                                     || (s.Album != null && (EF.Functions.Like(s.Album.Title, term) || EF.Functions.Like(s.Album.ArtistName, term))));
        }

        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<List<Guid>> SearchAllSongIdsInArtistAsync(Guid artistId, string searchTerm,
        SongSortOrder sortOrder)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.Songs.AsNoTracking().Where(s => s.SongArtists.Any(sa => sa.ArtistId == artistId));

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = $"%{searchTerm.Trim()}%";
            query = query.Where(s => EF.Functions.Like(s.Title, term)
                                     || EF.Functions.Like(s.ArtistName, term)
                                     || (s.Album != null && (EF.Functions.Like(s.Album.Title, term) || EF.Functions.Like(s.Album.ArtistName, term))));
        }

        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<List<Guid>> SearchAllSongIdsInAlbumAsync(Guid albumId, string searchTerm, SongSortOrder sortOrder)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.Songs.AsNoTracking().Where(s => s.AlbumId == albumId);

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = $"%{searchTerm.Trim()}%";
            query = query.Where(s => EF.Functions.Like(s.Title, term)
                                     || EF.Functions.Like(s.ArtistName, term)
                                     || (s.Album != null && (EF.Functions.Like(s.Album.Title, term) || EF.Functions.Like(s.Album.ArtistName, term))));
        }

        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<List<Guid>> SearchAllSongIdsInPlaylistAsync(Guid playlistId, string searchTerm, SongSortOrder sortOrder)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.PlaylistSongs.AsNoTracking()
            .Where(ps => ps.PlaylistId == playlistId);

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = $"%{searchTerm.Trim()}%";
            query = query.Where(ps => ps.Song != null &&
                                      (EF.Functions.Like(ps.Song.Title, term)
                                       || EF.Functions.Like(ps.Song.ArtistName, term)
                                       || ps.Song.SongArtists.Any(sa => EF.Functions.Like(sa.Artist.Name, term))
                                       || (ps.Song.Album != null && (EF.Functions.Like(ps.Song.Album.Title, term) || EF.Functions.Like(ps.Song.Album.ArtistName, term)))));
        }

        var songQuery = query.Select(ps => ps.Song!);

        return await ApplySongSortOrder(songQuery, sortOrder)
            .Select(s => s.Id)
            .ToListAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<List<Guid>> SearchAllSongIdsInGenreAsync(Guid genreId, string searchTerm, SongSortOrder sortOrder)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.Songs.AsNoTracking().Where(s => s.Genres.Any(g => g.Id == genreId));

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = $"%{searchTerm.Trim()}%";
            query = query.Where(s => EF.Functions.Like(s.Title, term)
                                     || EF.Functions.Like(s.ArtistName, term)
                                     || (s.Album != null && (EF.Functions.Like(s.Album.Title, term) || EF.Functions.Like(s.Album.ArtistName, term))));
        }

        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync().ConfigureAwait(false);
    }

    #endregion

    #region Private Helpers

    private bool IsPathInLrcCache(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;

        try
        {
            var lrcCachePath = _pathConfig.LrcCachePath;
            var normalizedFilePath = Path.GetFullPath(filePath);
            var normalizedCachePath = Path.GetFullPath(lrcCachePath);

            return normalizedFilePath.StartsWith(normalizedCachePath, StringComparison.OrdinalIgnoreCase)
                   && _fileSystem.FileExists(filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not validate LRC cache path for {FilePath}", filePath);
            return false;
        }
    }

    /// <summary>
    ///     Ensures that all discovered directory paths have corresponding Folder records in the database,
    ///     creating a hierarchical structure of subfolders under the root folder.
    ///     This includes all intermediate directories in the path hierarchy, even if they don't directly contain music files.
    /// </summary>
    private async Task EnsureSubFoldersExistAsync(Guid rootFolderId, string rootFolderPath,
        HashSet<string> discoveredDirectories, CancellationToken cancellationToken)
    {
        if (discoveredDirectories.Count == 0) return;

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var allFolders = await context.Folders.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        var existingFolders = allFolders.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);

        var foldersToCreate = new List<Folder>();
        var normalizedRootPath = rootFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var allDirectoriesToEnsure = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // For each discovered directory (which contains songs), ensure all parent directories
        // in the path hierarchy are also added, so empty intermediate folders show up in the UI
        foreach (var directoryPath in discoveredDirectories)
        {
            var normalizedDirPath = directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var currentPath = normalizedDirPath;

            // Walk up the directory tree to the root, collecting all intermediate paths
            while (!string.IsNullOrEmpty(currentPath) &&
                   !currentPath.Equals(normalizedRootPath, StringComparison.OrdinalIgnoreCase))
            {
                allDirectoriesToEnsure.Add(currentPath);
                var parentPath = _fileSystem.GetDirectoryName(currentPath);
                if (string.IsNullOrEmpty(parentPath)) break;
                currentPath = parentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }

        var sortedDirectories = allDirectoriesToEnsure
            .OrderBy(d => d.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries).Length)
            .ToList();

        foreach (var directoryPath in sortedDirectories)
        {
            var normalizedDirPath = directoryPath;

            if (existingFolders.ContainsKey(normalizedDirPath))
                continue;

            Guid? parentFolderId = rootFolderId;
            var parentPath = _fileSystem.GetDirectoryName(normalizedDirPath);

            if (!string.IsNullOrWhiteSpace(parentPath))
            {
                var normalizedParentPath =
                    parentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (!normalizedParentPath.Equals(normalizedRootPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (existingFolders.TryGetValue(normalizedParentPath, out var parentFolder))
                    {
                        parentFolderId = parentFolder.Id;
                    }
                    else
                    {
                        var parentInList = foldersToCreate.FirstOrDefault(f =>
                            f.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                .Equals(normalizedParentPath, StringComparison.OrdinalIgnoreCase));
                        if (parentInList != null) parentFolderId = parentInList.Id;
                    }
                }
            }

            var folderName = Path.GetFileName(normalizedDirPath);
            var newFolder = new Folder
            {
                Name = string.IsNullOrWhiteSpace(folderName) ? normalizedDirPath : folderName,
                Path = normalizedDirPath,
                ParentFolderId = parentFolderId,
                LastModifiedDate = DateTime.UtcNow
            };

            foldersToCreate.Add(newFolder);
            existingFolders[normalizedDirPath] = newFolder;
        }

        if (foldersToCreate.Count > 0)
        {
            context.Folders.AddRange(foldersToCreate);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<(List<string> filesToAdd, List<string> filesToUpdate, List<string> filesRemovedFromDisk)>
        AnalyzeFolderChangesAsync(Guid folderId, string folderPath, bool forceFullScan, CancellationToken cancellationToken)
    {
        await using var analysisContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var dbFileMap = (await analysisContext.Songs
                .AsNoTracking()
                .Where(s => s.FolderId == folderId)
                .Select(s => new { s.FilePath, s.FileModifiedDate })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(s => s.FilePath, s => s.FileModifiedDate, StringComparer.OrdinalIgnoreCase);

        cancellationToken.ThrowIfCancellationRequested();

        var diskFileMap = _fileSystem.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
            .Where(file => FileExtensions.MusicFileExtensions.Contains(_fileSystem.GetExtension(file)))
            .Select(path =>
            {
                try
                {
                    return new { Path = path, LastWriteTime = _fileSystem.GetLastWriteTimeUtc(path) };
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Could not access file {FilePath} during folder analysis.", path);
                    return null;
                }
            })
            .Where(x => x != null)
            .ToDictionary(x => x!.Path, x => x!.LastWriteTime, StringComparer.OrdinalIgnoreCase);

        var dbPaths = new HashSet<string>(dbFileMap.Keys, StringComparer.OrdinalIgnoreCase);
        var diskPaths = new HashSet<string>(diskFileMap.Keys, StringComparer.OrdinalIgnoreCase);

        var filesToAdd = diskPaths.Except(dbPaths).ToList();

        var commonPaths = dbPaths.Intersect(diskPaths);
        var filesToUpdate = forceFullScan 
            ? commonPaths.ToList() 
            : commonPaths.Where(path => dbFileMap[path] != diskFileMap[path]).ToList();

        var filesRemovedFromDisk = dbPaths.Except(diskPaths).ToList();

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

        // First pass: find all LRC files for songs that need them
        var songLrcMappings = new Dictionary<Guid, string>();
        foreach (var song in songsWithoutLrc)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var lrcPath = FindLrcFilePathForAudioFile(song.FilePath);
            if (lrcPath != null)
                songLrcMappings[song.Id] = lrcPath;
        }

        if (songLrcMappings.Count == 0)
            return 0;

        // Process in batches to avoid SQL parameter limits and memory issues with large libraries
        const int batchSize = 500;
        var songIdsToUpdate = songLrcMappings.Keys.ToList();
        var totalUpdated = 0;

        for (var i = 0; i < songIdsToUpdate.Count; i += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batchIds = songIdsToUpdate.Skip(i).Take(batchSize).ToList();
            var songsToUpdate = await context.Songs
                .Where(s => batchIds.Contains(s.Id))
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            foreach (var song in songsToUpdate)
            {
                if (songLrcMappings.TryGetValue(song.Id, out var lrcPath))
                    song.LrcFilePath = lrcPath;
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

        // Find cover art for each song that needs it
        var songCoverArtMappings = new Dictionary<Guid, (string coverArtPath, string? uri, string? lightSwatch, string? darkSwatch)>();

        foreach (var song in songsWithoutCoverArt)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var coverArtPath = FindCoverArtInDirectoryHierarchy(song.FilePath, baseFolderPath);
            if (coverArtPath != null)
            {
                try
                {
                    // Read and process the cover art file
                    var imageBytes = await System.IO.File.ReadAllBytesAsync(coverArtPath, cancellationToken).ConfigureAwait(false);
                    if (imageBytes.Length > 0)
                    {
                        // Use the new overload from IMetadataService to get the image processor
                        // We need to call the image processor directly
                        var metadata = await _metadataService.ExtractMetadataAsync(song.FilePath, baseFolderPath).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(metadata.CoverArtUri))
                        {
                            songCoverArtMappings[song.Id] = (coverArtPath, metadata.CoverArtUri, metadata.LightSwatchId, metadata.DarkSwatchId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process cover art file {CoverArtPath} for song {SongId}.",
                        coverArtPath, song.Id);
                }
            }
        }

        if (songCoverArtMappings.Count == 0)
            return 0;

        // Update songs with cover art in batches
        const int batchSize = 500;
        var songIdsToUpdate = songCoverArtMappings.Keys.ToList();
        var totalUpdated = 0;

        for (var i = 0; i < songIdsToUpdate.Count; i += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batchIds = songIdsToUpdate.Skip(i).Take(batchSize).ToList();
            var songsToUpdate = await context.Songs
                .Where(s => batchIds.Contains(s.Id))
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            foreach (var song in songsToUpdate)
            {
                if (songCoverArtMappings.TryGetValue(song.Id, out var coverArtInfo))
                {
                    song.AlbumArtUriFromTrack = coverArtInfo.uri;
                    song.LightSwatchId = coverArtInfo.lightSwatch;
                    song.DarkSwatchId = coverArtInfo.darkSwatch;
                }
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
    ///     Searches for a cover art file in the directory hierarchy, starting from the song's
    ///     directory and walking up to the base folder path.
    /// </summary>
    private string? FindCoverArtInDirectoryHierarchy(string songFilePath, string? baseFolderPath)
    {
        try
        {
            var currentDirectory = _fileSystem.GetDirectoryName(songFilePath);
            if (string.IsNullOrEmpty(currentDirectory)) return null;

            // Normalize the base folder path for comparison
            var normalizedBasePath = baseFolderPath?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            while (!string.IsNullOrEmpty(currentDirectory))
            {
                // Search for cover art in the current directory
                var coverArtPath = FindCoverArtInDirectory(currentDirectory);
                if (coverArtPath != null) return coverArtPath;

                // Check if we've reached or passed the base folder
                var normalizedCurrent = currentDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!string.IsNullOrEmpty(normalizedBasePath) &&
                    normalizedCurrent.Equals(normalizedBasePath, StringComparison.OrdinalIgnoreCase))
                {
                    // We've searched the base folder, stop here
                    break;
                }

                // Move up to the parent directory
                var parentDirectory = _fileSystem.GetDirectoryName(currentDirectory);

                // Safety check: if parent equals current, we're at the root
                if (string.IsNullOrEmpty(parentDirectory) ||
                    parentDirectory.Equals(currentDirectory, StringComparison.OrdinalIgnoreCase))
                    break;

                currentDirectory = parentDirectory;
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
    ///     Searches for a cover art file in a specific directory by enumerating files
    ///     and matching against known cover art file names (case-insensitive).
    /// </summary>
    private string? FindCoverArtInDirectory(string directory)
    {
        try
        {
            // Enumerate files once and find matches - more efficient than checking each combination
            var files = _fileSystem.GetFiles(directory, "*.*");
            
            // First pass: find all matching cover art files
            string? bestMatch = null;
            var bestPriority = int.MaxValue;
            
            foreach (var filePath in files)
            {
                var extension = _fileSystem.GetExtension(filePath);
                if (!FileExtensions.ImageFileExtensions.Contains(extension))
                    continue;
                    
                var fileNameWithoutExt = _fileSystem.GetFileNameWithoutExtension(filePath);
                if (!FileExtensions.CoverArtFileNames.Contains(fileNameWithoutExt))
                    continue;
                
                // Determine priority based on position in the priority list
                var priority = GetCoverArtPriority(fileNameWithoutExt);
                if (priority < bestPriority)
                {
                    bestPriority = priority;
                    bestMatch = filePath;
                }
            }
            
            return bestMatch;
        }
        catch (Exception)
        {
            // If we can't enumerate the directory, return null
            return null;
        }
    }
    
    /// <summary>
    ///     Gets the priority of a cover art file name (lower is higher priority).
    /// </summary>
    private static int GetCoverArtPriority(string fileNameWithoutExt)
    {
        // Priority order: cover (0), folder (1), album (2), front (3)
        return fileNameWithoutExt.ToLowerInvariant() switch
        {
            "cover" => 0,
            "folder" => 1,
            "album" => 2,
            "front" => 3,
            _ => int.MaxValue
        };
    }


    /// <summary>
    ///     Searches for an external .lrc file in the same directory as the audio file, matching by filename.
    /// </summary>
    private string? FindLrcFilePathForAudioFile(string audioFilePath)
    {
        try
        {
            var directory = _fileSystem.GetDirectoryName(audioFilePath);
            if (string.IsNullOrEmpty(directory)) return null;

            var audioFileNameWithoutExt = _fileSystem.GetFileNameWithoutExtension(audioFilePath);
            var lrcFiles = _fileSystem.GetFiles(directory, "*.lrc");

            var lrcMatch = lrcFiles.FirstOrDefault(lrcPath =>
                _fileSystem.GetFileNameWithoutExtension(lrcPath)
                    .Equals(audioFileNameWithoutExt, StringComparison.OrdinalIgnoreCase));

            if (lrcMatch != null) return lrcMatch;

            // Also search for .txt files as a fallback for unsynchronized external lyrics
            var txtFiles = _fileSystem.GetFiles(directory, "*.txt");
            return txtFiles.FirstOrDefault(txtPath =>
                _fileSystem.GetFileNameWithoutExtension(txtPath)
                    .Equals(audioFileNameWithoutExt, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while searching for external LRC file for '{AudioFilePath}'.",
                audioFilePath);
            return null;
        }
    }


    /// <summary>
    ///     Extracts metadata from files and writes to a Channel for streaming consumption.
    ///     This allows the database batch writer to start processing immediately rather than
    ///     waiting for all files to be extracted first, reducing peak memory usage.
    /// </summary>
    private async Task<int> ExtractAndSaveMetadataStreamingAsync(
        Guid folderId,
        List<string> filesToProcess,
        string baseFolderPath,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        const int channelCapacity = 500; // Matches batch size to limit memory
        var channel = Channel.CreateBounded<SongFileMetadata>(new BoundedChannelOptions(channelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        var totalFiles = filesToProcess.Count;
        var processedCount = 0;
        var extractedCount = 0;
        const int progressReportingBatchSize = 25;

        progress?.Report(new ScanProgress
            { StatusText = "Reading song details...", TotalFiles = totalFiles, Percentage = 0 });

        // Producer: Extract metadata concurrently and write to channel
        var producerTask = Task.Run(async () =>
        {
            try
            {
                var degreeOfParallelism = Environment.ProcessorCount;
                using var semaphore = new SemaphoreSlim(degreeOfParallelism);

                var extractionTasks = filesToProcess.Select(async filePath =>
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var metadata = await _metadataService.ExtractMetadataAsync(filePath, baseFolderPath).ConfigureAwait(false);
                        
                        if (!metadata.ExtractionFailed)
                        {
                            await channel.Writer.WriteAsync(metadata, cancellationToken).ConfigureAwait(false);
                            Interlocked.Increment(ref extractedCount);
                        }
                        else
                        {
                            _logger.LogWarning("Failed to extract metadata from file: {FilePath}", filePath);
                        }
                    }
                    finally
                    {
                        var currentCount = Interlocked.Increment(ref processedCount);

                        if (currentCount % progressReportingBatchSize == 0 || currentCount == totalFiles)
                            progress?.Report(new ScanProgress
                            {
                                StatusText = "Reading song details...",
                                CurrentFilePath = filePath,
                                Percentage = (double)currentCount / totalFiles * 50, // First 50% is extraction
                                TotalFiles = totalFiles,
                                NewSongsFound = extractedCount
                            });
                        semaphore.Release();
                    }
                });

                await Task.WhenAll(extractionTasks).ConfigureAwait(false);
                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
                throw;
            }
        }, cancellationToken);

        // Consumer: Batch and save to database as metadata arrives
        var totalSaved = 0;
        var consumerTask = Task.Run(async () =>
        {
            try
            {
                var batch = new List<SongFileMetadata>(500);
                var batchNumber = 0;

                await foreach (var metadata in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    batch.Add(metadata);

                    if (batch.Count >= 500)
                    {
                        batchNumber++;
                        progress?.Report(new ScanProgress
                        {
                            StatusText = $"Saving batch {batchNumber}...",
                            Percentage = 50 + ((double)totalSaved / totalFiles * 50), // Second 50% is saving
                            NewSongsFound = extractedCount
                        });

                        var saved = await ProcessSingleBatchAsync(folderId, batch.ToArray(), cancellationToken).ConfigureAwait(false);
                        totalSaved += saved;
                        batch.Clear();
                    }
                }

                // Process remaining items
                if (batch.Count > 0)
                {
                    batchNumber++;
                    progress?.Report(new ScanProgress
                    {
                        StatusText = $"Saving batch {batchNumber}...",
                        Percentage = 95,
                        NewSongsFound = extractedCount
                    });

                    var saved = await ProcessSingleBatchAsync(folderId, batch.ToArray(), cancellationToken).ConfigureAwait(false);
                    totalSaved += saved;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Consumer failed in streaming metadata extraction for folder {FolderId}", folderId);
                throw;
            }
        }, cancellationToken);

        await Task.WhenAll(producerTask, consumerTask).ConfigureAwait(false);
        return totalSaved;
    }



    private async Task<int> ProcessSingleBatchAsync(Guid folderId, SongFileMetadata[] metadataList, CancellationToken cancellationToken)
    {
        const int maxRetries = 3;
        var retryCount = 0;

        while (retryCount < maxRetries)
        {
            try
            {
                await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
                
                // Disable change tracking for faster reads/inserts where possible
                // (Though we need tracking for Add, we don't need it for the lookups if we attach properly,
                // but standard simple Add is safer for complex relationships)
                
                var filePaths = metadataList.Select(m => m.FilePath).ToList();

                // Fetch existing songs for this batch to support updating existing records.
                // Including Genres and SongArtists is crucial for safe many-to-many updates.
                var existingSongs = await context.Songs
                    .Include(s => s.Genres)
                    .Include(s => s.SongArtists)
                    .Where(s => filePaths.Contains(s.FilePath))
                    .AsSplitQuery()
                    .ToDictionaryAsync(s => s.FilePath, StringComparer.OrdinalIgnoreCase, cancellationToken).ConfigureAwait(false);

                var metadataToProcess = metadataList;

                if (metadataToProcess.Length == 0)
                    return 0;

                var artistNames = metadataToProcess.SelectMany(m =>
                    {
                        var trackArtists = m.Artists.Any() ? m.Artists : new List<string> { Artist.UnknownArtistName };
                        var albumArtists = m.AlbumArtists.Any() ? m.AlbumArtists : trackArtists;
                        return trackArtists.Concat(albumArtists);
                    })
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var albumTitles = metadataToProcess.Select(m => m.Album).Where(t => !string.IsNullOrWhiteSpace(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var genreNames = metadataToProcess.SelectMany(m => m.Genres ?? Enumerable.Empty<string>())
                    .Where(g => !string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                var existingArtists = await context.Artists.Where(a => artistNames.Contains(a.Name))
                    .ToDictionaryAsync(a => a.Name, StringComparer.OrdinalIgnoreCase, cancellationToken).ConfigureAwait(false);
                var existingAlbumList = await context.Albums
                    .Include(a => a.AlbumArtists)
                    .Where(a => albumTitles.Contains(a.Title))
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                var existingGenres = await context.Genres.Where(g => genreNames.Contains(g.Name))
                    .ToDictionaryAsync(g => g.Name, StringComparer.OrdinalIgnoreCase, cancellationToken).ConfigureAwait(false);

                // Convert album list to dictionary for efficient lookups by Title and Primary Artist ID
                var albumLookup = existingAlbumList.ToDictionary(a => $"{a.Title}|{a.AlbumArtists.OrderBy(aa => aa.Order).FirstOrDefault()?.ArtistId}", StringComparer.OrdinalIgnoreCase);

                // Add missing Artists/Genres to Context/Dict
                foreach (var name in artistNames)
                    if (!existingArtists.ContainsKey(name!))
                    {
                        var newArtist = new Artist { Name = name! };
                        context.Artists.Add(newArtist);
                        existingArtists[name!] = newArtist;
                    }

                foreach (var name in genreNames)
                    if (!existingGenres.ContainsKey(name!))
                    {
                        var newGenre = new Genre { Name = name! };
                        context.Genres.Add(newGenre);
                        existingGenres[name!] = newGenre;
                    }

                // We must save artists/genres first to get IDs if we were using IDs directly, 
                // but EF Core graph insertion handles nav properties. 
                // However, to prevent duplicates in the same batch, sharing the entity instance is key.

                foreach (var metadata in metadataToProcess)
                {
                    existingSongs.TryGetValue(metadata.FilePath, out var existingSong);
                    await AddSongWithDetailsOptimizedAsync(context, folderId, metadata, existingArtists, albumLookup,
                        existingGenres, existingSong);
                }

                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return metadataToProcess.Length;
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                retryCount++;
                _logger.LogWarning(ex, "Concurrency conflict during batch save. Attempt {RetryCount}/{MaxRetries}.", retryCount, maxRetries);
                if (retryCount >= maxRetries) throw;
                await Task.Delay(200 * retryCount, cancellationToken);
            }
        }
        return 0;
    }

    /// <summary>
    ///     Safely gets an artist from the lookup dictionary, creating a new one if not found.
    ///     This prevents KeyNotFoundException if metadata contains artist names not pre-populated in the lookup.
    /// </summary>
    private static Artist GetOrCreateArtist(
        MusicDbContext context,
        Dictionary<string, Artist> artistLookup,
        string artistName,
        ILogger logger)
    {
        if (artistLookup.TryGetValue(artistName, out var artist))
        {
            return artist;
        }

        // Artist not in lookup - this can happen due to race conditions or normalization differences
        logger.LogDebug("Artist '{ArtistName}' not found in pre-built lookup, creating on-the-fly.", artistName);
        artist = new Artist { Name = artistName };
        context.Artists.Add(artist);
        artistLookup[artistName] = artist;
        return artist;
    }

    private Task AddSongWithDetailsOptimizedAsync(
        MusicDbContext context,
        Guid folderId,
        SongFileMetadata metadata,
        Dictionary<string, Artist> artistLookup,
        Dictionary<string, Album> albumLookup,
        Dictionary<string, Genre> genreLookup,
        Song? existingSong = null)
    {
        // Filter and validate artist names, providing fallback for empty/invalid names
        var trackArtistNames = metadata.Artists
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .ToList();
        if (trackArtistNames.Count == 0)
        {
            trackArtistNames.Add(Artist.UnknownArtistName);
        }

        var albumArtistNames = metadata.AlbumArtists
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .ToList();
        if (albumArtistNames.Count == 0)
        {
            albumArtistNames = trackArtistNames;
        }

        var primaryAlbumArtist = GetOrCreateArtist(context, artistLookup, albumArtistNames[0], _logger);

        Album? album = null;
        if (!string.IsNullOrWhiteSpace(metadata.Album))
        {
            var albumTitle = metadata.Album.Trim();
            var albumKey = $"{albumTitle}|{primaryAlbumArtist.Id}";
            
            if (!albumLookup.TryGetValue(albumKey, out album))
            {
                album = new Album
                {
                    Title = albumTitle,
                    Year = metadata.Year
                };
                // Add all album artists using safe lookup
                for (int i = 0; i < albumArtistNames.Count; i++)
                {
                    var artist = GetOrCreateArtist(context, artistLookup, albumArtistNames[i], _logger);
                    album.AlbumArtists.Add(new AlbumArtist
                    {
                        Artist = artist,
                        Order = i
                    });
                }
                context.Albums.Add(album);
                albumLookup[albumKey] = album;
            }
            else
            {
                if (album.Year is null && metadata.Year.HasValue)
                {
                    album.Year = metadata.Year;
                }

                // Synchronize AlbumArtists using safe lookup
                var newAlbumArtists = albumArtistNames.Select((name, index) => new { Artist = GetOrCreateArtist(context, artistLookup, name, _logger), Order = index }).ToList();
                var currentAlbumArtists = album.AlbumArtists.OrderBy(aa => aa.Order).ToList(); // Requires Include(a => a.AlbumArtists) in caller

                if (currentAlbumArtists.Count != newAlbumArtists.Count ||
                    currentAlbumArtists.Zip(newAlbumArtists, (c, n) => c.ArtistId == n.Artist.Id && c.Order == n.Order).Any(val => !val))
                {
                    album.AlbumArtists.Clear();
                    foreach (var n in newAlbumArtists)
                    {
                        album.AlbumArtists.Add(new AlbumArtist
                        {
                            ArtistId = n.Artist.Id,
                            Artist = n.Artist,
                            Order = n.Order
                        });
                    }
                }
            }
        }

        // Safely get genres using TryGetValue with fallback to create missing ones
        var genres = new List<Genre>();
        if (metadata.Genres != null)
        {
            foreach (var genreName in metadata.Genres.Select(g => g.Trim()).Where(g => !string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (genreLookup.TryGetValue(genreName, out var genre))
                {
                    genres.Add(genre);
                }
                else
                {
                    // Genre not in lookup - create on-the-fly
                    _logger.LogDebug("Genre '{GenreName}' not found in pre-built lookup, creating on-the-fly.", genreName);
                    genre = new Genre { Name = genreName };
                    context.Genres.Add(genre);
                    genreLookup[genreName] = genre;
                    genres.Add(genre);
                }
            }
        }

        var directoryPath = _fileSystem.GetDirectoryName(metadata.FilePath) ?? string.Empty;

        var song = existingSong ?? new Song();

        song.FilePath = metadata.FilePath;
        song.DirectoryPath = directoryPath;
        song.Title = metadata.Title;
        song.DurationTicks = metadata.Duration.Ticks;
        song.AlbumArtUriFromTrack = metadata.CoverArtUri;
        song.LightSwatchId = metadata.LightSwatchId;
        song.DarkSwatchId = metadata.DarkSwatchId;
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
        song.LrcFilePath = metadata.LrcFilePath;
        song.AlbumId = album?.Id;
        
        // Synchronize SongArtists collection using safe lookup
        var newSongArtists = trackArtistNames.Select((name, index) => new { Artist = GetOrCreateArtist(context, artistLookup, name, _logger), Order = index }).ToList();
        var currentSongArtists = song.SongArtists.OrderBy(sa => sa.Order).ToList();

        if (currentSongArtists.Count != newSongArtists.Count ||
            currentSongArtists.Zip(newSongArtists, (c, n) => c.ArtistId == n.Artist.Id && c.Order == n.Order).Any(val => !val))
        {
            song.SongArtists.Clear();
            foreach (var nsa in newSongArtists)
            {
                song.SongArtists.Add(new SongArtist { ArtistId = nsa.Artist.Id, Artist = nsa.Artist, Order = nsa.Order });
            }
        }

        // Synchronize Genres collection instead of replacing it to avoid EF Core many-to-many churn.
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
            album.CoverArtUri = metadata.CoverArtUri;
        return Task.CompletedTask;
    }

    private async Task<Song?> AddSongWithDetailsAsync(MusicDbContext context, Guid folderId, SongFileMetadata metadata)
    {
        try
        {
            var trackArtistNames = metadata.Artists.Any() ? metadata.Artists : new List<string> { Artist.UnknownArtistName };
            var albumArtistNames = metadata.AlbumArtists.Any() ? metadata.AlbumArtists : trackArtistNames;

            var trackArtists = new List<Artist>();
            foreach (var name in trackArtistNames)
            {
                trackArtists.Add(await GetOrCreateArtistAsync(context, name).ConfigureAwait(false));
            }

            var albumArtists = new List<Artist>();
            foreach (var name in albumArtistNames)
            {
                albumArtists.Add(await GetOrCreateArtistAsync(context, name).ConfigureAwait(false));
            }

            var album = !string.IsNullOrWhiteSpace(metadata.Album)
                ? await GetOrCreateAlbumAsync(context, metadata.Album, albumArtists, metadata.Year).ConfigureAwait(false)
                : null;

            var genres = await EnsureGenresExistAsync(context, metadata.Genres).ConfigureAwait(false);

            var directoryPath = _fileSystem.GetDirectoryName(metadata.FilePath) ?? string.Empty;

            var existingSong = await context.Songs
                .Include(s => s.Genres)
                .Include(s => s.SongArtists)
                .FirstOrDefaultAsync(s => s.FilePath == metadata.FilePath).ConfigureAwait(false);
            var song = existingSong ?? new Song();

            song.FilePath = metadata.FilePath;
            song.DirectoryPath = directoryPath;
            song.Title = metadata.Title;
            song.Duration = metadata.Duration;
            song.AlbumArtUriFromTrack = metadata.CoverArtUri;
            song.LightSwatchId = metadata.LightSwatchId;
            song.DarkSwatchId = metadata.DarkSwatchId;
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
            song.LrcFilePath = metadata.LrcFilePath;
            song.AlbumId = album?.Id;

            // Synchronize SongArtists collection
            var newSongArtists = trackArtists.Select((a, index) => new { ArtistId = a.Id, Order = index }).ToList();
            var currentSongArtists = song.SongArtists.OrderBy(sa => sa.Order).ToList();

            if (currentSongArtists.Count != newSongArtists.Count ||
                currentSongArtists.Zip(newSongArtists, (c, n) => c.ArtistId == n.ArtistId && c.Order == n.Order).Any(val => !val))
            {
                song.SongArtists.Clear();
                foreach (var nsa in newSongArtists)
                {
                    var artist = trackArtists[nsa.Order];
                    song.SongArtists.Add(new SongArtist { ArtistId = nsa.ArtistId, Artist = artist, Order = nsa.Order });
                }
            }

            // Synchronize Genres collection
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

            if (existingSong == null)
            {
                song.DateAddedToLibrary = DateTime.UtcNow;
                context.Songs.Add(song);
            }

            if (album is not null && string.IsNullOrEmpty(album.CoverArtUri) &&
                !string.IsNullOrEmpty(metadata.CoverArtUri))
                album.CoverArtUri = metadata.CoverArtUri;

            return song;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to prepare song entity for {FilePath}.", metadata.FilePath);
            return null;
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
            
            // Read, process, and save the image
            var originalBytes = await _fileSystem.ReadAllBytesAsync(localFilePath).ConfigureAwait(false);
            var processedBytes = await _imageProcessor.ProcessImageBytesAsync(originalBytes).ConfigureAwait(false);
            await ImageStorageHelper.SaveImageBytesAsync(_fileSystem, cachePath, artistId.ToString(), ".custom", processedBytes).ConfigureAwait(false);

            // Find the file we just saved to get the correct path
            var newPath = ImageStorageHelper.FindImage(_fileSystem, cachePath, artistId.ToString(), ".custom");

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

            // Remove all variants of custom images
            ImageStorageHelper.DeleteImage(_fileSystem, cachePath, artistId.ToString(), ".custom");

            // Look for a fetched image as fallback
            var fetchedPath = ImageStorageHelper.FindImage(_fileSystem, cachePath, artistId.ToString(), ".fetched");

            if (!string.IsNullOrEmpty(fetchedPath))
            {
                artist.LocalImageCachePath = fetchedPath;
                _logger.LogInformation("Reverted to fetched image for artist {ArtistId}: {Path}", artistId, fetchedPath);
            }
            else
            {
                artist.LocalImageCachePath = null;
                _logger.LogInformation("No fallback fetched image found for artist {ArtistId}. Cleared image path.", artistId);
            }

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

    private async Task FetchAndUpdateArtistFromRemoteAsync(MusicDbContext context, Artist artist, CancellationToken cancellationToken = default)
    {
        var wasMetadataFoundAndUpdated = false;

        // Get enabled metadata providers in priority order
        var enabledProviders = await _settingsService.GetEnabledServiceProvidersAsync(Models.ServiceCategory.Metadata).ConfigureAwait(false);
        
        if (enabledProviders.Count == 0)
        {
            _logger.LogDebug("No metadata providers enabled. Skipping remote fetch for artist '{ArtistName}'.", artist.Name);
            return;
        }

        _logger.LogDebug("Using metadata providers for '{ArtistName}': {Providers}", 
            artist.Name, string.Join(", ", enabledProviders.Select(p => p.Id)));

        var enabledIds = enabledProviders.Select(p => p.Id).ToHashSet();

        // Initialize task storage
        var tasks = new Dictionary<string, Task<(string? ImageUrl, string? Biography)>>();

        // Pre-warm API keys in parallel for enabled providers only.
        // Start warmup FIRST so it runs concurrently with all provider tasks.
        // Warmup failures are acceptable - the actual service calls handle missing keys gracefully.
        var warmupTasks = new List<Task>();
        if (enabledIds.Contains(ServiceProviderIds.TheAudioDb))
            warmupTasks.Add(WarmupApiKeyAsync(ServiceProviderIds.TheAudioDb, cancellationToken));
        if (enabledIds.Contains(ServiceProviderIds.FanartTv))
            warmupTasks.Add(WarmupApiKeyAsync(ServiceProviderIds.FanartTv, cancellationToken));
        if (enabledIds.Contains(ServiceProviderIds.Spotify))
            warmupTasks.Add(WarmupApiKeyAsync(ServiceProviderIds.Spotify, cancellationToken));
        if (enabledIds.Contains(ServiceProviderIds.LastFm))
            warmupTasks.Add(WarmupApiKeyAsync(ServiceProviderIds.LastFm, cancellationToken));

        // Start MusicBrainz lookup in parallel with warmup (don't block other tasks)
        var mbid = artist.MusicBrainzId;
        Task<string?>? musicBrainzTask = null;
        if (string.IsNullOrEmpty(mbid) && enabledIds.Contains(ServiceProviderIds.MusicBrainz))
        {
            musicBrainzTask = _musicBrainzService.SearchArtistAsync(artist.Name, cancellationToken);
        }

        // Start MBID-independent tasks immediately (in parallel with warmup and MusicBrainz)
        if (enabledIds.Contains(ServiceProviderIds.Spotify))
            tasks[ServiceProviderIds.Spotify] = FetchFromSpotifyAsync(artist.Name, cancellationToken);
        if (enabledIds.Contains(ServiceProviderIds.LastFm))
            tasks[ServiceProviderIds.LastFm] = FetchFromLastFmAsync(artist.Name, cancellationToken);

        // Wait for MusicBrainz to complete (if started)
        // Note: Warmup tasks continue in background - they're best-effort cache warming
        if (musicBrainzTask != null)
        {
            var resolvedMbid = await musicBrainzTask.ConfigureAwait(false);
            if (!string.IsNullOrEmpty(resolvedMbid))
            {
                mbid = resolvedMbid;
                artist.MusicBrainzId = mbid;
                wasMetadataFoundAndUpdated = true;
                _logger.LogInformation("Resolved MusicBrainz ID {MBID} for artist '{ArtistName}'", mbid, artist.Name);
            }
        }

        // Start MBID-dependent tasks now that we have (or don't have) the MBID.
        // NOTE: TheAudioDB and Fanart.tv require a MusicBrainz ID to function.
        // If MusicBrainz is disabled and the artist has no existing MBID, these providers will be skipped.
        if (!string.IsNullOrEmpty(mbid))
        {
            if (enabledIds.Contains(ServiceProviderIds.TheAudioDb))
                tasks[ServiceProviderIds.TheAudioDb] = FetchFromTheAudioDbAsync(mbid, cancellationToken);
            if (enabledIds.Contains(ServiceProviderIds.FanartTv))
                tasks[ServiceProviderIds.FanartTv] = FetchFromFanartTvAsync(mbid, cancellationToken);
        }

        // Wait for warmup tasks to complete (these have been running in parallel with provider tasks above).
        // This ensures API key cache is populated before we evaluate results, reducing latency for the awaits below.
        await Task.WhenAll(warmupTasks).ConfigureAwait(false);

        // Evaluate results in priority order for image and biography
        string? finalImageUrl = null;
        string? finalBiography = null;

        foreach (var provider in enabledProviders)
        {
            if (!tasks.TryGetValue(provider.Id, out var task)) continue;
            
            try
            {
                // Sequential await in priority order. If a higher priority task is still running, 
                // we wait for it. If it finishes and has data, we break early and ignore lower priority ones.
                var (imageUrl, biography) = await task.ConfigureAwait(false);
                
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
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Metadata provider {Provider} failed for '{ArtistName}'.", provider.DisplayName, artist.Name);
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
                        var logger = (ILogger<LibraryService>)state!;
                        logger.LogDebug(t.Exception?.InnerException, "Metadata provider task faulted (ignored, already resolved)");
                    }
                },
                _logger,
                TaskContinuationOptions.NotOnRanToCompletion);
        }

        // Apply Biography
        if (!string.IsNullOrEmpty(finalBiography))
        {
            artist.Biography = finalBiography;
            wasMetadataFoundAndUpdated = true;
        }

        // Download and cache the best image found
        if (!string.IsNullOrEmpty(finalImageUrl))
        {
            // Skip fetching metadata image if a custom image is already set
            if (artist.LocalImageCachePath == null || !artist.LocalImageCachePath.Contains(".custom."))
            {
                var downloadedPath =
                    await DownloadAndCacheArtistImageAsync(artist, new Uri(finalImageUrl), cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(downloadedPath))
                {
                    artist.LocalImageCachePath = downloadedPath;
                    wasMetadataFoundAndUpdated = true;
                }
            }
        }

        artist.MetadataLastCheckedUtc = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (wasMetadataFoundAndUpdated)
            ArtistMetadataUpdated?.Invoke(this,
                new ArtistMetadataUpdatedEventArgs(artist.Id, artist.LocalImageCachePath));
    }

    /// <summary>
    ///     Warms up the API key cache for a specific service. Failures are swallowed
    ///     as this is a best-effort optimization - actual service calls handle missing keys.
    /// </summary>
    private async Task WarmupApiKeyAsync(string serviceKey, CancellationToken cancellationToken)
    {
        try
        {
            await _apiKeyService.GetApiKeyAsync(serviceKey, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Warmup failures are acceptable - the actual service call will handle missing keys gracefully
        }
    }

    private async Task<(string? ImageUrl, string? Biography)> FetchFromTheAudioDbAsync(string mbid, CancellationToken ct)
    {
        var result = await _theAudioDbService.GetArtistMetadataAsync(mbid, ct).ConfigureAwait(false);
        if (result.Status == ServiceResultStatus.Success && result.Data is not null)
        {
            // Prefer square thumbnail over landscape fanart for round frame display
            var url = result.Data.ThumbUrl ?? result.Data.FanartUrl;
            return (url, result.Data.Biography);
        }
        return (null, null);
    }

    private async Task<(string? ImageUrl, string? Biography)> FetchFromFanartTvAsync(string mbid, CancellationToken ct)
    {
        var result = await _fanartTvService.GetArtistImagesAsync(mbid, ct).ConfigureAwait(false);
        if (result.Status == ServiceResultStatus.Success && result.Data is not null)
        {
            // Prefer square thumbnail over landscape background for round frame display
            var url = result.Data.ThumbUrl ?? result.Data.BackgroundUrl;
            return (url, null); // Fanart.tv doesn't provide biography
        }
        return (null, null);
    }

    private async Task<(string? ImageUrl, string? Biography)> FetchFromSpotifyAsync(string artistName, CancellationToken ct)
    {
        var result = await _spotifyService.GetArtistImageUrlAsync(artistName, ct).ConfigureAwait(false);
        if (result.Status == ServiceResultStatus.Success && result.Data?.ImageUrl is not null)
        {
            return (result.Data.ImageUrl, null); // Spotify doesn't provide biography
        }
        return (null, null);
    }

    private async Task<(string? ImageUrl, string? Biography)> FetchFromLastFmAsync(string artistName, CancellationToken ct)
    {
        var result = await _lastFmService.GetArtistInfoAsync(artistName, ct).ConfigureAwait(false);
        if (result.Status == ServiceResultStatus.Success && result.Data is not null)
        {
            return (result.Data.ImageUrl, result.Data.Biography);
        }
        return (null, null);
    }

    private Task<string?> DownloadAndCacheArtistImageAsync(Artist artist, Uri imageUrl, CancellationToken cancellationToken)
    {
        var lazyTask = _artistImageProcessingTasks.GetOrAdd(artist.Id, _ =>
            new Lazy<Task<string?>>(() =>
            {
                var localPath = _fileSystem.Combine(_pathConfig.ArtistImageCachePath, $"{artist.Id}.fetched.jpg");
                return DownloadAndWriteImageInternalAsync(localPath, imageUrl, cancellationToken);
            })
        );

        try
        {
            return lazyTask.Value;
        }
        catch (Exception ex)
        {
            // Prevent retry storms by removing failed attempts from cache
            _logger.LogError(ex, "Artist image download failed for artist '{ArtistName}'.", artist.Name);
            return Task.FromResult<string?>(null);
        }
        finally
        {
            _artistImageProcessingTasks.TryRemove(artist.Id, out _);
        }
    }

    private async Task<string?> DownloadAndWriteImageInternalAsync(string localPath, Uri imageUrl, CancellationToken cancellationToken)
    {
        if (_fileSystem.FileExists(localPath)) return localPath;

        var operationName = $"Image download from {imageUrl}";
        using var httpClient = _httpClientFactory.CreateClient("ImageDownloader");

        var result = await HttpRetryHelper.ExecuteWithRetryAsync<string>(
            async attempt =>
            {
                _logger.LogDebug("Downloading artist image (Attempt {Attempt}/3): {ImageUrl}", attempt, imageUrl);

                using var response = await httpClient.GetAsync(imageUrl, cancellationToken).ConfigureAwait(false);
                
                if (response.IsSuccessStatusCode)
                {
                    var imageBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                    // Process image to standardized size (600px max, preserves aspect ratio)
                    var processedBytes = await _imageProcessor.ProcessImageBytesAsync(imageBytes).ConfigureAwait(false);
                    await _fileSystem.WriteAllBytesAsync(localPath, processedBytes).ConfigureAwait(false);
                    return RetryResult<string>.Success(localPath);
                }

                if (HttpRetryHelper.IsRetryableStatusCode(response.StatusCode))
                    return RetryResult<string>.TransientFailure();

                return RetryResult<string>.SuccessEmpty();
            },
            _logger,
            operationName,
            cancellationToken,
            3
        ).ConfigureAwait(false);

        return result;
    }

    private async Task<Artist> GetOrCreateArtistAsync(MusicDbContext context, string? name)
    {
        var normalizedName = string.IsNullOrWhiteSpace(name) ? Artist.UnknownArtistName : name.Trim();

        await _artistCreationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Check tracked entities (including Added state)
            var trackedArtist = context.ChangeTracker.Entries<Artist>()
                .FirstOrDefault(e =>
                    (e.State == EntityState.Added || e.State == EntityState.Unchanged) &&
                    e.Entity.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase))
                ?.Entity;

            if (trackedArtist is not null) return trackedArtist;

            // Check database
            var dbArtist = await context.Artists.FirstOrDefaultAsync(a => a.Name == normalizedName).ConfigureAwait(false);
            if (dbArtist is not null) return dbArtist;

            // Create new artist (but don't save yet - let the calling code control the transaction)
            var newArtist = new Artist { Name = normalizedName };
            context.Artists.Add(newArtist);
            return newArtist;
        }
        finally
        {
            _artistCreationLock.Release();
        }
    }

    private async Task<Album> GetOrCreateAlbumAsync(MusicDbContext context, string title, List<Artist> artists, int? year)
    {
        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? Album.UnknownAlbumName : title.Trim();
        var primaryArtistId = artists.FirstOrDefault()?.Id;

        await _albumCreationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Check tracked entities (including Added and Unchanged states)
            var trackedAlbum = context.ChangeTracker.Entries<Album>()
                .FirstOrDefault(e => (e.State == EntityState.Added || e.State == EntityState.Unchanged) &&
                                     e.Entity.Title.Equals(normalizedTitle, StringComparison.OrdinalIgnoreCase) &&
                                     e.Entity.AlbumArtists.Any(aa => aa.Order == 0 && aa.ArtistId == primaryArtistId))
                ?.Entity;

            if (trackedAlbum is not null) return trackedAlbum;

            // Check database
            var album = await context.Albums
                .Include(a => a.AlbumArtists)
                .FirstOrDefaultAsync(a => a.Title == normalizedTitle &&
                                          a.AlbumArtists.Any(aa => aa.Order == 0 && aa.ArtistId == primaryArtistId))
                .ConfigureAwait(false);

            if (album is not null)
            {
                // Update year if missing
                if (album.Year is null && year.HasValue) 
                {
                    album.Year = year;
                }

                // Check if artists changed and update if needed
                var incomingArtistIds = artists.Select(a => a.Id).ToList();
                var existingArtistIds = album.AlbumArtists.OrderBy(aa => aa.Order).Select(aa => aa.ArtistId).ToList();

                if (!existingArtistIds.SequenceEqual(incomingArtistIds))
                {
                    album.AlbumArtists.Clear();
                    for (int i = 0; i < artists.Count; i++)
                    {
                        album.AlbumArtists.Add(new AlbumArtist { ArtistId = artists[i].Id, Artist = artists[i], Order = i });
                    }
                }

                // Don't save here - let the calling code control the transaction
                return album;
            }

            // Create new album (but don't save yet - let the calling code control the transaction)
            var newAlbum = new Album
            {
                Title = normalizedTitle,
                Year = year
            };

            for (int i = 0; i < artists.Count; i++)
            {
                newAlbum.AlbumArtists.Add(new AlbumArtist { ArtistId = artists[i].Id, Order = i });
            }
            context.Albums.Add(newAlbum);
            return newAlbum;
        }
        finally
        {
            _albumCreationLock.Release();
        }
    }

    private async Task<List<Genre>> EnsureGenresExistAsync(MusicDbContext context, IEnumerable<string>? genreNames)
    {
        if (genreNames is null) return [];

        var distinctNames = genreNames
            .Select(g => g.Trim())
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinctNames.Count == 0) return [];

        var finalGenres = new List<Genre>(distinctNames.Count);

        var existingDbGenres = await context.Genres
            .Where(g => distinctNames.Contains(g.Name))
            .ToListAsync().ConfigureAwait(false);

        var trackedGenres = context.ChangeTracker.Entries<Genre>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity)
            .ToList();

        var existingGenresMap = existingDbGenres.ToDictionary(g => g.Name, StringComparer.OrdinalIgnoreCase);
        var trackedGenresMap = trackedGenres.ToDictionary(g => g.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var name in distinctNames)
            if (existingGenresMap.TryGetValue(name, out var genre) || trackedGenresMap.TryGetValue(name, out genre))
            {
                finalGenres.Add(genre);
            }
            else
            {
                var newGenre = new Genre { Name = name };
                context.Genres.Add(newGenre);
                finalGenres.Add(newGenre);
                trackedGenresMap.Add(name, newGenre);
            }

        return finalGenres;
    }



    private async Task CleanUpOrphanedEntitiesAsync(MusicDbContext context,
        CancellationToken cancellationToken = default)
    {
        var deletedAny = true;
        while (deletedAny)
        {
            var orphanedSubFolders = await context.Folders
                .AsNoTracking()
                .Where(f => f.ParentFolderId != null) // Only subfolders, never root folders
                .Where(f => !f.SubFolders.Any()) // No child folders
                .Select(f => new { f.Id, f.Path })
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            if (!orphanedSubFolders.Any())
            {
                deletedAny = false;
                break;
            }

            var folderPaths = orphanedSubFolders.Select(f => f.Path).ToList();

            // Prevent deletion of folders containing songs in subdirectories
            var pathsWithSongsOrDescendants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var folderPath in folderPaths)
            {
                var normalizedPath = folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                // Check if any songs exist in this folder or any subdirectory
                var hasSongs = await context.Songs
                    .AnyAsync(s => s.DirectoryPath == normalizedPath ||
                                   s.DirectoryPath.StartsWith(normalizedPath + Path.DirectorySeparatorChar) ||
                                   s.DirectoryPath.StartsWith(normalizedPath + Path.AltDirectorySeparatorChar),
                        cancellationToken).ConfigureAwait(false);

                if (hasSongs) pathsWithSongsOrDescendants.Add(folderPath);
            }

            var foldersToDelete = orphanedSubFolders
                .Where(f => !pathsWithSongsOrDescendants.Contains(f.Path))
                .Select(f => f.Id)
                .ToList();

            if (foldersToDelete.Any())
                await context.Folders
                    .Where(f => foldersToDelete.Contains(f.Id))
                    .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            else
                deletedAny = false;
        }

        await context.Albums.Where(a => !a.Songs.Any()).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        var orphanedArtists = await context.Artists
            .AsNoTracking()
            .Where(a => !a.SongArtists.Any() && !a.AlbumArtists.Any())
            .Select(a => new { a.Id, a.LocalImageCachePath })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (orphanedArtists.Any())
        {
            var idsToDelete = orphanedArtists.Select(a => a.Id).ToList();
            await context.Artists.Where(a => idsToDelete.Contains(a.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            
            foreach (var artist in orphanedArtists)
            {
                // Delete all possible image variants for the orphaned artist
                ImageStorageHelper.DeleteImage(_fileSystem, _pathConfig.ArtistImageCachePath, artist.Id.ToString(), ".fetched");
                ImageStorageHelper.DeleteImage(_fileSystem, _pathConfig.ArtistImageCachePath, artist.Id.ToString(), ".custom");
                
                // Cleanup legacy legacy as well
                ImageStorageHelper.DeleteImage(_fileSystem, _pathConfig.ArtistImageCachePath, artist.Id.ToString(), "");
            }
        }

        // Aggressive Cleanup: Remove any files in the artist image cache that don't match the new naming convention
        // or are for artists that no longer exist.
        try
        {
            var cachePath = _pathConfig.ArtistImageCachePath;
            if (_fileSystem.DirectoryExists(cachePath))
            {
                var allArtistIds = await context.Artists.Select(a => a.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
                var artistIdSet = new HashSet<Guid>(allArtistIds);
                // 3 argument overload not available on interface, using EnumerateFiles
                var files = _fileSystem.EnumerateFiles(cachePath, "*.*", SearchOption.TopDirectoryOnly);

                foreach (var file in files)
                {
                    var fileName = _fileSystem.GetFileName(file);
                    if (string.IsNullOrEmpty(fileName)) continue;

                    bool shouldDelete = false;

                    // 1. Check if it's a legacy .jpg file or has an invalid suffix
                    // We only want to keep: {id}.fetched.* OR {id}.custom.*
                    if (!fileName.Contains(".fetched.") && !fileName.Contains(".custom."))
                    {
                        shouldDelete = true;
                    }
                    else if (fileName.Contains(".fetched.") || fileName.Contains(".custom."))
                    {
                        // 2. Extract Guid and check if artist exists
                        var guidPart = fileName.Split('.')[0];
                        if (Guid.TryParse(guidPart, out var artistId))
                        {
                            if (!artistIdSet.Contains(artistId))
                            {
                                shouldDelete = true;
                            }
                        }
                        else
                        {
                            shouldDelete = true;
                        }
                    }
                    else
                    {
                        // Not a jpg file we manage
                        shouldDelete = true;
                    }

                    if (shouldDelete)
                    {
                        try
                        {
                            _fileSystem.DeleteFile(file);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to delete invalid/legacy artist image file {FilePath}.", file);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during aggressive artist image cache cleanup.");
        }

        await context.Genres.Where(g => !g.Songs.Any()).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Creates a projection of Song entities that excludes heavy, rarely-used fields (Lyrics, Comment, Copyright)
    ///     from the query results. This reduces memory usage significantly when loading song lists.
    /// </summary>
    /// <remarks>
    ///     The excluded fields are:
    ///     <list type="bullet">
    ///         <item><description>Lyrics - Up to 50,000 characters of embedded lyrics text</description></item>
    ///         <item><description>Comment - Up to 1,000 characters of comments</description></item>
    ///         <item><description>Copyright - Up to 1,000 characters of copyright info</description></item>
    ///     </list>
    ///     Collection navigation properties (Genres, PlaylistSongs, ListenHistory) are also excluded as they
    ///     are rarely needed in list views and cause issues with EF Core projections.
    ///     For methods that need the full song data (e.g., lyrics display), use GetSongByIdAsync.
    /// </remarks>
    private static IQueryable<Song> ExcludeHeavyFields(IQueryable<Song> query)
    {
        return query.Select(s => new Song
        {
            Id = s.Id,
            Title = s.Title,
            AlbumId = s.AlbumId,
            Album = s.Album,
            // SongArtists = s.SongArtists, -- EXCLUDED for performance in list views. Use GetSongByIdAsync for navigation.
            Composer = s.Composer,
            FolderId = s.FolderId,
            Folder = s.Folder,
            DurationTicks = s.DurationTicks,
            AlbumArtUriFromTrack = s.AlbumArtUriFromTrack,
            FilePath = s.FilePath,
            DirectoryPath = s.DirectoryPath,
            Year = s.Year,
            TrackNumber = s.TrackNumber,
            TrackCount = s.TrackCount,
            DiscNumber = s.DiscNumber,
            DiscCount = s.DiscCount,
            SampleRate = s.SampleRate,
            Bitrate = s.Bitrate,
            Channels = s.Channels,
            DateAddedToLibrary = s.DateAddedToLibrary,
            FileCreatedDate = s.FileCreatedDate,
            FileModifiedDate = s.FileModifiedDate,
            LightSwatchId = s.LightSwatchId,
            DarkSwatchId = s.DarkSwatchId,
            Rating = s.Rating,
            IsLoved = s.IsLoved,
            PlayCount = s.PlayCount,
            SkipCount = s.SkipCount,
            LastPlayedDate = s.LastPlayedDate,
            // Lyrics - EXCLUDED (up to 50KB per song)
            // Comment - EXCLUDED (up to 1KB per song)
            // Copyright - EXCLUDED (up to 1KB per song)
            LrcFilePath = s.LrcFilePath,
            LyricsLastCheckedUtc = s.LyricsLastCheckedUtc,
            Bpm = s.Bpm,
            ReplayGainTrackGain = s.ReplayGainTrackGain,
            ReplayGainTrackPeak = s.ReplayGainTrackPeak,
            Grouping = s.Grouping,
            Conductor = s.Conductor,
            MusicBrainzTrackId = s.MusicBrainzTrackId,
            MusicBrainzReleaseId = s.MusicBrainzReleaseId,
            ArtistName = s.ArtistName,
            PrimaryArtistName = s.PrimaryArtistName
            // Collection navigations excluded for EF Core compatibility:
            // Genres, PlaylistSongs, ListenHistory
        });
    }

    private IOrderedQueryable<Song> ApplySongSortOrder(IQueryable<Song> query, SongSortOrder sortOrder)
    {
        return sortOrder switch
        {
            SongSortOrder.TitleAsc => query.OrderBy(s => s.Title).ThenBy(s => s.Id),
            SongSortOrder.TitleDesc => query.OrderByDescending(s => s.Title).ThenByDescending(s => s.Id),
            SongSortOrder.YearAsc => query.OrderBy(s => s.Year)
                .ThenBy(s => s.PrimaryArtistName)
                .ThenBy(s => s.Album != null ? s.Album.Title : string.Empty)
                .ThenBy(s => s.DiscNumber ?? 0)
                .ThenBy(s => s.TrackNumber)
                .ThenBy(s => s.Title)
                .ThenBy(s => s.Id),
            SongSortOrder.YearDesc => query.OrderByDescending(s => s.Year)
                .ThenByDescending(s => s.PrimaryArtistName)
                .ThenByDescending(s => s.Album != null ? s.Album.Title : string.Empty)
                .ThenByDescending(s => s.DiscNumber ?? 0)
                .ThenByDescending(s => s.TrackNumber)
                .ThenByDescending(s => s.Title)
                .ThenByDescending(s => s.Id),
            SongSortOrder.AlbumAsc => query
                .OrderBy(s => s.Album != null ? s.Album.Title : string.Empty)
                .ThenBy(s => s.DiscNumber ?? 0)
                .ThenBy(s => s.TrackNumber)
                .ThenBy(s => s.Title)
                .ThenBy(s => s.Id),
            SongSortOrder.AlbumDesc => query
                .OrderByDescending(s => s.Album != null ? s.Album.Title : string.Empty)
                .ThenByDescending(s => s.DiscNumber ?? 0)
                .ThenByDescending(s => s.TrackNumber)
                .ThenByDescending(s => s.Title)
                .ThenByDescending(s => s.Id),
            SongSortOrder.TrackNumberAsc => query
                .OrderBy(s => s.Album != null ? s.Album.Title : string.Empty)
                .ThenBy(s => s.DiscNumber ?? 0)
                .ThenBy(s => s.TrackNumber)
                .ThenBy(s => s.Title)
                .ThenBy(s => s.Id),
            SongSortOrder.TrackNumberDesc => query
                .OrderByDescending(s => s.Album != null ? s.Album.Title : string.Empty)
                .ThenByDescending(s => s.DiscNumber ?? 0)
                .ThenByDescending(s => s.TrackNumber)
                .ThenByDescending(s => s.Title)
                .ThenByDescending(s => s.Id),
            SongSortOrder.ArtistAsc => query.OrderBy(s => s.PrimaryArtistName)
                .ThenBy(s => s.Album != null ? s.Album.Title : string.Empty)
                .ThenBy(s => s.DiscNumber ?? 0)
                .ThenBy(s => s.TrackNumber)
                .ThenBy(s => s.Title)
                .ThenBy(s => s.Id),
            SongSortOrder.ArtistDesc => query.OrderByDescending(s => s.PrimaryArtistName)
                .ThenByDescending(s => s.Album != null ? s.Album.Title : string.Empty)
                .ThenByDescending(s => s.DiscNumber ?? 0)
                .ThenByDescending(s => s.TrackNumber)
                .ThenByDescending(s => s.Title)
                .ThenByDescending(s => s.Id),
            _ => query.OrderBy(s => s.Title).ThenBy(s => s.Id)
        };
    }

    private IQueryable<Song> BuildSongSearchQuery(MusicDbContext context, string searchTerm)
    {
        // Leading wildcards prevent index usage; consider full-text search for large datasets
        var term = $"%{searchTerm}%";
        return context.Songs
            .Where(s =>
                EF.Functions.Like(s.Title, term)
                || EF.Functions.Like(s.ArtistName, term)
                || s.SongArtists.Any(sa => EF.Functions.Like(sa.Artist.Name, term))
                || (s.Album != null && (EF.Functions.Like(s.Album.Title, term) || EF.Functions.Like(s.Album.ArtistName, term) || s.Album.AlbumArtists.Any(aa => EF.Functions.Like(aa.Artist.Name, term))))
                || (s.Year != null && EF.Functions.Like(s.Year.ToString(), term))
                || s.Genres.Any(g => EF.Functions.Like(g.Name, term))
            );
    }

    private IQueryable<Artist> BuildArtistSearchQuery(MusicDbContext context, string searchTerm)
    {
        return context.Artists.Where(a => EF.Functions.Like(a.Name, $"%{searchTerm}%"));
    }

    private IQueryable<Album> BuildAlbumSearchQuery(MusicDbContext context, string searchTerm)
    {
        var term = $"%{searchTerm}%";
        return context.Albums
            .Where(al => EF.Functions.Like(al.Title, term)
                         || EF.Functions.Like(al.ArtistName, term)
                         || al.AlbumArtists.Any(aa => EF.Functions.Like(aa.Artist.Name, term)));
    }

    private bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        var innerMessage = ex.InnerException?.Message ?? string.Empty;
        return innerMessage.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)
               || innerMessage.Contains("Violation of UNIQUE KEY constraint", StringComparison.OrdinalIgnoreCase)
               || innerMessage.Contains("duplicate key value violates unique constraint",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static void SanitizePaging(ref int pageNumber, ref int pageSize)
    {
        pageNumber = Math.Max(1, pageNumber);
        pageSize = Math.Max(1, pageSize);
    }

    #endregion

    private void OnFetchOnlineMetadataEnabledChanged(bool isEnabled)
    {
        if (isEnabled) return;

        // Use synchronous wait with short timeout since this is a settings change handler
        if (!_metadataFetchSemaphore.Wait(100))
        {
            // If we can't acquire quickly, the background fetch is active - just cancel the token
            if (!_metadataFetchCts.IsCancellationRequested)
            {
                _logger.LogInformation("Fetch online metadata disabled. Cancelling background fetch.");
                _metadataFetchCts.Cancel();
            }
            return;
        }

        try
        {
            if (_disposed) return;
            if (_isMetadataFetchRunning && !_metadataFetchCts.IsCancellationRequested)
            {
                _logger.LogInformation("Fetch online metadata disabled. Cancelling background fetch.");
                _metadataFetchCts.Cancel();
            }
        }
        finally
        {
            _metadataFetchSemaphore.Release();
        }
    }

    #region IDisposable Implementation

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            _settingsService.FetchOnlineMetadataEnabledChanged -= OnFetchOnlineMetadataEnabledChanged;

            // Cancel and dispose CTS - don't wait for semaphore as we're disposing
            _metadataFetchCts.Cancel();
            _metadataFetchCts.Dispose();
            _disposed = true;

            _replayGainScanCts?.Cancel();
            _replayGainScanCts?.Dispose();
            _scanSemaphore.Dispose();
            _artistCreationLock.Dispose();
            _albumCreationLock.Dispose();
            _metadataFetchSemaphore.Dispose();
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

    #endregion
}