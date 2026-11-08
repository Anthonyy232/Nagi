using Nagi.Core.Playback;

namespace Nagi.Core.Listening;

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

    Task ClearListenHistoryAsync();

    /// <summary>
    ///     One page of the listen history, newest first. A search term matches the same song details as in song lists.
    /// </summary>
    Task<IReadOnlyList<RecentListen>> GetRecentListensAsync(string searchTerm, int skip, int take, CancellationToken token = default);

    /// <summary>A song the user played often but not for a long while, or null when there is none.</summary>
    Task<RediscoverySuggestion?> GetRediscoverySuggestionAsync(CancellationToken token = default);

    /// <summary>What a mix needs to know about each of the songs.</summary>
    Task<IReadOnlyList<MixCandidate>> GetMixCandidatesAsync(IEnumerable<Guid> songIds, CancellationToken token = default);
}
