using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Queue editing: adding, reordering and removing songs, files from outside the library, and queue lookups.
/// </summary>
public partial class MusicPlaybackService
{
    private void CommitQueueChanges()
    {
        _logger.LogTrace("Committing queue changes and rebuilding indices.");
        _queue.RebuildIndex();
        if (IsShuffleEnabled) _queue.SyncShuffledWithOrder();

        if (CurrentTrack != null)
        {
            CurrentQueueIndex = _queue.IndexOf(CurrentTrack.Id);
            CurrentShuffledIndex = IsShuffleEnabled ? _queue.ShuffledIndexOf(CurrentTrack.Id) : -1;
        }
        else
        {
            CurrentQueueIndex = Math.Clamp(CurrentQueueIndex, -1, _queue.Order.Count - 1);
            CurrentShuffledIndex = IsShuffleEnabled
                ? Math.Clamp(CurrentShuffledIndex, -1, _queue.Shuffled.Count - 1)
                : -1;
        }

        PruneTransientTracks();
        QueueChanged?.Invoke();
        UpdateMediaControlsNavigation();

    }

    public Task AddTransientFilesToQueueAsync(IEnumerable<string> filePaths)
    {
        if (_isDisposed) return Task.CompletedTask;
        var task = AddTransientFilesInternalAsync(filePaths);
        TrackPendingOperation(task);
        return task;
    }

    private async Task AddTransientFilesInternalAsync(IEnumerable<string> filePaths)
    {
        var queueVersion = _queueVersion;
        var request = _playbackRequestVersion;
        var paths = filePaths?
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
        if (paths.Count == 0) return;

        var songs = new List<(Song Song, bool Transient)>(paths.Count);
        foreach (var path in paths)
        {
            if (_transientTracks.TryGetByPath(path, out var knownTransient))
            {
                songs.Add((knownTransient, true));
                continue;
            }

            var librarySong = await _libraryReader.GetSongByFilePathAsync(path);
            if (_isDisposed || queueVersion != _queueVersion) return;
            if (librarySong != null)
            {
                songs.Add((librarySong, false));
                continue;
            }

            var transientSong = await CreateTransientSongAsync(path);
            if (_isDisposed || queueVersion != _queueVersion) return;
            songs.Add((transientSong, true));
        }

        if (_queue.Order.Count == 0 && CurrentTrack != null) _queue.Add(CurrentTrack.Id);
        var queuedIds = _queue.Order.ToHashSet();
        foreach (var item in songs)
        {
            var song = item.Song;
            if (item.Transient)
            {
                // Another add may have resolved the same path while metadata was loading.
                if (_transientTracks.TryGetByPath(song.FilePath, out var existing))
                    song = existing;
                else
                    _transientTracks.Register(song);
            }
            if (queuedIds.Add(song.Id)) _queue.Add(song.Id);
        }
        CommitQueueChanges();

        if (CurrentTrack == null && _queue.Order.Count > 0 && IsCurrentRequest(request))
        {
            _currentContext = new PlaybackContext(PlaybackContextType.Transient, null);
            await PlayQueueItemAsync(0);
        }
    }

    public async Task<IReadOnlyDictionary<Guid, Song>> GetQueueTracksAsync(IEnumerable<Guid> songIds)
    {
        var ids = songIds?.Distinct().ToList() ?? [];
        var result = new Dictionary<Guid, Song>(ids.Count);
        var libraryIds = new List<Guid>(ids.Count);

        foreach (var id in ids)
        {
            if (_transientTracks.TryGetValue(id, out var transientTrack))
                result[id] = transientTrack;
            else
                libraryIds.Add(id);
        }

        if (libraryIds.Count > 0)
        {
            var libraryTracks = await _libraryReader.GetSongsByIdsAsync(libraryIds);
            foreach (var pair in libraryTracks) result[pair.Key] = pair.Value;
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<Guid, Song>> GetQueueSongsAsync(IEnumerable<Guid> songIds,
        CancellationToken cancellationToken = default)
    {
        var ids = songIds.Distinct().ToArray();
        var transient = ids.Where(_transientTracks.ContainsKey).ToDictionary(id => id, id => _transientTracks[id]);
        var songs = await Task.Run(() => _libraryReader.GetQueueSongsAsync(
            ids.Where(id => !transient.ContainsKey(id)), cancellationToken), cancellationToken);
        foreach (var pair in songs) transient[pair.Key] = pair.Value;
        return transient;
    }

    public async Task<TimeSpan> GetQueueDurationAsync(IEnumerable<Guid> songIds,
        CancellationToken cancellationToken = default)
    {
        var libraryIds = new List<Guid>();
        long transientTicks = 0;
        foreach (var id in songIds.Distinct())
        {
            if (_transientTracks.TryGetValue(id, out var song)) transientTicks += song.DurationTicks;
            else libraryIds.Add(id);
        }
        var duration = await Task.Run(() => _libraryReader.GetSongsDurationAsync(libraryIds, cancellationToken),
            cancellationToken);
        return duration + TimeSpan.FromTicks(transientTicks);
    }

    public void MoveQueueItem(Guid songId, Guid? beforeSongId)
    {
        if (_queue.Move(songId, beforeSongId, IsShuffleEnabled)) CommitQueueChanges();
    }

    private void PruneTransientTracks()
    {
        var retainedIds = _queue.Order.ToHashSet();
        if (CurrentTrack != null) retainedIds.Add(CurrentTrack.Id);
        _transientTracks.RetainOnly(retainedIds);
    }

    public Task AddToQueueAsync(Song song)
    {
        return AddToQueueAsync(song?.Id ?? Guid.Empty);
    }

    public Task AddToQueueAsync(Guid songId)
    {
        if (songId == Guid.Empty || _queue.IndexOf(songId) >= 0) return Task.CompletedTask;

        _queue.Add(songId);
        CommitQueueChanges();

        return Task.CompletedTask;
    }

    public Task AddRangeToQueueAsync(IEnumerable<Song> songs)
    {
        return AddRangeToQueueAsync(songs?.Select(s => s.Id) ?? Enumerable.Empty<Guid>());
    }

    public Task AddRangeToQueueAsync(IEnumerable<Guid> songIds)
    {
        if (songIds == null) return Task.CompletedTask;

        var queuedIds = _queue.Order.ToHashSet();
        var idsToAdd = songIds
            .Where(id => id != Guid.Empty && queuedIds.Add(id))
            .ToList();

        if (idsToAdd.Count > 0)
        {
            _queue.AddRange(idsToAdd);
            CommitQueueChanges();
        }

        return Task.CompletedTask;
    }

    public Task PlayNextAsync(Song song)
    {
        return PlayNextAsync(song?.Id ?? Guid.Empty);
    }

    public Task PlayNextAsync(Guid songId)
    {
        if (songId == Guid.Empty) return Task.CompletedTask;

        _queue.MoveAfterCurrent(songId, CurrentTrack?.Id, CurrentQueueIndex, inShuffled: false);
        if (IsShuffleEnabled) _queue.MoveAfterCurrent(songId, CurrentTrack?.Id, CurrentShuffledIndex, inShuffled: true);
        CommitQueueChanges();

        return Task.CompletedTask;
    }

    public Task RemoveFromQueueAsync(Song song)
    {
        return RemoveFromQueueAsync(song?.Id ?? Guid.Empty);
    }

    public Task RemoveFromQueueAsync(Guid songId) =>
        _queue.IndexOf(songId) < 0 ? Task.CompletedTask : RemoveRangeFromQueueAsync([songId]);

    private void FinalizeCurrentListenBeforeQueueRemoval()
    {
        if (!CurrentListenHistoryId.HasValue) return;

        // LibVLC resets its reported time when Stop is called, so capture the listen state
        // synchronously before the queue-removal path stops the underlying player.
        var sessionId = CurrentListenHistoryId.Value;
        var position = _audioPlayer.CurrentPosition;
        CurrentListenHistoryId = null;
        FireAndForgetSafe(
            () => _libraryWriter.FinalizeListenSessionAsync(sessionId, position, PlaybackEndReason.Skipped),
            "Finalizing session removed from queue",
            trackForDisposal: true);
    }

    private Guid? GetNextSurvivingSongId(HashSet<Guid> idsToRemove) =>
        _queue.GetNextSurvivingSongId(idsToRemove, IsShuffleEnabled ? CurrentShuffledIndex : CurrentQueueIndex,
            IsShuffleEnabled, CurrentRepeatMode == RepeatMode.RepeatAll);

    public Task RemoveRangeFromQueueAsync(IEnumerable<Guid> songIds)
    {
        var idsToRemove = songIds?.ToHashSet() ?? [];
        if (idsToRemove.Count == 0) return Task.CompletedTask;
        if (CurrentTrack == null || !idsToRemove.Contains(CurrentTrack.Id))
        {
            RemoveQueuedTracks(idsToRemove);
            return Task.CompletedTask;
        }

        var nextSongId = GetNextSurvivingSongId(idsToRemove);
        return RunPlaybackRequest(async request =>
        {
            FinalizeCurrentListenBeforeQueueRemoval();
            await StopInternalAsync(request);
            if (!IsCurrentRequest(request)) return;
            RemoveQueuedTracks(idsToRemove);
            if (nextSongId.HasValue && IsCurrentRequest(request))
            {
                var index = _queue.IndexOf(nextSongId.Value);
                if (index >= 0) await PlayQueueItemInternalAsync(index, request);
            }
        });
    }

    private void RemoveQueuedTracks(HashSet<Guid> idsToRemove)
    {
        _queue.RemoveAll(idsToRemove);
        CommitQueueChanges();
    }

    public Task ClearQueueAsync() =>
        RunPlaybackRequest(ClearQueueInternalAsync);

    private async Task ClearQueueInternalAsync(int request)
    {
        _queueVersion++;
        _logger.LogDebug("Clearing playback queue.");
        await StopInternalAsync(request);
        if (!IsCurrentRequest(request)) return;

        ClearQueuesInternal();
        CommitQueueChanges();
    }

    private void ClearQueuesInternal()
    {
        _queueVersion++;
        _queue.Clear();
        _transientTracks.Clear();
        CurrentTrack = null;
        CurrentQueueIndex = -1;
        CurrentShuffledIndex = -1;
        CurrentListenHistoryId = null;
        _currentListenStartedUtc = null;
        CurrentShuffleMode = ShuffleMode.Random;
    }

    private int GetAdjacentTrackIndex(int originalIndex, bool moveForward) =>
        _queue.GetAdjacentIndex(originalIndex, moveForward, IsShuffleEnabled, CurrentRepeatMode == RepeatMode.RepeatAll);
}
