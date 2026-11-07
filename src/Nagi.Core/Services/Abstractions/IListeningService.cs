using Nagi.Core.Models;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     What the user listens to and loves: favorites, and the listen history that play counts and scrobbles come from.
/// </summary>
public interface IListeningService
{
    /// <returns>True if any song changed.</returns>
    Task<bool> SetSongsLovedAsync(IEnumerable<Guid> songIds, bool loved);

    /// <returns>The listen's ID, or null if the song isn't in the library.</returns>
    Task<long?> StartListenSessionAsync(Guid songId, PlaybackContext context);

    Task FinalizeListenSessionAsync(long listenHistoryId, TimeSpan finalDuration, PlaybackEndReason endReason);

    /// <summary>
    ///     Counts the listen as a play once it reached the scrobble threshold.
    /// </summary>
    /// <returns>False if the listen doesn't exist.</returns>
    Task<bool> MarkListenAsEligibleForScrobblingAsync(long listenHistoryId);

    Task<bool> MarkListenAsScrobbledAsync(long listenHistoryId);

    Task<bool> MarkListenAsSubmittedToListenBrainzAsync(long listenHistoryId);

    Task ClearListenHistoryAsync();
}
