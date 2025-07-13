using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nagi.Data;
using Nagi.Helpers;
using Nagi.Models;
using Nagi.Services.Abstractions;
using Nagi.Services.Data;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Nagi.Services.Implementations;

/// <summary>
/// Provides a concrete implementation of ILibraryService, managing all aspects of the music library,
/// including file scanning, metadata extraction, and database operations.
/// </summary>
public class LibraryService : ILibraryService {
    private const int ScanBatchSize = 500;
    private const string UnknownArtistName = "Unknown Artist";
    private const string UnknownAlbumName = "Unknown Album";
    private static readonly string[] MusicFileExtensions = { ".mp3", ".flac", ".wav", ".ogg", ".m4a", ".wma", ".aac" };

    private static bool _isMetadataFetchRunning;
    private static readonly object _metadataFetchLock = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _artistImageWriteSemaphores = new();

    private readonly IDbContextFactory<MusicDbContext> _contextFactory;
    private readonly IFileSystemService _fileSystem;
    private readonly HttpClient _httpClient;
    private readonly ILastFmService _lastFmService;
    private readonly IMetadataExtractor _metadataExtractor;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ISpotifyService _spotifyService;
    private readonly PathConfiguration _pathConfig;

    public LibraryService(
        IDbContextFactory<MusicDbContext> contextFactory,
        IFileSystemService fileSystem,
        IMetadataExtractor metadataExtractor,
        ILastFmService lastFmService,
        ISpotifyService spotifyService,
        IHttpClientFactory httpClientFactory,
        IServiceScopeFactory serviceScopeFactory,
        PathConfiguration pathConfig) {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _metadataExtractor = metadataExtractor ?? throw new ArgumentNullException(nameof(metadataExtractor));
        _lastFmService = lastFmService ?? throw new ArgumentNullException(nameof(lastFmService));
        _spotifyService = spotifyService ?? throw new ArgumentNullException(nameof(spotifyService));
        _httpClient = httpClientFactory.CreateClient("ImageDownloader");
        _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
        _pathConfig = pathConfig ?? throw new ArgumentNullException(nameof(pathConfig));
    }

    public event EventHandler<ArtistMetadataUpdatedEventArgs>? ArtistMetadataUpdated;

    #region Folder Management

    public async Task<Folder?> AddFolderAsync(string path, string? name = null) {
        if (string.IsNullOrWhiteSpace(path)) return null;

        await using var context = await _contextFactory.CreateDbContextAsync();
        if (await context.Folders.AnyAsync(f => f.Path == path)) {
            return await context.Folders.FirstAsync(f => f.Path == path);
        }

        var folder = new Folder { Path = path, Name = name ?? _fileSystem.GetDirectoryNameFromPath(path) };
        try {
            folder.LastModifiedDate = _fileSystem.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) {
            Debug.WriteLine($"[LibraryService] Could not get LastWriteTimeUtc for folder '{path}'. {ex.Message}");
            folder.LastModifiedDate = null;
        }

        context.Folders.Add(folder);
        try {
            await context.SaveChangesAsync();
            return folder;
        }
        catch (DbUpdateException ex) {
            Debug.WriteLine($"[LibraryService] ERROR: Database update failed for path '{path}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            context.Entry(folder).State = EntityState.Detached;
            return null;
        }
    }

    public async Task<bool> RemoveFolderAsync(Guid folderId) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();

        try {
            var folder = await context.Folders.FindAsync(folderId);
            if (folder == null) {
                await transaction.RollbackAsync();
                return false;
            }

            // Identify all songs within the folder to be removed.
            var songsInFolderQuery = context.Songs.AsNoTracking().Where(s => s.FolderId == folderId);
            var songIdsToDelete = await songsInFolderQuery.Select(s => s.Id).ToListAsync();

            if (!songIdsToDelete.Any()) {
                context.Folders.Remove(folder);
                await context.SaveChangesAsync();
                await transaction.CommitAsync();
                return true;
            }

            // Collect associated data that might become orphaned.
            var albumArtPathsToDelete = await songsInFolderQuery
                .Where(s => s.AlbumArtUriFromTrack != null)
                .Select(s => s.AlbumArtUriFromTrack!)
                .Distinct()
                .ToListAsync();

            var associatedAlbumIds = await songsInFolderQuery
                .Where(s => s.AlbumId.HasValue)
                .Select(s => s.AlbumId!.Value)
                .Distinct()
                .ToListAsync();

            var associatedTrackArtistIds = await songsInFolderQuery
                .Where(s => s.ArtistId.HasValue)
                .Select(s => s.ArtistId!.Value)
                .Distinct()
                .ToListAsync();

            var associatedAlbumArtistIds = await context.Albums.AsNoTracking()
                .Where(a => associatedAlbumIds.Contains(a.Id))
                .Select(a => a.ArtistId)
                .Distinct()
                .ToListAsync();
            var allAssociatedArtistIds = associatedTrackArtistIds.Union(associatedAlbumArtistIds).Distinct().ToList();

            // Perform deletions using efficient bulk operations.
            await context.PlaylistSongs.Where(ps => songIdsToDelete.Contains(ps.SongId)).ExecuteDeleteAsync();
            await context.Songs.Where(s => songIdsToDelete.Contains(s.Id)).ExecuteDeleteAsync();

            // Clean up orphaned albums.
            if (associatedAlbumIds.Any()) {
                var stillReferencedAlbumIds = await context.Songs.AsNoTracking()
                    .Where(s => s.AlbumId.HasValue && associatedAlbumIds.Contains(s.AlbumId.Value))
                    .Select(s => s.AlbumId!.Value)
                    .Distinct().ToHashSetAsync();
                var orphanedAlbumIds = associatedAlbumIds.Except(stillReferencedAlbumIds).ToList();
                if (orphanedAlbumIds.Any()) {
                    await context.Albums.Where(a => orphanedAlbumIds.Contains(a.Id)).ExecuteDeleteAsync();
                }
            }

            // Clean up orphaned artists and their cached images.
            if (allAssociatedArtistIds.Any()) {
                var artistsWithSongs = await context.Songs.AsNoTracking()
                    .Where(s => s.ArtistId.HasValue && allAssociatedArtistIds.Contains(s.ArtistId.Value))
                    .Select(s => s.ArtistId!.Value).Distinct().ToHashSetAsync();

                var artistsWithAlbums = await context.Albums.AsNoTracking()
                    .Where(a => allAssociatedArtistIds.Contains(a.ArtistId))
                    .Select(a => a.ArtistId).Distinct().ToHashSetAsync();

                var stillReferencedArtistIds = artistsWithSongs.Union(artistsWithAlbums);
                var orphanedArtistIds = allAssociatedArtistIds.Except(stillReferencedArtistIds).ToList();

                if (orphanedArtistIds.Any()) {
                    var artistsToDelete = await context.Artists.AsNoTracking()
                        .Where(a => orphanedArtistIds.Contains(a.Id) && a.LocalImageCachePath != null)
                        .ToListAsync();
                    foreach (var artist in artistsToDelete) {
                        if (_fileSystem.FileExists(artist.LocalImageCachePath!)) {
                            try { _fileSystem.DeleteFile(artist.LocalImageCachePath!); }
                            catch (Exception fileEx) { Debug.WriteLine($"[LibraryService] Failed to delete artist image file '{artist.LocalImageCachePath}'. {fileEx.Message}"); }
                        }
                    }
                    await context.Artists.Where(a => orphanedArtistIds.Contains(a.Id)).ExecuteDeleteAsync();
                }
            }

            // Clean up orphaned genres.
            var orphanedGenreIds = await context.Genres
                .Where(g => !g.Songs.Any())
                .Select(g => g.Id)
                .ToListAsync();
            if (orphanedGenreIds.Any()) {
                await context.Genres.Where(g => orphanedGenreIds.Contains(g.Id)).ExecuteDeleteAsync();
            }

            context.Folders.Remove(folder);
            await context.SaveChangesAsync();

            await transaction.CommitAsync();

            // Clean up cached album art files from disk after transaction commits.
            foreach (var artPath in albumArtPathsToDelete) {
                if (_fileSystem.FileExists(artPath)) {
                    try { _fileSystem.DeleteFile(artPath); }
                    catch (Exception fileEx) { Debug.WriteLine($"[LibraryService] Failed to delete art file '{artPath}'. {fileEx.Message}"); }
                }
            }
            return true;
        }
        catch (Exception ex) {
            Debug.WriteLine($"[LibraryService] ERROR: Folder removal for ID '{folderId}' failed and was rolled back. Reason: {ex.Message}");
            await transaction.RollbackAsync();
            return false;
        }
    }

    public async Task<Folder?> GetFolderByIdAsync(Guid folderId) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.Id == folderId);
    }

    public async Task<Folder?> GetFolderByPathAsync(string path) {
        if (string.IsNullOrWhiteSpace(path)) return null;
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.Path == path);
    }

    public async Task<IEnumerable<Folder>> GetAllFoldersAsync() {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Folders.AsNoTracking().OrderBy(f => f.Name).ThenBy(f => f.Path).ToListAsync();
    }

    public async Task<bool> UpdateFolderAsync(Folder folder) {
        if (folder == null) throw new ArgumentNullException(nameof(folder));
        await using var context = await _contextFactory.CreateDbContextAsync();
        try {
            folder.LastModifiedDate = _fileSystem.GetLastWriteTimeUtc(folder.Path);
        }
        catch (Exception ex) {
            Debug.WriteLine($"[LibraryService] Could not get LastWriteTimeUtc for folder '{folder.Path}'. {ex.Message}");
        }

        context.Folders.Update(folder);
        try {
            await context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex) {
            Debug.WriteLine($"[LibraryService] ERROR: Database update failed for ID '{folder.Id}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            await context.Entry(folder).ReloadAsync();
            return false;
        }
    }

    public async Task<int> GetSongCountForFolderAsync(Guid folderId) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Songs.CountAsync(s => s.FolderId == folderId);
    }

    #endregion

    #region Library Scanning

    public async Task ScanFolderForMusicAsync(string folderPath, IProgress<ScanProgress>? progress = null) {
        if (string.IsNullOrWhiteSpace(folderPath) || !_fileSystem.DirectoryExists(folderPath)) {
            progress?.Report(new ScanProgress { StatusText = "Invalid folder path.", Percentage = 100 });
            return;
        }

        await using var context = await _contextFactory.CreateDbContextAsync();
        var folder = await GetOrCreateFolderForScanAsync(context, folderPath);
        if (folder == null) {
            progress?.Report(new ScanProgress { StatusText = "Failed to register folder in database.", Percentage = 100 });
            return;
        }

        var scanContext = new ScanContext(folder.Id, progress);
        context.ChangeTracker.AutoDetectChangesEnabled = false;

        try {
            progress?.Report(new ScanProgress { StatusText = "Comparing with existing library...", IsIndeterminate = true });
            var existingFilePathsInDb = (await context.Songs
                    .Where(s => s.FolderId == folder.Id)
                    .Select(s => s.FilePath)
                    .ToListAsync())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            progress?.Report(new ScanProgress { StatusText = "Discovering new music files...", IsIndeterminate = true });
            var newFilesToProcess = _fileSystem.EnumerateFiles(folder.Path, "*.*", SearchOption.AllDirectories)
                .Where(file => file is not null
                               && MusicFileExtensions.Contains(_fileSystem.GetExtension(file), StringComparer.OrdinalIgnoreCase)
                               && !existingFilePathsInDb.Contains(file));

            await ProcessNewFilesAsync(context, newFilesToProcess, scanContext);

            var finalStatus = scanContext.ErrorsOccurred
                ? $"Scan complete with errors. Added {scanContext.NewSongsAdded} new songs."
                : $"Scan complete. Added {scanContext.NewSongsAdded} new songs.";

            progress?.Report(new ScanProgress {
                StatusText = finalStatus,
                NewSongsFound = scanContext.NewSongsAdded,
                Percentage = 100.0
            });
        }
        finally {
            context.ChangeTracker.AutoDetectChangesEnabled = true;
        }
    }

    public async Task<bool> RescanFolderForMusicAsync(Guid folderId, IProgress<ScanProgress>? progress = null) {
        const int RescanChunkSize = 500;

        await using var context = await _contextFactory.CreateDbContextAsync();
        var folder = await context.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.Id == folderId);
        if (folder == null) {
            progress?.Report(new ScanProgress { StatusText = "Folder not found.", Percentage = 100 });
            return false;
        }

        if (!_fileSystem.DirectoryExists(folder.Path)) {
            progress?.Report(new ScanProgress { StatusText = "Folder path no longer exists. Removing from library.", Percentage = 100 });
            return await RemoveFolderAsync(folderId);
        }

        progress?.Report(new ScanProgress { StatusText = "Analyzing library for changes...", IsIndeterminate = true });
        context.ChangeTracker.AutoDetectChangesEnabled = false;
        var changesMade = false;

        await using var transaction = await context.Database.BeginTransactionAsync();
        try {
            var dbPathsMasterSet = (await context.Songs
                .Where(s => s.FolderId == folderId)
                .Select(s => s.FilePath)
                .ToListAsync())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var pathsToAdd = new List<string>();
            var pathsToUpdate = new List<string>();
            var totalFilesScanned = 0;
            var potentiallyOrphanedAlbumIds = new HashSet<Guid>();
            var potentiallyOrphanedArtistIds = new HashSet<Guid>();

            // Process files from disk in manageable chunks to avoid high memory usage.
            var diskFileChunks = _fileSystem.EnumerateFiles(folder.Path, "*.*", SearchOption.AllDirectories)
                .Where(file => file is not null && MusicFileExtensions.Contains(_fileSystem.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                .Chunk(RescanChunkSize);

            foreach (var chunkOfDiskPaths in diskFileChunks) {
                var dbDataForChunk = await context.Songs
                    .AsNoTracking()
                    .Where(s => s.FolderId == folderId && chunkOfDiskPaths.Contains(s.FilePath))
                    .Select(s => new { s.FilePath, s.FileModifiedDate, s.AlbumId, s.ArtistId })
                    .ToDictionaryAsync(s => s.FilePath, s => s, StringComparer.OrdinalIgnoreCase);

                foreach (var diskFilePath in chunkOfDiskPaths) {
                    dbPathsMasterSet.Remove(diskFilePath);

                    DateTime diskLastWriteTime;
                    try { diskLastWriteTime = _fileSystem.GetLastWriteTimeUtc(diskFilePath); }
                    catch (Exception ex) {
                        Debug.WriteLine($"[LibraryService] WARNING: Could not get LastWriteTime for '{diskFilePath}'. Skipping. Reason: {ex.Message}");
                        continue;
                    }

                    if (dbDataForChunk.TryGetValue(diskFilePath, out var dbSongInfo)) {
                        // File exists in DB, check if it was modified.
                        if (diskLastWriteTime != dbSongInfo.FileModifiedDate) {
                            pathsToUpdate.Add(diskFilePath);
                            if (dbSongInfo.AlbumId.HasValue) potentiallyOrphanedAlbumIds.Add(dbSongInfo.AlbumId.Value);
                            if (dbSongInfo.ArtistId.HasValue) potentiallyOrphanedArtistIds.Add(dbSongInfo.ArtistId.Value);
                        }
                    }
                    else {
                        // File does not exist in DB, it's new.
                        pathsToAdd.Add(diskFilePath);
                    }
                }
                totalFilesScanned += chunkOfDiskPaths.Length;
                progress?.Report(new ScanProgress { StatusText = $"Scanning... ({totalFilesScanned} files checked)", IsIndeterminate = true });
            }

            // Any paths remaining in the master set were in the DB but not found on disk.
            var pathsToRemove = dbPathsMasterSet.ToList();
            if (pathsToRemove.Any()) {
                progress?.Report(new ScanProgress { StatusText = $"Removing {pathsToRemove.Count} deleted songs...", IsIndeterminate = true });
                var infoForDeletedSongs = await context.Songs.AsNoTracking()
                    .Where(s => s.FolderId == folderId && pathsToRemove.Contains(s.FilePath))
                    .Select(s => new { s.AlbumId, s.ArtistId }).ToListAsync();

                foreach (var info in infoForDeletedSongs) {
                    if (info.AlbumId.HasValue) potentiallyOrphanedAlbumIds.Add(info.AlbumId.Value);
                    if (info.ArtistId.HasValue) potentiallyOrphanedArtistIds.Add(info.ArtistId.Value);
                }

                await context.Songs.Where(s => s.FolderId == folderId && pathsToRemove.Contains(s.FilePath)).ExecuteDeleteAsync();
                changesMade = true;
            }

            if (pathsToUpdate.Any()) {
                progress?.Report(new ScanProgress { StatusText = $"Queueing {pathsToUpdate.Count} modified songs for update...", IsIndeterminate = true });
                await context.Songs.Where(s => s.FolderId == folderId && pathsToUpdate.Contains(s.FilePath)).ExecuteDeleteAsync();
                changesMade = true;
            }

            var filesToProcess = pathsToAdd.Concat(pathsToUpdate).ToList();
            if (filesToProcess.Any()) {
                progress?.Report(new ScanProgress { StatusText = $"Processing {filesToProcess.Count} new/modified songs...", IsIndeterminate = true });
                var scanContext = new ScanContext(folder.Id, progress);
                await ProcessNewFilesAsync(context, filesToProcess, scanContext);
                if (scanContext.NewSongsAdded > 0) changesMade = true;
            }

            if (potentiallyOrphanedAlbumIds.Any() || potentiallyOrphanedArtistIds.Any()) {
                var orphanedAlbumIds = await context.Albums
                    .Where(a => potentiallyOrphanedAlbumIds.Contains(a.Id) && !a.Songs.Any())
                    .Select(a => a.Id).ToListAsync();

                if (orphanedAlbumIds.Any()) {
                    var artistsOfOrphanedAlbums = await context.Albums
                        .Where(a => orphanedAlbumIds.Contains(a.Id)).Select(a => a.ArtistId).ToListAsync();
                    foreach (var artistId in artistsOfOrphanedAlbums) potentiallyOrphanedArtistIds.Add(artistId);

                    await context.Albums.Where(a => orphanedAlbumIds.Contains(a.Id)).ExecuteDeleteAsync();
                    changesMade = true;
                }

                var orphanedArtistIds = await context.Artists
                    .Where(a => potentiallyOrphanedArtistIds.Contains(a.Id) && !a.Songs.Any() && !a.Albums.Any())
                    .Select(a => a.Id).ToListAsync();

                if (orphanedArtistIds.Any()) {
                    await context.Artists.Where(a => orphanedArtistIds.Contains(a.Id)).ExecuteDeleteAsync();
                    changesMade = true;
                }
            }

            var updatedFolder = await context.Folders.AsTracking().FirstOrDefaultAsync(f => f.Id == folder.Id);
            if (updatedFolder != null) {
                try { updatedFolder.LastModifiedDate = _fileSystem.GetLastWriteTimeUtc(folder.Path); }
                catch (Exception ex) { Debug.WriteLine($"[LibraryService] Could not update LastWriteTimeUtc for folder '{folder.Path}'. {ex.Message}"); }
                await context.SaveChangesAsync();
            }

            await transaction.CommitAsync();
            progress?.Report(new ScanProgress { StatusText = "Rescan complete.", Percentage = 100.0 });
            return changesMade;
        }
        catch (Exception ex) {
            Debug.WriteLine($"[LibraryService] FATAL: Rescan for folder ID '{folderId}' failed and was rolled back. Reason: {ex.Message}");
            await transaction.RollbackAsync();
            progress?.Report(new ScanProgress { StatusText = "Rescan failed. See logs for details.", Percentage = 100 });
            return false;
        }
        finally {
            context.ChangeTracker.AutoDetectChangesEnabled = true;
        }
    }

    public async Task<bool> RefreshAllFoldersAsync(IProgress<ScanProgress>? progress = null) {
        progress?.Report(new ScanProgress { StatusText = "Preparing to refresh all folders...", IsIndeterminate = true });

        await using var context = await _contextFactory.CreateDbContextAsync();
        var folderIds = await context.Folders.AsNoTracking().Select(f => f.Id).ToListAsync();
        var totalFolders = folderIds.Count;

        if (totalFolders == 0) {
            progress?.Report(new ScanProgress { StatusText = "No folders in the library to refresh.", Percentage = 100 });
            return false;
        }

        var foldersProcessed = 0;
        var anyChangesMade = false;

        foreach (var folderId in folderIds) {
            var progressWrapper = new Progress<ScanProgress>(scanProgress => {
                if (progress == null) return;

                var overallPercentage = (foldersProcessed + (scanProgress.Percentage > 0 ? scanProgress.Percentage / 100.0 : 0)) / totalFolders * 100.0;
                var overallProgress = new ScanProgress {
                    StatusText = $"({foldersProcessed + 1}/{totalFolders}) {scanProgress.StatusText}",
                    CurrentFilePath = scanProgress.CurrentFilePath,
                    Percentage = overallPercentage,
                    TotalFiles = scanProgress.TotalFiles,
                    IsIndeterminate = scanProgress.IsIndeterminate,
                    NewSongsFound = scanProgress.NewSongsFound
                };
                progress.Report(overallProgress);
            });

            var result = await RescanFolderForMusicAsync(folderId, progressWrapper);
            if (result) anyChangesMade = true;

            foldersProcessed++;
        }

        var finalMessage = anyChangesMade ? "Refresh complete. Changes were detected and applied." : "Refresh complete. No changes found.";
        progress?.Report(new ScanProgress { StatusText = finalMessage, Percentage = 100 });
        return anyChangesMade;
    }

    #endregion

    #region Private Scanning Helpers

    /// <summary>
    /// A private helper class to encapsulate the state and caches for a single scanning operation.
    /// This improves performance by reducing database queries within a scan.
    /// </summary>
    private class ScanContext {
        public ScanContext(Guid folderId, IProgress<ScanProgress>? progress) {
            FolderId = folderId;
            Progress = progress;
        }

        public Guid FolderId { get; }
        public IProgress<ScanProgress>? Progress { get; }
        public ConcurrentDictionary<string, Artist> ArtistCache { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, Album> AlbumCache { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, Genre> GenreCache { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int NewSongsAdded { get; set; }
        public bool ErrorsOccurred { get; set; }

        public async Task PopulateCachesAsync(MusicDbContext dbContext) {
            var artists = await dbContext.Artists.AsNoTracking().ToListAsync();
            foreach (var artist in artists) ArtistCache.TryAdd(artist.Name, artist);

            var albums = await dbContext.Albums.AsNoTracking().Include(a => a.Artist).ToListAsync();
            foreach (var album in albums) {
                if (album.Artist != null)
                    AlbumCache.TryAdd($"{album.Artist.Name}|{album.Title}", album);
            }

            var genres = await dbContext.Genres.AsNoTracking().ToListAsync();
            foreach (var genre in genres) GenreCache.TryAdd(genre.Name, genre);
        }
    }

    private async Task ProcessNewFilesAsync(MusicDbContext context, IEnumerable<string?> filePaths, ScanContext scanContext) {
        var nonNullFilePaths = filePaths.Where(filePath => filePath != null).Cast<string>();
        const int processingChunkSize = 200;

        await scanContext.PopulateCachesAsync(context);

        foreach (var chunk in nonNullFilePaths.Chunk(processingChunkSize)) {
            var allMetadata = await Task.WhenAll(chunk.Select(f => _metadataExtractor.ExtractMetadataAsync(f)));

            foreach (var metadata in allMetadata) {
                if (metadata.ExtractionFailed) continue;
                if (await AddSongFromMetadataAsync(context, metadata, scanContext)) {
                    scanContext.NewSongsAdded++;
                }
            }

            if (context.ChangeTracker.Entries().Count() >= ScanBatchSize) {
                if (!await SaveChangesAndClearTrackerAsync(context, "[Scan Batch]")) {
                    scanContext.ErrorsOccurred = true;
                }
            }

            scanContext.Progress?.Report(new ScanProgress {
                NewSongsFound = scanContext.NewSongsAdded,
                IsIndeterminate = true,
                StatusText = "Scanning..."
            });
        }

        if (context.ChangeTracker.HasChanges()) {
            if (!await SaveChangesAndClearTrackerAsync(context, "[Scan Final]")) {
                scanContext.ErrorsOccurred = true;
            }
        }
    }

    private async Task<bool> AddSongFromMetadataAsync(MusicDbContext context, SongFileMetadata metadata, ScanContext scanContext) {
        try {
            var trackArtist = GetOrCreateArtistInScan(context, metadata.Artist, scanContext);
            var album = GetOrCreateAlbumInScan(context, metadata.Album, metadata.AlbumArtist, metadata.Year, scanContext, metadata.CoverArtUri);
            var genres = await EnsureGenresExistAsync(context, metadata.Genres, scanContext.GenreCache);

            var song = new Song {
                FilePath = metadata.FilePath,
                Title = metadata.Title,
                Duration = metadata.Duration,
                AlbumArtUriFromTrack = metadata.CoverArtUri,
                LightSwatchId = metadata.LightSwatchId,
                DarkSwatchId = metadata.DarkSwatchId,
                Year = metadata.Year,
                TrackNumber = metadata.TrackNumber,
                DiscNumber = metadata.DiscNumber,
                SampleRate = metadata.SampleRate,
                Bitrate = metadata.Bitrate,
                Channels = metadata.Channels,
                DateAddedToLibrary = DateTime.UtcNow,
                FileCreatedDate = metadata.FileCreatedDate,
                FileModifiedDate = metadata.FileModifiedDate,
                FolderId = scanContext.FolderId,
                ArtistId = trackArtist.Id,
                AlbumId = album?.Id,
                Genres = genres
            };

            context.Songs.Add(song);
            return true;
        }
        catch (Exception ex) {
            Debug.WriteLine($"[LibraryService] ERROR: Failed to prepare song '{metadata.FilePath}' for addition. Reason: {ex.Message}");
            return false;
        }
    }

    private Artist GetOrCreateArtistInScan(MusicDbContext context, string name, ScanContext scanContext) {
        var normalizedName = string.IsNullOrWhiteSpace(name) ? UnknownArtistName : name.Trim();

        if (scanContext.ArtistCache.TryGetValue(normalizedName, out var cachedArtist)) {
            // If the entity was detached by a previous ChangeTracker.Clear(), we must re-attach it.
            if (context.Entry(cachedArtist).State == EntityState.Detached) {
                context.Artists.Attach(cachedArtist);
            }
            return cachedArtist;
        }

        var localArtist = context.Artists.Local
            .FirstOrDefault(a => a.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase));
        if (localArtist != null)
            return localArtist;

        var newArtist = new Artist { Name = normalizedName };
        context.Artists.Add(newArtist);
        scanContext.ArtistCache.TryAdd(normalizedName, newArtist);
        return newArtist;
    }

    private Album? GetOrCreateAlbumInScan(MusicDbContext context, string? title, string? albumArtistName, int? year, ScanContext scanContext, string? coverArtUri) {
        if (string.IsNullOrWhiteSpace(title)) return null;

        var normalizedTitle = title.Trim();
        var artistForAlbum = GetOrCreateArtistInScan(context, string.IsNullOrWhiteSpace(albumArtistName) ? UnknownArtistName : albumArtistName, scanContext);
        var cacheKey = $"{artistForAlbum.Name}|{normalizedTitle}";

        if (scanContext.AlbumCache.TryGetValue(cacheKey, out var album)) {
            // Re-attach if necessary.
            if (context.Entry(album).State == EntityState.Detached) {
                context.Albums.Attach(album);
            }

            bool updated = false;
            if (year.HasValue && album.Year == null) { album.Year = year; updated = true; }
            if (string.IsNullOrEmpty(album.CoverArtUri) && !string.IsNullOrEmpty(coverArtUri)) { album.CoverArtUri = coverArtUri; updated = true; }
            if (updated && context.Entry(album).State == EntityState.Unchanged) { context.Albums.Update(album); }
            return album;
        }

        var localAlbum = context.Albums.Local
            .FirstOrDefault(a => a.Title.Equals(normalizedTitle, StringComparison.OrdinalIgnoreCase) && a.ArtistId == artistForAlbum.Id);
        if (localAlbum != null)
            return localAlbum;

        var newAlbum = new Album { Title = normalizedTitle, Year = year, ArtistId = artistForAlbum.Id, CoverArtUri = coverArtUri };
        context.Albums.Add(newAlbum);
        scanContext.AlbumCache.TryAdd(cacheKey, newAlbum);
        return newAlbum;
    }

    private async Task<bool> SaveChangesAndClearTrackerAsync(MusicDbContext context, string operationTag) {
        if (!context.ChangeTracker.HasChanges()) return true;

        try {
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
            return true;
        }
        catch (DbUpdateException ex) {
            Debug.WriteLine($"[LibraryService] ERROR: Failed to save batch during {operationTag}. Reason: {ex.InnerException?.Message ?? ex.Message}");
            return false;
        }
        catch (Exception ex) {
            Debug.WriteLine($"[LibraryService] ERROR: An unexpected error occurred during {operationTag}. Reason: {ex.Message}");
            return false;
        }
    }

    private async Task<Folder?> GetOrCreateFolderForScanAsync(MusicDbContext context, string folderPath) {
        var folder = await context.Folders.AsTracking().FirstOrDefaultAsync(f => f.Path == folderPath);
        DateTime? fileSystemLastModified = null;
        try { fileSystemLastModified = _fileSystem.GetLastWriteTimeUtc(folderPath); }
        catch (Exception ex) { Debug.WriteLine($"[LibraryService] Could not get LastWriteTimeUtc for folder '{folderPath}'. {ex.Message}"); }

        if (folder == null) {
            folder = new Folder { Path = folderPath, Name = _fileSystem.GetDirectoryNameFromPath(folderPath), LastModifiedDate = fileSystemLastModified };
            context.Folders.Add(folder);
        }
        else if (folder.LastModifiedDate != fileSystemLastModified) {
            folder.LastModifiedDate = fileSystemLastModified;
        }

        try {
            await context.SaveChangesAsync();
        }
        catch (DbUpdateException ex) {
            Debug.WriteLine($"[LibraryService] ERROR: Failed to save folder '{folderPath}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            context.Entry(folder).State = EntityState.Detached;
            return null;
        }

        return folder;
    }

    #endregion

    #region Song Management

    public async Task<Song?> AddSongAsync(Song songData) {
        if (songData == null) throw new ArgumentNullException(nameof(songData));
        if (string.IsNullOrWhiteSpace(songData.FilePath))
            throw new ArgumentException("Song FilePath cannot be empty.", nameof(songData.FilePath));

        await using var context = await _contextFactory.CreateDbContextAsync();
        var existingSong = await context.Songs.FirstOrDefaultAsync(s => s.FilePath == songData.FilePath);
        if (existingSong != null) return existingSong;

        context.Songs.Add(songData);
        try {
            await context.SaveChangesAsync();
            return songData;
        }
        catch (DbUpdateException ex) {
            Debug.WriteLine($"[LibraryService] ERROR: Database update failed for path '{songData.FilePath}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            context.Entry(songData).State = EntityState.Detached;
            return null;
        }
    }

    public async Task<Song?> AddSongWithDetailsAsync(Guid folderId, SongFileMetadata metadata) {
        if (metadata == null) throw new ArgumentNullException(nameof(metadata));
        if (string.IsNullOrWhiteSpace(metadata.FilePath))
            throw new ArgumentException("Metadata FilePath cannot be empty.", nameof(metadata));

        // Use a retry loop to handle potential concurrency issues when multiple operations
        // try to create the same artist or album simultaneously.
        for (var i = 0; i < 3; i++) {
            await using var context = await _contextFactory.CreateDbContextAsync();
            try {
                if (await context.Songs.AsNoTracking().AnyAsync(s => s.FilePath == metadata.FilePath))
                    return await context.Songs.AsNoTracking().FirstAsync(s => s.FilePath == metadata.FilePath);

                var song = new Song {
                    FilePath = metadata.FilePath,
                    Title = metadata.Title.Trim(),
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
                    Lyrics = metadata.Lyrics
                };

                song.Genres = await EnsureGenresExistAsync(context, metadata.Genres);

                var trackArtist = await GetOrCreateArtistAsync(context, metadata.Artist);
                song.Artist = trackArtist;

                if (!string.IsNullOrWhiteSpace(metadata.Album)) {
                    var artistNameToUseForAlbum = !string.IsNullOrWhiteSpace(metadata.AlbumArtist) ? metadata.AlbumArtist : metadata.Artist;
                    var album = await GetOrCreateAlbumAsync(context, metadata.Album, artistNameToUseForAlbum, metadata.Year);

                    if (string.IsNullOrEmpty(album.CoverArtUri) && !string.IsNullOrEmpty(metadata.CoverArtUri)) {
                        album.CoverArtUri = metadata.CoverArtUri;
                    }

                    song.Album = album;
                }

                context.Songs.Add(song);
                await context.SaveChangesAsync();
                return song;
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex)) {
                // This is a concurrency conflict. Wait a moment and retry.
                await Task.Delay(50 + Random.Shared.Next(50));
            }
            catch (DbUpdateException ex) {
                Debug.WriteLine($"[LibraryService] ERROR: Unrecoverable database update failed for path '{metadata.FilePath}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
                return null;
            }
        }
        return null;
    }

    public async Task<bool> RemoveSongAsync(Guid songId) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var song = await context.Songs.FindAsync(songId);
        if (song == null) return false;

        var albumArtPathToDelete = song.AlbumArtUriFromTrack;
        context.Songs.Remove(song);
        try {
            await context.SaveChangesAsync();
            if (!string.IsNullOrWhiteSpace(albumArtPathToDelete) && _fileSystem.FileExists(albumArtPathToDelete)) {
                try {
                    _fileSystem.DeleteFile(albumArtPathToDelete);
                }
                catch (Exception fileEx) {
                    Debug.WriteLine($"[LibraryService] Failed to delete art file '{albumArtPathToDelete}'. {fileEx.Message}");
                }
            }
            return true;
        }
        catch (DbUpdateException ex) {
            Debug.WriteLine($"[LibraryService] ERROR: Database update failed for ID '{songId}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            context.Entry(song).State = EntityState.Unchanged;
            return false;
        }
    }

    public async Task<Song?> GetSongByIdAsync(Guid songId) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Songs.AsNoTracking()
            .Include(s => s.Artist)
            .Include(s => s.Album).ThenInclude(a => a!.Artist)
            .Include(s => s.Folder)
            .AsSplitQuery()
            .FirstOrDefaultAsync(s => s.Id == songId);
    }

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

    public async Task<IReadOnlyDictionary<Guid, Song>> GetSongsByIdsAsync(IEnumerable<Guid> songIds) {
        if (songIds == null || !songIds.Any()) return new Dictionary<Guid, Song>();
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

    public async Task<bool> UpdateSongAsync(Song songToUpdate) {
        if (songToUpdate == null) throw new ArgumentNullException(nameof(songToUpdate));
        await using var context = await _contextFactory.CreateDbContextAsync();
        context.Songs.Update(songToUpdate);
        try {
            await context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex) {
            Debug.WriteLine($"[LibraryService] ERROR: Database update failed for ID '{songToUpdate.Id}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            await context.Entry(songToUpdate).ReloadAsync();
            return false;
        }
    }

    public async Task<IEnumerable<Song>> GetAllSongsAsync(SongSortOrder sortOrder = SongSortOrder.TitleAsc) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        IQueryable<Song> query = context.Songs.AsNoTracking()
            .Include(s => s.Artist)
            .Include(s => s.Album).ThenInclude(a => a!.Artist);

        var sortedQuery = ApplySongSortOrder(query, sortOrder);
        return await sortedQuery.AsSplitQuery().ToListAsync();
    }

    public async Task<IEnumerable<Song>> GetSongsByAlbumIdAsync(Guid albumId) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Songs.AsNoTracking()
            .Where(s => s.AlbumId == albumId)
            .Include(s => s.Artist)
            .OrderBy(s => s.TrackNumber).ThenBy(s => s.Title)
            .AsSplitQuery()
            .ToListAsync();
    }

    public async Task<IEnumerable<Song>> GetSongsByArtistIdAsync(Guid artistId) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Songs.AsNoTracking()
            .Where(s => s.ArtistId == artistId)
            .Include(s => s.Album).ThenInclude(alb => alb!.Artist)
            .Include(s => s.Artist)
            .OrderBy(s => s.Album != null ? s.Album.Title : string.Empty).ThenBy(s => s.TrackNumber)
            .AsSplitQuery()
            .ToListAsync();
    }

    public async Task<IEnumerable<Song>> GetSongsByFolderIdAsync(Guid folderId) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Songs.AsNoTracking()
            .Where(s => s.FolderId == folderId)
            .Include(s => s.Artist)
            .Include(s => s.Album).ThenInclude(alb => alb!.Artist)
            .OrderBy(s => s.Album != null ? s.Album.Title : string.Empty).ThenBy(s => s.TrackNumber).ThenBy(s => s.Title)
            .AsSplitQuery()
            .ToListAsync();
    }

    public async Task<IEnumerable<Song>> SearchSongsAsync(string searchTerm) {
        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetAllSongsAsync();
        var trimmedSearchTerm = searchTerm.Trim();
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await BuildSongSearchQuery(context, trimmedSearchTerm)
            .AsNoTracking()
            .OrderBy(s => s.Title).ThenBy(s => s.Id)
            .AsSplitQuery()
            .ToListAsync();
    }

    #endregion

    #region Song Metadata Updates

    public Task<bool> SetSongRatingAsync(Guid songId, int? rating) {
        if (rating.HasValue && (rating < 1 || rating > 5)) {
            throw new ArgumentOutOfRangeException(nameof(rating), "Rating must be between 1 and 5.");
        }
        return UpdateSongPropertyAsync(songId, s => s.Rating = rating);
    }

    public Task<bool> SetSongLovedStatusAsync(Guid songId, bool isLoved) {
        return UpdateSongPropertyAsync(songId, s => s.IsLoved = isLoved);
    }

    public Task<bool> UpdateSongLyricsAsync(Guid songId, string? lyrics) {
        return UpdateSongPropertyAsync(songId, s => s.Lyrics = lyrics);
    }

    private async Task<bool> UpdateSongPropertyAsync(Guid songId, Action<Song> updateAction) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var song = await context.Songs.FirstOrDefaultAsync(s => s.Id == songId);
        if (song == null) return false;

        context.Songs.Attach(song);
        updateAction(song);

        try {
            await context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex) {
            Debug.WriteLine($"[LibraryService] ERROR: Database update failed for song ID '{songId}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            return false;
        }
    }

    #endregion

    #region Artist Management

    public async Task<Artist?> GetArtistDetailsAsync(Guid artistId, bool allowOnlineFetch) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var artist = await context.Artists.AsTracking()
            .Include(a => a.Albums)
            .FirstOrDefaultAsync(a => a.Id == artistId);
        if (artist == null) return null;

        if (!allowOnlineFetch || !string.IsNullOrWhiteSpace(artist.Biography)) {
            context.Entry(artist).State = EntityState.Detached;
            return artist;
        }

        await FetchAndUpdateArtistFromRemoteAsync(context, artist);
        context.Entry(artist).State = EntityState.Detached;
        return artist;
    }

    public Task StartArtistMetadataBackgroundFetchAsync() {
        lock (_metadataFetchLock) {
            if (_isMetadataFetchRunning) return Task.CompletedTask;
            _isMetadataFetchRunning = true;
        }

        _ = Task.Run(async () => {
            try {
                const int batchSize = 50;
                while (true) {
                    List<Guid> artistIdsToUpdate;
                    await using (var idContext = await _contextFactory.CreateDbContextAsync()) {
                        artistIdsToUpdate = await idContext.Artists
                            .Where(a => a.Biography == null || a.RemoteImageUrl == null)
                            .OrderBy(a => a.Name)
                            .Select(a => a.Id)
                            .Take(batchSize)
                            .ToListAsync();
                    }

                    if (!artistIdsToUpdate.Any()) {
                        break;
                    }

                    using var scope = _serviceScopeFactory.CreateScope();
                    var scopedContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MusicDbContext>>();
                    await using var batchContext = await scopedContextFactory.CreateDbContextAsync();

                    foreach (var artistId in artistIdsToUpdate) {
                        try {
                            var artist = await batchContext.Artists.AsTracking().FirstOrDefaultAsync(a => a.Id == artistId);
                            if (artist == null || (!string.IsNullOrWhiteSpace(artist.Biography) && !string.IsNullOrWhiteSpace(artist.RemoteImageUrl))) {
                                continue;
                            }
                            await FetchAndUpdateArtistFromRemoteAsync(batchContext, artist);
                        }
                        catch (Exception ex) {
                            Debug.WriteLine($"[LibraryService] ERROR: Failed to update artist {artistId} in background. {ex}");
                        }
                    }
                }
            }
            finally {
                lock (_metadataFetchLock) {
                    _isMetadataFetchRunning = false;
                }
            }
        });
        return Task.CompletedTask;
    }

    public async Task<Artist?> GetArtistByIdAsync(Guid artistId) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Artists.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == artistId);
    }

    public async Task<Artist?> GetArtistByNameAsync(string name) {
        if (string.IsNullOrWhiteSpace(name)) return null;
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Artists.AsNoTracking().FirstOrDefaultAsync(a => a.Name == name.Trim());
    }

    public async Task<IEnumerable<Artist>> GetAllArtistsAsync() {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Artists.AsNoTracking().OrderBy(a => a.Name).ToListAsync();
    }

    public async Task<IEnumerable<Artist>> SearchArtistsAsync(string searchTerm) {
        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetAllArtistsAsync();
        var trimmedSearchTerm = searchTerm.Trim();
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await BuildArtistSearchQuery(context, trimmedSearchTerm).AsNoTracking().OrderBy(a => a.Name).ToListAsync();
    }

    #endregion

    #region Album Management

    public async Task<Album?> GetAlbumByIdAsync(Guid albumId) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Albums.AsNoTracking()
            .Include(al => al.Artist)
            .AsSplitQuery()
            .FirstOrDefaultAsync(al => al.Id == albumId);
    }

    public async Task<IEnumerable<Album>> GetAllAlbumsAsync() {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Albums.AsNoTracking()
            .Include(al => al.Artist)
            .OrderBy(al => al.Artist != null ? al.Artist.Name : string.Empty)
            .ThenBy(al => al.Title)
            .AsSplitQuery()
            .ToListAsync();
    }

    public async Task<IEnumerable<Album>> SearchAlbumsAsync(string searchTerm) {
        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetAllAlbumsAsync();
        var trimmedSearchTerm = searchTerm.Trim();
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await BuildAlbumSearchQuery(context, trimmedSearchTerm)
            .AsNoTracking()
            .OrderBy(al => al.Artist != null ? al.Artist.Name : string.Empty)
            .ThenBy(al => al.Title)
            .AsSplitQuery()
            .ToListAsync();
    }

    #endregion

    #region Playlist Management

    public async Task<Playlist?> CreatePlaylistAsync(string name, string? description = null, string? coverImageUri = null) {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Playlist name cannot be empty.", nameof(name));

        await using var context = await _contextFactory.CreateDbContextAsync();
        var playlist = new Playlist {
            Name = name.Trim(),
            Description = description,
            CoverImageUri = coverImageUri,
            DateCreated = DateTime.UtcNow,
            DateModified = DateTime.UtcNow
        };
        context.Playlists.Add(playlist);
        try {
            await context.SaveChangesAsync();
            return playlist;
        }
        catch (DbUpdateException ex) {
            Debug.WriteLine($"[LibraryService] ERROR: Database update failed for name '{name}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            context.Entry(playlist).State = EntityState.Detached;
            return null;
        }
    }

    public async Task<bool> DeletePlaylistAsync(Guid playlistId) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        await context.PlaylistSongs.Where(ps => ps.PlaylistId == playlistId).ExecuteDeleteAsync();
        var rowsAffected = await context.Playlists.Where(p => p.Id == playlistId).ExecuteDeleteAsync();
        return rowsAffected > 0;
    }

    public async Task<bool> RenamePlaylistAsync(Guid playlistId, string newName) {
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("New playlist name cannot be empty.", nameof(newName));

        await using var context = await _contextFactory.CreateDbContextAsync();
        var playlist = await context.Playlists.FindAsync(playlistId);
        if (playlist == null) return false;

        playlist.Name = newName.Trim();
        playlist.DateModified = DateTime.UtcNow;
        try {
            await context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex) {
            Debug.WriteLine($"[LibraryService] ERROR: Database update failed for ID '{playlist.Id}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            await context.Entry(playlist).ReloadAsync();
            return false;
        }
    }

    public async Task<bool> UpdatePlaylistCoverAsync(Guid playlistId, string? newCoverImageUri) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var playlist = await context.Playlists.FindAsync(playlistId);
        if (playlist == null) return false;

        playlist.CoverImageUri = newCoverImageUri;
        playlist.DateModified = DateTime.UtcNow;
        try {
            await context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex) {
            Debug.WriteLine($"[LibraryService] ERROR: Database update failed for ID '{playlist.Id}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            await context.Entry(playlist).ReloadAsync();
            return false;
        }
    }

    public async Task<bool> AddSongsToPlaylistAsync(Guid playlistId, IEnumerable<Guid> songIds) {
        if (songIds == null || !songIds.Any()) return false;
        await using var context = await _contextFactory.CreateDbContextAsync();
        var playlist = await context.Playlists.FindAsync(playlistId);
        if (playlist == null) return false;

        var existingPlaylistSongIds = await context.PlaylistSongs
            .Where(ps => ps.PlaylistId == playlistId)
            .Select(ps => ps.SongId)
            .ToHashSetAsync();

        var songIdsToAdd = songIds.Distinct().Except(existingPlaylistSongIds).ToList();
        if (!songIdsToAdd.Any()) return true;

        var songsToActuallyAdd = await context.Songs
            .Where(s => songIdsToAdd.Contains(s.Id))
            .Select(s => s.Id)
            .ToListAsync();
        if (!songsToActuallyAdd.Any()) return false;

        var nextOrder = (await context.PlaylistSongs.Where(ps => ps.PlaylistId == playlistId).MaxAsync(ps => (int?)ps.Order) ?? -1) + 1;
        var playlistSongsToAdd = songsToActuallyAdd.Select(songId => new PlaylistSong {
            PlaylistId = playlistId,
            SongId = songId,
            Order = nextOrder++
        });

        context.PlaylistSongs.AddRange(playlistSongsToAdd);
        playlist.DateModified = DateTime.UtcNow;
        try {
            await context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex) {
            Debug.WriteLine($"[LibraryService] ERROR: Database update failed for playlist '{playlistId}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            foreach (var entry in context.ChangeTracker.Entries().Where(e => e.State == EntityState.Added))
                entry.State = EntityState.Detached;
            return false;
        }
    }

    public async Task<bool> RemoveSongsFromPlaylistAsync(Guid playlistId, IEnumerable<Guid> songIds) {
        if (songIds == null || !songIds.Any()) return false;
        await using var context = await _contextFactory.CreateDbContextAsync();
        var playlist = await context.Playlists.FindAsync(playlistId);
        if (playlist == null) return false;

        var uniqueSongIds = songIds.Distinct();
        await context.PlaylistSongs
            .Where(ps => ps.PlaylistId == playlistId && uniqueSongIds.Contains(ps.SongId))
            .ExecuteDeleteAsync();

        await ReindexPlaylistAsync(context, playlistId);

        playlist.DateModified = DateTime.UtcNow;
        try {
            await context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex) {
            Debug.WriteLine($"[LibraryService] ERROR: Database update failed for playlist '{playlistId}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            return false;
        }
    }

    public async Task<bool> UpdatePlaylistSongOrderAsync(Guid playlistId, IEnumerable<Guid> orderedSongIds) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var playlist = await context.Playlists.FindAsync(playlistId);
        if (playlist == null) return false;

        var playlistSongs = await context.PlaylistSongs.Where(ps => ps.PlaylistId == playlistId).AsTracking().ToListAsync();
        var playlistSongMap = playlistSongs.ToDictionary(ps => ps.SongId);
        var newOrderList = orderedSongIds.ToList();
        var changesMade = false;

        for (var i = 0; i < newOrderList.Count; i++) {
            var songId = newOrderList[i];
            if (playlistSongMap.TryGetValue(songId, out var playlistSongToUpdate) && playlistSongToUpdate.Order != i) {
                playlistSongToUpdate.Order = i;
                changesMade = true;
            }
        }

        if (!changesMade) return true;
        playlist.DateModified = DateTime.UtcNow;
        try {
            await context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex) {
            Debug.WriteLine($"[LibraryService] ERROR: Database update failed for playlist reorder '{playlistId}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            return false;
        }
    }

    public async Task<Playlist?> GetPlaylistByIdAsync(Guid playlistId) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Playlists.AsNoTracking()
            .Include(p => p.PlaylistSongs.OrderBy(ps => ps.Order)).ThenInclude(ps => ps.Song).ThenInclude(s => s!.Artist)
            .Include(p => p.PlaylistSongs.OrderBy(ps => ps.Order)).ThenInclude(ps => ps.Song).ThenInclude(s => s!.Album).ThenInclude(a => a!.Artist)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == playlistId);
    }

    public async Task<IEnumerable<Playlist>> GetAllPlaylistsAsync() {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Playlists.AsNoTracking()
            .Include(p => p.PlaylistSongs)
            .OrderBy(p => p.Name).ThenBy(p => p.Id)
            .ToListAsync();
    }

    public async Task<IEnumerable<Song>> GetSongsInPlaylistOrderedAsync(Guid playlistId) {
        var playlist = await GetPlaylistByIdAsync(playlistId);
        if (playlist == null) return Enumerable.Empty<Song>();
        return playlist.PlaylistSongs.Select(ps => ps.Song).Where(s => s != null).ToList()!;
    }

    #endregion

    #region Genre Management

    public async Task<IEnumerable<Genre>> GetAllGenresAsync() {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Genres
            .AsNoTracking()
            .OrderBy(g => g.Name)
            .ToListAsync();
    }

    public async Task<IEnumerable<Song>> GetSongsByGenreIdAsync(Guid genreId) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Songs
            .AsNoTracking()
            .Include(s => s.Artist)
            .Include(s => s.Album)
            .Where(s => s.Genres.Any(g => g.Id == genreId))
            .OrderBy(s => s.Title)
            .ToListAsync();
    }

    #endregion

    #region Listen History

    public async Task LogListenAsync(Guid songId) {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var song = await context.Songs.FirstOrDefaultAsync(s => s.Id == songId);
        if (song == null) {
            Debug.WriteLine($"[LibraryService] WARNING: Attempted to log listen for non-existent song ID '{songId}'.");
            return;
        }

        context.Songs.Attach(song);
        song.PlayCount++;
        song.LastPlayedDate = DateTime.UtcNow;

        var listenEvent = new ListenHistory {
            SongId = songId,
            ListenTimestampUtc = DateTime.UtcNow
        };

        context.ListenHistory.Add(listenEvent);
        await context.SaveChangesAsync();
    }

    public async Task LogSkipAsync(Guid songId) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var song = await context.Songs.FirstOrDefaultAsync(s => s.Id == songId);
        if (song == null) return;

        context.Songs.Attach(song);
        song.SkipCount++;
        await context.SaveChangesAsync();
    }

    public async Task<int> GetListenCountForSongAsync(Guid songId) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.ListenHistory.CountAsync(lh => lh.SongId == songId);
    }

    #endregion

    #region Paged Loading

    public async Task<PagedResult<Song>> GetAllSongsPagedAsync(int pageNumber, int pageSize, SongSortOrder sortOrder = SongSortOrder.TitleAsc) {
        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1) pageSize = 1;

        await using var context = await _contextFactory.CreateDbContextAsync();
        IQueryable<Song> query = context.Songs.AsNoTracking()
            .Include(s => s.Artist)
            .Include(s => s.Album).ThenInclude(a => a!.Artist);

        var totalCount = await query.CountAsync();
        var sortedQuery = ApplySongSortOrder(query, sortOrder);

        var pagedData = await sortedQuery
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .AsSplitQuery()
            .ToListAsync();

        return new PagedResult<Song> {
            Items = pagedData,
            TotalCount = totalCount,
            PageNumber = pageNumber,
            PageSize = pageSize
        };
    }

    public async Task<PagedResult<Song>> SearchSongsPagedAsync(string searchTerm, int pageNumber, int pageSize) {
        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1) pageSize = 1;

        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = string.IsNullOrWhiteSpace(searchTerm)
            ? context.Songs.Include(s => s.Artist).Include(s => s.Album).ThenInclude(a => a!.Artist)
            : BuildSongSearchQuery(context, searchTerm.Trim());

        var totalCount = await query.CountAsync();
        var sortedQuery = ApplySongSortOrder(query, SongSortOrder.TitleAsc);

        var pagedData = await sortedQuery
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .AsSplitQuery()
            .ToListAsync();

        return new PagedResult<Song> {
            Items = pagedData,
            TotalCount = totalCount,
            PageNumber = pageNumber,
            PageSize = pageSize
        };
    }

    public async Task<PagedResult<Song>> GetSongsByAlbumIdPagedAsync(Guid albumId, int pageNumber, int pageSize, SongSortOrder sortOrder) {
        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1) pageSize = 1;

        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Songs.AsNoTracking()
            .Where(s => s.AlbumId == albumId)
            .Include(s => s.Artist)
            .Include(s => s.Album);

        var totalCount = await query.CountAsync();
        var sortedQuery = ApplySongSortOrder(query, sortOrder);

        var pagedData = await sortedQuery
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .AsSplitQuery()
            .ToListAsync();

        return new PagedResult<Song> {
            Items = pagedData,
            TotalCount = totalCount,
            PageNumber = pageNumber,
            PageSize = pageSize
        };
    }

    public async Task<PagedResult<Song>> GetSongsByArtistIdPagedAsync(Guid artistId, int pageNumber, int pageSize, SongSortOrder sortOrder) {
        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1) pageSize = 1;

        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Songs.AsNoTracking()
            .Where(s => s.ArtistId == artistId)
            .Include(s => s.Album).ThenInclude(alb => alb!.Artist)
            .Include(s => s.Artist);

        var totalCount = await query.CountAsync();
        var sortedQuery = ApplySongSortOrder(query, sortOrder);

        var pagedData = await sortedQuery
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .AsSplitQuery()
            .ToListAsync();

        return new PagedResult<Song> {
            Items = pagedData,
            TotalCount = totalCount,
            PageNumber = pageNumber,
            PageSize = pageSize
        };
    }

    public async Task<PagedResult<Song>> GetSongsByPlaylistPagedAsync(Guid playlistId, int pageNumber, int pageSize) {
        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1) pageSize = 1;

        await using var context = await _contextFactory.CreateDbContextAsync();

        var query = context.PlaylistSongs
            .AsNoTracking()
            .Where(ps => ps.PlaylistId == playlistId)
            .Include(ps => ps.Song).ThenInclude(s => s!.Artist)
            .Include(ps => ps.Song).ThenInclude(s => s!.Album).ThenInclude(a => a!.Artist)
            .OrderBy(ps => ps.Order)
            .Select(ps => ps.Song)
            .Where(s => s != null)
            .Cast<Song>();

        var totalCount = await query.CountAsync();

        var pagedData = await query
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .AsSplitQuery()
            .ToListAsync();

        return new PagedResult<Song> {
            Items = pagedData,
            TotalCount = totalCount,
            PageNumber = pageNumber,
            PageSize = pageSize
        };
    }

    public async Task<PagedResult<Artist>> GetAllArtistsPagedAsync(int pageNumber, int pageSize) {
        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1) pageSize = 1;

        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Artists.AsNoTracking();
        var totalCount = await query.CountAsync();
        var pagedData = await query
            .OrderBy(a => a.Name)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return new PagedResult<Artist> {
            Items = pagedData,
            TotalCount = totalCount,
            PageNumber = pageNumber,
            PageSize = pageSize
        };
    }

    public async Task<PagedResult<Artist>> SearchArtistsPagedAsync(string searchTerm, int pageNumber, int pageSize) {
        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1) pageSize = 1;

        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = string.IsNullOrWhiteSpace(searchTerm)
            ? context.Artists
            : BuildArtistSearchQuery(context, searchTerm.Trim());

        var totalCount = await query.CountAsync();
        var pagedData = await query.AsNoTracking()
            .OrderBy(a => a.Name)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return new PagedResult<Artist> {
            Items = pagedData,
            TotalCount = totalCount,
            PageNumber = pageNumber,
            PageSize = pageSize
        };
    }

    public async Task<PagedResult<Album>> GetAllAlbumsPagedAsync(int pageNumber, int pageSize) {
        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1) pageSize = 1;

        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Albums.AsNoTracking().Include(al => al.Artist);
        var totalCount = await query.CountAsync();
        var pagedData = await query
            .OrderBy(al => al.Artist != null ? al.Artist.Name : string.Empty)
            .ThenBy(al => al.Title)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .AsSplitQuery()
            .ToListAsync();

        return new PagedResult<Album> {
            Items = pagedData,
            TotalCount = totalCount,
            PageNumber = pageNumber,
            PageSize = pageSize
        };
    }

    public async Task<PagedResult<Album>> SearchAlbumsPagedAsync(string searchTerm, int pageNumber, int pageSize) {
        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1) pageSize = 1;

        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = string.IsNullOrWhiteSpace(searchTerm)
            ? context.Albums.Include(al => al.Artist)
            : BuildAlbumSearchQuery(context, searchTerm.Trim());

        var totalCount = await query.CountAsync();
        var pagedData = await query.AsNoTracking()
            .OrderBy(al => al.Artist != null ? al.Artist.Name : string.Empty)
            .ThenBy(al => al.Title)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .AsSplitQuery()
            .ToListAsync();

        return new PagedResult<Album> {
            Items = pagedData,
            TotalCount = totalCount,
            PageNumber = pageNumber,
            PageSize = pageSize
        };
    }

    public async Task<PagedResult<Playlist>> GetAllPlaylistsPagedAsync(int pageNumber, int pageSize) {
        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1) pageSize = 1;

        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Playlists.AsNoTracking().Include(p => p.PlaylistSongs);
        var totalCount = await query.CountAsync();
        var pagedData = await query
            .OrderBy(p => p.Name).ThenBy(p => p.Id)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return new PagedResult<Playlist> {
            Items = pagedData,
            TotalCount = totalCount,
            PageNumber = pageNumber,
            PageSize = pageSize
        };
    }

    public async Task<PagedResult<Song>> GetSongsByFolderIdPagedAsync(Guid folderId, int pageNumber, int pageSize, SongSortOrder sortOrder = SongSortOrder.TitleAsc) {
        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1) pageSize = 1;

        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Songs.AsNoTracking()
            .Where(s => s.FolderId == folderId)
            .Include(s => s.Artist)
            .Include(s => s.Album).ThenInclude(alb => alb!.Artist);

        var totalCount = await query.CountAsync();
        var sortedQuery = ApplySongSortOrder(query, sortOrder);

        var pagedData = await sortedQuery
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .AsSplitQuery()
            .ToListAsync();

        return new PagedResult<Song> {
            Items = pagedData,
            TotalCount = totalCount,
            PageNumber = pageNumber,
            PageSize = pageSize
        };
    }

    public async Task<List<Guid>> GetAllSongIdsAsync(SongSortOrder sortOrder) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Songs.AsNoTracking();

        var sortedQuery = ApplySongSortOrder(query, sortOrder);

        return await sortedQuery.Select(s => s.Id).ToListAsync();
    }

    public async Task<List<Guid>> GetAllSongIdsByFolderIdAsync(Guid folderId, SongSortOrder sortOrder) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Songs.AsNoTracking()
            .Where(s => s.FolderId == folderId);

        var sortedQuery = ApplySongSortOrder(query, sortOrder);

        return await sortedQuery.Select(s => s.Id).ToListAsync();
    }

    public async Task<List<Guid>> GetAllSongIdsByArtistIdAsync(Guid artistId, SongSortOrder sortOrder) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Songs.AsNoTracking()
            .Where(s => s.ArtistId == artistId);

        var sortedQuery = ApplySongSortOrder(query, sortOrder);

        return await sortedQuery.Select(s => s.Id).ToListAsync();
    }

    public async Task<List<Guid>> GetAllSongIdsByAlbumIdAsync(Guid albumId, SongSortOrder sortOrder) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Songs.AsNoTracking()
            .Where(s => s.AlbumId == albumId);

        var sortedQuery = ApplySongSortOrder(query, sortOrder);

        return await sortedQuery.Select(s => s.Id).ToListAsync();
    }

    public async Task<List<Guid>> GetAllSongIdsByPlaylistIdAsync(Guid playlistId) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.PlaylistSongs
            .AsNoTracking()
            .Where(ps => ps.PlaylistId == playlistId)
            .OrderBy(ps => ps.Order)
            .Select(ps => ps.SongId)
            .ToListAsync();
    }

    #endregion

    #region Data Reset

    public async Task ClearAllLibraryDataAsync() {
        try {
            await using var context = await _contextFactory.CreateDbContextAsync();
            await ClearAllTablesAsync(context);

            var albumArtStoragePath = _pathConfig.AlbumArtCachePath;
            var artistImageStoragePath = _pathConfig.ArtistImageCachePath;

            if (_fileSystem.DirectoryExists(albumArtStoragePath)) _fileSystem.DeleteDirectory(albumArtStoragePath, true);
            _fileSystem.CreateDirectory(albumArtStoragePath);

            if (_fileSystem.DirectoryExists(artistImageStoragePath)) _fileSystem.DeleteDirectory(artistImageStoragePath, true);
            _fileSystem.CreateDirectory(artistImageStoragePath);
        }
        catch (Exception ex) {
            Debug.WriteLine($"[LibraryService] FATAL: Failed to clear all library data. Reason: {ex.Message}");
            throw;
        }
    }

    private async Task ClearAllTablesAsync(MusicDbContext context) {
        try {
            context.ChangeTracker.AutoDetectChangesEnabled = false;
            await context.PlaylistSongs.ExecuteDeleteAsync();
            await context.ListenHistory.ExecuteDeleteAsync();
            await context.Songs.ExecuteDeleteAsync();
            await context.Playlists.ExecuteDeleteAsync();
            await context.Albums.ExecuteDeleteAsync();
            await context.Artists.ExecuteDeleteAsync();
            await context.Genres.ExecuteDeleteAsync();
            await context.Folders.ExecuteDeleteAsync();
        }
        finally {
            context.ChangeTracker.AutoDetectChangesEnabled = true;
            context.ChangeTracker.Clear();
        }
    }

    #endregion

    #region Scoped Search

    public async Task<IEnumerable<Song>> SearchSongsInFolderAsync(Guid folderId, string searchTerm) {
        if (string.IsNullOrWhiteSpace(searchTerm)) {
            return await GetSongsByFolderIdAsync(folderId);
        }

        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Songs.Where(s => s.FolderId == folderId);

        var trimmedTerm = searchTerm.Trim();
        var term = $"%{trimmedTerm}%";
        query = query.Where(s => EF.Functions.Like(s.Title, term)
                              || (s.Album != null && EF.Functions.Like(s.Album.Title, term))
                              || (s.Artist != null && EF.Functions.Like(s.Artist.Name, term)));

        return await query.Include(s => s.Artist)
                          .Include(s => s.Album).ThenInclude(a => a!.Artist)
                          .OrderBy(s => s.Title)
                          .AsSplitQuery()
                          .ToListAsync();
    }

    public async Task<IEnumerable<Song>> SearchSongsInAlbumAsync(Guid albumId, string searchTerm) {
        if (string.IsNullOrWhiteSpace(searchTerm)) {
            return await GetSongsByAlbumIdAsync(albumId);
        }

        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Songs.Where(s => s.AlbumId == albumId);

        var trimmedTerm = searchTerm.Trim();
        var term = $"%{trimmedTerm}%";
        query = query.Where(s => EF.Functions.Like(s.Title, term)
                              || (s.Artist != null && EF.Functions.Like(s.Artist.Name, term)));

        return await query.Include(s => s.Artist)
                          .Include(s => s.Album)
                          .OrderBy(s => s.TrackNumber)
                          .AsSplitQuery()
                          .ToListAsync();
    }

    public async Task<IEnumerable<Song>> SearchSongsInArtistAsync(Guid artistId, string searchTerm) {
        if (string.IsNullOrWhiteSpace(searchTerm)) {
            return await GetSongsByArtistIdAsync(artistId);
        }

        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Songs.Where(s => s.ArtistId == artistId);

        var trimmedTerm = searchTerm.Trim();
        var term = $"%{trimmedTerm}%";
        query = query.Where(s => EF.Functions.Like(s.Title, term)
                              || (s.Album != null && EF.Functions.Like(s.Album.Title, term)));

        return await query.Include(s => s.Artist)
                          .Include(s => s.Album)
                          .OrderBy(s => s.Album != null ? s.Album.Title : string.Empty)
                          .ThenBy(s => s.TrackNumber)
                          .AsSplitQuery()
                          .ToListAsync();
    }

    public async Task<PagedResult<Song>> SearchSongsInFolderPagedAsync(Guid folderId, string searchTerm, int pageNumber, int pageSize) {
        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1) pageSize = 1;

        await using var context = await _contextFactory.CreateDbContextAsync();

        var query = context.Songs.Where(s => s.FolderId == folderId);

        if (!string.IsNullOrWhiteSpace(searchTerm)) {
            var trimmedTerm = searchTerm.Trim();
            var term = $"%{trimmedTerm}%";
            query = query.Where(s => EF.Functions.Like(s.Title, term)
                                  || (s.Album != null && EF.Functions.Like(s.Album.Title, term))
                                  || (s.Artist != null && EF.Functions.Like(s.Artist.Name, term)));
        }

        query = query.Include(s => s.Artist).Include(s => s.Album).ThenInclude(a => a!.Artist);

        var totalCount = await query.CountAsync();
        var sortedQuery = ApplySongSortOrder(query, SongSortOrder.TitleAsc);

        var pagedData = await sortedQuery
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .AsSplitQuery()
            .ToListAsync();

        return new PagedResult<Song> {
            Items = pagedData,
            TotalCount = totalCount,
            PageNumber = pageNumber,
            PageSize = pageSize
        };
    }

    public async Task<PagedResult<Song>> SearchSongsInAlbumPagedAsync(Guid albumId, string searchTerm, int pageNumber, int pageSize) {
        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1) pageSize = 1;

        await using var context = await _contextFactory.CreateDbContextAsync();

        var query = context.Songs.Where(s => s.AlbumId == albumId);

        if (!string.IsNullOrWhiteSpace(searchTerm)) {
            var trimmedTerm = searchTerm.Trim();
            var term = $"%{trimmedTerm}%";
            query = query.Where(s => EF.Functions.Like(s.Title, term)
                                  || (s.Artist != null && EF.Functions.Like(s.Artist.Name, term)));
        }

        query = query.Include(s => s.Artist).Include(s => s.Album);

        var totalCount = await query.CountAsync();
        var sortedQuery = ApplySongSortOrder(query, SongSortOrder.TrackNumberAsc);

        var pagedData = await sortedQuery
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .AsSplitQuery()
            .ToListAsync();

        return new PagedResult<Song> {
            Items = pagedData,
            TotalCount = totalCount,
            PageNumber = pageNumber,
            PageSize = pageSize
        };
    }

    public async Task<PagedResult<Song>> SearchSongsInArtistPagedAsync(Guid artistId, string searchTerm, int pageNumber, int pageSize) {
        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1) pageSize = 1;

        await using var context = await _contextFactory.CreateDbContextAsync();

        var query = context.Songs.Where(s => s.ArtistId == artistId);

        if (!string.IsNullOrWhiteSpace(searchTerm)) {
            var trimmedTerm = searchTerm.Trim();
            var term = $"%{trimmedTerm}%";
            query = query.Where(s => EF.Functions.Like(s.Title, term)
                                  || (s.Album != null && EF.Functions.Like(s.Album.Title, term)));
        }

        query = query.Include(s => s.Artist).Include(s => s.Album);

        var totalCount = await query.CountAsync();
        var sortedQuery = ApplySongSortOrder(query, SongSortOrder.AlbumAsc);

        var pagedData = await sortedQuery
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .AsSplitQuery()
            .ToListAsync();

        return new PagedResult<Song> {
            Items = pagedData,
            TotalCount = totalCount,
            PageNumber = pageNumber,
            PageSize = pageSize
        };
    }

    #endregion

    #region Private Helpers

    private async Task FetchAndUpdateArtistFromRemoteAsync(MusicDbContext context, Artist artist) {
        var lastFmTask = _lastFmService.GetArtistInfoAsync(artist.Name);
        var spotifyImageTask = _spotifyService.GetArtistImageUrlAsync(artist.Name);

        ArtistInfo? lastFmArtistInfo = null;
        try { lastFmArtistInfo = await lastFmTask; }
        catch (Exception) { /* Service may be unavailable; proceed without this data. */ }

        string? spotifyImageUrl = null;
        try { spotifyImageUrl = await spotifyImageTask; }
        catch (Exception) { /* Service may be unavailable; proceed without this data. */ }

        var updated = false;
        if (string.IsNullOrWhiteSpace(artist.Biography) && !string.IsNullOrWhiteSpace(lastFmArtistInfo?.Biography)) {
            artist.Biography = lastFmArtistInfo.Biography;
            updated = true;
        }

        if (!string.IsNullOrWhiteSpace(spotifyImageUrl)) {
            artist.RemoteImageUrl = spotifyImageUrl;
            var downloaded = await DownloadAndCacheArtistImageAsync(artist, new Uri(spotifyImageUrl));
            if (downloaded) updated = true;
        }

        if (updated) {
            await context.SaveChangesAsync();
            ArtistMetadataUpdated?.Invoke(this, new ArtistMetadataUpdatedEventArgs(artist.Id, artist.LocalImageCachePath));
        }
    }

    private async Task<bool> DownloadAndCacheArtistImageAsync(Artist artist, Uri imageUrl) {
        var localPath = GetArtistImageCachePath(artist.Id);

        if (artist.LocalImageCachePath == localPath && artist.RemoteImageUrl == imageUrl.ToString() && _fileSystem.FileExists(localPath)) {
            return false;
        }

        var semaphore = _artistImageWriteSemaphores.GetOrAdd(localPath, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync();
        try {
            if (_fileSystem.FileExists(localPath)) {
                return false;
            }

            using var response = await _httpClient.GetAsync(imageUrl);
            response.EnsureSuccessStatusCode();
            var imageBytes = await response.Content.ReadAsByteArrayAsync();
            await _fileSystem.WriteAllBytesAsync(localPath, imageBytes);

            artist.LocalImageCachePath = localPath;
            artist.RemoteImageUrl = imageUrl.ToString();
            return true;
        }
        catch (Exception ex) {
            Debug.WriteLine($"[LibraryService] ERROR: Failed to download image for artist '{artist.Name}' from {imageUrl}. {ex.Message}");
            artist.LocalImageCachePath = null;
            return false;
        }
        finally {
            semaphore.Release();
        }
    }

    private string GetArtistImageCachePath(Guid artistId) {
        return _fileSystem.Combine(_pathConfig.ArtistImageCachePath, $"{artistId}.jpg");
    }

    private async Task<Artist> GetOrCreateArtistAsync(MusicDbContext context, string name) {
        var normalizedName = string.IsNullOrWhiteSpace(name) ? UnknownArtistName : name.Trim();

        var artist = context.Artists.Local.FirstOrDefault(a => a.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase))
            ?? await context.Artists.FirstOrDefaultAsync(a => a.Name == normalizedName);

        if (artist != null) {
            return artist;
        }

        var newArtist = new Artist { Name = normalizedName };
        context.Artists.Add(newArtist);
        return newArtist;
    }

    private async Task<Album> GetOrCreateAlbumAsync(MusicDbContext context, string title, string albumArtistName, int? year) {
        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? UnknownAlbumName : title.Trim();
        var artistForAlbum = await GetOrCreateArtistAsync(context, albumArtistName);
        var album = context.Albums.Local.FirstOrDefault(a => a.Title.Equals(normalizedTitle, StringComparison.OrdinalIgnoreCase) && a.ArtistId == artistForAlbum.Id)
            ?? await context.Albums.FirstOrDefaultAsync(a => a.Title == normalizedTitle && a.ArtistId == artistForAlbum.Id);

        if (album == null) {
            album = new Album { Title = normalizedTitle, Year = year, ArtistId = artistForAlbum.Id };
            context.Albums.Add(album);
        }

        if (album.Year == null && year.HasValue) {
            album.Year = year;
        }

        return album;
    }

    private async Task<List<Genre>> EnsureGenresExistAsync(MusicDbContext context, IEnumerable<string>? genreNames, ConcurrentDictionary<string, Genre>? cache = null) {
        if (genreNames == null || !genreNames.Any()) {
            return new List<Genre>();
        }

        var finalGenres = new List<Genre>();
        var distinctGenreNames = genreNames
            .Select(g => g.Trim())
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!distinctGenreNames.Any()) {
            return new List<Genre>();
        }

        if (cache != null) {
            foreach (var genreName in distinctGenreNames) {
                if (cache.TryGetValue(genreName, out var cachedGenre)) {
                    if (context.Entry(cachedGenre).State == EntityState.Detached) {
                        context.Genres.Attach(cachedGenre);
                    }
                    finalGenres.Add(cachedGenre);
                    continue;
                }

                var localGenre = context.Genres.Local
                    .FirstOrDefault(g => g.Name.Equals(genreName, StringComparison.OrdinalIgnoreCase));
                if (localGenre != null) {
                    finalGenres.Add(localGenre);
                    cache.TryAdd(genreName, localGenre);
                    continue;
                }

                var newGenre = new Genre { Name = genreName };
                context.Genres.Add(newGenre);
                cache.TryAdd(genreName, newGenre);
                finalGenres.Add(newGenre);
            }
            return finalGenres;
        }
        else {
            var existingGenres = await context.Genres
                .Where(g => distinctGenreNames.Contains(g.Name))
                .ToListAsync();

            var existingGenreNames = existingGenres.Select(g => g.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var newGenres = new List<Genre>();

            foreach (var genreName in distinctGenreNames) {
                if (!existingGenreNames.Contains(genreName)) {
                    var newGenre = new Genre { Name = genreName };
                    newGenres.Add(newGenre);
                }
            }

            if (newGenres.Any()) {
                context.Genres.AddRange(newGenres);
            }

            return existingGenres.Concat(newGenres).ToList();
        }
    }

    private async Task ReindexPlaylistAsync(MusicDbContext context, Guid playlistId) {
        var playlistSongs = await context.PlaylistSongs
            .Where(ps => ps.PlaylistId == playlistId)
            .OrderBy(ps => ps.Order)
            .AsTracking()
            .ToListAsync();

        for (var i = 0; i < playlistSongs.Count; i++) {
            if (playlistSongs[i].Order != i)
                playlistSongs[i].Order = i;
        }
    }

    private bool IsUniqueConstraintViolation(DbUpdateException ex) {
        var innerMessage = ex.InnerException?.Message ?? "";
        return innerMessage.Contains("UNIQUE constraint failed")
               || innerMessage.Contains("Violation of UNIQUE KEY constraint")
               || innerMessage.Contains("duplicate key value violates unique constraint");
    }

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

    private IQueryable<Song> BuildSongSearchQuery(MusicDbContext context, string searchTerm) {
        var term = $"%{searchTerm}%";
        return context.Songs
            .Include(s => s.Artist)
            .Include(s => s.Album).ThenInclude(a => a!.Artist)
            .Where(s => EF.Functions.Like(s.Title, term)
                || (s.Album != null && EF.Functions.Like(s.Album.Title, term))
                || (s.Artist != null && EF.Functions.Like(s.Artist.Name, term))
                || (s.Album != null && s.Album.Artist != null && EF.Functions.Like(s.Album.Artist.Name, term)));
    }

    private IQueryable<Artist> BuildArtistSearchQuery(MusicDbContext context, string searchTerm) {
        return context.Artists.Where(a => EF.Functions.Like(a.Name, $"%{searchTerm}%"));
    }

    private IQueryable<Album> BuildAlbumSearchQuery(MusicDbContext context, string searchTerm) {
        var term = $"%{searchTerm}%";
        return context.Albums
            .Include(al => al.Artist)
            .Where(al => EF.Functions.Like(al.Title, term)
                || (al.Artist != null && EF.Functions.Like(al.Artist.Name, term)));
    }

    #endregion
}