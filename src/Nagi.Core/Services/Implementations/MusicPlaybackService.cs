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

    private bool _isDisposed;
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

        _settingsService.VolumeNormalizationEnabledChanged += OnVolumeNormalizationEnabledChanged;

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
    public IReadOnlyList<Song> PlaybackQueue => _playbackQueue.AsReadOnly();
    public IReadOnlyList<Song> ShuffledQueue => _shuffledQueue.AsReadOnly();
    public int CurrentQueueIndex { get; private set; } = -1;
    public int CurrentShuffledIndex { get; private set; } = -1;
    public bool IsShuffleEnabled { get; private set; }
    public RepeatMode CurrentRepeatMode { get; private set; } = RepeatMode.Off;
    public bool IsTransitioningTrack { get; private set; }
    public IReadOnlyList<(uint Index, float Frequency)> EqualizerBands { get; }
    public IReadOnlyList<EqualizerPreset> AvailablePresets { get; } = new List<EqualizerPreset>
    {
        new("None", [0, 0, 0, 0, 0, 0, 0, 0, 0, 0]),
        new("Classical", [0, 0, 0, 0, 0, 0, -3.2f, -3.2f, -3.2f, -4.0f]),
        new("Dance", [3.2f, 2.4f, 0.8f, 0, 0, -1.6f, -2.4f, -2.4f, 0, 0]),
        new("Full Bass", [3.2f, 3.2f, 3.2f, 1.6f, 0, -1.6f, -3.2f, -3.2f, -3.2f, -4.0f]),
        new("Full Treble", [-3.2f, -3.2f, -3.2f, -1.6f, 0, 1.6f, 3.2f, 3.2f, 3.2f, 4.0f]),
        new("Jazz", [1.6f, 0.8f, 0, 0.8f, -0.8f, -0.8f, -1.6f, 0, 0.8f, 1.6f]),
        new("Pop", [-1.6f, 1.6f, 2.4f, 2.4f, 1.6f, 0, -0.8f, -0.8f, -1.6f, -1.6f]),
        new("Rock", [3.2f, 2.4f, -1.6f, -2.4f, -0.8f, 1.6f, 2.4f, 3.2f, 3.2f, 3.2f]),
        new("Soft", [1.6f, 0.8f, 0, -0.8f, -0.8f, 0, 0.8f, 1.6f, 2.4f, 3.2f]),
        new("Techno", [3.2f, 2.4f, 0, -1.6f, -1.6f, 0, 1.6f, 2.4f, 2.4f, 3.2f]),
        new("Vocal", [-0.8f, -2.4f, -2.4f, 0, 1.6f, 1.6f, 1.6f, 0.8f, 0, -0.8f])
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

    public async Task InitializeAsync(bool restoreLastSession = true)
    {
        if (_isInitialized)
        {
            _logger.LogDebug("MusicPlaybackService is already initialized.");
            return;
        }

        _logger.LogDebug("Initializing MusicPlaybackService...");

        try
        {
            // Phase 1: Parallelize all independent settings reads for faster startup
            var volumeTask = _settingsService.GetInitialVolumeAsync();
            var muteTask = _settingsService.GetInitialMuteStateAsync();
            var shuffleTask = _settingsService.GetInitialShuffleStateAsync();
            var repeatTask = _settingsService.GetInitialRepeatModeAsync();
            var eqTask = _settingsService.GetEqualizerSettingsAsync();
            var restoreEnabledTask = _settingsService.GetRestorePlaybackStateEnabledAsync();
            // Phase 2: Optimistically start reading playback state in parallel
            var playbackStateTask = _settingsService.GetPlaybackStateAsync();

            await Task.WhenAll(volumeTask, muteTask, shuffleTask, repeatTask, eqTask, restoreEnabledTask, playbackStateTask).ConfigureAwait(false);

            await _audioPlayer.SetVolumeAsync(volumeTask.Result).ConfigureAwait(false);
            await _audioPlayer.SetMuteAsync(muteTask.Result).ConfigureAwait(false);
            IsShuffleEnabled = shuffleTask.Result;
            CurrentRepeatMode = repeatTask.Result;

            CurrentEqualizerSettings = eqTask.Result;
            if (CurrentEqualizerSettings != null && CurrentEqualizerSettings.BandGains.Count != EqualizerBands.Count)
            {
                _logger.LogWarning("Equalizer settings mismatched (Saved: {SavedCount}, Required: {RequiredCount}). Resetting to default.", 
                    CurrentEqualizerSettings.BandGains.Count, EqualizerBands.Count);
                
                CurrentEqualizerSettings = null; // Force recreation below
            }

            if (CurrentEqualizerSettings == null)
            {
                CurrentEqualizerSettings = new EqualizerSettings
                {
                    Preamp = 10.0f,
                    BandGains = Enumerable.Repeat(0.0f, EqualizerBands.Count).ToList()
                };
            }
            _audioPlayer.ApplyEqualizerSettings(CurrentEqualizerSettings);

            var restoredSuccessfully = false;
            if (restoreLastSession && restoreEnabledTask.Result)
            {
                var savedState = playbackStateTask.Result;
                if (savedState != null) restoredSuccessfully = await RestoreInternalPlaybackStateAsync(savedState).ConfigureAwait(false);
            }

            if (!restoredSuccessfully) ClearQueuesInternal();

            _logger.LogDebug("MusicPlaybackService initialized successfully. Session restored: {IsRestored}",
                restoredSuccessfully);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MusicPlaybackService initialization failed. Using default settings.");
            await _audioPlayer.SetVolumeAsync(0.5).ConfigureAwait(false);
            await _audioPlayer.SetMuteAsync(false).ConfigureAwait(false);
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
        _logger.LogDebug("Playing transient file: {FilePath}", filePath);
        var metadata = await _metadataService.ExtractMetadataAsync(filePath).ConfigureAwait(false);

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

        await _audioPlayer.LoadAsync(CurrentTrack).ConfigureAwait(false);
        await _audioPlayer.PlayAsync().ConfigureAwait(false);
        UpdateSmtcControls();

        TrackChanged?.Invoke();
    }

    public async Task PlayAsync(Song song)
    {
        if (song == null) return;

        _logger.LogDebug("Playing single song: '{SongTitle}' ({SongId})", song.Title, song.Id);
        _playbackQueue = new List<Song> { song };
        if (IsShuffleEnabled)
            _shuffledQueue = new List<Song> { song };
        else
            _shuffledQueue.Clear();

        QueueChanged?.Invoke();
        await PlayQueueItemAsync(0).ConfigureAwait(false);
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

        _logger.LogDebug(
            "Playing a new queue of {SongCount} songs. Start index: {StartIndex}, Shuffled: {IsShuffled}",
            songList.Count, startIndex, startShuffled);

        if (IsShuffleEnabled != startShuffled) await SetShuffleAsync(startShuffled).ConfigureAwait(false);

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
                if (actualPlaybackIndex >= 0)
                    await PlayQueueItemAsync(actualPlaybackIndex).ConfigureAwait(false);
                else
                    await StopAsync().ConfigureAwait(false);
            }
            else
            {
                await StopAsync().ConfigureAwait(false);
            }
        }
        else
        {
            if (startIndex < 0 || startIndex >= _playbackQueue.Count) startIndex = 0;
            await PlayQueueItemAsync(startIndex).ConfigureAwait(false);
        }
    }

    public async Task PlayPauseAsync()
    {
        if (_audioPlayer.IsPlaying)
        {
            await _audioPlayer.PauseAsync().ConfigureAwait(false);
            return;
        }

        if (CurrentTrack != null)
        {
            await _audioPlayer.PlayAsync().ConfigureAwait(false);
            return;
        }

        // If no track is loaded but a queue exists, play from the last known position.
        if (_playbackQueue.Any())
        {
            var indexToPlay = CurrentQueueIndex >= 0 ? CurrentQueueIndex : 0;

            if (IsShuffleEnabled && _shuffledQueue.Any())
            {
                var shuffledIndex = CurrentShuffledIndex >= 0 ? CurrentShuffledIndex : 0;
                var songToPlay = _shuffledQueue.ElementAtOrDefault(shuffledIndex);
                if (songToPlay != null)
                {
                    indexToPlay = _playbackQueue.IndexOf(songToPlay);
                    if (indexToPlay == -1) indexToPlay = 0;
                }
            }

            if (indexToPlay >= 0 && indexToPlay < _playbackQueue.Count) await PlayQueueItemAsync(indexToPlay).ConfigureAwait(false);
        }
    }

    public async Task StopAsync()
    {
        await _audioPlayer.StopAsync().ConfigureAwait(false);

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
            await PlayQueueItemAsync(CurrentQueueIndex).ConfigureAwait(false);
            return;
        }

        // This logic handles two distinct cases for robust navigation.
        if (CurrentTrack != null)
        {
            // Case 1: A track is currently active. Find the next one in the sequence.
            if (TryGetNextTrackIndex(true, out var nextIndex))
                await PlayQueueItemAsync(nextIndex).ConfigureAwait(false);
            else
                // Reached the end of the queue.
                await StopAsync().ConfigureAwait(false);
        }
        else if (_playbackQueue.Any())
        {
            // Case 2: The player is stopped at a queue boundary.
            // Pressing Next should resume playback from the current position.
            await PlayQueueItemAsync(CurrentQueueIndex).ConfigureAwait(false);
        }
    }

    public async Task PreviousAsync()
    {
        // If the track has played for more than 3 seconds, restart it.
        if (CurrentTrack != null && _audioPlayer.CurrentPosition.TotalSeconds > 3 &&
            CurrentRepeatMode != RepeatMode.RepeatOne)
        {
            await SeekAsync(TimeSpan.Zero).ConfigureAwait(false);
            if (!_audioPlayer.IsPlaying) await _audioPlayer.PlayAsync().ConfigureAwait(false);
            return;
        }

        if (CurrentRepeatMode == RepeatMode.RepeatOne && CurrentTrack != null)
        {
            await PlayQueueItemAsync(CurrentQueueIndex).ConfigureAwait(false);
            return;
        }

        // This logic mirrors NextAsync for robust navigation.
        if (CurrentTrack != null)
        {
            // Case 1: A track is currently active. Find the previous one in the sequence.
            if (TryGetNextTrackIndex(false, out var prevIndex))
                await PlayQueueItemAsync(prevIndex).ConfigureAwait(false);
            else
                // Reached the beginning of the queue.
                await StopAsync().ConfigureAwait(false);
        }
        else if (_playbackQueue.Any())
        {
            // Case 2: The player is stopped at a queue boundary.
            // Pressing Previous should resume playback from the current position.
            await PlayQueueItemAsync(CurrentQueueIndex).ConfigureAwait(false);
        }
    }

    public async Task SeekAsync(TimeSpan position)
    {
        if (CurrentTrack != null) await _audioPlayer.SeekAsync(position).ConfigureAwait(false);
    }

    public async Task PlayAlbumAsync(Guid albumId)
    {
        var songIds = await _libraryService.GetAllSongIdsByAlbumIdAsync(albumId, SongSortOrder.TrackNumberAsc).ConfigureAwait(false);
        await PlayFromOrderedIdsAsync(songIds, false).ConfigureAwait(false);
    }

    public async Task PlayArtistAsync(Guid artistId)
    {
        var songIds = await _libraryService.GetAllSongIdsByArtistIdAsync(artistId, SongSortOrder.TitleAsc).ConfigureAwait(false);
        await PlayFromOrderedIdsAsync(songIds, false).ConfigureAwait(false);
    }

    public async Task PlayFolderAsync(Guid folderId)
    {
        var songIds = await _libraryService.GetAllSongIdsByFolderIdAsync(folderId, SongSortOrder.TitleAsc).ConfigureAwait(false);
        await PlayFromOrderedIdsAsync(songIds, false).ConfigureAwait(false);
    }

    public async Task PlayPlaylistAsync(Guid playlistId)
    {
        var orderedSongs = (await _libraryService.GetSongsInPlaylistOrderedAsync(playlistId).ConfigureAwait(false))?.ToList();
        await PlayAsync(orderedSongs ?? new List<Song>()).ConfigureAwait(false);
    }

    public async Task PlayGenreAsync(Guid genreId)
    {
        var songIds = await _libraryService.GetAllSongIdsByGenreIdAsync(genreId, SongSortOrder.TitleAsc).ConfigureAwait(false);
        await PlayFromOrderedIdsAsync(songIds, false).ConfigureAwait(false);
    }

    public async Task SetVolumeAsync(double volume)
    {
        await _audioPlayer.SetVolumeAsync(volume).ConfigureAwait(false);
        await _settingsService.SaveVolumeAsync(volume).ConfigureAwait(false);
    }

    public async Task ToggleMuteAsync()
    {
        var newMuteState = !_audioPlayer.IsMuted;
        await _audioPlayer.SetMuteAsync(newMuteState).ConfigureAwait(false);
        await _settingsService.SaveMuteStateAsync(newMuteState).ConfigureAwait(false);
    }

    public async Task SetShuffleAsync(bool enable)
    {
        if (IsShuffleEnabled == enable) return;

        IsShuffleEnabled = enable;
        _logger.LogDebug("Shuffle mode set to {ShuffleState}", IsShuffleEnabled);
        if (IsShuffleEnabled)
        {
            GenerateShuffledQueue();
            CurrentShuffledIndex = CurrentTrack != null ? _shuffledQueue.IndexOf(CurrentTrack) : -1;
            if (CurrentTrack != null && CurrentShuffledIndex == -1)
            {
                _logger.LogWarning("Current track not found in new shuffle. This should not happen.");
                CurrentShuffledIndex = 0;
            }
        }
        else
        {
            _shuffledQueue.Clear();
            CurrentShuffledIndex = -1;
        }

        await _settingsService.SaveShuffleStateAsync(IsShuffleEnabled).ConfigureAwait(false);
        ShuffleModeChanged?.Invoke();
        QueueChanged?.Invoke();
        UpdateSmtcControls();
    }

    public async Task SetRepeatModeAsync(RepeatMode mode)
    {
        if (CurrentRepeatMode == mode) return;

        CurrentRepeatMode = mode;
        _logger.LogDebug("Repeat mode set to {RepeatMode}", CurrentRepeatMode);
        await _settingsService.SaveRepeatModeAsync(CurrentRepeatMode).ConfigureAwait(false);
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
            if (CurrentTrack != null)
            {
                CurrentShuffledIndex = _shuffledQueue.IndexOf(CurrentTrack);
                if (CurrentShuffledIndex == -1)
                {
                    _logger.LogWarning("Current track lost in shuffle after queue addition. Resetting shuffle.");
                    CurrentShuffledIndex = 0;
                }
            }
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
                if (CurrentTrack != null)
                {
                    CurrentShuffledIndex = _shuffledQueue.IndexOf(CurrentTrack);
                    if (CurrentShuffledIndex == -1)
                    {
                        _logger.LogWarning("Current track lost in shuffle after range addition. Resetting shuffle.");
                        CurrentShuffledIndex = 0;
                    }
                }
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
        insertIndex = Math.Min(insertIndex, _playbackQueue.Count);
        _playbackQueue.Insert(insertIndex, song);

        if (IsShuffleEnabled)
        {
            var shuffledInsertIndex = CurrentShuffledIndex == -1 ? 0 : CurrentShuffledIndex + 1;
            shuffledInsertIndex = Math.Min(shuffledInsertIndex, _shuffledQueue.Count);
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
            _logger.LogDebug("Removing currently playing song '{SongTitle}' from queue.", song.Title);
            await _audioPlayer.StopAsync().ConfigureAwait(false);
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
                    await PlayQueueItemAsync(nextIndexToPlay).ConfigureAwait(false);
                }
                else
                {
                    await StopAsync().ConfigureAwait(false);
                    ClearQueuesInternal();
                    QueueChanged?.Invoke();
                }
            }
            else
            {
                // The queue is now empty.
                await StopAsync().ConfigureAwait(false);
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
                if (originalIndex < CurrentQueueIndex) CurrentQueueIndex--;
                if (IsShuffleEnabled)
                {
                    CurrentShuffledIndex = _shuffledQueue.IndexOf(CurrentTrack);
                    if (CurrentShuffledIndex == -1)
                    {
                        _logger.LogWarning("Current track lost in shuffle after removal. Regenerating.");
                        GenerateShuffledQueue();
                        CurrentShuffledIndex = _shuffledQueue.IndexOf(CurrentTrack);
                    }
                }
            }

            QueueChanged?.Invoke();
            UpdateSmtcControls();
        }
    }

    public async Task PlayQueueItemAsync(int originalQueueIndex)
    {
        if (originalQueueIndex < 0 || originalQueueIndex >= _playbackQueue.Count)
        {
            await StopAsync().ConfigureAwait(false);
            return;
        }

        IsTransitioningTrack = true;
        CurrentTrack = _playbackQueue[originalQueueIndex];
        CurrentQueueIndex = originalQueueIndex;

        CurrentListenHistoryId = await _libraryService.CreateListenHistoryEntryAsync(CurrentTrack.Id).ConfigureAwait(false);

        if (IsShuffleEnabled)
        {
            CurrentShuffledIndex = _shuffledQueue.IndexOf(CurrentTrack);
            if (CurrentShuffledIndex == -1)
            {
                _logger.LogWarning("Track '{SongTitle}' not found in shuffled queue. Regenerating shuffle.",
                    CurrentTrack.Title);
                GenerateShuffledQueue();
                CurrentShuffledIndex = _shuffledQueue.IndexOf(CurrentTrack);
            }
        }

        _logger.LogDebug("Now playing '{SongTitle}' (Index: {QueueIndex}, Shuffled Index: {ShuffledIndex})",
            CurrentTrack.Title, CurrentQueueIndex, CurrentShuffledIndex);
        await _audioPlayer.LoadAsync(CurrentTrack).ConfigureAwait(false);
        
        // Apply ReplayGain if enabled and available in database
        await ApplyReplayGainIfEnabledAsync().ConfigureAwait(false);
        
        await _audioPlayer.PlayAsync().ConfigureAwait(false);
        // Note: UpdateSmtcControls() is called by OnAudioPlayerMediaOpened after LoadAsync completes
    }

    public async Task ClearQueueAsync()
    {
        if (!_playbackQueue.Any()) return;
        _logger.LogDebug("Clearing playback queue.");
        await StopAsync().ConfigureAwait(false);
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
            CurrentShuffledQueueIndex = CurrentShuffledIndex
        };
        await _settingsService.SavePlaybackStateAsync(state).ConfigureAwait(false);
    }

    public async Task SetEqualizerBandAsync(uint bandIndex, float gain)
    {
        var currentSettings = CurrentEqualizerSettings;
        if (currentSettings == null || bandIndex >= currentSettings.BandGains.Count)
        {
            _logger.LogWarning("Invalid equalizer band index: {BandIndex}", bandIndex);
            return;
        }

        // Create new settings object (deep copy) for atomic update
        var newSettings = new EqualizerSettings
        {
            Preamp = currentSettings.Preamp,
            BandGains = new List<float>(currentSettings.BandGains)
        };
        
        newSettings.BandGains[(int)bandIndex] = gain;

        // Apply atomically
        CurrentEqualizerSettings = newSettings;
        _audioPlayer.ApplyEqualizerSettings(CurrentEqualizerSettings);
        await _settingsService.SetEqualizerSettingsAsync(CurrentEqualizerSettings);
        EqualizerChanged?.Invoke();
    }

    public async Task SetEqualizerGainsAsync(IEnumerable<float> gains)
    {
        if (CurrentEqualizerSettings == null) return;
        
        var gainsList = gains.ToList();
        _logger.LogDebug("Setting bulk equalizer gains: {Gains}", string.Join(", ", gainsList));
        
        // Create a new settings object (or deep copy) to ensure atomic update
        var newSettings = new EqualizerSettings
        {
            Preamp = CurrentEqualizerSettings.Preamp,
            BandGains = new List<float>(CurrentEqualizerSettings.BandGains)
        };

        for (int i = 0; i < newSettings.BandGains.Count; i++)
        {
            if (i < gainsList.Count)
            {
                newSettings.BandGains[i] = gainsList[i];
            }
        }

        // Apply atomically
        CurrentEqualizerSettings = newSettings;
        _audioPlayer.ApplyEqualizerSettings(CurrentEqualizerSettings);
        await _settingsService.SetEqualizerSettingsAsync(CurrentEqualizerSettings);
        EqualizerChanged?.Invoke();
    }

    public EqualizerPreset? GetMatchingPreset(IEnumerable<float>? bandGains)
    {
        if (bandGains == null) return null;
        var currentGains = bandGains.ToArray();

        return AvailablePresets.FirstOrDefault(preset => 
            preset.Gains.Length == currentGains.Length && 
            preset.Gains.Zip(currentGains).All(pair => Math.Abs(pair.First - pair.Second) <= 0.1f));
    }

    public async Task SetEqualizerPreampAsync(float gain)
    {
        var currentSettings = CurrentEqualizerSettings;
        if (currentSettings == null) return;

        // Create new settings object (deep copy) for atomic update
        var newSettings = new EqualizerSettings
        {
            Preamp = gain,
            BandGains = new List<float>(currentSettings.BandGains)
        };

        // Apply atomically
        CurrentEqualizerSettings = newSettings;
        _audioPlayer.ApplyEqualizerSettings(CurrentEqualizerSettings);
        await _settingsService.SetEqualizerSettingsAsync(CurrentEqualizerSettings);
        EqualizerChanged?.Invoke();
    }

    public async Task ResetEqualizerAsync()
    {
        if (CurrentEqualizerSettings == null) return;

        // Create new settings object (deep copy) for atomic update - matches SetEqualizerBandAsync pattern
        var newSettings = new EqualizerSettings
        {
            Preamp = 10.0f,
            BandGains = Enumerable.Repeat(0.0f, CurrentEqualizerSettings.BandGains.Count).ToList()
        };

        CurrentEqualizerSettings = newSettings;
        _audioPlayer.ApplyEqualizerSettings(CurrentEqualizerSettings);
        await _settingsService.SetEqualizerSettingsAsync(CurrentEqualizerSettings);
        EqualizerChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        _audioPlayer.PlaybackEnded -= OnAudioPlayerPlaybackEnded;
        _audioPlayer.StateChanged -= OnAudioPlayerStateChanged;
        _audioPlayer.VolumeChanged -= OnAudioPlayerVolumeChanged;
        _audioPlayer.PositionChanged -= OnAudioPlayerPositionChanged;
        _audioPlayer.ErrorOccurred -= OnAudioPlayerErrorOccurred;
        _audioPlayer.MediaOpened -= OnAudioPlayerMediaOpened;
        _audioPlayer.DurationChanged -= OnAudioPlayerDurationChanged;
        _audioPlayer.SmtcNextButtonPressed -= OnAudioPlayerSmtcNextButtonPressed;
        _audioPlayer.SmtcPreviousButtonPressed -= OnAudioPlayerSmtcPreviousButtonPressed;
        _settingsService.VolumeNormalizationEnabledChanged -= OnVolumeNormalizationEnabledChanged;
        
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Restores the playback queue, indices, and current track from a saved state.
    /// </summary>
    private async Task<bool> RestoreInternalPlaybackStateAsync(PlaybackState state)
    {
        _logger.LogDebug("Attempting to restore previous playback state.");
        var songIds = new HashSet<Guid>(state.PlaybackQueueTrackIds ?? Enumerable.Empty<Guid>());
        if (!songIds.Any()) return false;

        var songMap = await _libraryService.GetSongsByIdsAsync(songIds).ConfigureAwait(false);
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
            if (CurrentQueueIndex == -1)
            {
                _logger.LogWarning("Current track '{SongTitle}' not found in restored queue.", currentSong.Title);
                CurrentQueueIndex = 0;
            }

            if (IsShuffleEnabled)
            {
                CurrentShuffledIndex = _shuffledQueue.IndexOf(currentSong);
                if (CurrentShuffledIndex == -1)
                {
                    _logger.LogWarning("Current track not found in shuffled queue. Regenerating shuffle.");
                    GenerateShuffledQueue();
                    CurrentShuffledIndex = _shuffledQueue.IndexOf(currentSong);
                }
            }

            CurrentListenHistoryId = null;
            await _audioPlayer.LoadAsync(CurrentTrack).ConfigureAwait(false);
        }
        else
        {
            // Restore indices even if the track itself isn't loaded.
            CurrentQueueIndex = state.CurrentPlaybackQueueIndex;
            CurrentShuffledIndex = state.CurrentShuffledQueueIndex;
        }

        return true;
    }

    private async Task PlayFromOrderedIdsAsync(IList<Guid> orderedSongIds, bool startShuffled)
    {
        if (orderedSongIds == null || !orderedSongIds.Any())
        {
            await PlayAsync(new List<Song>()).ConfigureAwait(false);
            return;
        }

        var songMap = await _libraryService.GetSongsByIdsAsync(orderedSongIds).ConfigureAwait(false);

        var orderedSongs = orderedSongIds
            .Select(id => songMap.GetValueOrDefault(id))
            .Where(s => s != null)
            .Cast<Song>()
            .ToList();

        await PlayAsync(orderedSongs, 0, startShuffled).ConfigureAwait(false);
    }

    private void ClearQueuesInternal()
    {
        _playbackQueue.Clear();
        _shuffledQueue.Clear();
        CurrentTrack = null;
        CurrentQueueIndex = -1;
        CurrentShuffledIndex = -1;
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

        if (IsShuffleEnabled && (_shuffledQueue.Count != _playbackQueue.Count || CurrentShuffledIndex == -1))
        {
            _logger.LogWarning("Shuffled queue desynchronized. Regenerating.");
            GenerateShuffledQueue();
            if (CurrentTrack != null) CurrentShuffledIndex = _shuffledQueue.IndexOf(CurrentTrack);
        }

        var nextIndex = -1;

        if (IsShuffleEnabled)
        {
            var nextShuffledIndex = -1;
            if (moveForward)
            {
                if (CurrentShuffledIndex < _shuffledQueue.Count - 1)
                    nextShuffledIndex = CurrentShuffledIndex + 1;
                else if (CurrentRepeatMode == RepeatMode.RepeatAll) nextShuffledIndex = 0;
            }
            else // move backward
            {
                if (CurrentShuffledIndex > 0)
                    nextShuffledIndex = CurrentShuffledIndex - 1;
                else if (CurrentRepeatMode == RepeatMode.RepeatAll) nextShuffledIndex = _shuffledQueue.Count - 1;
            }

            if (nextShuffledIndex != -1 && nextShuffledIndex < _shuffledQueue.Count)
                nextIndex = _playbackQueue.IndexOf(_shuffledQueue[nextShuffledIndex]);
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
                canGoNext = CurrentShuffledIndex < _shuffledQueue.Count - 1;
                canGoPrevious = CurrentShuffledIndex > 0;
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
        try
        {
            // When the current track finishes naturally, advance to the next one.
            await NextAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handled in playback ended transition");
        }
    }

    private void OnAudioPlayerStateChanged()
    {
        PlaybackStateChanged?.Invoke();
        if (IsTransitioningTrack && _audioPlayer.IsPlaying) IsTransitioningTrack = false;
    }

    private async void OnAudioPlayerVolumeChanged()
    {
        try
        {
            await _settingsService.SaveVolumeAsync(_audioPlayer.Volume).ConfigureAwait(false);
            VolumeStateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving volume on change");
        }
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
        try
        {
            IsTransitioningTrack = false;
            await StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling audio player error event");
        }
    }

    private void OnAudioPlayerMediaOpened()
    {
        TrackChanged?.Invoke();
        UpdateSmtcControls();
    }

    private async void OnAudioPlayerSmtcNextButtonPressed()
    {
        try
        {
            await NextAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling SMTC next button press");
        }
    }

    private async void OnAudioPlayerSmtcPreviousButtonPressed()
    {
        try
        {
            await PreviousAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling SMTC previous button press");
        }
    }

    /// <summary>
    ///     Applies ReplayGain adjustment if volume normalization is enabled and the current track has gain data.
    ///     Includes peak-aware limiting to prevent clipping.
    /// </summary>
    private async Task ApplyReplayGainIfEnabledAsync(bool? isEnabledOverride = null)
    {
        if (CurrentTrack == null) return;

        var isEnabled = isEnabledOverride ?? await _settingsService.GetVolumeNormalizationEnabledAsync().ConfigureAwait(false);
        
        // If enabled but missing data in memory, attempt a database refresh.
        // Use the transient flag to avoid repeated lookups for songs without ReplayGain data.
        if (isEnabled && !CurrentTrack.ReplayGainTrackGain.HasValue && !CurrentTrack.ReplayGainCheckPerformed)
        {
            CurrentTrack.ReplayGainCheckPerformed = true; // Mark as checked before DB call
            var refreshedSong = await _libraryService.GetSongByIdAsync(CurrentTrack.Id).ConfigureAwait(false);
            if (refreshedSong != null && refreshedSong.ReplayGainTrackGain.HasValue)
            {
                CurrentTrack.ReplayGainTrackGain = refreshedSong.ReplayGainTrackGain;
                CurrentTrack.ReplayGainTrackPeak = refreshedSong.ReplayGainTrackPeak;
                _logger.LogDebug("Refreshed ReplayGain data for current track from database.");
            }
        }

        if (isEnabled && CurrentTrack.ReplayGainTrackGain.HasValue)
        {
            var gainDb = CurrentTrack.ReplayGainTrackGain.Value;
            
            // Apply peak-aware limiting to prevent clipping
            // If peak * gain > 1.0, reduce gain to prevent clipping
            if (CurrentTrack.ReplayGainTrackPeak.HasValue && CurrentTrack.ReplayGainTrackPeak.Value > 0)
            {
                var maxGainBeforeClip = -20.0 * Math.Log10(CurrentTrack.ReplayGainTrackPeak.Value);
                if (gainDb > maxGainBeforeClip)
                {
                    _logger.LogDebug("Limiting ReplayGain from {OriginalGain:F2} dB to {LimitedGain:F2} dB to prevent clipping",
                        gainDb, maxGainBeforeClip);
                    gainDb = maxGainBeforeClip;
                }
            }
            
            await _audioPlayer.SetReplayGainAsync(gainDb).ConfigureAwait(false);
            _logger.LogDebug("Applied ReplayGain {Gain:F2} dB for '{Title}'",
                gainDb, CurrentTrack.Title);
        }
        else
        {
            // Reset to neutral (no adjustment)
            await _audioPlayer.SetReplayGainAsync(0).ConfigureAwait(false);
        }
    }

    private async void OnVolumeNormalizationEnabledChanged(bool isEnabled)
    {
        try
        {
            await ApplyReplayGainIfEnabledAsync(isEnabled).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply ReplayGain on settings change.");
        }
    }
}
