using Nagi.Core.Library;
using Nagi.Core.Models;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

public interface ISmartPlaylistService
{
    /// <inheritdoc cref="IPlaylistService.PlaylistUpdated" />
    event EventHandler<PlaylistUpdatedEventArgs>? PlaylistUpdated;

    /// <inheritdoc cref="IPlaylistService.PlaylistsChanged" />
    event EventHandler? PlaylistsChanged;

    /// <exception cref="PlaylistNameTakenException">Another smart playlist already has the name.</exception>
    Task<SmartPlaylist> CreateSmartPlaylistAsync(SmartPlaylist playlist);
    Task<bool> DeleteSmartPlaylistAsync(Guid smartPlaylistId);
    /// <exception cref="PlaylistNameTakenException">Another smart playlist already has the name.</exception>
    Task<SmartPlaylist?> UpdateSmartPlaylistAsync(SmartPlaylist playlist);
    Task<SmartPlaylist?> GetSmartPlaylistByIdAsync(Guid smartPlaylistId);
    Task<IEnumerable<SmartPlaylist>> GetAllSmartPlaylistsAsync();
    /// <exception cref="PlaylistNameTakenException">Another smart playlist already has the name.</exception>
    Task<bool> RenameSmartPlaylistAsync(Guid smartPlaylistId, string newName);
    Task<bool> UpdateSmartPlaylistCoverAsync(Guid smartPlaylistId, string? newCoverImageUri);

    Task<Guid?> GetRandomSmartPlaylistIdAsync();
    Task<int> GetSmartPlaylistCountAsync();

    Task<bool> SetSortOrderAsync(Guid smartPlaylistId, SmartPlaylistSortOrder sortOrder);

    Task<PagedResult<Song>> GetMatchingSongsPagedAsync(Guid smartPlaylistId, int pageNumber, int pageSize, string? searchTerm = null, SongSortOrder? sortOrder = null, CancellationToken token = default);

    /// <summary>
    ///     Counts the songs an unsaved playlist would match, reading its rule values as saving would.
    /// </summary>
    Task<int> GetMatchingSongCountAsync(SmartPlaylist smartPlaylist, CancellationToken token = default);

    Task<List<Guid>> GetMatchingSongIdsAsync(Guid smartPlaylistId, string? searchTerm = null, SongSortOrder? sortOrder = null, CancellationToken token = default);

    Task<Dictionary<Guid, int>> GetAllMatchingSongCountsAsync();
}
