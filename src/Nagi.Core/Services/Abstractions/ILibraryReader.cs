using Nagi.Core.Models;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

public interface ILibraryReader
{
    Task<IReadOnlyList<MixCandidate>> GetMixCandidatesAsync(IEnumerable<Guid> songIds, CancellationToken token = default);
    Task<PagedResult<Song>> GetFavoritesPagedAsync(string searchTerm, int pageNumber, int pageSize, SongSortOrder sortOrder, CancellationToken token = default);
    Task<List<Guid>> GetFavoriteSongIdsAsync(string searchTerm, SongSortOrder sortOrder, CancellationToken token = default);
    Task<IReadOnlyList<RecentListen>> GetRecentListensAsync(string searchTerm, int skip, int take, CancellationToken token = default);
    Task<RediscoverySuggestion?> GetRediscoverySuggestionAsync(CancellationToken token = default);

    Task<Folder?> GetFolderByIdAsync(Guid folderId);
    Task<Folder?> GetFolderByPathAsync(string path);
    Task<bool> HasAnyFolderAsync(CancellationToken token = default);
    Task<IEnumerable<FolderSummary>> GetRootFoldersWithCountsAsync(CancellationToken token = default);

    Task<int> GetSubFolderCountAsync(Guid parentFolderId, CancellationToken token = default);
    Task<IEnumerable<Folder>> GetSubFoldersPagedAsync(Guid parentFolderId, int skip, int take, CancellationToken token = default);
    Task<int> GetSubFolderCountBySearchAsync(Guid parentFolderId, string searchTerm, CancellationToken token = default);
    Task<IEnumerable<Folder>> SearchSubFoldersPagedAsync(Guid parentFolderId, string searchTerm, int skip, int take, CancellationToken token = default);

    /// <summary>Includes lyrics, heavy fields, and artist relationships for playback and lyrics display.</summary>
    Task<Song?> GetSongByIdAsync(Guid songId);
    Task<Song?> GetSongByFilePathAsync(string filePath);
    Task<IReadOnlyDictionary<Guid, Song>> GetSongsByIdsAsync(IEnumerable<Guid> songIds);

    Task<IReadOnlyDictionary<Guid, Song>> GetQueueSongsAsync(IEnumerable<Guid> songIds, CancellationToken cancellationToken = default);
    Task<TimeSpan> GetSongsDurationAsync(IEnumerable<Guid> songIds, CancellationToken cancellationToken = default);
    Task<Artist?> GetArtistByNameAsync(string name);
    Task<IEnumerable<Album>> GetTopAlbumsForArtistAsync(Guid artistId, int limit);
    Task<Album?> GetAlbumByIdAsync(Guid albumId);
    Task<Playlist?> GetPlaylistByIdAsync(Guid playlistId);
    Task<IEnumerable<Playlist>> GetAllPlaylistsAsync(CancellationToken token = default);
    Task<IEnumerable<Song>> GetSongsInPlaylistOrderedAsync(Guid playlistId, CancellationToken token = default);
    Task<IEnumerable<GenreSummary>> GetGenreSummariesAsync(CancellationToken token = default);

    Task<Guid?> GetRandomAlbumIdAsync();
    Task<Guid?> GetRandomArtistIdAsync();
    Task<Guid?> GetRandomFolderIdAsync();
    Task<Guid?> GetRandomGenreIdAsync();
    Task<Guid?> GetRandomPlaylistIdAsync();

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

    Task<List<Guid>> GetAllSongIdsAsync(SongSortOrder sortOrder, CancellationToken token = default);
    Task<List<Guid>> GetAllSongIdsByFolderIdAsync(Guid folderId, SongSortOrder sortOrder, CancellationToken token = default);

    Task<List<Guid>> GetAllSongIdsInDirectoryRecursiveAsync(Guid folderId, string directoryPath,
        SongSortOrder sortOrder, CancellationToken token = default);

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

    Task<List<Guid>> GetAllSongIdsByArtistIdAsync(Guid artistId, SongSortOrder sortOrder, CancellationToken token = default);
    Task<List<Guid>> GetAllSongIdsByAlbumIdAsync(Guid albumId, SongSortOrder sortOrder, CancellationToken token = default);
    Task<List<Guid>> GetAllSongIdsByPlaylistIdAsync(Guid playlistId, SongSortOrder sortOrder, CancellationToken token = default);
    Task<List<Guid>> GetAllSongIdsByGenreIdAsync(Guid genreId, SongSortOrder sortOrder, CancellationToken token = default);
    Task<List<Guid>> SearchAllSongIdsAsync(string searchTerm, SongSortOrder sortOrder, CancellationToken token = default);

    Task<PagedResult<Song>> SearchSongsInAlbumPagedAsync(Guid albumId, string searchTerm, int pageNumber, int pageSize, CancellationToken token = default);

    Task<PagedResult<Song>> SearchSongsInArtistPagedAsync(Guid artistId, string searchTerm, int pageNumber,
        int pageSize, CancellationToken token = default);

    Task<PagedResult<Song>> SearchSongsInPlaylistPagedAsync(Guid playlistId, string searchTerm, int pageNumber, int pageSize, SongSortOrder sortOrder, CancellationToken token = default);

    Task<PagedResult<Song>> SearchSongsInGenrePagedAsync(Guid genreId, string searchTerm, int pageNumber, int pageSize, CancellationToken token = default);

    Task<List<Guid>> SearchAllSongIdsInFolderAsync(Guid folderId, string directoryPath, string searchTerm, SongSortOrder sortOrder, CancellationToken token = default);
    Task<List<Guid>> SearchAllSongIdsInArtistAsync(Guid artistId, string searchTerm, SongSortOrder sortOrder, CancellationToken token = default);
    Task<List<Guid>> SearchAllSongIdsInAlbumAsync(Guid albumId, string searchTerm, SongSortOrder sortOrder, CancellationToken token = default);
    Task<List<Guid>> SearchAllSongIdsInPlaylistAsync(Guid playlistId, string searchTerm, SongSortOrder sortOrder, CancellationToken token = default);
    Task<List<Guid>> SearchAllSongIdsInGenreAsync(Guid genreId, string searchTerm, SongSortOrder sortOrder, CancellationToken token = default);

    /// <summary>
    ///     Calculates the total duration of songs in an album that match the search term.
    ///     If the search term is empty, returns the total duration of all songs in the album.
    /// </summary>
    Task<TimeSpan> GetSearchTotalDurationInAlbumAsync(Guid albumId, string searchTerm, CancellationToken token = default);

    /// <summary>
    ///    Gets the artists associated with a song, ordered by their sequence.
    ///    Returns a lightweight projection containing only Id and Name.
    /// </summary>
    Task<IEnumerable<Artist>> GetArtistsForSongAsync(Guid songId);
}
