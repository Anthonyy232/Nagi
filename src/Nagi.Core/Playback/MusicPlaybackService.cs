using Microsoft.Extensions.Logging;
using Nagi.Core.Library;
using Nagi.Core.Listening;
using Nagi.Core.Metadata;
using Nagi.Core.MusicServers;
using Nagi.Core.Platform;
using Nagi.Core.Playlists;
using Nagi.Core.Settings;

namespace Nagi.Core.Playback;

public partial class MusicPlaybackService : IMusicPlaybackService
{
    private readonly IAudioPlayer _audioPlayer;
    private readonly ISystemMediaControls _mediaControls;
    private readonly IDispatcherService _dispatcher;
    private readonly ILibraryReader _libraryReader;
    private readonly ISmartPlaylistService _smartPlaylists;
    private readonly IListeningService _listening;
    private readonly ILogger<MusicPlaybackService> _logger;
    private readonly IMetadataService _metadataService;
    private readonly IFileSystemService _fileSystem;
    private readonly IMusicServerService _musicServers;
    private readonly ISettingsService _settingsService;
    private readonly IPlaybackStateStore _playbackStateStore;
    private readonly object _pendingOperationsLock = new();

    // Each playback request cancels the one before it; replacing the queue cancels adds still resolving files.
    private CancellationTokenSource _requestCts = new();
    private CancellationTokenSource _queueCts = new();
    private CancellationTokenSource _replayGainCts = new();
    private TrackGain? _trackGain;
    private bool _isDisposed;
    private bool _isInitialized;
    private Task? _initializationTask;
    private bool _isEligibilityMarked;
    private DateTime? _currentListenStartedUtc;
    private readonly PlaybackQueue _queue = new();
    private readonly TransientTrackCache _transientTracks = new();

    private PlaybackContext _currentContext = new(PlaybackContextType.Library, null);

    private readonly HashSet<Task> _pendingOperations = new();

    public MusicPlaybackService(
        ISettingsService settingsService,
        IPlaybackStateStore playbackStateStore,
        IAudioPlayer audioPlayer,
        ISystemMediaControls mediaControls,
        IDispatcherService dispatcher,
        ILibraryReader libraryReader,
        ISmartPlaylistService smartPlaylists,
        IListeningService listening,
        IMetadataService metadataService,
        IFileSystemService fileSystem,
        IMusicServerService musicServers,
        ILogger<MusicPlaybackService> logger, TimeProvider? timeProvider = null)
    {
        _fileSystem = fileSystem;
        _musicServers = musicServers;
        _settingsService = settingsService;
        _playbackStateStore = playbackStateStore;
        _audioPlayer = audioPlayer;
        _mediaControls = mediaControls;
        _dispatcher = dispatcher;
        _libraryReader = libraryReader;
        _smartPlaylists = smartPlaylists;
        _listening = listening;
        _metadataService = metadataService;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;

        _audioPlayer.PlaybackEnded += OnAudioPlayerPlaybackEnded;
        _audioPlayer.StateChanged += OnAudioPlayerStateChanged;
        _audioPlayer.VolumeChanged += OnAudioPlayerVolumeChanged;
        _audioPlayer.PositionChanged += OnAudioPlayerPositionChanged;
        _audioPlayer.ErrorOccurred += OnAudioPlayerErrorOccurred;
        _audioPlayer.MediaOpened += OnAudioPlayerMediaOpened;
        _audioPlayer.DurationChanged += OnAudioPlayerDurationChanged;
        _mediaControls.PlayPressed += OnMediaControlsPlayPressed;
        _mediaControls.PausePressed += OnMediaControlsPausePressed;
        _mediaControls.NextPressed += OnMediaControlsNextPressed;
        _mediaControls.PreviousPressed += OnMediaControlsPreviousPressed;
        _mediaControls.SeekRequested += OnMediaControlsSeekRequested;

        _settingsService.SettingChanged += OnSettingChanged;
    }

    public Song? CurrentTrack { get; private set; }
    public long? CurrentListenHistoryId { get; private set; }
    public bool IsPlaying => _audioPlayer.IsPlaying;
    public bool IsOpening => _audioPlayer.IsOpening;
    public TimeSpan CurrentPosition => _audioPlayer.CurrentPosition;
    public TimeSpan Duration => _audioPlayer.Duration > TimeSpan.Zero
        ? _audioPlayer.Duration
        : (CurrentTrack?.Duration ?? TimeSpan.Zero);
    public double Volume => _audioPlayer.Volume;
    public bool IsMuted => _audioPlayer.IsMuted;
    public IReadOnlyList<Guid> PlayOrder => _queue.PlayOrder;
    public IReadOnlyList<Guid> UpcomingQueue => _queue.Upcoming(Wraps);
    public Guid? NextTrackId => CurrentTrack is null ? _queue.CurrentId : _queue.GetAdjacent(forward: true, Wraps);
    public bool IsShuffleEnabled => _queue.IsShuffled;
    public RepeatMode CurrentRepeatMode { get; private set; } = RepeatMode.Off;
    public double PlaybackRate { get; private set; } = 1.0;
    private bool _isTransitioningTrack;
    public event Action? TrackChanged;
    public event Action? PlaybackStateChanged;
    public event Action? VolumeStateChanged;
    public event Action? ShuffleModeChanged;
    public event Action? RepeatModeChanged;
    public event Action? QueueChanged;
    public event Action? PositionChanged;
    public event Action? DurationChanged;
    public event Action<Song, long, DateTime>? ScrobbleEligibilityReached;
    public event Action<Song>? ServerSongFailed;

    public Task InitializeAsync(bool restoreLastSession = true)
    {
        if (_isDisposed) return Task.CompletedTask;
        if (_initializationTask != null) return _initializationTask;
        _initializationTask = InitializeCoreAsync(restoreLastSession);
        TrackPendingOperation(_initializationTask);
        return _initializationTask;
    }

    private async Task InitializeCoreAsync(bool restoreLastSession)
    {
        _logger.LogDebug("Initializing MusicPlaybackService...");

        try
        {
            var savedState = await _playbackStateStore.LoadAsync();
            if (_isDisposed) return;

            await _audioPlayer.SetVolumeAsync(_settingsService.Get(CoreSettings.Volume));
            if (_isDisposed) return;
            await _audioPlayer.SetMuteAsync(_settingsService.Get(CoreSettings.IsMuted));
            if (_isDisposed) return;
            _queue.SetShuffled(_settingsService.Get(CoreSettings.IsShuffleEnabled));
            CurrentRepeatMode = _settingsService.Get(CoreSettings.RepeatMode);

            var restoredSuccessfully = false;
            if (restoreLastSession && _settingsService.Get(CoreSettings.RestorePlaybackState) && savedState != null)
                restoredSuccessfully = await RestoreInternalPlaybackStateAsync(savedState);

            if (_isDisposed) return;
            if (!restoredSuccessfully) ClearQueuesInternal();

            _logger.LogDebug("MusicPlaybackService initialized successfully. Session restored: {IsRestored}",
                restoredSuccessfully);
        }
        catch (Exception ex)
        {
            if (_isDisposed) return;
            _logger.LogError(ex, "MusicPlaybackService initialization failed. Using default settings.");
            await _audioPlayer.SetVolumeAsync(CoreSettings.Volume.Default);
            if (_isDisposed) return;
            await _audioPlayer.SetMuteAsync(false);
            if (_isDisposed) return;
            _queue.SetShuffled(false);
            CurrentRepeatMode = RepeatMode.Off;
            ClearQueuesInternal();
        }

        if (_isDisposed) return;
        _isInitialized = true;
        UpdateMediaControlsNavigation();

        VolumeStateChanged?.Invoke();
        ShuffleModeChanged?.Invoke();
        RepeatModeChanged?.Invoke();
        QueueChanged?.Invoke();
        TrackChanged?.Invoke();
        PlaybackStateChanged?.Invoke();
        PositionChanged?.Invoke();
    }

    public Task PlayAsync(Guid songId) => songId == Guid.Empty
        ? Task.CompletedTask
        : PlayAsync([songId]);

    public Task PlayAsync(IEnumerable<Guid> songIds, int startIndex = 0, bool? startShuffled = null, PlaybackContext? context = null) =>
        RunPlaybackRequest(request => PlayInternalAsync(songIds, startIndex, startShuffled, context, request));

    private async Task PlayInternalAsync(IEnumerable<Guid> songIds, int startIndex, bool? startShuffled, PlaybackContext? context, CancellationToken request, IReadOnlyList<Guid>? shuffleOrder = null, ShuffleMode mode = ShuffleMode.Random)
    {
        CancelQueueEdits();
        var requestedIds = songIds?.ToList() ?? [];
        var requestedStartId = startIndex >= 0 && startIndex < requestedIds.Count ? requestedIds[startIndex] : Guid.Empty;
        var idList = requestedIds.Where(id => id != Guid.Empty).Distinct().ToList();
        startIndex = requestedStartId == Guid.Empty ? 0 : idList.IndexOf(requestedStartId);
        if (!idList.Any())
        {
            _logger.LogWarning("PlayAsync called with an empty song list. Stopping playback.");
            await StopInternalAsync(request);
            if (request.IsCancellationRequested) return;
            ClearQueuesInternal();
            QueueChanged?.Invoke();
            UpdateMediaControlsNavigation();
            return;
        }

        _logger.LogDebug(
            "Playing a new queue of {SongCount} songs. Start index: {StartIndex}, Shuffled: {IsShuffled}",
            idList.Count, startIndex, startShuffled);

        _currentContext = context ?? new PlaybackContext(PlaybackContextType.Library, null);
        if (StopMode is PlaybackStopMode.AfterTrack or PlaybackStopMode.EndOfQueue) ScheduleStop(PlaybackStopMode.Off);
        var shuffle = startShuffled ?? IsShuffleEnabled;
        if (shuffle != IsShuffleEnabled) _settingsService.Set(CoreSettings.IsShuffleEnabled, shuffle);

        // A shuffled start lets the shuffle choose the first song; any other start plays the chosen song first.
        _queue.Replace(idList, shuffle, startShuffled == true ? null : idList[startIndex], shuffleOrder);
        CurrentShuffleMode = mode;
        ShuffleModeChanged?.Invoke();
        CommitQueueChanges();

        if (_queue.CurrentId is { } first) await PlayQueueItemInternalAsync(first, request);
    }

    public Task PlayPauseAsync() =>
        RunPlaybackRequest(PlayPauseInternalAsync);

    private async Task PlayPauseInternalAsync(CancellationToken request)
    {
        if (_isTransitioningTrack)
        {
            await StopInternalAsync(request);
            return;
        }

        if (_audioPlayer.IsPlaying)
        {
            await _audioPlayer.PauseAsync();
            return;
        }

        if (CurrentTrack != null)
        {
            if (!CurrentListenHistoryId.HasValue && !_transientTracks.ContainsKey(CurrentTrack.Id))
                await StartListenSessionAsync(CurrentTrack.Id, request);
            if (request.IsCancellationRequested) return;
            await ApplyReplayGainIfEnabledAsync();
            if (request.IsCancellationRequested) return;
            await _audioPlayer.PlayAsync();
            return;
        }

        var resumeId = _queue.CurrentId ?? _queue.PlayOrder.FirstOrDefault();
        if (resumeId != Guid.Empty) await PlayQueueItemInternalAsync(resumeId, request);
    }

    internal Task StopAsync() =>
        RunPlaybackRequest(request => StopInternalAsync(request));

    private async Task StopInternalAsync(CancellationToken request, bool clearStopSchedule = true)
    {
        if (clearStopSchedule) ScheduleStop(PlaybackStopMode.Off);
        _ = EndListenSession(PlaybackEndReason.PausedAndAbandoned);

        await _audioPlayer.StopAsync();
        if (request.IsCancellationRequested) return;

        // The queue keeps its position, so Play, Next and Previous carry on from the stopped song.
        CurrentTrack = null;
        _isTransitioningTrack = false;
        TrackChanged?.Invoke();
        PositionChanged?.Invoke();
        _mediaControls.Clear();
        UpdateMediaControlsNavigation();
    }

    public Task NextAsync() =>
        RunPlaybackRequest(request => PlayAdjacentAsync(forward: true, request));

    public Task PreviousAsync() =>
        RunPlaybackRequest(PreviousInternalAsync);

    private async Task PreviousInternalAsync(CancellationToken request)
    {
        if (CurrentTrack != null && _audioPlayer.CurrentPosition.TotalSeconds > 3)
        {
            await SeekAsync(TimeSpan.Zero);
            if (request.IsCancellationRequested) return;
            if (!_audioPlayer.IsPlaying) await _audioPlayer.PlayAsync();
            return;
        }

        await PlayAdjacentAsync(forward: false, request);
    }

    // While stopped, the song at the current position plays again rather than the one beside it.
    private async Task PlayAdjacentAsync(bool forward, CancellationToken request)
    {
        var songId = CurrentTrack is null ? _queue.CurrentId : _queue.GetAdjacent(forward, Wraps);
        if (songId is { } id) await PlayQueueItemInternalAsync(id, request);
        else if (CurrentTrack != null) await StopInternalAsync(request);
    }

    private async Task AdvanceAfterTrackEndAsync(CancellationToken request)
    {
        if (ShouldStopAfterTrack())
        {
            await StopInternalAsync(request);
            return;
        }

        var nextId = CurrentRepeatMode == RepeatMode.RepeatOne
            ? CurrentTrack?.Id
            : _queue.GetAdjacent(forward: true, wrap: CurrentRepeatMode == RepeatMode.RepeatAll);
        if (nextId is { } id) await PlayQueueItemInternalAsync(id, request);
        else await StopInternalAsync(request);
    }

    public async Task SeekAsync(TimeSpan position)
    {
        if (CurrentTrack == null) return;
        var request = _requestCts.Token;
        await _audioPlayer.SeekAsync(position);
        // The player applies a seek asynchronously, so its own position can still be the old one here.
        if (!request.IsCancellationRequested) UpdateMediaControlsTimeline(position);
    }

    public Task PlayAlbumAsync(Guid albumId) => RunPlaybackRequest(async request =>
    {
        var songIds = await _libraryReader.GetSongIdsAsync(new SongScope.Album(albumId), null, SongSortOrder.TrackNumberAsc);
        if (!request.IsCancellationRequested)
            await PlayInternalAsync(songIds, 0, null, new PlaybackContext(PlaybackContextType.Album, albumId), request);
    });

    public Task PlayArtistAsync(Guid artistId) => RunPlaybackRequest(async request =>
    {
        var songIds = await _libraryReader.GetSongIdsAsync(new SongScope.Artist(artistId), null, SongSortOrder.TitleAsc);
        if (!request.IsCancellationRequested)
            await PlayInternalAsync(songIds, 0, null, new PlaybackContext(PlaybackContextType.Artist, artistId), request);
    });

    public Task PlayFolderAsync(Guid folderId) => RunPlaybackRequest(async request =>
    {
        var songIds = await _libraryReader.GetSongIdsAsync(new SongScope.Folder(folderId), null, SongSortOrder.TitleAsc);
        if (!request.IsCancellationRequested)
            await PlayInternalAsync(songIds, 0, null, new PlaybackContext(PlaybackContextType.Folder, folderId), request);
    });

    public Task PlayPlaylistAsync(Guid playlistId) => RunPlaybackRequest(async request =>
    {
        var songIds = await _libraryReader.GetSongIdsAsync(new SongScope.Playlist(playlistId), null, SongSortOrder.PlaylistOrder);
        if (!request.IsCancellationRequested)
            await PlayInternalAsync(songIds, 0, null, new PlaybackContext(PlaybackContextType.Playlist, playlistId), request);
    });

    public async Task<bool> PlaySmartPlaylistAsync(Guid smartPlaylistId)
    {
        var played = false;
        await RunPlaybackRequest(async request =>
        {
            var songIds = await _smartPlaylists.GetMatchingSongIdsAsync(smartPlaylistId, token: request);
            if (songIds.Count == 0 || request.IsCancellationRequested) return;
            await PlayInternalAsync(songIds, 0, null, new PlaybackContext(PlaybackContextType.SmartPlaylist, smartPlaylistId), request);
            played = true;
        });
        return played;
    }

    public Task PlayGenreAsync(Guid genreId) => RunPlaybackRequest(async request =>
    {
        var songIds = await _libraryReader.GetSongIdsAsync(new SongScope.Genre(genreId), null, SongSortOrder.TitleAsc);
        if (!request.IsCancellationRequested)
            await PlayInternalAsync(songIds, 0, null, new PlaybackContext(PlaybackContextType.Genre, genreId), request);
    });

    public Task ShuffleLibraryAsync() => RunPlaybackRequest(async request =>
    {
        var songIds = await _libraryReader.GetSongIdsAsync(SongScope.Library, null, SongSortOrder.TitleAsc);
        if (!request.IsCancellationRequested)
            await PlayInternalAsync(songIds, 0, true, new PlaybackContext(PlaybackContextType.Library, null), request);
    });

    public Task SetVolumeAsync(double volume) => _audioPlayer.SetVolumeAsync(volume);

    public Task ToggleMuteAsync() => _audioPlayer.SetMuteAsync(!_audioPlayer.IsMuted);

    public async Task SetPlaybackRateAsync(double rate)
    {
        if (!double.IsFinite(rate) || rate <= 0) return;
        await _audioPlayer.SetPlaybackRateAsync(rate);
        PlaybackRate = rate;
    }

    public Task SetShuffleAsync(bool enable)
    {
        if (IsShuffleEnabled == enable) return Task.CompletedTask;

        _queue.SetShuffled(enable);
        CurrentShuffleMode = ShuffleMode.Random;
        _logger.LogDebug("Shuffle mode set to {ShuffleState}", IsShuffleEnabled);
        CommitQueueChanges();

        _settingsService.Set(CoreSettings.IsShuffleEnabled, IsShuffleEnabled);
        ShuffleModeChanged?.Invoke();
        return Task.CompletedTask;
    }

    public Task SetRepeatModeAsync(RepeatMode mode)
    {
        if (CurrentRepeatMode == mode) return Task.CompletedTask;

        CurrentRepeatMode = mode;
        _logger.LogDebug("Repeat mode set to {RepeatMode}", CurrentRepeatMode);
        _settingsService.Set(CoreSettings.RepeatMode, CurrentRepeatMode);
        RepeatModeChanged?.Invoke();
        UpdateMediaControlsNavigation();
        return Task.CompletedTask;
    }

    public Task PlayQueueItemAsync(Guid songId) =>
        RunPlaybackRequest(request => PlayQueueItemInternalAsync(songId, request));

    private async Task PlayQueueItemInternalAsync(Guid songId, CancellationToken request)
    {
        if (!_queue.Contains(songId))
        {
            await StopInternalAsync(request);
            return;
        }

        _ = EndListenSession(PlaybackEndReason.Skipped);
        _isEligibilityMarked = false;
        _isTransitioningTrack = true;

        // Songs deleted from disk can still be queued; give up after a run of them rather than walk the whole queue.
        const int maxSkipAttempts = 20;
        Guid? candidate = songId;
        var skipCount = 0;
        Song? song = null;

        while (skipCount < maxSkipAttempts && candidate is { } id)
        {
            _queue.SetCurrent(id);
            song = _transientTracks.GetValueOrDefault(id) ?? await _libraryReader.GetSongByIdAsync(id);
            if (request.IsCancellationRequested) return;
            if (!_queue.Contains(id))
            {
                await StopInternalAsync(request);
                return;
            }

            if (song != null) break;

            _logger.LogWarning("Song metadata not found for ID {SongId}. Skipping to next track. (Skip {SkipCount}/{MaxSkip})",
                id, skipCount + 1, maxSkipAttempts);
            skipCount++;
            candidate = _queue.GetAdjacent(forward: true, Wraps);
        }

        if (song == null)
        {
            _logger.LogError("Failed to find playable track after skipping {SkipCount} deleted songs. Stopping playback.", skipCount);
            await StopInternalAsync(request);
            return;
        }

        // Before the listen session starts, so a server song that can't stream isn't credited with any listening time.
        var streamUri = await _musicServers.GetStreamUriAsync(song);
        if (request.IsCancellationRequested) return;
        if (song.IsFromServer && streamUri is null)
        {
            await StopInternalAsync(request);
            ServerSongFailed?.Invoke(song);
            return;
        }

        if (!_transientTracks.ContainsKey(song.Id)) await StartListenSessionAsync(song.Id, request);
        if (request.IsCancellationRequested) return;

        // After the listen starts, so CurrentListenHistoryId already belongs to the new track when listeners hear of it.
        CurrentTrack = song;
        TrackChanged?.Invoke();
        _logger.LogDebug("Now playing '{SongTitle}' (position {Position} of {Count})",
            song.Title, _queue.CurrentPosition, _queue.Count);

        await _audioPlayer.LoadAsync(song, streamUri);
        if (request.IsCancellationRequested) return;
        await ApplyReplayGainIfEnabledAsync();
        if (!request.IsCancellationRequested) await _audioPlayer.PlayAsync();
    }

    private async Task StartListenSessionAsync(Guid songId, CancellationToken request)
    {
        var startedUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var sessionId = await _listening.StartListenSessionAsync(songId, _currentContext);
        if (request.IsCancellationRequested)
        {
            if (sessionId.HasValue)
                await _listening.FinalizeListenSessionAsync(sessionId.Value, TimeSpan.Zero, PlaybackEndReason.PausedAndAbandoned);
            return;
        }

        CurrentListenHistoryId = sessionId;
        _currentListenStartedUtc = sessionId.HasValue ? startedUtc : null;
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
        _currentListenStartedUtc = null;
        return RunInBackground(() => _listening.FinalizeListenSessionAsync(sessionId, listened, reason),
            "Finalizing a listen");
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();

        // Loads still pending can start listens, which have to be ended before the database goes away.
        try
        {
            await AwaitPendingOperationsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A pending playback request failed during disposal.");
        }

        await EndListenSession(PlaybackEndReason.PausedAndAbandoned);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _stopTimer?.Dispose();
        _requestCts.Cancel();
        _queueCts.Cancel();
        _replayGainCts.Cancel();

        _audioPlayer.PlaybackEnded -= OnAudioPlayerPlaybackEnded;
        _audioPlayer.StateChanged -= OnAudioPlayerStateChanged;
        _audioPlayer.VolumeChanged -= OnAudioPlayerVolumeChanged;
        _audioPlayer.PositionChanged -= OnAudioPlayerPositionChanged;
        _audioPlayer.ErrorOccurred -= OnAudioPlayerErrorOccurred;
        _audioPlayer.MediaOpened -= OnAudioPlayerMediaOpened;
        _audioPlayer.DurationChanged -= OnAudioPlayerDurationChanged;
        _mediaControls.PlayPressed -= OnMediaControlsPlayPressed;
        _mediaControls.PausePressed -= OnMediaControlsPausePressed;
        _mediaControls.NextPressed -= OnMediaControlsNextPressed;
        _mediaControls.PreviousPressed -= OnMediaControlsPreviousPressed;
        _mediaControls.SeekRequested -= OnMediaControlsSeekRequested;
        _settingsService.SettingChanged -= OnSettingChanged;
    }

    public bool CanGoNext => _queue.Count > 0 &&
                             (CurrentRepeatMode != RepeatMode.Off || _queue.CurrentPosition < _queue.Count - 1);

    // A playing track can always go back, to its start or to the track before it.
    public bool CanGoPrevious => _queue.Count > 0 &&
                                 (CurrentRepeatMode != RepeatMode.Off || CurrentTrack != null || _queue.CurrentPosition > 0);

    // Either repeat mode makes skipping go round the queue.
    private bool Wraps => CurrentRepeatMode != RepeatMode.Off;

    private void UpdateMediaControlsNavigation() => _mediaControls.SetNavigationEnabled(CanGoNext, CanGoPrevious);

    private void UpdateMediaControlsStatus()
    {
        if (CurrentTrack is null) return;
        _mediaControls.SetPlaybackStatus(IsOpening ? SystemMediaPlaybackStatus.Changing
            : IsPlaying ? SystemMediaPlaybackStatus.Playing
            : SystemMediaPlaybackStatus.Paused);
    }

    // Stopping clears the media controls, so they only follow a loaded track.
    private void UpdateMediaControlsTimeline(TimeSpan position)
    {
        if (CurrentTrack != null) _mediaControls.SetTimeline(position, Duration);
    }

    private Task RunPlaybackRequest(Func<CancellationToken, Task> action)
    {
        if (_isDisposed) return Task.CompletedTask;
        _requestCts.Cancel();
        _requestCts = new CancellationTokenSource();
        var request = _requestCts.Token;
        var task = ExecuteAsync();
        TrackPendingOperation(task);
        return task;

        async Task ExecuteAsync()
        {
            try
            {
                await action(request);
            }
            finally
            {
                if (!request.IsCancellationRequested) _isTransitioningTrack = false;
            }
        }
    }

    private void CancelQueueEdits()
    {
        _queueCts.Cancel();
        _queueCts = new CancellationTokenSource();
    }

    /// <summary>Runs work nobody waits for, logging its failure. Disposal waits for it.</summary>
    private Task RunInBackground(Func<Task> asyncAction, string operationName)
    {
        var task = ExecuteAsync();
        TrackPendingOperation(task);
        return task;

        async Task ExecuteAsync()
        {
            try
            {
                await asyncAction();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in background operation: {Operation}", operationName);
            }
        }
    }

    private void TrackPendingOperation(Task task)
    {
        lock (_pendingOperationsLock)
        {
            _pendingOperations.Add(task);
        }

        _ = task.ContinueWith(
            _ =>
            {
                lock (_pendingOperationsLock)
                {
                    _pendingOperations.Remove(task);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private Task AwaitPendingOperationsAsync()
    {
        Task[] pending;
        lock (_pendingOperationsLock)
        {
            if (_pendingOperations.Count == 0)
                return Task.CompletedTask;

            pending = _pendingOperations.ToArray();
        }

        return Task.WhenAll(pending);
    }

    internal Task FlushPendingFinalizationAsync() => AwaitPendingOperationsAsync();
}
