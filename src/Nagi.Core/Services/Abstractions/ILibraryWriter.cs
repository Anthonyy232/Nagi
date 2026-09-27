using Nagi.Core.Models;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Defines the contract for writing, updating, and deleting data in the music library.
/// </summary>
public interface ILibraryWriter
{
    Task<Folder?> AddFolderAsync(string path, string? name = null);
    Task<bool> RemoveFolderAsync(Guid folderId);
    Task<bool> UpdateSongLrcPathAsync(Guid songId, string? lrcPath);
    Task<bool> UpdateSongLyricsLastCheckedAsync(Guid songId);
    Task<bool> UpdateArtistImageAsync(Guid artistId, string localFilePath);
    Task<bool> RemoveArtistImageAsync(Guid artistId);
    Task<long?> StartListenSessionAsync(Guid songId, PlaybackContext context);
    Task FinalizeListenSessionAsync(long listenHistoryId, TimeSpan finalDuration, PlaybackEndReason endReason);
    Task<bool> MarkListenAsScrobbledAsync(long listenHistoryId);

    /// <summary>
    ///     Marks the listen session identified by <paramref name="listenHistoryId" /> as successfully
    ///     submitted to ListenBrainz.
    /// </summary>
    /// <returns>True if the row was updated; false if the id did not exist.</returns>
    Task<bool> MarkListenAsSubmittedToListenBrainzAsync(long listenHistoryId);

    Task<bool> MarkListenAsEligibleForScrobblingAsync(long listenHistoryId);
    Task ClearListenHistoryAsync();
    Task ClearAllLibraryDataAsync();
}
