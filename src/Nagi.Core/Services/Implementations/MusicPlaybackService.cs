using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

public partial class MusicPlaybackService : IMusicPlaybackService
{
    private readonly IAudioPlayer _audioPlayer;
    private readonly ISystemMediaControls _mediaControls;
    private readonly ILibraryReader _libraryReader;
    private readonly ILibraryWriter _libraryWriter;
    private readonly ILogger<MusicPlaybackService> _logger;
    private readonly IMetadataService _metadataService;
    private readonly ISettingsService _settingsService;
    private readonly object _pendingOperationsLock = new();

    private int _playbackRequestVersion;
    private int _queueVersion;
    private int _replayGainVersion;
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
        IAudioPlayer audioPlayer,
        ISystemMediaControls mediaControls,
        ILibraryReader libraryReader,
        ILibraryWriter libraryWriter,
        IMetadataService metadataService,
        ILogger<MusicPlaybackService> logger, TimeProvider? timeProvider = null)
    {
        _settingsService = settingsService;
        _audioPlayer = audioPlayer;
        _mediaControls = mediaControls;
        _libraryReader = libraryReader;
        _libraryWriter = libraryWriter;
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
        _mediaControls.NextPressed += OnMediaControlsNextPressed;
        _mediaControls.PreviousPressed += OnMediaControlsPreviousPressed;

        _settingsService.VolumeNormalizationEnabledChanged += OnVolumeNormalizationEnabledChanged;
        _settingsService.FadeOnPlayPauseEnabledChanged += OnFadeOnPlayPauseEnabledChanged;
        _settingsService.FadeInDurationChanged += OnFadeInDurationChanged;
        _settingsService.FadeOutDurationChanged += OnFadeOutDurationChanged;

        EqualizerBands = _audioPlayer.GetEqualizerBands();
    }

    public Song? CurrentTrack { get; private set; }
    public long? CurrentListenHistoryId { get; private set; }
    public bool IsPlaying => _audioPlayer.IsPlaying;
    public TimeSpan CurrentPosition => _audioPlayer.CurrentPosition;
    public TimeSpan Duration => _audioPlayer.Duration > TimeSpan.Zero
        ? _audioPlayer.Duration
        : (CurrentTrack?.Duration ?? TimeSpan.Zero);
    public double Volume => _audioPlayer.Volume;
    public bool IsMuted => _audioPlayer.IsMuted;
    public IReadOnlyList<Guid> PlaybackQueue => _queue.OrderView();
    public IReadOnlyList<Guid> ShuffledQueue => _queue.ShuffledView();
    public int CurrentQueueIndex { get; private set; } = -1;
    public int CurrentShuffledIndex { get; private set; } = -1;
    public bool IsShuffleEnabled { get; private set; }
    public RepeatMode CurrentRepeatMode { get; private set; } = RepeatMode.Off;
    private bool _isTransitioningTrack;
    public IReadOnlyList<(uint Index, float Frequency)> EqualizerBands { get; }
    public IReadOnlyList<EqualizerPreset> AvailablePresets { get; } = new List<EqualizerPreset>
    {
        new(Resources.Strings.EqPreset_None, [0, 0, 0, 0, 0, 0, 0, 0, 0, 0]),
        new(Resources.Strings.EqPreset_Classical, [0, 0, 0, 0, 0, 0, -3.2f, -3.2f, -3.2f, -4.0f]),
        new(Resources.Strings.EqPreset_Dance, [3.2f, 2.4f, 0.8f, 0, 0, -1.6f, -2.4f, -2.4f, 0, 0]),
        new(Resources.Strings.EqPreset_FullBass, [3.2f, 3.2f, 3.2f, 1.6f, 0, -1.6f, -3.2f, -3.2f, -3.2f, -4.0f]),
        new(Resources.Strings.EqPreset_FullTreble, [-3.2f, -3.2f, -3.2f, -1.6f, 0, 1.6f, 3.2f, 3.2f, 3.2f, 4.0f]),
        new(Resources.Strings.EqPreset_Jazz, [1.6f, 0.8f, 0, 0.8f, -0.8f, -0.8f, -1.6f, 0, 0.8f, 1.6f]),
        new(Resources.Strings.EqPreset_Pop, [-1.6f, 1.6f, 2.4f, 2.4f, 1.6f, 0, -0.8f, -0.8f, -1.6f, -1.6f]),
        new(Resources.Strings.EqPreset_Rock, [3.2f, 2.4f, -1.6f, -2.4f, -0.8f, 1.6f, 2.4f, 3.2f, 3.2f, 3.2f]),
        new(Resources.Strings.EqPreset_Soft, [1.6f, 0.8f, 0, -0.8f, -0.8f, 0, 0.8f, 1.6f, 2.4f, 3.2f]),
        new(Resources.Strings.EqPreset_Techno, [3.2f, 2.4f, 0, -1.6f, -1.6f, 0, 1.6f, 2.4f, 2.4f, 3.2f]),
        new(Resources.Strings.EqPreset_Vocal, [-0.8f, -2.4f, -2.4f, 0, 1.6f, 1.6f, 1.6f, 0.8f, 0, -0.8f])
    };

    public EqualizerSettings? CurrentEqualizerSettings { get; private set; }

    public event Action? TrackChanged;
    public event Action? PlaybackStateChanged;
    public event Action? VolumeStateChanged;
    public event Action? ShuffleModeChanged;
    public event Action? RepeatModeChanged;
    public event Action? QueueChanged;
    public event Action? PositionChanged;
    public event Action? DurationChanged;
    public event Action? EqualizerChanged;
    public event Action<Song, long, DateTime>? ScrobbleEligibilityReached;

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
            var volumeTask = _settingsService.GetInitialVolumeAsync();
            var muteTask = _settingsService.GetInitialMuteStateAsync();
            var shuffleTask = _settingsService.GetInitialShuffleStateAsync();
            var repeatTask = _settingsService.GetInitialRepeatModeAsync();
            var eqTask = _settingsService.GetEqualizerSettingsAsync();
            var restoreEnabledTask = _settingsService.GetRestorePlaybackStateEnabledAsync();
            var fadeEnabledTask = _settingsService.GetFadeOnPlayPauseEnabledAsync();
            var fadeInTask = _settingsService.GetFadeInDurationMsAsync();
            var fadeOutTask = _settingsService.GetFadeOutDurationMsAsync();
            var playbackStateTask = _settingsService.GetPlaybackStateAsync();

            await Task.WhenAll(volumeTask, muteTask, shuffleTask, repeatTask, eqTask, restoreEnabledTask, fadeEnabledTask, fadeInTask, fadeOutTask, playbackStateTask);
            if (_isDisposed) return;

            await _audioPlayer.SetVolumeAsync(volumeTask.Result);
            if (_isDisposed) return;
            await _audioPlayer.SetMuteAsync(muteTask.Result);
            if (_isDisposed) return;
            _audioPlayer.SetFadeOnPlayPauseEnabled(fadeEnabledTask.Result);
            _audioPlayer.SetFadeInDuration(fadeInTask.Result);
            _audioPlayer.SetFadeOutDuration(fadeOutTask.Result);
            IsShuffleEnabled = shuffleTask.Result;
            CurrentRepeatMode = repeatTask.Result;

            CurrentEqualizerSettings = eqTask.Result;
            if (CurrentEqualizerSettings != null && CurrentEqualizerSettings.BandGains.Count != EqualizerBands.Count)
            {
                _logger.LogWarning("Equalizer settings band count ({SavedCount}) does not match available bands ({AvailableCount}). Resetting equalizer to default.",
                    CurrentEqualizerSettings.BandGains.Count, EqualizerBands.Count);

                CurrentEqualizerSettings = null; // Force recreation below
            }

            if (CurrentEqualizerSettings == null)
            {
                CurrentEqualizerSettings = new EqualizerSettings
                {
                    Preamp = EqualizerSettings.DefaultPreampDb,
                    BandGains = Enumerable.Repeat(0.0f, EqualizerBands.Count).ToList()
                };

                await _settingsService.SetEqualizerSettingsAsync(CurrentEqualizerSettings);
                if (_isDisposed) return;
            }
            _audioPlayer.ApplyEqualizerSettings(CurrentEqualizerSettings);

            var restoredSuccessfully = false;
            if (restoreLastSession && restoreEnabledTask.Result)
            {
                var savedState = playbackStateTask.Result;
                if (savedState != null) restoredSuccessfully = await RestoreInternalPlaybackStateAsync(savedState);
            }

            if (_isDisposed) return;
            if (!restoredSuccessfully) ClearQueuesInternal();

            _logger.LogDebug("MusicPlaybackService initialized successfully. Session restored: {IsRestored}",
                restoredSuccessfully);
        }
        catch (Exception ex)
        {
            if (_isDisposed) return;
            _logger.LogError(ex, "MusicPlaybackService initialization failed. Using default settings.");
            await _audioPlayer.SetVolumeAsync(0.5);
            if (_isDisposed) return;
            await _audioPlayer.SetMuteAsync(false);
            if (_isDisposed) return;
            IsShuffleEnabled = false;
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

    public Task PlayTransientFileAsync(string filePath) =>
        RunPlaybackRequest(request => PlayTransientFileInternalAsync(filePath, request));

    private async Task PlayTransientFileInternalAsync(string filePath, int request)
    {
        _queueVersion++;
        _logger.LogDebug("Playing transient file: {FilePath}", filePath);
        var transientSong = await CreateTransientSongAsync(filePath);
        if (!IsCurrentRequest(request)) return;

        if (CurrentListenHistoryId.HasValue)
        {
            var sessionId = CurrentListenHistoryId.Value;
            var position = _audioPlayer.CurrentPosition;
            CurrentListenHistoryId = null;
            FireAndForgetSafe(() => _libraryWriter.FinalizeListenSessionAsync(sessionId, position, PlaybackEndReason.Skipped), "Finalizing session on Transient Playback", trackForDisposal: true);
        }

        _isEligibilityMarked = false;

        _isTransitioningTrack = true;
        ClearQueuesInternal();
        _transientTracks.Register(transientSong);
        QueueChanged?.Invoke();

        CurrentTrack = transientSong;
        CurrentQueueIndex = -1;
        CurrentListenHistoryId = null;
        _currentContext = new PlaybackContext(PlaybackContextType.Transient, null);

        // Track listens for transient playback only when the file maps to a known library song.
        // Truly external files remain untracked because ListenHistory requires a Song FK.
        var existingLibrarySong = await _libraryReader.GetSongByFilePathAsync(filePath);
        if (!IsCurrentRequest(request)) return;
        if (existingLibrarySong != null)
            await StartListenSessionAsync(existingLibrarySong.Id, request);
        if (!IsCurrentRequest(request)) return;

        await _audioPlayer.LoadAsync(transientSong);
        if (!IsCurrentRequest(request)) return;
        await ApplyReplayGainIfEnabledAsync();
        if (!IsCurrentRequest(request)) return;
        await _audioPlayer.PlayAsync();
        if (!IsCurrentRequest(request)) return;
        UpdateMediaControlsNavigation();

        TrackChanged?.Invoke();
    }

    private async Task<Song> CreateTransientSongAsync(string filePath)
    {
        var metadata = await _metadataService.ExtractMetadataAsync(filePath);

        var validArtists = metadata.Artists
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => ArtistNameHelper.Normalize(a))
            .ToList();
        if (validArtists.Count == 0)
        {
            validArtists.Add(Artist.UnknownArtistName);
        }

        var validAlbumArtists = metadata.AlbumArtists
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => ArtistNameHelper.Normalize(a))
            .ToList();
        if (validAlbumArtists.Count == 0)
        {
            validAlbumArtists = validArtists;
        }

        return new Song
        {
            FilePath = filePath,
            Title = metadata.Title,
            ArtistName = Artist.GetDisplayName(validArtists),
            PrimaryArtistName = validArtists.First(),

            SongArtists = validArtists.Select((a, i) => new SongArtist
            {
                Artist = new Artist { Name = a },
                Order = i
            }).ToList(),
            Album = new Album
            {
                Title = ArtistNameHelper.NormalizeStringCore(metadata.Album) ?? Album.UnknownAlbumName,

                ArtistName = Artist.GetDisplayName(validAlbumArtists),
                PrimaryArtistName = validAlbumArtists.First(),

                AlbumArtists = validAlbumArtists.Select((aa, i) => new AlbumArtist
                {
                    Artist = new Artist { Name = aa },
                    Order = i
                }).ToList()
            },
            Duration = metadata.Duration,
            Genres = metadata.Genres.Where(g => !string.IsNullOrWhiteSpace(g))
                .Distinct(StringComparer.OrdinalIgnoreCase).Select(g => new Genre { Name = g }).ToList(),
            AlbumArtUriFromTrack = metadata.CoverArtUri,
            Lyrics = metadata.Lyrics,
            LrcFilePath = metadata.LrcFilePath
        };
    }

    public Task PlayAsync(Song song)
    {
        return PlayAsync(song?.Id ?? Guid.Empty);
    }

    public Task PlayAsync(Guid songId) => songId == Guid.Empty
        ? Task.CompletedTask
        : PlayAsync([songId]);

    public Task PlayAsync(IEnumerable<Song> songs, int startIndex = 0, bool? startShuffled = null)
    {
        return PlayAsync(songs?.Select(s => s.Id) ?? Enumerable.Empty<Guid>(), startIndex, startShuffled);
    }

    public Task PlayAsync(IEnumerable<Guid> songIds, int startIndex = 0, bool? startShuffled = null, PlaybackContext? context = null) =>
        RunPlaybackRequest(request => PlayInternalAsync(songIds, startIndex, startShuffled, context, request));

    private async Task PlayInternalAsync(IEnumerable<Guid> songIds, int startIndex, bool? startShuffled, PlaybackContext? context, int request, IReadOnlyList<Guid>? shuffleOrder = null, ShuffleMode mode = ShuffleMode.Random)
    {
        _queueVersion++;
        var requestedIds = songIds?.ToList() ?? [];
        var requestedStartId = startIndex >= 0 && startIndex < requestedIds.Count ? requestedIds[startIndex] : Guid.Empty;
        var idList = requestedIds.Where(id => id != Guid.Empty).Distinct().ToList();
        startIndex = requestedStartId == Guid.Empty ? 0 : idList.IndexOf(requestedStartId);
        if (!idList.Any())
        {
            _logger.LogWarning("PlayAsync called with an empty song list. Stopping playback.");
            await StopInternalAsync(request);
            if (!IsCurrentRequest(request)) return;
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
        if (startShuffled.HasValue && IsShuffleEnabled != startShuffled.Value) await SetShuffleAsync(startShuffled.Value);

        if (!IsCurrentRequest(request)) return;

        _queue.SetOrder(idList);
        if (IsShuffleEnabled)
        {
            if (shuffleOrder is null) _queue.GenerateShuffled();
            else _queue.SetShuffled([.. shuffleOrder]);
        }
        CurrentShuffleMode = mode;
        ShuffleModeChanged?.Invoke();
        CommitQueueChanges();

        if (IsShuffleEnabled)
        {
            // If playing from a specific item (not just "Shuffle All"), ensure it plays first
            if (startShuffled != true && startIndex >= 0 && startIndex < _queue.Order.Count)
            {
                _queue.MoveToShuffledFront(_queue.Order[startIndex]);
                _queue.RebuildShuffledIndex();
            }

            startIndex = _queue.IndexOf(_queue.Shuffled[0]);
        }

        if (IsCurrentRequest(request)) await PlayQueueItemInternalAsync(startIndex, request);
    }

    public Task PlayPauseAsync() =>
        RunPlaybackRequest(PlayPauseInternalAsync);

    private async Task PlayPauseInternalAsync(int request)
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
            if (!IsCurrentRequest(request)) return;
            await ApplyReplayGainIfEnabledAsync();
            if (!IsCurrentRequest(request)) return;
            await _audioPlayer.PlayAsync();
            return;
        }

        if (_queue.Order.Any())
        {
            var indexToPlay = CurrentQueueIndex >= 0 ? CurrentQueueIndex : 0;

            if (IsShuffleEnabled && _queue.Shuffled.Any())
            {
                var shuffledIndex = CurrentShuffledIndex >= 0 ? CurrentShuffledIndex : 0;
                var songIdToPlay = _queue.Shuffled.ElementAtOrDefault(shuffledIndex);
                if (songIdToPlay != Guid.Empty)
                {
                    indexToPlay = _queue.IndexOf(songIdToPlay);
                    if (indexToPlay == -1) indexToPlay = 0;
                }
            }

            if (indexToPlay >= 0 && indexToPlay < _queue.Order.Count) await PlayQueueItemInternalAsync(indexToPlay, request);
        }
    }

    public Task StopAsync() =>
        RunPlaybackRequest(StopInternalAsync);

    private async Task StopInternalAsync(int request)
    {
        ScheduleStop(PlaybackStopMode.Off);
        if (CurrentListenHistoryId.HasValue)
        {
            // Capture and clear synchronously before the async gap, mirroring the pattern in
            // OnAudioPlayerPlaybackEnded, to prevent a racing PlaybackEnded event from issuing
            // a second FinalizeListenSessionAsync on the same session.
            var sessionId = CurrentListenHistoryId.Value;
            var position = _audioPlayer.CurrentPosition;
            CurrentListenHistoryId = null;
            FireAndForgetSafe(() => _libraryWriter.FinalizeListenSessionAsync(sessionId, position, PlaybackEndReason.PausedAndAbandoned), "Finalizing session on Stop", trackForDisposal: true);
        }

        await _audioPlayer.StopAsync();
        if (!IsCurrentRequest(request)) return;

        // Null the track to indicate a "stopped" state but preserve the queue indices.
        // This is crucial for Next/Previous commands to work correctly from a stopped state.
        CurrentTrack = null;
        CurrentListenHistoryId = null;
        _currentListenStartedUtc = null;
        _isTransitioningTrack = false;
        TrackChanged?.Invoke();
        PositionChanged?.Invoke();
        UpdateMediaControlsNavigation();
    }

    public Task NextAsync() =>
        RunPlaybackRequest(NextInternalAsync);

    private async Task NextInternalAsync(int request)
    {
        if (CurrentTrack is not null && ShouldStopAfterTrack())
        {
            await StopInternalAsync(request);
            return;
        }
        if (CurrentRepeatMode == RepeatMode.RepeatOne && CurrentTrack != null)
        {
            await PlayQueueItemInternalAsync(CurrentQueueIndex, request);
            return;
        }

        if (CurrentTrack != null)
        {
            var nextIndex = GetAdjacentTrackIndex(CurrentQueueIndex, moveForward: true);
            if (nextIndex >= 0)
                await PlayQueueItemInternalAsync(nextIndex, request);
            else
                await StopInternalAsync(request);
        }
        else if (_queue.Order.Any())
        {
            // Pressing Next should resume playback from the current position.
            await PlayQueueItemInternalAsync(CurrentQueueIndex, request);
        }
    }

    public Task PreviousAsync() =>
        RunPlaybackRequest(PreviousInternalAsync);

    private async Task PreviousInternalAsync(int request)
    {
        if (CurrentTrack != null && _audioPlayer.CurrentPosition.TotalSeconds > 3 &&
            CurrentRepeatMode != RepeatMode.RepeatOne)
        {
            await SeekAsync(TimeSpan.Zero);
            if (!IsCurrentRequest(request)) return;
            if (!_audioPlayer.IsPlaying) await _audioPlayer.PlayAsync();
            return;
        }

        if (CurrentRepeatMode == RepeatMode.RepeatOne && CurrentTrack != null)
        {
            await PlayQueueItemInternalAsync(CurrentQueueIndex, request);
            return;
        }

        if (CurrentTrack != null)
        {
            var prevIndex = GetAdjacentTrackIndex(CurrentQueueIndex, moveForward: false);
            if (prevIndex >= 0)
                await PlayQueueItemInternalAsync(prevIndex, request);
            else
                await StopInternalAsync(request);
        }
        else if (_queue.Order.Any())
        {
            // Pressing Previous should resume playback from the current position.
            await PlayQueueItemInternalAsync(CurrentQueueIndex, request);
        }
    }

    public async Task SeekAsync(TimeSpan position)
    {
        if (CurrentTrack != null) await _audioPlayer.SeekAsync(position);
    }

    public Task PlayAlbumAsync(Guid albumId) => RunPlaybackRequest(async request =>
    {
        var songIds = await _libraryReader.GetAllSongIdsByAlbumIdAsync(albumId, SongSortOrder.TrackNumberAsc);
        if (IsCurrentRequest(request))
            await PlayInternalAsync(songIds, 0, null, new PlaybackContext(PlaybackContextType.Album, albumId), request);
    });

    public Task PlayArtistAsync(Guid artistId) => RunPlaybackRequest(async request =>
    {
        var songIds = await _libraryReader.GetAllSongIdsByArtistIdAsync(artistId, SongSortOrder.TitleAsc);
        if (IsCurrentRequest(request))
            await PlayInternalAsync(songIds, 0, null, new PlaybackContext(PlaybackContextType.Artist, artistId), request);
    });

    public Task PlayFolderAsync(Guid folderId) => RunPlaybackRequest(async request =>
    {
        var songIds = await _libraryReader.GetAllSongIdsByFolderIdAsync(folderId, SongSortOrder.TitleAsc);
        if (IsCurrentRequest(request))
            await PlayInternalAsync(songIds, 0, null, new PlaybackContext(PlaybackContextType.Folder, folderId), request);
    });

    public Task PlayPlaylistAsync(Guid playlistId) => RunPlaybackRequest(async request =>
    {
        var songIds = await _libraryReader.GetAllSongIdsByPlaylistIdAsync(playlistId, SongSortOrder.PlaylistOrder);
        if (IsCurrentRequest(request))
            await PlayInternalAsync(songIds, 0, null, new PlaybackContext(PlaybackContextType.Playlist, playlistId), request);
    });

    public Task PlayGenreAsync(Guid genreId) => RunPlaybackRequest(async request =>
    {
        var songIds = await _libraryReader.GetAllSongIdsByGenreIdAsync(genreId, SongSortOrder.TitleAsc);
        if (IsCurrentRequest(request))
            await PlayInternalAsync(songIds, 0, null, new PlaybackContext(PlaybackContextType.Genre, genreId), request);
    });

    public async Task SetVolumeAsync(double volume)
    {
        await _audioPlayer.SetVolumeAsync(volume);
        await _settingsService.SaveVolumeAsync(_audioPlayer.Volume);
    }

    public async Task ToggleMuteAsync()
    {
        var newMuteState = !_audioPlayer.IsMuted;
        await _audioPlayer.SetMuteAsync(newMuteState);
        await _settingsService.SaveMuteStateAsync(newMuteState);
    }

    public async Task SetShuffleAsync(bool enable)
    {
        if (IsShuffleEnabled == enable) return;

        IsShuffleEnabled = enable;
        CurrentShuffleMode = ShuffleMode.Random;
        _logger.LogDebug("Shuffle mode set to {ShuffleState}", IsShuffleEnabled);

        if (IsShuffleEnabled)
        {
            _queue.GenerateShuffled();
        }
        else
        {
            _queue.ClearShuffled();
        }
        CommitQueueChanges();

        await _settingsService.SaveShuffleStateAsync(IsShuffleEnabled);
        ShuffleModeChanged?.Invoke();
        UpdateMediaControlsNavigation();
    }

    public async Task SetRepeatModeAsync(RepeatMode mode)
    {
        if (CurrentRepeatMode == mode) return;

        CurrentRepeatMode = mode;
        _logger.LogDebug("Repeat mode set to {RepeatMode}", CurrentRepeatMode);
        await _settingsService.SaveRepeatModeAsync(CurrentRepeatMode);
        RepeatModeChanged?.Invoke();
        UpdateMediaControlsNavigation();
    }

    public Task PlayQueueItemAsync(int originalQueueIndex) =>
        RunPlaybackRequest(request => PlayQueueItemInternalAsync(originalQueueIndex, request));

    private async Task PlayQueueItemInternalAsync(int originalQueueIndex, int request)
    {
        if (originalQueueIndex < 0 || originalQueueIndex >= _queue.Order.Count)
        {
            await StopInternalAsync(request);
            return;
        }

        if (CurrentListenHistoryId.HasValue)
        {
            var previousSessionId = CurrentListenHistoryId.Value;
            var position = _audioPlayer.CurrentPosition;
            CurrentListenHistoryId = null;
            FireAndForgetSafe(() => _libraryWriter.FinalizeListenSessionAsync(previousSessionId, position, PlaybackEndReason.Skipped), "Finalizing skipped session", trackForDisposal: true);
        }

        _isEligibilityMarked = false;

        _isTransitioningTrack = true;

        // Use iterative approach with skip limit to prevent stack overflow if many
        // consecutive songs are deleted from disk but still in the queue
        const int maxSkipAttempts = 20;
        var currentIndex = originalQueueIndex;
        var skipCount = 0;
        Song? song = null;

        while (skipCount < maxSkipAttempts && currentIndex >= 0 && currentIndex < _queue.Order.Count)
        {
            var songId = _queue.Order[currentIndex];
            song = _transientTracks.GetValueOrDefault(songId)
                   ?? await _libraryReader.GetSongByIdAsync(songId);
            if (!IsCurrentRequest(request)) return;
            currentIndex = _queue.IndexOf(songId);
            if (currentIndex < 0)
            {
                await StopInternalAsync(request);
                return;
            }

            if (song != null)
            {
                break;
            }

            _logger.LogWarning("Song metadata not found for ID {SongId}. Skipping to next track. (Skip {SkipCount}/{MaxSkip})",
                songId, skipCount + 1, maxSkipAttempts);
            skipCount++;

            currentIndex = GetAdjacentTrackIndex(currentIndex, moveForward: true);
        }

        if (song == null)
        {
            _logger.LogError("Failed to find playable track after skipping {SkipCount} deleted songs. Stopping playback.", skipCount);
            _isTransitioningTrack = false;
            await StopInternalAsync(request);
            return;
        }

        CurrentTrack = song;
        CurrentQueueIndex = currentIndex;
        PruneTransientTracks();

        if (!_transientTracks.ContainsKey(song.Id)) await StartListenSessionAsync(song.Id, request);
        if (!IsCurrentRequest(request)) return;

        CurrentShuffledIndex = IsShuffleEnabled ? _queue.ShuffledIndexOf(song.Id) : -1;
        _logger.LogDebug("Now playing '{SongTitle}' (Index: {QueueIndex}, Shuffled Index: {ShuffledIndex})",
            song.Title, CurrentQueueIndex, CurrentShuffledIndex);

        await _audioPlayer.LoadAsync(song);
        if (!IsCurrentRequest(request)) return;
        await ApplyReplayGainIfEnabledAsync();
        if (IsCurrentRequest(request)) await _audioPlayer.PlayAsync();
    }

    private async Task StartListenSessionAsync(Guid songId, int request)
    {
        // Presence listeners need the session before LoadAsync can raise MediaOpened.
        var startedUtc = DateTime.UtcNow;
        var sessionId = await _libraryWriter.StartListenSessionAsync(songId, _currentContext);
        if (!IsCurrentRequest(request))
        {
            if (sessionId.HasValue)
                await _libraryWriter.FinalizeListenSessionAsync(sessionId.Value, TimeSpan.Zero, PlaybackEndReason.PausedAndAbandoned);
            return;
        }

        CurrentListenHistoryId = sessionId;
        _currentListenStartedUtc = sessionId.HasValue ? startedUtc : null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        Dispose();

        // Pending loads may still create sessions that need finalizing before dependencies are disposed.
        try
        {
            await AwaitPendingOperationsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pending finalization task failed during DisposeAsync.");
        }

        if (CurrentListenHistoryId.HasValue)
        {
            var sessionId = CurrentListenHistoryId.Value;
            var position = _audioPlayer.CurrentPosition;
            CurrentListenHistoryId = null;
            try
            {
                await _libraryWriter.FinalizeListenSessionAsync(sessionId, position, PlaybackEndReason.PausedAndAbandoned);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to finalize session during application exit.");
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _playbackRequestVersion++;
        _queueVersion++;
        _replayGainVersion++;

        _audioPlayer.PlaybackEnded -= OnAudioPlayerPlaybackEnded;
        _audioPlayer.StateChanged -= OnAudioPlayerStateChanged;
        _audioPlayer.VolumeChanged -= OnAudioPlayerVolumeChanged;
        _audioPlayer.PositionChanged -= OnAudioPlayerPositionChanged;
        _audioPlayer.ErrorOccurred -= OnAudioPlayerErrorOccurred;
        _audioPlayer.MediaOpened -= OnAudioPlayerMediaOpened;
        _audioPlayer.DurationChanged -= OnAudioPlayerDurationChanged;
        _mediaControls.NextPressed -= OnMediaControlsNextPressed;
        _mediaControls.PreviousPressed -= OnMediaControlsPreviousPressed;
        _settingsService.VolumeNormalizationEnabledChanged -= OnVolumeNormalizationEnabledChanged;
        _settingsService.FadeOnPlayPauseEnabledChanged -= OnFadeOnPlayPauseEnabledChanged;
        _settingsService.FadeInDurationChanged -= OnFadeInDurationChanged;
        _settingsService.FadeOutDurationChanged -= OnFadeOutDurationChanged;
    }

    private void UpdateMediaControlsNavigation()
    {
        var canGoNext = false;
        var canGoPrevious = false;

        if (_queue.Order.Any())
        {
            if (CurrentRepeatMode != RepeatMode.Off)
            {
                canGoNext = true;
                canGoPrevious = true;
            }
            else if (IsShuffleEnabled)
            {
                canGoNext = CurrentShuffledIndex < _queue.Shuffled.Count - 1;
                canGoPrevious = CurrentShuffledIndex > 0;
            }
            else
            {
                canGoNext = CurrentQueueIndex < _queue.Order.Count - 1;
                canGoPrevious = CurrentQueueIndex > 0;
            }

            // If a track is active, you can always go "previous" to either restart the track or go to the prior one.
            if (CurrentTrack != null) canGoPrevious = true;
        }

        _mediaControls.SetNavigationEnabled(canGoNext, canGoPrevious);
    }

    private Task RunPlaybackRequest(Func<int, Task> action)
    {
        if (_isDisposed) return Task.CompletedTask;
        var request = ++_playbackRequestVersion;
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
                if (IsCurrentRequest(request)) _isTransitioningTrack = false;
            }
        }
    }

    private bool IsCurrentRequest(int request) => !_isDisposed && request == _playbackRequestVersion;

    private void FireAndForgetSafe(Func<Task> asyncAction, string operationName, bool trackForDisposal = false)
    {
        var task = ExecuteAsync();

        if (trackForDisposal)
            TrackPendingOperation(task);

        async Task ExecuteAsync()
        {
            try
            {
                await asyncAction();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in fire-and-forget operation: {Operation}", operationName);
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
