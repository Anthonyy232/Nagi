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
using Nagi.Data;
using Nagi.Helpers;
using Nagi.Models;
using Nagi.Services.Abstractions;
using Nagi.Services.Data;

namespace Nagi.Services.Implementations;

/// <summary>
/// Manages the music library, including scanning, metadata, and data access.
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
            Debug.WriteLine($"Could not get LastWriteTimeUtc for folder '{path}'. {ex.Message}");
            folder.LastModifiedDate = null;
        }

        context.Folders.Add(folder);
        try {
            await context.SaveChangesAsync();
            return folder;
        }
        catch (DbUpdateException ex) {
            Debug.WriteLine($"[ERROR] Database update failed for path '{path}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
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

            var songsInFolderQuery = context.Songs.AsNoTracking().Where(s => s.FolderId == folderId);
            var songIdsToDelete = await songsInFolderQuery.Select(s => s.Id).ToListAsync();

            if (!songIdsToDelete.Any()) {
                context.Folders.Remove(folder);
                await context.SaveChangesAsync();
                await transaction.CommitAsync();
                return true;
            }

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

            var associatedTrackArtistIds = await songsInFolderQuery.Select(s => s.ArtistId).Distinct().ToListAsync();
            var associatedAlbumArtistIds = await context.Albums.AsNoTracking()
                .Where(a => associatedAlbumIds.Contains(a.Id))
                .Select(a => a.ArtistId)
                .Distinct()
                .ToListAsync();
            var allAssociatedArtistIds = associatedTrackArtistIds.Union(associatedAlbumArtistIds).Distinct().ToList();

            await context.PlaylistSongs.Where(ps => songIdsToDelete.Contains(ps.SongId)).ExecuteDeleteAsync();
            await context.Songs.Where(s => songIdsToDelete.Contains(s.Id)).ExecuteDeleteAsync();

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

            if (allAssociatedArtistIds.Any()) {
                var artistsWithSongs = await context.Songs.AsNoTracking().Where(s => allAssociatedArtistIds.Contains(s.ArtistId)).Select(s => s.ArtistId).Distinct().ToHashSetAsync();
                var artistsWithAlbums = await context.Albums.AsNoTracking().Where(a => allAssociatedArtistIds.Contains(a.ArtistId)).Select(a => a.ArtistId).Distinct().ToHashSetAsync();
                var stillReferencedArtistIds = artistsWithSongs.Union(artistsWithAlbums);
                var orphanedArtistIds = allAssociatedArtistIds.Except(stillReferencedArtistIds).ToList();

                if (orphanedArtistIds.Any()) {
                    var artistsToDelete = await context.Artists.AsNoTracking()
                        .Where(a => orphanedArtistIds.Contains(a.Id) && a.LocalImageCachePath != null)
                        .ToListAsync();

                    foreach (var artist in artistsToDelete) {
                        if (_fileSystem.FileExists(artist.LocalImageCachePath!)) {
                            try { _fileSystem.DeleteFile(artist.LocalImageCachePath!); }
                            catch (Exception fileEx) { Debug.WriteLine($"Failed to delete artist image file '{artist.LocalImageCachePath}'. {fileEx.Message}"); }
                        }
                    }
                    await context.Artists.Where(a => orphanedArtistIds.Contains(a.Id)).ExecuteDeleteAsync();
                }
            }

            context.Folders.Remove(folder);
            await context.SaveChangesAsync();

            await transaction.CommitAsync();

            foreach (var artPath in albumArtPathsToDelete) {
                if (_fileSystem.FileExists(artPath)) {
                    try { _fileSystem.DeleteFile(artPath); }
                    catch (Exception fileEx) { Debug.WriteLine($"Failed to delete art file '{artPath}'. {fileEx.Message}"); }
                }
            }
            return true;
        }
        catch (Exception ex) {
            Debug.WriteLine($"[ERROR] Database update for folder ID '{folderId}' failed and was rolled back. Reason: {ex.Message}");
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
            Debug.WriteLine($"Could not get LastWriteTimeUtc for folder '{folder.Path}'. {ex.Message}");
        }

        context.Folders.Update(folder);
        try {
            await context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex) {
            Debug.WriteLine($"[ERROR] Database update failed for ID '{folder.Id}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            await context.Entry(folder).ReloadAsync();
            return false;
        }
    }

    public async Task<int> GetSongCountForFolderAsync(Guid folderId) {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Songs.CountAsync(s => s.FolderId == folderId);
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
            Debug.WriteLine($"[ERROR] Database update failed for path '{songData.FilePath}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            context.Entry(songData).State = EntityState.Detached;
            return null;
        }
    }

    public async Task<Song?> AddSongWithDetailsAsync(Guid folderId, string filePath, string title, string trackArtistName, string? albumTitle, string? albumArtistName, TimeSpan duration, string? songSpecificCoverArtUri, string? lightSwatchId, string? darkSwatchId, int? releaseYear = null, IEnumerable<string>? genres = null, int? trackNumber = null, int? discNumber = null, int? sampleRate = null, int? bitrate = null, int? channels = null, DateTime? fileCreatedDate = null, DateTime? fileModifiedDate = null) {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("FilePath is required.", nameof(filePath));

        for (var i = 0; i < 3; i++) {
            await using var context = await _contextFactory.CreateDbContextAsync();
            try {
                if (await context.Songs.AsNoTracking().AnyAsync(s => s.FilePath == filePath))
                    return await context.Songs.AsNoTracking().FirstAsync(s => s.FilePath == filePath);

                var song = new Song {
                    FilePath = filePath,
                    Title = title.Trim(),
                    Duration = duration,
                    AlbumArtUriFromTrack = songSpecificCoverArtUri,
                    LightSwatchId = lightSwatchId,
                    DarkSwatchId = darkSwatchId,
                    Year = releaseYear,
                    Genres = genres?.ToList() ?? new List<string>(),
                    TrackNumber = trackNumber,
                    DiscNumber = discNumber,
                    SampleRate = sampleRate,
                    Bitrate = bitrate,
                    Channels = channels,
                    DateAddedToLibrary = DateTime.UtcNow,
                    FileCreatedDate = fileCreatedDate,
                    FileModifiedDate = fileModifiedDate,
                    FolderId = folderId
                };

                var trackArtist = await GetOrCreateArtistAsync(context, trackArtistName);
                song.Artist = trackArtist;

                if (!string.IsNullOrWhiteSpace(albumTitle)) {
                    var artistNameToUseForAlbum = string.IsNullOrWhiteSpace(albumArtistName) ? trackArtistName : albumArtistName;
                    var album = await GetOrCreateAlbumAsync(context, albumTitle, artistNameToUseForAlbum, releaseYear);

                    if (string.IsNullOrEmpty(album.CoverArtUri) && !string.IsNullOrEmpty(songSpecificCoverArtUri)) {
                        album.CoverArtUri = songSpecificCoverArtUri;
                    }

                    song.Album = album;
                }

                context.Songs.Add(song);
                await context.SaveChangesAsync();
                return song;
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex)) {
                await Task.Delay(50 + Random.Shared.Next(50));
            }
            catch (DbUpdateException ex) {
                Debug.WriteLine($"[ERROR] Unrecoverable database update failed for path '{filePath}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
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
                    Debug.WriteLine($"Failed to delete art file '{albumArtPathToDelete}'. {fileEx.Message}");
                }
            }
            return true;
        }
        catch (DbUpdateException ex) {
            Debug.WriteLine($"[ERROR] Database update failed for ID '{songId}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
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
            Debug.WriteLine($"[ERROR] Database update failed for ID '{songToUpdate.Id}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
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
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await BuildSongSearchQuery(context, searchTerm)
            .AsNoTracking()
            .OrderBy(s => s.Title).ThenBy(s => s.Id)
            .AsSplitQuery()
            .ToListAsync();
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
                            Debug.WriteLine($"[ERROR] Failed to update artist {artistId} in background. {ex}");
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
            Debug.WriteLine($"[ERROR] Failed to download image for artist '{artist.Name}' from {imageUrl}. {ex.Message}");
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

    private async Task<Artist> GetOrCreateArtistAsync(MusicDbContext context, string name) {
        var normalizedName = string.IsNullOrWhiteSpace(name) ? UnknownArtistName : name.Trim();
        var artist = context.Artists.Local.FirstOrDefault(a => a.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase))
            ?? await context.Artists.FirstOrDefaultAsync(a => a.Name == normalizedName);

        if (artist == null) {
            artist = new Artist { Name = normalizedName };
            context.Artists.Add(artist);
        }
        return artist;
    }

    public async Task<IEnumerable<Artist>> GetAllArtistsAsync() {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Artists.AsNoTracking().OrderBy(a => a.Name).ToListAsync();
    }

    public async Task<IEnumerable<Artist>> SearchArtistsAsync(string searchTerm) {
        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetAllArtistsAsync();
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await BuildArtistSearchQuery(context, searchTerm).AsNoTracking().OrderBy(a => a.Name).ToListAsync();
    }

    private IQueryable<Artist> BuildArtistSearchQuery(MusicDbContext context, string searchTerm) {
        return context.Artists.Where(a => EF.Functions.Like(a.Name, $"%{searchTerm}%"));
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

    private async Task<Album> GetOrCreateAlbumAsync(MusicDbContext context, string title, string albumArtistName, int? year) {
        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? UnknownAlbumName : title.Trim();
        var artistForAlbum = await GetOrCreateArtistAsync(context, albumArtistName);
        var album = context.Albums.Local.FirstOrDefault(a => a.Title.Equals(normalizedTitle, StringComparison.OrdinalIgnoreCase) && a.ArtistId == artistForAlbum.Id)
            ?? await context.Albums.FirstOrDefaultAsync(a => a.Title == normalizedTitle && a.ArtistId == artistForAlbum.Id);

        if (album == null) {
            album = new Album { Title = normalizedTitle, Year = year, ArtistId = artistForAlbum.Id };
            context.Albums.Add(album);
        }
        else {
            context.Attach(album);
        }

        if (album.Year == null && year.HasValue) {
            album.Year = year;
        }

        return album;
    }

    public async Task<IEnumerable<Album>> GetAllAlbumsAsync() {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Albums.AsNoTracking()
            .Include(al => al.Artist)
            .OrderBy(al => al.Title)
            .AsSplitQuery()
            .ToListAsync();
    }

    public async Task<IEnumerable<Album>> SearchAlbumsAsync(string searchTerm) {
        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetAllAlbumsAsync();
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await BuildAlbumSearchQuery(context, searchTerm)
            .AsNoTracking()
            .OrderBy(al => al.Title)
            .AsSplitQuery()
            .ToListAsync();
    }

    private IQueryable<Album> BuildAlbumSearchQuery(MusicDbContext context, string searchTerm) {
        var term = $"%{searchTerm}%";
        return context.Albums
            .Include(al => al.Artist)
            .Where(al => EF.Functions.Like(al.Title, term)
                || (al.Artist != null && EF.Functions.Like(al.Artist.Name, term)));
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
            Debug.WriteLine($"[ERROR] Database update failed for name '{name}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
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
            Debug.WriteLine($"[ERROR] Database update failed for ID '{playlist.Id}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
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
            Debug.WriteLine($"[ERROR] Database update failed for ID '{playlist.Id}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
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
            Debug.WriteLine($"[ERROR] Database update failed for playlist '{playlistId}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
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
            Debug.WriteLine($"[ERROR] Database update failed for playlist '{playlistId}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
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
            Debug.WriteLine($"[ERROR] Database update failed for playlist reorder '{playlistId}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            return false;
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

    #region Scan Management

    private class ScanContext {
        public ScanContext(Guid folderId, IProgress<ScanProgress>? progress) {
            FolderId = folderId;
            Progress = progress;
        }

        public Guid FolderId { get; }
        public IProgress<ScanProgress>? Progress { get; }
        public ConcurrentDictionary<string, Artist> ArtistCache { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, Album> AlbumCache { get; } = new(StringComparer.OrdinalIgnoreCase);
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
        }
    }

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
            var newFilesToProcess = _fileSystem.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
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

        progress?.Report(new ScanProgress { StatusText = "Scanning for file changes...", IsIndeterminate = true });
        context.ChangeTracker.AutoDetectChangesEnabled = false;
        var changesMade = false;

        try {
            var filesOnDisk = _fileSystem.EnumerateFiles(folder.Path, "*.*", SearchOption.AllDirectories)
                .Where(file => file is not null && MusicFileExtensions.Contains(_fileSystem.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var filePathsInDb = (await context.Songs
                    .Where(s => s.FolderId == folder.Id)
                    .Select(s => s.FilePath)
                    .ToListAsync())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var pathsToRemove = filePathsInDb.Except(filesOnDisk).ToList();
            if (pathsToRemove.Any()) {
                progress?.Report(new ScanProgress { StatusText = $"Removing {pathsToRemove.Count} deleted songs...", IsIndeterminate = true });
                await context.Songs.Where(s => s.FolderId == folder.Id && pathsToRemove.Contains(s.FilePath)).ExecuteDeleteAsync();
                changesMade = true;
            }

            var filesToAddStream = filesOnDisk.Except(filePathsInDb);
            var scanContext = new ScanContext(folder.Id, progress);

            await ProcessNewFilesAsync(context, filesToAddStream, scanContext);

            if (scanContext.NewSongsAdded > 0) {
                changesMade = true;
            }

            var updatedFolder = await context.Folders.AsTracking().FirstOrDefaultAsync(f => f.Id == folder.Id);
            if (updatedFolder != null) {
                try { updatedFolder.LastModifiedDate = _fileSystem.GetLastWriteTimeUtc(folder.Path); }
                catch (Exception ex) { Debug.WriteLine($"Could not update LastWriteTimeUtc for folder '{folder.Path}'. {ex.Message}"); }
            }

            if (context.ChangeTracker.HasChanges()) await SaveChangesAndClearTrackerAsync(context, "[Rescan Final]");

            progress?.Report(new ScanProgress { StatusText = "Rescan complete.", Percentage = 100.0 });
            return changesMade;
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

    private async Task ProcessNewFilesAsync(MusicDbContext context, IEnumerable<string?> filePaths, ScanContext scanContext) {
        var nonNullFilePaths = filePaths.Where(filePath => filePath != null).Cast<string>();
        const int processingChunkSize = 100;

        await scanContext.PopulateCachesAsync(context);

        foreach (var chunk in nonNullFilePaths.Chunk(processingChunkSize)) {
            var allMetadata = await Task.WhenAll(chunk.Select(f => _metadataExtractor.ExtractMetadataAsync(f)));

            foreach (var metadata in allMetadata) {
                if (metadata.ExtractionFailed) continue;
                if (AddSongFromMetadata(context, metadata, scanContext)) {
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
            await SaveChangesAndClearTrackerAsync(context, "[Scan Final]");
        }

        scanContext.Progress?.Report(new ScanProgress {
            NewSongsFound = scanContext.NewSongsAdded,
            IsIndeterminate = true,
            StatusText = "Finalizing..."
        });
    }

    private bool AddSongFromMetadata(MusicDbContext context, SongFileMetadata metadata, ScanContext scanContext) {
        try {
            var trackArtist = GetOrCreateArtistInScan(context, metadata.Artist, scanContext);
            var album = GetOrCreateAlbumInScan(context, metadata.Album, metadata.AlbumArtist, metadata.Year, scanContext, metadata.CoverArtUri);

            var song = new Song {
                FilePath = metadata.FilePath,
                Title = metadata.Title,
                Duration = metadata.Duration,
                AlbumArtUriFromTrack = metadata.CoverArtUri,
                LightSwatchId = metadata.LightSwatchId,
                DarkSwatchId = metadata.DarkSwatchId,
                Year = metadata.Year,
                Genres = metadata.Genres,
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
                AlbumId = album?.Id
            };

            context.Songs.Add(song);
            return true;
        }
        catch (Exception ex) {
            Debug.WriteLine($"[ERROR] Failed to prepare song '{metadata.FilePath}' for addition. Reason: {ex.Message}");
            return false;
        }
    }

    private Artist GetOrCreateArtistInScan(MusicDbContext context, string name, ScanContext scanContext) {
        var normalizedName = string.IsNullOrWhiteSpace(name) ? UnknownArtistName : name.Trim();

        if (scanContext.ArtistCache.TryGetValue(normalizedName, out var artist)) return artist;

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
            bool updated = false;
            if (year.HasValue && album.Year == null) {
                album.Year = year;
                updated = true;
            }
            if (string.IsNullOrEmpty(album.CoverArtUri) && !string.IsNullOrEmpty(coverArtUri)) {
                album.CoverArtUri = coverArtUri;
                updated = true;
            }
            if (updated) {
                context.Albums.Update(album);
            }
            return album;
        }

        var newAlbum = new Album { Title = normalizedTitle, Year = year, ArtistId = artistForAlbum.Id, CoverArtUri = coverArtUri };
        context.Albums.Add(newAlbum);
        scanContext.AlbumCache.TryAdd(cacheKey, newAlbum);
        return newAlbum;
    }

    private async Task<bool> SaveChangesAndClearTrackerAsync(MusicDbContext context, string operationTag) {
        if (!context.ChangeTracker.HasChanges()) return true;

        for (var i = 0; i < 3; i++) {
            try {
                await context.SaveChangesAsync();
                return true;
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex)) {
                Debug.WriteLine($"[WARNING] Handled unique constraint violation during {operationTag}. Retrying... (Attempt {i + 1})");
                var conflictingEntries = context.ChangeTracker.Entries()
                    .Where(e => e.State == EntityState.Added)
                    .ToList();
                foreach (var entry in conflictingEntries) {
                    entry.State = EntityState.Detached;
                }

                await Task.Delay(50 + Random.Shared.Next(50));
            }
            catch (DbUpdateException ex) {
                Debug.WriteLine($"[ERROR] Failed to save batch during {operationTag}. Reason: {ex.InnerException?.Message ?? ex.Message}");
                return false;
            }
        }

        Debug.WriteLine($"[ERROR] Failed to save batch during {operationTag} after multiple retries.");
        return false;
    }

    private async Task<Folder?> GetOrCreateFolderForScanAsync(MusicDbContext context, string folderPath) {
        var folder = await context.Folders.AsTracking().FirstOrDefaultAsync(f => f.Path == folderPath);
        DateTime? fileSystemLastModified = null;
        try { fileSystemLastModified = _fileSystem.GetLastWriteTimeUtc(folderPath); }
        catch (Exception ex) { Debug.WriteLine($"Could not get LastWriteTimeUtc for folder '{folderPath}'. {ex.Message}"); }

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
            Debug.WriteLine($"[ERROR] Failed to save folder '{folderPath}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            context.Entry(folder).State = EntityState.Detached;
            return null;
        }

        return folder;
    }

    private bool IsUniqueConstraintViolation(DbUpdateException ex) {
        var innerMessage = ex.InnerException?.Message ?? "";
        return innerMessage.Contains("UNIQUE constraint failed")
               || innerMessage.Contains("Violation of UNIQUE KEY constraint")
               || innerMessage.Contains("duplicate key value violates unique constraint");
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
            Debug.WriteLine($"[FATAL] Failed to clear all library data. Reason: {ex.Message}");
            throw;
        }
    }

    private async Task ClearAllTablesAsync(MusicDbContext context) {
        try {
            context.ChangeTracker.AutoDetectChangesEnabled = false;
            await context.PlaylistSongs.ExecuteDeleteAsync();
            await context.Songs.ExecuteDeleteAsync();
            await context.Playlists.ExecuteDeleteAsync();
            await context.Albums.ExecuteDeleteAsync();
            await context.Artists.ExecuteDeleteAsync();
            await context.Folders.ExecuteDeleteAsync();
        }
        finally {
            context.ChangeTracker.AutoDetectChangesEnabled = true;
            context.ChangeTracker.Clear();
        }
    }

    #endregion

    #region Incremental Loading Implementations

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
            : BuildSongSearchQuery(context, searchTerm);

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
            : BuildArtistSearchQuery(context, searchTerm);

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
            .OrderBy(al => al.Title)
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
            : BuildAlbumSearchQuery(context, searchTerm);

        var totalCount = await query.CountAsync();
        var pagedData = await query.AsNoTracking()
            .OrderBy(al => al.Title)
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

    #endregion
}