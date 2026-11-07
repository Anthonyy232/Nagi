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

        var position = _queue.CurrentId is { } id && !_transientTracks.ContainsKey(id) ? id : (Guid?)null;
        var state = new PlaybackState
        {
            PlaybackQueueTrackIds = _queue.Order.Where(songId => !_transientTracks.ContainsKey(songId)).ToList(),
            ShuffledQueueTrackIds = IsShuffleEnabled
                ? _queue.PlayOrder.Where(songId => !_transientTracks.ContainsKey(songId)).ToList()
                : [],
            ShuffleMode = CurrentShuffleMode,
            PositionId = position,
            IsLoaded = position is not null && CurrentTrack?.Id == position
        };
        await _playbackStateStore.SaveAsync(state);
    }

    private async Task<bool> RestoreInternalPlaybackStateAsync(PlaybackState state, bool shuffle)
    {
        _logger.LogDebug("Attempting to restore previous playback state.");
        var savedIds = (state.PlaybackQueueTrackIds ?? []).Where(id => id != Guid.Empty).Distinct().ToList();
        // Songs can leave the library while the app is closed, for example when it is rescanned.
        var librarySongs = await _libraryReader.GetSongsByIdsAsync(savedIds);
        if (_isDisposed) return false;
        if (librarySongs.Count < savedIds.Count)
            _logger.LogWarning("Dropped {Count} songs no longer in the library from the restored queue.",
                savedIds.Count - librarySongs.Count);

        var restoredIds = savedIds.Where(librarySongs.ContainsKey).ToList();
        if (restoredIds.Count == 0) return false;
        CurrentShuffleMode = Enum.IsDefined(state.ShuffleMode) ? state.ShuffleMode : ShuffleMode.Random;

        var restoredShuffle = (state.ShuffledQueueTrackIds ?? []).Where(librarySongs.ContainsKey).ToList();
        // Equal lengths alone do not rule out duplicate or unrelated tracks.
        var shuffledOrder = restoredShuffle.Count == restoredIds.Count && restoredShuffle.ToHashSet().SetEquals(restoredIds)
            ? restoredShuffle
            : null;
        _queue.Replace(restoredIds, shuffle, startId: null, shuffledOrder);

        if (state.PositionId is not { } positionId || !_queue.Contains(positionId)) return true;
        _queue.SetCurrent(positionId);
        if (!state.IsLoaded) return true;

        var song = await _libraryReader.GetSongByIdAsync(positionId);
        if (_isDisposed) return false;
        if (song is null) return true;
        var streamUri = await _musicServers.GetStreamUriAsync(song);
        if (_isDisposed) return false;
        // With nothing loaded, Play starts the song from the queue, which stops cleanly while it can't stream.
        if (song.IsFromServer && streamUri is null) return true;
        CurrentTrack = song;
        await _audioPlayer.LoadAsync(song, streamUri);
        return true;
    }
}
