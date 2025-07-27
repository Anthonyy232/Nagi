using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using System.Diagnostics;

namespace Nagi.Core.Services.Implementations;

/// <summary>
/// Manages music playback, queue, and state by coordinating between the audio player,
/// library service, and settings service. This class is responsible for all playback
/// logic, including shuffle and repeat modes.
/// </summary>
public class MusicPlaybackService : IMusicPlaybackService, IDisposable {
    private readonly IAudioPlayer _audioPlayer;
    private readonly ILibraryService _libraryService;
    private readonly ISettingsService _settingsService;
    private readonly Random _random = new();

    private List<Song> _playbackQueue = new();
    private List<Song> _shuffledQueue = new();
    private int _currentShuffledIndex = -1;
    private bool _isInitialized;
    private long? _currentListenHistoryId;

    /// <summary>
    /// Initializes a new instance of the <see cref="MusicPlaybackService"/> class.
    /// </summary>
    /// <param name="settingsService">The service for managing application settings.</param>
    /// <param name="audioPlayer">The low-level audio player implementation.</param>
    /// <param name="libraryService">The service for accessing the music library.</param>
    public MusicPlaybackService(ISettingsService settingsService, IAudioPlayer audioPlayer, ILibraryService libraryService) {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _audioPlayer = audioPlayer ?? throw new ArgumentNullException(nameof(audioPlayer));
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));

        Debug.WriteLine("[MusicPlaybackService] INFO: Subscribing to IAudioPlayer events.");
        _audioPlayer.PlaybackEnded += OnAudioPlayerPlaybackEnded;
        _audioPlayer.StateChanged += OnAudioPlayerStateChanged;
        _audioPlayer.VolumeChanged += OnAudioPlayerVolumeChanged;
        _audioPlayer.PositionChanged += OnAudioPlayerPositionChanged;
        _audioPlayer.ErrorOccurred += OnAudioPlayerErrorOccurred;
        _audioPlayer.MediaOpened += OnAudioPlayerMediaOpened;
        _audioPlayer.DurationChanged += OnAudioPlayerDurationChanged;
        _audioPlayer.SmtcNextButtonPressed += OnAudioPlayerSmtcNextButtonPressed;
        _audioPlayer.SmtcPreviousButtonPressed += OnAudioPlayerSmtcPreviousButtonPressed;
    }

    /// <inheritdoc />
    public Song? CurrentTrack { get; private set; }

    /// <inheritdoc />
    public long? CurrentListenHistoryId => _currentListenHistoryId;

    /// <inheritdoc />
    public bool IsPlaying => _audioPlayer.IsPlaying;

    /// <inheritdoc />
    public TimeSpan CurrentPosition => _audioPlayer.CurrentPosition;

    /// <inheritdoc />
    public TimeSpan Duration => _audioPlayer.Duration;

    /// <inheritdoc />
    public double Volume => _audioPlayer.Volume;

    /// <inheritdoc />
    public bool IsMuted => _audioPlayer.IsMuted;

    /// <inheritdoc />
    public IReadOnlyList<Song> PlaybackQueue => _playbackQueue.AsReadOnly();

    /// <inheritdoc />
    public IReadOnlyList<Song> ShuffledQueue => _shuffledQueue.AsReadOnly();

    /// <inheritdoc />
    public int CurrentQueueIndex { get; private set; } = -1;

    /// <inheritdoc />
    public bool IsShuffleEnabled { get; private set; }

    /// <inheritdoc />
    public RepeatMode CurrentRepeatMode { get; private set; } = RepeatMode.Off;

    /// <inheritdoc />
    public bool IsTransitioningTrack { get; private set; }

    /// <inheritdoc />
    public event Action? TrackChanged;

    /// <inheritdoc />
    public event Action? PlaybackStateChanged;

    /// <inheritdoc />
    public event Action? VolumeStateChanged;

    /// <inheritdoc />
    public event Action? ShuffleModeChanged;

    /// <inheritdoc />
    public event Action? RepeatModeChanged;

    /// <inheritdoc />
    public event Action? QueueChanged;

    /// <inheritdoc />
    public event Action? PositionChanged;

    /// <inheritdoc />
    public event Action? DurationChanged;

    /// <inheritdoc />
    public async Task InitializeAsync() {
        if (_isInitialized) return;
        Debug.WriteLine("[MusicPlaybackService] INFO: Initializing service.");

        try {
            await _audioPlayer.SetVolumeAsync(await _settingsService.GetInitialVolumeAsync());
            await _audioPlayer.SetMuteAsync(await _settingsService.GetInitialMuteStateAsync());
            IsShuffleEnabled = await _settingsService.GetInitialShuffleStateAsync();
            CurrentRepeatMode = await _settingsService.GetInitialRepeatModeAsync();

            var restorePlayback = await _settingsService.GetRestorePlaybackStateEnabledAsync();
            if (restorePlayback) {
                Debug.WriteLine("[MusicPlaybackService] INFO: Restore playback state is enabled. Attempting to restore.");
                var savedState = await _settingsService.GetPlaybackStateAsync();
                if (savedState != null && !await RestoreInternalPlaybackStateAsync(savedState)) {
                    Debug.WriteLine("[MusicPlaybackService] WARN: Failed to restore playback state. Clearing queues.");
                    ClearQueuesInternal();
                }
            }
        }
        catch (Exception ex) {
            Debug.WriteLine($"[MusicPlaybackService] ERROR: Error during initialization: {ex.Message}. Using default settings.");
            await _audioPlayer.SetVolumeAsync(0.5);
            await _audioPlayer.SetMuteAsync(false);
            IsShuffleEnabled = false;
            CurrentRepeatMode = RepeatMode.Off;
            ClearQueuesInternal();
        }

        _isInitialized = true;
        UpdateSmtcControls();

        VolumeStateChanged?.Invoke();
        ShuffleModeChanged?.Invoke();
        RepeatModeChanged?.Invoke();
        QueueChanged?.Invoke();
        TrackChanged?.Invoke();
        PlaybackStateChanged?.Invoke();
        PositionChanged?.Invoke();
        Debug.WriteLine("[MusicPlaybackService] INFO: Initialization complete.");
    }

    /// <inheritdoc />
    public async Task PlayAsync(Song song) {
        if (song == null) return;
        Debug.WriteLine($"[MusicPlaybackService] INFO: PlayAsync called for single song: '{song.Title}'.");

        _playbackQueue = new List<Song> { song };
        if (IsShuffleEnabled) {
            _shuffledQueue = new List<Song> { song };
        }
        else {
            _shuffledQueue.Clear();
        }

        QueueChanged?.Invoke();
        await PlayQueueItemAsync(0);
    }

    /// <inheritdoc />
    public async Task PlayAsync(IEnumerable<Song> songs, int startIndex = 0, bool startShuffled = false) {
        var songList = songs?.Distinct().ToList() ?? new List<Song>();
        Debug.WriteLine($"[MusicPlaybackService] INFO: PlayAsync called for collection of {songList.Count} songs.");
        if (!songList.Any()) {
            await StopAsync();
            ClearQueuesInternal();
            QueueChanged?.Invoke();
            UpdateSmtcControls();
            return;
        }

        if (IsShuffleEnabled != startShuffled) {
            await SetShuffleAsync(startShuffled);
        }

        _playbackQueue = songList;
        QueueChanged?.Invoke();

        if (IsShuffleEnabled) {
            GenerateShuffledQueue();
            _currentShuffledIndex = _random.Next(_shuffledQueue.Count);
            var songToPlay = _shuffledQueue[_currentShuffledIndex];
            var actualStartIndex = _playbackQueue.IndexOf(songToPlay);
            await PlayQueueItemAsync(actualStartIndex);
        }
        else {
            _shuffledQueue.Clear();
            _currentShuffledIndex = -1;
            var actualStartIndex = Math.Clamp(startIndex, 0, _playbackQueue.Count - 1);
            await PlayQueueItemAsync(actualStartIndex);
        }
    }

    /// <inheritdoc />
    public async Task PlayPauseAsync() {
        if (_audioPlayer.IsPlaying) {
            await _audioPlayer.PauseAsync();
            return;
        }

        if (CurrentTrack != null) {
            await _audioPlayer.PlayAsync();
            return;
        }

        if (_playbackQueue.Any()) {
            int indexToPlay = 0;
            if (IsShuffleEnabled && _shuffledQueue.Any()) {
                var shuffledIndex = _currentShuffledIndex >= 0 ? _currentShuffledIndex : 0;
                var songToPlay = _shuffledQueue.ElementAtOrDefault(shuffledIndex);
                if (songToPlay != null) {
                    indexToPlay = _playbackQueue.IndexOf(songToPlay);
                }
            }
            else {
                indexToPlay = CurrentQueueIndex >= 0 ? CurrentQueueIndex : 0;
            }

            if (indexToPlay >= 0 && indexToPlay < _playbackQueue.Count) {
                await PlayQueueItemAsync(indexToPlay);
            }
        }
    }

    /// <inheritdoc />
    public async Task StopAsync() {
        Debug.WriteLine("[MusicPlaybackService] INFO: StopAsync called.");
        await _audioPlayer.StopAsync();
        CurrentTrack = null;
        _currentListenHistoryId = null;
        IsTransitioningTrack = false;
        TrackChanged?.Invoke();
        PositionChanged?.Invoke();
        UpdateSmtcControls();
    }

    /// <inheritdoc />
    public async Task NextAsync() {
        if (CurrentRepeatMode == RepeatMode.RepeatOne && CurrentTrack != null) {
            await PlayQueueItemAsync(CurrentQueueIndex);
            return;
        }

        if (TryGetNextTrackIndex(moveForward: true, out int nextIndex)) {
            await PlayQueueItemAsync(nextIndex);
        }
        else {
            Debug.WriteLine("[MusicPlaybackService] INFO: End of queue reached. Stopping playback.");
            await StopAsync();
            CurrentQueueIndex = -1;
            _currentShuffledIndex = -1;
        }
    }

    /// <inheritdoc />
    public async Task PreviousAsync() {
        if (CurrentTrack != null && _audioPlayer.CurrentPosition.TotalSeconds > 3 && CurrentRepeatMode != RepeatMode.RepeatOne) {
            Debug.WriteLine("[MusicPlaybackService] INFO: Restarting current track.");
            await SeekAsync(TimeSpan.Zero);
            if (!_audioPlayer.IsPlaying) {
                await _audioPlayer.PlayAsync();
            }
            return;
        }

        if (CurrentRepeatMode == RepeatMode.RepeatOne && CurrentTrack != null) {
            await PlayQueueItemAsync(CurrentQueueIndex);
            return;
        }

        if (TryGetNextTrackIndex(moveForward: false, out int prevIndex)) {
            await PlayQueueItemAsync(prevIndex);
        }
        else {
            Debug.WriteLine("[MusicPlaybackService] INFO: Beginning of queue reached. Stopping playback.");
            await StopAsync();
            CurrentQueueIndex = -1;
            _currentShuffledIndex = -1;
        }
    }

    /// <inheritdoc />
    public async Task SeekAsync(TimeSpan position) {
        if (CurrentTrack != null) {
            await _audioPlayer.SeekAsync(position);
        }
    }

    /// <inheritdoc />
    public async Task PlayAlbumAsync(Guid albumId) {
        Debug.WriteLine($"[MusicPlaybackService] INFO: PlayAlbumAsync called for AlbumId: {albumId}.");
        var songIds = await _libraryService.GetAllSongIdsByAlbumIdAsync(albumId, SongSortOrder.TrackNumberAsc);
        await PlayFromOrderedIdsAsync(songIds, startShuffled: false);
    }

    /// <inheritdoc />
    public async Task PlayArtistAsync(Guid artistId) {
        Debug.WriteLine($"[MusicPlaybackService] INFO: PlayArtistAsync called for ArtistId: {artistId}.");
        var songIds = await _libraryService.GetAllSongIdsByArtistIdAsync(artistId, SongSortOrder.TitleAsc);
        await PlayFromOrderedIdsAsync(songIds, startShuffled: false);
    }

    /// <inheritdoc />
    public async Task PlayFolderAsync(Guid folderId) {
        Debug.WriteLine($"[MusicPlaybackService] INFO: PlayFolderAsync called for FolderId: {folderId}.");
        var songIds = await _libraryService.GetAllSongIdsByFolderIdAsync(folderId, SongSortOrder.TitleAsc);
        await PlayFromOrderedIdsAsync(songIds, startShuffled: false);
    }

    /// <inheritdoc />
    public async Task PlayPlaylistAsync(Guid playlistId) {
        Debug.WriteLine($"[MusicPlaybackService] INFO: PlayPlaylistAsync called for PlaylistId: {playlistId}.");
        var orderedSongs = (await _libraryService.GetSongsInPlaylistOrderedAsync(playlistId))?.ToList();
        await PlayAsync(orderedSongs ?? new List<Song>(), 0, startShuffled: false);
    }

    /// <inheritdoc />
    public async Task PlayGenreAsync(Guid genreId) {
        Debug.WriteLine($"[MusicPlaybackService] INFO: PlayGenreAsync called for GenreId: {genreId}.");
        var songIds = await _libraryService.GetAllSongIdsByGenreIdAsync(genreId, SongSortOrder.TitleAsc);
        await PlayFromOrderedIdsAsync(songIds, startShuffled: false);
    }

    /// <inheritdoc />
    public async Task SetVolumeAsync(double volume) {
        await _audioPlayer.SetVolumeAsync(volume);
        await _settingsService.SaveVolumeAsync(volume);
    }

    /// <inheritdoc />
    public async Task ToggleMuteAsync() {
        var newMuteState = !_audioPlayer.IsMuted;
        await _audioPlayer.SetMuteAsync(newMuteState);
        await _settingsService.SaveMuteStateAsync(newMuteState);
    }

    /// <inheritdoc />
    public async Task SetShuffleAsync(bool enable) {
        if (IsShuffleEnabled == enable) return;
        Debug.WriteLine($"[MusicPlaybackService] INFO: SetShuffleAsync called. New state: {enable}.");

        IsShuffleEnabled = enable;
        if (IsShuffleEnabled) {
            GenerateShuffledQueue();
            _currentShuffledIndex = CurrentTrack != null ? _shuffledQueue.IndexOf(CurrentTrack) : -1;
        }
        else {
            _shuffledQueue.Clear();
            _currentShuffledIndex = -1;
        }

        await _settingsService.SaveShuffleStateAsync(IsShuffleEnabled);
        ShuffleModeChanged?.Invoke();
        QueueChanged?.Invoke();
        UpdateSmtcControls();
    }

    /// <inheritdoc />
    public async Task SetRepeatModeAsync(RepeatMode mode) {
        if (CurrentRepeatMode == mode) return;
        Debug.WriteLine($"[MusicPlaybackService] INFO: SetRepeatModeAsync called. New mode: {mode}.");
        CurrentRepeatMode = mode;
        await _settingsService.SaveRepeatModeAsync(CurrentRepeatMode);
        RepeatModeChanged?.Invoke();
        UpdateSmtcControls();
    }

    /// <inheritdoc />
    public Task AddToQueueAsync(Song song) {
        if (song == null || _playbackQueue.Contains(song)) return Task.CompletedTask;
        Debug.WriteLine($"[MusicPlaybackService] INFO: Adding song '{song.Title}' to queue.");

        _playbackQueue.Add(song);
        if (IsShuffleEnabled) {
            GenerateShuffledQueue();
        }
        QueueChanged?.Invoke();
        UpdateSmtcControls();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task AddRangeToQueueAsync(IEnumerable<Song> songs) {
        if (songs == null || !songs.Any()) return Task.CompletedTask;

        var currentQueueSet = new HashSet<Song>(_playbackQueue);
        var songsToAdd = songs.Where(s => currentQueueSet.Add(s)).ToList();

        if (songsToAdd.Any()) {
            Debug.WriteLine($"[MusicPlaybackService] INFO: Adding range of {songsToAdd.Count} songs to queue.");
            _playbackQueue.AddRange(songsToAdd);
            if (IsShuffleEnabled) {
                GenerateShuffledQueue();
            }
            QueueChanged?.Invoke();
            UpdateSmtcControls();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task PlayNextAsync(Song song) {
        if (song == null) return Task.CompletedTask;
        Debug.WriteLine($"[MusicPlaybackService] INFO: Setting song '{song.Title}' to play next.");

        _playbackQueue.Remove(song);
        if (IsShuffleEnabled) {
            _shuffledQueue.Remove(song);
        }

        var currentTrackIndex = CurrentTrack != null ? _playbackQueue.IndexOf(CurrentTrack) : -1;
        var insertIndex = currentTrackIndex == -1 ? 0 : currentTrackIndex + 1;
        _playbackQueue.Insert(insertIndex, song);

        if (IsShuffleEnabled) {
            var currentShuffledIndex = CurrentTrack != null ? _shuffledQueue.IndexOf(CurrentTrack) : -1;
            var shuffledInsertIndex = currentShuffledIndex == -1 ? 0 : currentShuffledIndex + 1;
            _shuffledQueue.Insert(shuffledInsertIndex, song);
        }

        QueueChanged?.Invoke();
        UpdateSmtcControls();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task RemoveFromQueueAsync(Song song) {
        if (song == null) return;

        var originalIndex = _playbackQueue.IndexOf(song);
        if (originalIndex == -1) return;
        Debug.WriteLine($"[MusicPlaybackService] INFO: Removing song '{song.Title}' from queue.");

        bool isRemovingCurrentTrack = CurrentTrack == song;

        if (isRemovingCurrentTrack) {
            Debug.WriteLine("[MusicPlaybackService] INFO: Removed song was the current track. Advancing to next.");
            await _audioPlayer.StopAsync();
            _playbackQueue.RemoveAt(originalIndex);
            if (IsShuffleEnabled) _shuffledQueue.Remove(song);

            if (_playbackQueue.Any()) {
                int nextIndexToPlay = originalIndex;
                if (nextIndexToPlay >= _playbackQueue.Count) {
                    nextIndexToPlay = CurrentRepeatMode == RepeatMode.RepeatAll ? 0 : -1;
                }

                if (nextIndexToPlay != -1) {
                    await PlayQueueItemAsync(nextIndexToPlay);
                }
                else {
                    await StopAsync();
                    ClearQueuesInternal();
                    QueueChanged?.Invoke();
                }
            }
            else {
                await StopAsync();
                ClearQueuesInternal();
                QueueChanged?.Invoke();
            }
        }
        else {
            _playbackQueue.RemoveAt(originalIndex);
            if (IsShuffleEnabled) _shuffledQueue.Remove(song);

            if (CurrentTrack != null) {
                CurrentQueueIndex = _playbackQueue.IndexOf(CurrentTrack);
                if (IsShuffleEnabled) {
                    _currentShuffledIndex = _shuffledQueue.IndexOf(CurrentTrack);
                }
            }
            QueueChanged?.Invoke();
            UpdateSmtcControls();
        }
    }

    /// <inheritdoc />
    public async Task PlayQueueItemAsync(int originalQueueIndex) {
        if (originalQueueIndex < 0 || originalQueueIndex >= _playbackQueue.Count) {
            Debug.WriteLine($"[MusicPlaybackService] WARN: PlayQueueItemAsync called with invalid index {originalQueueIndex}. Stopping.");
            await StopAsync();
            return;
        }

        IsTransitioningTrack = true;
        CurrentTrack = _playbackQueue[originalQueueIndex];
        CurrentQueueIndex = originalQueueIndex;
        Debug.WriteLine($"[MusicPlaybackService] INFO: Preparing to play track '{CurrentTrack.Title}' at index {originalQueueIndex}.");

        _currentListenHistoryId = await _libraryService.CreateListenHistoryEntryAsync(CurrentTrack.Id);

        if (IsShuffleEnabled) {
            _currentShuffledIndex = _shuffledQueue.IndexOf(CurrentTrack);
        }

        await _audioPlayer.LoadAsync(CurrentTrack);
        await _audioPlayer.PlayAsync();
        UpdateSmtcControls();
    }

    /// <inheritdoc />
    public async Task ClearQueueAsync() {
        if (!_playbackQueue.Any()) return;
        Debug.WriteLine("[MusicPlaybackService] INFO: Clearing playback queue.");
        await StopAsync();
        ClearQueuesInternal();
        QueueChanged?.Invoke();
        UpdateSmtcControls();
    }

    /// <inheritdoc />
    public async Task SavePlaybackStateAsync() {
        if (!_isInitialized) return;
        Debug.WriteLine("[MusicPlaybackService] INFO: Saving playback state.");

        var state = new PlaybackState {
            CurrentTrackId = CurrentTrack?.Id,
            CurrentPositionSeconds = CurrentTrack != null ? _audioPlayer.CurrentPosition.TotalSeconds : 0,
            PlaybackQueueTrackIds = _playbackQueue.Select(s => s.Id).ToList(),
            CurrentPlaybackQueueIndex = CurrentQueueIndex,
            ShuffledQueueTrackIds = IsShuffleEnabled ? _shuffledQueue.Select(s => s.Id).ToList() : new List<Guid>(),
            CurrentShuffledQueueIndex = _currentShuffledIndex
        };
        await _settingsService.SavePlaybackStateAsync(state);
    }

    private async Task PlayFromOrderedIdsAsync(IList<Guid> orderedSongIds, bool startShuffled) {
        if (orderedSongIds == null || !orderedSongIds.Any()) {
            await PlayAsync(new List<Song>(), 0, startShuffled: false);
            return;
        }

        var songMap = await _libraryService.GetSongsByIdsAsync(orderedSongIds);

        var orderedSongs = orderedSongIds
            .Select(id => songMap.GetValueOrDefault(id))
            .Where(s => s != null)
            .Cast<Song>()
            .ToList();

        await PlayAsync(orderedSongs, 0, startShuffled);
    }

    private void ClearQueuesInternal() {
        _playbackQueue.Clear();
        _shuffledQueue.Clear();
        CurrentTrack = null;
        CurrentQueueIndex = -1;
        _currentShuffledIndex = -1;
        _currentListenHistoryId = null;
    }

    private async Task<bool> RestoreInternalPlaybackStateAsync(PlaybackState state) {
        Debug.WriteLine("[MusicPlaybackService] INFO: Executing internal playback state restoration.");
        var songIds = new HashSet<Guid>(state.PlaybackQueueTrackIds ?? Enumerable.Empty<Guid>());
        if (!songIds.Any()) return false;

        var songMap = await _libraryService.GetSongsByIdsAsync(songIds);
        if (!songMap.Any()) return false;

        _playbackQueue = (state.PlaybackQueueTrackIds ?? Enumerable.Empty<Guid>())
            .Select(id => songMap.GetValueOrDefault(id))
            .Where(s => s != null)
            .Cast<Song>()
            .ToList();

        if (!_playbackQueue.Any()) return false;

        if (IsShuffleEnabled) {
            var shuffledIds = state.ShuffledQueueTrackIds ?? Enumerable.Empty<Guid>();
            _shuffledQueue = shuffledIds
                .Select(id => songMap.GetValueOrDefault(id))
                .Where(s => s != null)
                .Cast<Song>()
                .ToList();
            if (_shuffledQueue.Count != _playbackQueue.Count) {
                GenerateShuffledQueue();
            }
        }

        if (state.CurrentTrackId.HasValue && songMap.TryGetValue(state.CurrentTrackId.Value, out var currentSong)) {
            CurrentTrack = currentSong;
            CurrentQueueIndex = _playbackQueue.IndexOf(currentSong);
            if (IsShuffleEnabled) {
                _currentShuffledIndex = _shuffledQueue.IndexOf(currentSong);
            }

            _currentListenHistoryId = null;

            await _audioPlayer.LoadAsync(CurrentTrack);
            // A short delay can be necessary to allow the audio player to load the media
            // and report a valid duration before we attempt to seek.
            await Task.Delay(50);
            if (_audioPlayer.Duration > TimeSpan.Zero && state.CurrentPositionSeconds > 0) {
                await _audioPlayer.SeekAsync(TimeSpan.FromSeconds(state.CurrentPositionSeconds));
            }
        }
        else {
            CurrentQueueIndex = state.CurrentPlaybackQueueIndex;
            _currentShuffledIndex = state.CurrentShuffledQueueIndex;
        }
        Debug.WriteLine("[MusicPlaybackService] INFO: Internal playback state restoration complete.");
        return true;
    }

    private void GenerateShuffledQueue() {
        if (!_playbackQueue.Any()) {
            _shuffledQueue.Clear();
            return;
        }
        Debug.WriteLine("[MusicPlaybackService] INFO: Generating new shuffled queue.");

        _shuffledQueue = new List<Song>(_playbackQueue);
        var n = _shuffledQueue.Count;
        while (n > 1) {
            n--;
            var k = _random.Next(n + 1);
            (_shuffledQueue[k], _shuffledQueue[n]) = (_shuffledQueue[n], _shuffledQueue[k]);
        }
    }

    private bool TryGetNextTrackIndex(bool moveForward, out int nextPlaybackQueueIndex) {
        nextPlaybackQueueIndex = -1;
        if (!_playbackQueue.Any()) return false;

        int nextIndex = -1;

        if (IsShuffleEnabled) {
            int nextShuffledIndex = -1;
            if (moveForward) {
                if (_currentShuffledIndex < _shuffledQueue.Count - 1)
                    nextShuffledIndex = _currentShuffledIndex + 1;
                else if (CurrentRepeatMode == RepeatMode.RepeatAll)
                    nextShuffledIndex = 0;
            }
            else {
                if (_currentShuffledIndex > 0)
                    nextShuffledIndex = _currentShuffledIndex - 1;
                else if (CurrentRepeatMode == RepeatMode.RepeatAll)
                    nextShuffledIndex = _shuffledQueue.Count - 1;
            }

            if (nextShuffledIndex != -1) {
                nextIndex = _playbackQueue.IndexOf(_shuffledQueue[nextShuffledIndex]);
            }
        }
        else {
            if (moveForward) {
                if (CurrentQueueIndex < _playbackQueue.Count - 1)
                    nextIndex = CurrentQueueIndex + 1;
                else if (CurrentRepeatMode == RepeatMode.RepeatAll)
                    nextIndex = 0;
            }
            else {
                if (CurrentQueueIndex > 0)
                    nextIndex = CurrentQueueIndex - 1;
                else if (CurrentRepeatMode == RepeatMode.RepeatAll)
                    nextIndex = _playbackQueue.Count - 1;
            }
        }

        if (nextIndex != -1) {
            nextPlaybackQueueIndex = nextIndex;
            return true;
        }

        return false;
    }

    private void UpdateSmtcControls() {
        bool canGoNext = false;
        bool canGoPrevious = false;

        if (_playbackQueue.Any()) {
            if (CurrentRepeatMode != RepeatMode.Off) {
                canGoNext = true;
                canGoPrevious = true;
            }
            else if (IsShuffleEnabled) {
                canGoNext = _currentShuffledIndex < _shuffledQueue.Count - 1;
                canGoPrevious = _currentShuffledIndex > 0;
            }
            else {
                canGoNext = CurrentQueueIndex < _playbackQueue.Count - 1;
                canGoPrevious = CurrentQueueIndex > 0;
            }

            if (CurrentTrack != null) {
                canGoPrevious = true;
            }
        }

        _audioPlayer.UpdateSmtcButtonStates(canGoNext, canGoPrevious);
    }

    private async void OnAudioPlayerPlaybackEnded() {
        Debug.WriteLine("[MusicPlaybackService] INFO: Received PlaybackEnded event from audio player.");
        await NextAsync();
    }

    private void OnAudioPlayerStateChanged() {
        PlaybackStateChanged?.Invoke();
        if (IsTransitioningTrack && _audioPlayer.IsPlaying) {
            Debug.WriteLine("[MusicPlaybackService] INFO: Track transition complete (player is now playing).");
            IsTransitioningTrack = false;
        }
    }

    private void OnAudioPlayerVolumeChanged() {
        VolumeStateChanged?.Invoke();
    }

    private void OnAudioPlayerPositionChanged() {
        PositionChanged?.Invoke();
    }

    private void OnAudioPlayerDurationChanged() {
        DurationChanged?.Invoke();
    }

    private async void OnAudioPlayerErrorOccurred(string errorMessage) {
        Debug.WriteLine($"[MusicPlaybackService] ERROR: Received error from audio player: {errorMessage}");
        IsTransitioningTrack = false;
        await StopAsync();
    }

    private void OnAudioPlayerMediaOpened() {
        Debug.WriteLine("[MusicPlaybackService] INFO: Received MediaOpened event from audio player.");
        TrackChanged?.Invoke();
        UpdateSmtcControls();
    }

    private async void OnAudioPlayerSmtcNextButtonPressed() {
        await NextAsync();
    }

    private async void OnAudioPlayerSmtcPreviousButtonPressed() {
        await PreviousAsync();
    }

    /// <inheritdoc />
    public void Dispose() {
        Debug.WriteLine("[MusicPlaybackService] INFO: Disposing service.");
        _audioPlayer.PlaybackEnded -= OnAudioPlayerPlaybackEnded;
        _audioPlayer.StateChanged -= OnAudioPlayerStateChanged;
        _audioPlayer.VolumeChanged -= OnAudioPlayerVolumeChanged;
        _audioPlayer.PositionChanged -= OnAudioPlayerPositionChanged;
        _audioPlayer.ErrorOccurred -= OnAudioPlayerErrorOccurred;
        _audioPlayer.MediaOpened -= OnAudioPlayerMediaOpened;
        _audioPlayer.DurationChanged -= OnAudioPlayerDurationChanged;
        _audioPlayer.SmtcNextButtonPressed -= OnAudioPlayerSmtcNextButtonPressed;
        _audioPlayer.SmtcPreviousButtonPressed -= OnAudioPlayerSmtcPreviousButtonPressed;
        GC.SuppressFinalize(this);
        Debug.WriteLine("[MusicPlaybackService] INFO: Service disposed.");
    }
}