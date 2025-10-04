using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Manages music playback, queue, and state by coordinating between the audio player,
///     library service, and settings service.
/// </summary>
public class MusicPlaybackService : IMusicPlaybackService, IDisposable
{
    private readonly IAudioPlayer _audioPlayer;
    private readonly ILibraryService _libraryService;
    private readonly ILogger<MusicPlaybackService> _logger;
    private readonly IMetadataService _metadataService;
    private readonly Random _random = new();
    private readonly ISettingsService _settingsService;
    private int _currentShuffledIndex = -1;

    private bool _isInitialized;
    private List<Song> _playbackQueue = new();
    private List<Song> _shuffledQueue = new();

    public MusicPlaybackService(
        ISettingsService settingsService,
        IAudioPlayer audioPlayer,
        ILibraryService libraryService,
        IMetadataService metadataService,
        ILogger<MusicPlaybackService> logger)
    {
        _settingsService = settingsService;
        _audioPlayer = audioPlayer;
        _libraryService = libraryService;
        _metadataService = metadataService;
        _logger = logger;

        _audioPlayer.PlaybackEnded += OnAudioPlayerPlaybackEnded;
        _audioPlayer.StateChanged += OnAudioPlayerStateChanged;
        _audioPlayer.VolumeChanged += OnAudioPlayerVolumeChanged;
        _audioPlayer.PositionChanged += OnAudioPlayerPositionChanged;
        _audioPlayer.ErrorOccurred += OnAudioPlayerErrorOccurred;
        _audioPlayer.MediaOpened += OnAudioPlayerMediaOpened;
        _audioPlayer.DurationChanged += OnAudioPlayerDurationChanged;
        _audioPlayer.SmtcNextButtonPressed += OnAudioPlayerSmtcNextButtonPressed;
        _audioPlayer.SmtcPreviousButtonPressed += OnAudioPlayerSmtcPreviousButtonPressed;

        EqualizerBands = _audioPlayer.GetEqualizerBands();
    }

    public Song? CurrentTrack { get; private set; }
    public long? CurrentListenHistoryId { get; private set; }
    public bool IsPlaying => _audioPlayer.IsPlaying;
    public TimeSpan CurrentPosition => _audioPlayer.CurrentPosition;
    public TimeSpan Duration => _audioPlayer.Duration;
    public double Volume => _audioPlayer.Volume;
    public bool IsMuted => _audioPlayer.IsMuted;
    public IReadOnlyList<Song> PlaybackQueue => _playbackQueue.AsReadOnly();
    public IReadOnlyList<Song> ShuffledQueue => _shuffledQueue.AsReadOnly();
    public int CurrentQueueIndex { get; private set; } = -1;
    public bool IsShuffleEnabled { get; private set; }
    public RepeatMode CurrentRepeatMode { get; private set; } = RepeatMode.Off;
    public bool IsTransitioningTrack { get; private set; }
    public IReadOnlyList<(uint Index, float Frequency)> EqualizerBands { get; }
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

    public async Task InitializeAsync(bool restoreLastSession = true)
    {
        if (_isInitialized)
        {
            _logger.LogInformation("MusicPlaybackService is already initialized.");
            return;
        }

        _logger.LogInformation("Initializing MusicPlaybackService...");

        try
        {
            await _audioPlayer.SetVolumeAsync(await _settingsService.GetInitialVolumeAsync());
            await _audioPlayer.SetMuteAsync(await _settingsService.GetInitialMuteStateAsync());
            IsShuffleEnabled = await _settingsService.GetInitialShuffleStateAsync();
            CurrentRepeatMode = await _settingsService.GetInitialRepeatModeAsync();

            CurrentEqualizerSettings = await _settingsService.GetEqualizerSettingsAsync();
            CurrentEqualizerSettings ??= new EqualizerSettings
            {
                Preamp = 10.0f,
                BandGains = Enumerable.Repeat(0.0f, EqualizerBands.Count).ToList()
            };
            _audioPlayer.ApplyEqualizerSettings(CurrentEqualizerSettings);

            var restoredSuccessfully = false;
            if (restoreLastSession && await _settingsService.GetRestorePlaybackStateEnabledAsync())
            {
                var savedState = await _settingsService.GetPlaybackStateAsync();
                if (savedState != null) restoredSuccessfully = await RestoreInternalPlaybackStateAsync(savedState);
            }

            if (!restoredSuccessfully) ClearQueuesInternal();

            _logger.LogInformation("MusicPlaybackService initialized successfully. Session restored: {IsRestored}",
                restoredSuccessfully);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MusicPlaybackService initialization failed. Using default settings.");
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
    }

    public async Task PlayTransientFileAsync(string filePath)
    {
        _logger.LogInformation("Playing transient file: {FilePath}", filePath);
        var metadata = await _metadataService.ExtractMetadataAsync(filePath);

        var transientSong = new Song
        {
            FilePath = filePath,
            Title = metadata.Title,
            Artist = new Artist { Name = metadata.Artist ?? "Unknown Artist" },
            Album = new Album { Title = metadata.Album ?? "Unknown Album" },
            Duration = metadata.Duration,
            AlbumArtUriFromTrack = metadata.CoverArtUri
        };

        IsTransitioningTrack = true;
        ClearQueuesInternal();
        QueueChanged?.Invoke();

        CurrentTrack = transientSong;
        CurrentQueueIndex = -1;
        CurrentListenHistoryId = null;

        await _audioPlayer.LoadAsync(CurrentTrack);
        await _audioPlayer.PlayAsync();
        UpdateSmtcControls();

        TrackChanged?.Invoke();
    }

    public async Task PlayAsync(Song song)
    {
        if (song == null) return;

        _logger.LogInformation("Playing single song: '{SongTitle}' ({SongId})", song.Title, song.Id);
        _playbackQueue = new List<Song> { song };
        if (IsShuffleEnabled)
            _shuffledQueue = new List<Song> { song };
        else
            _shuffledQueue.Clear();

        QueueChanged?.Invoke();
        await PlayQueueItemAsync(0);
    }

    public async Task PlayAsync(IEnumerable<Song> songs, int startIndex = 0, bool startShuffled = false)
    {
        var songList = songs?.Distinct().ToList() ?? new List<Song>();
        if (!songList.Any())
        {
            _logger.LogWarning("PlayAsync called with an empty song list. Stopping playback.");
            await StopAsync();
            ClearQueuesInternal();
            QueueChanged?.Invoke();
            UpdateSmtcControls();
            return;
        }

        _logger.LogInformation(
            "Playing a new queue of {SongCount} songs. Start index: {StartIndex}, Shuffled: {IsShuffled}",
            songList.Count, startIndex, startShuffled);

        if (IsShuffleEnabled != startShuffled) await SetShuffleAsync(startShuffled);

        _playbackQueue = songList;
        QueueChanged?.Invoke();

        if (IsShuffleEnabled)
        {
            GenerateShuffledQueue();
            var songToPlay = _shuffledQueue.ElementAtOrDefault(startIndex);
            if (songToPlay == null)
            {
                startIndex = 0;
                songToPlay = _shuffledQueue.FirstOrDefault();
            }

            if (songToPlay != null)
            {
                var actualPlaybackIndex = _playbackQueue.IndexOf(songToPlay);
                await PlayQueueItemAsync(actualPlaybackIndex);
            }
            else
            {
                await StopAsync();
            }
        }
        else
        {
            if (startIndex < 0 || startIndex >= _playbackQueue.Count) startIndex = 0;
            await PlayQueueItemAsync(startIndex);
        }
    }

    public async Task PlayPauseAsync()
    {
        if (_audioPlayer.IsPlaying)
        {
            await _audioPlayer.PauseAsync();
            return;
        }

        if (CurrentTrack != null)
        {
            await _audioPlayer.PlayAsync();
            return;
        }

        // If no track is loaded but a queue exists, play from the last known position.
        if (_playbackQueue.Any())
        {
            var indexToPlay = CurrentQueueIndex >= 0 ? CurrentQueueIndex : 0;

            if (IsShuffleEnabled && _shuffledQueue.Any())
            {
                var shuffledIndex = _currentShuffledIndex >= 0 ? _currentShuffledIndex : 0;
                var songToPlay = _shuffledQueue.ElementAtOrDefault(shuffledIndex);
                if (songToPlay != null) indexToPlay = _playbackQueue.IndexOf(songToPlay);
            }

            if (indexToPlay >= 0 && indexToPlay < _playbackQueue.Count) await PlayQueueItemAsync(indexToPlay);
        }
    }

    public async Task StopAsync()
    {
        await _audioPlayer.StopAsync();

        // Null the track to indicate a "stopped" state but preserve the queue indices.
        // This is crucial for Next/Previous commands to work correctly from a stopped state.
        CurrentTrack = null;
        CurrentListenHistoryId = null;
        IsTransitioningTrack = false;
        TrackChanged?.Invoke();
        PositionChanged?.Invoke();
        UpdateSmtcControls();
    }

    public async Task NextAsync()
    {
        if (CurrentRepeatMode == RepeatMode.RepeatOne && CurrentTrack != null)
        {
            await PlayQueueItemAsync(CurrentQueueIndex);
            return;
        }

        // This logic handles two distinct cases for robust navigation.
        if (CurrentTrack != null)
        {
            // Case 1: A track is currently active. Find the next one in the sequence.
            if (TryGetNextTrackIndex(true, out var nextIndex))
                await PlayQueueItemAsync(nextIndex);
            else
                // Reached the end of the queue.
                await StopAsync();
        }
        else if (_playbackQueue.Any())
        {
            // Case 2: The player is stopped at a queue boundary.
            // Pressing Next should resume playback from the current position.
            await PlayQueueItemAsync(CurrentQueueIndex);
        }
    }

    public async Task PreviousAsync()
    {
        // If the track has played for more than 3 seconds, restart it.
        if (CurrentTrack != null && _audioPlayer.CurrentPosition.TotalSeconds > 3 &&
            CurrentRepeatMode != RepeatMode.RepeatOne)
        {
            await SeekAsync(TimeSpan.Zero);
            if (!_audioPlayer.IsPlaying) await _audioPlayer.PlayAsync();
            return;
        }

        if (CurrentRepeatMode == RepeatMode.RepeatOne && CurrentTrack != null)
        {
            await PlayQueueItemAsync(CurrentQueueIndex);
            return;
        }

        // This logic mirrors NextAsync for robust navigation.
        if (CurrentTrack != null)
        {
            // Case 1: A track is currently active. Find the previous one in the sequence.
            if (TryGetNextTrackIndex(false, out var prevIndex))
                await PlayQueueItemAsync(prevIndex);
            else
                // Reached the beginning of the queue.
                await StopAsync();
        }
        else if (_playbackQueue.Any())
        {
            // Case 2: The player is stopped at a queue boundary.
            // Pressing Previous should resume playback from the current position.
            await PlayQueueItemAsync(CurrentQueueIndex);
        }
    }

    public async Task SeekAsync(TimeSpan position)
    {
        if (CurrentTrack != null) await _audioPlayer.SeekAsync(position);
    }

    public async Task PlayAlbumAsync(Guid albumId)
    {
        var songIds = await _libraryService.GetAllSongIdsByAlbumIdAsync(albumId, SongSortOrder.TrackNumberAsc);
        await PlayFromOrderedIdsAsync(songIds, false);
    }

    public async Task PlayArtistAsync(Guid artistId)
    {
        var songIds = await _libraryService.GetAllSongIdsByArtistIdAsync(artistId, SongSortOrder.TitleAsc);
        await PlayFromOrderedIdsAsync(songIds, false);
    }

    public async Task PlayFolderAsync(Guid folderId)
    {
        var songIds = await _libraryService.GetAllSongIdsByFolderIdAsync(folderId, SongSortOrder.TitleAsc);
        await PlayFromOrderedIdsAsync(songIds, false);
    }

    public async Task PlayPlaylistAsync(Guid playlistId)
    {
        var orderedSongs = (await _libraryService.GetSongsInPlaylistOrderedAsync(playlistId))?.ToList();
        await PlayAsync(orderedSongs ?? new List<Song>());
    }

    public async Task PlayGenreAsync(Guid genreId)
    {
        var songIds = await _libraryService.GetAllSongIdsByGenreIdAsync(genreId, SongSortOrder.TitleAsc);
        await PlayFromOrderedIdsAsync(songIds, false);
    }

    public async Task SetVolumeAsync(double volume)
    {
        await _audioPlayer.SetVolumeAsync(volume);
        await _settingsService.SaveVolumeAsync(volume);
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
        _logger.LogInformation("Shuffle mode set to {ShuffleState}", IsShuffleEnabled);
        if (IsShuffleEnabled)
        {
            GenerateShuffledQueue();
            _currentShuffledIndex = CurrentTrack != null ? _shuffledQueue.IndexOf(CurrentTrack) : -1;
        }
        else
        {
            _shuffledQueue.Clear();
            _currentShuffledIndex = -1;
        }

        await _settingsService.SaveShuffleStateAsync(IsShuffleEnabled);
        ShuffleModeChanged?.Invoke();
        QueueChanged?.Invoke();
        UpdateSmtcControls();
    }

    public async Task SetRepeatModeAsync(RepeatMode mode)
    {
        if (CurrentRepeatMode == mode) return;

        CurrentRepeatMode = mode;
        _logger.LogInformation("Repeat mode set to {RepeatMode}", CurrentRepeatMode);
        await _settingsService.SaveRepeatModeAsync(CurrentRepeatMode);
        RepeatModeChanged?.Invoke();
        UpdateSmtcControls();
    }

    public Task AddToQueueAsync(Song song)
    {
        if (song == null || _playbackQueue.Contains(song)) return Task.CompletedTask;

        _playbackQueue.Add(song);
        if (IsShuffleEnabled)
        {
            GenerateShuffledQueue();
            if (CurrentTrack != null) _currentShuffledIndex = _shuffledQueue.IndexOf(CurrentTrack);
        }

        QueueChanged?.Invoke();
        UpdateSmtcControls();
        return Task.CompletedTask;
    }

    public Task AddRangeToQueueAsync(IEnumerable<Song> songs)
    {
        if (songs == null || !songs.Any()) return Task.CompletedTask;

        var currentQueueSet = _playbackQueue.ToHashSet();
        var songsToAdd = songs.Where(s => currentQueueSet.Add(s)).ToList();

        if (songsToAdd.Any())
        {
            _playbackQueue.AddRange(songsToAdd);
            if (IsShuffleEnabled)
            {
                GenerateShuffledQueue();
                if (CurrentTrack != null) _currentShuffledIndex = _shuffledQueue.IndexOf(CurrentTrack);
            }

            QueueChanged?.Invoke();
            UpdateSmtcControls();
        }

        return Task.CompletedTask;
    }

    public Task PlayNextAsync(Song song)
    {
        if (song == null) return Task.CompletedTask;

        _playbackQueue.Remove(song);
        if (IsShuffleEnabled) _shuffledQueue.Remove(song);

        var insertIndex = CurrentQueueIndex == -1 ? 0 : CurrentQueueIndex + 1;
        _playbackQueue.Insert(insertIndex, song);

        if (IsShuffleEnabled)
        {
            var shuffledInsertIndex = _currentShuffledIndex == -1 ? 0 : _currentShuffledIndex + 1;
            _shuffledQueue.Insert(shuffledInsertIndex, song);
        }

        QueueChanged?.Invoke();
        UpdateSmtcControls();
        return Task.CompletedTask;
    }

    public async Task RemoveFromQueueAsync(Song song)
    {
        if (song == null) return;

        var originalIndex = _playbackQueue.IndexOf(song);
        if (originalIndex == -1) return;

        var isRemovingCurrentTrack = CurrentTrack == song;

        if (isRemovingCurrentTrack)
        {
            _logger.LogInformation("Removing currently playing song '{SongTitle}' from queue.", song.Title);
            await _audioPlayer.StopAsync();
            _playbackQueue.RemoveAt(originalIndex);
            if (IsShuffleEnabled) _shuffledQueue.Remove(song);

            if (_playbackQueue.Any())
            {
                // Attempt to play the next song in the queue.
                var nextIndexToPlay = originalIndex;
                if (nextIndexToPlay >= _playbackQueue.Count)
                    // If the removed track was the last one, wrap around if repeat is on.
                    nextIndexToPlay = CurrentRepeatMode == RepeatMode.RepeatAll ? 0 : -1;

                if (nextIndexToPlay != -1)
                {
                    await PlayQueueItemAsync(nextIndexToPlay);
                }
                else
                {
                    await StopAsync();
                    ClearQueuesInternal();
                    QueueChanged?.Invoke();
                }
            }
            else
            {
                // The queue is now empty.
                await StopAsync();
                ClearQueuesInternal();
                QueueChanged?.Invoke();
            }
        }
        else
        {
            _playbackQueue.RemoveAt(originalIndex);
            if (IsShuffleEnabled) _shuffledQueue.Remove(song);

            if (CurrentTrack != null)
            {
                // Adjust the current index if the removed song was before the current one.
                if (originalIndex < CurrentQueueIndex) CurrentQueueIndex--;
                if (IsShuffleEnabled) _currentShuffledIndex = _shuffledQueue.IndexOf(CurrentTrack);
            }

            QueueChanged?.Invoke();
            UpdateSmtcControls();
        }
    }

    public async Task PlayQueueItemAsync(int originalQueueIndex)
    {
        if (originalQueueIndex < 0 || originalQueueIndex >= _playbackQueue.Count)
        {
            await StopAsync();
            return;
        }

        IsTransitioningTrack = true;
        CurrentTrack = _playbackQueue[originalQueueIndex];
        CurrentQueueIndex = originalQueueIndex;

        CurrentListenHistoryId = await _libraryService.CreateListenHistoryEntryAsync(CurrentTrack.Id);

        if (IsShuffleEnabled) _currentShuffledIndex = _shuffledQueue.IndexOf(CurrentTrack);

        _logger.LogInformation("Now playing '{SongTitle}' (Index: {QueueIndex}, Shuffled Index: {ShuffledIndex})",
            CurrentTrack.Title, CurrentQueueIndex, _currentShuffledIndex);
        await _audioPlayer.LoadAsync(CurrentTrack);
        await _audioPlayer.PlayAsync();
        UpdateSmtcControls();
    }

    public async Task ClearQueueAsync()
    {
        if (!_playbackQueue.Any()) return;
        _logger.LogInformation("Clearing playback queue.");
        await StopAsync();
        ClearQueuesInternal();
        QueueChanged?.Invoke();
        UpdateSmtcControls();
    }

    public async Task SavePlaybackStateAsync()
    {
        if (!_isInitialized) return;

        var state = new PlaybackState
        {
            CurrentTrackId = CurrentTrack?.Id,
            PlaybackQueueTrackIds = _playbackQueue.Select(s => s.Id).ToList(),
            CurrentPlaybackQueueIndex = CurrentQueueIndex,
            ShuffledQueueTrackIds = IsShuffleEnabled ? _shuffledQueue.Select(s => s.Id).ToList() : new List<Guid>(),
            CurrentShuffledQueueIndex = _currentShuffledIndex
        };
        await _settingsService.SavePlaybackStateAsync(state);
    }

    public async Task SetEqualizerBandAsync(uint bandIndex, float gain)
    {
        if (CurrentEqualizerSettings == null || bandIndex >= CurrentEqualizerSettings.BandGains.Count) return;

        CurrentEqualizerSettings.BandGains[(int)bandIndex] = gain;
        _audioPlayer.ApplyEqualizerSettings(CurrentEqualizerSettings);
        await _settingsService.SetEqualizerSettingsAsync(CurrentEqualizerSettings);
        EqualizerChanged?.Invoke();
    }

    public async Task SetEqualizerPreampAsync(float gain)
    {
        if (CurrentEqualizerSettings == null) return;

        CurrentEqualizerSettings.Preamp = gain;
        _audioPlayer.ApplyEqualizerSettings(CurrentEqualizerSettings);
        await _settingsService.SetEqualizerSettingsAsync(CurrentEqualizerSettings);
        EqualizerChanged?.Invoke();
    }

    public async Task ResetEqualizerAsync()
    {
        if (CurrentEqualizerSettings == null) return;

        CurrentEqualizerSettings.Preamp = 10.0f;
        for (var i = 0; i < CurrentEqualizerSettings.BandGains.Count; i++) CurrentEqualizerSettings.BandGains[i] = 0.0f;

        _audioPlayer.ApplyEqualizerSettings(CurrentEqualizerSettings);
        await _settingsService.SetEqualizerSettingsAsync(CurrentEqualizerSettings);
        EqualizerChanged?.Invoke();
    }

    public void Dispose()
    {
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
    }

    /// <summary>
    ///     Restores the playback queue, indices, and current track from a saved state.
    /// </summary>
    private async Task<bool> RestoreInternalPlaybackStateAsync(PlaybackState state)
    {
        _logger.LogInformation("Attempting to restore previous playback state.");
        var songIds = new HashSet<Guid>(state.PlaybackQueueTrackIds ?? Enumerable.Empty<Guid>());
        if (!songIds.Any()) return false;

        var songMap = await _libraryService.GetSongsByIdsAsync(songIds);
        if (!songMap.Any())
        {
            _logger.LogWarning("Could not restore playback state: No songs from the previous queue were found.");
            return false;
        }

        _playbackQueue = (state.PlaybackQueueTrackIds ?? Enumerable.Empty<Guid>())
            .Select(id => songMap.GetValueOrDefault(id))
            .Where(s => s != null)
            .Cast<Song>()
            .ToList();

        if (!_playbackQueue.Any()) return false;

        if (IsShuffleEnabled)
        {
            var shuffledIds = state.ShuffledQueueTrackIds ?? Enumerable.Empty<Guid>();
            _shuffledQueue = shuffledIds
                .Select(id => songMap.GetValueOrDefault(id))
                .Where(s => s != null)
                .Cast<Song>()
                .ToList();

            // Ensure shuffled queue is valid, otherwise regenerate it.
            if (_shuffledQueue.Count != _playbackQueue.Count) GenerateShuffledQueue();
        }

        if (state.CurrentTrackId.HasValue && songMap.TryGetValue(state.CurrentTrackId.Value, out var currentSong))
        {
            CurrentTrack = currentSong;
            CurrentQueueIndex = _playbackQueue.IndexOf(currentSong);
            if (IsShuffleEnabled) _currentShuffledIndex = _shuffledQueue.IndexOf(currentSong);

            CurrentListenHistoryId = null;

            // Wait for the media duration to be known before seeking to the saved position.
            var durationKnownTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnDurationChangedHandler()
            {
                _audioPlayer.DurationChanged -= OnDurationChangedHandler;
                durationKnownTcs.TrySetResult();
            }

            _audioPlayer.DurationChanged += OnDurationChangedHandler;

            try
            {
                await _audioPlayer.LoadAsync(CurrentTrack);
                var completedTask = await Task.WhenAny(durationKnownTcs.Task, Task.Delay(5000));

                if (completedTask == durationKnownTcs.Task && _audioPlayer.Duration > TimeSpan.Zero)
                {
                    // previously we sought to the saved position; position restore intentionally removed
                }
                else if (completedTask != durationKnownTcs.Task)
                    _logger.LogWarning("Timed out waiting for media duration during session restore.");
            }
            finally
            {
                _audioPlayer.DurationChanged -= OnDurationChangedHandler;
            }
        }
        else
        {
            // Restore indices even if the track itself isn't loaded.
            CurrentQueueIndex = state.CurrentPlaybackQueueIndex;
            _currentShuffledIndex = state.CurrentShuffledQueueIndex;
        }

        return true;
    }

    private async Task PlayFromOrderedIdsAsync(IList<Guid> orderedSongIds, bool startShuffled)
    {
        if (orderedSongIds == null || !orderedSongIds.Any())
        {
            await PlayAsync(new List<Song>());
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

    private void ClearQueuesInternal()
    {
        _playbackQueue.Clear();
        _shuffledQueue.Clear();
        CurrentTrack = null;
        CurrentQueueIndex = -1;
        _currentShuffledIndex = -1;
        CurrentListenHistoryId = null;
    }

    private void GenerateShuffledQueue()
    {
        if (!_playbackQueue.Any())
        {
            _shuffledQueue.Clear();
            return;
        }

        _shuffledQueue = new List<Song>(_playbackQueue);
        var n = _shuffledQueue.Count;
        while (n > 1)
        {
            n--;
            var k = _random.Next(n + 1);
            (_shuffledQueue[k], _shuffledQueue[n]) = (_shuffledQueue[n], _shuffledQueue[k]);
        }
    }

    /// <summary>
    ///     Calculates the index of the next track to play based on the current state.
    /// </summary>
    /// <param name="moveForward">True to get the next track, false for the previous.</param>
    /// <param name="nextPlaybackQueueIndex">The calculated index in the main playback queue.</param>
    /// <returns>True if a next track exists; otherwise, false.</returns>
    private bool TryGetNextTrackIndex(bool moveForward, out int nextPlaybackQueueIndex)
    {
        nextPlaybackQueueIndex = -1;
        if (!_playbackQueue.Any() || CurrentQueueIndex == -1) return false;

        var nextIndex = -1;

        if (IsShuffleEnabled)
        {
            var nextShuffledIndex = -1;
            if (moveForward)
            {
                if (_currentShuffledIndex < _shuffledQueue.Count - 1)
                    nextShuffledIndex = _currentShuffledIndex + 1;
                else if (CurrentRepeatMode == RepeatMode.RepeatAll) nextShuffledIndex = 0;
            }
            else // move backward
            {
                if (_currentShuffledIndex > 0)
                    nextShuffledIndex = _currentShuffledIndex - 1;
                else if (CurrentRepeatMode == RepeatMode.RepeatAll) nextShuffledIndex = _shuffledQueue.Count - 1;
            }

            if (nextShuffledIndex != -1) nextIndex = _playbackQueue.IndexOf(_shuffledQueue[nextShuffledIndex]);
        }
        else // Not shuffled
        {
            if (moveForward)
            {
                if (CurrentQueueIndex < _playbackQueue.Count - 1)
                    nextIndex = CurrentQueueIndex + 1;
                else if (CurrentRepeatMode == RepeatMode.RepeatAll) nextIndex = 0;
            }
            else // move backward
            {
                if (CurrentQueueIndex > 0)
                    nextIndex = CurrentQueueIndex - 1;
                else if (CurrentRepeatMode == RepeatMode.RepeatAll) nextIndex = _playbackQueue.Count - 1;
            }
        }

        if (nextIndex != -1)
        {
            nextPlaybackQueueIndex = nextIndex;
            return true;
        }

        return false;
    }

    private void UpdateSmtcControls()
    {
        var canGoNext = false;
        var canGoPrevious = false;

        if (_playbackQueue.Any())
        {
            if (CurrentRepeatMode != RepeatMode.Off)
            {
                canGoNext = true;
                canGoPrevious = true;
            }
            else if (IsShuffleEnabled)
            {
                canGoNext = _currentShuffledIndex < _shuffledQueue.Count - 1;
                canGoPrevious = _currentShuffledIndex > 0;
            }
            else
            {
                canGoNext = CurrentQueueIndex < _playbackQueue.Count - 1;
                canGoPrevious = CurrentQueueIndex > 0;
            }

            // If a track is active, you can always go "previous" to either restart the track or go to the prior one.
            if (CurrentTrack != null) canGoPrevious = true;
        }

        _audioPlayer.UpdateSmtcButtonStates(canGoNext, canGoPrevious);
    }

    private async void OnAudioPlayerPlaybackEnded()
    {
        // When the current track finishes naturally, advance to the next one.
        await NextAsync();
    }

    private void OnAudioPlayerStateChanged()
    {
        PlaybackStateChanged?.Invoke();
        if (IsTransitioningTrack && _audioPlayer.IsPlaying) IsTransitioningTrack = false;
    }

    private async void OnAudioPlayerVolumeChanged()
    {
        await _settingsService.SaveVolumeAsync(_audioPlayer.Volume);
        VolumeStateChanged?.Invoke();
    }

    private void OnAudioPlayerPositionChanged()
    {
        PositionChanged?.Invoke();
    }

    private void OnAudioPlayerDurationChanged()
    {
        DurationChanged?.Invoke();
    }

    private async void OnAudioPlayerErrorOccurred(string errorMessage)
    {
        _logger.LogError("Audio player error occurred: {ErrorMessage}", errorMessage);
        IsTransitioningTrack = false;
        await StopAsync();
    }

    private void OnAudioPlayerMediaOpened()
    {
        TrackChanged?.Invoke();
        UpdateSmtcControls();
    }

    private async void OnAudioPlayerSmtcNextButtonPressed()
    {
        await NextAsync();
    }

    private async void OnAudioPlayerSmtcPreviousButtonPressed()
    {
        await PreviousAsync();
    }
}