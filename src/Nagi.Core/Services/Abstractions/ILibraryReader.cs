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
    Task<IEnumerable<Folder>> GetAllFoldersAsync(CancellationToken token = default);
    Task<int> GetSongCountForFolderAsync(Guid folderId);

    // Folder Hierarchy Methods
    Task<IEnumerable<Folder>> GetRootFoldersAsync(CancellationToken token = default);
    Task<IEnumerable<Folder>> GetSubFoldersAsync(Guid parentFolderId, CancellationToken token = default);
    Task<Folder?> GetFolderByDirectoryPathAsync(Guid rootFolderId, string directoryPath);
    Task<IEnumerable<Song>> GetSongsInDirectoryAsync(Guid folderId, string directoryPath);
    Task<IEnumerable<Song>> GetSongsInDirectoryRecursiveAsync(Guid folderId, string directoryPath, CancellationToken token = default);
    Task<int> GetSongCountInDirectoryAsync(Guid folderId, string directoryPath);
    Task<int> GetSubFolderCountAsync(Guid parentFolderId, CancellationToken token = default);
    Task<IEnumerable<Folder>> GetSubFoldersPagedAsync(Guid parentFolderId, int skip, int take, CancellationToken token = default);
    Task<int> GetSubFolderCountBySearchAsync(Guid parentFolderId, string searchTerm, CancellationToken token = default);
    Task<IEnumerable<Folder>> SearchSubFoldersPagedAsync(Guid parentFolderId, string searchTerm, int skip, int take, CancellationToken token = default);

    Task<Song?> GetSongByIdAsync(Guid songId);
    Task<Song?> GetSongByFilePathAsync(string filePath);
    Task<IReadOnlyDictionary<Guid, Song>> GetSongsByIdsAsync(IEnumerable<Guid> songIds);
    Task<IEnumerable<Song>> GetAllSongsAsync(SongSortOrder sortOrder = SongSortOrder.TitleAsc, CancellationToken token = default);
    Task<IEnumerable<Song>> GetSongsByAlbumIdAsync(Guid albumId, CancellationToken token = default);
    Task<IEnumerable<Song>> GetSongsByArtistIdAsync(Guid artistId, CancellationToken token = default);
    Task<IEnumerable<Song>> GetSongsByFolderIdAsync(Guid folderId, CancellationToken token = default);
    Task<IEnumerable<Song>> SearchSongsAsync(string searchTerm, CancellationToken token = default);
    Task<Artist?> GetArtistByIdAsync(Guid artistId);
    Task<Artist?> GetArtistByNameAsync(string name);
    Task<IEnumerable<Artist>> GetAllArtistsAsync(CancellationToken token = default);
    Task<IEnumerable<Artist>> SearchArtistsAsync(string searchTerm, CancellationToken token = default);
    Task<IEnumerable<Album>> GetTopAlbumsForArtistAsync(Guid artistId, int limit);
    Task<Album?> GetAlbumByIdAsync(Guid albumId);
    Task<IEnumerable<Album>> GetAllAlbumsAsync(CancellationToken token = default);
    Task<IEnumerable<Album>> SearchAlbumsAsync(string searchTerm, CancellationToken token = default);
    Task<Playlist?> GetPlaylistByIdAsync(Guid playlistId);
    Task<IEnumerable<Playlist>> GetAllPlaylistsAsync(CancellationToken token = default);
    Task<IEnumerable<Song>> GetSongsInPlaylistOrderedAsync(Guid playlistId, CancellationToken token = default);
    Task<IEnumerable<Genre>> GetAllGenresAsync(CancellationToken token = default);
    Task<IEnumerable<Song>> GetSongsByGenreIdAsync(Guid genreId, CancellationToken token = default);
    Task<int> GetListenCountForSongAsync(Guid songId);
    
    // Random Access Methods
    Task<Guid?> GetRandomAlbumIdAsync();
    Task<Guid?> GetRandomArtistIdAsync();
    Task<Guid?> GetRandomFolderIdAsync();
    Task<Guid?> GetRandomGenreIdAsync();
    Task<Guid?> GetRandomPlaylistIdAsync();
    
    // Count Methods
    Task<int> GetPlaylistCountAsync();

    Task<PagedResult<Song>> GetAllSongsPagedAsync(int pageNumber, int pageSize,
        SongSortOrder sortOrder = SongSortOrder.TitleAsc, CancellationToken token = default);

    Task<PagedResult<Song>> SearchSongsPagedAsync(string searchTerm, int pageNumber, int pageSize, CancellationToken token = default);

    Task<PagedResult<Song>> GetSongsByAlbumIdPagedAsync(Guid albumId, int pageNumber, int pageSize,
        SongSortOrder sortOrder, CancellationToken token = default);

    Task<PagedResult<Song>> GetSongsByArtistIdPagedAsync(Guid artistId, int pageNumber, int pageSize,
        SongSortOrder sortOrder, CancellationToken token = default);

    Task<PagedResult<Song>> GetSongsByGenreIdPagedAsync(Guid genreId, int pageNumber, int pageSize,
        SongSortOrder sortOrder, CancellationToken token = default);

    Task<PagedResult<Song>> GetSongsByPlaylistPagedAsync(Guid playlistId, int pageNumber, int pageSize, SongSortOrder sortOrder, CancellationToken token = default);
    Task<PagedResult<Artist>> GetAllArtistsPagedAsync(int pageNumber, int pageSize,
        ArtistSortOrder sortOrder = ArtistSortOrder.NameAsc, CancellationToken token = default);
    Task<PagedResult<Artist>> SearchArtistsPagedAsync(string searchTerm, int pageNumber, int pageSize, CancellationToken token = default);
    Task<PagedResult<Album>> GetAllAlbumsPagedAsync(int pageNumber, int pageSize,
        AlbumSortOrder sortOrder = AlbumSortOrder.ArtistAsc, CancellationToken token = default);
    Task<PagedResult<Album>> SearchAlbumsPagedAsync(string searchTerm, int pageNumber, int pageSize, CancellationToken token = default);
    Task<PagedResult<Playlist>> GetAllPlaylistsPagedAsync(int pageNumber, int pageSize, CancellationToken token = default);

    Task<PagedResult<Song>> GetSongsByFolderIdPagedAsync(Guid folderId, int pageNumber, int pageSize,
        SongSortOrder sortOrder = SongSortOrder.TitleAsc, CancellationToken token = default);

    Task<List<Guid>> GetAllSongIdsAsync(SongSortOrder sortOrder, CancellationToken token = default);
    Task<List<Guid>> GetAllSongIdsByFolderIdAsync(Guid folderId, SongSortOrder sortOrder, CancellationToken token = default);

    Task<List<Guid>> GetAllSongIdsInDirectoryRecursiveAsync(Guid folderId, string directoryPath,
        SongSortOrder sortOrder, CancellationToken token = default);

    Task<PagedResult<Song>> GetSongsInDirectoryPagedAsync(Guid folderId, string directoryPath, int pageNumber,
        int pageSize, SongSortOrder sortOrder, CancellationToken token = default);

    /// <summary>
    ///     Fetches a skip/take slice of songs in a directory, always including TotalCount even when take=0.
    ///     Note: <see cref="PagedResult{T}.PageNumber"/> is always 0 — this is an offset-based query, not page-based.
    /// </summary>
    Task<PagedResult<Song>> GetSongsInDirectoryOffsetAsync(Guid folderId, string directoryPath,
        int skip, int take, SongSortOrder sortOrder, CancellationToken token = default);

    /// <summary>
    ///     Fetches a skip/take slice of songs in a directory matching searchTerm, always including TotalCount even when take=0.
    ///     Scoped to the same directoryPath as <see cref="GetSongsInDirectoryOffsetAsync"/> so only direct-children songs are returned.
    ///     Note: <see cref="PagedResult{T}.PageNumber"/> is always 0 — this is an offset-based query, not page-based.
    /// </summary>
    Task<PagedResult<Song>> SearchSongsInFolderOffsetAsync(Guid folderId, string directoryPath, string searchTerm, int skip, int take, SongSortOrder sortOrder, CancellationToken token = default);

    Task<List<Guid>> GetSongIdsInDirectoryAsync(Guid folderId, string directoryPath, SongSortOrder sortOrder, CancellationToken token = default);

    Task<List<Guid>> GetAllSongIdsByArtistIdAsync(Guid artistId, SongSortOrder sortOrder, CancellationToken token = default);
    Task<List<Guid>> GetAllSongIdsByAlbumIdAsync(Guid albumId, SongSortOrder sortOrder, CancellationToken token = default);
    Task<List<Guid>> GetAllSongIdsByPlaylistIdAsync(Guid playlistId, SongSortOrder sortOrder, CancellationToken token = default);
    Task<List<Guid>> GetAllSongIdsByGenreIdAsync(Guid genreId, SongSortOrder sortOrder, CancellationToken token = default);
    Task<List<Guid>> SearchAllSongIdsAsync(string searchTerm, SongSortOrder sortOrder, CancellationToken token = default);

    // Scoped Search (Non-Paged)
    Task<IEnumerable<Song>> SearchSongsInFolderAsync(Guid folderId, string searchTerm, CancellationToken token = default);
    Task<IEnumerable<Song>> SearchSongsInAlbumAsync(Guid albumId, string searchTerm, CancellationToken token = default);
    Task<IEnumerable<Song>> SearchSongsInArtistAsync(Guid artistId, string searchTerm, CancellationToken token = default);
    Task<IEnumerable<Song>> SearchSongsInPlaylistAsync(Guid playlistId, string searchTerm, CancellationToken token = default);
    Task<IEnumerable<Song>> SearchSongsInGenreAsync(Guid genreId, string searchTerm, CancellationToken token = default);

    // Scoped Search (Paged)
    Task<PagedResult<Song>> SearchSongsInFolderPagedAsync(Guid folderId, string searchTerm, int pageNumber,
        int pageSize, CancellationToken token = default);

    Task<PagedResult<Song>> SearchSongsInAlbumPagedAsync(Guid albumId, string searchTerm, int pageNumber, int pageSize, CancellationToken token = default);

    Task<PagedResult<Song>> SearchSongsInArtistPagedAsync(Guid artistId, string searchTerm, int pageNumber,
        int pageSize, CancellationToken token = default);

    Task<PagedResult<Song>> SearchSongsInPlaylistPagedAsync(Guid playlistId, string searchTerm, int pageNumber, int pageSize, SongSortOrder sortOrder, CancellationToken token = default);

    Task<PagedResult<Song>> SearchSongsInGenrePagedAsync(Guid genreId, string searchTerm, int pageNumber, int pageSize, CancellationToken token = default);

    // Scoped Search (Song IDs)
    Task<List<Guid>> SearchAllSongIdsInFolderAsync(Guid folderId, string searchTerm, SongSortOrder sortOrder, CancellationToken token = default);
    Task<List<Guid>> SearchAllSongIdsInArtistAsync(Guid artistId, string searchTerm, SongSortOrder sortOrder, CancellationToken token = default);
    Task<List<Guid>> SearchAllSongIdsInAlbumAsync(Guid albumId, string searchTerm, SongSortOrder sortOrder, CancellationToken token = default);
    Task<List<Guid>> SearchAllSongIdsInPlaylistAsync(Guid playlistId, string searchTerm, SongSortOrder sortOrder, CancellationToken token = default);
    Task<List<Guid>> SearchAllSongIdsInGenreAsync(Guid genreId, string searchTerm, SongSortOrder sortOrder, CancellationToken token = default);

    /// <summary>
    ///     Gets a song by its ID, including all heavy fields like Lyrics, Comment, and Copyright.
    ///     Use this method when you need the full song data (e.g., for lyrics display or editing).
    /// </summary>
    Task<Song?> GetSongWithFullDataAsync(Guid songId);

    /// <summary>
    ///     Calculates the total duration of all songs in an album.
    /// </summary>
    /// <param name="albumId">The unique identifier of the album.</param>
    /// <returns>The total duration of all songs in the album.</returns>
    Task<TimeSpan> GetAlbumTotalDurationAsync(Guid albumId);

    /// <summary>
    ///     Calculates the total duration of songs in an album that match the search term.
    ///     If the search term is empty, returns the total duration of all songs in the album.
    /// </summary>
    /// <param name="albumId">The unique identifier of the album.</param>
    /// <param name="searchTerm">The search term to filter songs. Can be empty or null.</param>
    /// <returns>The total duration of matching songs in the album.</returns>
    Task<TimeSpan> GetSearchTotalDurationInAlbumAsync(Guid albumId, string searchTerm, CancellationToken token = default);

    /// <summary>
    ///    Gets the artists associated with a song, ordered by their sequence.
    ///    Returns a lightweight projection containing only Id and Name.
    /// </summary>
    Task<IEnumerable<Artist>> GetArtistsForSongAsync(Guid songId);
}