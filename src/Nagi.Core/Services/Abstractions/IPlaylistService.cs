using Nagi.Core.Models;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Changes playlists made in Nagi. A playlist from a music server follows the server, so changing one returns false.
/// </summary>
public interface IPlaylistService
{
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
