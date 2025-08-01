using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nagi.Core.Data;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

/// <summary>
/// Manages all aspects of the music library, including file scanning, metadata, and database operations.
/// This service is designed to be a singleton and is internally thread-safe.
/// </summary>
public class LibraryService : ILibraryService {
    private const string UnknownArtistName = "Unknown Artist";
    private const string UnknownAlbumName = "Unknown Album";

    private static readonly HashSet<string> MusicFileExtensions = new(new[]
    {
        // Audio Formats
        ".aa", ".aax", ".aac", ".aiff", ".ape", ".dsf", ".flac",
        ".m4a", ".m4b", ".m4p", ".mp3", ".mpc", ".mpp", ".ogg",
        ".oga", ".wav", ".wma", ".wv", ".webm",
        // Video Formats (treated as audio)
        ".mkv", ".ogv", ".avi", ".wmv", ".asf", ".mp4", ".m4v",
        ".mpeg", ".mpg", ".mpe", ".mpv", ".m2v"
    }, StringComparer.OrdinalIgnoreCase);

    private readonly IDbContextFactory<MusicDbContext> _contextFactory;
    private readonly IFileSystemService _fileSystem;
    private readonly IMetadataService _metadataService;
    private readonly ILastFmMetadataService _lastFmService;
    private readonly ISpotifyService _spotifyService;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IPathConfiguration _pathConfig;
    private readonly HttpClient _httpClient;

    // State management for the singleton background artist metadata fetch task.
    private readonly object _metadataFetchLock = new();
    private volatile bool _isMetadataFetchRunning;
    private CancellationTokenSource _metadataFetchCts;

    // Prevents race conditions when downloading images for the same artist concurrently.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _artistImageWriteSemaphores = new();

    public LibraryService(
        IDbContextFactory<MusicDbContext> contextFactory,
        IFileSystemService fileSystem,
        IMetadataService metadataService,
        ILastFmMetadataService lastFmService,
        ISpotifyService spotifyService,
        IHttpClientFactory httpClientFactory,
        IServiceScopeFactory serviceScopeFactory,
        IPathConfiguration pathConfig) {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _lastFmService = lastFmService ?? throw new ArgumentNullException(nameof(lastFmService));
        _spotifyService = spotifyService ?? throw new ArgumentNullException(nameof(spotifyService));
        _httpClient = httpClientFactory.CreateClient("ImageDownloader");
        _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
        _pathConfig = pathConfig ?? throw new ArgumentNullException(nameof(pathConfig));
        _metadataFetchCts = new CancellationTokenSource();
    }

    /// <summary>
    /// Occurs when an artist's metadata (e.g., biography, image) has been successfully updated from a remote source.
    /// </summary>
    public event EventHandler<ArtistMetadataUpdatedEventArgs>? ArtistMetadataUpdated;

    #region Folder Management

    /// <summary>
    /// Adds a new folder to the library by its path. If the folder already exists, it returns the existing entry.
    /// </summary>
    /// <param name="path">The absolute path of the folder to add.</param>
    /// <param name="name">An optional display name for the folder. If null, the folder's directory name is used.</param>
    /// <returns>The added or existing <see cref="Folder"/>, or null if the path is invalid.</returns>
    public async Task<Folder?> AddFolderAsync(string path, string? name = null) {
        if (string.IsNullOrWhiteSpace(path)) return null;

        await using var context = await _contextFactory.CreateDbContextAsync();
        var existingFolder = await context.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.Path == path);
        if (existingFolder is not null) {
            return existingFolder;
        }

        var folder = new Folder { Path = path, Name = name ?? _fileSystem.GetFileNameWithoutExtension(path) ?? "" };
        try {
            folder.LastModifiedDate = _fileSystem.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) {
            Debug.WriteLine($"[{nameof(LibraryService)}] Could not get LastWriteTimeUtc for folder '{path}'. {ex.Message}");
            folder.LastModifiedDate = null;
        }

        context.Folders.Add(folder);
        await context.SaveChangesAsync();
        return folder;
    }

    /// <summary>
    /// Removes a folder and all its associated songs and cached files (album art, lyrics) from the library.
    /// The operation is transactional, and physical files are only deleted after the database transaction succeeds.
    /// </summary>
    /// <param name="folderId">The ID of the folder to remove.</param>
    /// <returns>True if the folder was successfully removed; otherwise, false.</returns>
    public async Task<bool> RemoveFolderAsync(Guid folderId) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var folder = await context.Folders.FindAsync(folderId);
        if (folder is null) return false;

        List<string> albumArtPathsToDelete;
        List<string> lrcPathsToDelete;

        await using var transaction = await context.Database.BeginTransactionAsync();
        try {
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

            context.Folders.Remove(folder);
            await context.SaveChangesAsync();

            await CleanUpOrphanedEntitiesAsync(context);

            await transaction.CommitAsync();
        }
        catch (Exception ex) {
            Debug.WriteLine($"[{nameof(LibraryService)}] Folder removal for ID '{folderId}' failed and was rolled back. Exception: {ex}");
            await transaction.RollbackAsync();
            return false;
        }

        foreach (var artPath in albumArtPathsToDelete) {
            try {
                if (_fileSystem.FileExists(artPath)) {
                    _fileSystem.DeleteFile(artPath);
                }
            }
            catch (Exception ex) {
                Debug.WriteLine($"[{nameof(LibraryService)}] Failed to delete album art file '{artPath}'. {ex.Message}");
            }
        }

        foreach (var lrcPath in lrcPathsToDelete) {
            if (IsPathInLrcCache(lrcPath)) {
                try {
                    _fileSystem.DeleteFile(lrcPath);
                }
                catch (Exception ex) {
                    Debug.WriteLine($"[{nameof(LibraryService)}] Failed to delete cached LRC file '{lrcPath}'. {ex.Message}");
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Updates the properties of an existing folder in the database.
    /// </summary>
    /// <param name="folder">The folder entity with updated information.</param>
    /// <returns>True if the update was successful.</returns>
    public async Task<bool> UpdateFolderAsync(Folder folder) {
        ArgumentNullException.ThrowIfNull(folder);
        await using var context = await _contextFactory.CreateDbContextAsync();

        try {
            folder.LastModifiedDate = _fileSystem.GetLastWriteTimeUtc(folder.Path);
        }
        catch (Exception ex) {
            Debug.WriteLine($"[{nameof(LibraryService)}] Could not get LastWriteTimeUtc for folder '{folder.Path}'. {ex.Message}");
        }

        context.Folders.Update(folder);
        await context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Retrieves a folder by its unique ID.
    /// </summary>
    /// <param name="folderId">The ID of the folder.</param>
    /// <returns>The <see cref="Folder"/>, or null if not found.</returns>
    public async Task<Folder?> GetFolderByIdAsync(Guid folderId) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.Id == folderId);
    }

    /// <summary>
    /// Retrieves a folder by its absolute path.
    /// </summary>
    /// <param name="path">The path of the folder.</param>
    /// <returns>The <see cref="Folder"/>, or null if not found.</returns>
    public async Task<Folder?> GetFolderByPathAsync(string path) {
        if (string.IsNullOrWhiteSpace(path)) return null;
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.Path == path);
    }

    /// <summary>
    /// Retrieves all folders in the library, ordered by name.
    /// </summary>
    /// <returns>An enumerable collection of all <see cref="Folder"/> objects.</returns>
    public async Task<IEnumerable<Folder>> GetAllFoldersAsync() {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Folders.AsNoTracking().OrderBy(f => f.Name).ThenBy(f => f.Path).ToListAsync();
    }

    /// <summary>
    /// Gets the total number of songs within a specific folder.
    /// </summary>
    /// <param name="folderId">The ID of the folder.</param>
    /// <returns>The count of songs in the folder.</returns>
    public async Task<int> GetSongCountForFolderAsync(Guid folderId) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Songs.CountAsync(s => s.FolderId == folderId);
    }

    #endregion

    #region Library Scanning

    /// <summary>
    /// Scans a folder path for music files, adding it to the library if necessary. This is a convenience wrapper for <see cref="RescanFolderForMusicAsync"/>.
    /// </summary>
    /// <param name="folderPath">The absolute path of the folder to scan.</param>
    /// <param name="progress">An optional progress reporter for UI updates.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    public async Task ScanFolderForMusicAsync(string folderPath, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default) {
        var folder = await GetFolderByPathAsync(folderPath) ?? await AddFolderAsync(folderPath);
        if (folder is null) {
            progress?.Report(new ScanProgress { StatusText = "Failed to add folder.", Percentage = 100 });
            return;
        }
        await RescanFolderForMusicAsync(folder.Id, progress, cancellationToken);
    }

    /// <summary>
    /// Rescans an existing library folder, adding new files, updating changed files, and removing deleted files and their associated caches.
    /// </summary>
    /// <param name="folderId">The ID of the folder to rescan.</param>
    /// <param name="progress">An optional progress reporter for UI updates.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>True if the scan completed and made changes; otherwise, false. Returns false if cancelled or an error occurred.</returns>
    public Task<bool> RescanFolderForMusicAsync(Guid folderId, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default) {
        // Offloads the entire scan to a background thread to avoid blocking the caller.
        return Task.Run(async () => {
            try {
                var folder = await GetFolderByIdAsync(folderId);
                if (folder is null) {
                    progress?.Report(new ScanProgress { StatusText = "Folder not found.", Percentage = 100 });
                    return false;
                }

                cancellationToken.ThrowIfCancellationRequested();

                if (!_fileSystem.DirectoryExists(folder.Path)) {
                    progress?.Report(new ScanProgress { StatusText = "Folder path no longer exists. Removing from library.", Percentage = 100 });
                    return await RemoveFolderAsync(folderId);
                }

                // Determine which files are new, modified, or deleted by comparing the database to the file system.
                progress?.Report(new ScanProgress { StatusText = $"Analyzing '{folder.Name}'...", IsIndeterminate = true });
                var (filesToAdd, filesToUpdate, filesToDelete) = await AnalyzeFolderChangesAsync(folderId, folder.Path, cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();

                if (filesToDelete.Any()) {
                    progress?.Report(new ScanProgress { StatusText = "Cleaning up your library...", IsIndeterminate = true });
                    await using var deleteContext = await _contextFactory.CreateDbContextAsync();

                    // Before performing a bulk delete, retrieve the paths of associated cache files (art, lyrics) to prevent orphans.
                    // This is critical because ExecuteDeleteAsync bypasses entity tracking and any C# cleanup logic.
                    var songsToDeleteQuery = deleteContext.Songs
                        .Where(s => s.FolderId == folderId && filesToDelete.Contains(s.FilePath));

                    var lrcPathsToDelete = await songsToDeleteQuery
                        .Where(s => s.LrcFilePath != null)
                        .Select(s => s.LrcFilePath!)
                        .ToListAsync(cancellationToken);

                    var albumArtPathsToDelete = await songsToDeleteQuery
                        .Where(s => s.AlbumArtUriFromTrack != null)
                        .Select(s => s.AlbumArtUriFromTrack!)
                        .ToListAsync(cancellationToken);

                    await songsToDeleteQuery.ExecuteDeleteAsync(cancellationToken);

                    foreach (var lrcPath in lrcPathsToDelete) {
                        if (IsPathInLrcCache(lrcPath)) {
                            try {
                                _fileSystem.DeleteFile(lrcPath);
                            }
                            catch (Exception ex) {
                                Debug.WriteLine($"[{nameof(LibraryService)}] Failed to delete cached LRC file '{lrcPath}' during rescan. {ex.Message}");
                            }
                        }
                    }

                    foreach (var artPath in albumArtPathsToDelete) {
                        try {
                            if (_fileSystem.FileExists(artPath)) {
                                _fileSystem.DeleteFile(artPath);
                            }
                        }
                        catch (Exception ex) {
                            Debug.WriteLine($"[{nameof(LibraryService)}] Failed to delete orphaned album art file '{artPath}' during rescan. {ex.Message}");
                        }
                    }
                }

                var filesToProcess = filesToAdd.Concat(filesToUpdate).ToList();
                if (!filesToProcess.Any()) {
                    progress?.Report(new ScanProgress { StatusText = "Scan complete. No new songs found.", Percentage = 100 });
                    return filesToDelete.Any();
                }

                // Process all new and updated files in parallel to accelerate metadata extraction.
                var extractedMetadata = await ExtractMetadataConcurrentlyAsync(filesToProcess, progress, cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();

                // Persist all extracted metadata to the database in a single, optimized batch operation.
                int newSongsFound = 0;
                if (extractedMetadata.Any()) {
                    newSongsFound = await BatchUpdateDatabaseAsync(folderId, extractedMetadata, progress, cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Remove any artists, albums, or genres that are no longer referenced by any songs.
                progress?.Report(new ScanProgress { StatusText = "Finalizing...", IsIndeterminate = true });
                await using (var finalContext = await _contextFactory.CreateDbContextAsync()) {
                    await CleanUpOrphanedEntitiesAsync(finalContext, cancellationToken);
                }

                var pluralSong = newSongsFound == 1 ? "song" : "songs";
                var summary = newSongsFound > 0
                    ? $"Scan complete. Added {newSongsFound:N0} new {pluralSong}."
                    : "Scan complete. No new songs found.";
                progress?.Report(new ScanProgress { StatusText = summary, Percentage = 100, NewSongsFound = newSongsFound });
                return true;
            }
            catch (OperationCanceledException) {
                Debug.WriteLine($"[{nameof(LibraryService)}] Scan for folder ID '{folderId}' was cancelled.");
                progress?.Report(new ScanProgress { StatusText = "Scan cancelled by user.", Percentage = 100 });
                return false;
            }
            catch (Exception ex) {
                Debug.WriteLine($"[{nameof(LibraryService)}] FATAL: Rescan for folder ID '{folderId}' failed. Exception: {ex}");
                progress?.Report(new ScanProgress { StatusText = "An error occurred during the scan. Please check the logs.", Percentage = 100 });
                return false;
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Rescans all folders currently in the library.
    /// </summary>
    /// <param name="progress">An optional progress reporter for UI updates.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>True if any folder scan resulted in changes; otherwise, false.</returns>
    public async Task<bool> RefreshAllFoldersAsync(IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default) {
        var folders = (await GetAllFoldersAsync()).ToList();
        var totalFolders = folders.Count;

        if (totalFolders == 0) {
            progress?.Report(new ScanProgress { StatusText = "No folders in the library to refresh.", Percentage = 100 });
            return false;
        }

        var foldersProcessed = 0;
        var anyChangesMade = false;

        foreach (var folder in folders) {
            cancellationToken.ThrowIfCancellationRequested();

            // Wraps the progress reporter to provide contextual updates about which folder is being scanned.
            var progressWrapper = new Progress<ScanProgress>(scanProgress => {
                var status = scanProgress.Percentage >= 100
                    ? scanProgress.StatusText
                    : $"({foldersProcessed + 1}/{totalFolders}) {scanProgress.StatusText}";

                progress?.Report(new ScanProgress {
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

    /// <summary>
    /// Retrieves detailed information for an artist, optionally fetching missing data (biography, image) from online services.
    /// </summary>
    /// <param name="artistId">The ID of the artist to retrieve.</param>
    /// <param name="allowOnlineFetch">If true, the service will attempt to fetch missing metadata from remote APIs.</param>
    /// <returns>The <see cref="Artist"/> with details, or null if not found.</returns>
    public async Task<Artist?> GetArtistDetailsAsync(Guid artistId, bool allowOnlineFetch) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var artist = await context.Artists.AsTracking().FirstOrDefaultAsync(a => a.Id == artistId);

        if (artist is null) return null;

        var needsUpdate = string.IsNullOrWhiteSpace(artist.Biography) || string.IsNullOrWhiteSpace(artist.RemoteImageUrl);
        if (allowOnlineFetch && needsUpdate) {
            await FetchAndUpdateArtistFromRemoteAsync(context, artist);
        }

        return await context.Artists.AsNoTracking().FirstOrDefaultAsync(a => a.Id == artistId);
    }

    /// <summary>
    /// Starts a long-running background task to find artists missing metadata and fetch it from online services.
    /// The task is designed to be resilient and will not run if it's already active.
    /// </summary>
    public Task StartArtistMetadataBackgroundFetchAsync() {
        lock (_metadataFetchLock) {
            if (_isMetadataFetchRunning) return Task.CompletedTask;

            if (_metadataFetchCts.IsCancellationRequested) {
                _metadataFetchCts.Dispose();
                _metadataFetchCts = new CancellationTokenSource();
            }
            _isMetadataFetchRunning = true;
        }

        var token = _metadataFetchCts.Token;

        _ = Task.Run(async () => {
            try {
                const int batchSize = 50;
                while (!token.IsCancellationRequested) {
                    List<Guid> artistIdsToUpdate;
                    await using (var idContext = await _contextFactory.CreateDbContextAsync()) {
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
                    var scopedContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MusicDbContext>>();
                    await using var batchContext = await scopedContextFactory.CreateDbContextAsync();

                    var artistsInBatch = await batchContext.Artists.Where(a => artistIdsToUpdate.Contains(a.Id)).ToListAsync(token);
                    foreach (var artist in artistsInBatch) {
                        if (token.IsCancellationRequested) break;
                        try {
                            await FetchAndUpdateArtistFromRemoteAsync(batchContext, artist);
                        }
                        catch (DbUpdateConcurrencyException) {
                            Debug.WriteLine($"[{nameof(LibraryService)}] Concurrency conflict for artist {artist.Id} during background fetch. Ignoring.");
                        }
                        catch (Exception ex) {
                            Debug.WriteLine($"[{nameof(LibraryService)}] Failed to update artist {artist.Id} in background. {ex.Message}");
                        }
                    }
                }
            }
            catch (OperationCanceledException) {
                Debug.WriteLine($"[{nameof(LibraryService)}] Artist metadata background fetch was cancelled.");
            }
            finally {
                lock (_metadataFetchLock) {
                    _isMetadataFetchRunning = false;
                }
            }
        }, token);
        return Task.CompletedTask;
    }

    #endregion

    #region Song Management

    /// <summary>
    /// Adds a single song to the database. If a song with the same file path already exists, the existing song is returned.
    /// </summary>
    /// <param name="songData">The song entity to add.</param>
    /// <returns>The newly added or existing <see cref="Song"/>.</returns>
    public async Task<Song?> AddSongAsync(Song songData) {
        ArgumentNullException.ThrowIfNull(songData);
        await using var context = await _contextFactory.CreateDbContextAsync();

        var existingSong = await context.Songs.AsNoTracking().FirstOrDefaultAsync(s => s.FilePath == songData.FilePath);
        if (existingSong is not null) return existingSong;

        context.Songs.Add(songData);
        await context.SaveChangesAsync();
        return songData;
    }

    /// <summary>
    /// Adds a new song to the database using its extracted metadata, creating related artist, album, and genre entities as needed.
    /// </summary>
    /// <param name="folderId">The ID of the folder containing the song.</param>
    /// <param name="metadata">The extracted metadata of the song file.</param>
    /// <returns>The newly created <see cref="Song"/>, or null if creation failed.</returns>
    public async Task<Song?> AddSongWithDetailsAsync(Guid folderId, SongFileMetadata metadata) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var song = await AddSongWithDetailsAsync(context, folderId, metadata);
        if (song is not null) {
            await context.SaveChangesAsync();
        }
        return song;
    }

    /// <summary>
    /// Removes a song from the database by its ID and cleans up any associated cached files.
    /// </summary>
    /// <param name="songId">The ID of the song to remove.</param>
    /// <returns>True if the song was successfully removed; otherwise, false.</returns>
    public async Task<bool> RemoveSongAsync(Guid songId) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var song = await context.Songs.FindAsync(songId);
        if (song is null) return false;

        var albumArtPathToDelete = song.AlbumArtUriFromTrack;
        var lrcPathToDelete = song.LrcFilePath;

        context.Songs.Remove(song);
        await context.SaveChangesAsync();

        if (IsPathInLrcCache(lrcPathToDelete)) {
            try {
                _fileSystem.DeleteFile(lrcPathToDelete!);
            }
            catch (Exception ex) {
                Debug.WriteLine($"[{nameof(LibraryService)}] Failed to delete cached LRC file '{lrcPathToDelete}'. {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(albumArtPathToDelete) && _fileSystem.FileExists(albumArtPathToDelete)) {
            try {
                _fileSystem.DeleteFile(albumArtPathToDelete);
            }
            catch (Exception ex) {
                Debug.WriteLine($"[{nameof(LibraryService)}] Failed to delete album art file '{albumArtPathToDelete}'. {ex.Message}");
            }
        }

        await CleanUpOrphanedEntitiesAsync(context);
        return true;
    }

    /// <summary>
    /// Retrieves a song by its unique ID, including its related artist, album, and folder.
    /// </summary>
    /// <param name="songId">The ID of the song.</param>
    /// <returns>The <see cref="Song"/>, or null if not found.</returns>
    public async Task<Song?> GetSongByIdAsync(Guid songId) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Songs.AsNoTracking()
            .Include(s => s.Artist)
            .Include(s => s.Album).ThenInclude(a => a!.Artist)
            .Include(s => s.Folder)
            .AsSplitQuery()
            .FirstOrDefaultAsync(s => s.Id == songId);
    }

    /// <summary>
    /// Retrieves a song by its file path, including its related artist, album, and folder.
    /// </summary>
    /// <param name="filePath">The absolute file path of the song.</param>
    /// <returns>The <see cref="Song"/>, or null if not found.</returns>
    public async Task<Song?> GetSongByFilePathAsync(string filePath) {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Songs.AsNoTracking()
            .Include(s => s.Artist)
            .Include(s => s.Album).ThenInclude(a => a!.Artist)
            .Include(s => s.Folder)
            .AsSplitQuery()
            .FirstOrDefaultAsync(s => s.FilePath == filePath);
    }

    /// <summary>
    /// Retrieves a collection of songs by their unique IDs, including related data.
    /// </summary>
    /// <param name="songIds">An enumerable of song IDs to retrieve.</param>
    /// <returns>A dictionary mapping song IDs to the corresponding <see cref="Song"/> objects.</returns>
    public async Task<IReadOnlyDictionary<Guid, Song>> GetSongsByIdsAsync(IEnumerable<Guid> songIds) {
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

    /// <summary>
    /// Updates an existing song in the database.
    /// </summary>
    /// <param name="songToUpdate">The song entity with updated information.</param>
    /// <returns>True if the update was successful.</returns>
    public async Task<bool> UpdateSongAsync(Song songToUpdate) {
        ArgumentNullException.ThrowIfNull(songToUpdate);
        await using var context = await _contextFactory.CreateDbContextAsync();
        context.Songs.Update(songToUpdate);
        await context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Retrieves all songs from the library with a specified sort order, including related data.
    /// </summary>
    /// <param name="sortOrder">The order in which to sort the songs.</param>
    /// <returns>An enumerable collection of all <see cref="Song"/> objects.</returns>
    public async Task<IEnumerable<Song>> GetAllSongsAsync(SongSortOrder sortOrder = SongSortOrder.TitleAsc) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        IQueryable<Song> query = context.Songs.AsNoTracking()
            .Include(s => s.Artist)
            .Include(s => s.Album).ThenInclude(a => a!.Artist);

        return await ApplySongSortOrder(query, sortOrder).AsSplitQuery().ToListAsync();
    }

    /// <summary>
    /// Retrieves all songs belonging to a specific album, ordered by track number.
    /// </summary>
    /// <param name="albumId">The ID of the album.</param>
    /// <returns>An enumerable collection of <see cref="Song"/> objects from the album.</returns>
    public async Task<IEnumerable<Song>> GetSongsByAlbumIdAsync(Guid albumId) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Songs.AsNoTracking()
            .Where(s => s.AlbumId == albumId)
            .Include(s => s.Artist)
            .OrderBy(s => s.TrackNumber).ThenBy(s => s.Title)
            .ToListAsync();
    }

    /// <summary>
    /// Retrieves all songs by a specific artist, ordered by album and track number.
    /// </summary>
    /// <param name="artistId">The ID of the artist.</param>
    /// <returns>An enumerable collection of <see cref="Song"/> objects by the artist.</returns>
    public async Task<IEnumerable<Song>> GetSongsByArtistIdAsync(Guid artistId) {
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

    /// <summary>
    /// Retrieves all songs within a specific folder, ordered by album and track number.
    /// </summary>
    /// <param name="folderId">The ID of the folder.</param>
    /// <returns>An enumerable collection of <see cref="Song"/> objects from the folder.</returns>
    public async Task<IEnumerable<Song>> GetSongsByFolderIdAsync(Guid folderId) {
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

    /// <summary>
    /// Searches for songs across titles, artists, and albums.
    /// </summary>
    /// <param name="searchTerm">The text to search for.</param>
    /// <returns>An enumerable collection of matching <see cref="Song"/> objects.</returns>
    public async Task<IEnumerable<Song>> SearchSongsAsync(string searchTerm) {
        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetAllSongsAsync();

        await using var context = await _contextFactory.CreateDbContextAsync();
        return await BuildSongSearchQuery(context, searchTerm.Trim())
            .OrderBy(s => s.Title).ThenBy(s => s.Id)
            .AsSplitQuery()
            .ToListAsync();
    }

    #endregion

    #region Song Metadata Updates

    /// <summary>
    /// Sets the star rating for a song.
    /// </summary>
    /// <param name="songId">The ID of the song to update.</param>
    /// <param name="rating">The rating from 1 to 5, or null to clear the rating.</param>
    /// <returns>True if the update was successful.</returns>
    public Task<bool> SetSongRatingAsync(Guid songId, int? rating) {
        if (rating.HasValue && (rating < 1 || rating > 5))
            throw new ArgumentOutOfRangeException(nameof(rating), "Rating must be between 1 and 5.");

        return UpdateSongPropertyAsync(songId, s => s.Rating = rating);
    }

    /// <summary>
    /// Sets the "loved" status for a song.
    /// </summary>
    /// <param name="songId">The ID of the song to update.</param>
    /// <param name="isLoved">True to mark the song as loved; otherwise, false.</param>
    /// <returns>True if the update was successful.</returns>
    public Task<bool> SetSongLovedStatusAsync(Guid songId, bool isLoved) {
        return UpdateSongPropertyAsync(songId, s => s.IsLoved = isLoved);
    }

    /// <summary>
    /// Updates the lyrics for a song.
    /// </summary>
    /// <param name="songId">The ID of the song to update.</param>
    /// <param name="lyrics">The new lyrics text, or null to clear it.</param>
    /// <returns>True if the update was successful.</returns>
    public Task<bool> UpdateSongLyricsAsync(Guid songId, string? lyrics) {
        return UpdateSongPropertyAsync(songId, s => s.Lyrics = lyrics);
    }

    /// <summary>
    /// Updates the file path to a synchronized lyrics file (.lrc) for a song.
    /// </summary>
    /// <param name="songId">The ID of the song to update.</param>
    /// <param name="lrcPath">The new file path, or null to clear it.</param>
    /// <returns>True if the update was successful.</returns>
    public Task<bool> UpdateSongLrcPathAsync(Guid songId, string? lrcPath) {
        return UpdateSongPropertyAsync(songId, s => s.LrcFilePath = lrcPath);
    }

    #endregion

    #region Artist Management

    /// <summary>
    /// Retrieves an artist by their unique ID.
    /// </summary>
    /// <param name="artistId">The ID of the artist.</param>
    /// <returns>The <see cref="Artist"/>, or null if not found.</returns>
    public async Task<Artist?> GetArtistByIdAsync(Guid artistId) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Artists.AsNoTracking().FirstOrDefaultAsync(a => a.Id == artistId);
    }

    /// <summary>
    /// Retrieves an artist by their name.
    /// </summary>
    /// <param name="name">The name of the artist.</param>
    /// <returns>The <see cref="Artist"/>, or null if not found.</returns>
    public async Task<Artist?> GetArtistByNameAsync(string name) {
        if (string.IsNullOrWhiteSpace(name)) return null;
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Artists.AsNoTracking().FirstOrDefaultAsync(a => a.Name == name.Trim());
    }

    /// <summary>
    /// Retrieves all artists in the library, ordered by name.
    /// </summary>
    /// <returns>An enumerable collection of all <see cref="Artist"/> objects.</returns>
    public async Task<IEnumerable<Artist>> GetAllArtistsAsync() {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Artists.AsNoTracking().OrderBy(a => a.Name).ToListAsync();
    }

    /// <summary>
    /// Searches for artists by name.
    /// </summary>
    /// <param name="searchTerm">The text to search for.</param>
    /// <returns>An enumerable collection of matching <see cref="Artist"/> objects.</returns>
    public async Task<IEnumerable<Artist>> SearchArtistsAsync(string searchTerm) {
        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetAllArtistsAsync();

        await using var context = await _contextFactory.CreateDbContextAsync();
        return await BuildArtistSearchQuery(context, searchTerm.Trim()).AsNoTracking().OrderBy(a => a.Name).ToListAsync();
    }

    #endregion

    #region Album Management

    /// <summary>
    /// Retrieves an album by its unique ID, including its artist.
    /// </summary>
    /// <param name="albumId">The ID of the album.</param>
    /// <returns>The <see cref="Album"/>, or null if not found.</returns>
    public async Task<Album?> GetAlbumByIdAsync(Guid albumId) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Albums.AsNoTracking()
            .Include(al => al.Artist)
            .AsSplitQuery()
            .FirstOrDefaultAsync(al => al.Id == albumId);
    }

    /// <summary>
    /// Retrieves all albums in the library, ordered by artist then title.
    /// </summary>
    /// <returns>An enumerable collection of all <see cref="Album"/> objects.</returns>
    public async Task<IEnumerable<Album>> GetAllAlbumsAsync() {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Albums.AsNoTracking()
            .Include(al => al.Artist)
            .OrderBy(al => al.Artist != null ? al.Artist.Name : string.Empty)
            .ThenBy(al => al.Title)
            .AsSplitQuery()
            .ToListAsync();
    }

    /// <summary>
    /// Searches for albums by title or artist name.
    /// </summary>
    /// <param name="searchTerm">The text to search for.</param>
    /// <returns>An enumerable collection of matching <see cref="Album"/> objects.</returns>
    public async Task<IEnumerable<Album>> SearchAlbumsAsync(string searchTerm) {
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

    /// <summary>
    /// Creates a new, empty playlist.
    /// </summary>
    /// <param name="name">The name of the playlist. Cannot be empty.</param>
    /// <param name="description">An optional description for the playlist.</param>
    /// <param name="coverImageUri">An optional URI for the playlist's cover image.</param>
    /// <returns>The newly created <see cref="Playlist"/>.</returns>
    public async Task<Playlist?> CreatePlaylistAsync(string name, string? description = null, string? coverImageUri = null) {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Playlist name cannot be empty.", nameof(name));

        await using var context = await _contextFactory.CreateDbContextAsync();
        var playlist = new Playlist {
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

    /// <summary>
    /// Deletes a playlist and its associations with songs. The songs themselves are not deleted.
    /// </summary>
    /// <param name="playlistId">The ID of the playlist to delete.</param>
    /// <returns>True if the playlist was deleted; otherwise, false.</returns>
    public async Task<bool> DeletePlaylistAsync(Guid playlistId) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var rowsAffected = await context.Playlists.Where(p => p.Id == playlistId).ExecuteDeleteAsync();
        return rowsAffected > 0;
    }

    /// <summary>
    /// Renames an existing playlist.
    /// </summary>
    /// <param name="playlistId">The ID of the playlist to rename.</param>
    /// <param name="newName">The new name for the playlist. Cannot be empty.</param>
    /// <returns>True if the rename was successful; otherwise, false.</returns>
    public async Task<bool> RenamePlaylistAsync(Guid playlistId, string newName) {
        if (string.IsNullOrWhiteSpace(newName)) throw new ArgumentException("New playlist name cannot be empty.", nameof(newName));

        await using var context = await _contextFactory.CreateDbContextAsync();
        var playlist = await context.Playlists.FindAsync(playlistId);
        if (playlist is null) return false;

        playlist.Name = newName.Trim();
        playlist.DateModified = DateTime.UtcNow;
        await context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Updates the cover image URI for a playlist.
    /// </summary>
    /// <param name="playlistId">The ID of the playlist to update.</param>
    /// <param name="newCoverImageUri">The new cover image URI, or null to remove it.</param>
    /// <returns>True if the update was successful; otherwise, false.</returns>
    public async Task<bool> UpdatePlaylistCoverAsync(Guid playlistId, string? newCoverImageUri) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var playlist = await context.Playlists.FindAsync(playlistId);
        if (playlist is null) return false;

        playlist.CoverImageUri = newCoverImageUri;
        playlist.DateModified = DateTime.UtcNow;
        await context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Adds a collection of songs to the end of a playlist, ignoring any duplicates.
    /// </summary>
    /// <param name="playlistId">The ID of the playlist.</param>
    /// <param name="songIds">The IDs of the songs to add.</param>
    /// <returns>True if the operation was successful; otherwise, false.</returns>
    public async Task<bool> AddSongsToPlaylistAsync(Guid playlistId, IEnumerable<Guid> songIds) {
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

        var maxOrder = await context.PlaylistSongs.Where(ps => ps.PlaylistId == playlistId).MaxAsync(ps => (int?)ps.Order) ?? -1;
        var playlistSongsToAdd = songIdsToAdd.Select(songId => new PlaylistSong {
            PlaylistId = playlistId,
            SongId = songId,
            Order = ++maxOrder
        });

        context.PlaylistSongs.AddRange(playlistSongsToAdd);
        playlist.DateModified = DateTime.UtcNow;
        await context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Removes a collection of songs from a playlist and re-indexes the remaining songs.
    /// </summary>
    /// <param name="playlistId">The ID of the playlist.</param>
    /// <param name="songIds">The IDs of the songs to remove.</param>
    /// <returns>True if the operation was successful; otherwise, false.</returns>
    public async Task<bool> RemoveSongsFromPlaylistAsync(Guid playlistId, IEnumerable<Guid> songIds) {
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

    /// <summary>
    /// Updates the order of songs in a playlist based on a provided list of song IDs.
    /// </summary>
    /// <param name="playlistId">The ID of the playlist.</param>
    /// <param name="orderedSongIds">An enumerable of song IDs in the desired new order.</param>
    /// <returns>True if the update was successful; otherwise, false.</returns>
    public async Task<bool> UpdatePlaylistSongOrderAsync(Guid playlistId, IEnumerable<Guid> orderedSongIds) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var playlist = await context.Playlists.FindAsync(playlistId);
        if (playlist is null) return false;

        var playlistSongs = await context.PlaylistSongs
            .Where(ps => ps.PlaylistId == playlistId)
            .ToListAsync();

        var songMap = playlistSongs.ToDictionary(ps => ps.SongId);
        var newOrderList = orderedSongIds.ToList();

        for (var i = 0; i < newOrderList.Count; i++) {
            if (songMap.TryGetValue(newOrderList[i], out var playlistSong)) {
                playlistSong.Order = i;
            }
        }

        playlist.DateModified = DateTime.UtcNow;
        await context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Retrieves a playlist by its ID, including all its songs in order.
    /// </summary>
    /// <param name="playlistId">The ID of the playlist.</param>
    /// <returns>The <see cref="Playlist"/> with its songs, or null if not found.</returns>
    public async Task<Playlist?> GetPlaylistByIdAsync(Guid playlistId) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Playlists.AsNoTracking()
            .Include(p => p.PlaylistSongs.OrderBy(ps => ps.Order)).ThenInclude(ps => ps.Song).ThenInclude(s => s!.Artist)
            .Include(p => p.PlaylistSongs.OrderBy(ps => ps.Order)).ThenInclude(ps => ps.Song).ThenInclude(s => s!.Album).ThenInclude(a => a!.Artist)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == playlistId);
    }

    /// <summary>
    /// Retrieves all playlists in the library, ordered by name.
    /// </summary>
    /// <returns>An enumerable collection of all <see cref="Playlist"/> objects.</returns>
    public async Task<IEnumerable<Playlist>> GetAllPlaylistsAsync() {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Playlists.AsNoTracking()
            .Include(p => p.PlaylistSongs)
            .OrderBy(p => p.Name).ThenBy(p => p.Id)
            .ToListAsync();
    }

    /// <summary>
    /// Retrieves all songs in a specific playlist, in their correct order.
    /// </summary>
    /// <param name="playlistId">The ID of the playlist.</param>
    /// <returns>An ordered enumerable collection of <see cref="Song"/> objects.</returns>
    public async Task<IEnumerable<Song>> GetSongsInPlaylistOrderedAsync(Guid playlistId) {
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

    /// <summary>
    /// Retrieves all genres in the library, ordered by name.
    /// </summary>
    /// <returns>An enumerable collection of all <see cref="Genre"/> objects.</returns>
    public async Task<IEnumerable<Genre>> GetAllGenresAsync() {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Genres.AsNoTracking().OrderBy(g => g.Name).ToListAsync();
    }

    /// <summary>
    /// Retrieves all songs associated with a specific genre, ordered by title.
    /// </summary>
    /// <param name="genreId">The ID of the genre.</param>
    /// <returns>An enumerable collection of <see cref="Song"/> objects in the genre.</returns>
    public async Task<IEnumerable<Song>> GetSongsByGenreIdAsync(Guid genreId) {
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

    /// <summary>
    /// Creates a new listen history entry for a song and increments the song's play count.
    /// </summary>
    /// <param name="songId">The ID of the song that was listened to.</param>
    /// <returns>The ID of the newly created <see cref="ListenHistory"/> entry, or null if the song was not found.</returns>
    public async Task<long?> CreateListenHistoryEntryAsync(Guid songId) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var song = await context.Songs.FindAsync(songId);
        if (song is null) return null;

        song.PlayCount++;
        song.LastPlayedDate = DateTime.UtcNow;

        var historyEntry = new ListenHistory { SongId = songId, ListenTimestampUtc = DateTime.UtcNow, IsScrobbled = false };
        context.ListenHistory.Add(historyEntry);

        await context.SaveChangesAsync();
        return historyEntry.Id;
    }

    /// <summary>
    /// Marks a listen history entry as meeting the criteria for scrobbling (e.g., played for a certain duration).
    /// </summary>
    /// <param name="listenHistoryId">The ID of the listen history entry.</param>
    /// <returns>True if the update was successful; otherwise, false.</returns>
    public async Task<bool> MarkListenAsEligibleForScrobblingAsync(long listenHistoryId) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var historyEntry = await context.ListenHistory.FindAsync(listenHistoryId);
        if (historyEntry is null) return false;

        historyEntry.IsEligibleForScrobbling = true;
        await context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Marks a listen history entry as having been successfully scrobbled to an external service.
    /// </summary>
    /// <param name="listenHistoryId">The ID of the listen history entry.</param>
    /// <returns>True if the update was successful; otherwise, false.</returns>
    public async Task<bool> MarkListenAsScrobbledAsync(long listenHistoryId) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var historyEntry = await context.ListenHistory.FindAsync(listenHistoryId);
        if (historyEntry is null) return false;

        historyEntry.IsScrobbled = true;
        await context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Increments the skip count for a song.
    /// </summary>
    /// <param name="songId">The ID of the song that was skipped.</param>
    public async Task LogSkipAsync(Guid songId) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var song = await context.Songs.FindAsync(songId);
        if (song is null) return;

        song.SkipCount++;
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Gets the total number of times a song has been listened to.
    /// </summary>
    /// <param name="songId">The ID of the song.</param>
    /// <returns>The total listen count for the song.</returns>
    public async Task<int> GetListenCountForSongAsync(Guid songId) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.ListenHistory.CountAsync(lh => lh.SongId == songId);
    }

    #endregion

    #region Paged Loading

    /// <summary>
    /// Retrieves a single page of all songs from the library.
    /// </summary>
    /// <param name="pageNumber">The one-based page number to retrieve.</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <param name="sortOrder">The order in which to sort the songs before paging.</param>
    /// <returns>A <see cref="PagedResult{T}"/> containing the songs for the specified page.</returns>
    public async Task<PagedResult<Song>> GetAllSongsPagedAsync(int pageNumber, int pageSize, SongSortOrder sortOrder = SongSortOrder.TitleAsc) {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Songs.AsNoTracking().Include(s => s.Artist).Include(s => s.Album).ThenInclude(a => a!.Artist);
        var totalCount = await query.CountAsync();
        var pagedData = await ApplySongSortOrder(query, sortOrder)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync();

        return new PagedResult<Song> { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <summary>
    /// Searches for songs and returns a single page of results.
    /// </summary>
    /// <param name="searchTerm">The text to search for.</param>
    /// <param name="pageNumber">The one-based page number to retrieve.</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <returns>A <see cref="PagedResult{T}"/> containing the matching songs for the specified page.</returns>
    public async Task<PagedResult<Song>> SearchSongsPagedAsync(string searchTerm, int pageNumber, int pageSize) {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = string.IsNullOrWhiteSpace(searchTerm)
            ? context.Songs.Include(s => s.Artist).Include(s => s.Album).ThenInclude(a => a!.Artist)
            : BuildSongSearchQuery(context, searchTerm.Trim());

        var totalCount = await query.CountAsync();
        var pagedData = await ApplySongSortOrder(query, SongSortOrder.TitleAsc)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync();

        return new PagedResult<Song> { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <summary>
    /// Retrieves a single page of songs from a specific album.
    /// </summary>
    /// <param name="albumId">The ID of the album.</param>
    /// <param name="pageNumber">The one-based page number to retrieve.</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <param name="sortOrder">The order in which to sort the songs before paging.</param>
    /// <returns>A <see cref="PagedResult{T}"/> containing the songs for the specified page.</returns>
    public async Task<PagedResult<Song>> GetSongsByAlbumIdPagedAsync(Guid albumId, int pageNumber, int pageSize, SongSortOrder sortOrder) {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Songs.AsNoTracking().Where(s => s.AlbumId == albumId).Include(s => s.Artist).Include(s => s.Album);
        var totalCount = await query.CountAsync();
        var pagedData = await ApplySongSortOrder(query, sortOrder)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync();

        return new PagedResult<Song> { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <summary>
    /// Retrieves a single page of songs by a specific artist.
    /// </summary>
    /// <param name="artistId">The ID of the artist.</param>
    /// <param name="pageNumber">The one-based page number to retrieve.</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <param name="sortOrder">The order in which to sort the songs before paging.</param>
    /// <returns>A <see cref="PagedResult{T}"/> containing the songs for the specified page.</returns>
    public async Task<PagedResult<Song>> GetSongsByArtistIdPagedAsync(Guid artistId, int pageNumber, int pageSize, SongSortOrder sortOrder) {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Songs.AsNoTracking().Where(s => s.ArtistId == artistId).Include(s => s.Artist).Include(s => s.Album).ThenInclude(a => a!.Artist);
        var totalCount = await query.CountAsync();
        var pagedData = await ApplySongSortOrder(query, sortOrder)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync();

        return new PagedResult<Song> { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <summary>
    /// Retrieves a single page of songs from a specific genre.
    /// </summary>
    /// <param name="genreId">The ID of the genre.</param>
    /// <param name="pageNumber">The one-based page number to retrieve.</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <param name="sortOrder">The order in which to sort the songs before paging.</param>
    /// <returns>A <see cref="PagedResult{T}"/> containing the songs for the specified page.</returns>
    public async Task<PagedResult<Song>> GetSongsByGenreIdPagedAsync(Guid genreId, int pageNumber, int pageSize, SongSortOrder sortOrder) {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Songs.AsNoTracking().Where(s => s.Genres.Any(g => g.Id == genreId)).Include(s => s.Artist).Include(s => s.Album).ThenInclude(a => a!.Artist);
        var totalCount = await query.CountAsync();
        var pagedData = await ApplySongSortOrder(query, sortOrder)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync();

        return new PagedResult<Song> { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <summary>
    /// Retrieves a single page of songs from a specific playlist, in playlist order.
    /// </summary>
    /// <param name="playlistId">The ID of the playlist.</param>
    /// <param name="pageNumber">The one-based page number to retrieve.</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <returns>A <see cref="PagedResult{T}"/> containing the songs for the specified page.</returns>
    public async Task<PagedResult<Song>> GetSongsByPlaylistPagedAsync(Guid playlistId, int pageNumber, int pageSize) {
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

        return new PagedResult<Song> { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <summary>
    /// Retrieves a single page of all artists from the library.
    /// </summary>
    /// <param name="pageNumber">The one-based page number to retrieve.</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <returns>A <see cref="PagedResult{T}"/> containing the artists for the specified page.</returns>
    public async Task<PagedResult<Artist>> GetAllArtistsPagedAsync(int pageNumber, int pageSize) {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Artists.AsNoTracking();
        var totalCount = await query.CountAsync();
        var pagedData = await query.OrderBy(a => a.Name)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).ToListAsync();

        return new PagedResult<Artist> { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <summary>
    /// Searches for artists and returns a single page of results.
    /// </summary>
    /// <param name="searchTerm">The text to search for.</param>
    /// <param name="pageNumber">The one-based page number to retrieve.</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <returns>A <see cref="PagedResult{T}"/> containing the matching artists for the specified page.</returns>
    public async Task<PagedResult<Artist>> SearchArtistsPagedAsync(string searchTerm, int pageNumber, int pageSize) {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = string.IsNullOrWhiteSpace(searchTerm) ? context.Artists : BuildArtistSearchQuery(context, searchTerm.Trim());
        var totalCount = await query.CountAsync();
        var pagedData = await query.AsNoTracking().OrderBy(a => a.Name)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).ToListAsync();

        return new PagedResult<Artist> { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <summary>
    /// Retrieves a single page of all albums from the library.
    /// </summary>
    /// <param name="pageNumber">The one-based page number to retrieve.</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <returns>A <see cref="PagedResult{T}"/> containing the albums for the specified page.</returns>
    public async Task<PagedResult<Album>> GetAllAlbumsPagedAsync(int pageNumber, int pageSize) {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Albums.AsNoTracking().Include(al => al.Artist);
        var totalCount = await query.CountAsync();
        var pagedData = await query
            .OrderBy(al => al.Artist != null ? al.Artist.Name : string.Empty).ThenBy(al => al.Title)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync();

        return new PagedResult<Album> { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <summary>
    /// Searches for albums and returns a single page of results.
    /// </summary>
    /// <param name="searchTerm">The text to search for.</param>
    /// <param name="pageNumber">The one-based page number to retrieve.</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <returns>A <see cref="PagedResult{T}"/> containing the matching albums for the specified page.</returns>
    public async Task<PagedResult<Album>> SearchAlbumsPagedAsync(string searchTerm, int pageNumber, int pageSize) {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = string.IsNullOrWhiteSpace(searchTerm) ? context.Albums.Include(al => al.Artist) : BuildAlbumSearchQuery(context, searchTerm.Trim());
        var totalCount = await query.CountAsync();
        var pagedData = await query.AsNoTracking()
            .OrderBy(al => al.Artist != null ? al.Artist.Name : string.Empty).ThenBy(al => al.Title)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync();

        return new PagedResult<Album> { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <summary>
    /// Retrieves a single page of all playlists from the library.
    /// </summary>
    /// <param name="pageNumber">The one-based page number to retrieve.</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <returns>A <see cref="PagedResult{T}"/> containing the playlists for the specified page.</returns>
    public async Task<PagedResult<Playlist>> GetAllPlaylistsPagedAsync(int pageNumber, int pageSize) {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Playlists.AsNoTracking().Include(p => p.PlaylistSongs);
        var totalCount = await query.CountAsync();
        var pagedData = await query
            .OrderBy(p => p.Name).ThenBy(p => p.Id)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).ToListAsync();

        return new PagedResult<Playlist> { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <summary>
    /// Retrieves a single page of songs from a specific folder.
    /// </summary>
    /// <param name="folderId">The ID of the folder.</param>
    /// <param name="pageNumber">The one-based page number to retrieve.</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <param name="sortOrder">The order in which to sort the songs before paging.</param>
    /// <returns>A <see cref="PagedResult{T}"/> containing the songs for the specified page.</returns>
    public async Task<PagedResult<Song>> GetSongsByFolderIdPagedAsync(Guid folderId, int pageNumber, int pageSize, SongSortOrder sortOrder = SongSortOrder.TitleAsc) {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Songs.AsNoTracking().Where(s => s.FolderId == folderId).Include(s => s.Artist).Include(s => s.Album).ThenInclude(a => a!.Artist);
        var totalCount = await query.CountAsync();
        var pagedData = await ApplySongSortOrder(query, sortOrder)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync();

        return new PagedResult<Song> { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <summary>
    /// Retrieves a list of all song IDs in the library, with a specified sort order.
    /// </summary>
    /// <param name="sortOrder">The order in which to sort the songs before retrieving IDs.</param>
    /// <returns>A list of song GUIDs.</returns>
    public async Task<List<Guid>> GetAllSongIdsAsync(SongSortOrder sortOrder) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await ApplySongSortOrder(context.Songs.AsNoTracking(), sortOrder).Select(s => s.Id).ToListAsync();
    }

    /// <summary>
    /// Retrieves a list of all song IDs in a specific folder, with a specified sort order.
    /// </summary>
    /// <param name="folderId">The ID of the folder.</param>
    /// <param name="sortOrder">The order in which to sort the songs.</param>
    /// <returns>A list of song GUIDs.</returns>
    public async Task<List<Guid>> GetAllSongIdsByFolderIdAsync(Guid folderId, SongSortOrder sortOrder) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Songs.AsNoTracking().Where(s => s.FolderId == folderId);
        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync();
    }

    /// <summary>
    /// Retrieves a list of all song IDs by a specific artist, with a specified sort order.
    /// </summary>
    /// <param name="artistId">The ID of the artist.</param>
    /// <param name="sortOrder">The order in which to sort the songs.</param>
    /// <returns>A list of song GUIDs.</returns>
    public async Task<List<Guid>> GetAllSongIdsByArtistIdAsync(Guid artistId, SongSortOrder sortOrder) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Songs.AsNoTracking().Where(s => s.ArtistId == artistId);
        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync();
    }

    /// <summary>
    /// Retrieves a list of all song IDs in a specific album, with a specified sort order.
    /// </summary>
    /// <param name="albumId">The ID of the album.</param>
    /// <param name="sortOrder">The order in which to sort the songs.</param>
    /// <returns>A list of song GUIDs.</returns>
    public async Task<List<Guid>> GetAllSongIdsByAlbumIdAsync(Guid albumId, SongSortOrder sortOrder) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Songs.AsNoTracking().Where(s => s.AlbumId == albumId);
        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync();
    }

    /// <summary>
    /// Retrieves a list of all song IDs in a specific playlist, in playlist order.
    /// </summary>
    /// <param name="playlistId">The ID of the playlist.</param>
    /// <returns>An ordered list of song GUIDs.</returns>
    public async Task<List<Guid>> GetAllSongIdsByPlaylistIdAsync(Guid playlistId) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.PlaylistSongs.AsNoTracking()
            .Where(ps => ps.PlaylistId == playlistId).OrderBy(ps => ps.Order).Select(ps => ps.SongId).ToListAsync();
    }

    /// <summary>
    /// Retrieves a list of all song IDs in a specific genre, with a specified sort order.
    /// </summary>
    /// <param name="genreId">The ID of the genre.</param>
    /// <param name="sortOrder">The order in which to sort the songs.</param>
    /// <returns>A list of song GUIDs.</returns>
    public async Task<List<Guid>> GetAllSongIdsByGenreIdAsync(Guid genreId, SongSortOrder sortOrder) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Songs.AsNoTracking().Where(s => s.Genres.Any(g => g.Id == genreId));
        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync();
    }

    #endregion

    #region Scoped Search

    /// <summary>
    /// Searches for songs within a specific folder.
    /// </summary>
    /// <param name="folderId">The ID of the folder to search within.</param>
    /// <param name="searchTerm">The text to search for.</param>
    /// <returns>An enumerable collection of matching <see cref="Song"/> objects.</returns>
    public async Task<IEnumerable<Song>> SearchSongsInFolderAsync(Guid folderId, string searchTerm) {
        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetSongsByFolderIdAsync(folderId);

        await using var context = await _contextFactory.CreateDbContextAsync();
        var term = $"%{searchTerm.Trim()}%";
        return await context.Songs.AsNoTracking().Where(s => s.FolderId == folderId
            && (EF.Functions.Like(s.Title, term)
                || s.Album != null && EF.Functions.Like(s.Album.Title, term)
                || s.Artist != null && EF.Functions.Like(s.Artist.Name, term)))
            .Include(s => s.Artist).Include(s => s.Album).ThenInclude(a => a!.Artist)
            .OrderBy(s => s.Title).AsSplitQuery().ToListAsync();
    }

    /// <summary>
    /// Searches for songs within a specific album.
    /// </summary>
    /// <param name="albumId">The ID of the album to search within.</param>
    /// <param name="searchTerm">The text to search for.</param>
    /// <returns>An enumerable collection of matching <see cref="Song"/> objects.</returns>
    public async Task<IEnumerable<Song>> SearchSongsInAlbumAsync(Guid albumId, string searchTerm) {
        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetSongsByAlbumIdAsync(albumId);

        await using var context = await _contextFactory.CreateDbContextAsync();
        var term = $"%{searchTerm.Trim()}%";
        return await context.Songs.AsNoTracking().Where(s => s.AlbumId == albumId
            && (EF.Functions.Like(s.Title, term) || s.Artist != null && EF.Functions.Like(s.Artist.Name, term)))
            .Include(s => s.Artist).Include(s => s.Album)
            .OrderBy(s => s.TrackNumber).AsSplitQuery().ToListAsync();
    }

    /// <summary>
    /// Searches for songs by a specific artist.
    /// </summary>
    /// <param name="artistId">The ID of the artist to search within.</param>
    /// <param name="searchTerm">The text to search for.</param>
    /// <returns>An enumerable collection of matching <see cref="Song"/> objects.</returns>
    public async Task<IEnumerable<Song>> SearchSongsInArtistAsync(Guid artistId, string searchTerm) {
        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetSongsByArtistIdAsync(artistId);

        await using var context = await _contextFactory.CreateDbContextAsync();
        var term = $"%{searchTerm.Trim()}%";
        return await context.Songs.AsNoTracking().Where(s => s.ArtistId == artistId
            && (EF.Functions.Like(s.Title, term) || s.Album != null && EF.Functions.Like(s.Album.Title, term)))
            .Include(s => s.Artist).Include(s => s.Album)
            .OrderBy(s => s.Album != null ? s.Album.Title : string.Empty).ThenBy(s => s.TrackNumber)
            .AsSplitQuery()
            .ToListAsync();
    }

    /// <summary>
    /// Searches for songs within a specific folder and returns a paged result.
    /// </summary>
    /// <param name="folderId">The ID of the folder to search within.</param>
    /// <param name="searchTerm">The text to search for.</param>
    /// <param name="pageNumber">The one-based page number to retrieve.</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <returns>A <see cref="PagedResult{T}"/> containing the matching songs for the specified page.</returns>
    public async Task<PagedResult<Song>> SearchSongsInFolderPagedAsync(Guid folderId, string searchTerm, int pageNumber, int pageSize) {
        SanitizePaging(ref pageNumber, ref pageSize);
        await using var context = await _contextFactory.CreateDbContextAsync();

        var query = context.Songs.AsNoTracking().Where(s => s.FolderId == folderId);
        if (!string.IsNullOrWhiteSpace(searchTerm)) {
            var term = $"%{searchTerm.Trim()}%";
            query = query.Where(s => EF.Functions.Like(s.Title, term)
                || s.Album != null && EF.Functions.Like(s.Album.Title, term)
                || s.Artist != null && EF.Functions.Like(s.Artist.Name, term));
        }

        query = query.Include(s => s.Artist).Include(s => s.Album).ThenInclude(a => a!.Artist);
        var totalCount = await query.CountAsync();
        var pagedData = await ApplySongSortOrder(query, SongSortOrder.TitleAsc)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync();

        return new PagedResult<Song> { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <summary>
    /// Searches for songs within a specific album and returns a paged result.
    /// </summary>
    /// <param name="albumId">The ID of the album to search within.</param>
    /// <param name="searchTerm">The text to search for.</param>
    /// <param name="pageNumber">The one-based page number to retrieve.</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <returns>A <see cref="PagedResult{T}"/> containing the matching songs for the specified page.</returns>
    public async Task<PagedResult<Song>> SearchSongsInAlbumPagedAsync(Guid albumId, string searchTerm, int pageNumber, int pageSize) {
        SanitizePaging(ref pageNumber, ref pageSize);
        await using var context = await _contextFactory.CreateDbContextAsync();

        var query = context.Songs.AsNoTracking().Where(s => s.AlbumId == albumId);
        if (!string.IsNullOrWhiteSpace(searchTerm)) {
            var term = $"%{searchTerm.Trim()}%";
            query = query.Where(s => EF.Functions.Like(s.Title, term) || s.Artist != null && EF.Functions.Like(s.Artist.Name, term));
        }

        query = query.Include(s => s.Artist).Include(s => s.Album);
        var totalCount = await query.CountAsync();
        var pagedData = await ApplySongSortOrder(query, SongSortOrder.TrackNumberAsc)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync();

        return new PagedResult<Song> { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    /// <summary>
    /// Searches for songs by a specific artist and returns a paged result.
    /// </summary>
    /// <param name="artistId">The ID of the artist to search within.</param>
    /// <param name="searchTerm">The text to search for.</param>
    /// <param name="pageNumber">The one-based page number to retrieve.</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <returns>A <see cref="PagedResult{T}"/> containing the matching songs for the specified page.</returns>
    public async Task<PagedResult<Song>> SearchSongsInArtistPagedAsync(Guid artistId, string searchTerm, int pageNumber, int pageSize) {
        SanitizePaging(ref pageNumber, ref pageSize);
        await using var context = await _contextFactory.CreateDbContextAsync();

        var query = context.Songs.AsNoTracking().Where(s => s.ArtistId == artistId);
        if (!string.IsNullOrWhiteSpace(searchTerm)) {
            var term = $"%{searchTerm.Trim()}%";
            query = query.Where(s => EF.Functions.Like(s.Title, term) || s.Album != null && EF.Functions.Like(s.Album.Title, term));
        }

        query = query.Include(s => s.Artist).Include(s => s.Album);
        var totalCount = await query.CountAsync();
        var pagedData = await ApplySongSortOrder(query, SongSortOrder.AlbumAsc)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync();

        return new PagedResult<Song> { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    #endregion

    #region Data Reset

    /// <summary>
    /// Deletes all data from the library, including all songs, artists, albums, playlists, and folders.
    /// Also clears all associated cache files (album art, artist images, LRC files).
    /// This is a highly destructive, non-recoverable operation.
    /// </summary>
    public async Task ClearAllLibraryDataAsync() {
        Debug.WriteLine($"[{nameof(LibraryService)}] Data reset requested. Cancelling background tasks...");
        _metadataFetchCts.Cancel();
        await Task.Delay(250);

        await using var context = await _contextFactory.CreateDbContextAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();

        try {
            await context.PlaylistSongs.ExecuteDeleteAsync();
            await context.ListenHistory.ExecuteDeleteAsync();
            await context.Songs.ExecuteDeleteAsync();
            await context.Playlists.ExecuteDeleteAsync();
            await context.Albums.ExecuteDeleteAsync();
            await context.Artists.ExecuteDeleteAsync();
            await context.Genres.ExecuteDeleteAsync();
            await context.Folders.ExecuteDeleteAsync();

            await transaction.CommitAsync();
        }
        catch (Exception ex) {
            await transaction.RollbackAsync();
            Debug.WriteLine($"[{nameof(LibraryService)}] Database reset failed and was rolled back. Exception: {ex}");
            throw;
        }

        var albumArtPath = _pathConfig.AlbumArtCachePath;
        var artistImagePath = _pathConfig.ArtistImageCachePath;
        var lrcCachePath = _pathConfig.LrcCachePath;

        if (_fileSystem.DirectoryExists(albumArtPath)) _fileSystem.DeleteDirectory(albumArtPath, true);
        if (_fileSystem.DirectoryExists(artistImagePath)) _fileSystem.DeleteDirectory(artistImagePath, true);
        if (_fileSystem.DirectoryExists(lrcCachePath)) _fileSystem.DeleteDirectory(lrcCachePath, true);

        _fileSystem.CreateDirectory(albumArtPath);
        _fileSystem.CreateDirectory(artistImagePath);
        _fileSystem.CreateDirectory(lrcCachePath);
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Determines if a file path is located within the app's managed LRC cache directory,
    /// which is necessary to prevent the deletion of user-provided external .lrc files.
    /// </summary>
    /// <param name="filePath">The file path to check.</param>
    /// <returns>True if the file is in the LRC cache; otherwise, false.</returns>
    private bool IsPathInLrcCache(string? filePath) {
        if (string.IsNullOrWhiteSpace(filePath)) return false;

        try {
            var lrcCachePath = _pathConfig.LrcCachePath;
            var normalizedFilePath = Path.GetFullPath(filePath);
            var normalizedCachePath = Path.GetFullPath(lrcCachePath);

            return normalizedFilePath.StartsWith(normalizedCachePath, StringComparison.OrdinalIgnoreCase)
                   && _fileSystem.FileExists(filePath);
        }
        catch (Exception ex) {
            Debug.WriteLine($"[{nameof(LibraryService)}] Could not validate LRC cache path for '{filePath}'. Reason: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Compares files on disk with database records to determine what has been added, modified, or deleted.
    /// </summary>
    private async Task<(List<string> filesToAdd, List<string> filesToUpdate, List<string> filesToDelete)> AnalyzeFolderChangesAsync(Guid folderId, string folderPath, CancellationToken cancellationToken) {
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
        .Select(path => {
            try { return new { Path = path, LastWriteTime = _fileSystem.GetLastWriteTimeUtc(path) }; }
            catch (IOException) { return null; }
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
        var filesToDelete = filesRemovedFromDisk.Concat(filesToUpdate).ToList();

        return (filesToAdd, filesToUpdate, filesToDelete);
    }

    /// <summary>
    /// Extracts metadata from a list of files concurrently to improve performance.
    /// </summary>
    private async Task<List<SongFileMetadata>> ExtractMetadataConcurrentlyAsync(List<string> filesToProcess, IProgress<ScanProgress>? progress, CancellationToken cancellationToken) {
        var extractedMetadata = new ConcurrentBag<SongFileMetadata>();
        int degreeOfParallelism = Environment.ProcessorCount;
        using var semaphore = new SemaphoreSlim(degreeOfParallelism);
        int processedCount = 0;
        int totalFiles = filesToProcess.Count;
        const int progressReportingBatchSize = 25;

        progress?.Report(new ScanProgress { StatusText = "Reading song details...", TotalFiles = totalFiles, Percentage = 0 });

        var extractionTasks = filesToProcess.Select(async filePath => {
            await semaphore.WaitAsync(cancellationToken);
            try {
                cancellationToken.ThrowIfCancellationRequested();
                var metadata = await _metadataService.ExtractMetadataAsync(filePath);
                if (!metadata.ExtractionFailed) {
                    extractedMetadata.Add(metadata);
                }
            }
            finally {
                int currentCount = Interlocked.Increment(ref processedCount);

                if (currentCount % progressReportingBatchSize == 0 || currentCount == totalFiles) {
                    progress?.Report(new ScanProgress {
                        StatusText = "Reading song details...",
                        CurrentFilePath = filePath,
                        Percentage = (double)currentCount / totalFiles * 100,
                        TotalFiles = totalFiles,
                        NewSongsFound = extractedMetadata.Count
                    });
                }
                semaphore.Release();
            }
        });

        await Task.WhenAll(extractionTasks);
        return extractedMetadata.ToList();
    }

    /// <summary>
    /// Updates the database with a batch of new or updated songs. This method is optimized for performance by
    /// pre-fetching existing entities and includes retry logic to handle potential concurrency conflicts.
    /// </summary>
    private async Task<int> BatchUpdateDatabaseAsync(Guid folderId, List<SongFileMetadata> metadataList, IProgress<ScanProgress>? progress, CancellationToken cancellationToken) {
        const int maxRetries = 3;
        int retryCount = 0;
        bool saveSucceeded = false;
        int totalMetadataCount = metadataList.Count;

        progress?.Report(new ScanProgress { StatusText = "Adding songs to your library...", IsIndeterminate = true, NewSongsFound = totalMetadataCount });

        while (retryCount < maxRetries && !saveSucceeded) {
            cancellationToken.ThrowIfCancellationRequested();
            try {
                await using var context = await _contextFactory.CreateDbContextAsync();

                var artistNames = metadataList.SelectMany(m => new[] { m.Artist, m.AlbumArtist }).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var albumTitles = metadataList.Select(m => m.Album).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var genreNames = metadataList.SelectMany(m => m.Genres ?? Enumerable.Empty<string>()).Where(g => !string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                var existingArtists = await context.Artists.Where(a => artistNames.Contains(a.Name)).ToDictionaryAsync(a => a.Name, StringComparer.OrdinalIgnoreCase, cancellationToken);
                var existingAlbums = await context.Albums.Where(a => albumTitles.Contains(a.Title)).ToListAsync(cancellationToken);
                var existingGenres = await context.Genres.Where(g => genreNames.Contains(g.Name)).ToDictionaryAsync(g => g.Name, StringComparer.OrdinalIgnoreCase, cancellationToken);

                foreach (var name in artistNames) {
                    if (!existingArtists.ContainsKey(name!)) {
                        var newArtist = new Artist { Name = name! };
                        context.Artists.Add(newArtist);
                        existingArtists[name!] = newArtist;
                    }
                }
                foreach (var name in genreNames) {
                    if (!existingGenres.ContainsKey(name!)) {
                        var newGenre = new Genre { Name = name! };
                        context.Genres.Add(newGenre);
                        existingGenres[name!] = newGenre;
                    }
                }

                foreach (var metadata in metadataList) {
                    await AddSongWithDetailsOptimizedAsync(context, folderId, metadata, existingArtists, existingAlbums, existingGenres);
                }

                await context.SaveChangesAsync(cancellationToken);
                saveSucceeded = true;
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex)) {
                retryCount++;
                Debug.WriteLine($"[{nameof(LibraryService)}] Concurrency conflict during batch save. Attempt {retryCount}/{maxRetries}. Error: {ex.InnerException?.Message}");
                if (retryCount >= maxRetries) {
                    Debug.WriteLine($"[{nameof(LibraryService)}] Batch save failed after max retries. The operation will be aborted.");
                    throw;
                }
                await Task.Delay(200 * retryCount, cancellationToken);
            }
        }

        return totalMetadataCount;
    }

    /// <summary>
    /// An optimized version of song creation that uses pre-fetched entity lookups to avoid database hits during a batch operation.
    /// </summary>
    private Task AddSongWithDetailsOptimizedAsync(
        MusicDbContext context,
        Guid folderId,
        SongFileMetadata metadata,
        Dictionary<string, Artist> artistLookup,
        List<Album> existingAlbumList,
        Dictionary<string, Genre> genreLookup) {
        var trackArtistName = string.IsNullOrWhiteSpace(metadata.Artist) ? UnknownArtistName : metadata.Artist.Trim();
        var albumArtistName = string.IsNullOrWhiteSpace(metadata.AlbumArtist) ? trackArtistName : metadata.AlbumArtist.Trim();

        var trackArtist = artistLookup[trackArtistName];
        var albumArtist = artistLookup[albumArtistName];

        Album? album = null;
        if (!string.IsNullOrWhiteSpace(metadata.Album)) {
            var albumTitle = metadata.Album.Trim();
            album = existingAlbumList.FirstOrDefault(a => a.Title.Equals(albumTitle, StringComparison.OrdinalIgnoreCase) && a.ArtistId == albumArtist.Id);
            if (album == null) {
                album = new Album { Title = albumTitle, ArtistId = albumArtist.Id, Year = metadata.Year };
                context.Albums.Add(album);
                existingAlbumList.Add(album);
            }
            else if (album.Year is null && metadata.Year.HasValue) {
                album.Year = metadata.Year;
            }
        }

        var genres = metadata.Genres?.Select(g => g.Trim()).Where(g => !string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.OrdinalIgnoreCase).Select(name => genreLookup[name]).ToList() ?? new List<Genre>();

        var song = new Song {
            FilePath = metadata.FilePath,
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

        if (album is not null && string.IsNullOrEmpty(album.CoverArtUri) && !string.IsNullOrEmpty(metadata.CoverArtUri)) {
            album.CoverArtUri = metadata.CoverArtUri;
        }

        context.Songs.Add(song);
        return Task.CompletedTask;
    }

    /// <summary>
    /// A non-optimized version of song creation for single additions, involving individual database lookups.
    /// </summary>
    private async Task<Song?> AddSongWithDetailsAsync(MusicDbContext context, Guid folderId, SongFileMetadata metadata) {
        try {
            var trackArtist = await GetOrCreateArtistAsync(context, metadata.Artist);
            var albumArtist = !string.IsNullOrWhiteSpace(metadata.AlbumArtist)
                ? await GetOrCreateArtistAsync(context, metadata.AlbumArtist)
                : trackArtist;

            var album = !string.IsNullOrWhiteSpace(metadata.Album)
                ? await GetOrCreateAlbumAsync(context, metadata.Album, albumArtist.Id, metadata.Year)
                : null;

            var genres = await EnsureGenresExistAsync(context, metadata.Genres);

            var song = new Song {
                FilePath = metadata.FilePath,
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

            if (album is not null && string.IsNullOrEmpty(album.CoverArtUri) && !string.IsNullOrEmpty(metadata.CoverArtUri)) {
                album.CoverArtUri = metadata.CoverArtUri;
            }

            context.Songs.Add(song);
            return song;
        }
        catch (Exception ex) {
            Debug.WriteLine($"[{nameof(LibraryService)}] Failed to prepare song entity for '{metadata.FilePath}'. Reason: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Updates a single property of a song in the database.
    /// </summary>
    private async Task<bool> UpdateSongPropertyAsync(Guid songId, Action<Song> updateAction) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var song = await context.Songs.FindAsync(songId);
        if (song is null) return false;

        updateAction(song);
        try {
            await context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex) {
            Debug.WriteLine($"[{nameof(LibraryService)}] Database update failed for song ID '{songId}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Fetches artist metadata from remote services and updates the local database record.
    /// </summary>
    private async Task FetchAndUpdateArtistFromRemoteAsync(MusicDbContext context, Artist artist) {
        var lastFmResultTask = _lastFmService.GetArtistInfoAsync(artist.Name);
        var spotifyResultTask = _spotifyService.GetArtistImageUrlAsync(artist.Name);
        await Task.WhenAll(lastFmResultTask, spotifyResultTask);

        var lastFmResult = lastFmResultTask.Result;
        var spotifyResult = spotifyResultTask.Result;

        if (!lastFmResult.IsConclusive || !spotifyResult.IsConclusive) {
            Debug.WriteLine($"[{nameof(LibraryService)}] Skipping metadata update for '{artist.Name}' due to a temporary service error. Will retry later.");
            return;
        }

        bool wasMetadataFoundAndUpdated = false;

        if (lastFmResult.Status == ServiceResultStatus.Success && lastFmResult.Data?.Biography is not null) {
            artist.Biography = lastFmResult.Data.Biography;
            wasMetadataFoundAndUpdated = true;
        }

        if (spotifyResult.Status == ServiceResultStatus.Success && spotifyResult.Data?.ImageUrl is not null) {
            artist.RemoteImageUrl = spotifyResult.Data.ImageUrl;
            if (await DownloadAndCacheArtistImageAsync(artist, new Uri(spotifyResult.Data.ImageUrl))) {
                wasMetadataFoundAndUpdated = true;
            }
        }

        artist.MetadataLastCheckedUtc = DateTime.UtcNow;
        await context.SaveChangesAsync();

        if (wasMetadataFoundAndUpdated) {
            ArtistMetadataUpdated?.Invoke(this, new ArtistMetadataUpdatedEventArgs(artist.Id, artist.LocalImageCachePath));
        }
    }

    /// <summary>
    /// Downloads an artist image and saves it to the local cache, using a semaphore to prevent race conditions.
    /// </summary>
    private async Task<bool> DownloadAndCacheArtistImageAsync(Artist artist, Uri imageUrl) {
        var localPath = _fileSystem.Combine(_pathConfig.ArtistImageCachePath, $"{artist.Id}.jpg");
        if (artist.LocalImageCachePath == localPath && _fileSystem.FileExists(localPath)) return false;

        var semaphore = _artistImageWriteSemaphores.GetOrAdd(localPath, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync();
        try {
            if (_fileSystem.FileExists(localPath)) return false;

            using var response = await _httpClient.GetAsync(imageUrl);
            response.EnsureSuccessStatusCode();
            var imageBytes = await response.Content.ReadAsByteArrayAsync();
            await _fileSystem.WriteAllBytesAsync(localPath, imageBytes);

            artist.LocalImageCachePath = localPath;
            return true;
        }
        catch (Exception ex) {
            Debug.WriteLine($"[{nameof(LibraryService)}] Failed to download image for artist '{artist.Name}'. {ex.Message}");
            return false;
        }
        finally {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Retrieves an artist from the database or creates a new one if it doesn't exist.
    /// Checks the EF Core change tracker first to avoid redundant database queries within a transaction.
    /// </summary>
    private async Task<Artist> GetOrCreateArtistAsync(MusicDbContext context, string? name) {
        var normalizedName = string.IsNullOrWhiteSpace(name) ? UnknownArtistName : name.Trim();

        var trackedArtist = context.ChangeTracker.Entries<Artist>()
            .FirstOrDefault(e => e.State == EntityState.Added && e.Entity.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase))
            ?.Entity;

        if (trackedArtist is not null) {
            return trackedArtist;
        }

        var dbArtist = await context.Artists.FirstOrDefaultAsync(a => a.Name == normalizedName);
        if (dbArtist is not null) {
            return dbArtist;
        }

        var newArtist = new Artist { Name = normalizedName };
        context.Artists.Add(newArtist);
        return newArtist;
    }

    /// <summary>
    /// Retrieves an album from the database or creates a new one if it doesn't exist.
    /// Checks the EF Core change tracker first to avoid redundant database queries.
    /// </summary>
    private async Task<Album> GetOrCreateAlbumAsync(MusicDbContext context, string title, Guid artistId, int? year) {
        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? UnknownAlbumName : title.Trim();

        var trackedAlbum = context.ChangeTracker.Entries<Album>()
            .FirstOrDefault(e => e.State == EntityState.Added &&
                                 e.Entity.ArtistId == artistId &&
                                 e.Entity.Title.Equals(normalizedTitle, StringComparison.OrdinalIgnoreCase))
            ?.Entity;

        if (trackedAlbum is not null) {
            if (trackedAlbum.Year is null && year.HasValue) trackedAlbum.Year = year;
            return trackedAlbum;
        }

        var dbAlbum = await context.Albums.FirstOrDefaultAsync(a => a.Title == normalizedTitle && a.ArtistId == artistId);
        if (dbAlbum is not null) {
            if (dbAlbum.Year is null && year.HasValue) dbAlbum.Year = year;
            return dbAlbum;
        }

        var newAlbum = new Album { Title = normalizedTitle, ArtistId = artistId, Year = year };
        context.Albums.Add(newAlbum);
        return newAlbum;
    }

    /// <summary>
    /// Ensures all specified genres exist in the database, creating any that are missing.
    /// </summary>
    private async Task<List<Genre>> EnsureGenresExistAsync(MusicDbContext context, IEnumerable<string>? genreNames) {
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

        foreach (var name in distinctNames) {
            if (existingGenresMap.TryGetValue(name, out var genre) || trackedGenresMap.TryGetValue(name, out genre)) {
                finalGenres.Add(genre);
            }
            else {
                var newGenre = new Genre { Name = name };
                context.Genres.Add(newGenre);
                finalGenres.Add(newGenre);
                trackedGenresMap.Add(name, newGenre);
            }
        }

        return finalGenres;
    }

    /// <summary>
    /// Re-calculates the 'Order' property for all songs in a playlist after a modification.
    /// </summary>
    private async Task ReindexPlaylistAsync(MusicDbContext context, Guid playlistId) {
        var playlistSongs = await context.PlaylistSongs
            .Where(ps => ps.PlaylistId == playlistId).OrderBy(ps => ps.Order).ToListAsync();

        for (var i = 0; i < playlistSongs.Count; i++) {
            playlistSongs[i].Order = i;
        }
    }

    /// <summary>
    /// Deletes any artists, albums, or genres that are no longer referenced by any songs, and cleans up associated artist images.
    /// </summary>
    private async Task CleanUpOrphanedEntitiesAsync(MusicDbContext context, CancellationToken cancellationToken = default) {
        await context.Albums.Where(a => !a.Songs.Any()).ExecuteDeleteAsync(cancellationToken);

        var orphanedArtists = await context.Artists
            .AsNoTracking()
            .Where(a => !a.Songs.Any() && !a.Albums.Any())
            .Select(a => new { a.Id, a.LocalImageCachePath })
            .ToListAsync(cancellationToken);

        if (orphanedArtists.Any()) {
            var idsToDelete = orphanedArtists.Select(a => a.Id).ToList();
            await context.Artists.Where(a => idsToDelete.Contains(a.Id)).ExecuteDeleteAsync(cancellationToken);
            foreach (var artist in orphanedArtists) {
                if (!string.IsNullOrEmpty(artist.LocalImageCachePath) && _fileSystem.FileExists(artist.LocalImageCachePath)) {
                    try {
                        _fileSystem.DeleteFile(artist.LocalImageCachePath);
                    }
                    catch (Exception ex) {
                        Debug.WriteLine($"[{nameof(LibraryService)}] Failed to delete orphaned artist image '{artist.LocalImageCachePath}'. {ex.Message}");
                    }
                }
            }
        }

        await context.Genres.Where(g => !g.Songs.Any()).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Applies a specific sort order to an IQueryable of songs.
    /// </summary>
    private IOrderedQueryable<Song> ApplySongSortOrder(IQueryable<Song> query, SongSortOrder sortOrder) {
        return sortOrder switch {
            SongSortOrder.TitleDesc => query.OrderByDescending(s => s.Title).ThenBy(s => s.Id),
            SongSortOrder.DateAddedDesc => query.OrderByDescending(s => s.DateAddedToLibrary).ThenBy(s => s.Title),
            SongSortOrder.DateAddedAsc => query.OrderBy(s => s.DateAddedToLibrary).ThenBy(s => s.Title),
            SongSortOrder.AlbumAsc or SongSortOrder.TrackNumberAsc => query.OrderBy(s => s.Album != null ? s.Album.Title : string.Empty).ThenBy(s => s.TrackNumber).ThenBy(s => s.Title),
            SongSortOrder.ArtistAsc => query.OrderBy(s => s.Artist != null ? s.Artist.Name : string.Empty).ThenBy(s => s.Album != null ? s.Album.Title : string.Empty).ThenBy(s => s.TrackNumber),
            _ => query.OrderBy(s => s.Title).ThenBy(s => s.Id)
        };
    }

    /// <summary>
    /// Constructs a query for searching songs based on a search term.
    /// </summary>
    private IQueryable<Song> BuildSongSearchQuery(MusicDbContext context, string searchTerm) {
        var term = $"%{searchTerm}%";
        return context.Songs
            .Include(s => s.Artist)
            .Include(s => s.Album!.Artist)
            .Where(s => EF.Functions.Like(s.Title, term)
                || s.Album != null && EF.Functions.Like(s.Album.Title, term)
                || s.Artist != null && EF.Functions.Like(s.Artist.Name, term)
                || s.Album != null && s.Album.Artist != null && EF.Functions.Like(s.Album.Artist.Name, term));
    }

    /// <summary>
    /// Constructs a query for searching artists based on a search term.
    /// </summary>
    private IQueryable<Artist> BuildArtistSearchQuery(MusicDbContext context, string searchTerm) {
        return context.Artists.Where(a => EF.Functions.Like(a.Name, $"%{searchTerm}%"));
    }

    /// <summary>
    /// Constructs a query for searching albums based on a search term.
    /// </summary>
    private IQueryable<Album> BuildAlbumSearchQuery(MusicDbContext context, string searchTerm) {
        var term = $"%{searchTerm}%";
        return context.Albums
            .Include(al => al.Artist)
            .Where(al => EF.Functions.Like(al.Title, term)
                || al.Artist != null && EF.Functions.Like(al.Artist.Name, term));
    }

    /// <summary>
    /// Checks if a DbUpdateException was caused by a unique constraint violation, used for batch update retry logic.
    /// </summary>
    private bool IsUniqueConstraintViolation(DbUpdateException ex) {
        var innerMessage = ex.InnerException?.Message ?? string.Empty;
        return innerMessage.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)
               || innerMessage.Contains("Violation of UNIQUE KEY constraint", StringComparison.OrdinalIgnoreCase)
               || innerMessage.Contains("duplicate key value violates unique constraint", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Ensures page number and page size are valid positive integers.
    /// </summary>
    private static void SanitizePaging(ref int pageNumber, ref int pageSize) {
        pageNumber = Math.Max(1, pageNumber);
        pageSize = Math.Max(1, pageSize);
    }

    #endregion
}