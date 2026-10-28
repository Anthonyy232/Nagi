using Microsoft.Extensions.Logging;
using Nagi.Core.Models;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Saving and restoring the queue and current track between sessions.
/// </summary>
public partial class MusicPlaybackService
{
    public async Task SavePlaybackStateAsync()
    {
        if (!_isInitialized) return;

        var playbackQueue = _queue.Order.Where(id => !_transientTracks.ContainsKey(id)).ToList();
        var shuffledQueue = _queue.Shuffled.Where(id => !_transientTracks.ContainsKey(id)).ToList();
        var currentTrackId = CurrentTrack is not null && !_transientTracks.ContainsKey(CurrentTrack.Id)
            ? CurrentTrack.Id
            : (Guid?)null;

        var state = new PlaybackState
        {
            CurrentTrackId = currentTrackId,
            ShuffleMode = CurrentShuffleMode,
            PlaybackQueueTrackIds = playbackQueue,
            CurrentPlaybackQueueIndex = playbackQueue.IndexOf(_queue.Order.ElementAtOrDefault(CurrentQueueIndex)),
            ShuffledQueueTrackIds = IsShuffleEnabled ? shuffledQueue : [],
            CurrentShuffledQueueIndex = shuffledQueue.IndexOf(_queue.Shuffled.ElementAtOrDefault(CurrentShuffledIndex))
        };
        await _settingsService.SavePlaybackStateAsync(state);
    }

    private async Task<bool> RestoreInternalPlaybackStateAsync(PlaybackState state)
    {
        _logger.LogDebug("Attempting to restore previous playback state.");
        var savedQueue = state.PlaybackQueueTrackIds ?? [];
        _queue.SetOrder(savedQueue.Where(id => id != Guid.Empty).Distinct().ToList());
        _queue.RebuildIndex();

        if (!_queue.Order.Any()) return false;
        CurrentShuffleMode = Enum.IsDefined(state.ShuffleMode) ? state.ShuffleMode : ShuffleMode.Random;

        if (IsShuffleEnabled)
        {
            _queue.SetShuffled((state.ShuffledQueueTrackIds ?? Enumerable.Empty<Guid>()).ToList());

            // Equal lengths alone do not rule out duplicate or unrelated tracks.
            if (_queue.Shuffled.Count != _queue.Order.Count ||
                !_queue.Shuffled.ToHashSet().SetEquals(_queue.Order))
                _queue.GenerateShuffled();
            _queue.RebuildShuffledIndex();
        }

        if (state.CurrentTrackId.HasValue)
        {
            var currentSong = _queue.IndexOf(state.CurrentTrackId.Value) >= 0
                ? await _libraryReader.GetSongByIdAsync(state.CurrentTrackId.Value)
                : null;
            if (_isDisposed) return false;
            if (currentSong != null)
            {
                CurrentTrack = currentSong;
                CurrentQueueIndex = _queue.IndexOf(currentSong.Id);
                if (IsShuffleEnabled)
                {
                    CurrentShuffledIndex = _queue.ShuffledIndexOf(currentSong.Id);
                }

                await _audioPlayer.LoadAsync(CurrentTrack);
            }
            else
            {
                // The current track is missing from the library or queue, but the queue is still valid.
                // Fall back to starting from the beginning of the queue rather than clearing it.
                _logger.LogWarning("Could not restore current track {SongId}: it is missing from the library or saved queue. Falling back to queue start.", state.CurrentTrackId.Value);
                CurrentQueueIndex = 0;
                CurrentShuffledIndex = IsShuffleEnabled ? 0 : -1;
                CurrentTrack = null;
            }
        }
        else
        {
            // Resolve saved positions by identity because removing duplicates can shift indices.
            var positionId = state.CurrentPlaybackQueueIndex >= 0 && state.CurrentPlaybackQueueIndex < savedQueue.Count
                ? savedQueue[state.CurrentPlaybackQueueIndex]
                : Guid.Empty;
            CurrentQueueIndex = Math.Max(0, _queue.IndexOf(positionId));
            CurrentShuffledIndex = -1;
            if (IsShuffleEnabled)
            {
                var savedShuffle = state.ShuffledQueueTrackIds ?? [];
                var shuffledPositionId = state.CurrentShuffledQueueIndex >= 0 && state.CurrentShuffledQueueIndex < savedShuffle.Count
                    ? savedShuffle[state.CurrentShuffledQueueIndex]
                    : Guid.Empty;
                CurrentShuffledIndex = _queue.ShuffledIndexOf(shuffledPositionId);
                if (CurrentShuffledIndex < 0)
                    CurrentShuffledIndex = _queue.ShuffledIndexOf(_queue.Order[CurrentQueueIndex]);
                CurrentQueueIndex = _queue.IndexOf(_queue.Shuffled[CurrentShuffledIndex]);
            }
        }

        return true;
    }
}
