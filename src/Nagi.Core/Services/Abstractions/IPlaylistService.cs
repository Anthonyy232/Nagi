using Nagi.Core.Models;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

public interface IPlaylistService
{
    event EventHandler<PlaylistUpdatedEventArgs>? PlaylistUpdated;
    event EventHandler? PlaylistsChanged;

    /// <exception cref="PlaylistNameTakenException">Another playlist already has the name.</exception>
    Task<Playlist> CreatePlaylistAsync(string name, string? description = null, string? coverImageUri = null);
    Task<bool> DeletePlaylistAsync(Guid playlistId);
    /// <exception cref="PlaylistNameTakenException">Another playlist already has the name.</exception>
    Task<bool> RenamePlaylistAsync(Guid playlistId, string newName);
    Task<bool> UpdatePlaylistCoverAsync(Guid playlistId, string? newCoverImageUri);
    Task<bool> AddSongsToPlaylistAsync(Guid playlistId, IEnumerable<Guid> songIds);
    Task<bool> RemoveSongsFromPlaylistAsync(Guid playlistId, IEnumerable<Guid> songIds);
    Task<bool> UpdatePlaylistOrderAsync(Guid playlistId, IEnumerable<Guid> orderedSongIds);
    Task<bool> MovePlaylistSongAsync(Guid playlistId, Guid songId, double newOrder);
}
