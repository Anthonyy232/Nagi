using Nagi.Core.Models;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

public interface ILibraryWriter
{
    Task<bool> SetSongsLovedAsync(IEnumerable<Guid> songIds, bool loved);
    /// <returns>The existing folder when the path is already in the library.</returns>
    /// <exception cref="FolderOverlapsException">The path is inside, or contains, a folder already in the library.</exception>
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
