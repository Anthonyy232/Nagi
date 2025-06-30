// Nagi/Services/Abstractions/ILibraryService.cs

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nagi.Models;
using Nagi.Services.Implementations;

namespace Nagi.Services.Abstractions;

/// <summary>
///     Defines the contract for a service that manages the music library.
///     This includes handling folders, songs, artists, albums, playlists, and library scanning operations.
/// </summary>
public interface ILibraryService
{
    /// <summary>
    ///     Occurs when an artist's metadata (e.g., image) has been updated.
    /// </summary>
    event EventHandler<ArtistMetadataUpdatedEventArgs> ArtistMetadataUpdated;

    // --- Folder Management ---

    Task<Folder?> AddFolderAsync(string path, string? name = null);
    Task<bool> RemoveFolderAsync(Guid folderId);
    Task<Folder?> GetFolderByIdAsync(Guid folderId);
    Task<Folder?> GetFolderByPathAsync(string path);
    Task<IEnumerable<Folder>> GetAllFoldersAsync();
    Task<bool> UpdateFolderAsync(Folder folder);
    Task<int> GetSongCountForFolderAsync(Guid folderId);

    // --- Library Scanning & Refreshing ---

    Task ScanFolderForMusicAsync(string folderPath, IProgress<ScanProgress>? progress = null);
    Task<bool> RescanFolderForMusicAsync(Guid folderId, IProgress<ScanProgress>? progress = null);
    Task<bool> RefreshAllFoldersAsync(IProgress<ScanProgress>? progress = null);

    // --- Song Management ---

    Task<Song?> AddSongAsync(Song songData);

    Task<Song?> AddSongWithDetailsAsync(
        Guid folderId,
        string filePath, string title, string trackArtistName, string? albumTitle, string? albumArtistName,
        TimeSpan duration, string? songSpecificCoverArtUri, string? lightSwatchId, string? darkSwatchId,
        int? releaseYear = null, IEnumerable<string>? genres = null,
        int? trackNumber = null, int? discNumber = null, int? sampleRate = null, int? bitrate = null,
        int? channels = null,
        DateTime? fileCreatedDate = null, DateTime? fileModifiedDate = null);

    Task<bool> RemoveSongAsync(Guid songId);
    Task<Song?> GetSongByIdAsync(Guid songId);
    Task<Song?> GetSongByFilePathAsync(string filePath);
    Task<IEnumerable<Song>> GetAllSongsAsync(SongSortOrder sortOrder = SongSortOrder.TitleAsc);
    Task<IReadOnlyDictionary<Guid, Song>> GetSongsByIdsAsync(IEnumerable<Guid> songIds);
    Task<IEnumerable<Song>> GetSongsByAlbumIdAsync(Guid albumId);
    Task<IEnumerable<Song>> GetSongsByArtistIdAsync(Guid artistId);
    Task<IEnumerable<Song>> GetSongsByFolderIdAsync(Guid folderId);
    Task<IEnumerable<Song>> SearchSongsAsync(string searchTerm);
    Task<bool> UpdateSongAsync(Song songToUpdate);

    // --- Artist Management ---

    Task<Artist?> GetArtistByIdAsync(Guid artistId);
    Task<Artist?> GetArtistDetailsAsync(Guid artistId, bool allowOnlineFetch);
    Task<Artist?> GetArtistByNameAsync(string name);
    Task<IEnumerable<Artist>> GetAllArtistsAsync();
    Task<IEnumerable<Artist>> SearchArtistsAsync(string searchTerm);
    Task<Artist> GetOrCreateArtistAsync(string name, bool saveImmediate = false);
    Task StartArtistMetadataBackgroundFetchAsync();

    // --- Album Management ---

    Task<Album?> GetAlbumByIdAsync(Guid albumId);
    Task<IEnumerable<Album>> GetAllAlbumsAsync();
    Task<IEnumerable<Album>> SearchAlbumsAsync(string searchTerm);
    Task<Album> GetOrCreateAlbumAsync(string title, string albumArtistName, int? year, bool saveImmediate = false);

    // --- Playlist Management ---

    Task<Playlist?> CreatePlaylistAsync(string name, string? description = null, string? coverImageUri = null);
    Task<bool> DeletePlaylistAsync(Guid playlistId);
    Task<bool> RenamePlaylistAsync(Guid playlistId, string newName);
    Task<bool> UpdatePlaylistCoverAsync(Guid playlistId, string? newCoverImageUri);
    Task<bool> AddSongsToPlaylistAsync(Guid playlistId, IEnumerable<Guid> songIds);
    Task<bool> RemoveSongsFromPlaylistAsync(Guid playlistId, IEnumerable<Guid> songIds);

    /// <summary>
    ///     Updates the order of all songs in a playlist based on a provided sequence of song IDs.
    ///     This is the preferred method for reordering as it's a single, atomic, and safe transaction.
    /// </summary>
    Task<bool> UpdatePlaylistSongOrderAsync(Guid playlistId, IEnumerable<Guid> orderedSongIds);

    Task<Playlist?> GetPlaylistByIdAsync(Guid playlistId);
    Task<IEnumerable<Playlist>> GetAllPlaylistsAsync();
    Task<IEnumerable<Song>> GetSongsInPlaylistOrderedAsync(Guid playlistId);

    // --- Data Reset ---

    Task ClearAllLibraryDataAsync();
}