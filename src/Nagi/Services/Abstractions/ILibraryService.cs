using Nagi.Helpers;
using Nagi.Models;
using Nagi.Services.Implementations;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Nagi.Services.Abstractions;

/// <summary>
/// Defines the contract for a service that manages the music library,
/// including scanning, metadata manipulation, and data access.
/// </summary>
public interface ILibraryService {
    /// <summary>
    /// Occurs when an artist's metadata (e.g., biography or image) has been updated.
    /// </summary>
    event EventHandler<ArtistMetadataUpdatedEventArgs>? ArtistMetadataUpdated;

    #region Folder Management

    Task<Folder?> AddFolderAsync(string path, string? name = null);
    Task<bool> RemoveFolderAsync(Guid folderId);
    Task<Folder?> GetFolderByIdAsync(Guid folderId);
    Task<Folder?> GetFolderByPathAsync(string path);
    Task<IEnumerable<Folder>> GetAllFoldersAsync();
    Task<bool> UpdateFolderAsync(Folder folder);
    Task<int> GetSongCountForFolderAsync(Guid folderId);

    #endregion

    #region Library Scanning

    Task ScanFolderForMusicAsync(string folderPath, IProgress<ScanProgress>? progress = null);
    Task<bool> RescanFolderForMusicAsync(Guid folderId, IProgress<ScanProgress>? progress = null);
    Task<bool> RefreshAllFoldersAsync(IProgress<ScanProgress>? progress = null);

    #endregion

    #region Song Management

    Task<Song?> AddSongAsync(Song songData);
    Task<Song?> AddSongWithDetailsAsync(Guid folderId, SongFileMetadata metadata);
    Task<bool> RemoveSongAsync(Guid songId);
    Task<Song?> GetSongByIdAsync(Guid songId);
    Task<Song?> GetSongByFilePathAsync(string filePath);
    Task<IReadOnlyDictionary<Guid, Song>> GetSongsByIdsAsync(IEnumerable<Guid> songIds);
    Task<bool> UpdateSongAsync(Song songToUpdate);
    Task<IEnumerable<Song>> GetAllSongsAsync(SongSortOrder sortOrder = SongSortOrder.TitleAsc);
    Task<IEnumerable<Song>> GetSongsByAlbumIdAsync(Guid albumId);
    Task<IEnumerable<Song>> GetSongsByArtistIdAsync(Guid artistId);
    Task<IEnumerable<Song>> GetSongsByFolderIdAsync(Guid folderId);
    Task<IEnumerable<Song>> SearchSongsAsync(string searchTerm);

    #endregion

    #region Song Metadata Updates

    Task<bool> SetSongRatingAsync(Guid songId, int? rating);
    Task<bool> SetSongLovedStatusAsync(Guid songId, bool isLoved);
    Task<bool> UpdateSongLyricsAsync(Guid songId, string? lyrics);

    #endregion

    #region Artist Management

    Task<Artist?> GetArtistDetailsAsync(Guid artistId, bool allowOnlineFetch);
    Task StartArtistMetadataBackgroundFetchAsync();
    Task<Artist?> GetArtistByIdAsync(Guid artistId);
    Task<Artist?> GetArtistByNameAsync(string name);
    Task<IEnumerable<Artist>> GetAllArtistsAsync();
    Task<IEnumerable<Artist>> SearchArtistsAsync(string searchTerm);

    #endregion

    #region Album Management

    Task<Album?> GetAlbumByIdAsync(Guid albumId);
    Task<IEnumerable<Album>> GetAllAlbumsAsync();
    Task<IEnumerable<Album>> SearchAlbumsAsync(string searchTerm);

    #endregion

    #region Playlist Management

    Task<Playlist?> CreatePlaylistAsync(string name, string? description = null, string? coverImageUri = null);
    Task<bool> DeletePlaylistAsync(Guid playlistId);
    Task<bool> RenamePlaylistAsync(Guid playlistId, string newName);
    Task<bool> UpdatePlaylistCoverAsync(Guid playlistId, string? newCoverImageUri);
    Task<bool> AddSongsToPlaylistAsync(Guid playlistId, IEnumerable<Guid> songIds);
    Task<bool> RemoveSongsFromPlaylistAsync(Guid playlistId, IEnumerable<Guid> songIds);
    Task<bool> UpdatePlaylistSongOrderAsync(Guid playlistId, IEnumerable<Guid> orderedSongIds);
    Task<Playlist?> GetPlaylistByIdAsync(Guid playlistId);
    Task<IEnumerable<Playlist>> GetAllPlaylistsAsync();
    Task<IEnumerable<Song>> GetSongsInPlaylistOrderedAsync(Guid playlistId);

    #endregion

    #region Genre Management

    Task<IEnumerable<Genre>> GetAllGenresAsync();
    Task<IEnumerable<Song>> GetSongsByGenreIdAsync(Guid genreId);

    #endregion

    #region Listen History

    Task LogListenAsync(Guid songId);
    Task LogSkipAsync(Guid songId);
    Task<int> GetListenCountForSongAsync(Guid songId);

    #endregion

    #region Paged Loading

    Task<PagedResult<Song>> GetAllSongsPagedAsync(int pageNumber, int pageSize, SongSortOrder sortOrder = SongSortOrder.TitleAsc);
    Task<PagedResult<Song>> SearchSongsPagedAsync(string searchTerm, int pageNumber, int pageSize);
    Task<PagedResult<Song>> GetSongsByAlbumIdPagedAsync(Guid albumId, int pageNumber, int pageSize, SongSortOrder sortOrder);
    Task<PagedResult<Song>> GetSongsByArtistIdPagedAsync(Guid artistId, int pageNumber, int pageSize, SongSortOrder sortOrder);
    Task<PagedResult<Song>> GetSongsByPlaylistPagedAsync(Guid playlistId, int pageNumber, int pageSize);
    Task<PagedResult<Artist>> GetAllArtistsPagedAsync(int pageNumber, int pageSize);
    Task<PagedResult<Artist>> SearchArtistsPagedAsync(string searchTerm, int pageNumber, int pageSize);
    Task<PagedResult<Album>> GetAllAlbumsPagedAsync(int pageNumber, int pageSize);
    Task<PagedResult<Album>> SearchAlbumsPagedAsync(string searchTerm, int pageNumber, int pageSize);
    Task<PagedResult<Playlist>> GetAllPlaylistsPagedAsync(int pageNumber, int pageSize);
    Task<PagedResult<Song>> GetSongsByFolderIdPagedAsync(Guid folderId, int pageNumber, int pageSize, SongSortOrder sortOrder = SongSortOrder.TitleAsc);
    Task<List<Guid>> GetAllSongIdsAsync(SongSortOrder sortOrder);
    Task<List<Guid>> GetAllSongIdsByFolderIdAsync(Guid folderId, SongSortOrder sortOrder);
    Task<List<Guid>> GetAllSongIdsByArtistIdAsync(Guid artistId, SongSortOrder sortOrder);
    Task<List<Guid>> GetAllSongIdsByAlbumIdAsync(Guid albumId, SongSortOrder sortOrder);
    Task<List<Guid>> GetAllSongIdsByPlaylistIdAsync(Guid playlistId);

    #endregion

    #region Data Reset

    Task ClearAllLibraryDataAsync();

    #endregion

    #region Scoped Search

    Task<IEnumerable<Song>> SearchSongsInFolderAsync(Guid folderId, string searchTerm);
    Task<IEnumerable<Song>> SearchSongsInAlbumAsync(Guid albumId, string searchTerm);
    Task<IEnumerable<Song>> SearchSongsInArtistAsync(Guid artistId, string searchTerm);
    Task<PagedResult<Song>> SearchSongsInFolderPagedAsync(Guid folderId, string searchTerm, int pageNumber, int pageSize);
    Task<PagedResult<Song>> SearchSongsInAlbumPagedAsync(Guid albumId, string searchTerm, int pageNumber, int pageSize);
    Task<PagedResult<Song>> SearchSongsInArtistPagedAsync(Guid artistId, string searchTerm, int pageNumber, int pageSize);

    #endregion
}