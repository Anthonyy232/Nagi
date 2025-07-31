using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Nagi.Core.Services.Implementations {
    /// <summary>
    /// Manages music playback, queue, and state by coordinating between the audio player,
    /// library service, and settings service.
    /// </summary>
    public class MusicPlaybackService : IMusicPlaybackService, IDisposable {
        private readonly IAudioPlayer _audioPlayer;
        private readonly ILibraryService _libraryService;
        private readonly ISettingsService _settingsService;
        private readonly IMetadataExtractor _metadataExtractor;
        private readonly Random _random = new();

        private List<Song> _playbackQueue = new();
        private List<Song> _shuffledQueue = new();
        private int _currentShuffledIndex = -1;
        private bool _isInitialized;

        /// <summary>
        /// Initializes a new instance of the <see cref="MusicPlaybackService"/> class.
        /// </summary>
        public MusicPlaybackService(
            ISettingsService settingsService,
            IAudioPlayer audioPlayer,
            ILibraryService libraryService,
            IMetadataExtractor metadataExtractor) {
            _settingsService = settingsService;
            _audioPlayer = audioPlayer;
            _libraryService = libraryService;
            _metadataExtractor = metadataExtractor;

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
        public long? CurrentListenHistoryId { get; private set; }
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
        public async Task InitializeAsync(bool restoreLastSession = true) {
            if (_isInitialized) return;

            try {
                // Initialize player state from settings.
                await _audioPlayer.SetVolumeAsync(await _settingsService.GetInitialVolumeAsync());
                await _audioPlayer.SetMuteAsync(await _settingsService.GetInitialMuteStateAsync());
                IsShuffleEnabled = await _settingsService.GetInitialShuffleStateAsync();
                CurrentRepeatMode = await _settingsService.GetInitialRepeatModeAsync();

                bool restoredSuccessfully = false;
                if (restoreLastSession && await _settingsService.GetRestorePlaybackStateEnabledAsync()) {
                    var savedState = await _settingsService.GetPlaybackStateAsync();
                    if (savedState != null) {
                        restoredSuccessfully = await RestoreInternalPlaybackStateAsync(savedState);
                    }
                }

                // If restoration was not attempted or failed, ensure queues are cleared.
                if (!restoredSuccessfully) {
                    ClearQueuesInternal();
                }
            }
            catch (Exception ex) {
                Debug.WriteLine($"[MusicPlaybackService] Initialization failed: {ex.Message}. Using default settings.");
                await _audioPlayer.SetVolumeAsync(0.5);
                await _audioPlayer.SetMuteAsync(false);
                IsShuffleEnabled = false;
                CurrentRepeatMode = RepeatMode.Off;
                ClearQueuesInternal();
            }

            _isInitialized = true;
            UpdateSmtcControls();

            // Invoke events to update the UI with the initial state.
            VolumeStateChanged?.Invoke();
            ShuffleModeChanged?.Invoke();
            RepeatModeChanged?.Invoke();
            QueueChanged?.Invoke();
            TrackChanged?.Invoke();
            PlaybackStateChanged?.Invoke();
            PositionChanged?.Invoke();
        }

        /// <summary>
        /// Restores the playback queue, indices, and current track from a saved state.
        /// </summary>
        private async Task<bool> RestoreInternalPlaybackStateAsync(PlaybackState state) {
            var songIds = new HashSet<Guid>(state.PlaybackQueueTrackIds ?? Enumerable.Empty<Guid>());
            if (!songIds.Any()) return false;

            var songMap = await _libraryService.GetSongsByIdsAsync(songIds);
            if (!songMap.Any()) return false;

            _playbackQueue = (state.PlaybackQueueTrackIds ?? Enumerable.Empty<Guid>())
                .Select(id => songMap.GetValueOrDefault(id))
                .Where(s => s != null)
                .ToList();

            if (!_playbackQueue.Any()) return false;

            if (IsShuffleEnabled) {
                var shuffledIds = state.ShuffledQueueTrackIds ?? Enumerable.Empty<Guid>();
                _shuffledQueue = shuffledIds
                    .Select(id => songMap.GetValueOrDefault(id))
                    .Where(s => s != null)
                    .ToList();

                // Ensure shuffled queue is valid, otherwise regenerate it.
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

                CurrentListenHistoryId = null;

                // Wait for the media duration to be known before seeking to the saved position.
                var durationKnownTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnDurationChangedHandler() {
                    _audioPlayer.DurationChanged -= OnDurationChangedHandler;
                    durationKnownTcs.TrySetResult();
                }
                _audioPlayer.DurationChanged += OnDurationChangedHandler;

                try {
                    await _audioPlayer.LoadAsync(CurrentTrack);
                    var completedTask = await Task.WhenAny(durationKnownTcs.Task, Task.Delay(5000));

                    if (completedTask == durationKnownTcs.Task && _audioPlayer.Duration > TimeSpan.Zero && state.CurrentPositionSeconds > 0) {
                        await _audioPlayer.SeekAsync(TimeSpan.FromSeconds(state.CurrentPositionSeconds));
                    }
                    else if (completedTask != durationKnownTcs.Task) {
                        Debug.WriteLine("[MusicPlaybackService] WARN: Timed out waiting for duration during session restore.");
                    }
                }
                finally {
                    _audioPlayer.DurationChanged -= OnDurationChangedHandler;
                }
            }
            else {
                // Restore indices even if the track itself isn't loaded.
                CurrentQueueIndex = state.CurrentPlaybackQueueIndex;
                _currentShuffledIndex = state.CurrentShuffledQueueIndex;
            }

            return true;
        }

        /// <inheritdoc />
        public async Task PlayTransientFileAsync(string filePath) {
            var metadata = await _metadataExtractor.ExtractMetadataAsync(filePath);

            var transientSong = new Song {
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



        /// <inheritdoc />
        public async Task PlayAsync(Song song) {
            if (song == null) return;

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
                var songToPlay = _shuffledQueue.ElementAtOrDefault(startIndex);
                if (songToPlay == null) {
                    startIndex = 0;
                    songToPlay = _shuffledQueue.FirstOrDefault();
                }

                if (songToPlay != null) {
                    var actualPlaybackIndex = _playbackQueue.IndexOf(songToPlay);
                    await PlayQueueItemAsync(actualPlaybackIndex);
                }
                else {
                    await StopAsync();
                }
            }
            else {
                if (startIndex < 0 || startIndex >= _playbackQueue.Count) {
                    startIndex = 0;
                }
                await PlayQueueItemAsync(startIndex);
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

            // If no track is loaded but a queue exists, play from the last known position.
            // This allows playback to resume correctly after being stopped at a queue boundary.
            if (_playbackQueue.Any()) {
                int indexToPlay = CurrentQueueIndex >= 0 ? CurrentQueueIndex : 0;

                if (IsShuffleEnabled && _shuffledQueue.Any()) {
                    var shuffledIndex = _currentShuffledIndex >= 0 ? _currentShuffledIndex : 0;
                    var songToPlay = _shuffledQueue.ElementAtOrDefault(shuffledIndex);
                    if (songToPlay != null) {
                        indexToPlay = _playbackQueue.IndexOf(songToPlay);
                    }
                }

                if (indexToPlay >= 0 && indexToPlay < _playbackQueue.Count) {
                    await PlayQueueItemAsync(indexToPlay);
                }
            }
        }

        /// <inheritdoc />
        public async Task StopAsync() {
            await _audioPlayer.StopAsync();

            // Null the track to indicate a "stopped" state but preserve the queue indices.
            // This is crucial for Next/Previous commands to work correctly from the
            // beginning or end of the queue.
            CurrentTrack = null;
            CurrentListenHistoryId = null;
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

            // This logic handles two distinct cases for robust navigation.
            if (CurrentTrack != null) {
                // Case 1: A track is currently active. Find the next one in the sequence.
                if (TryGetNextTrackIndex(moveForward: true, out int nextIndex)) {
                    await PlayQueueItemAsync(nextIndex);
                }
                else {
                    // Reached the end of the queue.
                    await StopAsync();
                }
            }
            else if (_playbackQueue.Any()) {
                // Case 2: The player is stopped at a queue boundary (e.g., after pressing Previous
                // until the start). Pressing Next should resume playback from the current position.
                await PlayQueueItemAsync(CurrentQueueIndex);
            }
        }

        /// <inheritdoc />
        public async Task PreviousAsync() {
            // If the track has played for more than 3 seconds, restart it.
            if (CurrentTrack != null && _audioPlayer.CurrentPosition.TotalSeconds > 3 && CurrentRepeatMode != RepeatMode.RepeatOne) {
                await SeekAsync(TimeSpan.Zero);
                if (!_audioPlayer.IsPlaying) await _audioPlayer.PlayAsync();
                return;
            }

            if (CurrentRepeatMode == RepeatMode.RepeatOne && CurrentTrack != null) {
                await PlayQueueItemAsync(CurrentQueueIndex);
                return;
            }

            // This logic mirrors NextAsync for robust navigation.
            if (CurrentTrack != null) {
                // Case 1: A track is currently active. Find the previous one in the sequence.
                if (TryGetNextTrackIndex(moveForward: false, out int prevIndex)) {
                    await PlayQueueItemAsync(prevIndex);
                }
                else {
                    // Reached the beginning of the queue.
                    await StopAsync();
                }
            }
            else if (_playbackQueue.Any()) {
                // Case 2: The player is stopped at a queue boundary (e.g., after pressing Next
                // until the end). Pressing Previous should resume playback from the current position.
                await PlayQueueItemAsync(CurrentQueueIndex);
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
            var songIds = await _libraryService.GetAllSongIdsByAlbumIdAsync(albumId, SongSortOrder.TrackNumberAsc);
            await PlayFromOrderedIdsAsync(songIds, startShuffled: false);
        }

        /// <inheritdoc />
        public async Task PlayArtistAsync(Guid artistId) {
            var songIds = await _libraryService.GetAllSongIdsByArtistIdAsync(artistId, SongSortOrder.TitleAsc);
            await PlayFromOrderedIdsAsync(songIds, startShuffled: false);
        }

        /// <inheritdoc />
        public async Task PlayFolderAsync(Guid folderId) {
            var songIds = await _libraryService.GetAllSongIdsByFolderIdAsync(folderId, SongSortOrder.TitleAsc);
            await PlayFromOrderedIdsAsync(songIds, startShuffled: false);
        }

        /// <inheritdoc />
        public async Task PlayPlaylistAsync(Guid playlistId) {
            var orderedSongs = (await _libraryService.GetSongsInPlaylistOrderedAsync(playlistId))?.ToList();
            await PlayAsync(orderedSongs ?? new List<Song>(), 0, startShuffled: false);
        }

        /// <inheritdoc />
        public async Task PlayGenreAsync(Guid genreId) {
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

            CurrentRepeatMode = mode;
            await _settingsService.SaveRepeatModeAsync(CurrentRepeatMode);
            RepeatModeChanged?.Invoke();
            UpdateSmtcControls();
        }

        /// <inheritdoc />
        public Task AddToQueueAsync(Song song) {
            if (song == null || _playbackQueue.Contains(song)) return Task.CompletedTask;

            _playbackQueue.Add(song);
            if (IsShuffleEnabled) {
                // To maintain predictable behavior, the entire shuffled queue is regenerated
                // to include the new song in a random position.
                GenerateShuffledQueue();
                if (CurrentTrack != null) {
                    _currentShuffledIndex = _shuffledQueue.IndexOf(CurrentTrack);
                }
            }
            QueueChanged?.Invoke();
            UpdateSmtcControls();
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task AddRangeToQueueAsync(IEnumerable<Song> songs) {
            if (songs == null || !songs.Any()) return Task.CompletedTask;

            var currentQueueSet = _playbackQueue.ToHashSet();
            var songsToAdd = songs.Where(s => currentQueueSet.Add(s)).ToList();

            if (songsToAdd.Any()) {
                _playbackQueue.AddRange(songsToAdd);
                if (IsShuffleEnabled) {
                    GenerateShuffledQueue();
                    if (CurrentTrack != null) {
                        _currentShuffledIndex = _shuffledQueue.IndexOf(CurrentTrack);
                    }
                }
                QueueChanged?.Invoke();
                UpdateSmtcControls();
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task PlayNextAsync(Song song) {
            if (song == null) return Task.CompletedTask;

            _playbackQueue.Remove(song);
            if (IsShuffleEnabled) {
                _shuffledQueue.Remove(song);
            }

            var insertIndex = CurrentQueueIndex == -1 ? 0 : CurrentQueueIndex + 1;
            _playbackQueue.Insert(insertIndex, song);

            if (IsShuffleEnabled) {
                var shuffledInsertIndex = _currentShuffledIndex == -1 ? 0 : _currentShuffledIndex + 1;
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

            bool isRemovingCurrentTrack = CurrentTrack == song;

            if (isRemovingCurrentTrack) {
                // If the currently playing track is removed, stop playback and decide what to do next.
                await _audioPlayer.StopAsync();
                _playbackQueue.RemoveAt(originalIndex);
                if (IsShuffleEnabled) _shuffledQueue.Remove(song);

                if (_playbackQueue.Any()) {
                    // Attempt to play the next song in the queue.
                    int nextIndexToPlay = originalIndex;
                    if (nextIndexToPlay >= _playbackQueue.Count) {
                        // If the removed track was the last one, wrap around if repeat is on.
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
                    // The queue is now empty.
                    await StopAsync();
                    ClearQueuesInternal();
                    QueueChanged?.Invoke();
                }
            }
            else {
                // If a different track is removed, simply update the queues and indices.
                _playbackQueue.RemoveAt(originalIndex);
                if (IsShuffleEnabled) _shuffledQueue.Remove(song);

                if (CurrentTrack != null) {
                    // Adjust the current index if the removed song was before the current one.
                    if (originalIndex < CurrentQueueIndex) {
                        CurrentQueueIndex--;
                    }
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
                await StopAsync();
                return;
            }

            IsTransitioningTrack = true;
            CurrentTrack = _playbackQueue[originalQueueIndex];
            CurrentQueueIndex = originalQueueIndex;

            CurrentListenHistoryId = await _libraryService.CreateListenHistoryEntryAsync(CurrentTrack.Id);

            if (IsShuffleEnabled) {
                _currentShuffledIndex = _shuffledQueue.IndexOf(CurrentTrack);
            }

            Debug.WriteLine($"[MusicPlaybackService] Now playing '{CurrentTrack.Title}' (Index: {CurrentQueueIndex}, Shuffled Index: {_currentShuffledIndex})");
            await _audioPlayer.LoadAsync(CurrentTrack);
            await _audioPlayer.PlayAsync();
            UpdateSmtcControls();
        }

        /// <inheritdoc />
        public async Task ClearQueueAsync() {
            if (!_playbackQueue.Any()) return;
            await StopAsync();
            ClearQueuesInternal();
            QueueChanged?.Invoke();
            UpdateSmtcControls();
        }

        /// <inheritdoc />
        public async Task SavePlaybackStateAsync() {
            if (!_isInitialized) return;

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

        /// <summary>
        /// Fetches songs by their IDs and initiates playback.
        /// </summary>
        private async Task PlayFromOrderedIdsAsync(IList<Guid> orderedSongIds, bool startShuffled) {
            if (orderedSongIds == null || !orderedSongIds.Any()) {
                await PlayAsync(new List<Song>(), 0, startShuffled: false);
                return;
            }

            var songMap = await _libraryService.GetSongsByIdsAsync(orderedSongIds);

            var orderedSongs = orderedSongIds
                .Select(id => songMap.GetValueOrDefault(id))
                .Where(s => s != null)
                .ToList();

            await PlayAsync(orderedSongs, 0, startShuffled);
        }

        /// <summary>
        /// Resets all queues and playback indices to their default state.
        /// </summary>
        private void ClearQueuesInternal() {
            _playbackQueue.Clear();
            _shuffledQueue.Clear();
            CurrentTrack = null;
            CurrentQueueIndex = -1;
            _currentShuffledIndex = -1;
            CurrentListenHistoryId = null;
        }

        /// <summary>
        /// Generates a new shuffled queue from the main playback queue using the Fisher-Yates algorithm.
        /// </summary>
        private void GenerateShuffledQueue() {
            if (!_playbackQueue.Any()) {
                _shuffledQueue.Clear();
                return;
            }

            _shuffledQueue = new List<Song>(_playbackQueue);
            var n = _shuffledQueue.Count;
            while (n > 1) {
                n--;
                var k = _random.Next(n + 1);
                (_shuffledQueue[k], _shuffledQueue[n]) = (_shuffledQueue[n], _shuffledQueue[k]);
            }
        }

        /// <summary>
        /// Calculates the index of the next track to play based on the current state.
        /// </summary>
        /// <param name="moveForward">True to get the next track, false for the previous.</param>
        /// <param name="nextPlaybackQueueIndex">The calculated index in the main playback queue.</param>
        /// <returns>True if a next track exists; otherwise, false.</returns>
        private bool TryGetNextTrackIndex(bool moveForward, out int nextPlaybackQueueIndex) {
            nextPlaybackQueueIndex = -1;
            if (!_playbackQueue.Any() || CurrentQueueIndex == -1) {
                return false;
            }

            int nextIndex = -1;

            if (IsShuffleEnabled) {
                int nextShuffledIndex = -1;
                if (moveForward) {
                    if (_currentShuffledIndex < _shuffledQueue.Count - 1) {
                        nextShuffledIndex = _currentShuffledIndex + 1;
                    }
                    else if (CurrentRepeatMode == RepeatMode.RepeatAll) {
                        nextShuffledIndex = 0;
                    }
                }
                else // move backward
                {
                    if (_currentShuffledIndex > 0) {
                        nextShuffledIndex = _currentShuffledIndex - 1;
                    }
                    else if (CurrentRepeatMode == RepeatMode.RepeatAll) {
                        nextShuffledIndex = _shuffledQueue.Count - 1;
                    }
                }

                if (nextShuffledIndex != -1) {
                    nextIndex = _playbackQueue.IndexOf(_shuffledQueue[nextShuffledIndex]);
                }
            }
            else // Not shuffled
            {
                if (moveForward) {
                    if (CurrentQueueIndex < _playbackQueue.Count - 1) {
                        nextIndex = CurrentQueueIndex + 1;
                    }
                    else if (CurrentRepeatMode == RepeatMode.RepeatAll) {
                        nextIndex = 0;
                    }
                }
                else // move backward
                {
                    if (CurrentQueueIndex > 0) {
                        nextIndex = CurrentQueueIndex - 1;
                    }
                    else if (CurrentRepeatMode == RepeatMode.RepeatAll) {
                        nextIndex = _playbackQueue.Count - 1;
                    }
                }
            }

            if (nextIndex != -1) {
                nextPlaybackQueueIndex = nextIndex;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Updates the enabled/disabled state of the Next/Previous buttons in the System Media Transport Controls.
        /// </summary>
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

                // If a track is active, you can always go "previous" to either restart the track or go to the prior one.
                if (CurrentTrack != null) {
                    canGoPrevious = true;
                }
            }

            _audioPlayer.UpdateSmtcButtonStates(canGoNext, canGoPrevious);
        }

        private async void OnAudioPlayerPlaybackEnded() {
            // When the current track finishes naturally, advance to the next one.
            await NextAsync();
        }

        private void OnAudioPlayerStateChanged() {
            PlaybackStateChanged?.Invoke();
            if (IsTransitioningTrack && _audioPlayer.IsPlaying) {
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
            Debug.WriteLine($"[MusicPlaybackService] ERROR: Audio player error: {errorMessage}");
            IsTransitioningTrack = false;
            await StopAsync();
        }

        private void OnAudioPlayerMediaOpened() {
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
    }
}