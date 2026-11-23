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
    private readonly ILibraryReader _libraryReader;
    private readonly ISmartPlaylistService _smartPlaylists;
    private readonly IListeningService _listening;
    private readonly ILogger<MusicPlaybackService> _logger;
    private readonly IMetadataService _metadataService;
    private readonly IFileSystemService _fileSystem;
    private readonly IMusicServerService _musicServers;
    private readonly ISettingsService _settingsService;
    private readonly IPlaybackStateStore _playbackStateStore;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _pendingOperationsLock = new();

    // Each playback request cancels the one before it; replacing the queue cancels adds still resolving files.
    private CancellationTokenSource _requestCts = new();
    private Task _request = Task.CompletedTask;
    private CancellationTokenSource _queueCts = new();
    private CancellationTokenSource _replayGainCts = new();
    private TrackGain? _trackGain;
    private bool _isDisposed;
    private bool _isInitialized;
    private Task? _initializationTask;
    private PlaybackQueue _queue = new();
    private readonly TransientTrackCache _transientTracks = new();

    private PlaybackContext _currentContext = new(PlaybackContextType.Library, null);

    private readonly HashSet<Task> _pendingOperations = new();

    public MusicPlaybackService(
        ISettingsService settingsService,
        IPlaybackStateStore playbackStateStore,
        IAudioPlayer audioPlayer,
        ISystemMediaControls mediaControls,
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
    // A stop clears the track before the player unloads it, so the player's values only count while there is a track.
    public TimeSpan CurrentPosition => CurrentTrack is null ? TimeSpan.Zero : _audioPlayer.CurrentPosition;
    public TimeSpan Duration => CurrentTrack is null ? TimeSpan.Zero
        : _audioPlayer.Duration > TimeSpan.Zero ? _audioPlayer.Duration : CurrentTrack.Duration;
    public double Volume => _audioPlayer.Volume;
    public bool IsMuted => _audioPlayer.IsMuted;
    public IReadOnlyList<Guid> PlayOrder => _queue.PlayOrder;
    public IReadOnlyList<Guid> UpcomingQueue => _queue.Upcoming(Wraps);
    public Guid? NextTrackId => AdjacentId(forward: true);
    public bool IsShuffleEnabled => _queue.IsShuffled;
    public RepeatMode CurrentRepeatMode { get; private set; } = RepeatMode.Off;
    public double PlaybackRate { get; private set; } = 1.0;
    public event Action? TrackChanged;
    public event Action? PlaybackStateChanged;
    public event Action? VolumeStateChanged;
    public event Action? ShuffleModeChanged;
    public event Action? RepeatModeChanged;
    public event Action? QueueChanged;
    public event Action? PositionChanged;
    public event Action? DurationChanged;
    public event Action? ScrobbleEligibilityReached;
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

        var savedState = await _playbackStateStore.LoadAsync();
        if (_isDisposed) return;

        if (_settingsService.Get(CoreSettings.LegacyVolume) is { } legacyVolume)
        {
            _settingsService.Set(CoreSettings.Volume, Math.Pow(legacyVolume, 3));
            _settingsService.Set(CoreSettings.LegacyVolume, null);
        }

        try
        {
            await _audioPlayer.SetVolumeAsync(_settingsService.Get(CoreSettings.Volume));
            if (_isDisposed) return;
            await _audioPlayer.SetMuteAsync(_settingsService.Get(CoreSettings.IsMuted));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The audio player failed to start, so nothing can play.");
        }

        if (_isDisposed) return;
        var shuffle = _settingsService.Get(CoreSettings.IsShuffleEnabled);
        CurrentRepeatMode = _settingsService.Get(CoreSettings.RepeatMode);

        var restoredSuccessfully = false;
        if (restoreLastSession && _settingsService.Get(CoreSettings.RestorePlaybackState) && savedState != null)
        {
            try
            {
                restoredSuccessfully = await RestoreInternalPlaybackStateAsync(savedState, shuffle);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not restore the last session.");
            }
        }

        if (_isDisposed) return;
        if (!restoredSuccessfully)
        {
            ClearQueuesInternal();
            _queue.SetShuffled(shuffle);
        }
        _logger.LogDebug("MusicPlaybackService initialized. Session restored: {IsRestored}", restoredSuccessfully);

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

    public Task PlayAsync(Guid songId) => PlayAsync([songId]);

    public Task PlayAsync(IEnumerable<Guid> songIds, int startIndex = 0, bool? startShuffled = null, PlaybackContext? context = null) =>
        RunPlaybackRequest(request => PlayInternalAsync(songIds, startIndex, startShuffled, context, request));

    private async Task PlayInternalAsync(IEnumerable<Guid> songIds, int startIndex, bool? startShuffled, PlaybackContext? context, CancellationToken request, IReadOnlyList<Guid>? shuffleOrder = null, ShuffleMode mode = ShuffleMode.Random)
    {
        var requestedIds = songIds?.ToList() ?? [];
        var requestedStartId = startIndex >= 0 && startIndex < requestedIds.Count ? requestedIds[startIndex] : Guid.Empty;
        var idList = requestedIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (idList.Count == 0) return;
        startIndex = requestedStartId == Guid.Empty ? 0 : idList.IndexOf(requestedStartId);
        CancelQueueEdits();

        _logger.LogDebug(
            "Playing a new queue of {SongCount} songs. Start index: {StartIndex}, Shuffled: {IsShuffled}",
            idList.Count, startIndex, startShuffled);

        // A shuffled start lets the shuffle choose the first song; any other start plays the chosen song first.
        var queue = new PlaybackQueue();
        queue.Replace(idList, startShuffled ?? IsShuffleEnabled, startShuffled == true ? null : idList[startIndex], shuffleOrder);
        var replacement = new QueueReplacement(queue, context ?? new PlaybackContext(PlaybackContextType.Library, null), mode);
        await PlayQueueItemInternalAsync(queue.CurrentId!.Value, request, replacement);
    }

    /// <summary>A queue that replaces the current one once its first song is ready to play.</summary>
    private sealed record QueueReplacement(PlaybackQueue Queue, PlaybackContext Context, ShuffleMode Mode);

    private void ReplaceQueue(QueueReplacement replacement)
    {
        _currentContext = replacement.Context;
        if (StopMode is PlaybackStopMode.AfterTrack or PlaybackStopMode.EndOfQueue) ScheduleStop(PlaybackStopMode.Off);
        var wasShuffled = IsShuffleEnabled;
        _queue = replacement.Queue;
        SetShuffleMode(replacement.Mode, wasShuffled);
        CommitQueueChanges();
    }

    public Task PlayPauseAsync() =>
        RunPlaybackRequest(PlayPauseInternalAsync);

    private async Task PlayPauseInternalAsync(CancellationToken request)
    {
        if (_audioPlayer.IsPlaying)
        {
            await _audioPlayer.PauseAsync();
            return;
        }

        if (CurrentTrack != null)
        {
            if (!CurrentListenHistoryId.HasValue)
            {
                var listenId = await StartListenAsync(CurrentTrack, _currentContext, request);
                if (request.IsCancellationRequested) return;
                BeginListen(listenId);
            }
            await ApplyReplayGainIfEnabledAsync();
            if (request.IsCancellationRequested) return;
            await _audioPlayer.PlayAsync();
            return;
        }

        var resumeId = _queue.CurrentId ?? _queue.PlayOrder.FirstOrDefault();
        if (resumeId != Guid.Empty) await PlayQueueItemInternalAsync(resumeId, request);
    }

    internal Task StopAsync() =>
        RunPlaybackRequest(StopInternalAsync);

    private async Task StopInternalAsync(CancellationToken request)
    {
        ScheduleStop(PlaybackStopMode.Off);
        _ = EndListenSession(PlaybackEndReason.PausedAndAbandoned);

        // The queue keeps its position, so Play, Next and Previous carry on from the stopped song. Cleared before the
        // player unloads it, so a request that supersedes this one never finds a current track with nothing loaded.
        CurrentTrack = null;
        TrackChanged?.Invoke();
        PositionChanged?.Invoke();
        _mediaControls.Clear();
        UpdateMediaControlsNavigation();

        await _audioPlayer.StopAsync();
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

    // True while stopped, and while a song removed from the queue is still playing until the next one is ready. The
    // queue's position is then the song to play, rather than the one beside it.
    private bool IsOffQueuePosition => CurrentTrack?.Id != _queue.CurrentId;

    private Guid? AdjacentId(bool forward) => IsOffQueuePosition ? _queue.CurrentId : _queue.GetAdjacent(forward, Wraps);

    private async Task PlayAdjacentAsync(bool forward, CancellationToken request)
    {
        if (AdjacentId(forward) is { } id) await PlayQueueItemInternalAsync(id, request);
        else if (CurrentTrack != null) await StopInternalAsync(request);
    }

    private async Task AdvanceAfterTrackEndAsync(CancellationToken request)
    {
        if (StopMode == PlaybackStopMode.AfterTrack)
        {
            await StopInternalAsync(request);
            return;
        }

        // Stopping at the end of the queue plays it through once, whatever the repeat mode.
        var repeat = StopMode == PlaybackStopMode.EndOfQueue ? RepeatMode.Off : CurrentRepeatMode;
        var nextId = IsOffQueuePosition || repeat == RepeatMode.RepeatOne
            ? _queue.CurrentId
            : _queue.GetAdjacent(forward: true, wrap: repeat == RepeatMode.RepeatAll);
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

    public async Task<bool> PlayScopeAsync(SongScope scope, bool? startShuffled = null)
    {
        var isEmpty = false;
        await RunPlaybackRequest(async request =>
        {
            var songIds = await _libraryReader.GetSongIdsAsync(scope, null, PlayOrderOf(scope), request);
            isEmpty = songIds.Count == 0;
            if (!request.IsCancellationRequested)
                await PlayInternalAsync(songIds, 0, startShuffled, PlaybackContext.Of(scope), request);
        });
        return !isEmpty;
    }

    private static SongSortOrder PlayOrderOf(SongScope scope) => scope switch
    {
        SongScope.Album => SongSortOrder.TrackNumberAsc,
        SongScope.Playlist => SongSortOrder.PlaylistOrder,
        _ => SongSortOrder.TitleAsc
    };

    public async Task<bool> PlaySmartPlaylistAsync(Guid smartPlaylistId)
    {
        var isEmpty = false;
        await RunPlaybackRequest(async request =>
        {
            var songIds = await _smartPlaylists.GetMatchingSongIdsAsync(smartPlaylistId, token: request);
            isEmpty = songIds.Count == 0;
            if (!request.IsCancellationRequested)
                await PlayInternalAsync(songIds, 0, null, new PlaybackContext(PlaybackContextType.SmartPlaylist, smartPlaylistId), request);
        });
        return !isEmpty;
    }

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
        _logger.LogDebug("Shuffle mode set to {ShuffleState}", IsShuffleEnabled);
        CommitQueueChanges();
        SetShuffleMode(ShuffleMode.Random, wasShuffled: !enable);
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

    /// <summary>
    ///     Plays a song of the queue, or of <paramref name="replacement" />, which then replaces the queue. Nothing
    ///     changes until the song is ready to load, so a request superseded before then leaves playback as it was.
    ///     <paramref name="context" /> becomes the context of a queue that isn't replaced.
    /// </summary>
    private async Task PlayQueueItemInternalAsync(Guid songId, CancellationToken request,
        QueueReplacement? replacement = null, PlaybackContext? context = null)
    {
        var queue = replacement?.Queue ?? _queue;
        // Songs deleted from disk can still be queued; give up after a run of them rather than walk the whole queue.
        const int maxSkipAttempts = 20;
        Guid? candidate = songId;
        var tried = songId;
        var skipCount = 0;
        Song? song = null;

        while (skipCount < maxSkipAttempts && candidate is { } id)
        {
            tried = id;
            song = _transientTracks.GetValueOrDefault(id) ?? await _libraryReader.GetSongByIdAsync(id);
            if (request.IsCancellationRequested) return;
            if (song != null) break;

            _logger.LogWarning("Song metadata not found for ID {SongId}. Skipping to next track. (Skip {SkipCount}/{MaxSkip})",
                id, skipCount + 1, maxSkipAttempts);
            skipCount++;
            candidate = queue.GetAdjacent(forward: true, Wraps, from: id);
        }

        if (song == null)
        {
            _logger.LogError("Failed to find playable track after skipping {SkipCount} deleted songs. Stopping playback.", skipCount);
            await StopAtAsync(tried, replacement, request);
            return;
        }

        // Before the listen session starts, so a server song that can't stream isn't credited with any listening time.
        var streamUri = await _musicServers.GetStreamUriAsync(song);
        if (request.IsCancellationRequested) return;
        if (song.IsFromServer && streamUri is null)
        {
            await StopAtAsync(song.Id, replacement, request);
            ServerSongFailed?.Invoke(song);
            return;
        }

        var listenId = await StartListenAsync(song, replacement?.Context ?? context ?? _currentContext, request);
        if (request.IsCancellationRequested) return;
        // Removing a song that isn't playing is no request, so the song can have left the queue meanwhile.
        if (!queue.Contains(song.Id))
        {
            await AbandonListenAsync(listenId);
            return;
        }

        // One step, so listeners never see the new track with the old listen or queue position.
        queue.SetCurrent(song.Id);
        if (replacement is not null) ReplaceQueue(replacement);
        else if (context is not null) _currentContext = context;
        _ = EndListenSession(PlaybackEndReason.Skipped);
        BeginListen(listenId);
        CurrentTrack = song;
        TrackChanged?.Invoke();
        _logger.LogDebug("Now playing '{SongTitle}' (position {Position} of {Count})",
            song.Title, _queue.CurrentPosition, _queue.Count);

        await _audioPlayer.LoadAsync(song, streamUri);
        if (request.IsCancellationRequested) return;
        await ApplyReplayGainIfEnabledAsync();
        if (!request.IsCancellationRequested) await _audioPlayer.PlayAsync();
    }

    // Stops at a song that can't play, so Play and Next carry on from it. One that has left the queue meanwhile
    // leaves playback as it was, like a song that can play.
    private async Task StopAtAsync(Guid songId, QueueReplacement? replacement, CancellationToken request)
    {
        if (!(replacement?.Queue ?? _queue).Contains(songId)) return;
        if (replacement is not null) ReplaceQueue(replacement);
        _queue.SetCurrent(songId);
        await StopInternalAsync(request);
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
        _stopTimerCts?.Cancel();
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

    public bool CanGoNext => NextTrackId is not null;

    // A playing track can always go back, to its start or to the track before it.
    public bool CanGoPrevious => CurrentTrack is not null || AdjacentId(forward: false) is not null;

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
        _request = RunAsync();
        TrackPendingOperation(_request);
        return _request;

        // A superseded request ends quietly, however its work noticed: a check of the token or a cancelled await.
        // It completes with its work, not a turn of the UI context later, so IsRequestPending ends when the work does.
        async Task RunAsync()
        {
            try
            {
                await action(request).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested)
            {
            }
        }
    }

    // Until the latest request finishes, the player's events can still be about the track it replaces.
    private bool IsRequestPending => !_request.IsCompleted;

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

    internal Task AwaitPendingOperationsAsync()
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
}
