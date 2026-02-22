using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Helpers;

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
    private List<Guid> _playbackQueue = new();
    private List<Guid> _shuffledQueue = new();
    
    // Reverse index dictionaries for O(1) lookups in large queues (500k+ songs)
    private Dictionary<Guid, int> _playbackQueueIndex = new();
    private Dictionary<Guid, int> _shuffledQueueIndex = new();

    private int _updateCount;

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
        _settingsService.FadeOnPlayPauseEnabledChanged += OnFadeOnPlayPauseEnabledChanged;

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
    public IReadOnlyList<Guid> PlaybackQueue => _playbackQueue.AsReadOnly();
    public IReadOnlyList<Guid> ShuffledQueue => _shuffledQueue.AsReadOnly();
    public int CurrentQueueIndex { get; private set; } = -1;
    public int CurrentShuffledIndex { get; private set; } = -1;
    public bool IsShuffleEnabled { get; private set; }
    public RepeatMode CurrentRepeatMode { get; private set; } = RepeatMode.Off;
    public bool IsTransitioningTrack { get; private set; }
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
            var fadeTask = _settingsService.GetFadeOnPlayPauseEnabledAsync();
            // Phase 2: Optimistically start reading playback state in parallel
            var playbackStateTask = _settingsService.GetPlaybackStateAsync();

            await Task.WhenAll(volumeTask, muteTask, shuffleTask, repeatTask, eqTask, restoreEnabledTask, fadeTask, playbackStateTask).ConfigureAwait(false);

            await _audioPlayer.SetVolumeAsync(volumeTask.Result).ConfigureAwait(false);
            await _audioPlayer.SetMuteAsync(muteTask.Result).ConfigureAwait(false);
            _audioPlayer.SetFadeOnPlayPauseEnabled(fadeTask.Result);
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
                
                // Save the new default settings immediately.
                // If we don't, the file remains empty/invalid, causing issues on next startup.
                await _settingsService.SetEqualizerSettingsAsync(CurrentEqualizerSettings).ConfigureAwait(false);
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

    /// <inheritdoc />
    /// <remarks>
    /// This method is designed to be called from the UI thread only. All playback service
    /// operations are expected to originate from the main thread, so no thread synchronization
    /// is required for the update counter.
    /// </remarks>
    public IDisposable BeginQueueUpdate()
    {
        _updateCount++;
        return new QueueUpdateScope(this);
    }

    private void CommitQueueChanges()
    {
        if (_updateCount > 0) _updateCount--;

        if (_updateCount == 0)
        {
            _logger.LogTrace("Committing queue changes and rebuilding indices.");
            RebuildPlaybackQueueIndex();
            
            if (IsShuffleEnabled)
            {
                // Ensure shuffled queue is still in sync with playback queue length/content
                if (_shuffledQueue.Count != _playbackQueue.Count)
                {
                    GenerateShuffledQueue();
                }
                else
                {
                    RebuildShuffledQueueIndex();
                }
            }

            // Sync current track indices after mutations
            if (CurrentTrack != null)
            {
                CurrentQueueIndex = GetPlaybackQueueIndex(CurrentTrack.Id);
                if (IsShuffleEnabled)
                {
                    CurrentShuffledIndex = GetShuffledQueueIndex(CurrentTrack.Id);
                    if (CurrentShuffledIndex == -1)
                    {
                        _logger.LogWarning("Current track lost in shuffle after batch update. Regenerating shuffle.");
                        GenerateShuffledQueue();
                        CurrentShuffledIndex = GetShuffledQueueIndex(CurrentTrack.Id);
                    }
                }
                else
                {
                    CurrentShuffledIndex = -1;
                }
            }
            else
            {
                // If stopped/no track, ensure indices are clamped or reset if queue is empty
                if (_playbackQueue.Count == 0)
                {
                    CurrentQueueIndex = -1;
                    CurrentShuffledIndex = -1;
                }
                else
                {
                    CurrentQueueIndex = Math.Clamp(CurrentQueueIndex, -1, _playbackQueue.Count - 1);
                    CurrentShuffledIndex = IsShuffleEnabled 
                        ? Math.Clamp(CurrentShuffledIndex, -1, _shuffledQueue.Count - 1)
                        : -1;
                }
            }

            QueueChanged?.Invoke();
            UpdateSmtcControls();
        }
    }

    private class QueueUpdateScope : IDisposable
    {
        private readonly MusicPlaybackService _service;
        private bool _isDisposed;

        public QueueUpdateScope(MusicPlaybackService service) => _service = service;

        public void Dispose()
        {
            if (_isDisposed) return;
            _service.CommitQueueChanges();
            _isDisposed = true;
        }
    }

    public async Task PlayTransientFileAsync(string filePath)
    {
        _logger.LogDebug("Playing transient file: {FilePath}", filePath);
        var metadata = await _metadataService.ExtractMetadataAsync(filePath).ConfigureAwait(false);

        // Filter out null/empty artist names and provide fallback if all are invalid
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
        // If no valid album artists, fall back to track artists
        if (validAlbumArtists.Count == 0)
        {
            validAlbumArtists = validArtists;
        }

        var transientSong = new Song
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

    public Task PlayAsync(Song song)
    {
        return PlayAsync(song?.Id ?? Guid.Empty);
    }

    public async Task PlayAsync(Guid songId)
    {
        if (songId == Guid.Empty) return;

        _logger.LogDebug("Playing single song ID: {SongId}", songId);
        
        using (BeginQueueUpdate())
        {
            _playbackQueue = new List<Guid> { songId };
            
            if (IsShuffleEnabled)
            {
                _shuffledQueue = new List<Guid> { songId };
            }
            else
            {
                _shuffledQueue.Clear();
            }
        }

        await PlayQueueItemAsync(0).ConfigureAwait(false);
    }

    public Task PlayAsync(IEnumerable<Song> songs, int startIndex = 0, bool? startShuffled = null)
    {
        return PlayAsync(songs?.Select(s => s.Id) ?? Enumerable.Empty<Guid>(), startIndex, startShuffled);
    }

    public async Task PlayAsync(IEnumerable<Guid> songIds, int startIndex = 0, bool? startShuffled = null)
    {
        var idList = songIds?.ToList() ?? new List<Guid>();
        if (!idList.Any())
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
            idList.Count, startIndex, startShuffled);

        if (startShuffled.HasValue && IsShuffleEnabled != startShuffled.Value) await SetShuffleAsync(startShuffled.Value).ConfigureAwait(false);

        using (BeginQueueUpdate())
        {
            _playbackQueue = idList;
            if (IsShuffleEnabled) GenerateShuffledQueue();
        }

        if (IsShuffleEnabled)
        {
            // If playing from a specific item (not just "Shuffle All"), ensure it plays first
            if (startShuffled != true && startIndex >= 0 && startIndex < _playbackQueue.Count)
            {
                var targetSongId = _playbackQueue[startIndex];
                _shuffledQueue.Remove(targetSongId);
                _shuffledQueue.Insert(0, targetSongId);
                RebuildShuffledQueueIndex();
            }

            var songIdToPlay = _shuffledQueue.FirstOrDefault();

            if (songIdToPlay != Guid.Empty)
            {
                var actualPlaybackIndex = GetPlaybackQueueIndex(songIdToPlay);
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
                var songIdToPlay = _shuffledQueue.ElementAtOrDefault(shuffledIndex);
                if (songIdToPlay != Guid.Empty)
                {
                    indexToPlay = GetPlaybackQueueIndex(songIdToPlay);
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
        await PlayFromOrderedIdsAsync(songIds, null).ConfigureAwait(false);
    }

    public async Task PlayArtistAsync(Guid artistId)
    {
        var songIds = await _libraryService.GetAllSongIdsByArtistIdAsync(artistId, SongSortOrder.TitleAsc).ConfigureAwait(false);
        await PlayFromOrderedIdsAsync(songIds, null).ConfigureAwait(false);
    }

    public async Task PlayFolderAsync(Guid folderId)
    {
        var songIds = await _libraryService.GetAllSongIdsByFolderIdAsync(folderId, SongSortOrder.TitleAsc).ConfigureAwait(false);
        await PlayFromOrderedIdsAsync(songIds, null).ConfigureAwait(false);
    }

    public async Task PlayPlaylistAsync(Guid playlistId)
    {
        var songIds = await _libraryService.GetAllSongIdsByPlaylistIdAsync(playlistId, SongSortOrder.TrackNumberAsc).ConfigureAwait(false);
        await PlayFromOrderedIdsAsync(songIds, null).ConfigureAwait(false);
    }

    public async Task PlayGenreAsync(Guid genreId)
    {
        var songIds = await _libraryService.GetAllSongIdsByGenreIdAsync(genreId, SongSortOrder.TitleAsc).ConfigureAwait(false);
        await PlayFromOrderedIdsAsync(songIds, null).ConfigureAwait(false);
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
        
        using (BeginQueueUpdate())
        {
            if (IsShuffleEnabled)
            {
                GenerateShuffledQueue();
            }
            else
            {
                _shuffledQueue.Clear();
            }
        }

        await _settingsService.SaveShuffleStateAsync(IsShuffleEnabled).ConfigureAwait(false);
        ShuffleModeChanged?.Invoke();
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
        return AddToQueueAsync(song?.Id ?? Guid.Empty);
    }

    public Task AddToQueueAsync(Guid songId)
    {
        if (songId == Guid.Empty || _playbackQueue.Contains(songId)) return Task.CompletedTask;

        using (BeginQueueUpdate())
        {
            _playbackQueue.Add(songId);
        }

        return Task.CompletedTask;
    }

    public Task AddRangeToQueueAsync(IEnumerable<Song> songs)
    {
        return AddRangeToQueueAsync(songs?.Select(s => s.Id) ?? Enumerable.Empty<Guid>());
    }

    public Task AddRangeToQueueAsync(IEnumerable<Guid> songIds)
    {
        if (songIds == null || !songIds.Any()) return Task.CompletedTask;

        var idsToAdd = songIds.ToList();

        if (idsToAdd.Any())
        {
            using (BeginQueueUpdate())
            {
                _playbackQueue.AddRange(idsToAdd);
            }
        }

        return Task.CompletedTask;
    }

    public Task PlayNextAsync(Song song)
    {
        return PlayNextAsync(song?.Id ?? Guid.Empty);
    }

    /// <summary>
    ///    Inserts a song ID into the queue immediately after the current track.
    /// </summary>
    /// <param name="songId">The ID of the song to play next.</param>
    public Task PlayNextAsync(Guid songId)
    {
        if (songId == Guid.Empty) return Task.CompletedTask;

        using (BeginQueueUpdate())
        {
            _playbackQueue.Remove(songId);
            if (IsShuffleEnabled) _shuffledQueue.Remove(songId);

            var insertIndex = CurrentQueueIndex == -1 ? 0 : CurrentQueueIndex + 1;
            insertIndex = Math.Min(insertIndex, _playbackQueue.Count);
            _playbackQueue.Insert(insertIndex, songId);

            if (IsShuffleEnabled)
            {
                var shuffledInsertIndex = CurrentShuffledIndex == -1 ? 0 : CurrentShuffledIndex + 1;
                shuffledInsertIndex = Math.Min(shuffledInsertIndex, _shuffledQueue.Count);
                _shuffledQueue.Insert(shuffledInsertIndex, songId);
            }
        }

        return Task.CompletedTask;
    }

    public Task RemoveFromQueueAsync(Song song)
    {
        return RemoveFromQueueAsync(song?.Id ?? Guid.Empty);
    }

    public async Task RemoveFromQueueAsync(Guid songId)
    {
        if (songId == Guid.Empty) return;

        var originalIndex = GetPlaybackQueueIndex(songId);
        if (originalIndex == -1) return;

        var isRemovingCurrentTrack = CurrentTrack?.Id == songId;

        if (isRemovingCurrentTrack)
        {
            _logger.LogDebug("Removing currently playing song ID '{SongId}' from queue.", songId);
            await _audioPlayer.StopAsync().ConfigureAwait(false);
            
            using (BeginQueueUpdate())
            {
                _playbackQueue.RemoveAt(originalIndex);
                if (IsShuffleEnabled) _shuffledQueue.Remove(songId);
            }

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
                }
            }
            else
            {
                // The queue is now empty.
                await StopAsync().ConfigureAwait(false);
                ClearQueuesInternal();
                QueueChanged?.Invoke();
                UpdateSmtcControls();
            }
        }
        else
        {
            using (BeginQueueUpdate())
            {
                _playbackQueue.RemoveAt(originalIndex);
                if (IsShuffleEnabled) _shuffledQueue.Remove(songId);
            }
        }
    }

    /// <inheritdoc />
    public async Task RemoveRangeFromQueueAsync(IEnumerable<Guid> songIds)
    {
        if (songIds == null || !songIds.Any()) return;

        var idsToRemove = songIds.ToHashSet();
        var currentTrackId = CurrentTrack?.Id;
        var isRemovingCurrentTrack = currentTrackId.HasValue && idsToRemove.Contains(currentTrackId.Value);

        if (isRemovingCurrentTrack)
        {
            _logger.LogDebug("Removing range that includes current track from queue.");
            await _audioPlayer.StopAsync().ConfigureAwait(false);
        }

        using (BeginQueueUpdate())
        {
            _playbackQueue.RemoveAll(id => idsToRemove.Contains(id));
            if (IsShuffleEnabled)
            {
                _shuffledQueue.RemoveAll(id => idsToRemove.Contains(id));
            }
        }

        if (isRemovingCurrentTrack)
        {
            if (_playbackQueue.Any())
            {
                // Play from current index (which has been clamped in CommitQueueChanges)
                var indexToPlay = Math.Clamp(CurrentQueueIndex, 0, _playbackQueue.Count - 1);
                await PlayQueueItemAsync(indexToPlay).ConfigureAwait(false);
            }
            else
            {
                await StopAsync().ConfigureAwait(false);
                ClearQueuesInternal();
                QueueChanged?.Invoke();
                UpdateSmtcControls();
            }
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

        // Use iterative approach with skip limit to prevent stack overflow if many
        // consecutive songs are deleted from disk but still in the queue
        const int maxSkipAttempts = 20;
        var currentIndex = originalQueueIndex;
        var skipCount = 0;
        Song? song = null;

        while (skipCount < maxSkipAttempts && currentIndex >= 0 && currentIndex < _playbackQueue.Count)
        {
            var songId = _playbackQueue[currentIndex];
            song = await _libraryService.GetSongByIdAsync(songId).ConfigureAwait(false);

            if (song != null)
            {
                // Found a valid song
                break;
            }

            _logger.LogWarning("Song metadata not found for ID {SongId}. Skipping to next track. (Skip {SkipCount}/{MaxSkip})",
                songId, skipCount + 1, maxSkipAttempts);
            skipCount++;

            // Move to next track using the same logic as TryGetNextTrackIndex
            if (IsShuffleEnabled)
            {
                var shuffledIdx = GetShuffledQueueIndex(songId);
                if (shuffledIdx >= 0 && shuffledIdx < _shuffledQueue.Count - 1)
                {
                    var nextShuffledId = _shuffledQueue[shuffledIdx + 1];
                    currentIndex = GetPlaybackQueueIndex(nextShuffledId);
                }
                else if (CurrentRepeatMode == RepeatMode.RepeatAll && _shuffledQueue.Count > 0)
                {
                    currentIndex = GetPlaybackQueueIndex(_shuffledQueue[0]);
                }
                else
                {
                    currentIndex = -1;
                }
            }
            else
            {
                if (currentIndex < _playbackQueue.Count - 1)
                {
                    currentIndex++;
                }
                else if (CurrentRepeatMode == RepeatMode.RepeatAll)
                {
                    currentIndex = 0;
                }
                else
                {
                    currentIndex = -1;
                }
            }
        }

        if (song == null)
        {
            _logger.LogError("Failed to find playable track after skipping {SkipCount} deleted songs. Stopping playback.", skipCount);
            IsTransitioningTrack = false;
            await StopAsync().ConfigureAwait(false);
            return;
        }

        CurrentTrack = song;
        CurrentQueueIndex = currentIndex;

        CurrentListenHistoryId = await _libraryService.CreateListenHistoryEntryAsync(CurrentTrack.Id).ConfigureAwait(false);

        if (IsShuffleEnabled)
        {
            CurrentShuffledIndex = GetShuffledQueueIndex(CurrentTrack.Id);
            if (CurrentShuffledIndex == -1)
            {
                _logger.LogWarning("Track ID '{SongId}' not found in shuffled queue. Regenerating shuffle.",
                    CurrentTrack.Id);
                GenerateShuffledQueue();
                CurrentShuffledIndex = GetShuffledQueueIndex(CurrentTrack.Id);
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
        
        using (BeginQueueUpdate())
        {
            ClearQueuesInternal();
        }
    }

    public async Task SavePlaybackStateAsync()
    {
        if (!_isInitialized) return;

        var state = new PlaybackState
        {
            CurrentTrackId = CurrentTrack?.Id,
            PlaybackQueueTrackIds = _playbackQueue.ToList(),
            CurrentPlaybackQueueIndex = CurrentQueueIndex,
            ShuffledQueueTrackIds = IsShuffleEnabled ? _shuffledQueue.ToList() : new List<Guid>(),
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
        _settingsService.FadeOnPlayPauseEnabledChanged -= OnFadeOnPlayPauseEnabledChanged;
        
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Restores the playback queue, indices, and current track from a saved state.
    /// </summary>
    private async Task<bool> RestoreInternalPlaybackStateAsync(PlaybackState state)
    {
        _logger.LogDebug("Attempting to restore previous playback state.");
        _playbackQueue = (state.PlaybackQueueTrackIds ?? Enumerable.Empty<Guid>()).ToList();
        RebuildPlaybackQueueIndex();

        if (!_playbackQueue.Any()) return false;

        if (IsShuffleEnabled)
        {
            _shuffledQueue = (state.ShuffledQueueTrackIds ?? Enumerable.Empty<Guid>()).ToList();
            RebuildShuffledQueueIndex();

            // Ensure shuffled queue is valid, otherwise regenerate it.
            if (_shuffledQueue.Count != _playbackQueue.Count) GenerateShuffledQueue();
        }

        if (state.CurrentTrackId.HasValue)
        {
            var currentSong = await _libraryService.GetSongByIdAsync(state.CurrentTrackId.Value).ConfigureAwait(false);
            if (currentSong != null)
            {
                CurrentTrack = currentSong;
                CurrentQueueIndex = GetPlaybackQueueIndex(currentSong.Id);
                if (CurrentQueueIndex == -1)
                {
                    _logger.LogWarning("Current track '{SongTitle}' not found in restored queue.", currentSong.Title);
                    CurrentQueueIndex = 0;
                }

                if (IsShuffleEnabled)
                {
                    CurrentShuffledIndex = GetShuffledQueueIndex(currentSong.Id);
                    if (CurrentShuffledIndex == -1)
                    {
                        _logger.LogWarning("Current track not found in shuffled queue. Regenerating shuffle.");
                        GenerateShuffledQueue();
                        CurrentShuffledIndex = GetShuffledQueueIndex(currentSong.Id);
                    }
                }

                CurrentListenHistoryId = null;
                await _audioPlayer.LoadAsync(CurrentTrack).ConfigureAwait(false);
            }
            else
            {
                // The current track was deleted from the library but the queue is still valid.
                // Fall back to starting from the beginning of the queue rather than clearing it.
                _logger.LogWarning("Could not restore current track metadata: Song ID {SongId} not found. Falling back to queue start.", state.CurrentTrackId.Value);
                CurrentQueueIndex = 0;
                CurrentShuffledIndex = IsShuffleEnabled ? 0 : -1;
                CurrentTrack = null;
                CurrentListenHistoryId = null;
            }
        }
        else
        {
            // Restore indices even if the track itself isn't loaded.
            CurrentQueueIndex = state.CurrentPlaybackQueueIndex;
            CurrentShuffledIndex = state.CurrentShuffledQueueIndex;
        }

        return true;
    }

    private async Task PlayFromOrderedIdsAsync(IList<Guid> orderedSongIds, bool? startShuffled = null)
    {
        if (orderedSongIds == null || !orderedSongIds.Any())
        {
            await PlayAsync(Enumerable.Empty<Guid>(), 0, startShuffled).ConfigureAwait(false);
            return;
        }

        await PlayAsync(orderedSongIds, 0, startShuffled).ConfigureAwait(false);
    }

    private void ClearQueuesInternal()
    {
        _playbackQueue.Clear();
        _shuffledQueue.Clear();
        _playbackQueueIndex.Clear();
        _shuffledQueueIndex.Clear();
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
            _shuffledQueueIndex.Clear();
            return;
        }

        _shuffledQueue = new List<Guid>(_playbackQueue);
        var n = _shuffledQueue.Count;
        while (n > 1)
        {
            n--;
            var k = _random.Next(n + 1);
            (_shuffledQueue[k], _shuffledQueue[n]) = (_shuffledQueue[n], _shuffledQueue[k]);
        }

        // Rebuild the index dictionary for O(1) lookups
        RebuildShuffledQueueIndex();
    }

    /// <summary>
    ///     Rebuilds the playback queue index dictionary for O(1) lookups.
    /// </summary>
    private void RebuildPlaybackQueueIndex()
    {
        if (_updateCount > 0) return;
        _playbackQueueIndex.Clear();
        for (var i = 0; i < _playbackQueue.Count; i++)
        {
            _playbackQueueIndex[_playbackQueue[i]] = i;
        }
    }

    /// <summary>
    ///     Rebuilds the shuffled queue index dictionary for O(1) lookups.
    /// </summary>
    private void RebuildShuffledQueueIndex()
    {
        if (_updateCount > 0) return;
        _shuffledQueueIndex.Clear();
        for (var i = 0; i < _shuffledQueue.Count; i++)
        {
            _shuffledQueueIndex[_shuffledQueue[i]] = i;
        }
    }

    /// <summary>
    ///     Gets the index of a song ID in the playback queue using O(1) dictionary lookup.
    /// </summary>
    private int GetPlaybackQueueIndex(Guid songId)
    {
        return _playbackQueueIndex.TryGetValue(songId, out var index) ? index : -1;
    }

    /// <summary>
    ///     Gets the index of a song ID in the shuffled queue using O(1) dictionary lookup.
    /// </summary>
    private int GetShuffledQueueIndex(Guid songId)
    {
        return _shuffledQueueIndex.TryGetValue(songId, out var index) ? index : -1;
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
            if (CurrentTrack != null) CurrentShuffledIndex = GetShuffledQueueIndex(CurrentTrack.Id);
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
                nextIndex = GetPlaybackQueueIndex(_shuffledQueue[nextShuffledIndex]);
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

    /// <summary>
    ///     Executes an async action with proper error handling for event handlers.
    ///     This is the preferred pattern over async void for fire-and-forget scenarios.
    /// </summary>
    private void FireAndForgetSafe(Func<Task> asyncAction, string operationName)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await asyncAction().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in fire-and-forget operation: {Operation}", operationName);
            }
        });
    }

    private void OnAudioPlayerPlaybackEnded()
    {
        // Use fire-and-forget helper to avoid async void
        FireAndForgetSafe(
            async () => await NextAsync().ConfigureAwait(false),
            "PlaybackEnded transition");
    }

    private void OnAudioPlayerStateChanged()
    {
        PlaybackStateChanged?.Invoke();
        if (IsTransitioningTrack && _audioPlayer.IsPlaying) IsTransitioningTrack = false;
    }

    private void OnAudioPlayerVolumeChanged()
    {
        // Use fire-and-forget helper to avoid async void
        FireAndForgetSafe(
            async () =>
            {
                await _settingsService.SaveVolumeAsync(_audioPlayer.Volume).ConfigureAwait(false);
                VolumeStateChanged?.Invoke();
            },
            "Volume save");
    }

    private void OnAudioPlayerPositionChanged()
    {
        PositionChanged?.Invoke();
    }

    private void OnAudioPlayerDurationChanged()
    {
        DurationChanged?.Invoke();
    }

    private void OnAudioPlayerErrorOccurred(string errorMessage)
    {
        // Use fire-and-forget helper to avoid async void
        FireAndForgetSafe(
            async () =>
            {
                IsTransitioningTrack = false;
                await StopAsync().ConfigureAwait(false);
            },
            "Error recovery");
    }

    private void OnAudioPlayerMediaOpened()
    {
        TrackChanged?.Invoke();
        UpdateSmtcControls();
    }

    private void OnAudioPlayerSmtcNextButtonPressed()
    {
        // Use fire-and-forget helper to avoid async void
        FireAndForgetSafe(
            async () => await NextAsync().ConfigureAwait(false),
            "SMTC Next");
    }

    private void OnAudioPlayerSmtcPreviousButtonPressed()
    {
        // Use fire-and-forget helper to avoid async void
        FireAndForgetSafe(
            async () => await PreviousAsync().ConfigureAwait(false),
            "SMTC Previous");
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

    private void OnVolumeNormalizationEnabledChanged(bool isEnabled)
    {
        // Use fire-and-forget helper to avoid async void
        FireAndForgetSafe(
            async () => await ApplyReplayGainIfEnabledAsync(isEnabled).ConfigureAwait(false),
            "ReplayGain settings change");
    }

    private void OnFadeOnPlayPauseEnabledChanged(bool isEnabled)
    {
        _audioPlayer.SetFadeOnPlayPauseEnabled(isEnabled);
    }
}
