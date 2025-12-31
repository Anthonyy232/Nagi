using Nagi.Core.Models;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Defines the contract for managing smart playlists.
/// </summary>
public interface ISmartPlaylistService
{
    // Events
    event EventHandler<PlaylistUpdatedEventArgs>? PlaylistUpdated;

    // CRUD Operations
    Task<SmartPlaylist?> CreateSmartPlaylistAsync(string name, string? description = null, string? coverImageUri = null);
    Task<bool> DeleteSmartPlaylistAsync(Guid smartPlaylistId);
    Task<bool> UpdateSmartPlaylistAsync(SmartPlaylist smartPlaylist);
    Task<SmartPlaylist?> GetSmartPlaylistByIdAsync(Guid smartPlaylistId);
    Task<IEnumerable<SmartPlaylist>> GetAllSmartPlaylistsAsync();
    Task<bool> RenameSmartPlaylistAsync(Guid smartPlaylistId, string newName);
    Task<bool> UpdateSmartPlaylistCoverAsync(Guid smartPlaylistId, string? newCoverImageUri);

    // Configuration
    Task<bool> SetMatchAllRulesAsync(Guid smartPlaylistId, bool matchAll);

    Task<bool> SetSortOrderAsync(Guid smartPlaylistId, SmartPlaylistSortOrder sortOrder);

    // Rule Management
    Task<SmartPlaylistRule?> AddRuleAsync(Guid smartPlaylistId, SmartPlaylistField field, SmartPlaylistOperator op,
        string? value, string? secondValue = null);

    Task<bool> UpdateRuleAsync(Guid ruleId, SmartPlaylistField field, SmartPlaylistOperator op,
        string? value, string? secondValue = null);

    Task<bool> RemoveRuleAsync(Guid ruleId);
    Task<bool> ReorderRulesAsync(Guid smartPlaylistId, IEnumerable<Guid> orderedRuleIds);

    // Query Execution
    Task<IEnumerable<Song>> GetMatchingSongsAsync(Guid smartPlaylistId, string? searchTerm = null);
    Task<PagedResult<Song>> GetMatchingSongsPagedAsync(Guid smartPlaylistId, int pageNumber, int pageSize, string? searchTerm = null);
    Task<int> GetMatchingSongCountAsync(Guid smartPlaylistId, string? searchTerm = null);
    Task<int> GetMatchingSongCountAsync(SmartPlaylist smartPlaylist, string? searchTerm = null);
    Task<List<Guid>> GetMatchingSongIdsAsync(Guid smartPlaylistId);
    
    /// <summary>
    ///     Gets the matching song counts for all smart playlists in a single batch operation.
    ///     This is more efficient than calling GetMatchingSongCountAsync for each playlist individually.
    /// </summary>
    /// <returns>A dictionary mapping smart playlist IDs to their matching song counts.</returns>
    Task<Dictionary<Guid, int>> GetAllMatchingSongCountsAsync();
}
