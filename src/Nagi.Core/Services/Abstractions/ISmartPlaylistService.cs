using Nagi.Core.Models;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Defines the contract for managing smart playlists.
/// </summary>
public interface ISmartPlaylistService
{
    event EventHandler<PlaylistUpdatedEventArgs>? PlaylistUpdated;
    event EventHandler? PlaylistsChanged;

    Task<SmartPlaylist> CreateSmartPlaylistAsync(SmartPlaylist playlist);
    Task<bool> DeleteSmartPlaylistAsync(Guid smartPlaylistId);
    Task<SmartPlaylist?> UpdateSmartPlaylistAsync(SmartPlaylist playlist);
    Task<SmartPlaylist?> GetSmartPlaylistByIdAsync(Guid smartPlaylistId);
    Task<IEnumerable<SmartPlaylist>> GetAllSmartPlaylistsAsync();
    Task<bool> RenameSmartPlaylistAsync(Guid smartPlaylistId, string newName);
    Task<bool> UpdateSmartPlaylistCoverAsync(Guid smartPlaylistId, string? newCoverImageUri);

    Task<Guid?> GetRandomSmartPlaylistIdAsync();
    Task<int> GetSmartPlaylistCountAsync();

    Task<bool> SetSortOrderAsync(Guid smartPlaylistId, SmartPlaylistSortOrder sortOrder);

    Task<PagedResult<Song>> GetMatchingSongsPagedAsync(Guid smartPlaylistId, int pageNumber, int pageSize, string? searchTerm = null, SongSortOrder? sortOrder = null, CancellationToken token = default);
    Task<int> GetMatchingSongCountAsync(Guid smartPlaylistId, string? searchTerm = null, CancellationToken token = default);
    Task<int> GetMatchingSongCountAsync(SmartPlaylist smartPlaylist, string? searchTerm = null, CancellationToken token = default);
    Task<List<Guid>> GetMatchingSongIdsAsync(Guid smartPlaylistId, string? searchTerm = null, SongSortOrder? sortOrder = null, CancellationToken token = default);

    Task<Dictionary<Guid, int>> GetAllMatchingSongCountsAsync();
}
