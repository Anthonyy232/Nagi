using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nagi.Core.Data;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Manages all aspects of the music library, including file scanning, metadata, and database operations.
///     This service is designed to be a singleton and is internally thread-safe.
/// </summary>
public class LibraryService : ILibraryService, ILibraryReader, IDisposable
{
    private const string UnknownArtistName = "Unknown Artist";
    private const string UnknownAlbumName = "Unknown Album";

    private static readonly HashSet<string> MusicFileExtensions = new(new[]
    {
        ".aa", ".aax", ".aac", ".aiff", ".ape", ".dsf", ".flac",
        ".m4a", ".m4b", ".m4p", ".mp3", ".mpc", ".mpp", ".ogg",
        ".oga", ".opus", ".wav", ".wma", ".wv", ".webm",
        ".mkv", ".ogv", ".avi", ".wmv", ".asf", ".mp4", ".m4v",
        ".mpeg", ".mpg", ".mpe", ".mpv", ".m2v"
    }, StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<Guid, Lazy<Task<string?>>> _artistImageProcessingTasks = new();

    private readonly IDbContextFactory<MusicDbContext> _contextFactory;
    private readonly IFileSystemService _fileSystem;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILastFmMetadataService _lastFmService;
    private readonly ILogger<LibraryService> _logger;
    private readonly object _metadataFetchLock = new();
    private readonly IMetadataService _metadataService;
    private readonly IPathConfiguration _pathConfig;
    private readonly SemaphoreSlim _scanSemaphore = new(1, 1);
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ISpotifyService _spotifyService;
    private bool _disposed;
    private volatile bool _isMetadataFetchRunning;
    private CancellationTokenSource _metadataFetchCts;

    public LibraryService(
        IDbContextFactory<MusicDbContext> contextFactory,
        IFileSystemService fileSystem,
        IMetadataService metadataService,
        ILastFmMetadataService lastFmService,
        ISpotifyService spotifyService,
        IHttpClientFactory httpClientFactory,
        IServiceScopeFactory serviceScopeFactory,
        IPathConfiguration pathConfig,
        ILogger<LibraryService> logger)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _lastFmService = lastFmService ?? throw new ArgumentNullException(nameof(lastFmService));
        _spotifyService = spotifyService ?? throw new ArgumentNullException(nameof(spotifyService));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
        _pathConfig = pathConfig ?? throw new ArgumentNullException(nameof(pathConfig));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metadataFetchCts = new CancellationTokenSource();
    }

    /// <summary>
    ///     Occurs when an artist's metadata (e.g., biography, image) has been successfully updated from a remote source.
    /// </summary>
    public event EventHandler<ArtistMetadataUpdatedEventArgs>? ArtistMetadataUpdated;

    #region Data Reset

    /// <inheritdoc />
    public async Task ClearAllLibraryDataAsync()
    {
        _logger.LogInformation("Starting to clear all library data and cache files.");
        _metadataFetchCts.Cancel();
        await Task.Delay(250);

        await using var context = await _contextFactory.CreateDbContextAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();

        try
        {
            await context.PlaylistSongs.ExecuteDeleteAsync();
            await context.ListenHistory.ExecuteDeleteAsync();
            await context.Songs.ExecuteDeleteAsync();
            await context.Playlists.ExecuteDeleteAsync();
            await context.Albums.ExecuteDeleteAsync();
            await context.Artists.ExecuteDeleteAsync();
            await context.Genres.ExecuteDeleteAsync();
            await context.Folders.ExecuteDeleteAsync();

            await transaction.CommitAsync();
            _logger.LogInformation("Successfully deleted all data from the database.");
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
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

        await using var context = await _contextFactory.CreateDbContextAsync();

        var normalizedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var existingFolder = await context.Folders.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Path == normalizedPath);
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
        await context.SaveChangesAsync();
        return folder;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveFolderAsync(Guid folderId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var folder = await context.Folders.FindAsync(folderId);
        if (folder is null)
        {
            _logger.LogWarning("Could not remove folder: Folder with ID {FolderId} not found.", folderId);
            return false;
        }

        List<string> albumArtPathsToDelete;
        List<string> lrcPathsToDelete;

        await using var transaction = await context.Database.BeginTransactionAsync();
        try
        {
            if (folder.ParentFolderId == null)
            {
                var songsInFolder = context.Songs.Where(s => s.FolderId == folderId);

                albumArtPathsToDelete = await songsInFolder
                    .Where(s => s.AlbumArtUriFromTrack != null)
                    .Select(s => s.AlbumArtUriFromTrack!)
                    .Distinct()
                    .ToListAsync();

                lrcPathsToDelete = await songsInFolder
                    .Where(s => s.LrcFilePath != null)
                    .Select(s => s.LrcFilePath!)
                    .ToListAsync();

                await songsInFolder.ExecuteDeleteAsync();
            }
            else
            {
                albumArtPathsToDelete = new List<string>();
                lrcPathsToDelete = new List<string>();
            }

            context.Folders.Remove(folder);
            await context.SaveChangesAsync();

            await CleanUpOrphanedEntitiesAsync(context);

            await transaction.CommitAsync();
            _logger.LogInformation("Removed folder '{FolderName}' and associated data.", folder.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Folder removal for ID {FolderId} failed and was rolled back.", folderId);
            await transaction.RollbackAsync();
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
        await using var context = await _contextFactory.CreateDbContextAsync();

        try
        {
            folder.LastModifiedDate = _fileSystem.GetLastWriteTimeUtc(folder.Path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not get LastWriteTimeUtc for folder {FolderPath}", folder.Path);
        }

        context.Folders.Update(folder);
        await context.SaveChangesAsync();
        return true;
    }

    /// <inheritdoc />
    public async Task<Folder?> GetFolderByIdAsync(Guid folderId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.Id == folderId);
    }

    /// <inheritdoc />
    public async Task<Folder?> GetFolderByPathAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var normalizedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.Path == normalizedPath);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Folder>> GetAllFoldersAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Folders.AsNoTracking().OrderBy(f => f.Name).ThenBy(f => f.Path).ToListAsync();
    }

    /// <inheritdoc />
    public async Task<int> GetSongCountForFolderAsync(Guid folderId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Songs.CountAsync(s => s.FolderId == folderId);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Folder>> GetRootFoldersAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Folders.AsNoTracking()
            .Where(f => f.ParentFolderId == null)
            .OrderBy(f => f.Name)
            .ThenBy(f => f.Path)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Folder>> GetSubFoldersAsync(Guid parentFolderId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Folders.AsNoTracking()
            .Where(f => f.ParentFolderId == parentFolderId)
            .OrderBy(f => f.Name)
            .ThenBy(f => f.Path)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<Folder?> GetFolderByDirectoryPathAsync(Guid rootFolderId, string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath)) return null;

        await using var context = await _contextFactory.CreateDbContextAsync();

        var folder = await context.Folders.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Path == directoryPath);

        if (folder != null) return folder;

        return null;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Song>> GetSongsInDirectoryAsync(Guid folderId, string directoryPath)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var normalizedPath = directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return await context.Songs.AsNoTracking()
            .Where(s => s.FolderId == folderId && s.DirectoryPath == normalizedPath)
            .Include(s => s.Artist)
            .Include(s => s.Album).ThenInclude(alb => alb!.Artist)
            .OrderBy(s => s.Album != null ? s.Album.Title : string.Empty)
            .ThenBy(s => s.TrackNumber)
            .ThenBy(s => s.Title)
            .AsSplitQuery()
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<int> GetSongCountInDirectoryAsync(Guid folderId, string directoryPath)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var normalizedPath = directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return await context.Songs.CountAsync(s => s.FolderId == folderId && s.DirectoryPath == normalizedPath);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Song>> GetSongsInDirectoryRecursiveAsync(Guid folderId, string directoryPath)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var normalizedPath = directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return await context.Songs.AsNoTracking()
            .Where(s => s.FolderId == folderId &&
                        (s.DirectoryPath == normalizedPath ||
                         s.DirectoryPath.StartsWith(normalizedPath + "\\") ||
                         s.DirectoryPath.StartsWith(normalizedPath + "/")))
            .Include(s => s.Artist)
            .Include(s => s.Album).ThenInclude(alb => alb!.Artist)
            .OrderBy(s => s.DirectoryPath)
            .ThenBy(s => s.Album != null ? s.Album.Title : string.Empty)
            .ThenBy(s => s.TrackNumber)
            .ThenBy(s => s.Title)
            .AsSplitQuery()
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<int> GetSubFolderCountAsync(Guid parentFolderId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Folders.CountAsync(f => f.ParentFolderId == parentFolderId);
    }

    #endregion

    #region Library Scanning

    /// <inheritdoc />
    public async Task ScanFolderForMusicAsync(string folderPath, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var folder = await GetFolderByPathAsync(folderPath) ?? await AddFolderAsync(folderPath);
        if (folder is null)
        {
            _logger.LogWarning("Failed to add or find folder for path {FolderPath}, aborting scan.", folderPath);
            progress?.Report(new ScanProgress { StatusText = "Failed to add folder.", Percentage = 100 });
            return;
        }

        await RescanFolderForMusicAsync(folder.Id, progress, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> RescanFolderForMusicAsync(Guid folderId, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Wait to acquire the semaphore. If the operation is cancelled while waiting, it will throw.
            await _scanSemaphore.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Scan for folder ID {FolderId} was cancelled before acquiring semaphore.", folderId);
            progress?.Report(new ScanProgress { StatusText = "Scan cancelled by user.", Percentage = 100 });
            return false;
        }

        try
        {
            return await Task.Run(async () =>
            {
                var folder = await GetFolderByIdAsync(folderId);
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
                        return await RemoveFolderAsync(folderId);
                    }

                    progress?.Report(new ScanProgress
                        { StatusText = $"Analyzing '{folder.Name}'...", IsIndeterminate = true });
                    var (filesToAdd, filesToUpdate, filesRemovedFromDisk) =
                        await AnalyzeFolderChangesAsync(folderId, folder.Path, cancellationToken);

                    if (filesToAdd.Any() || filesToUpdate.Any() || filesRemovedFromDisk.Any())
                        _logger.LogInformation("Changes detected: +{New} ~{Updated} -{Removed}",
                            filesToAdd.Count, filesToUpdate.Count, filesRemovedFromDisk.Count);

                    cancellationToken.ThrowIfCancellationRequested();

                    var allFilePathsToDelete = filesRemovedFromDisk.Concat(filesToUpdate).Distinct().ToList();

                    if (allFilePathsToDelete.Any())
                    {
                        progress?.Report(new ScanProgress
                            { StatusText = "Cleaning up your library...", IsIndeterminate = true });
                        await using var deleteContext = await _contextFactory.CreateDbContextAsync();

                        var songsToDeleteQuery = deleteContext.Songs
                            .Where(s => s.FolderId == folderId && allFilePathsToDelete.Contains(s.FilePath));

                        var lrcPathsToDelete = await songsToDeleteQuery
                            .Where(s => s.LrcFilePath != null)
                            .Select(s => s.LrcFilePath!)
                            .ToListAsync(cancellationToken);

                        var albumArtPathsToDelete = await songsToDeleteQuery
                            .Where(s => s.AlbumArtUriFromTrack != null)
                            .Select(s => s.AlbumArtUriFromTrack!)
                            .Distinct()
                            .ToListAsync(cancellationToken);

                        await songsToDeleteQuery.ExecuteDeleteAsync(cancellationToken);

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
                            await using var cleanupContext = await _contextFactory.CreateDbContextAsync();
                            await CleanUpOrphanedEntitiesAsync(cleanupContext, cancellationToken);
                        }

                        progress?.Report(new ScanProgress
                            { StatusText = "Scan complete. Library is up to date.", Percentage = 100 });
                        return allFilePathsToDelete.Any();
                    }

                    var extractedMetadata =
                        await ExtractMetadataConcurrentlyAsync(filesToProcess, progress, cancellationToken);

                    cancellationToken.ThrowIfCancellationRequested();

                    var newSongsFound = 0;
                    if (extractedMetadata.Any())
                        newSongsFound =
                            await BatchUpdateDatabaseAsync(folderId, extractedMetadata, progress, cancellationToken);

                    cancellationToken.ThrowIfCancellationRequested();

                    progress?.Report(new ScanProgress { StatusText = "Finalizing...", IsIndeterminate = true });
                    await using (var finalContext = await _contextFactory.CreateDbContextAsync())
                    {
                        await CleanUpOrphanedEntitiesAsync(finalContext, cancellationToken);
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
                    _logger.LogInformation("Scan for folder ID {FolderId} was cancelled.", folderId);
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
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Scan for folder ID {FolderId} was cancelled during execution.", folderId);
            progress?.Report(new ScanProgress { StatusText = "Scan cancelled by user.", Percentage = 100 });
            return false;
        }
        finally
        {
            // CRITICAL: Release the semaphore so other scans can proceed.
            _scanSemaphore.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> RefreshAllFoldersAsync(IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var folders = (await GetAllFoldersAsync()).ToList();
        var totalFolders = folders.Count;

        if (totalFolders == 0)
        {
            _logger.LogInformation("No folders found in the library to refresh.");
            progress?.Report(
                new ScanProgress { StatusText = "No folders in the library to refresh.", Percentage = 100 });
            return false;
        }

        var foldersProcessed = 0;
        var anyChangesMade = false;

        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var progressWrapper = new Progress<ScanProgress>(scanProgress =>
            {
                var status = scanProgress.Percentage >= 100
                    ? scanProgress.StatusText
                    : $"({foldersProcessed + 1}/{totalFolders}) {scanProgress.StatusText}";

                progress?.Report(new ScanProgress
                {
                    StatusText = status,
                    Percentage = scanProgress.Percentage,
                    IsIndeterminate = scanProgress.IsIndeterminate,
                    CurrentFilePath = scanProgress.CurrentFilePath,
                    TotalFiles = scanProgress.TotalFiles,
                    NewSongsFound = scanProgress.NewSongsFound
                });
            });

            var result = await RescanFolderForMusicAsync(folder.Id, progressWrapper, cancellationToken);
            if (result) anyChangesMade = true;

            foldersProcessed++;
        }

        progress?.Report(new ScanProgress { StatusText = "Library refresh complete.", Percentage = 100 });
        return anyChangesMade;
    }

    /// <inheritdoc />
    public async Task<Artist?> GetArtistDetailsAsync(Guid artistId, bool allowOnlineFetch)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var artist = await context.Artists.AsTracking().FirstOrDefaultAsync(a => a.Id == artistId);

        if (artist is null) return null;

        var needsUpdate = string.IsNullOrWhiteSpace(artist.Biography) ||
                          string.IsNullOrWhiteSpace(artist.LocalImageCachePath);
        if (allowOnlineFetch && needsUpdate)
            await FetchAndUpdateArtistFromRemoteAsync(context, artist);

        return await context.Artists.AsNoTracking().FirstOrDefaultAsync(a => a.Id == artistId);
    }

    /// <inheritdoc />
    public Task StartArtistMetadataBackgroundFetchAsync()
    {
        lock (_metadataFetchLock)
        {
            if (_isMetadataFetchRunning)
                return Task.CompletedTask;

            if (_metadataFetchCts.IsCancellationRequested)
            {
                _metadataFetchCts.Dispose();
                _metadataFetchCts = new CancellationTokenSource();
            }

            _isMetadataFetchRunning = true;
        }

        var token = _metadataFetchCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                const int batchSize = 50;
                while (!token.IsCancellationRequested)
                {
                    List<Guid> artistIdsToUpdate;
                    await using (var idContext = await _contextFactory.CreateDbContextAsync())
                    {
                        artistIdsToUpdate = await idContext.Artists
                            .AsNoTracking()
                            .Where(a => a.MetadataLastCheckedUtc == null)
                            .OrderBy(a => a.Name)
                            .Select(a => a.Id)
                            .Take(batchSize)
                            .ToListAsync(token);
                    }

                    if (artistIdsToUpdate.Count == 0 || token.IsCancellationRequested) break;

                    using var scope = _serviceScopeFactory.CreateScope();
                    var scopedContextFactory =
                        scope.ServiceProvider.GetRequiredService<IDbContextFactory<MusicDbContext>>();
                    await using var batchContext = await scopedContextFactory.CreateDbContextAsync();

                    var artistsInBatch = await batchContext.Artists.Where(a => artistIdsToUpdate.Contains(a.Id))
                        .ToListAsync(token);
                    foreach (var artist in artistsInBatch)
                    {
                        if (token.IsCancellationRequested) break;
                        try
                        {
                            await FetchAndUpdateArtistFromRemoteAsync(batchContext, artist);
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
                _logger.LogInformation("Artist metadata background fetch was cancelled.");
            }
            finally
            {
                lock (_metadataFetchLock)
                {
                    _isMetadataFetchRunning = false;
                }
            }
        }, token);
        return Task.CompletedTask;
    }

    #endregion

    #region Song Management

    /// <inheritdoc />
    public async Task<Song?> AddSongAsync(Song songData)
    {
        ArgumentNullException.ThrowIfNull(songData);
        await using var context = await _contextFactory.CreateDbContextAsync();

        var existingSong = await context.Songs.AsNoTracking().FirstOrDefaultAsync(s => s.FilePath == songData.FilePath);
        if (existingSong is not null) return existingSong;

        context.Songs.Add(songData);
        await context.SaveChangesAsync();
        return songData;
    }

    /// <inheritdoc />
    public async Task<Song?> AddSongWithDetailsAsync(Guid folderId, SongFileMetadata metadata)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var song = await AddSongWithDetailsAsync(context, folderId, metadata);
        if (song is not null) await context.SaveChangesAsync();
        return song;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveSongAsync(Guid songId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var song = await context.Songs.FindAsync(songId);
        if (song is null) return false;

        var albumArtPathToDelete = song.AlbumArtUriFromTrack;
        var lrcPathToDelete = song.LrcFilePath;

        context.Songs.Remove(song);
        await context.SaveChangesAsync();

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

        await CleanUpOrphanedEntitiesAsync(context);
        return true;
    }

    /// <inheritdoc />
    public async Task<Song?> GetSongByIdAsync(Guid songId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Songs.AsNoTracking()
            .Include(s => s.Artist)
            .Include(s => s.Album).ThenInclude(a => a!.Artist)
            .Include(s => s.Folder)
            .AsSplitQuery()
            .FirstOrDefaultAsync(s => s.Id == songId);
    }

    /// <inheritdoc />
    public async Task<Song?> GetSongByFilePathAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Songs.AsNoTracking()
            .Include(s => s.Artist)
            .Include(s => s.Album).ThenInclude(a => a!.Artist)
            .Include(s => s.Folder)
            .AsSplitQuery()
            .FirstOrDefaultAsync(s => s.FilePath == filePath);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, Song>> GetSongsByIdsAsync(IEnumerable<Guid> songIds)
    {
        if (songIds is null || !songIds.Any()) return new Dictionary<Guid, Song>();

        await using var context = await _contextFactory.CreateDbContextAsync();
        var uniqueIds = songIds.Distinct().ToList();

        var songs = await context.Songs.AsNoTracking()
            .Where(s => uniqueIds.Contains(s.Id))
            .Include(s => s.Artist)
            .Include(s => s.Album).ThenInclude(a => a!.Artist)
            .AsSplitQuery()
            .ToListAsync();

        return songs.ToDictionary(s => s.Id);
    }

    /// <inheritdoc />
    public async Task<bool> UpdateSongAsync(Song songToUpdate)
    {
        ArgumentNullException.ThrowIfNull(songToUpdate);
        await using var context = await _contextFactory.CreateDbContextAsync();
        context.Songs.Update(songToUpdate);
        await context.SaveChangesAsync();
        return true;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Song>> GetAllSongsAsync(SongSortOrder sortOrder = SongSortOrder.TitleAsc)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        IQueryable<Song> query = context.Songs.AsNoTracking()
            .Include(s => s.Artist)
            .Include(s => s.Album).ThenInclude(a => a!.Artist);

        return await ApplySongSortOrder(query, sortOrder).AsSplitQuery().ToListAsync();
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Song>> GetSongsByAlbumIdAsync(Guid albumId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Songs.AsNoTracking()
            .Where(s => s.AlbumId == albumId)
            .Include(s => s.Artist)
            .OrderBy(s => s.TrackNumber).ThenBy(s => s.Title)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Song>> GetSongsByArtistIdAsync(Guid artistId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Songs.AsNoTracking()
            .Where(s => s.ArtistId == artistId)
            .Include(s => s.Album)
            .Include(s => s.Artist)
            .OrderBy(s => s.Album != null ? s.Album.Title : string.Empty)
            .ThenBy(s => s.TrackNumber)
            .AsSplitQuery()
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Song>> GetSongsByFolderIdAsync(Guid folderId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Songs.AsNoTracking()
            .Where(s => s.FolderId == folderId)
            .Include(s => s.Artist)
            .Include(s => s.Album).ThenInclude(alb => alb!.Artist)
            .OrderBy(s => s.Album != null ? s.Album.Title : string.Empty)
            .ThenBy(s => s.TrackNumber).ThenBy(s => s.Title)
            .AsSplitQuery()
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Song>> SearchSongsAsync(string searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetAllSongsAsync();

        await using var context = await _contextFactory.CreateDbContextAsync();
        return await BuildSongSearchQuery(context, searchTerm.Trim())
            .OrderBy(s => s.Title).ThenBy(s => s.Id)
            .AsSplitQuery()
            .ToListAsync();
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

    #endregion

    #region Artist Management

    /// <inheritdoc />
    public async Task<Artist?> GetArtistByIdAsync(Guid artistId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Artists.AsNoTracking().FirstOrDefaultAsync(a => a.Id == artistId);
    }

    /// <inheritdoc />
    public async Task<Artist?> GetArtistByNameAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Artists.AsNoTracking().FirstOrDefaultAsync(a => a.Name == name.Trim());
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Artist>> GetAllArtistsAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Artists.AsNoTracking().OrderBy(a => a.Name).ToListAsync();
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Artist>> SearchArtistsAsync(string searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetAllArtistsAsync();

        await using var context = await _contextFactory.CreateDbContextAsync();
        return await BuildArtistSearchQuery(context, searchTerm.Trim()).AsNoTracking().OrderBy(a => a.Name)
            .ToListAsync();
    }

    #endregion

    #region Album Management

    /// <inheritdoc />
    public async Task<Album?> GetAlbumByIdAsync(Guid albumId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Albums.AsNoTracking()
            .Include(al => al.Artist)
            .AsSplitQuery()
            .FirstOrDefaultAsync(al => al.Id == albumId);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Album>> GetAllAlbumsAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Albums.AsNoTracking()
            .Include(al => al.Artist)
            .OrderBy(al => al.Artist != null ? al.Artist.Name : string.Empty)
            .ThenBy(al => al.Title)
            .AsSplitQuery()
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Album>> SearchAlbumsAsync(string searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetAllAlbumsAsync();

        await using var context = await _contextFactory.CreateDbContextAsync();
        return await BuildAlbumSearchQuery(context, searchTerm.Trim())
            .OrderBy(al => al.Artist != null ? al.Artist.Name : string.Empty)
            .ThenBy(al => al.Title)
            .AsSplitQuery()
            .ToListAsync();
    }

    #endregion

    #region Playlist Management

    /// <inheritdoc />
    public async Task<Playlist?> CreatePlaylistAsync(string name, string? description = null,
        string? coverImageUri = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Playlist name cannot be empty.", nameof(name));

        await using var context = await _contextFactory.CreateDbContextAsync();
        var playlist = new Playlist
        {
            Name = name.Trim(),
            Description = description,
            CoverImageUri = coverImageUri,
            DateCreated = DateTime.UtcNow,
            DateModified = DateTime.UtcNow
        };
        context.Playlists.Add(playlist);
        await context.SaveChangesAsync();
        return playlist;
    }

    /// <inheritdoc />
    public async Task<bool> DeletePlaylistAsync(Guid playlistId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var rowsAffected = await context.Playlists.Where(p => p.Id == playlistId).ExecuteDeleteAsync();
        return rowsAffected > 0;
    }

    /// <inheritdoc />
    public async Task<bool> RenamePlaylistAsync(Guid playlistId, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("New playlist name cannot be empty.", nameof(newName));

        await using var context = await _contextFactory.CreateDbContextAsync();
        var playlist = await context.Playlists.FindAsync(playlistId);
        if (playlist is null) return false;

        playlist.Name = newName.Trim();
        playlist.DateModified = DateTime.UtcNow;
        await context.SaveChangesAsync();
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> UpdatePlaylistCoverAsync(Guid playlistId, string? newCoverImageUri)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var playlist = await context.Playlists.FindAsync(playlistId);
        if (playlist is null) return false;

        playlist.CoverImageUri = newCoverImageUri;
        playlist.DateModified = DateTime.UtcNow;
        await context.SaveChangesAsync();
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> AddSongsToPlaylistAsync(Guid playlistId, IEnumerable<Guid> songIds)
    {
        if (songIds is null || !songIds.Any()) return false;

        await using var context = await _contextFactory.CreateDbContextAsync();
        var playlist = await context.Playlists.FindAsync(playlistId);
        if (playlist is null) return false;

        var existingSongIds = await context.PlaylistSongs
            .Where(ps => ps.PlaylistId == playlistId)
            .Select(ps => ps.SongId)
            .ToHashSetAsync();

        var songIdsToAdd = songIds.Distinct().Except(existingSongIds).ToList();
        if (songIdsToAdd.Count == 0) return true;

        var maxOrder =
            await context.PlaylistSongs.Where(ps => ps.PlaylistId == playlistId).MaxAsync(ps => (int?)ps.Order) ?? -1;
        var playlistSongsToAdd = songIdsToAdd.Select(songId => new PlaylistSong
        {
            PlaylistId = playlistId,
            SongId = songId,
            Order = ++maxOrder
        });

        context.PlaylistSongs.AddRange(playlistSongsToAdd);
        playlist.DateModified = DateTime.UtcNow;
        await context.SaveChangesAsync();
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveSongsFromPlaylistAsync(Guid playlistId, IEnumerable<Guid> songIds)
    {
        if (songIds is null || !songIds.Any()) return false;

        await using var context = await _contextFactory.CreateDbContextAsync();
        var playlist = await context.Playlists.FindAsync(playlistId);
        if (playlist is null) return false;

        await context.PlaylistSongs
            .Where(ps => ps.PlaylistId == playlistId && songIds.Contains(ps.SongId))
            .ExecuteDeleteAsync();

        await ReindexPlaylistAsync(context, playlistId);

        playlist.DateModified = DateTime.UtcNow;
        await context.SaveChangesAsync();
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> UpdatePlaylistSongOrderAsync(Guid playlistId, IEnumerable<Guid> orderedSongIds)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var playlist = await context.Playlists.FindAsync(playlistId);
        if (playlist is null) return false;

        var playlistSongs = await context.PlaylistSongs
            .Where(ps => ps.PlaylistId == playlistId)
            .ToListAsync();

        var songMap = playlistSongs.ToDictionary(ps => ps.SongId);
        var newOrderList = orderedSongIds.ToList();

        for (var i = 0; i < newOrderList.Count; i++)
            if (songMap.TryGetValue(newOrderList[i], out var playlistSong))
                playlistSong.Order = i;

        playlist.DateModified = DateTime.UtcNow;
        await context.SaveChangesAsync();
        return true;
    }

    /// <inheritdoc />
    public async Task<Playlist?> GetPlaylistByIdAsync(Guid playlistId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Playlists.AsNoTracking()
            .Include(p => p.PlaylistSongs.OrderBy(ps => ps.Order)).ThenInclude(ps => ps.Song)
            .ThenInclude(s => s!.Artist)
            .Include(p => p.PlaylistSongs.OrderBy(ps => ps.Order)).ThenInclude(ps => ps.Song).ThenInclude(s => s!.Album)
            .ThenInclude(a => a!.Artist)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == playlistId);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Playlist>> GetAllPlaylistsAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Playlists.AsNoTracking()
            .Include(p => p.PlaylistSongs)
            .OrderBy(p => p.Name).ThenBy(p => p.Id)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Song>> GetSongsInPlaylistOrderedAsync(Guid playlistId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.PlaylistSongs.AsNoTracking()
            .Where(ps => ps.PlaylistId == playlistId)
            .OrderBy(ps => ps.Order)
            .Include(ps => ps.Song).ThenInclude(s => s!.Artist)
            .Include(ps => ps.Song).ThenInclude(s => s!.Album).ThenInclude(a => a!.Artist)
            .Select(ps => ps.Song!)
            .AsSplitQuery()
            .ToListAsync();
    }

    #endregion

    #region Genre Management

    /// <inheritdoc />
    public async Task<IEnumerable<Genre>> GetAllGenresAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Genres.AsNoTracking().OrderBy(g => g.Name).ToListAsync();
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Song>> GetSongsByGenreIdAsync(Guid genreId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Songs.AsNoTracking()
            .Where(s => s.Genres.Any(g => g.Id == genreId))
            .Include(s => s.Artist)
            .Include(s => s.Album).ThenInclude(a => a!.Artist)
            .OrderBy(s => s.Title)
            .AsSplitQuery()
            .ToListAsync();
    }

    #endregion

    #region Listen History

    /// <inheritdoc />
    public async Task<long?> CreateListenHistoryEntryAsync(Guid songId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var song = await context.Songs.FindAsync(songId);
        if (song is null) return null;

        song.PlayCount++;
        song.LastPlayedDate = DateTime.UtcNow;

        var historyEntry = new ListenHistory
            { SongId = songId, ListenTimestampUtc = DateTime.UtcNow, IsScrobbled = false };
        context.ListenHistory.Add(historyEntry);

        await context.SaveChangesAsync();
        return historyEntry.Id;
    }

    /// <inheritdoc />
    public async Task<bool> MarkListenAsEligibleForScrobblingAsync(long listenHistoryId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var historyEntry = await context.ListenHistory.FindAsync(listenHistoryId);
        if (historyEntry is null) return false;

        historyEntry.IsEligibleForScrobbling = true;
        await context.SaveChangesAsync();
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> MarkListenAsScrobbledAsync(long listenHistoryId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var historyEntry = await context.ListenHistory.FindAsync(listenHistoryId);
        if (historyEntry is null) return false;

        historyEntry.IsScrobbled = true;
        await context.SaveChangesAsync();
        return true;
    }

    /// <inheritdoc />
    public async Task LogSkipAsync(Guid songId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var song = await context.Songs.FindAsync(songId);
        if (song is null) return;

        song.SkipCount++;
        await context.SaveChangesAsync();
    }

    /// <inheritdoc />
    public async Task<int> GetListenCountForSongAsync(Guid songId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.ListenHistory.CountAsync(lh => lh.SongId == songId);
    }

    #endregion

    #region Paged Loading

    /// <inheritdoc />
    public async Task<PagedResult<Song>> GetAllSongsPagedAsync(int pageNumber, int pageSize,
        SongSortOrder sortOrder = SongSortOrder.TitleAsc)
    {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Songs.AsNoTracking().Include(s => s.Artist).Include(s => s.Album)
            .ThenInclude(a => a!.Artist);
        var totalCount = await query.CountAsync();
        var pagedData = await ApplySongSortOrder(query, sortOrder)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync();

        return new PagedResult<Song>
            { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <inheritdoc />
    public async Task<PagedResult<Song>> SearchSongsPagedAsync(string searchTerm, int pageNumber, int pageSize)
    {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync();

        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetAllSongsPagedAsync(pageNumber, pageSize);

        var query = BuildSongSearchQuery(context, searchTerm.Trim());

        var totalCount = await query.CountAsync();
        var pagedData = await ApplySongSortOrder(query, SongSortOrder.TitleAsc)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync();

        return new PagedResult<Song>
            { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <inheritdoc />
    public async Task<PagedResult<Song>> GetSongsByAlbumIdPagedAsync(Guid albumId, int pageNumber, int pageSize,
        SongSortOrder sortOrder)
    {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Songs.AsNoTracking().Where(s => s.AlbumId == albumId).Include(s => s.Artist)
            .Include(s => s.Album);
        var totalCount = await query.CountAsync();
        var pagedData = await ApplySongSortOrder(query, sortOrder)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync();

        return new PagedResult<Song>
            { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <inheritdoc />
    public async Task<PagedResult<Song>> GetSongsByArtistIdPagedAsync(Guid artistId, int pageNumber, int pageSize,
        SongSortOrder sortOrder)
    {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Songs.AsNoTracking().Where(s => s.ArtistId == artistId).Include(s => s.Artist)
            .Include(s => s.Album).ThenInclude(a => a!.Artist);
        var totalCount = await query.CountAsync();
        var pagedData = await ApplySongSortOrder(query, sortOrder)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync();

        return new PagedResult<Song>
            { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <inheritdoc />
    public async Task<PagedResult<Song>> GetSongsByGenreIdPagedAsync(Guid genreId, int pageNumber, int pageSize,
        SongSortOrder sortOrder)
    {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Songs.AsNoTracking().Where(s => s.Genres.Any(g => g.Id == genreId)).Include(s => s.Artist)
            .Include(s => s.Album).ThenInclude(a => a!.Artist);
        var totalCount = await query.CountAsync();
        var pagedData = await ApplySongSortOrder(query, sortOrder)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync();

        return new PagedResult<Song>
            { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <inheritdoc />
    public async Task<PagedResult<Song>> GetSongsByPlaylistPagedAsync(Guid playlistId, int pageNumber, int pageSize)
    {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.PlaylistSongs.AsNoTracking()
            .Where(ps => ps.PlaylistId == playlistId)
            .OrderBy(ps => ps.Order)
            .Include(ps => ps.Song).ThenInclude(s => s!.Artist)
            .Include(ps => ps.Song).ThenInclude(s => s!.Album).ThenInclude(a => a!.Artist)
            .Select(ps => ps.Song!);

        var totalCount = await query.CountAsync();
        var pagedData = await query
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync();

        return new PagedResult<Song>
            { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <inheritdoc />
    public async Task<PagedResult<Artist>> GetAllArtistsPagedAsync(int pageNumber, int pageSize)
    {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Artists.AsNoTracking();
        var totalCount = await query.CountAsync();
        var pagedData = await query.OrderBy(a => a.Name)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).ToListAsync();

        return new PagedResult<Artist>
            { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <inheritdoc />
    public async Task<PagedResult<Artist>> SearchArtistsPagedAsync(string searchTerm, int pageNumber, int pageSize)
    {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = string.IsNullOrWhiteSpace(searchTerm)
            ? context.Artists
            : BuildArtistSearchQuery(context, searchTerm.Trim());
        var totalCount = await query.CountAsync();
        var pagedData = await query.AsNoTracking().OrderBy(a => a.Name)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).ToListAsync();

        return new PagedResult<Artist>
            { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <inheritdoc />
    public async Task<PagedResult<Album>> GetAllAlbumsPagedAsync(int pageNumber, int pageSize)
    {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Albums.AsNoTracking().Include(al => al.Artist);
        var totalCount = await query.CountAsync();
        var pagedData = await query
            .OrderBy(al => al.Artist != null ? al.Artist.Name : string.Empty).ThenBy(al => al.Title)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync();

        return new PagedResult<Album>
            { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <inheritdoc />
    public async Task<PagedResult<Album>> SearchAlbumsPagedAsync(string searchTerm, int pageNumber, int pageSize)
    {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = string.IsNullOrWhiteSpace(searchTerm)
            ? context.Albums.Include(al => al.Artist)
            : BuildAlbumSearchQuery(context, searchTerm.Trim());
        var totalCount = await query.CountAsync();
        var pagedData = await query.AsNoTracking()
            .OrderBy(al => al.Artist != null ? al.Artist.Name : string.Empty).ThenBy(al => al.Title)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync();

        return new PagedResult<Album>
            { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <inheritdoc />
    public async Task<PagedResult<Playlist>> GetAllPlaylistsPagedAsync(int pageNumber, int pageSize)
    {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Playlists.AsNoTracking().Include(p => p.PlaylistSongs);
        var totalCount = await query.CountAsync();
        var pagedData = await query
            .OrderBy(p => p.Name).ThenBy(p => p.Id)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).ToListAsync();

        return new PagedResult<Playlist>
            { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <inheritdoc />
    public async Task<PagedResult<Song>> GetSongsByFolderIdPagedAsync(Guid folderId, int pageNumber, int pageSize,
        SongSortOrder sortOrder = SongSortOrder.TitleAsc)
    {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Songs.AsNoTracking().Where(s => s.FolderId == folderId).Include(s => s.Artist)
            .Include(s => s.Album).ThenInclude(a => a!.Artist);
        var totalCount = await query.CountAsync();
        var pagedData = await ApplySongSortOrder(query, sortOrder)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync();

        return new PagedResult<Song>
            { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <inheritdoc />
    public async Task<List<Guid>> GetAllSongIdsAsync(SongSortOrder sortOrder)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await ApplySongSortOrder(context.Songs.AsNoTracking(), sortOrder).Select(s => s.Id).ToListAsync();
    }

    /// <inheritdoc />
    public async Task<List<Guid>> SearchAllSongIdsAsync(string searchTerm, SongSortOrder sortOrder)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetAllSongIdsAsync(sortOrder);

        var query = BuildSongSearchQuery(context, searchTerm.Trim());
        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync();
    }

    /// <inheritdoc />
    public async Task<List<Guid>> GetAllSongIdsByFolderIdAsync(Guid folderId, SongSortOrder sortOrder)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Songs.AsNoTracking().Where(s => s.FolderId == folderId);
        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync();
    }

    /// <inheritdoc />
    public async Task<List<Guid>> GetAllSongIdsInDirectoryRecursiveAsync(Guid folderId, string directoryPath,
        SongSortOrder sortOrder)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        // Normalize the directory path for comparison
        var normalizedPath = directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Get all song IDs where DirectoryPath equals normalizedPath or starts with normalizedPath followed by a separator
        // This prevents false positives (e.g., "C:\\Music\\Rock" matching "C:\\Music\\Rockabilly")
        var query = context.Songs.AsNoTracking()
            .Where(s => s.FolderId == folderId &&
                        (s.DirectoryPath == normalizedPath ||
                         s.DirectoryPath.StartsWith(normalizedPath + "\\\\") ||
                         s.DirectoryPath.StartsWith(normalizedPath + "/")));

        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync();
    }

    /// <inheritdoc />
    public async Task<List<Guid>> GetAllSongIdsByArtistIdAsync(Guid artistId, SongSortOrder sortOrder)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Songs.AsNoTracking().Where(s => s.ArtistId == artistId);
        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync();
    }

    /// <inheritdoc />
    public async Task<List<Guid>> GetAllSongIdsByAlbumIdAsync(Guid albumId, SongSortOrder sortOrder)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Songs.AsNoTracking().Where(s => s.AlbumId == albumId);
        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync();
    }

    /// <inheritdoc />
    public async Task<List<Guid>> GetAllSongIdsByPlaylistIdAsync(Guid playlistId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.PlaylistSongs.AsNoTracking()
            .Where(ps => ps.PlaylistId == playlistId).OrderBy(ps => ps.Order).Select(ps => ps.SongId).ToListAsync();
    }

    /// <inheritdoc />
    public async Task<List<Guid>> GetAllSongIdsByGenreIdAsync(Guid genreId, SongSortOrder sortOrder)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Songs.AsNoTracking().Where(s => s.Genres.Any(g => g.Id == genreId));
        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync();
    }

    #endregion

    #region Scoped Search

    /// <inheritdoc />
    public async Task<IEnumerable<Song>> SearchSongsInFolderAsync(Guid folderId, string searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetSongsByFolderIdAsync(folderId);

        await using var context = await _contextFactory.CreateDbContextAsync();
        var term = $"%{searchTerm.Trim()}%";
        return await context.Songs.AsNoTracking().Where(s => s.FolderId == folderId
                                                             && (EF.Functions.Like(s.Title, term)
                                                                 || (s.Album != null &&
                                                                     EF.Functions.Like(s.Album.Title, term))
                                                                 || (s.Artist != null &&
                                                                     EF.Functions.Like(s.Artist.Name, term))))
            .Include(s => s.Artist).Include(s => s.Album).ThenInclude(a => a!.Artist)
            .OrderBy(s => s.Title).AsSplitQuery().ToListAsync();
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Song>> SearchSongsInAlbumAsync(Guid albumId, string searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetSongsByAlbumIdAsync(albumId);

        await using var context = await _contextFactory.CreateDbContextAsync();
        var term = $"%{searchTerm.Trim()}%";
        return await context.Songs.AsNoTracking().Where(s => s.AlbumId == albumId
                                                             && (EF.Functions.Like(s.Title, term) ||
                                                                 (s.Artist != null &&
                                                                  EF.Functions.Like(s.Artist.Name, term))))
            .Include(s => s.Artist).Include(s => s.Album)
            .OrderBy(s => s.TrackNumber).AsSplitQuery().ToListAsync();
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Song>> SearchSongsInArtistAsync(Guid artistId, string searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetSongsByArtistIdAsync(artistId);

        await using var context = await _contextFactory.CreateDbContextAsync();
        var term = $"%{searchTerm.Trim()}%";
        return await context.Songs.AsNoTracking().Where(s => s.ArtistId == artistId
                                                             && (EF.Functions.Like(s.Title, term) ||
                                                                 (s.Album != null &&
                                                                  EF.Functions.Like(s.Album.Title, term))))
            .Include(s => s.Artist).Include(s => s.Album)
            .OrderBy(s => s.Album != null ? s.Album.Title : string.Empty).ThenBy(s => s.TrackNumber)
            .AsSplitQuery()
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Song>> SearchSongsInPlaylistAsync(Guid playlistId, string searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetSongsInPlaylistOrderedAsync(playlistId);

        await using var context = await _contextFactory.CreateDbContextAsync();
        var term = $"%{searchTerm.Trim()}%";
        var query = context.PlaylistSongs.AsNoTracking()
            .Where(ps => ps.PlaylistId == playlistId && ps.Song != null &&
                         (EF.Functions.Like(ps.Song.Title, term)
                          || (ps.Song.Album != null && EF.Functions.Like(ps.Song.Album.Title, term))
                          || (ps.Song.Artist != null && EF.Functions.Like(ps.Song.Artist.Name, term))));

        return await query
            .OrderBy(ps => ps.Order)
            .Include(ps => ps.Song).ThenInclude(s => s!.Artist)
            .Include(ps => ps.Song).ThenInclude(s => s!.Album).ThenInclude(a => a!.Artist)
            .Select(ps => ps.Song!)
            .AsSplitQuery()
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Song>> SearchSongsInGenreAsync(Guid genreId, string searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetSongsByGenreIdAsync(genreId);

        await using var context = await _contextFactory.CreateDbContextAsync();
        var term = $"%{searchTerm.Trim()}%";
        return await context.Songs.AsNoTracking()
            .Where(s => s.Genres.Any(g => g.Id == genreId)
                        && (EF.Functions.Like(s.Title, term)
                            || (s.Album != null && EF.Functions.Like(s.Album.Title, term))
                            || (s.Artist != null && EF.Functions.Like(s.Artist.Name, term))))
            .Include(s => s.Artist).Include(s => s.Album).ThenInclude(a => a!.Artist)
            .OrderBy(s => s.Title)
            .AsSplitQuery()
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<PagedResult<Song>> SearchSongsInFolderPagedAsync(Guid folderId, string searchTerm, int pageNumber,
        int pageSize)
    {
        SanitizePaging(ref pageNumber, ref pageSize);
        await using var context = await _contextFactory.CreateDbContextAsync();

        var query = context.Songs.AsNoTracking().Where(s => s.FolderId == folderId);
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = $"%{searchTerm.Trim()}%";
            query = query.Where(s => EF.Functions.Like(s.Title, term)
                                     || (s.Album != null && EF.Functions.Like(s.Album.Title, term))
                                     || (s.Artist != null && EF.Functions.Like(s.Artist.Name, term)));
        }

        query = query.Include(s => s.Artist).Include(s => s.Album).ThenInclude(a => a!.Artist);
        var totalCount = await query.CountAsync();
        var pagedData = await ApplySongSortOrder(query, SongSortOrder.TitleAsc)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync();

        return new PagedResult<Song>
            { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <inheritdoc />
    public async Task<PagedResult<Song>> SearchSongsInAlbumPagedAsync(Guid albumId, string searchTerm, int pageNumber,
        int pageSize)
    {
        SanitizePaging(ref pageNumber, ref pageSize);
        await using var context = await _contextFactory.CreateDbContextAsync();

        var query = context.Songs.AsNoTracking().Where(s => s.AlbumId == albumId);
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = $"%{searchTerm.Trim()}%";
            query = query.Where(s =>
                EF.Functions.Like(s.Title, term) || (s.Artist != null && EF.Functions.Like(s.Artist.Name, term)));
        }

        query = query.Include(s => s.Artist).Include(s => s.Album);
        var totalCount = await query.CountAsync();
        var pagedData = await ApplySongSortOrder(query, SongSortOrder.TrackNumberAsc)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync();

        return new PagedResult<Song>
            { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <inheritdoc />
    public async Task<PagedResult<Song>> SearchSongsInArtistPagedAsync(Guid artistId, string searchTerm, int pageNumber,
        int pageSize)
    {
        SanitizePaging(ref pageNumber, ref pageSize);
        await using var context = await _contextFactory.CreateDbContextAsync();

        var query = context.Songs.AsNoTracking().Where(s => s.ArtistId == artistId);
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = $"%{searchTerm.Trim()}%";
            query = query.Where(s =>
                EF.Functions.Like(s.Title, term) || (s.Album != null && EF.Functions.Like(s.Album.Title, term)));
        }

        query = query.Include(s => s.Artist).Include(s => s.Album);
        var totalCount = await query.CountAsync();
        var pagedData = await ApplySongSortOrder(query, SongSortOrder.AlbumAsc)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync();

        return new PagedResult<Song>
            { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <inheritdoc />
    public async Task<PagedResult<Song>> SearchSongsInPlaylistPagedAsync(Guid playlistId, string searchTerm,
        int pageNumber, int pageSize)
    {
        SanitizePaging(ref pageNumber, ref pageSize);
        await using var context = await _contextFactory.CreateDbContextAsync();

        var query = context.PlaylistSongs.AsNoTracking()
            .Where(ps => ps.PlaylistId == playlistId);

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = $"%{searchTerm.Trim()}%";
            query = query.Where(ps => ps.Song != null &&
                                      (EF.Functions.Like(ps.Song.Title, term)
                                       || (ps.Song.Album != null && EF.Functions.Like(ps.Song.Album.Title, term))
                                       || (ps.Song.Artist != null && EF.Functions.Like(ps.Song.Artist.Name, term))));
        }

        var songQuery = query
            .OrderBy(ps => ps.Order)
            .Include(ps => ps.Song).ThenInclude(s => s!.Artist)
            .Include(ps => ps.Song).ThenInclude(s => s!.Album).ThenInclude(a => a!.Artist)
            .Select(ps => ps.Song!);

        var totalCount = await songQuery.CountAsync();
        var pagedData = await songQuery
            .Skip((pageNumber - 1) * pageSize).Take(pageSize)
            .AsSplitQuery()
            .ToListAsync();

        return new PagedResult<Song>
            { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <inheritdoc />
    public async Task<PagedResult<Song>> SearchSongsInGenrePagedAsync(Guid genreId, string searchTerm, int pageNumber,
        int pageSize)
    {
        SanitizePaging(ref pageNumber, ref pageSize);
        await using var context = await _contextFactory.CreateDbContextAsync();

        var query = context.Songs.AsNoTracking()
            .Where(s => s.Genres.Any(g => g.Id == genreId));

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = $"%{searchTerm.Trim()}%";
            query = query.Where(s => EF.Functions.Like(s.Title, term)
                                     || (s.Album != null && EF.Functions.Like(s.Album.Title, term))
                                     || (s.Artist != null && EF.Functions.Like(s.Artist.Name, term)));
        }

        query = query.Include(s => s.Artist).Include(s => s.Album).ThenInclude(a => a!.Artist);
        var totalCount = await query.CountAsync();
        var pagedData = await ApplySongSortOrder(query, SongSortOrder.TitleAsc)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize)
            .AsSplitQuery()
            .ToListAsync();

        return new PagedResult<Song>
            { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <inheritdoc />
    public async Task<List<Guid>> SearchAllSongIdsInFolderAsync(Guid folderId, string searchTerm,
        SongSortOrder sortOrder)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Songs.AsNoTracking().Where(s => s.FolderId == folderId);

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = $"%{searchTerm.Trim()}%";
            query = query.Where(s => EF.Functions.Like(s.Title, term)
                                     || (s.Album != null && EF.Functions.Like(s.Album.Title, term))
                                     || (s.Artist != null && EF.Functions.Like(s.Artist.Name, term)));
        }

        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync();
    }

    /// <inheritdoc />
    public async Task<List<Guid>> SearchAllSongIdsInArtistAsync(Guid artistId, string searchTerm,
        SongSortOrder sortOrder)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Songs.AsNoTracking().Where(s => s.ArtistId == artistId);

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = $"%{searchTerm.Trim()}%";
            query = query.Where(s => EF.Functions.Like(s.Title, term)
                                     || (s.Album != null && EF.Functions.Like(s.Album.Title, term)));
        }

        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync();
    }

    /// <inheritdoc />
    public async Task<List<Guid>> SearchAllSongIdsInAlbumAsync(Guid albumId, string searchTerm, SongSortOrder sortOrder)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Songs.AsNoTracking().Where(s => s.AlbumId == albumId);

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = $"%{searchTerm.Trim()}%";
            query = query.Where(s => EF.Functions.Like(s.Title, term)
                                     || (s.Artist != null && EF.Functions.Like(s.Artist.Name, term)));
        }

        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync();
    }

    /// <inheritdoc />
    public async Task<List<Guid>> SearchAllSongIdsInPlaylistAsync(Guid playlistId, string searchTerm)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.PlaylistSongs.AsNoTracking()
            .Where(ps => ps.PlaylistId == playlistId);

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = $"%{searchTerm.Trim()}%";
            query = query.Where(ps => ps.Song != null &&
                                      (EF.Functions.Like(ps.Song.Title, term)
                                       || (ps.Song.Album != null && EF.Functions.Like(ps.Song.Album.Title, term))
                                       || (ps.Song.Artist != null && EF.Functions.Like(ps.Song.Artist.Name, term))));
        }

        return await query
            .OrderBy(ps => ps.Order)
            .Select(ps => ps.SongId)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<List<Guid>> SearchAllSongIdsInGenreAsync(Guid genreId, string searchTerm, SongSortOrder sortOrder)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Songs.AsNoTracking().Where(s => s.Genres.Any(g => g.Id == genreId));

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = $"%{searchTerm.Trim()}%";
            query = query.Where(s => EF.Functions.Like(s.Title, term)
                                     || (s.Album != null && EF.Functions.Like(s.Album.Title, term))
                                     || (s.Artist != null && EF.Functions.Like(s.Artist.Name, term)));
        }

        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync();
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

        await using var context = await _contextFactory.CreateDbContextAsync();

        var allFolders = await context.Folders.AsNoTracking().ToListAsync(cancellationToken);
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
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<(List<string> filesToAdd, List<string> filesToUpdate, List<string> filesRemovedFromDisk)>
        AnalyzeFolderChangesAsync(Guid folderId, string folderPath, CancellationToken cancellationToken)
    {
        await using var analysisContext = await _contextFactory.CreateDbContextAsync();
        var dbFileMap = (await analysisContext.Songs
                .AsNoTracking()
                .Where(s => s.FolderId == folderId)
                .Select(s => new { s.FilePath, s.FileModifiedDate })
                .ToListAsync(cancellationToken))
            .ToDictionary(s => s.FilePath, s => s.FileModifiedDate, StringComparer.OrdinalIgnoreCase);

        cancellationToken.ThrowIfCancellationRequested();

        var diskFileMap = _fileSystem.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
            .Where(file => MusicFileExtensions.Contains(_fileSystem.GetExtension(file)))
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
        var filesToUpdate = commonPaths
            .Where(path => dbFileMap[path] != diskFileMap[path])
            .ToList();

        var filesRemovedFromDisk = dbPaths.Except(diskPaths).ToList();

        return (filesToAdd, filesToUpdate, filesRemovedFromDisk);
    }

    private async Task<List<SongFileMetadata>> ExtractMetadataConcurrentlyAsync(List<string> filesToProcess,
        IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        var extractedMetadata = new ConcurrentBag<SongFileMetadata>();
        var degreeOfParallelism = Environment.ProcessorCount;
        using var semaphore = new SemaphoreSlim(degreeOfParallelism);
        var processedCount = 0;
        var totalFiles = filesToProcess.Count;
        const int progressReportingBatchSize = 25;

        progress?.Report(new ScanProgress
            { StatusText = "Reading song details...", TotalFiles = totalFiles, Percentage = 0 });

        var extractionTasks = filesToProcess.Select(async filePath =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var metadata = await _metadataService.ExtractMetadataAsync(filePath);
                if (!metadata.ExtractionFailed)
                    extractedMetadata.Add(metadata);
                else
                    _logger.LogWarning("Failed to extract metadata from file: {FilePath}", filePath);
            }
            finally
            {
                var currentCount = Interlocked.Increment(ref processedCount);

                if (currentCount % progressReportingBatchSize == 0 || currentCount == totalFiles)
                    progress?.Report(new ScanProgress
                    {
                        StatusText = "Reading song details...",
                        CurrentFilePath = filePath,
                        Percentage = (double)currentCount / totalFiles * 100,
                        TotalFiles = totalFiles,
                        NewSongsFound = extractedMetadata.Count
                    });
                semaphore.Release();
            }
        });

        await Task.WhenAll(extractionTasks);
        return extractedMetadata.ToList();
    }

    private async Task<int> BatchUpdateDatabaseAsync(Guid folderId, List<SongFileMetadata> metadataList,
        IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        const int batchSize = 500;
        var totalProcessed = 0;
        var totalMetadataCount = metadataList.Count;

        progress?.Report(new ScanProgress
        {
            StatusText = $"Preparing to add {totalMetadataCount:N0} songs...",
            IsIndeterminate = true,
            NewSongsFound = totalMetadataCount
        });

        await using var folderContext = await _contextFactory.CreateDbContextAsync();
        var rootFolder = await folderContext.Folders.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == folderId, cancellationToken);
        if (rootFolder == null)
        {
            _logger.LogError("Root folder with ID {FolderId} not found during batch update.", folderId);
            return 0;
        }

        // Ensure folders exist for ALL files first, to avoid repeated checks
        var discoveredDirectories = metadataList
            .Select(m => _fileSystem.GetDirectoryName(m.FilePath))
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        await EnsureSubFoldersExistAsync(folderId, rootFolder.Path, discoveredDirectories, cancellationToken);

        // Chunk the metadata list
        var chunks = metadataList.Chunk(batchSize).ToList();
        var currentChunkIndex = 0;

        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            currentChunkIndex++;

            progress?.Report(new ScanProgress
            {
                StatusText = $"Saving batch {currentChunkIndex}/{chunks.Count}...",
                Percentage = (double)currentChunkIndex / chunks.Count * 100,
                NewSongsFound = totalMetadataCount // Maintain the total count info
            });

            var addedInChunk = await ProcessSingleBatchAsync(folderId, chunk, cancellationToken);
            totalProcessed += addedInChunk;
        }

        return totalProcessed;
    }

    private async Task<int> ProcessSingleBatchAsync(Guid folderId, SongFileMetadata[] metadataList, CancellationToken cancellationToken)
    {
        const int maxRetries = 3;
        var retryCount = 0;

        while (retryCount < maxRetries)
        {
            try
            {
                await using var context = await _contextFactory.CreateDbContextAsync();
                
                // Disable change tracking for faster reads/inserts where possible
                // (Though we need tracking for Add, we don't need it for the lookups if we attach properly,
                // but standard simple Add is safer for complex relationships)
                
                var filePaths = metadataList.Select(m => m.FilePath).ToList();

                var existingSongPaths = await context.Songs
                    .AsNoTracking()
                    .Where(s => filePaths.Contains(s.FilePath))
                    .Select(s => s.FilePath)
                    .ToListAsync(cancellationToken);

                var existingSongPathsSet = new HashSet<string>(existingSongPaths, StringComparer.OrdinalIgnoreCase);

                var metadataToInsert = metadataList
                    .Where(m => !existingSongPathsSet.Contains(m.FilePath))
                    .ToList();

                if (metadataToInsert.Count == 0)
                    return 0;

                var artistNames = metadataToInsert.SelectMany(m => new[] { m.Artist, m.AlbumArtist })
                    .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var albumTitles = metadataToInsert.Select(m => m.Album).Where(t => !string.IsNullOrWhiteSpace(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var genreNames = metadataToInsert.SelectMany(m => m.Genres ?? Enumerable.Empty<string>())
                    .Where(g => !string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                var existingArtists = await context.Artists.Where(a => artistNames.Contains(a.Name))
                    .ToDictionaryAsync(a => a.Name, StringComparer.OrdinalIgnoreCase, cancellationToken);
                var existingAlbums = await context.Albums.Where(a => albumTitles.Contains(a.Title))
                    .ToListAsync(cancellationToken);
                var existingGenres = await context.Genres.Where(g => genreNames.Contains(g.Name))
                    .ToDictionaryAsync(g => g.Name, StringComparer.OrdinalIgnoreCase, cancellationToken);

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

                foreach (var metadata in metadataToInsert)
                    await AddSongWithDetailsOptimizedAsync(context, folderId, metadata, existingArtists, existingAlbums,
                        existingGenres);

                await context.SaveChangesAsync(cancellationToken);
                return metadataToInsert.Count;
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

    private Task AddSongWithDetailsOptimizedAsync(
        MusicDbContext context,
        Guid folderId,
        SongFileMetadata metadata,
        Dictionary<string, Artist> artistLookup,
        List<Album> existingAlbumList,
        Dictionary<string, Genre> genreLookup)
    {
        var trackArtistName = string.IsNullOrWhiteSpace(metadata.Artist) ? UnknownArtistName : metadata.Artist.Trim();
        var albumArtistName = string.IsNullOrWhiteSpace(metadata.AlbumArtist)
            ? trackArtistName
            : metadata.AlbumArtist.Trim();

        var trackArtist = artistLookup[trackArtistName];
        var albumArtist = artistLookup[albumArtistName];

        Album? album = null;
        if (!string.IsNullOrWhiteSpace(metadata.Album))
        {
            var albumTitle = metadata.Album.Trim();
            album = existingAlbumList.FirstOrDefault(a =>
                a.Title.Equals(albumTitle, StringComparison.OrdinalIgnoreCase) && a.ArtistId == albumArtist.Id);
            if (album == null)
            {
                album = new Album { Title = albumTitle, ArtistId = albumArtist.Id, Year = metadata.Year };
                context.Albums.Add(album);
                existingAlbumList.Add(album);
            }
            else if (album.Year is null && metadata.Year.HasValue)
            {
                album.Year = metadata.Year;
            }
        }

        var genres =
            metadata.Genres?.Select(g => g.Trim()).Where(g => !string.IsNullOrWhiteSpace(g))
                .Distinct(StringComparer.OrdinalIgnoreCase).Select(name => genreLookup[name]).ToList() ??
            new List<Genre>();

        var directoryPath = _fileSystem.GetDirectoryName(metadata.FilePath) ?? string.Empty;

        var song = new Song
        {
            FilePath = metadata.FilePath,
            DirectoryPath = directoryPath,
            Title = metadata.Title,
            Duration = metadata.Duration,
            AlbumArtUriFromTrack = metadata.CoverArtUri,
            LightSwatchId = metadata.LightSwatchId,
            DarkSwatchId = metadata.DarkSwatchId,
            Year = metadata.Year,
            TrackNumber = metadata.TrackNumber,
            TrackCount = metadata.TrackCount,
            DiscNumber = metadata.DiscNumber,
            DiscCount = metadata.DiscCount,
            SampleRate = metadata.SampleRate,
            Bitrate = metadata.Bitrate,
            Channels = metadata.Channels,
            DateAddedToLibrary = DateTime.UtcNow,
            FileCreatedDate = metadata.FileCreatedDate,
            FileModifiedDate = metadata.FileModifiedDate,
            FolderId = folderId,
            Composer = metadata.Composer,
            Bpm = metadata.Bpm,
            Lyrics = metadata.Lyrics,
            LrcFilePath = metadata.LrcFilePath,
            ArtistId = trackArtist.Id,
            AlbumId = album?.Id,
            Genres = genres,
            Grouping = metadata.Grouping,
            Copyright = metadata.Copyright,
            Comment = metadata.Comment,
            Conductor = metadata.Conductor,
            MusicBrainzTrackId = metadata.MusicBrainzTrackId,
            MusicBrainzReleaseId = metadata.MusicBrainzReleaseId
        };

        if (album is not null && string.IsNullOrEmpty(album.CoverArtUri) && !string.IsNullOrEmpty(metadata.CoverArtUri))
            album.CoverArtUri = metadata.CoverArtUri;

        context.Songs.Add(song);
        return Task.CompletedTask;
    }

    private async Task<Song?> AddSongWithDetailsAsync(MusicDbContext context, Guid folderId, SongFileMetadata metadata)
    {
        try
        {
            var trackArtist = await GetOrCreateArtistAsync(context, metadata.Artist);
            var albumArtist = !string.IsNullOrWhiteSpace(metadata.AlbumArtist)
                ? await GetOrCreateArtistAsync(context, metadata.AlbumArtist)
                : trackArtist;

            var album = !string.IsNullOrWhiteSpace(metadata.Album)
                ? await GetOrCreateAlbumAsync(context, metadata.Album, albumArtist.Id, metadata.Year)
                : null;

            var genres = await EnsureGenresExistAsync(context, metadata.Genres);

            var directoryPath = _fileSystem.GetDirectoryName(metadata.FilePath) ?? string.Empty;

            var song = new Song
            {
                FilePath = metadata.FilePath,
                DirectoryPath = directoryPath,
                Title = metadata.Title,
                Duration = metadata.Duration,
                AlbumArtUriFromTrack = metadata.CoverArtUri,
                LightSwatchId = metadata.LightSwatchId,
                DarkSwatchId = metadata.DarkSwatchId,
                Year = metadata.Year,
                TrackNumber = metadata.TrackNumber,
                TrackCount = metadata.TrackCount,
                DiscNumber = metadata.DiscNumber,
                DiscCount = metadata.DiscCount,
                SampleRate = metadata.SampleRate,
                Bitrate = metadata.Bitrate,
                Channels = metadata.Channels,
                DateAddedToLibrary = DateTime.UtcNow,
                FileCreatedDate = metadata.FileCreatedDate,
                FileModifiedDate = metadata.FileModifiedDate,
                FolderId = folderId,
                Composer = metadata.Composer,
                Bpm = metadata.Bpm,
                Lyrics = metadata.Lyrics,
                LrcFilePath = metadata.LrcFilePath,
                ArtistId = trackArtist.Id,
                AlbumId = album?.Id,
                Genres = genres,
                Grouping = metadata.Grouping,
                Copyright = metadata.Copyright,
                Comment = metadata.Comment,
                Conductor = metadata.Conductor,
                MusicBrainzTrackId = metadata.MusicBrainzTrackId,
                MusicBrainzReleaseId = metadata.MusicBrainzReleaseId
            };

            if (album is not null && string.IsNullOrEmpty(album.CoverArtUri) &&
                !string.IsNullOrEmpty(metadata.CoverArtUri))
                album.CoverArtUri = metadata.CoverArtUri;

            context.Songs.Add(song);
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
        await using var context = await _contextFactory.CreateDbContextAsync();
        var song = await context.Songs.FindAsync(songId);
        if (song is null) return false;

        updateAction(song);
        try
        {
            await context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Database update failed for song ID {SongId}.", songId);
            return false;
        }
    }

    private async Task FetchAndUpdateArtistFromRemoteAsync(MusicDbContext context, Artist artist)
    {
        using var httpClient = _httpClientFactory.CreateClient("ImageDownloader");
        var lastFmResultTask = _lastFmService.GetArtistInfoAsync(artist.Name);
        var spotifyResultTask = _spotifyService.GetArtistImageUrlAsync(artist.Name);
        await Task.WhenAll(lastFmResultTask, spotifyResultTask);

        var lastFmResult = await lastFmResultTask;
        var spotifyResult = await spotifyResultTask;

        if (!lastFmResult.IsConclusive || !spotifyResult.IsConclusive)
        {
            _logger.LogWarning(
                "Skipping metadata update for '{ArtistName}' due to a temporary service error. Will retry later.",
                artist.Name);
            return;
        }

        var wasMetadataFoundAndUpdated = false;

        if (lastFmResult.Status == ServiceResultStatus.Success && lastFmResult.Data?.Biography is not null)
        {
            artist.Biography = lastFmResult.Data.Biography;
            wasMetadataFoundAndUpdated = true;
        }

        if (spotifyResult.Status == ServiceResultStatus.Success && spotifyResult.Data?.ImageUrl is not null)
        {
            var downloadedPath =
                await DownloadAndCacheArtistImageAsync(artist, new Uri(spotifyResult.Data.ImageUrl), httpClient);
            if (!string.IsNullOrEmpty(downloadedPath))
            {
                artist.LocalImageCachePath = downloadedPath;
                wasMetadataFoundAndUpdated = true;
            }
        }

        artist.MetadataLastCheckedUtc = DateTime.UtcNow;
        await context.SaveChangesAsync();

        if (wasMetadataFoundAndUpdated)
            ArtistMetadataUpdated?.Invoke(this,
                new ArtistMetadataUpdatedEventArgs(artist.Id, artist.LocalImageCachePath));
    }

    private Task<string?> DownloadAndCacheArtistImageAsync(Artist artist, Uri imageUrl, HttpClient httpClient)
    {
        var lazyTask = _artistImageProcessingTasks.GetOrAdd(artist.Id, _ =>
            new Lazy<Task<string?>>(() =>
            {
                var localPath = _fileSystem.Combine(_pathConfig.ArtistImageCachePath, $"{artist.Id}.jpg");
                return DownloadAndWriteImageInternalAsync(localPath, imageUrl, httpClient);
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
            _artistImageProcessingTasks.TryRemove(artist.Id, out _);
            return Task.FromResult<string?>(null);
        }
    }

    private async Task<string?> DownloadAndWriteImageInternalAsync(string localPath, Uri imageUrl,
        HttpClient httpClient)
    {
        if (_fileSystem.FileExists(localPath)) return localPath;

        using var response = await httpClient.GetAsync(imageUrl);
        response.EnsureSuccessStatusCode();
        var imageBytes = await response.Content.ReadAsByteArrayAsync();
        await _fileSystem.WriteAllBytesAsync(localPath, imageBytes);
        return localPath;
    }

    private async Task<Artist> GetOrCreateArtistAsync(MusicDbContext context, string? name)
    {
        var normalizedName = string.IsNullOrWhiteSpace(name) ? UnknownArtistName : name.Trim();

        var trackedArtist = context.ChangeTracker.Entries<Artist>()
            .FirstOrDefault(e =>
                e.State == EntityState.Added &&
                e.Entity.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase))
            ?.Entity;

        if (trackedArtist is not null) return trackedArtist;

        var dbArtist = await context.Artists.FirstOrDefaultAsync(a => a.Name == normalizedName);
        if (dbArtist is not null) return dbArtist;

        var newArtist = new Artist { Name = normalizedName };
        context.Artists.Add(newArtist);
        return newArtist;
    }

    private async Task<Album> GetOrCreateAlbumAsync(MusicDbContext context, string title, Guid artistId, int? year)
    {
        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? UnknownAlbumName : title.Trim();

        var trackedAlbum = context.ChangeTracker.Entries<Album>()
            .FirstOrDefault(e => e.State == EntityState.Added &&
                                 e.Entity.ArtistId == artistId &&
                                 e.Entity.Title.Equals(normalizedTitle, StringComparison.OrdinalIgnoreCase))
            ?.Entity;

        var album = trackedAlbum ??
                    await context.Albums.FirstOrDefaultAsync(a => a.Title == normalizedTitle && a.ArtistId == artistId);

        if (album is not null)
        {
            if (album.Year is null && year.HasValue) album.Year = year;
            return album;
        }

        var newAlbum = new Album { Title = normalizedTitle, ArtistId = artistId, Year = year };
        context.Albums.Add(newAlbum);
        return newAlbum;
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
            .ToListAsync();

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

    private async Task ReindexPlaylistAsync(MusicDbContext context, Guid playlistId)
    {
        var playlistSongs = await context.PlaylistSongs
            .Where(ps => ps.PlaylistId == playlistId).OrderBy(ps => ps.Order).ToListAsync();

        for (var i = 0; i < playlistSongs.Count; i++) playlistSongs[i].Order = i;
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
                .ToListAsync(cancellationToken);

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
                        cancellationToken);

                if (hasSongs) pathsWithSongsOrDescendants.Add(folderPath);
            }

            var foldersToDelete = orphanedSubFolders
                .Where(f => !pathsWithSongsOrDescendants.Contains(f.Path))
                .Select(f => f.Id)
                .ToList();

            if (foldersToDelete.Any())
                await context.Folders
                    .Where(f => foldersToDelete.Contains(f.Id))
                    .ExecuteDeleteAsync(cancellationToken);
            else
                deletedAny = false;
        }

        await context.Albums.Where(a => !a.Songs.Any()).ExecuteDeleteAsync(cancellationToken);

        var orphanedArtists = await context.Artists
            .AsNoTracking()
            .Where(a => !a.Songs.Any() && !a.Albums.Any())
            .Select(a => new { a.Id, a.LocalImageCachePath })
            .ToListAsync(cancellationToken);

        if (orphanedArtists.Any())
        {
            var idsToDelete = orphanedArtists.Select(a => a.Id).ToList();
            await context.Artists.Where(a => idsToDelete.Contains(a.Id)).ExecuteDeleteAsync(cancellationToken);
            foreach (var artist in orphanedArtists)
                if (!string.IsNullOrEmpty(artist.LocalImageCachePath) &&
                    _fileSystem.FileExists(artist.LocalImageCachePath))
                    try
                    {
                        _fileSystem.DeleteFile(artist.LocalImageCachePath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete orphaned artist image {ArtistImagePath}.",
                            artist.LocalImageCachePath);
                    }
        }

        await context.Genres.Where(g => !g.Songs.Any()).ExecuteDeleteAsync(cancellationToken);
    }

    private IOrderedQueryable<Song> ApplySongSortOrder(IQueryable<Song> query, SongSortOrder sortOrder)
    {
        return sortOrder switch
        {
            SongSortOrder.TitleDesc => query.OrderByDescending(s => s.Title).ThenBy(s => s.Id),
            SongSortOrder.DateAddedDesc => query.OrderByDescending(s => s.DateAddedToLibrary).ThenBy(s => s.Title),
            SongSortOrder.DateAddedAsc => query.OrderBy(s => s.DateAddedToLibrary).ThenBy(s => s.Title),
            SongSortOrder.AlbumAsc or SongSortOrder.TrackNumberAsc => query
                .OrderBy(s => s.Album != null ? s.Album.Title : string.Empty)
                .ThenBy(s => s.DiscNumber ?? 0)
                .ThenBy(s => s.TrackNumber)
                .ThenBy(s => s.Title),
            SongSortOrder.ArtistAsc => query.OrderBy(s => s.Artist != null ? s.Artist.Name : string.Empty)
                .ThenBy(s => s.Album != null ? s.Album.Title : string.Empty)
                .ThenBy(s => s.DiscNumber ?? 0)
                .ThenBy(s => s.TrackNumber),
            _ => query.OrderBy(s => s.Title).ThenBy(s => s.Id)
        };
    }

    private IQueryable<Song> BuildSongSearchQuery(MusicDbContext context, string searchTerm)
    {
        // Leading wildcards prevent index usage; consider full-text search for large datasets
        var term = $"%{searchTerm}%";
        return context.Songs
            .Include(s => s.Artist)
            .Include(s => s.Album!.Artist)
            .Where(s =>
                EF.Functions.Like(s.Title, term)
                || (s.Artist != null && EF.Functions.Like(s.Artist.Name, term))
                || (s.Album != null && EF.Functions.Like(s.Album.Title, term))
                || (s.Album != null && s.Album.Artist != null && EF.Functions.Like(s.Album.Artist.Name, term))
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
            .Include(al => al.Artist)
            .Where(al => EF.Functions.Like(al.Title, term)
                         || (al.Artist != null && EF.Functions.Like(al.Artist.Name, term)));
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

    #region IDisposable Implementation

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _metadataFetchCts.Cancel();
                _metadataFetchCts.Dispose();
                _scanSemaphore.Dispose();
            }

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