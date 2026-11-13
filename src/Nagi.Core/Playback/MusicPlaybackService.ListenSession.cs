using Nagi.Core.Library;
using Nagi.Core.Listening;

namespace Nagi.Core.Playback;

/// <summary>
///     Recording listens: starting and ending them, and marking them eligible for scrobbling.
/// </summary>
public partial class MusicPlaybackService
{
    private bool _isEligibilityMarked;

    /// <returns>The new listen, or null when none is recorded. A listen started for a superseded request is ended at once.</returns>
    private async Task<long?> StartListenAsync(Song song, PlaybackContext context, CancellationToken request)
    {
        if (_transientTracks.ContainsKey(song.Id)) return null;
        var sessionId = await _listening.StartListenSessionAsync(song.Id, context);
        if (request.IsCancellationRequested) await AbandonListenAsync(sessionId);
        return sessionId;
    }

    // Ends a listen that was started but never became current.
    private Task AbandonListenAsync(long? sessionId) => sessionId is { } id
        ? _listening.FinalizeListenSessionAsync(id, TimeSpan.Zero, PlaybackEndReason.PausedAndAbandoned)
        : Task.CompletedTask;

    private void BeginListen(long? sessionId)
    {
        CurrentListenHistoryId = sessionId;
        _isEligibilityMarked = false;
    }

    /// <summary>
    ///     Records the end of the current listen, if one is being recorded.
    /// </summary>
    /// <remarks>
    ///     The session and position are taken at once: stopping the player resets its position, and a track ending
    ///     at the same moment must not end the same session again.
    /// </remarks>
    /// <param name="position">How far the listen got, or null for where the player is now.</param>
    /// <returns>The write, which logs its own failure.</returns>
    private Task EndListenSession(PlaybackEndReason reason, TimeSpan? position = null)
    {
        if (CurrentListenHistoryId is not { } sessionId) return Task.CompletedTask;
        var listened = position ?? _audioPlayer.CurrentPosition;
        CurrentListenHistoryId = null;
        return RunInBackground(() => _listening.FinalizeListenSessionAsync(sessionId, listened, reason),
            "Finalizing a listen");
    }

    private void MaybeMarkEligibleForScrobbling()
    {
        if (IsRequestPending || _isEligibilityMarked || !CurrentListenHistoryId.HasValue) return;

        if (!ListenRules.IsEligible(Duration, _audioPlayer.CurrentPosition)) return;

        _isEligibilityMarked = true;
        var sessionId = CurrentListenHistoryId.Value;
        RunInBackground(
            async () =>
            {
                try
                {
                    if (await _listening.MarkListenAsEligibleForScrobblingAsync(sessionId) && !_isDisposed)
                        ScrobbleEligibilityReached?.Invoke();
                }
                catch
                {
                    // A failed write is tried again on the next position update. A listen whose history was cleared
                    // is not, since it can never be marked.
                    if (CurrentListenHistoryId == sessionId) _isEligibilityMarked = false;
                    throw;
                }
            },
            "Mark listen eligible for scrobbling");
    }
}
