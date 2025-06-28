using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Nagi.Data;
using Nagi.Models;
using Nagi.Services.Abstractions;

namespace Nagi.Services.Implementations;

/// <summary>
/// Implements the ILibraryService interface to provide data access and business logic
/// for the music library, interacting with the database and file system.
/// </summary>
public class LibraryService : ILibraryService {
    public event EventHandler<ArtistMetadataUpdatedEventArgs>? ArtistMetadataUpdated;

    private const int ScanBatchSize = 100;
    private const string UnknownArtistName = "Unknown Artist";
    private const string UnknownAlbumName = "Unknown Album";
    private const string ArtistImageCacheFolderName = "ArtistImages";
    private const string AlbumArtCacheFolderName = "AlbumArt";
    private static readonly string[] MusicFileExtensions = { ".mp3", ".flac", ".wav", ".ogg", ".m4a", ".wma", ".aac" };

    private readonly MusicDbContext _context;
    private readonly IFileSystemService _fileSystem;
    private readonly IMetadataExtractor _metadataExtractor;
    private readonly ILastFmService _lastFmService;
    private readonly HttpClient _httpClient;
    private readonly int _parallelExtractionBatchSize;

    // Concurrency guard to ensure only one background metadata fetch runs at a time.
    private static bool _isMetadataFetchRunning;
    private static readonly object _metadataFetchLock = new();

    public LibraryService(MusicDbContext context, IFileSystemService fileSystem, IMetadataExtractor metadataExtractor, ILastFmService lastFmService, IHttpClientFactory httpClientFactory) {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _metadataExtractor = metadataExtractor ?? throw new ArgumentNullException(nameof(metadataExtractor));
        _lastFmService = lastFmService ?? throw new ArgumentNullException(nameof(lastFmService));
        _httpClient = httpClientFactory.CreateClient("ImageDownloader");
        _parallelExtractionBatchSize = Environment.ProcessorCount * 2;
    }

    #region Folder Management

    /// <summary>
    /// Adds a new folder to the library if it does not already exist.
    /// </summary>
    /// <param name="path">The absolute path of the folder.</param>
    /// <param name="name">An optional display name for the folder. If null, the directory name is used.</param>
    /// <returns>The newly created or existing Folder entity, or null if the operation fails.</returns>
    public async Task<Folder?> AddFolderAsync(string path, string? name = null) {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var existingFolder = await _context.Folders.FirstOrDefaultAsync(f => f.Path == path);
        if (existingFolder != null) return existingFolder;

        var folder = new Folder { Path = path, Name = name ?? _fileSystem.GetDirectoryNameFromPath(path) };
        try {
            folder.LastModifiedDate = _fileSystem.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) {
            Debug.WriteLine($"[LibraryService:AddFolderAsync] Warning: Could not get LastWriteTimeUtc for folder '{path}': {ex.Message}");
            folder.LastModifiedDate = null;
        }

        _context.Folders.Add(folder);
        try {
            await _context.SaveChangesAsync();
            return folder;
        }
        catch (DbUpdateException ex) {
            Debug.WriteLine($"[LibraryService:AddFolderAsync] ERROR: Database update failed for path '{path}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            // Detach the failed entity to prevent DbContext corruption.
            _context.Entry(folder).State = EntityState.Detached;
            return null;
        }
    }

    /// <summary>
    /// Removes a folder and all its associated songs from the library.
    /// </summary>
    /// <param name="folderId">The ID of the folder to remove.</param>
    /// <returns>True if the folder was successfully removed, otherwise false.</returns>
    public async Task<bool> RemoveFolderAsync(Guid folderId) {
        var folder = await _context.Folders.FindAsync(folderId);
        if (folder == null) return false;

        var songsInFolder = await _context.Songs
            .Where(s => s.FolderId == folderId)
            .ToListAsync();

        var albumArtPathsToDelete = songsInFolder
            .Select(s => s.AlbumArtUriFromTrack)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct()
            .ToList();

        _context.Songs.RemoveRange(songsInFolder);
        _context.Folders.Remove(folder);

        try {
            await _context.SaveChangesAsync();

            // Clean up associated album art files from the cache.
            foreach (var artPath in albumArtPathsToDelete) {
                if (_fileSystem.FileExists(artPath!)) {
                    try {
                        _fileSystem.DeleteFile(artPath!);
                    }
                    catch (Exception fileEx) {
                        Debug.WriteLine($"[LibraryService:RemoveFolderAsync] Warning: Failed to delete art file '{artPath}': {fileEx.Message}");
                    }
                }
            }
            return true;
        }
        catch (DbUpdateException ex) {
            Debug.WriteLine($"[LibraryService:RemoveFolderAsync] ERROR: Database update failed for ID '{folderId}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            // Revert changes in the context to allow for potential retries.
            _context.Entry(folder).State = EntityState.Unchanged;
            foreach (var song in songsInFolder) _context.Entry(song).State = EntityState.Unchanged;
            return false;
        }
    }

    /// <summary>
    /// Retrieves a folder by its unique identifier.
    /// </summary>
    /// <param name="folderId">The ID of the folder.</param>
    /// <returns>The Folder entity, or null if not found.</returns>
    public async Task<Folder?> GetFolderByIdAsync(Guid folderId) {
        return await _context.Folders.FirstOrDefaultAsync(f => f.Id == folderId);
    }

    /// <summary>
    /// Retrieves a folder by its path.
    /// </summary>
    /// <param name="path">The path of the folder.</param>
    /// <returns>The Folder entity, or null if not found.</returns>
    public async Task<Folder?> GetFolderByPathAsync(string path) {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return await _context.Folders.FirstOrDefaultAsync(f => f.Path == path);
    }

    /// <summary>
    /// Retrieves all folders in the library, ordered by name.
    /// </summary>
    /// <returns>An enumerable collection of all Folder entities.</returns>
    public async Task<IEnumerable<Folder>> GetAllFoldersAsync() {
        return await _context.Folders.OrderBy(f => f.Name).ThenBy(f => f.Path).ToListAsync();
    }

    /// <summary>
    /// Updates the properties of an existing folder.
    /// </summary>
    /// <param name="folder">The folder entity with updated values.</param>
    /// <returns>True if the update was successful, otherwise false.</returns>
    public async Task<bool> UpdateFolderAsync(Folder folder) {
        if (folder == null) throw new ArgumentNullException(nameof(folder));

        try {
            folder.LastModifiedDate = _fileSystem.GetLastWriteTimeUtc(folder.Path);
        }
        catch (Exception ex) {
            Debug.WriteLine($"[LibraryService:UpdateFolderAsync] Warning: Could not get LastWriteTimeUtc for folder '{folder.Path}': {ex.Message}");
        }

        _context.Folders.Update(folder);
        try {
            await _context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex) {
            Debug.WriteLine($"[LibraryService:UpdateFolderAsync] ERROR: Database update failed for ID '{folder.Id}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            // Reload the entity from the database to discard failed changes.
            await _context.Entry(folder).ReloadAsync();
            return false;
        }
    }

    /// <summary>
    /// Gets the total number of songs within a specific folder.
    /// </summary>
    /// <param name="folderId">The ID of the folder.</param>
    /// <returns>The count of songs in the folder.</returns>
    public async Task<int> GetSongCountForFolderAsync(Guid folderId) {
        return await _context.Songs.CountAsync(s => s.FolderId == folderId);
    }

    #endregion

    #region Song Management

    /// <summary>
    /// Adds a new song to the library if it does not already exist based on file path.
    /// </summary>
    /// <param name="songData">The song entity to add.</param>
    /// <returns>The newly created or existing Song entity, or null if the operation fails.</returns>
    public async Task<Song?> AddSongAsync(Song songData) {
        if (songData == null) throw new ArgumentNullException(nameof(songData));
        if (string.IsNullOrWhiteSpace(songData.FilePath))
            throw new ArgumentException("Song FilePath cannot be empty.", nameof(songData.FilePath));

        var existingSong = await _context.Songs.FirstOrDefaultAsync(s => s.FilePath == songData.FilePath);
        if (existingSong != null) return existingSong;

        _context.Songs.Add(songData);
        try {
            await _context.SaveChangesAsync();
            return songData;
        }
        catch (DbUpdateException ex) {
            Debug.WriteLine($"[LibraryService:AddSongAsync] ERROR: Database update failed for path '{songData.FilePath}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            _context.Entry(songData).State = EntityState.Detached;
            return null;
        }
    }

    /// <summary>
    /// Creates and adds a new song with all its details, including artist and album relations.
    /// </summary>
    /// <returns>The newly created Song entity, or the existing one if found.</returns>
    public async Task<Song?> AddSongWithDetailsAsync(
        Guid folderId,
        string filePath, string title, string trackArtistName, string? albumTitle, string? albumArtistName,
        TimeSpan duration, string? songSpecificCoverArtUri,
        string? lightSwatchId, string? darkSwatchId,
        int? releaseYear = null, IEnumerable<string>? genres = null,
        int? trackNumber = null, int? discNumber = null, int? sampleRate = null, int? bitrate = null,
        int? channels = null,
        DateTime? fileCreatedDate = null, DateTime? fileModifiedDate = null) {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("FilePath is required.", nameof(filePath));

        var existingSong = await _context.Songs.FirstOrDefaultAsync(s => s.FilePath == filePath);
        if (existingSong != null) return existingSong;

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

        var trackArtist = await GetOrCreateArtistAsync(trackArtistName);
        song.Artist = trackArtist;

        if (!string.IsNullOrWhiteSpace(albumTitle)) {
            var artistNameToUseForAlbum = string.IsNullOrWhiteSpace(albumArtistName) ? trackArtistName : albumArtistName;
            var album = await GetOrCreateAlbumAsync(albumTitle, artistNameToUseForAlbum, releaseYear);
            song.Album = album;
        }

        _context.Songs.Add(song);
        return song;
    }

    /// <summary>
    /// Removes a song from the library.
    /// </summary>
    /// <param name="songId">The ID of the song to remove.</param>
    /// <returns>True if the song was successfully removed, otherwise false.</returns>
    public async Task<bool> RemoveSongAsync(Guid songId) {
        var song = await _context.Songs.FindAsync(songId);
        if (song == null) return false;

        var albumArtPathToDelete = song.AlbumArtUriFromTrack;
        _context.Songs.Remove(song);

        try {
            await _context.SaveChangesAsync();
            if (!string.IsNullOrWhiteSpace(albumArtPathToDelete) && _fileSystem.FileExists(albumArtPathToDelete)) {
                try {
                    _fileSystem.DeleteFile(albumArtPathToDelete);
                }
                catch (Exception fileEx) {
                    Debug.WriteLine($"[LibraryService:RemoveSongAsync] Warning: Failed to delete art file '{albumArtPathToDelete}': {fileEx.Message}");
                }
            }
            return true;
        }
        catch (DbUpdateException ex) {
            Debug.WriteLine($"[LibraryService:RemoveSongAsync] ERROR: Database update failed for ID '{songId}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            _context.Entry(song).State = EntityState.Unchanged;
            return false;
        }
    }

    /// <summary>
    /// Retrieves a song by its ID, including related artist, album, and folder data.
    /// </summary>
    /// <param name="songId">The ID of the song.</param>
    /// <returns>The fully loaded Song entity, or null if not found.</returns>
    public async Task<Song?> GetSongByIdAsync(Guid songId) {
        return await _context.Songs
            .Include(s => s.Artist)
            .Include(s => s.Album).ThenInclude(a => a!.Artist)
            .Include(s => s.Folder)
            .AsSplitQuery()
            .FirstOrDefaultAsync(s => s.Id == songId);
    }

    /// <summary>
    /// Retrieves a song by its file path, including related artist, album, and folder data.
    /// </summary>
    /// <param name="filePath">The file path of the song.</param>
    /// <returns>The fully loaded Song entity, or null if not found.</returns>
    public async Task<Song?> GetSongByFilePathAsync(string filePath) {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        return await _context.Songs
            .Include(s => s.Artist)
            .Include(s => s.Album).ThenInclude(a => a!.Artist)
            .Include(s => s.Folder)
            .AsSplitQuery()
            .FirstOrDefaultAsync(s => s.FilePath == filePath);
    }

    /// <summary>
    /// Retrieves a batch of songs by their IDs.
    /// </summary>
    /// <param name="songIds">An enumerable of song IDs to retrieve.</param>
    /// <returns>A dictionary mapping song IDs to the corresponding Song objects.</returns>
    public async Task<IReadOnlyDictionary<Guid, Song>> GetSongsByIdsAsync(IEnumerable<Guid> songIds) {
        if (songIds == null || !songIds.Any()) return new Dictionary<Guid, Song>();

        var uniqueIds = songIds.Distinct().ToList();
        var songs = await _context.Songs
            .Where(s => uniqueIds.Contains(s.Id))
            .Include(s => s.Artist)
            .Include(s => s.Album).ThenInclude(a => a!.Artist)
            .AsSplitQuery()
            .ToListAsync();
        return songs.ToDictionary(s => s.Id);
    }

    /// <summary>
    /// Updates the properties of an existing song.
    /// </summary>
    /// <param name="songToUpdate">The song entity with updated values.</param>
    /// <returns>True if the update was successful, otherwise false.</returns>
    public async Task<bool> UpdateSongAsync(Song songToUpdate) {
        if (songToUpdate == null) throw new ArgumentNullException(nameof(songToUpdate));
        _context.Songs.Update(songToUpdate);
        try {
            await _context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex) {
            Debug.WriteLine($"[LibraryService:UpdateSongAsync] ERROR: Database update failed for ID '{songToUpdate.Id}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            await _context.Entry(songToUpdate).ReloadAsync();
            return false;
        }
    }

    /// <summary>
    /// Retrieves all songs from the library, with a specified sort order.
    /// </summary>
    /// <param name="sortOrder">The criteria to use for sorting the songs.</param>
    /// <returns>An enumerable collection of all Song entities, sorted as requested.</returns>
    public async Task<IEnumerable<Song>> GetAllSongsAsync(SongSortOrder sortOrder = SongSortOrder.TitleAsc) {
        IQueryable<Song> query = _context.Songs
            .Include(s => s.Artist)
            .Include(s => s.Album).ThenInclude(a => a!.Artist);

        var sortedQuery = sortOrder switch {
            SongSortOrder.TitleDesc => query.OrderByDescending(s => s.Title).ThenBy(s => s.Id),
            SongSortOrder.DateAddedDesc => query.OrderByDescending(s => s.DateAddedToLibrary).ThenBy(s => s.Title),
            SongSortOrder.DateAddedAsc => query.OrderBy(s => s.DateAddedToLibrary).ThenBy(s => s.Title),
            SongSortOrder.AlbumAsc or SongSortOrder.TrackNumberAsc => query
                .OrderBy(s => s.Album != null ? s.Album.Title : string.Empty).ThenBy(s => s.TrackNumber)
                .ThenBy(s => s.Title),
            SongSortOrder.ArtistAsc => query.OrderBy(s => s.Artist != null ? s.Artist.Name : string.Empty)
                .ThenBy(s => s.Album != null ? s.Album.Title : string.Empty).ThenBy(s => s.TrackNumber),
            _ => query.OrderBy(s => s.Title).ThenBy(s => s.Id)
        };

        return await sortedQuery.AsSplitQuery().ToListAsync();
    }

    /// <summary>
    /// Retrieves all songs belonging to a specific album.
    /// </summary>
    /// <param name="albumId">The ID of the album.</param>
    /// <returns>An enumerable collection of songs from the specified album.</returns>
    public async Task<IEnumerable<Song>> GetSongsByAlbumIdAsync(Guid albumId) {
        return await _context.Songs.Where(s => s.AlbumId == albumId)
            .Include(s => s.Artist)
            .OrderBy(s => s.TrackNumber).ThenBy(s => s.Title)
            .AsSplitQuery()
            .ToListAsync();
    }

    /// <summary>
    /// Retrieves all songs by a specific artist.
    /// </summary>
    /// <param name="artistId">The ID of the artist.</param>
    /// <returns>An enumerable collection of songs by the specified artist.</returns>
    public async Task<IEnumerable<Song>> GetSongsByArtistIdAsync(Guid artistId) {
        return await _context.Songs.Where(s => s.ArtistId == artistId)
            .Include(s => s.Album).ThenInclude(alb => alb!.Artist)
            .Include(s => s.Artist)
            .OrderBy(s => s.Album != null ? s.Album.Title : string.Empty).ThenBy(s => s.TrackNumber)
            .AsSplitQuery()
            .ToListAsync();
    }

    /// <summary>
    /// Retrieves all songs located in a specific folder.
    /// </summary>
    /// <param name="folderId">The ID of the folder.</param>
    /// <returns>An enumerable collection of songs from the specified folder.</returns>
    public async Task<IEnumerable<Song>> GetSongsByFolderIdAsync(Guid folderId) {
        return await _context.Songs.Where(s => s.FolderId == folderId)
            .Include(s => s.Artist)
            .Include(s => s.Album).ThenInclude(alb => alb!.Artist)
            .OrderBy(s => s.Album != null ? s.Album.Title : string.Empty).ThenBy(s => s.TrackNumber)
            .ThenBy(s => s.Title)
            .AsSplitQuery()
            .ToListAsync();
    }

    /// <summary>
    /// Searches for songs matching a given term in their title, album, or artist names.
    /// </summary>
    /// <param name="searchTerm">The term to search for.</param>
    /// <returns>An enumerable collection of matching songs.</returns>
    public async Task<IEnumerable<Song>> SearchSongsAsync(string searchTerm) {
        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetAllSongsAsync();
        return await BuildSongSearchQuery(searchTerm)
            .OrderBy(s => s.Title).ThenBy(s => s.Id)
            .AsSplitQuery()
            .ToListAsync();
    }

    private IQueryable<Song> BuildSongSearchQuery(string searchTerm) {
        var term = $"%{searchTerm}%";
        return _context.Songs
            .Include(s => s.Artist).Include(s => s.Album).ThenInclude(a => a!.Artist)
            .Where(s => EF.Functions.Like(s.Title, term) ||
                        (s.Album != null && EF.Functions.Like(s.Album.Title, term)) ||
                        (s.Artist != null && EF.Functions.Like(s.Artist.Name, term)) ||
                        (s.Album != null && s.Album.Artist != null &&
                         EF.Functions.Like(s.Album.Artist.Name, term)));
    }

    #endregion

    #region Artist Management

    /// <summary>
    /// Retrieves an artist by ID, including their albums and songs.
    /// </summary>
    /// <param name="artistId">The ID of the artist to retrieve.</param>
    /// <returns>The fully loaded Artist entity, or null if not found.</returns>
    public async Task<Artist?> GetArtistByIdAsync(Guid artistId) {
        return await _context.Artists
            .Include(a => a.Albums)
            .ThenInclude(album => album.Songs)
            .Include(a => a.Songs)
            .ThenInclude(song => song.Album)
            .AsSplitQuery()
            .FirstOrDefaultAsync(a => a.Id == artistId);
    }

    /// <summary>
    /// Retrieves artist details. If metadata (biography, image) is not available locally,
    /// it fetches it from a remote service (Last.fm) and caches it.
    /// </summary>
    /// <param name="artistId">The ID of the artist.</param>
    /// <returns>The Artist entity with details, or null if the artist is not found.</returns>
    public async Task<Artist?> GetOrFetchArtistDetailsAsync(Guid artistId) {
        var artist = await _context.Artists
            .Include(a => a.Albums)
            .ThenInclude(album => album.Songs)
            .AsSplitQuery()
            .FirstOrDefaultAsync(a => a.Id == artistId);

        if (artist == null) return null;

        // Return immediately if metadata has already been fetched and cached.
        if (artist.Biography != null) return artist;

        await FetchAndUpdateArtistFromRemoteAsync(artist);

        return artist;
    }

    /// <summary>
    /// Initiates a background task to fetch missing metadata for all artists in the library.
    /// This method returns immediately, allowing the caller to continue without waiting.
    /// </summary>
    /// <returns>A completed task.</returns>
    public Task StartArtistMetadataBackgroundFetchAsync() {
        lock (_metadataFetchLock) {
            if (_isMetadataFetchRunning) {
                Debug.WriteLine("[LibraryService:StartArtistMetadataBackgroundFetchAsync] Background metadata fetch is already running. Skipping new request.");
                return Task.CompletedTask;
            }
            _isMetadataFetchRunning = true;
        }

        Debug.WriteLine("[LibraryService:StartArtistMetadataBackgroundFetchAsync] Starting background artist metadata fetch...");

        _ = Task.Run(async () => {
            try {
                // Find all artists where we haven't checked for a biography yet (Biography is null).
                var artistsToUpdate = await _context.Artists
                    .Where(a => a.Biography == null)
                    .OrderBy(a => a.Name)
                    .Select(a => a.Id)
                    .ToListAsync();

                if (!artistsToUpdate.Any()) {
                    Debug.WriteLine("[LibraryService:StartArtistMetadataBackgroundFetchAsync] No artists found requiring a metadata fetch.");
                    return;
                }

                Debug.WriteLine($"[LibraryService:StartArtistMetadataBackgroundFetchAsync] Found {artistsToUpdate.Count} artists to process in the background.");

                foreach (var artistId in artistsToUpdate) {
                    // Use the main method to fetch and cache details for each artist.
                    await GetOrFetchArtistDetailsAsync(artistId);
                }
                Debug.WriteLine("[LibraryService:StartArtistMetadataBackgroundFetchAsync] Background metadata fetch completed successfully.");
            }
            catch (Exception ex) {
                Debug.WriteLine($"[LibraryService:StartArtistMetadataBackgroundFetchAsync] An error occurred during the background metadata fetch: {ex.Message}");
            }
            finally {
                lock (_metadataFetchLock) {
                    _isMetadataFetchRunning = false;
                }
            }
        });

        return Task.CompletedTask;
    }

    private async Task FetchAndUpdateArtistFromRemoteAsync(Artist artist) {
        Debug.WriteLine($"[LibraryService:FetchAndUpdateArtistFromRemoteAsync] Fetching remote metadata for artist '{artist.Name}'.");

        var artistInfo = await _lastFmService.GetArtistInfoAsync(artist.Name);
        if (artistInfo == null) {
            Debug.WriteLine($"[LibraryService:FetchAndUpdateArtistFromRemoteAsync] No remote info found for artist '{artist.Name}'. Caching empty result.");
            // Set to empty string to indicate that a check was performed and nothing was found.
            artist.Biography = string.Empty;
        }
        else {
            artist.Biography = artistInfo.Biography;
            artist.RemoteImageUrl = artistInfo.ImageUrl;

            if (!string.IsNullOrWhiteSpace(artistInfo.ImageUrl)) {
                await DownloadAndCacheArtistImageAsync(artist, new Uri(artistInfo.ImageUrl));
            }
        }

        _context.Artists.Update(artist);
        await _context.SaveChangesAsync();
    }

    private async Task DownloadAndCacheArtistImageAsync(Artist artist, Uri imageUrl) {
        try {
            var localPath = GetArtistImageCachePath(artist.Id);
            if (_fileSystem.FileExists(localPath)) return;

            using var response = await _httpClient.GetAsync(imageUrl);
            response.EnsureSuccessStatusCode();

            var imageBytes = await response.Content.ReadAsByteArrayAsync();
            await _fileSystem.WriteAllBytesAsync(localPath, imageBytes);

            artist.LocalImageCachePath = localPath;
            ArtistMetadataUpdated?.Invoke(this, new ArtistMetadataUpdatedEventArgs(artist.Id, artist.LocalImageCachePath));
        }
        catch (Exception ex) {
            Debug.WriteLine($"[LibraryService:DownloadAndCacheArtistImageAsync] Failed to download image for artist '{artist.Name}'. URL: {imageUrl}. Error: {ex.Message}");
        }
    }

    private string GetArtistImageCachePath(Guid artistId) {
        var baseCachePath = _fileSystem.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Nagi",
            ArtistImageCacheFolderName);

        _fileSystem.CreateDirectory(baseCachePath);
        return _fileSystem.Combine(baseCachePath, $"{artistId}.jpg");
    }

    /// <summary>
    /// Retrieves an artist by their name.
    /// </summary>
    /// <param name="name">The name of the artist.</param>
    /// <returns>The Artist entity, or null if not found.</returns>
    public async Task<Artist?> GetArtistByNameAsync(string name) {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return await _context.Artists.FirstOrDefaultAsync(a => a.Name == name.Trim());
    }

    /// <summary>
    /// Retrieves an artist by name, or creates a new one if not found.
    /// </summary>
    /// <param name="name">The name of the artist.</param>
    /// <param name="saveImmediate">If true, saves the new artist to the database immediately.</param>
    /// <returns>The existing or newly created Artist entity.</returns>
    public async Task<Artist> GetOrCreateArtistAsync(string name, bool saveImmediate = false) {
        var normalizedName = string.IsNullOrWhiteSpace(name) ? UnknownArtistName : name.Trim();

        var artist =
            _context.Artists.Local.FirstOrDefault(a => a.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase))
            ?? await _context.Artists.FirstOrDefaultAsync(a => a.Name == normalizedName);

        if (artist == null) {
            artist = new Artist { Name = normalizedName };
            _context.Artists.Add(artist);
            if (saveImmediate) {
                try {
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateException ex) {
                    Debug.WriteLine($"[LibraryService:GetOrCreateArtistAsync] ERROR: Failed to save '{normalizedName}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
                    _context.Entry(artist).State = EntityState.Detached;
                    throw;
                }
            }
        }

        return artist;
    }

    /// <summary>
    /// Retrieves all artists from the library, ordered by name.
    /// </summary>
    /// <returns>An enumerable collection of all Artist entities.</returns>
    public async Task<IEnumerable<Artist>> GetAllArtistsAsync() {
        return await _context.Artists.OrderBy(a => a.Name).ToListAsync();
    }

    /// <summary>
    /// Searches for artists matching a given term in their name.
    /// </summary>
    /// <param name="searchTerm">The term to search for.</param>
    /// <returns>An enumerable collection of matching artists.</returns>
    public async Task<IEnumerable<Artist>> SearchArtistsAsync(string searchTerm) {
        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetAllArtistsAsync();
        return await BuildArtistSearchQuery(searchTerm).OrderBy(a => a.Name).ToListAsync();
    }

    private IQueryable<Artist> BuildArtistSearchQuery(string searchTerm) {
        return _context.Artists.Where(a => EF.Functions.Like(a.Name, $"%{searchTerm}%"));
    }

    #endregion

    #region Album Management

    /// <summary>
    /// Retrieves an album by ID, including its artist and sorted songs.
    /// </summary>
    /// <param name="albumId">The ID of the album to retrieve.</param>
    /// <returns>The fully loaded Album entity, or null if not found.</returns>
    public async Task<Album?> GetAlbumByIdAsync(Guid albumId) {
        return await _context.Albums
            .Include(al => al.Artist)
            .Include(al => al.Songs.OrderBy(s => s.TrackNumber).ThenBy(s => s.Title)).ThenInclude(s => s!.Artist)
            .AsSplitQuery()
            .FirstOrDefaultAsync(al => al.Id == albumId);
    }

    /// <summary>
    /// Retrieves an album by title and artist name, or creates a new one if not found.
    /// </summary>
    /// <param name="title">The title of the album.</param>
    /// <param name="albumArtistName">The name of the album's artist.</param>
    /// <param name="year">The release year of the album.</param>
    /// <param name="saveImmediate">If true, saves the new album to the database immediately.</param>
    /// <returns>The existing or newly created Album entity.</returns>
    public async Task<Album> GetOrCreateAlbumAsync(string title, string albumArtistName, int? year, bool saveImmediate = false) {
        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? UnknownAlbumName : title.Trim();
        var artistForAlbum = await GetOrCreateArtistAsync(albumArtistName);

        var album = _context.Albums.Local.FirstOrDefault(a =>
                        a.Title.Equals(normalizedTitle, StringComparison.OrdinalIgnoreCase) &&
                        a.ArtistId == artistForAlbum.Id)
                    ?? await _context.Albums.FirstOrDefaultAsync(a =>
                        a.Title == normalizedTitle && a.ArtistId == artistForAlbum.Id);

        if (album == null) {
            album = new Album { Title = normalizedTitle, Year = year, ArtistId = artistForAlbum.Id };
            _context.Albums.Add(album);
        }
        else if (year.HasValue && album.Year == null) {
            album.Year = year;
            _context.Albums.Update(album);
        }

        if (saveImmediate) {
            try {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex) {
                Debug.WriteLine($"[LibraryService:GetOrCreateAlbumAsync] ERROR: Failed to save '{normalizedTitle}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
                _context.Entry(album).State = EntityState.Detached;
                throw;
            }
        }
        return album;
    }

    /// <summary>
    /// Retrieves all albums from the library, ordered by title.
    /// </summary>
    /// <returns>An enumerable collection of all Album entities.</returns>
    public async Task<IEnumerable<Album>> GetAllAlbumsAsync() {
        return await _context.Albums
            .Include(al => al.Artist)
            .OrderBy(al => al.Title)
            .AsSplitQuery()
            .ToListAsync();
    }

    /// <summary>
    /// Searches for albums matching a given term in their title or artist name.
    /// </summary>
    /// <param name="searchTerm">The term to search for.</param>
    /// <returns>An enumerable collection of matching albums.</returns>
    public async Task<IEnumerable<Album>> SearchAlbumsAsync(string searchTerm) {
        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetAllAlbumsAsync();
        return await BuildAlbumSearchQuery(searchTerm)
            .OrderBy(al => al.Title)
            .AsSplitQuery()
            .ToListAsync();
    }

    private IQueryable<Album> BuildAlbumSearchQuery(string searchTerm) {
        var term = $"%{searchTerm}%";
        return _context.Albums
            .Include(al => al.Artist)
            .Where(al => EF.Functions.Like(al.Title, term) ||
                         (al.Artist != null && EF.Functions.Like(al.Artist.Name, term)));
    }

    #endregion

    #region Playlist Management

    /// <summary>
    /// Creates a new, empty playlist.
    /// </summary>
    /// <param name="name">The name of the playlist.</param>
    /// <param name="description">An optional description for the playlist.</param>
    /// <param name="coverImageUri">An optional URI for the playlist's cover image.</param>
    /// <returns>The newly created Playlist entity, or null on failure.</returns>
    public async Task<Playlist?> CreatePlaylistAsync(string name, string? description = null, string? coverImageUri = null) {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Playlist name cannot be empty.", nameof(name));

        var playlist = new Playlist {
            Name = name.Trim(),
            Description = description,
            CoverImageUri = coverImageUri,
            DateCreated = DateTime.UtcNow,
            DateModified = DateTime.UtcNow
        };
        _context.Playlists.Add(playlist);
        try {
            await _context.SaveChangesAsync();
            return playlist;
        }
        catch (DbUpdateException ex) {
            Debug.WriteLine($"[LibraryService:CreatePlaylistAsync] ERROR: Database update failed for name '{name}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            _context.Entry(playlist).State = EntityState.Detached;
            return null;
        }
    }

    /// <summary>
    /// Deletes a playlist and its song associations.
    /// </summary>
    /// <param name="playlistId">The ID of the playlist to delete.</param>
    /// <returns>True if the playlist was successfully deleted, otherwise false.</returns>
    public async Task<bool> DeletePlaylistAsync(Guid playlistId) {
        var playlist = await _context.Playlists.FindAsync(playlistId);
        if (playlist == null) return false;

        // Eagerly load the associations so they are tracked for deletion.
        await _context.Entry(playlist)
            .Collection(p => p.PlaylistSongs)
            .LoadAsync();

        _context.Playlists.Remove(playlist);

        try {
            await _context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex) {
            Debug.WriteLine($"[LibraryService:DeletePlaylistAsync] ERROR: Database update failed for ID '{playlistId}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            _context.Entry(playlist).State = EntityState.Unchanged;
            return false;
        }
    }

    /// <summary>
    /// Renames an existing playlist.
    /// </summary>
    /// <param name="playlistId">The ID of the playlist to rename.</param>
    /// <param name="newName">The new name for the playlist.</param>
    /// <returns>True if the rename was successful, otherwise false.</returns>
    public async Task<bool> RenamePlaylistAsync(Guid playlistId, string newName) {
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("New playlist name cannot be empty.", nameof(newName));

        var playlist = await _context.Playlists.FindAsync(playlistId);
        if (playlist == null) return false;

        playlist.Name = newName.Trim();
        playlist.DateModified = DateTime.UtcNow;
        try {
            await _context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex) {
            Debug.WriteLine($"[LibraryService:RenamePlaylistAsync] ERROR: Database update failed for ID '{playlist.Id}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            await _context.Entry(playlist).ReloadAsync();
            return false;
        }
    }

    /// <summary>
    /// Updates the cover image URI for a playlist.
    /// </summary>
    /// <param name="playlistId">The ID of the playlist to update.</param>
    /// <param name="newCoverImageUri">The new cover image URI. Can be null.</param>
    /// <returns>True if the update was successful, otherwise false.</returns>
    public async Task<bool> UpdatePlaylistCoverAsync(Guid playlistId, string? newCoverImageUri) {
        var playlist = await _context.Playlists.FindAsync(playlistId);
        if (playlist == null) return false;

        playlist.CoverImageUri = newCoverImageUri;
        playlist.DateModified = DateTime.UtcNow;
        try {
            await _context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex) {
            Debug.WriteLine($"[LibraryService:UpdatePlaylistCoverAsync] ERROR: Database update failed for ID '{playlist.Id}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            await _context.Entry(playlist).ReloadAsync();
            return false;
        }
    }

    /// <summary>
    /// Adds a collection of songs to a playlist.
    /// </summary>
    /// <param name="playlistId">The ID of the playlist.</param>
    /// <param name="songIds">The IDs of the songs to add.</param>
    /// <returns>True if any songs were successfully added, otherwise false.</returns>
    public async Task<bool> AddSongsToPlaylistAsync(Guid playlistId, IEnumerable<Guid> songIds) {
        if (songIds == null || !songIds.Any()) return false;

        var playlist = await _context.Playlists.FindAsync(playlistId);
        if (playlist == null) return false;

        var existingPlaylistSongIds = await _context.PlaylistSongs
            .Where(ps => ps.PlaylistId == playlistId)
            .Select(ps => ps.SongId)
            .ToHashSetAsync();

        var songIdsToAdd = songIds.Distinct().Except(existingPlaylistSongIds).ToList();
        if (!songIdsToAdd.Any()) return true;

        var validSongIds = await _context.Songs
            .Where(s => songIdsToAdd.Contains(s.Id))
            .Select(s => s.Id)
            .ToHashSetAsync();

        if (!validSongIds.Any()) return false;

        var nextOrder = (await _context.PlaylistSongs.Where(ps => ps.PlaylistId == playlistId).MaxAsync(ps => (int?)ps.Order) ?? -1) + 1;

        var playlistSongsToAdd = validSongIds.Select(songId => new PlaylistSong {
            PlaylistId = playlistId,
            SongId = songId,
            Order = nextOrder++
        });

        _context.PlaylistSongs.AddRange(playlistSongsToAdd);
        playlist.DateModified = DateTime.UtcNow;

        try {
            await _context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex) {
            Debug.WriteLine($"[LibraryService:AddSongsToPlaylistAsync] ERROR: Database update failed for playlist '{playlistId}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            foreach (var entry in _context.ChangeTracker.Entries().Where(e => e.State == EntityState.Added || e.State == EntityState.Modified)) {
                entry.State = EntityState.Detached;
            }
            return false;
        }
    }

    /// <summary>
    /// Removes a collection of songs from a playlist.
    /// </summary>
    /// <param name="playlistId">The ID of the playlist.</param>
    /// <param name="songIds">The IDs of the songs to remove.</param>
    /// <returns>True if any songs were successfully removed, otherwise false.</returns>
    public async Task<bool> RemoveSongsFromPlaylistAsync(Guid playlistId, IEnumerable<Guid> songIds) {
        if (songIds == null || !songIds.Any()) return false;

        var playlistSongsToRemove = await _context.PlaylistSongs
            .Where(ps => ps.PlaylistId == playlistId && songIds.Contains(ps.SongId))
            .ToListAsync();

        if (!playlistSongsToRemove.Any()) return false;

        _context.PlaylistSongs.RemoveRange(playlistSongsToRemove);
        var playlist = await _context.Playlists.FindAsync(playlistId);
        if (playlist != null) playlist.DateModified = DateTime.UtcNow;

        try {
            await _context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex) {
            Debug.WriteLine($"[LibraryService:RemoveSongsFromPlaylistAsync] ERROR: Database update failed for playlist '{playlistId}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            foreach (var entry in _context.ChangeTracker.Entries().Where(e => e.State == EntityState.Deleted || e.State == EntityState.Modified)) {
                entry.State = EntityState.Unchanged;
            }
            return false;
        }
    }

    /// <summary>
    /// Changes the position of a song within a playlist.
    /// </summary>
    /// <param name="playlistId">The ID of the playlist.</param>
    /// <param name="songId">The ID of the song to reorder.</param>
    /// <param name="newOrder">The new zero-based position for the song.</param>
    /// <returns>True if the reorder was successful, otherwise false.</returns>
    public async Task<bool> ReorderSongInPlaylistAsync(Guid playlistId, Guid songId, int newOrder) {
        var playlistSong = await _context.PlaylistSongs.FirstOrDefaultAsync(ps => ps.PlaylistId == playlistId && ps.SongId == songId);
        if (playlistSong == null) return false;

        playlistSong.Order = newOrder;
        var playlist = await _context.Playlists.FindAsync(playlistId);
        if (playlist != null) playlist.DateModified = DateTime.UtcNow;

        try {
            await _context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex) {
            Debug.WriteLine($"[LibraryService:ReorderSongInPlaylistAsync] ERROR: Database update failed for playlist '{playlistId}', song '{songId}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            await _context.Entry(playlistSong).ReloadAsync();
            if (playlist != null) await _context.Entry(playlist).ReloadAsync();
            return false;
        }
    }

    /// <summary>
    /// Retrieves a playlist by ID, including its songs in order.
    /// </summary>
    /// <param name="playlistId">The ID of the playlist.</param>
    /// <returns>The fully loaded Playlist entity, or null if not found.</returns>
    public async Task<Playlist?> GetPlaylistByIdAsync(Guid playlistId) {
        return await _context.Playlists
            .Include(p => p.PlaylistSongs.OrderBy(ps => ps.Order)).ThenInclude(ps => ps.Song)
            .ThenInclude(s => s!.Artist)
            .Include(p => p.PlaylistSongs.OrderBy(ps => ps.Order)).ThenInclude(ps => ps.Song)
            .ThenInclude(s => s!.Album).ThenInclude(a => a!.Artist)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == playlistId);
    }

    /// <summary>
    /// Retrieves all playlists from the library.
    /// </summary>
    /// <returns>An enumerable collection of all Playlist entities.</returns>
    public async Task<IEnumerable<Playlist>> GetAllPlaylistsAsync() {
        return await _context.Playlists
            .Include(p => p.PlaylistSongs)
            .OrderBy(p => p.Name).ThenBy(p => p.Id)
            .ToListAsync();
    }

    /// <summary>
    /// Retrieves all songs in a playlist, in their specified order.
    /// </summary>
    /// <param name="playlistId">The ID of the playlist.</param>
    /// <returns>An ordered enumerable collection of songs in the playlist.</returns>
    public async Task<IEnumerable<Song>> GetSongsInPlaylistOrderedAsync(Guid playlistId) {
        var playlist = await GetPlaylistByIdAsync(playlistId);
        if (playlist == null) return Enumerable.Empty<Song>();
        return playlist.PlaylistSongs.Select(ps => ps.Song).Where(s => s != null).ToList()!;
    }

    #endregion

    #region Scan Management

    /// <summary>
    /// A private context class to hold state and caches during a library scan operation.
    /// This improves performance by reducing database queries for existing artists and albums.
    /// </summary>
    private class ScanContext {
        public ScanContext(Guid folderId, IProgress<ScanProgress>? progress) {
            FolderId = folderId;
            Progress = progress;
        }

        public Guid FolderId { get; }
        public IProgress<ScanProgress>? Progress { get; }
        public Dictionary<string, Artist> ArtistCache { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Album> AlbumCache { get; } = new(StringComparer.OrdinalIgnoreCase);

        public async Task PopulateCachesAsync(MusicDbContext dbContext) {
            var artists = await dbContext.Artists.AsNoTracking().ToListAsync();
            foreach (var artist in artists) ArtistCache[artist.Name] = artist;

            var albums = await dbContext.Albums.AsNoTracking().Include(a => a.Artist).ToListAsync();
            foreach (var album in albums) {
                if (album.Artist != null) {
                    // Cache key combines artist name and album title for uniqueness.
                    AlbumCache[$"{album.Artist.Name}|{album.Title}"] = album;
                }
            }
            Debug.WriteLine($"[LibraryService:ScanContext] Pre-cached {ArtistCache.Count} artists and {AlbumCache.Count} albums.");
        }
    }

    /// <summary>
    /// Scans a folder for new music files and adds them to the library.
    /// </summary>
    /// <param name="folderPath">The path of the folder to scan.</param>
    /// <param name="progress">An optional progress reporter for UI updates.</param>
    public async Task ScanFolderForMusicAsync(string folderPath, IProgress<ScanProgress>? progress = null) {
        if (string.IsNullOrWhiteSpace(folderPath) || !_fileSystem.DirectoryExists(folderPath)) {
            progress?.Report(new ScanProgress { StatusText = "Invalid folder path.", Percentage = 100 });
            return;
        }

        var folder = await GetOrCreateFolderForScanAsync(folderPath);
        if (folder == null) {
            progress?.Report(new ScanProgress { StatusText = "Failed to register folder in database.", Percentage = 100 });
            return;
        }

        var scanContext = new ScanContext(folder.Id, progress);

        List<string> allFiles;
        try {
            allFiles = _fileSystem.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
                .Where(file => MusicFileExtensions.Contains(_fileSystem.GetExtension(file)))
                .ToList();
        }
        catch (Exception ex) {
            progress?.Report(new ScanProgress { StatusText = $"Error enumerating files: {ex.Message}", Percentage = 100 });
            Debug.WriteLine($"[LibraryService:ScanFolderForMusicAsync] ERROR: Failed to enumerate files in '{folderPath}'. Reason: {ex.Message}");
            return;
        }

        var totalFiles = allFiles.Count;
        if (totalFiles == 0) {
            progress?.Report(new ScanProgress { StatusText = "No music files found.", Percentage = 100 });
            return;
        }

        progress?.Report(new ScanProgress { TotalFiles = totalFiles, StatusText = $"Found {totalFiles} music files. Preparing scan..." });

        // Disable change tracking for performance during bulk operations.
        _context.ChangeTracker.AutoDetectChangesEnabled = false;

        Debug.WriteLine("[LibraryService:ScanFolderForMusicAsync] Pre-fetching existing song file paths and caches...");
        var existingFilePathsInDb = (await _context.Songs.Select(s => s.FilePath).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        await scanContext.PopulateCachesAsync(_context);
        Debug.WriteLine($"[LibraryService:ScanFolderForMusicAsync] Loaded {existingFilePathsInDb.Count} existing file paths.");

        var filesToProcess = allFiles.Where(f => !existingFilePathsInDb.Contains(f)).ToList();
        var filesToProcessCount = filesToProcess.Count;
        var filesScannedCount = totalFiles - filesToProcessCount;
        var newSongsAdded = 0;

        progress?.Report(new ScanProgress {
            FilesProcessed = filesScannedCount,
            TotalFiles = totalFiles,
            StatusText = $"Found {filesToProcessCount} new files to process."
        });

        for (var i = 0; i < filesToProcessCount; i += _parallelExtractionBatchSize) {
            var fileChunk = filesToProcess.Skip(i).Take(_parallelExtractionBatchSize);

            // Process metadata extraction in parallel for a chunk of files.
            var metadataTasks = fileChunk.Select(filePath => _metadataExtractor.ExtractMetadataAsync(filePath!));
            var allMetadata = await Task.WhenAll(metadataTasks);

            foreach (var metadata in allMetadata) {
                filesScannedCount++;
                var percentage = (double)filesScannedCount / totalFiles * 100.0;

                if (metadata.ExtractionFailed) {
                    progress?.Report(new ScanProgress {
                        FilesProcessed = filesScannedCount,
                        TotalFiles = totalFiles,
                        CurrentFilePath = metadata.FilePath,
                        StatusText = $"Error: {_fileSystem.GetFileNameWithoutExtension(metadata.FilePath)}",
                        Percentage = percentage
                    });
                    continue;
                }

                progress?.Report(new ScanProgress {
                    FilesProcessed = filesScannedCount,
                    TotalFiles = totalFiles,
                    CurrentFilePath = metadata.FilePath,
                    StatusText = $"Adding: {_fileSystem.GetFileNameWithoutExtension(metadata.FilePath)}",
                    Percentage = percentage
                });

                if (AddSongFromMetadata(metadata, scanContext)) newSongsAdded++;

                // Save changes in batches to manage memory usage.
                if (_context.ChangeTracker.Entries().Count(e => e.State == EntityState.Added) >= ScanBatchSize) {
                    await SaveChangesAndClearTrackerAsync("[Scan]");
                }
            }
        }

        await SaveChangesAndClearTrackerAsync("[Scan Final]");
        _context.ChangeTracker.AutoDetectChangesEnabled = true;

        progress?.Report(new ScanProgress {
            FilesProcessed = totalFiles,
            TotalFiles = totalFiles,
            StatusText = $"Scan complete. Added {newSongsAdded} new songs.",
            Percentage = 100.0
        });
        Debug.WriteLine($"[LibraryService:ScanFolderForMusicAsync] Scan complete for folder: '{folderPath}'. Added {newSongsAdded} new songs.");
    }

    /// <summary>
    /// Rescans a folder, adding new files and removing entries for deleted files.
    /// </summary>
    /// <param name="folderId">The ID of the folder to rescan.</param>
    /// <param name="progress">An optional progress reporter for UI updates.</param>
    /// <returns>True if any changes were made to the library, otherwise false.</returns>
    public async Task<bool> RescanFolderForMusicAsync(Guid folderId, IProgress<ScanProgress>? progress = null) {
        var folder = await _context.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.Id == folderId);
        if (folder == null) {
            progress?.Report(new ScanProgress { StatusText = "Folder not found.", Percentage = 100 });
            return false;
        }

        if (!_fileSystem.DirectoryExists(folder.Path)) {
            progress?.Report(new ScanProgress { StatusText = "Folder path no longer exists. Removing from library.", Percentage = 100 });
            return await RemoveFolderAsync(folderId);
        }

        Debug.WriteLine($"[LibraryService:RescanFolderForMusicAsync] Starting for folder '{folder.Path}'");
        progress?.Report(new ScanProgress { StatusText = "Scanning for file changes..." });

        _context.ChangeTracker.AutoDetectChangesEnabled = false;
        var changesMadeCount = 0;

        try {
            var filesOnDisk = _fileSystem.EnumerateFiles(folder.Path, "*.*", SearchOption.AllDirectories)
                .Where(file => MusicFileExtensions.Contains(_fileSystem.GetExtension(file)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var filePathsInDb = (await _context.Songs
                    .Where(s => s.FolderId == folder.Id)
                    .Select(s => s.FilePath)
                    .ToListAsync())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var pathsToRemove = filePathsInDb.Except(filesOnDisk).ToList();
            if (pathsToRemove.Any()) {
                Debug.WriteLine($"[LibraryService:RescanFolderForMusicAsync] Found {pathsToRemove.Count} songs to remove.");
                progress?.Report(new ScanProgress { StatusText = $"Removing {pathsToRemove.Count} deleted songs..." });

                var songsToRemove = await _context.Songs
                    .Where(s => s.FolderId == folder.Id && pathsToRemove.Contains(s.FilePath))
                    .ToListAsync();

                _context.Songs.RemoveRange(songsToRemove);
                changesMadeCount += songsToRemove.Count;

                foreach (var artPath in songsToRemove.Select(s => s.AlbumArtUriFromTrack).Where(p => !string.IsNullOrEmpty(p)).Distinct()) {
                    if (_fileSystem.FileExists(artPath!)) {
                        try {
                            _fileSystem.DeleteFile(artPath!);
                        }
                        catch (Exception ex) {
                            Debug.WriteLine($"[LibraryService:RescanFolderForMusicAsync] Warning: Failed to delete orphaned art '{artPath}': {ex.Message}");
                        }
                    }
                }
            }

            var filesToAdd = filesOnDisk.Except(filePathsInDb).ToList();
            if (filesToAdd.Any()) {
                Debug.WriteLine($"[LibraryService:RescanFolderForMusicAsync] Found {filesToAdd.Count} new files to add.");
                progress?.Report(new ScanProgress { StatusText = $"Adding {filesToAdd.Count} new songs..." });

                var scanContext = new ScanContext(folder.Id, progress);
                await scanContext.PopulateCachesAsync(_context);

                for (var i = 0; i < filesToAdd.Count; i += _parallelExtractionBatchSize) {
                    var fileChunk = filesToAdd.Skip(i).Take(_parallelExtractionBatchSize);
                    var metadataTasks = fileChunk.Select(filePath => _metadataExtractor.ExtractMetadataAsync(filePath!));
                    var allMetadata = await Task.WhenAll(metadataTasks);

                    foreach (var metadata in allMetadata.Where(m => !m.ExtractionFailed)) {
                        if (AddSongFromMetadata(metadata, scanContext)) changesMadeCount++;
                        if (_context.ChangeTracker.Entries().Count(e => e.State != EntityState.Unchanged) >= ScanBatchSize) {
                            await SaveChangesAndClearTrackerAsync("[Rescan Batch Save]");
                        }
                    }
                }
            }

            try {
                var updatedFolder = await _context.Folders.FindAsync(folder.Id);
                if (updatedFolder != null) {
                    updatedFolder.LastModifiedDate = _fileSystem.GetLastWriteTimeUtc(folder.Path);
                    _context.Folders.Update(updatedFolder);
                }
            }
            catch (Exception ex) {
                Debug.WriteLine($"[LibraryService:RescanFolderForMusicAsync] Warning: Could not update LastWriteTimeUtc for folder '{folder.Path}': {ex.Message}");
            }

            if (_context.ChangeTracker.HasChanges()) await SaveChangesAndClearTrackerAsync("[Rescan Final]");

            progress?.Report(new ScanProgress {
                StatusText = $"Rescan complete. Added {filesToAdd.Count}, removed {pathsToRemove.Count}.",
                Percentage = 100.0
            });
            Debug.WriteLine($"[LibraryService:RescanFolderForMusicAsync] Rescan complete for folder: '{folder.Path}'. Total changes: {changesMadeCount}.");
            return changesMadeCount > 0;
        }
        catch (Exception ex) {
            progress?.Report(new ScanProgress { StatusText = $"Error during rescan: {ex.Message}", Percentage = 100 });
            Debug.WriteLine($"[LibraryService:RescanFolderForMusicAsync] ERROR: Unhandled error for '{folder.Path}'. Reason: {ex.Message}");
            return false;
        }
        finally {
            _context.ChangeTracker.AutoDetectChangesEnabled = true;
        }
    }

    /// <summary>
    /// Triggers a rescan for all folders currently in the library.
    /// </summary>
    /// <param name="progress">An optional progress reporter for UI updates.</param>
    /// <returns>True if any changes were made to the library, otherwise false.</returns>
    public async Task<bool> RefreshAllFoldersAsync(IProgress<ScanProgress>? progress = null) {
        Debug.WriteLine("[LibraryService:RefreshAllFoldersAsync] Starting refresh for all library folders.");
        progress?.Report(new ScanProgress { StatusText = "Preparing to refresh all folders...", Percentage = 0 });

        var folderIds = await _context.Folders.AsNoTracking().Select(f => f.Id).ToListAsync();
        var totalFolders = folderIds.Count;

        if (totalFolders == 0) {
            progress?.Report(new ScanProgress { StatusText = "No folders in the library to refresh.", Percentage = 100 });
            Debug.WriteLine("[LibraryService:RefreshAllFoldersAsync] No folders found to refresh.");
            return false;
        }

        Debug.WriteLine($"[LibraryService:RefreshAllFoldersAsync] Found {totalFolders} folders to refresh.");
        var foldersProcessed = 0;
        var anyChangesMade = false;

        foreach (var folderId in folderIds) {
            var progressWrapper = new Progress<ScanProgress>(scanProgress => {
                if (progress != null) {
                    var overallPercentage = (foldersProcessed + scanProgress.Percentage / 100.0) / totalFolders * 100.0;
                    var overallProgress = new ScanProgress {
                        StatusText = $"({foldersProcessed + 1}/{totalFolders}) {scanProgress.StatusText}",
                        CurrentFilePath = scanProgress.CurrentFilePath,
                        Percentage = overallPercentage,
                        FilesProcessed = scanProgress.FilesProcessed,
                        TotalFiles = scanProgress.TotalFiles
                    };
                    progress.Report(overallProgress);
                }
            });

            var folderPath = (await _context.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.Id == folderId))?.Path ?? "ID not found";
            Debug.WriteLine($"[LibraryService:RefreshAllFoldersAsync] Refreshing folder {foldersProcessed + 1}/{totalFolders}: '{folderPath}'");

            var result = await RescanFolderForMusicAsync(folderId, progressWrapper);
            if (result) anyChangesMade = true;

            foldersProcessed++;
        }

        var finalMessage = anyChangesMade
            ? "Refresh complete. Changes were detected and applied."
            : "Refresh complete. No changes found in library folders.";

        progress?.Report(new ScanProgress { StatusText = finalMessage, Percentage = 100 });
        Debug.WriteLine($"[LibraryService:RefreshAllFoldersAsync] Refresh all folders finished. Any changes: {anyChangesMade}.");
        return anyChangesMade;
    }

    private bool AddSongFromMetadata(SongFileMetadata metadata, ScanContext scanContext) {
        try {
            var trackArtist = GetOrCreateArtistInScan(metadata.Artist, scanContext);
            var albumArtistName = string.IsNullOrWhiteSpace(metadata.AlbumArtist) ? metadata.Artist : metadata.AlbumArtist;
            var album = GetOrCreateAlbumInScan(metadata.Album, albumArtistName, metadata.Year, scanContext);

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

            _context.Songs.Add(song);
            return true;
        }
        catch (Exception ex) {
            Debug.WriteLine($"[LibraryService:AddSongFromMetadata] ERROR: Failed to prepare song '{metadata.FilePath}' for addition. Reason: {ex.Message}");
            return false;
        }
    }

    private Artist GetOrCreateArtistInScan(string name, ScanContext scanContext) {
        var normalizedName = string.IsNullOrWhiteSpace(name) ? UnknownArtistName : name.Trim();

        if (scanContext.ArtistCache.TryGetValue(normalizedName, out var artist)) return artist;

        var newArtist = new Artist { Name = normalizedName };
        _context.Artists.Add(newArtist);
        scanContext.ArtistCache[normalizedName] = newArtist;
        return newArtist;
    }

    private Album? GetOrCreateAlbumInScan(string? title, string albumArtistName, int? year, ScanContext scanContext) {
        if (string.IsNullOrWhiteSpace(title)) return null;

        var normalizedTitle = title.Trim();
        var artistForAlbum = GetOrCreateArtistInScan(albumArtistName, scanContext);
        var cacheKey = $"{artistForAlbum.Name}|{normalizedTitle}";

        if (scanContext.AlbumCache.TryGetValue(cacheKey, out var album)) {
            if (year.HasValue && album.Year == null) {
                album.Year = year;
                _context.Albums.Update(album);
            }
            return album;
        }

        var newAlbum = new Album {
            Title = normalizedTitle,
            Year = year,
            ArtistId = artistForAlbum.Id
        };
        _context.Albums.Add(newAlbum);
        scanContext.AlbumCache[cacheKey] = newAlbum;
        return newAlbum;
    }

    private async Task SaveChangesAndClearTrackerAsync(string operationTag) {
        var changedEntries = _context.ChangeTracker.Entries().Count(e => e.State != EntityState.Unchanged && e.State != EntityState.Detached);
        if (changedEntries == 0) return;

        try {
            await _context.SaveChangesAsync();
            Debug.WriteLine($"[LibraryService:{operationTag}] Batch saved {changedEntries} entities.");
        }
        catch (DbUpdateException ex) {
            Debug.WriteLine($"[LibraryService:{operationTag}] ERROR: Failed to save batch. Reason: {ex.InnerException?.Message ?? ex.Message}");
        }
        finally {
            // Clear the tracker to free memory and prevent reprocessing the same entities.
            _context.ChangeTracker.Clear();
        }
    }

    private async Task<Folder?> GetOrCreateFolderForScanAsync(string folderPath) {
        var folder = await _context.Folders.AsTracking().FirstOrDefaultAsync(f => f.Path == folderPath);
        DateTime? fileSystemLastModified = null;
        try {
            fileSystemLastModified = _fileSystem.GetLastWriteTimeUtc(folderPath);
        }
        catch (Exception ex) {
            Debug.WriteLine($"[LibraryService:GetOrCreateFolderForScanAsync] Warning: Could not get LastWriteTimeUtc for folder '{folderPath}': {ex.Message}");
        }

        if (folder == null) {
            folder = new Folder {
                Path = folderPath,
                Name = _fileSystem.GetDirectoryNameFromPath(folderPath),
                LastModifiedDate = fileSystemLastModified
            };
            _context.Folders.Add(folder);
            try {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex) {
                Debug.WriteLine($"[LibraryService:GetOrCreateFolderForScanAsync] ERROR: Failed to save new folder '{folderPath}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
                _context.Entry(folder).State = EntityState.Detached;
                return null;
            }
        }
        else if (folder.LastModifiedDate != fileSystemLastModified) {
            folder.LastModifiedDate = fileSystemLastModified;
            _context.Folders.Update(folder);
        }

        return folder;
    }

    #endregion

    #region Data Reset

    /// <summary>
    /// WARNING: Deletes all data from the library, including all folders, songs, artists,
    /// albums, and playlists. Also clears associated cached images from the file system.
    /// This operation is irreversible.
    /// </summary>
    public async Task ClearAllLibraryDataAsync() {
        Debug.WriteLine("[LibraryService:ClearAllLibraryDataAsync] Initiating full library data clearance.");
        try {
            await ClearAllTablesAsync();

            var baseLocalAppDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var albumArtStoragePath = _fileSystem.Combine(baseLocalAppDataPath, "Nagi", AlbumArtCacheFolderName);
            var artistImageStoragePath = _fileSystem.Combine(baseLocalAppDataPath, "Nagi", ArtistImageCacheFolderName);

            if (_fileSystem.DirectoryExists(albumArtStoragePath)) _fileSystem.DeleteDirectory(albumArtStoragePath, true);
            _fileSystem.CreateDirectory(albumArtStoragePath);

            if (_fileSystem.DirectoryExists(artistImageStoragePath)) _fileSystem.DeleteDirectory(artistImageStoragePath, true);
            _fileSystem.CreateDirectory(artistImageStoragePath);

            Debug.WriteLine("[LibraryService:ClearAllLibraryDataAsync] All library data and cached images cleared successfully.");
        }
        catch (Exception ex) {
            Debug.WriteLine($"[LibraryService:ClearAllLibraryDataAsync] FATAL: Failed to clear all library data. Reason: {ex.Message}");
            throw;
        }
    }

    private async Task ClearAllTablesAsync() {
        Debug.WriteLine("[LibraryService:ClearAllTablesAsync] Clearing data from all tables using bulk delete operations...");
        try {
            _context.ChangeTracker.AutoDetectChangesEnabled = false;

            // Delete in order of dependency to avoid foreign key constraint violations.
            await _context.PlaylistSongs.ExecuteDeleteAsync();
            await _context.Playlists.ExecuteDeleteAsync();
            await _context.Songs.ExecuteDeleteAsync();
            await _context.Albums.ExecuteDeleteAsync();
            await _context.Artists.ExecuteDeleteAsync();
            await _context.Folders.ExecuteDeleteAsync();
        }
        finally {
            _context.ChangeTracker.AutoDetectChangesEnabled = true;
            _context.ChangeTracker.Clear();
        }
    }

    #endregion
}