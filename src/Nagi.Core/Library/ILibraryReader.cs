using Nagi.Core.Listening;
using Nagi.Core.MusicServers;
using Nagi.Core.Playlists;

namespace Nagi.Core.Library;

/// <summary>
///     Reads the library. A search term matches the same song details in every list; a blank one matches everything.
/// </summary>
public interface ILibraryReader
{
    Task<IReadOnlyList<MixCandidate>> GetMixCandidatesAsync(IEnumerable<Guid> songIds, CancellationToken token = default);
    Task<IReadOnlyList<RecentListen>> GetRecentListensAsync(string searchTerm, int skip, int take, CancellationToken token = default);
    Task<RediscoverySuggestion?> GetRediscoverySuggestionAsync(CancellationToken token = default);

    /// <returns>The folder with its <see cref="Folder.MusicServer" />, or null.</returns>
    Task<Folder?> GetFolderByIdAsync(Guid folderId);
    Task<bool> HasAnyFolderAsync(CancellationToken token = default);
    Task<IEnumerable<FolderSummary>> GetRootFoldersWithCountsAsync(CancellationToken token = default);

    /// <summary>Includes lyrics, heavy fields, and artist relationships for playback and lyrics display.</summary>
    Task<Song?> GetSongByIdAsync(Guid songId);
    Task<Song?> GetSongByFilePathAsync(string filePath);
    Task<IReadOnlyDictionary<Guid, Song>> GetSongsByIdsAsync(IEnumerable<Guid> songIds);

    Task<IReadOnlyDictionary<Guid, Song>> GetQueueSongsAsync(IEnumerable<Guid> songIds, CancellationToken cancellationToken = default);
    Task<TimeSpan> GetSongsDurationAsync(IEnumerable<Guid> songIds, CancellationToken cancellationToken = default);
    Task<Artist?> GetArtistByNameAsync(string name);

    /// <summary>
    ///     The albums the artist made or appears on, the most played first.
    /// </summary>
    Task<IEnumerable<Album>> GetAlbumsForArtistAsync(Guid artistId);

    Task<Album?> GetAlbumByIdAsync(Guid albumId);
    Task<IEnumerable<Playlist>> GetAllPlaylistsAsync(CancellationToken token = default);
    Task<Playlist?> GetPlaylistByIdAsync(Guid playlistId);
    Task<IEnumerable<Song>> GetSongsInPlaylistOrderedAsync(Guid playlistId, CancellationToken token = default);
    Task<IEnumerable<GenreSummary>> GetGenreSummariesAsync(CancellationToken token = default);

    Task<Guid?> GetRandomAlbumIdAsync();
    Task<Guid?> GetRandomArtistIdAsync();
    Task<Guid?> GetRandomFolderIdAsync();
    Task<Guid?> GetRandomGenreIdAsync();
    Task<Guid?> GetRandomPlaylistIdAsync();

    Task<int> GetPlaylistCountAsync();

    /// <summary>
    ///     One page of the songs in <paramref name="scope" /> that match <paramref name="searchTerm" />, as list rows
    ///     without lyrics.
    /// </summary>
    Task<PagedResult<Song>> GetSongsAsync(SongScope scope, string? searchTerm, SongSortOrder sortOrder, int pageNumber,
        int pageSize, CancellationToken token = default);

    /// <summary>
    ///     One page of a directory's sub-folders whose names match <paramref name="searchTerm" />, sorted by name, followed
    ///     by its songs that match it, sorted by <paramref name="sortOrder" />.
    /// </summary>
    Task<DirectoryPage> GetDirectoryPageAsync(SongScope.Directory directory, string? searchTerm, SongSortOrder sortOrder,
        int pageNumber, int pageSize, CancellationToken token = default);

    /// <summary>
    ///     The IDs of every song <see cref="GetSongsAsync" /> lists, in the same order, such as to play them all.
    /// </summary>
    Task<List<Guid>> GetSongIdsAsync(SongScope scope, string? searchTerm, SongSortOrder sortOrder,
        CancellationToken token = default);

    Task<TimeSpan> GetSongsDurationAsync(SongScope scope, string? searchTerm, CancellationToken token = default);

    Task<PagedResult<Artist>> GetArtistsAsync(string? searchTerm, ArtistSortOrder sortOrder, int pageNumber, int pageSize,
        CancellationToken token = default);

    Task<PagedResult<Album>> GetAlbumsAsync(string? searchTerm, AlbumSortOrder sortOrder, int pageNumber, int pageSize,
        CancellationToken token = default);

    /// <summary>
    ///    Gets the artists associated with a song, ordered by their sequence.
    ///    Returns a lightweight projection containing only Id and Name.
    /// </summary>
    Task<IEnumerable<Artist>> GetArtistsForSongAsync(Guid songId);
}
