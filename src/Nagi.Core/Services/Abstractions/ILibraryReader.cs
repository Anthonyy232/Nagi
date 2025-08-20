using Nagi.Core.Models;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Defines the contract for reading and querying data from the music library.
/// </summary>
public interface ILibraryReader
{
    Task<Folder?> GetFolderByIdAsync(Guid folderId);
    Task<Folder?> GetFolderByPathAsync(string path);
    Task<IEnumerable<Folder>> GetAllFoldersAsync();
    Task<int> GetSongCountForFolderAsync(Guid folderId);
    Task<Song?> GetSongByIdAsync(Guid songId);
    Task<Song?> GetSongByFilePathAsync(string filePath);
    Task<IReadOnlyDictionary<Guid, Song>> GetSongsByIdsAsync(IEnumerable<Guid> songIds);
    Task<IEnumerable<Song>> GetAllSongsAsync(SongSortOrder sortOrder = SongSortOrder.TitleAsc);
    Task<IEnumerable<Song>> GetSongsByAlbumIdAsync(Guid albumId);
    Task<IEnumerable<Song>> GetSongsByArtistIdAsync(Guid artistId);
    Task<IEnumerable<Song>> GetSongsByFolderIdAsync(Guid folderId);
    Task<IEnumerable<Song>> SearchSongsAsync(string searchTerm);
    Task<Artist?> GetArtistByIdAsync(Guid artistId);
    Task<Artist?> GetArtistByNameAsync(string name);
    Task<IEnumerable<Artist>> GetAllArtistsAsync();
    Task<IEnumerable<Artist>> SearchArtistsAsync(string searchTerm);
    Task<Album?> GetAlbumByIdAsync(Guid albumId);
    Task<IEnumerable<Album>> GetAllAlbumsAsync();
    Task<IEnumerable<Album>> SearchAlbumsAsync(string searchTerm);
    Task<Playlist?> GetPlaylistByIdAsync(Guid playlistId);
    Task<IEnumerable<Playlist>> GetAllPlaylistsAsync();
    Task<IEnumerable<Song>> GetSongsInPlaylistOrderedAsync(Guid playlistId);
    Task<IEnumerable<Genre>> GetAllGenresAsync();
    Task<IEnumerable<Song>> GetSongsByGenreIdAsync(Guid genreId);
    Task<int> GetListenCountForSongAsync(Guid songId);

    Task<PagedResult<Song>> GetAllSongsPagedAsync(int pageNumber, int pageSize,
        SongSortOrder sortOrder = SongSortOrder.TitleAsc);

    Task<PagedResult<Song>> SearchSongsPagedAsync(string searchTerm, int pageNumber, int pageSize);

    Task<PagedResult<Song>> GetSongsByAlbumIdPagedAsync(Guid albumId, int pageNumber, int pageSize,
        SongSortOrder sortOrder);

    Task<PagedResult<Song>> GetSongsByArtistIdPagedAsync(Guid artistId, int pageNumber, int pageSize,
        SongSortOrder sortOrder);

    Task<PagedResult<Song>> GetSongsByGenreIdPagedAsync(Guid genreId, int pageNumber, int pageSize,
        SongSortOrder sortOrder);

    Task<PagedResult<Song>> GetSongsByPlaylistPagedAsync(Guid playlistId, int pageNumber, int pageSize);
    Task<PagedResult<Artist>> GetAllArtistsPagedAsync(int pageNumber, int pageSize);
    Task<PagedResult<Artist>> SearchArtistsPagedAsync(string searchTerm, int pageNumber, int pageSize);
    Task<PagedResult<Album>> GetAllAlbumsPagedAsync(int pageNumber, int pageSize);
    Task<PagedResult<Album>> SearchAlbumsPagedAsync(string searchTerm, int pageNumber, int pageSize);
    Task<PagedResult<Playlist>> GetAllPlaylistsPagedAsync(int pageNumber, int pageSize);

    Task<PagedResult<Song>> GetSongsByFolderIdPagedAsync(Guid folderId, int pageNumber, int pageSize,
        SongSortOrder sortOrder = SongSortOrder.TitleAsc);

    Task<List<Guid>> GetAllSongIdsAsync(SongSortOrder sortOrder);
    Task<List<Guid>> GetAllSongIdsByFolderIdAsync(Guid folderId, SongSortOrder sortOrder);
    Task<List<Guid>> GetAllSongIdsByArtistIdAsync(Guid artistId, SongSortOrder sortOrder);
    Task<List<Guid>> GetAllSongIdsByAlbumIdAsync(Guid albumId, SongSortOrder sortOrder);
    Task<List<Guid>> GetAllSongIdsByPlaylistIdAsync(Guid playlistId);
    Task<List<Guid>> GetAllSongIdsByGenreIdAsync(Guid genreId, SongSortOrder sortOrder);
    Task<List<Guid>> SearchAllSongIdsAsync(string searchTerm, SongSortOrder sortOrder);

    // Scoped Search (Non-Paged)
    Task<IEnumerable<Song>> SearchSongsInFolderAsync(Guid folderId, string searchTerm);
    Task<IEnumerable<Song>> SearchSongsInAlbumAsync(Guid albumId, string searchTerm);
    Task<IEnumerable<Song>> SearchSongsInArtistAsync(Guid artistId, string searchTerm);
    Task<IEnumerable<Song>> SearchSongsInPlaylistAsync(Guid playlistId, string searchTerm);
    Task<IEnumerable<Song>> SearchSongsInGenreAsync(Guid genreId, string searchTerm);

    // Scoped Search (Paged)
    Task<PagedResult<Song>> SearchSongsInFolderPagedAsync(Guid folderId, string searchTerm, int pageNumber,
        int pageSize);

    Task<PagedResult<Song>> SearchSongsInAlbumPagedAsync(Guid albumId, string searchTerm, int pageNumber, int pageSize);

    Task<PagedResult<Song>> SearchSongsInArtistPagedAsync(Guid artistId, string searchTerm, int pageNumber,
        int pageSize);

    Task<PagedResult<Song>> SearchSongsInPlaylistPagedAsync(Guid playlistId, string searchTerm, int pageNumber,
        int pageSize);

    Task<PagedResult<Song>> SearchSongsInGenrePagedAsync(Guid genreId, string searchTerm, int pageNumber, int pageSize);

    // Scoped Search (Song IDs)
    Task<List<Guid>> SearchAllSongIdsInFolderAsync(Guid folderId, string searchTerm, SongSortOrder sortOrder);
    Task<List<Guid>> SearchAllSongIdsInArtistAsync(Guid artistId, string searchTerm, SongSortOrder sortOrder);
    Task<List<Guid>> SearchAllSongIdsInAlbumAsync(Guid albumId, string searchTerm, SongSortOrder sortOrder);
    Task<List<Guid>> SearchAllSongIdsInPlaylistAsync(Guid playlistId, string searchTerm);
    Task<List<Guid>> SearchAllSongIdsInGenreAsync(Guid genreId, string searchTerm, SongSortOrder sortOrder);
}