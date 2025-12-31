using Nagi.Core.Models;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Defines the contract for managing playlists.
/// </summary>
public interface IPlaylistService
{
    event EventHandler<PlaylistUpdatedEventArgs>? PlaylistUpdated;

    Task<Playlist?> CreatePlaylistAsync(string name, string? description = null, string? coverImageUri = null);
    Task<bool> DeletePlaylistAsync(Guid playlistId);
    Task<bool> RenamePlaylistAsync(Guid playlistId, string newName);
    Task<bool> UpdatePlaylistCoverAsync(Guid playlistId, string? newCoverImageUri);
    Task<bool> AddSongsToPlaylistAsync(Guid playlistId, IEnumerable<Guid> songIds);
    Task<bool> RemoveSongsFromPlaylistAsync(Guid playlistId, IEnumerable<Guid> songIds);
    Task<bool> UpdatePlaylistSongOrderAsync(Guid playlistId, IEnumerable<Guid> orderedSongIds);
}