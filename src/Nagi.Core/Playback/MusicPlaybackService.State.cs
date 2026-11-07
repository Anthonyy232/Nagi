using Microsoft.Extensions.Logging;
using Nagi.Core.Settings;

namespace Nagi.Core.Playback;

/// <summary>
///     Saving and restoring the queue and current track between sessions.
/// </summary>
public partial class MusicPlaybackService
{
    public async Task SavePlaybackStateAsync()
    {
        if (!_settingsService.Get(CoreSettings.RestorePlaybackState))
        {
            _playbackStateStore.Clear();
            return;
        }
        if (!_isInitialized) return;

        var playbackQueue = _queue.Order.Where(id => !_transientTracks.ContainsKey(id)).ToList();
        List<Guid> shuffledQueue = IsShuffleEnabled ? _queue.PlayOrder.Where(id => !_transientTracks.ContainsKey(id)).ToList() : [];
        var position = _queue.CurrentId ?? Guid.Empty;

        var state = new PlaybackState
        {
            CurrentTrackId = CurrentTrack is not null && !_transientTracks.ContainsKey(CurrentTrack.Id)
                ? CurrentTrack.Id
                : null,
            ShuffleMode = CurrentShuffleMode,
            PlaybackQueueTrackIds = playbackQueue,
            CurrentPlaybackQueueIndex = playbackQueue.IndexOf(position),
            ShuffledQueueTrackIds = shuffledQueue,
            CurrentShuffledQueueIndex = shuffledQueue.IndexOf(position)
        };
        await _playbackStateStore.SaveAsync(state);
    }

    private async Task<bool> RestoreInternalPlaybackStateAsync(PlaybackState state)
    {
        _logger.LogDebug("Attempting to restore previous playback state.");
        var savedQueue = state.PlaybackQueueTrackIds ?? [];
        var savedIds = savedQueue.Where(id => id != Guid.Empty).Distinct().ToList();
        // Songs can leave the library while the app is closed, for example when it is rescanned.
        var librarySongs = await _libraryReader.GetSongsByIdsAsync(savedIds);
        if (_isDisposed) return false;
        if (librarySongs.Count < savedIds.Count)
            _logger.LogWarning("Dropped {Count} songs no longer in the library from the restored queue.",
                savedIds.Count - librarySongs.Count);

        var restoredIds = savedIds.Where(librarySongs.ContainsKey).ToList();
        if (restoredIds.Count == 0) return false;
        CurrentShuffleMode = Enum.IsDefined(state.ShuffleMode) ? state.ShuffleMode : ShuffleMode.Random;

        var savedShuffle = state.ShuffledQueueTrackIds ?? [];
        var restoredShuffle = savedShuffle.Where(librarySongs.ContainsKey).ToList();
        // Equal lengths alone do not rule out duplicate or unrelated tracks.
        var shuffledOrder = restoredShuffle.Count == restoredIds.Count && restoredShuffle.ToHashSet().SetEquals(restoredIds)
            ? restoredShuffle
            : null;
        _queue.Replace(restoredIds, IsShuffleEnabled, startId: null, shuffledOrder);

        if (state.CurrentTrackId is { } trackId)
        {
            var currentSong = _queue.Contains(trackId) ? await _libraryReader.GetSongByIdAsync(trackId) : null;
            if (_isDisposed) return false;
            if (currentSong != null)
            {
                _queue.SetCurrent(trackId);
                CurrentTrack = currentSong;

                var streamUri = await _musicServers.GetStreamUriAsync(currentSong);
                if (_isDisposed) return false;
                // With nothing loaded, Play starts the song from the queue, which stops cleanly while it can't stream.
                if (currentSong.IsFromServer && streamUri is null) CurrentTrack = null;
                else await _audioPlayer.LoadAsync(currentSong, streamUri);
            }
            else
            {
                _logger.LogWarning("Could not restore current track {SongId}: it is missing from the library or saved queue. Falling back to queue start.", trackId);
            }
        }
        else
        {
            // Resolve saved positions by identity because removing duplicates can shift indices.
            var positionId = IsShuffleEnabled ? IdAt(savedShuffle, state.CurrentShuffledQueueIndex) : Guid.Empty;
            if (!_queue.Contains(positionId)) positionId = IdAt(savedQueue, state.CurrentPlaybackQueueIndex);
            _queue.SetCurrent(_queue.Contains(positionId) ? positionId : _queue.Order[0]);
        }

        return true;
    }

    private static Guid IdAt(List<Guid> ids, int index) => index >= 0 && index < ids.Count ? ids[index] : Guid.Empty;
}
