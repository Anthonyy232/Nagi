using Nagi.Core.Library;

namespace Nagi.Core.Playlists;

/// <summary>
///     Reads playlists and changes the ones made in Nagi. A playlist from a music server follows the server, so
///     changing one returns false.
/// </summary>
public interface IPlaylistService
{
    /// <summary>Every playlist with its entries and server folder, sorted by name.</summary>
    Task<IEnumerable<Playlist>> GetAllPlaylistsAsync(CancellationToken token = default);

    Task<Playlist?> GetPlaylistByIdAsync(Guid playlistId);

    /// <summary>The playlist's songs in playlist order, as list rows without lyrics.</summary>
    Task<IEnumerable<Song>> GetSongsInPlaylistOrderedAsync(Guid playlistId, CancellationToken token = default);

    Task<int> GetPlaylistCountAsync();
    Task<Guid?> GetRandomPlaylistIdAsync();

    /// <summary>
    ///     Raised after any change to an existing playlist or its songs, with its new cover and modification date.
    /// </summary>
    event EventHandler<PlaylistUpdatedEventArgs>? PlaylistUpdated;

    /// <summary>
    ///     Raised after any change a list of playlists shows: a playlist added, removed or changed.
    /// </summary>
    event EventHandler? PlaylistsChanged;

    /// <exception cref="PlaylistNameTakenException">Another playlist already has the name.</exception>
    Task<Playlist> CreatePlaylistAsync(string name, string? description = null, string? coverImageUri = null);

    /// <summary>
    ///     The name as a playlist would store it, or the first free "Name (2)", "Name (3)"… when that is taken.
    /// </summary>
    Task<string> FindFreeNameAsync(string name);

    Task<bool> DeletePlaylistAsync(Guid playlistId);
    /// <exception cref="PlaylistNameTakenException">Another playlist already has the name.</exception>
    Task<bool> RenamePlaylistAsync(Guid playlistId, string newName);
    Task<bool> UpdatePlaylistCoverAsync(Guid playlistId, string? newCoverImageUri);
    Task<bool> AddSongsToPlaylistAsync(Guid playlistId, IEnumerable<Guid> songIds);
    Task<bool> RemoveSongsFromPlaylistAsync(Guid playlistId, IEnumerable<Guid> songIds);
    Task<bool> UpdatePlaylistOrderAsync(Guid playlistId, IEnumerable<Guid> orderedSongIds);
    Task<bool> MovePlaylistSongAsync(Guid playlistId, Guid songId, double newOrder);
}
