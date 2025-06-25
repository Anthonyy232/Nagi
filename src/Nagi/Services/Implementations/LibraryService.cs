using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Nagi.Data;
using Nagi.Models;
using Nagi.Services.Abstractions;

namespace Nagi.Services.Implementations;

/// <summary>
///     Implements the ILibraryService interface to provide data access and business logic
///     for the music library, interacting with the database and file system.
/// </summary>
public class LibraryService : ILibraryService
{
    private const int ScanBatchSize = 100;
    private const string UnknownArtistName = "Unknown Artist";
    private const string UnknownAlbumName = "Unknown Album";
    private static readonly string[] MusicFileExtensions = { ".mp3", ".flac", ".wav", ".ogg", ".m4a", ".wma", ".aac" };
    private readonly MusicDbContext _context;
    private readonly IFileSystemService _fileSystem;
    private readonly IMetadataExtractor _metadataExtractor;

    private readonly int _parallelExtractionBatchSize;

    public LibraryService(MusicDbContext context, IFileSystemService fileSystem, IMetadataExtractor metadataExtractor)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _metadataExtractor = metadataExtractor ?? throw new ArgumentNullException(nameof(metadataExtractor));
        _parallelExtractionBatchSize = Environment.ProcessorCount * 2;
    }

    #region Folder Management

    public async Task<Folder?> AddFolderAsync(string path, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var existingFolder = await _context.Folders.FirstOrDefaultAsync(f => f.Path == path);
        if (existingFolder != null) return existingFolder;

        var folder = new Folder { Path = path, Name = name ?? _fileSystem.GetDirectoryNameFromPath(path) };
        try
        {
            folder.LastModifiedDate = _fileSystem.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[LibraryService] Warning: Could not get LastWriteTimeUtc for folder '{path}': {ex.Message}");
            folder.LastModifiedDate = null;
        }

        _context.Folders.Add(folder);
        try
        {
            await _context.SaveChangesAsync();
            return folder;
        }
        catch (DbUpdateException ex)
        {
            Debug.WriteLine(
                $"[LibraryService] ERROR: AddFolderAsync failed for path '{path}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            _context.Entry(folder).State = EntityState.Detached;
            return null;
        }
    }

    public async Task<bool> RemoveFolderAsync(Guid folderId)
    {
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

        try
        {
            await _context.SaveChangesAsync();

            foreach (var artPath in albumArtPathsToDelete)
                if (_fileSystem.FileExists(artPath!))
                    try
                    {
                        _fileSystem.DeleteFile(artPath!);
                    }
                    catch (Exception fileEx)
                    {
                        Debug.WriteLine(
                            $"[LibraryService] Warning: Failed to delete art file '{artPath}': {fileEx.Message}");
                    }

            return true;
        }
        catch (DbUpdateException ex)
        {
            Debug.WriteLine(
                $"[LibraryService] ERROR: RemoveFolderAsync failed for ID '{folderId}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            _context.Entry(folder).State = EntityState.Unchanged;
            foreach (var song in songsInFolder) _context.Entry(song).State = EntityState.Unchanged;
            return false;
        }
    }

    public async Task<Folder?> GetFolderByIdAsync(Guid folderId)
    {
        return await _context.Folders.FirstOrDefaultAsync(f => f.Id == folderId);
    }

    public async Task<Folder?> GetFolderByPathAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return await _context.Folders.FirstOrDefaultAsync(f => f.Path == path);
    }

    public async Task<IEnumerable<Folder>> GetAllFoldersAsync()
    {
        return await _context.Folders.OrderBy(f => f.Name).ThenBy(f => f.Path).ToListAsync();
    }

    public async Task<bool> UpdateFolderAsync(Folder folder)
    {
        if (folder == null) throw new ArgumentNullException(nameof(folder));

        try
        {
            folder.LastModifiedDate = _fileSystem.GetLastWriteTimeUtc(folder.Path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[LibraryService] Warning: Could not get LastWriteTimeUtc for folder '{folder.Path}': {ex.Message}");
        }

        _context.Folders.Update(folder);
        try
        {
            await _context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex)
        {
            Debug.WriteLine(
                $"[LibraryService] ERROR: UpdateFolderAsync failed for ID '{folder.Id}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            await _context.Entry(folder).ReloadAsync();
            return false;
        }
    }

    public async Task<int> GetSongCountForFolderAsync(Guid folderId)
    {
        return await _context.Songs.CountAsync(s => s.FolderId == folderId);
    }

    #endregion

    #region Song Management

    public async Task<Song?> AddSongAsync(Song songData)
    {
        if (songData == null) throw new ArgumentNullException(nameof(songData));
        if (string.IsNullOrWhiteSpace(songData.FilePath))
            throw new ArgumentException("Song FilePath cannot be empty.", nameof(songData.FilePath));

        var existingSong = await _context.Songs.FirstOrDefaultAsync(s => s.FilePath == songData.FilePath);
        if (existingSong != null) return existingSong;

        _context.Songs.Add(songData);
        try
        {
            await _context.SaveChangesAsync();
            return songData;
        }
        catch (DbUpdateException ex)
        {
            Debug.WriteLine(
                $"[LibraryService] ERROR: AddSongAsync failed for path '{songData.FilePath}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            _context.Entry(songData).State = EntityState.Detached;
            return null;
        }
    }

    public async Task<Song?> AddSongWithDetailsAsync(
        Guid folderId,
        string filePath, string title, string trackArtistName, string? albumTitle, string? albumArtistName,
        TimeSpan duration, string? songSpecificCoverArtUri,
        string? lightSwatchId, string? darkSwatchId,
        int? releaseYear = null, IEnumerable<string>? genres = null,
        int? trackNumber = null, int? discNumber = null, int? sampleRate = null, int? bitrate = null,
        int? channels = null,
        DateTime? fileCreatedDate = null, DateTime? fileModifiedDate = null)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("FilePath is required.", nameof(filePath));

        var existingSong = await _context.Songs.FirstOrDefaultAsync(s => s.FilePath == filePath);
        if (existingSong != null) return existingSong;

        var song = new Song
        {
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

        if (!string.IsNullOrWhiteSpace(albumTitle))
        {
            var artistNameToUseForAlbum =
                string.IsNullOrWhiteSpace(albumArtistName) ? trackArtistName : albumArtistName;
            var album = await GetOrCreateAlbumAsync(albumTitle, artistNameToUseForAlbum, releaseYear);
            song.Album = album;
        }

        _context.Songs.Add(song);
        return song;
    }

    public async Task<bool> RemoveSongAsync(Guid songId)
    {
        var song = await _context.Songs.FindAsync(songId);
        if (song == null) return false;

        var albumArtPathToDelete = song.AlbumArtUriFromTrack;
        _context.Songs.Remove(song);

        try
        {
            await _context.SaveChangesAsync();
            if (!string.IsNullOrWhiteSpace(albumArtPathToDelete) && _fileSystem.FileExists(albumArtPathToDelete))
                try
                {
                    _fileSystem.DeleteFile(albumArtPathToDelete);
                }
                catch (Exception fileEx)
                {
                    Debug.WriteLine(
                        $"[LibraryService] Warning: Failed to delete art file '{albumArtPathToDelete}': {fileEx.Message}");
                }

            return true;
        }
        catch (DbUpdateException ex)
        {
            Debug.WriteLine(
                $"[LibraryService] ERROR: RemoveSongAsync failed for ID '{songId}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            _context.Entry(song).State = EntityState.Unchanged;
            return false;
        }
    }

    public async Task<Song?> GetSongByIdAsync(Guid songId)
    {
        return await _context.Songs
            .Include(s => s.Artist)
            .Include(s => s.Album).ThenInclude(a => a!.Artist)
            .Include(s => s.Folder)
            .AsSplitQuery()
            .FirstOrDefaultAsync(s => s.Id == songId);
    }

    public async Task<Song?> GetSongByFilePathAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        return await _context.Songs
            .Include(s => s.Artist)
            .Include(s => s.Album).ThenInclude(a => a!.Artist)
            .Include(s => s.Folder)
            .AsSplitQuery()
            .FirstOrDefaultAsync(s => s.FilePath == filePath);
    }

    public async Task<IReadOnlyDictionary<Guid, Song>> GetSongsByIdsAsync(IEnumerable<Guid> songIds)
    {
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

    public async Task<bool> UpdateSongAsync(Song songToUpdate)
    {
        if (songToUpdate == null) throw new ArgumentNullException(nameof(songToUpdate));
        _context.Songs.Update(songToUpdate);
        try
        {
            await _context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex)
        {
            Debug.WriteLine(
                $"[LibraryService] ERROR: UpdateSongAsync failed for ID '{songToUpdate.Id}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            await _context.Entry(songToUpdate).ReloadAsync();
            return false;
        }
    }

    public async Task<IEnumerable<Song>> GetAllSongsAsync(SongSortOrder sortOrder = SongSortOrder.TitleAsc)
    {
        IQueryable<Song> query = _context.Songs
            .Include(s => s.Artist)
            .Include(s => s.Album).ThenInclude(a => a!.Artist);

        var sortedQuery = sortOrder switch
        {
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

    public async Task<IEnumerable<Song>> GetSongsByAlbumIdAsync(Guid albumId)
    {
        return await _context.Songs.Where(s => s.AlbumId == albumId)
            .Include(s => s.Artist)
            .OrderBy(s => s.TrackNumber).ThenBy(s => s.Title)
            .AsSplitQuery()
            .ToListAsync();
    }

    public async Task<IEnumerable<Song>> GetSongsByArtistIdAsync(Guid artistId)
    {
        return await _context.Songs.Where(s => s.ArtistId == artistId)
            .Include(s => s.Album).ThenInclude(alb => alb!.Artist)
            .Include(s => s.Artist)
            .OrderBy(s => s.Album != null ? s.Album.Title : string.Empty).ThenBy(s => s.TrackNumber)
            .AsSplitQuery()
            .ToListAsync();
    }

    public async Task<IEnumerable<Song>> GetSongsByFolderIdAsync(Guid folderId)
    {
        return await _context.Songs.Where(s => s.FolderId == folderId)
            .Include(s => s.Artist)
            .Include(s => s.Album).ThenInclude(alb => alb!.Artist)
            .OrderBy(s => s.Album != null ? s.Album.Title : string.Empty).ThenBy(s => s.TrackNumber)
            .ThenBy(s => s.Title)
            .AsSplitQuery()
            .ToListAsync();
    }

    public async Task<IEnumerable<Song>> SearchSongsAsync(string searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetAllSongsAsync();
        return await BuildSongSearchQuery(searchTerm)
            .OrderBy(s => s.Title).ThenBy(s => s.Id)
            .AsSplitQuery()
            .ToListAsync();
    }

    private IQueryable<Song> BuildSongSearchQuery(string searchTerm)
    {
        return _context.Songs
            .Include(s => s.Artist).Include(s => s.Album).ThenInclude(a => a!.Artist)
            .Where(s => EF.Functions.Like(s.Title, $"%{searchTerm}%") ||
                        (s.Album != null && EF.Functions.Like(s.Album.Title, $"%{searchTerm}%")) ||
                        (s.Artist != null && EF.Functions.Like(s.Artist.Name, $"%{searchTerm}%")) ||
                        (s.Album != null && s.Album.Artist != null &&
                         EF.Functions.Like(s.Album.Artist.Name, $"%{searchTerm}%")));
    }

    #endregion

    #region Artist Management

    public async Task<Artist?> GetArtistByIdAsync(Guid artistId)
    {
        return await _context.Artists
            .Include(a => a.Albums)
            .ThenInclude(album => album.Songs)
            .Include(a => a.Songs)
            .ThenInclude(song => song.Album)
            .AsSplitQuery()
            .FirstOrDefaultAsync(a => a.Id == artistId);
    }

    public async Task<Artist?> GetArtistByNameAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return await _context.Artists.FirstOrDefaultAsync(a => a.Name == name.Trim());
    }

    public async Task<Artist> GetOrCreateArtistAsync(string name, bool saveImmediate = false)
    {
        var normalizedName = string.IsNullOrWhiteSpace(name) ? UnknownArtistName : name.Trim();

        var artist =
            _context.Artists.Local.FirstOrDefault(a =>
                a.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase))
            ?? await _context.Artists.FirstOrDefaultAsync(a => a.Name == normalizedName);

        if (artist == null)
        {
            artist = new Artist { Name = normalizedName };
            _context.Artists.Add(artist);
            if (saveImmediate)
                try
                {
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateException ex)
                {
                    Debug.WriteLine(
                        $"[LibraryService] ERROR: GetOrCreateArtistAsync failed to save '{normalizedName}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
                    _context.Entry(artist).State = EntityState.Detached;
                    throw;
                }
        }

        return artist;
    }

    public async Task<IEnumerable<Artist>> GetAllArtistsAsync()
    {
        return await _context.Artists.OrderBy(a => a.Name).ToListAsync();
    }

    public async Task<IEnumerable<Artist>> SearchArtistsAsync(string searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetAllArtistsAsync();
        return await BuildArtistSearchQuery(searchTerm).OrderBy(a => a.Name).ToListAsync();
    }

    private IQueryable<Artist> BuildArtistSearchQuery(string searchTerm)
    {
        return _context.Artists.Where(a => EF.Functions.Like(a.Name, $"%{searchTerm}%"));
    }

    #endregion

    #region Album Management

    public async Task<Album?> GetAlbumByIdAsync(Guid albumId)
    {
        return await _context.Albums
            .Include(al => al.Artist)
            .Include(al => al.Songs.OrderBy(s => s.TrackNumber).ThenBy(s => s.Title)).ThenInclude(s => s!.Artist)
            .AsSplitQuery()
            .FirstOrDefaultAsync(al => al.Id == albumId);
    }

    public async Task<Album> GetOrCreateAlbumAsync(string title, string albumArtistName, int? year,
        bool saveImmediate = false)
    {
        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? UnknownAlbumName : title.Trim();
        var artistForAlbum = await GetOrCreateArtistAsync(albumArtistName);

        var album = _context.Albums.Local.FirstOrDefault(a =>
                        a.Title.Equals(normalizedTitle, StringComparison.OrdinalIgnoreCase) &&
                        a.ArtistId == artistForAlbum.Id)
                    ?? await _context.Albums.FirstOrDefaultAsync(a =>
                        a.Title == normalizedTitle && a.ArtistId == artistForAlbum.Id);

        if (album == null)
        {
            album = new Album { Title = normalizedTitle, Year = year, ArtistId = artistForAlbum.Id };
            _context.Albums.Add(album);
        }
        else if (year.HasValue && album.Year == null)
        {
            album.Year = year;
            _context.Albums.Update(album);
        }

        if (saveImmediate)
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex)
            {
                Debug.WriteLine(
                    $"[LibraryService] ERROR: GetOrCreateAlbumAsync failed to save '{normalizedTitle}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
                _context.Entry(album).State = EntityState.Detached;
                throw;
            }

        return album;
    }

    public async Task<IEnumerable<Album>> GetAllAlbumsAsync()
    {
        return await _context.Albums
            .Include(al => al.Artist)
            .OrderBy(al => al.Title)
            .AsSplitQuery()
            .ToListAsync();
    }

    public async Task<IEnumerable<Album>> SearchAlbumsAsync(string searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetAllAlbumsAsync();
        return await BuildAlbumSearchQuery(searchTerm)
            .OrderBy(al => al.Title)
            .AsSplitQuery()
            .ToListAsync();
    }

    private IQueryable<Album> BuildAlbumSearchQuery(string searchTerm)
    {
        return _context.Albums
            .Include(al => al.Artist)
            .Where(al => EF.Functions.Like(al.Title, $"%{searchTerm}%") ||
                         (al.Artist != null && EF.Functions.Like(al.Artist.Name, $"%{searchTerm}%")));
    }

    #endregion

    #region Playlist Management

    public async Task<Playlist?> CreatePlaylistAsync(string name, string? description = null,
        string? coverImageUri = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Playlist name cannot be empty.", nameof(name));

        var playlist = new Playlist
        {
            Name = name.Trim(),
            Description = description,
            CoverImageUri = coverImageUri,
            DateCreated = DateTime.UtcNow,
            DateModified = DateTime.UtcNow
        };
        _context.Playlists.Add(playlist);
        try
        {
            await _context.SaveChangesAsync();
            return playlist;
        }
        catch (DbUpdateException ex)
        {
            Debug.WriteLine(
                $"[LibraryService] ERROR: CreatePlaylistAsync failed for name '{name}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            _context.Entry(playlist).State = EntityState.Detached;
            return null;
        }
    }

    public async Task<bool> DeletePlaylistAsync(Guid playlistId)
    {
        var playlist = await _context.Playlists.FindAsync(playlistId);
        if (playlist == null)
            // The playlist doesn't exist in the database, so there's nothing to delete.
            return false;

        await _context.Entry(playlist)
            .Collection(p => p.PlaylistSongs)
            .LoadAsync();

        _context.Playlists.Remove(playlist);

        try
        {
            await _context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex)
        {
            Debug.WriteLine(
                $"[LibraryService] ERROR: DeletePlaylistAsync failed for ID '{playlistId}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            _context.Entry(playlist).State = EntityState.Unchanged;
            return false;
        }
    }

    public async Task<bool> RenamePlaylistAsync(Guid playlistId, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("New playlist name cannot be empty.", nameof(newName));

        var playlist = await _context.Playlists.FindAsync(playlistId);
        if (playlist == null) return false;

        playlist.Name = newName.Trim();
        playlist.DateModified = DateTime.UtcNow;
        try
        {
            await _context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex)
        {
            Debug.WriteLine(
                $"[LibraryService] ERROR: RenamePlaylistAsync failed for ID '{playlist.Id}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            await _context.Entry(playlist).ReloadAsync();
            return false;
        }
    }

    public async Task<bool> UpdatePlaylistCoverAsync(Guid playlistId, string? newCoverImageUri)
    {
        var playlist = await _context.Playlists.FindAsync(playlistId);
        if (playlist == null) return false;

        playlist.CoverImageUri = newCoverImageUri;
        playlist.DateModified = DateTime.UtcNow;
        try
        {
            await _context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex)
        {
            Debug.WriteLine(
                $"[LibraryService] ERROR: UpdatePlaylistCoverAsync failed for ID '{playlist.Id}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            await _context.Entry(playlist).ReloadAsync();
            return false;
        }
    }

    public async Task<bool> AddSongsToPlaylistAsync(Guid playlistId, IEnumerable<Guid> songIds)
    {
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

        var nextOrder =
            (await _context.PlaylistSongs.Where(ps => ps.PlaylistId == playlistId).MaxAsync(ps => (int?)ps.Order) ??
             -1) + 1;

        var playlistSongsToAdd = validSongIds.Select(songId => new PlaylistSong
        {
            PlaylistId = playlistId,
            SongId = songId,
            Order = nextOrder++
        });

        _context.PlaylistSongs.AddRange(playlistSongsToAdd);
        playlist.DateModified = DateTime.UtcNow;

        try
        {
            await _context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex)
        {
            Debug.WriteLine(
                $"[LibraryService] ERROR: AddSongsToPlaylistAsync failed for playlist '{playlistId}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            foreach (var entry in _context.ChangeTracker.Entries()
                         .Where(e => e.State == EntityState.Added || e.State == EntityState.Modified))
                entry.State = EntityState.Detached;
            return false;
        }
    }

    public async Task<bool> RemoveSongsFromPlaylistAsync(Guid playlistId, IEnumerable<Guid> songIds)
    {
        if (songIds == null || !songIds.Any()) return false;

        var playlistSongsToRemove = await _context.PlaylistSongs
            .Where(ps => ps.PlaylistId == playlistId && songIds.Contains(ps.SongId))
            .ToListAsync();

        if (!playlistSongsToRemove.Any()) return false;

        _context.PlaylistSongs.RemoveRange(playlistSongsToRemove);
        var playlist = await _context.Playlists.FindAsync(playlistId);
        if (playlist != null) playlist.DateModified = DateTime.UtcNow;

        try
        {
            await _context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex)
        {
            Debug.WriteLine(
                $"[LibraryService] ERROR: RemoveSongsFromPlaylistAsync failed for playlist '{playlistId}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            foreach (var entry in _context.ChangeTracker.Entries()
                         .Where(e => e.State == EntityState.Deleted || e.State == EntityState.Modified))
                entry.State = EntityState.Unchanged;
            return false;
        }
    }

    public async Task<bool> ReorderSongInPlaylistAsync(Guid playlistId, Guid songId, int newOrder)
    {
        var playlistSong =
            await _context.PlaylistSongs.FirstOrDefaultAsync(ps => ps.PlaylistId == playlistId && ps.SongId == songId);
        if (playlistSong == null) return false;

        playlistSong.Order = newOrder;
        var playlist = await _context.Playlists.FindAsync(playlistId);
        if (playlist != null) playlist.DateModified = DateTime.UtcNow;

        try
        {
            await _context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex)
        {
            Debug.WriteLine(
                $"[LibraryService] ERROR: ReorderSongInPlaylistAsync failed for playlist '{playlistId}', song '{songId}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
            await _context.Entry(playlistSong).ReloadAsync();
            if (playlist != null) await _context.Entry(playlist).ReloadAsync();
            return false;
        }
    }

    public async Task<Playlist?> GetPlaylistByIdAsync(Guid playlistId)
    {
        return await _context.Playlists
            .Include(p => p.PlaylistSongs.OrderBy(ps => ps.Order)).ThenInclude(ps => ps.Song)
            .ThenInclude(s => s!.Artist)
            .Include(p => p.PlaylistSongs.OrderBy(ps => ps.Order)).ThenInclude(ps => ps.Song)
            .ThenInclude(s => s!.Album).ThenInclude(a => a!.Artist)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == playlistId);
    }

    public async Task<IEnumerable<Playlist>> GetAllPlaylistsAsync()
    {
        return await _context.Playlists
            .Include(p => p.PlaylistSongs)
            .OrderBy(p => p.Name).ThenBy(p => p.Id)
            .ToListAsync();
    }

    public async Task<IEnumerable<Song>> GetSongsInPlaylistOrderedAsync(Guid playlistId)
    {
        var playlist = await GetPlaylistByIdAsync(playlistId);
        if (playlist == null) return Enumerable.Empty<Song>();
        return playlist.PlaylistSongs.Select(ps => ps.Song).Where(s => s != null).ToList()!;
    }

    #endregion

    #region Scan Management

    private class ScanContext
    {
        public ScanContext(Guid folderId, IProgress<ScanProgress>? progress)
        {
            FolderId = folderId;
            Progress = progress;
        }

        public Guid FolderId { get; }
        public IProgress<ScanProgress>? Progress { get; }
        public Dictionary<string, Artist> ArtistCache { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Album> AlbumCache { get; } = new(StringComparer.OrdinalIgnoreCase);

        public async Task PopulateCachesAsync(MusicDbContext dbContext)
        {
            var artists = await dbContext.Artists.AsNoTracking().ToListAsync();
            foreach (var artist in artists) ArtistCache[artist.Name] = artist;

            var albums = await dbContext.Albums.AsNoTracking().Include(a => a.Artist).ToListAsync();
            foreach (var album in albums)
                if (album.Artist != null)
                    AlbumCache[$"{album.Artist.Name}|{album.Title}"] = album;

            Debug.WriteLine(
                $"[LibraryService] ScanContext: Pre-cached {ArtistCache.Count} artists and {AlbumCache.Count} albums.");
        }
    }

    public async Task ScanFolderForMusicAsync(string folderPath, IProgress<ScanProgress>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !_fileSystem.DirectoryExists(folderPath))
        {
            progress?.Report(new ScanProgress { StatusText = "Invalid folder path.", Percentage = 100 });
            return;
        }

        var folder = await GetOrCreateFolderForScanAsync(folderPath);
        if (folder == null)
        {
            progress?.Report(new ScanProgress
                { StatusText = "Failed to register folder in database.", Percentage = 100 });
            return;
        }

        var scanContext = new ScanContext(folder.Id, progress);

        List<string> allFiles;
        try
        {
            allFiles = _fileSystem.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
                .Where(file => MusicFileExtensions.Contains(_fileSystem.GetExtension(file)))
                .ToList();
        }
        catch (Exception ex)
        {
            progress?.Report(new ScanProgress
                { StatusText = $"Error enumerating files: {ex.Message}", Percentage = 100 });
            Debug.WriteLine(
                $"[LibraryService] ERROR: ScanFolderForMusicAsync failed to enumerate files in '{folderPath}'. Reason: {ex.Message}");
            return;
        }

        var totalFiles = allFiles.Count;
        if (totalFiles == 0)
        {
            progress?.Report(new ScanProgress { StatusText = "No music files found.", Percentage = 100 });
            return;
        }

        progress?.Report(new ScanProgress
            { TotalFiles = totalFiles, StatusText = $"Found {totalFiles} music files. Preparing scan..." });

        _context.ChangeTracker.AutoDetectChangesEnabled = false;

        Debug.WriteLine("[LibraryService] Scan: Pre-fetching existing song file paths and caches...");
        var existingFilePathsInDb =
            (await _context.Songs.Select(s => s.FilePath).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        await scanContext.PopulateCachesAsync(_context);
        Debug.WriteLine($"[LibraryService] Scan: Loaded {existingFilePathsInDb.Count} existing file paths.");

        var filesToProcess = allFiles.Where(f => !existingFilePathsInDb.Contains(f)).ToList();
        var filesToProcessCount = filesToProcess.Count;
        var filesScannedCount = totalFiles - filesToProcessCount;
        var newSongsAdded = 0;

        progress?.Report(new ScanProgress
        {
            FilesProcessed = filesScannedCount,
            TotalFiles = totalFiles,
            StatusText = $"Found {filesToProcessCount} new files to process."
        });

        for (var i = 0; i < filesToProcessCount; i += _parallelExtractionBatchSize)
        {
            var fileChunk = filesToProcess.Skip(i).Take(_parallelExtractionBatchSize);

            var metadataTasks = fileChunk.Select(filePath => _metadataExtractor.ExtractMetadataAsync(filePath!));
            var allMetadata = await Task.WhenAll(metadataTasks);

            foreach (var metadata in allMetadata)
            {
                filesScannedCount++;
                var percentage = (double)filesScannedCount / totalFiles * 100.0;

                if (metadata.ExtractionFailed)
                {
                    progress?.Report(new ScanProgress
                    {
                        FilesProcessed = filesScannedCount,
                        TotalFiles = totalFiles,
                        CurrentFilePath = metadata.FilePath,
                        StatusText = $"Error: {_fileSystem.GetFileNameWithoutExtension(metadata.FilePath)}",
                        Percentage = percentage
                    });
                    continue;
                }

                progress?.Report(new ScanProgress
                {
                    FilesProcessed = filesScannedCount,
                    TotalFiles = totalFiles,
                    CurrentFilePath = metadata.FilePath,
                    StatusText = $"Adding: {_fileSystem.GetFileNameWithoutExtension(metadata.FilePath)}",
                    Percentage = percentage
                });

                if (AddSongFromMetadata(metadata, scanContext)) newSongsAdded++;

                if (_context.ChangeTracker.Entries().Count(e => e.State == EntityState.Added) >= ScanBatchSize)
                    await SaveChangesAndClearTrackerAsync("[Scan]");
            }
        }

        await SaveChangesAndClearTrackerAsync("[Scan Final]");
        _context.ChangeTracker.AutoDetectChangesEnabled = true;

        progress?.Report(new ScanProgress
        {
            FilesProcessed = totalFiles,
            TotalFiles = totalFiles,
            StatusText = $"Scan complete. Added {newSongsAdded} new songs.",
            Percentage = 100.0
        });
        Debug.WriteLine($"[LibraryService] Scan complete for folder: '{folderPath}'. Added {newSongsAdded} new songs.");
    }

    public async Task<bool> RescanFolderForMusicAsync(Guid folderId, IProgress<ScanProgress>? progress = null)
    {
        var folder = await _context.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.Id == folderId);
        if (folder == null)
        {
            progress?.Report(new ScanProgress { StatusText = "Folder not found.", Percentage = 100 });
            return false;
        }

        if (!_fileSystem.DirectoryExists(folder.Path))
        {
            progress?.Report(new ScanProgress
                { StatusText = "Folder path no longer exists. Removing from library.", Percentage = 100 });
            return await RemoveFolderAsync(folderId);
        }

        Debug.WriteLine($"[LibraryService] Rescan: Starting for folder '{folder.Path}'");
        progress?.Report(new ScanProgress { StatusText = "Scanning for file changes..." });

        _context.ChangeTracker.AutoDetectChangesEnabled = false;
        var changesMadeCount = 0;

        try
        {
            var filesOnDisk = _fileSystem.EnumerateFiles(folder.Path, "*.*", SearchOption.AllDirectories)
                .Where(file => MusicFileExtensions.Contains(_fileSystem.GetExtension(file)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var filePathsInDb = (await _context.Songs
                    .Where(s => s.FolderId == folder.Id)
                    .Select(s => s.FilePath)
                    .ToListAsync())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var pathsToRemove = filePathsInDb.Except(filesOnDisk).ToList();
            if (pathsToRemove.Any())
            {
                Debug.WriteLine($"[LibraryService] Rescan: Found {pathsToRemove.Count} songs to remove.");
                progress?.Report(new ScanProgress { StatusText = $"Removing {pathsToRemove.Count} deleted songs..." });

                var songsToRemove = await _context.Songs
                    .Where(s => s.FolderId == folder.Id && pathsToRemove.Contains(s.FilePath))
                    .ToListAsync();

                _context.Songs.RemoveRange(songsToRemove);
                changesMadeCount += songsToRemove.Count;

                foreach (var artPath in songsToRemove.Select(s => s.AlbumArtUriFromTrack)
                             .Where(p => !string.IsNullOrEmpty(p)).Distinct())
                    if (_fileSystem.FileExists(artPath!))
                        try
                        {
                            _fileSystem.DeleteFile(artPath!);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine(
                                $"[LibraryService] Warning: Failed to delete orphaned art '{artPath}': {ex.Message}");
                        }
            }

            var filesToAdd = filesOnDisk.Except(filePathsInDb).ToList();
            if (filesToAdd.Any())
            {
                Debug.WriteLine($"[LibraryService] Rescan: Found {filesToAdd.Count} new files to add.");
                progress?.Report(new ScanProgress { StatusText = $"Adding {filesToAdd.Count} new songs..." });

                var scanContext = new ScanContext(folder.Id, progress);
                await scanContext.PopulateCachesAsync(_context);

                for (var i = 0; i < filesToAdd.Count; i += _parallelExtractionBatchSize)
                {
                    var fileChunk = filesToAdd.Skip(i).Take(_parallelExtractionBatchSize);
                    var metadataTasks =
                        fileChunk.Select(filePath => _metadataExtractor.ExtractMetadataAsync(filePath!));
                    var allMetadata = await Task.WhenAll(metadataTasks);

                    foreach (var metadata in allMetadata.Where(m => !m.ExtractionFailed))
                    {
                        if (AddSongFromMetadata(metadata, scanContext)) changesMadeCount++;
                        if (_context.ChangeTracker.Entries().Count(e => e.State != EntityState.Unchanged) >=
                            ScanBatchSize) await SaveChangesAndClearTrackerAsync("[Rescan Batch Save]");
                    }
                }
            }

            try
            {
                var updatedFolder = await _context.Folders.FindAsync(folder.Id);
                if (updatedFolder != null)
                {
                    updatedFolder.LastModifiedDate = _fileSystem.GetLastWriteTimeUtc(folder.Path);
                    _context.Folders.Update(updatedFolder);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[LibraryService] Warning: Could not update LastWriteTimeUtc for folder '{folder.Path}': {ex.Message}");
            }

            if (_context.ChangeTracker.HasChanges()) await SaveChangesAndClearTrackerAsync("[Rescan Final]");

            progress?.Report(new ScanProgress
            {
                StatusText = $"Rescan complete. Added {filesToAdd.Count}, removed {pathsToRemove.Count}.",
                Percentage = 100.0
            });
            Debug.WriteLine(
                $"[LibraryService] Rescan complete for folder: '{folder.Path}'. Total changes: {changesMadeCount}.");
            return changesMadeCount > 0;
        }
        catch (Exception ex)
        {
            progress?.Report(new ScanProgress { StatusText = $"Error during rescan: {ex.Message}", Percentage = 100 });
            Debug.WriteLine(
                $"[LibraryService] ERROR: RescanFolderForMusicAsync unhandled error for '{folder.Path}'. Reason: {ex.Message}");
            return false;
        }
        finally
        {
            _context.ChangeTracker.AutoDetectChangesEnabled = true;
        }
    }

    public async Task<bool> RefreshAllFoldersAsync(IProgress<ScanProgress>? progress = null)
    {
        Debug.WriteLine("[LibraryService] Starting refresh for all library folders.");
        progress?.Report(new ScanProgress { StatusText = "Preparing to refresh all folders...", Percentage = 0 });

        var folderIds = await _context.Folders.AsNoTracking().Select(f => f.Id).ToListAsync();
        var totalFolders = folderIds.Count;

        if (totalFolders == 0)
        {
            progress?.Report(
                new ScanProgress { StatusText = "No folders in the library to refresh.", Percentage = 100 });
            Debug.WriteLine("[LibraryService] No folders found to refresh.");
            return false;
        }

        Debug.WriteLine($"[LibraryService] Found {totalFolders} folders to refresh.");
        var foldersProcessed = 0;
        var anyChangesMade = false;

        foreach (var folderId in folderIds)
        {
            var progressWrapper = new Progress<ScanProgress>(scanProgress =>
            {
                if (progress != null)
                {
                    var overallPercentage = (foldersProcessed + scanProgress.Percentage / 100.0) / totalFolders * 100.0;
                    var overallProgress = new ScanProgress
                    {
                        StatusText = $"({foldersProcessed + 1}/{totalFolders}) {scanProgress.StatusText}",
                        CurrentFilePath = scanProgress.CurrentFilePath,
                        Percentage = overallPercentage,
                        FilesProcessed = scanProgress.FilesProcessed,
                        TotalFiles = scanProgress.TotalFiles
                    };
                    progress.Report(overallProgress);
                }
            });

            var folderPath = (await _context.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.Id == folderId))?.Path ??
                             "ID not found";
            Debug.WriteLine(
                $"[LibraryService] Refreshing folder {foldersProcessed + 1}/{totalFolders}: '{folderPath}'");

            var result = await RescanFolderForMusicAsync(folderId, progressWrapper);
            if (result) anyChangesMade = true;

            foldersProcessed++;
        }

        var finalMessage = anyChangesMade
            ? "Refresh complete. Changes were detected and applied."
            : "Refresh complete. No changes found in library folders.";

        progress?.Report(new ScanProgress { StatusText = finalMessage, Percentage = 100 });
        Debug.WriteLine($"[LibraryService] Refresh all folders finished. Any changes: {anyChangesMade}.");
        return anyChangesMade;
    }

    private bool AddSongFromMetadata(SongFileMetadata metadata, ScanContext scanContext)
    {
        try
        {
            var trackArtist = GetOrCreateArtist(metadata.Artist, scanContext);
            var albumArtistName =
                string.IsNullOrWhiteSpace(metadata.AlbumArtist) ? metadata.Artist : metadata.AlbumArtist;
            var album = GetOrCreateAlbum(metadata.Album, albumArtistName, metadata.Year, scanContext);

            var song = new Song
            {
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
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[LibraryService] ERROR: Failed to prepare song '{metadata.FilePath}' for addition. Reason: {ex.Message}");
            return false;
        }
    }

    private Artist GetOrCreateArtist(string name, ScanContext scanContext)
    {
        var normalizedName = string.IsNullOrWhiteSpace(name) ? UnknownArtistName : name.Trim();

        if (scanContext.ArtistCache.TryGetValue(normalizedName, out var artist)) return artist;

        var newArtist = new Artist { Name = normalizedName };
        _context.Artists.Add(newArtist);
        scanContext.ArtistCache[normalizedName] = newArtist;
        return newArtist;
    }

    private Album? GetOrCreateAlbum(string? title, string albumArtistName, int? year, ScanContext scanContext)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        var normalizedTitle = title.Trim();
        var artistForAlbum = GetOrCreateArtist(albumArtistName, scanContext);
        var cacheKey = $"{artistForAlbum.Name}|{normalizedTitle}";

        if (scanContext.AlbumCache.TryGetValue(cacheKey, out var album))
        {
            if (year.HasValue && album.Year == null)
            {
                album.Year = year;
                _context.Albums.Update(album);
            }

            return album;
        }

        var newAlbum = new Album
        {
            Title = normalizedTitle,
            Year = year,
            ArtistId = artistForAlbum.Id
        };
        _context.Albums.Add(newAlbum);
        scanContext.AlbumCache[cacheKey] = newAlbum;
        return newAlbum;
    }

    private async Task SaveChangesAndClearTrackerAsync(string operationTag)
    {
        var changedEntries = _context.ChangeTracker.Entries()
            .Count(e => e.State != EntityState.Unchanged && e.State != EntityState.Detached);
        if (changedEntries == 0) return;

        try
        {
            await _context.SaveChangesAsync();
            Debug.WriteLine($"[LibraryService] {operationTag}: Batch saved {changedEntries} entities.");
        }
        catch (DbUpdateException ex)
        {
            Debug.WriteLine(
                $"[LibraryService] {operationTag} ERROR: Failed to save batch. Reason: {ex.InnerException?.Message ?? ex.Message}");
        }
        finally
        {
            _context.ChangeTracker.Clear();
        }
    }

    private async Task<Folder?> GetOrCreateFolderForScanAsync(string folderPath)
    {
        var folder = await _context.Folders.AsTracking().FirstOrDefaultAsync(f => f.Path == folderPath);
        DateTime? fileSystemLastModified = null;
        try
        {
            fileSystemLastModified = _fileSystem.GetLastWriteTimeUtc(folderPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[LibraryService] Warning: Could not get LastWriteTimeUtc for folder '{folderPath}': {ex.Message}");
        }

        if (folder == null)
        {
            folder = new Folder
            {
                Path = folderPath,
                Name = _fileSystem.GetDirectoryNameFromPath(folderPath),
                LastModifiedDate = fileSystemLastModified
            };
            _context.Folders.Add(folder);
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex)
            {
                Debug.WriteLine(
                    $"[LibraryService] ERROR: GetOrCreateFolderForScanAsync failed to save new folder '{folderPath}'. Reason: {ex.InnerException?.Message ?? ex.Message}");
                _context.Entry(folder).State = EntityState.Detached;
                return null;
            }
        }
        else if (folder.LastModifiedDate != fileSystemLastModified)
        {
            folder.LastModifiedDate = fileSystemLastModified;
            _context.Folders.Update(folder);
        }

        return folder;
    }

    #endregion

    #region Data Reset

    public async Task ClearAllLibraryDataAsync()
    {
        Debug.WriteLine("[LibraryService] Initiating full library data clearance.");
        try
        {
            await ClearAllTablesAsync();

            var baseLocalAppDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var albumArtStoragePath = _fileSystem.Combine(baseLocalAppDataPath, "Nagi", "AlbumArt");

            if (_fileSystem.DirectoryExists(albumArtStoragePath))
                _fileSystem.DeleteDirectory(albumArtStoragePath, true);
            _fileSystem.CreateDirectory(albumArtStoragePath);

            Debug.WriteLine("[LibraryService] All library data and associated album art cleared successfully.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LibraryService] FATAL: Failed to clear all library data. Reason: {ex.Message}");
            throw;
        }
    }

    private async Task ClearAllTablesAsync()
    {
        Debug.WriteLine("[LibraryService] Clearing data from all tables using ExecuteDeleteAsync...");
        try
        {
            _context.ChangeTracker.AutoDetectChangesEnabled = false;

            await _context.PlaylistSongs.ExecuteDeleteAsync();
            await _context.Playlists.ExecuteDeleteAsync();
            await _context.Songs.ExecuteDeleteAsync();
            await _context.Albums.ExecuteDeleteAsync();
            await _context.Artists.ExecuteDeleteAsync();
            await _context.Folders.ExecuteDeleteAsync();
        }
        finally
        {
            _context.ChangeTracker.AutoDetectChangesEnabled = true;
            _context.ChangeTracker.Clear();
        }
    }

    #endregion
}