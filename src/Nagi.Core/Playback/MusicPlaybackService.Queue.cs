using Microsoft.Extensions.Logging;
using Nagi.Core.Library;
using Nagi.Core.Metadata;
using Nagi.Core.Settings;

namespace Nagi.Core.Playback;

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

    public Task PlayTransientFileAsync(string filePath) => RunPlaybackRequest(async request =>
    {
        _logger.LogDebug("Playing file: {FilePath}", filePath);
        var songs = await ResolveFilesAsync([filePath], request);
        if (songs is not null)
            await PlayInternalAsync(songs.Select(song => song.Id), 0, null,
                new PlaybackContext(PlaybackContextType.Transient, null), request);
    });

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
        var queueEdit = _queueCts.Token;
        var request = _requestCts.Token;
        var requested = inputPaths?.Where(path => !string.IsNullOrWhiteSpace(path)).ToList() ?? [];
        // Walking a folder can touch many files, so only that goes to the thread pool; plain files
        // stay in call order with other adds.
        var paths = requested.Any(_fileSystem.DirectoryExists)
            ? await Task.Run(() => ExpandToAudioFiles(requested))
            : ExpandToAudioFiles(requested);
        if (paths.Count == 0) return;

        var songs = await ResolveFilesAsync(paths, queueEdit);
        if (songs is null) return;
        _queue.Add(songs.Select(song => song.Id));
        CommitQueueChanges();

        if (CurrentTrack == null && !request.IsCancellationRequested)
        {
            _currentContext = new PlaybackContext(PlaybackContextType.Transient, null);
            await PlayQueueItemAsync(songs[0].Id);
        }
    }

    /// <summary>
    ///     A file in the library plays as its library song, so its listens are recorded; any other file gets a song
    ///     built from its tags, kept only while it is queued.
    /// </summary>
    /// <returns>The songs, or null when cancelled along the way.</returns>
    private async Task<List<Song>?> ResolveFilesAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var songs = new List<Song>(paths.Count);
        foreach (var path in paths)
        {
            if (_transientTracks.TryGetByPath(path, out var knownTransient))
            {
                songs.Add(knownTransient);
                continue;
            }

            var librarySong = await _libraryReader.GetSongByFilePathAsync(path);
            if (cancellationToken.IsCancellationRequested) return null;
            if (librarySong != null)
            {
                songs.Add(librarySong);
                continue;
            }

            var transientSong = await CreateTransientSongAsync(path);
            if (cancellationToken.IsCancellationRequested) return null;
            // Another add may have resolved the same path while metadata was loading.
            if (_transientTracks.TryGetByPath(path, out var existing)) transientSong = existing;
            else _transientTracks.Register(transientSong);
            songs.Add(transientSong);
        }
        return songs;
    }

    private async Task<Song> CreateTransientSongAsync(string filePath)
    {
        var metadata = await _metadataService.ExtractMetadataAsync(filePath);
        // As the library imports them, also for a file whose tags couldn't be read.
        metadata.NormalizeTags(_settingsService.Get(CoreSettings.ArtistSplitCharacters),
            _settingsService.Get(CoreSettings.GenreSplitCharacters));
        var artists = metadata.Artists;
        var albumArtists = metadata.AlbumArtists;

        return new Song
        {
            FilePath = filePath,
            Title = metadata.Title,
            ArtistName = Artist.GetDisplayName(artists),
            PrimaryArtistName = artists[0],
            SongArtists = artists.Select((name, i) => new SongArtist { Artist = new Artist { Name = name }, Order = i }).ToList(),
            Album = new Album
            {
                Title = metadata.Album!,
                ArtistName = Artist.GetDisplayName(albumArtists),
                PrimaryArtistName = albumArtists[0],
                AlbumArtists = albumArtists.Select((name, i) => new AlbumArtist { Artist = new Artist { Name = name }, Order = i }).ToList()
            },
            Duration = metadata.Duration,
            Genres = metadata.Genres.Select(g => new Genre { Name = g }).ToList(),
            AlbumArtUriFromTrack = metadata.CoverArtUri,
            Lyrics = metadata.Lyrics,
            LrcFilePath = metadata.LrcFilePath
        };
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

    private void PruneTransientTracks() => _transientTracks.RetainOnly(_queue.Order.ToHashSet());

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
        RemoveQueuedTracks(songId);
        if (nextSongId is { } next) return RunPlaybackRequest(request => PlayQueueItemInternalAsync(next, request));

        _ = EndListenSession(PlaybackEndReason.Skipped);
        return RunPlaybackRequest(StopInternalAsync);
    }

    private void RemoveQueuedTracks(Guid songId)
    {
        _queue.Remove(new HashSet<Guid> { songId });
        CommitQueueChanges();
    }

    public Task ClearQueueAsync() =>
        RunPlaybackRequest(ClearQueueInternalAsync);

    private async Task ClearQueueInternalAsync(CancellationToken request)
    {
        CancelQueueEdits();
        _logger.LogDebug("Clearing playback queue.");
        await StopInternalAsync(request);
        if (request.IsCancellationRequested) return;

        ClearQueuesInternal();
        CommitQueueChanges();
    }

    private void ClearQueuesInternal()
    {
        CancelQueueEdits();
        _queue.Clear();
        _transientTracks.Clear();
        CurrentTrack = null;
        SetShuffleMode(ShuffleMode.Random, IsShuffleEnabled);
    }
}
