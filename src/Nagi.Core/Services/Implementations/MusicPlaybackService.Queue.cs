using Microsoft.Extensions.Logging;
using Nagi.Core.Constants;
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
        PruneTransientTracks();
        QueueChanged?.Invoke();
        UpdateMediaControlsNavigation();
    }

    public Task AddTransientFilesToQueueAsync(IEnumerable<string> paths)
    {
        if (_isDisposed) return Task.CompletedTask;
        var task = AddTransientFilesInternalAsync(paths);
        TrackPendingOperation(task);
        return task;
    }

    private List<string> ExpandToAudioFiles(IEnumerable<string> paths) => paths
        .SelectMany(path => _fileSystem.DirectoryExists(path)
            ? _fileSystem.EnumerateFiles(path, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase)
            : (IEnumerable<string>)[path])
        .Where(file => FileExtensions.MusicFileExtensions.Contains(Path.GetExtension(file)))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private async Task AddTransientFilesInternalAsync(IEnumerable<string> inputPaths)
    {
        var queueVersion = _queueVersion;
        var request = _playbackRequestVersion;
        var requested = inputPaths?.Where(path => !string.IsNullOrWhiteSpace(path)).ToList() ?? [];
        // Walking a folder can touch many files, so only that goes to the thread pool; plain files
        // stay in call order with other adds.
        var paths = requested.Any(_fileSystem.DirectoryExists)
            ? await Task.Run(() => ExpandToAudioFiles(requested))
            : ExpandToAudioFiles(requested);
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

        if (_queue.Count == 0 && CurrentTrack != null)
        {
            _queue.Add([CurrentTrack.Id]);
            _queue.SetCurrent(CurrentTrack.Id);
        }
        var added = new List<Guid>(songs.Count);
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
            added.Add(song.Id);
        }
        _queue.Add(added);
        CommitQueueChanges();

        if (CurrentTrack == null && added.Count > 0 && IsCurrentRequest(request))
        {
            _currentContext = new PlaybackContext(PlaybackContextType.Transient, null);
            await PlayQueueItemAsync(added[0]);
        }
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

    public bool MoveQueueItem(Guid songId, Guid? beforeSongId)
    {
        var current = _queue.CurrentId;
        if (songId == current || (beforeSongId is { } before && before == current)) return false;
        // While the upcoming queue wraps around, its end is just before the current song.
        if (beforeSongId is null && Wraps && _queue.CurrentPosition > 0) beforeSongId = current;
        if (!_queue.Move(songId, beforeSongId)) return false;
        CommitQueueChanges();
        return true;
    }

    private void PruneTransientTracks()
    {
        var retainedIds = _queue.Order.ToHashSet();
        if (CurrentTrack != null) retainedIds.Add(CurrentTrack.Id);
        _transientTracks.RetainOnly(retainedIds);
    }

    public Task AddRangeToQueueAsync(IEnumerable<Guid> songIds)
    {
        if (_queue.Add(songIds)) CommitQueueChanges();
        return Task.CompletedTask;
    }

    public Task PlayNextAsync(IEnumerable<Guid> songIds)
    {
        _queue.MoveAfterCurrent(songIds.ToList());
        CommitQueueChanges();
        return Task.CompletedTask;
    }

    public Task RemoveFromQueueAsync(Guid songId)
    {
        if (!_queue.Contains(songId)) return Task.CompletedTask;
        if (CurrentTrack?.Id != songId)
        {
            RemoveQueuedTracks(songId);
            return Task.CompletedTask;
        }

        var nextSongId = _queue.FindFromCurrent(id => id != songId, Wraps);
        return RunPlaybackRequest(async request =>
        {
            FinalizeCurrentListenBeforeQueueRemoval();
            // Playback carries on with the next song, so a sleep timer or "stop at queue end" still applies.
            await StopInternalAsync(request, clearStopSchedule: !nextSongId.HasValue);
            if (!IsCurrentRequest(request)) return;
            RemoveQueuedTracks(songId);
            if (nextSongId is { } next && _queue.Contains(next) && IsCurrentRequest(request))
                await PlayQueueItemInternalAsync(next, request);
        });
    }

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

    private void RemoveQueuedTracks(Guid songId)
    {
        _queue.Remove(new HashSet<Guid> { songId });
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
        CurrentListenHistoryId = null;
        _currentListenStartedUtc = null;
        CurrentShuffleMode = ShuffleMode.Random;
    }
}
