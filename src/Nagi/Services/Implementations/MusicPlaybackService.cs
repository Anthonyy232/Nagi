using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Nagi.Models;
using Nagi.Services.Abstractions;

namespace Nagi.Services.Implementations;

public class MusicPlaybackService : IMusicPlaybackService, IDisposable
{
    private readonly IAudioPlayer _audioPlayer;
    private readonly ILibraryService _libraryService;
    private readonly Random _random = new();
    private readonly ISettingsService _settingsService;

    private int _currentShuffledIndex = -1;
    private bool _isInitialized;

    private List<Song> _playbackQueue = new();
    private List<Song> _shuffledQueue = new();

    public MusicPlaybackService(ISettingsService settingsService, IAudioPlayer audioPlayer,
        ILibraryService libraryService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _audioPlayer = audioPlayer ?? throw new ArgumentNullException(nameof(audioPlayer));
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));

        _audioPlayer.PlaybackEnded += OnAudioPlayerPlaybackEnded;
        _audioPlayer.StateChanged += OnAudioPlayerStateChanged;
        _audioPlayer.VolumeChanged += OnAudioPlayerVolumeChanged;
        _audioPlayer.PositionChanged += OnAudioPlayerPositionChanged;
        _audioPlayer.ErrorOccurred += OnAudioPlayerErrorOccurred;
        _audioPlayer.MediaOpened += OnAudioPlayerMediaOpened;
        _audioPlayer.SmtcNextButtonPressed += OnAudioPlayerSmtcNextButtonPressed;
        _audioPlayer.SmtcPreviousButtonPressed += OnAudioPlayerSmtcPreviousButtonPressed;
    }

    public void Dispose()
    {
        if (_audioPlayer != null)
        {
            _audioPlayer.PlaybackEnded -= OnAudioPlayerPlaybackEnded;
            _audioPlayer.StateChanged -= OnAudioPlayerStateChanged;
            _audioPlayer.VolumeChanged -= OnAudioPlayerVolumeChanged;
            _audioPlayer.PositionChanged -= OnAudioPlayerPositionChanged;
            _audioPlayer.ErrorOccurred -= OnAudioPlayerErrorOccurred;
            _audioPlayer.MediaOpened -= OnAudioPlayerMediaOpened;
            _audioPlayer.SmtcNextButtonPressed -= OnAudioPlayerSmtcNextButtonPressed;
            _audioPlayer.SmtcPreviousButtonPressed -= OnAudioPlayerSmtcPreviousButtonPressed;
        }

        GC.SuppressFinalize(this);
    }

    public Song? CurrentTrack { get; private set; }

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

    public event Action? TrackChanged;
    public event Action? PlaybackStateChanged;
    public event Action? VolumeStateChanged;
    public event Action? ShuffleModeChanged;
    public event Action? RepeatModeChanged;
    public event Action? QueueChanged;
    public event Action? PositionChanged;

    public async Task InitializeAsync()
    {
        if (_isInitialized) return;

        try
        {
            await _audioPlayer.SetVolumeAsync(await _settingsService.GetInitialVolumeAsync());
            await _audioPlayer.SetMuteAsync(await _settingsService.GetInitialMuteStateAsync());
            IsShuffleEnabled = await _settingsService.GetInitialShuffleStateAsync();
            CurrentRepeatMode = await _settingsService.GetInitialRepeatModeAsync();

            var savedState = await _settingsService.GetPlaybackStateAsync();
            if (savedState != null && !await RestoreInternalPlaybackStateAsync(savedState)) ClearQueuesInternal();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[MusicPlaybackService] Error during initialization: {ex.Message}. Using default settings.");
            await _audioPlayer.SetVolumeAsync(0.5);
            await _audioPlayer.SetMuteAsync(false);
            IsShuffleEnabled = false;
            CurrentRepeatMode = RepeatMode.Off;
            ClearQueuesInternal();
        }

        _isInitialized = true;
        UpdateSmtcControls();

        // Notify listeners of the initial state.
        VolumeStateChanged?.Invoke();
        ShuffleModeChanged?.Invoke();
        RepeatModeChanged?.Invoke();
        QueueChanged?.Invoke();
        TrackChanged?.Invoke();
        PlaybackStateChanged?.Invoke();
        PositionChanged?.Invoke();
    }

    public async Task PlayAsync(Song song)
    {
        if (song == null) return;

        // If shuffle is on and the song is already in the queue, just jump to it.
        if (IsShuffleEnabled && _playbackQueue.Contains(song))
        {
            await PlayQueueItemAsync(_playbackQueue.IndexOf(song));
        }
        else
        {
            // Otherwise, create a new queue with just this song.
            var wasShuffleEnabled = IsShuffleEnabled;
            await ClearQueueAsync();
            await SetShuffleAsync(wasShuffleEnabled); // Restore previous shuffle state
            await AddToQueueAsync(song);
            await PlayQueueItemAsync(0);
        }
    }

    public async Task PlayAsync(IEnumerable<Song> songs, int startIndex = 0, bool startShuffled = false)
    {
        var songList = songs?.Distinct().ToList() ?? new List<Song>();
        if (!songList.Any())
        {
            await StopAsync();
            ClearQueuesInternal();
            QueueChanged?.Invoke();
            UpdateSmtcControls();
            return;
        }

        await StopAsync();
        _playbackQueue = songList;
        await SetShuffleAsync(startShuffled);

        var actualStartIndex = startIndex;
        if (startShuffled && _shuffledQueue.Any())
        {
            _currentShuffledIndex = _random.Next(_shuffledQueue.Count);
            actualStartIndex = _playbackQueue.IndexOf(_shuffledQueue[_currentShuffledIndex]);
        }
        else
        {
            if (actualStartIndex < 0 || actualStartIndex >= _playbackQueue.Count) actualStartIndex = 0;
            _currentShuffledIndex = -1;
        }

        QueueChanged?.Invoke();
        await PlayQueueItemAsync(actualStartIndex);
    }

    public async Task PlayPauseAsync()
    {
        if (_audioPlayer.IsPlaying)
        {
            await _audioPlayer.PauseAsync();
            return;
        }

        // If a track is loaded but paused, play it.
        if (CurrentTrack != null)
        {
            await _audioPlayer.PlayAsync();
            return;
        }

        // If no track is loaded, try to play from the queue.
        if (_playbackQueue.Any())
        {
            var indexToPlay = 0;
            if (IsShuffleEnabled && _shuffledQueue.Any())
            {
                var shuffledIndex = _currentShuffledIndex >= 0 ? _currentShuffledIndex : 0;
                var songToPlay = _shuffledQueue.ElementAtOrDefault(shuffledIndex);
                if (songToPlay != null) indexToPlay = _playbackQueue.IndexOf(songToPlay);
            }
            else
            {
                indexToPlay = CurrentQueueIndex >= 0 ? CurrentQueueIndex : 0;
            }

            if (indexToPlay >= 0 && indexToPlay < _playbackQueue.Count) await PlayQueueItemAsync(indexToPlay);
        }
    }

    public async Task StopAsync()
    {
        await _audioPlayer.StopAsync();
        CurrentTrack = null;
        IsTransitioningTrack = false;
        TrackChanged?.Invoke();
        PositionChanged?.Invoke();
        UpdateSmtcControls();
    }

    public async Task NextAsync()
    {
        if (!_playbackQueue.Any())
        {
            await StopAsync();
            return;
        }

        // If repeating a single track, just restart it.
        if (CurrentRepeatMode == RepeatMode.RepeatOne && CurrentTrack != null)
        {
            await PlayQueueItemAsync(CurrentQueueIndex);
            return;
        }

        var nextIndex = -1;
        if (IsShuffleEnabled)
        {
            if (_currentShuffledIndex < _shuffledQueue.Count - 1)
            {
                nextIndex = _currentShuffledIndex + 1;
            }
            else if (CurrentRepeatMode == RepeatMode.RepeatAll)
            {
                GenerateShuffledQueue();
                nextIndex = 0;
            }

            if (nextIndex != -1 && nextIndex < _shuffledQueue.Count)
            {
                await PlayQueueItemAsync(_playbackQueue.IndexOf(_shuffledQueue[nextIndex]));
                return;
            }
        }
        else // Sequential
        {
            if (CurrentQueueIndex < _playbackQueue.Count - 1)
                nextIndex = CurrentQueueIndex + 1;
            else if (CurrentRepeatMode == RepeatMode.RepeatAll) nextIndex = 0;
            if (nextIndex != -1)
            {
                await PlayQueueItemAsync(nextIndex);
                return;
            }
        }

        // If no next track is found, stop playback.
        await StopAsync();
    }

    public async Task PreviousAsync()
    {
        // If the track has been playing for a few seconds, restart it instead of going to the previous one.
        if (CurrentTrack != null && _audioPlayer.CurrentPosition.TotalSeconds > 3 &&
            CurrentRepeatMode != RepeatMode.RepeatOne)
        {
            await SeekAsync(TimeSpan.Zero);
            if (!_audioPlayer.IsPlaying) await _audioPlayer.PlayAsync();
            return;
        }

        if (!_playbackQueue.Any())
        {
            await StopAsync();
            return;
        }

        if (CurrentRepeatMode == RepeatMode.RepeatOne && CurrentTrack != null)
        {
            await PlayQueueItemAsync(CurrentQueueIndex);
            return;
        }

        var prevIndex = -1;
        if (IsShuffleEnabled)
        {
            if (_currentShuffledIndex > 0)
                prevIndex = _currentShuffledIndex - 1;
            else if (CurrentRepeatMode == RepeatMode.RepeatAll) prevIndex = _shuffledQueue.Count - 1;
            if (prevIndex != -1 && prevIndex < _shuffledQueue.Count)
            {
                await PlayQueueItemAsync(_playbackQueue.IndexOf(_shuffledQueue[prevIndex]));
                return;
            }
        }
        else // Sequential
        {
            if (CurrentQueueIndex > 0)
                prevIndex = CurrentQueueIndex - 1;
            else if (CurrentRepeatMode == RepeatMode.RepeatAll) prevIndex = _playbackQueue.Count - 1;
            if (prevIndex != -1)
            {
                await PlayQueueItemAsync(prevIndex);
                return;
            }
        }

        await StopAsync();
    }

    public async Task SeekAsync(TimeSpan position)
    {
        if (CurrentTrack != null) await _audioPlayer.SeekAsync(position);
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
        await _settingsService.SaveRepeatModeAsync(CurrentRepeatMode);
        RepeatModeChanged?.Invoke();
        UpdateSmtcControls();
    }

    public Task AddToQueueAsync(Song song)
    {
        if (song == null || _playbackQueue.Contains(song)) return Task.CompletedTask;

        _playbackQueue.Add(song);
        if (IsShuffleEnabled)
            // To maintain a good shuffle experience, regenerate the queue.
            GenerateShuffledQueue();
        QueueChanged?.Invoke();
        UpdateSmtcControls();
        return Task.CompletedTask;
    }

    public Task AddRangeToQueueAsync(IEnumerable<Song> songs)
    {
        if (songs == null || !songs.Any()) return Task.CompletedTask;

        // Use a HashSet for an efficient check of existing songs to avoid duplicates.
        var currentQueueSet = new HashSet<Song>(_playbackQueue);
        var songsToAdd = songs.Where(s => currentQueueSet.Add(s)).ToList();

        if (songsToAdd.Any())
        {
            _playbackQueue.AddRange(songsToAdd);
            if (IsShuffleEnabled) GenerateShuffledQueue();
            QueueChanged?.Invoke();
            UpdateSmtcControls();
        }

        return Task.CompletedTask;
    }

    public Task PlayNextAsync(Song song)
    {
        if (song == null) return Task.CompletedTask;

        // Remove the song if it already exists to avoid duplicates.
        _playbackQueue.Remove(song);
        if (IsShuffleEnabled) _shuffledQueue.Remove(song);

        // Recalculate current index after potential removal.
        if (CurrentTrack != null) CurrentQueueIndex = _playbackQueue.IndexOf(CurrentTrack);

        // Insert the song after the current track.
        var insertIndex = CurrentQueueIndex == -1 ? 0 : CurrentQueueIndex + 1;
        _playbackQueue.Insert(insertIndex, song);

        if (IsShuffleEnabled)
        {
            if (CurrentTrack != null) _currentShuffledIndex = _shuffledQueue.IndexOf(CurrentTrack);
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

        _playbackQueue.RemoveAt(originalIndex);
        if (IsShuffleEnabled) _shuffledQueue.Remove(song);

        if (CurrentTrack == song)
        {
            // If the current song was removed, stop and play the next one.
            await _audioPlayer.StopAsync();
            CurrentTrack = null;
            await NextAsync();
        }
        else
        {
            // If another song was removed, update the current indices.
            if (CurrentTrack != null)
            {
                CurrentQueueIndex = _playbackQueue.IndexOf(CurrentTrack);
                if (IsShuffleEnabled) _currentShuffledIndex = _shuffledQueue.IndexOf(CurrentTrack);
            }

            QueueChanged?.Invoke();
            UpdateSmtcControls();
        }
    }

    public async Task ClearQueueAsync()
    {
        if (!_playbackQueue.Any()) return;
        await StopAsync();
        ClearQueuesInternal();
        QueueChanged?.Invoke();
        UpdateSmtcControls();
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

        if (IsShuffleEnabled) _currentShuffledIndex = _shuffledQueue.IndexOf(CurrentTrack);

        await _audioPlayer.LoadAsync(CurrentTrack);
        await _audioPlayer.PlayAsync();
        UpdateSmtcControls();
    }

    public async Task SavePlaybackStateAsync()
    {
        if (!_isInitialized) return;

        var state = new PlaybackState
        {
            CurrentTrackId = CurrentTrack?.Id,
            CurrentPositionSeconds = CurrentTrack != null ? _audioPlayer.CurrentPosition.TotalSeconds : 0,
            PlaybackQueueTrackIds = _playbackQueue.Select(s => s.Id).ToList(),
            CurrentPlaybackQueueIndex = CurrentQueueIndex,
            ShuffledQueueTrackIds = IsShuffleEnabled ? _shuffledQueue.Select(s => s.Id).ToList() : new List<Guid>(),
            CurrentShuffledQueueIndex = _currentShuffledIndex
        };
        await _settingsService.SavePlaybackStateAsync(state);
    }

    private void ClearQueuesInternal()
    {
        _playbackQueue.Clear();
        _shuffledQueue.Clear();
        CurrentTrack = null;
        CurrentQueueIndex = -1;
        _currentShuffledIndex = -1;
    }

    private async Task<bool> RestoreInternalPlaybackStateAsync(PlaybackState state)
    {
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

        if (IsShuffleEnabled)
        {
            var shuffledIds = state.ShuffledQueueTrackIds ?? Enumerable.Empty<Guid>();
            _shuffledQueue = shuffledIds
                .Select(id => songMap.GetValueOrDefault(id))
                .Where(s => s != null)
                .Cast<Song>()
                .ToList();
            // Validate and regenerate shuffled queue if it's inconsistent.
            if (_shuffledQueue.Count != _playbackQueue.Count) GenerateShuffledQueue();
        }

        if (state.CurrentTrackId.HasValue && songMap.TryGetValue(state.CurrentTrackId.Value, out var currentSong))
        {
            CurrentTrack = currentSong;
            CurrentQueueIndex = _playbackQueue.IndexOf(currentSong);
            if (IsShuffleEnabled) _currentShuffledIndex = _shuffledQueue.IndexOf(currentSong);

            await _audioPlayer.LoadAsync(CurrentTrack);
            // This delay is a workaround. It gives the audio player time to open the media
            // and report its duration, which is needed for an accurate seek.
            await Task.Delay(50);
            if (_audioPlayer.Duration > TimeSpan.Zero && state.CurrentPositionSeconds > 0)
                await _audioPlayer.SeekAsync(TimeSpan.FromSeconds(state.CurrentPositionSeconds));
        }
        else
        {
            CurrentQueueIndex = state.CurrentPlaybackQueueIndex;
            _currentShuffledIndex = state.CurrentShuffledQueueIndex;
        }

        return true;
    }

    private void GenerateShuffledQueue()
    {
        if (!_playbackQueue.Any())
        {
            _shuffledQueue.Clear();
            return;
        }

        // Fisher-Yates shuffle algorithm.
        _shuffledQueue = new List<Song>(_playbackQueue);
        var n = _shuffledQueue.Count;
        while (n > 1)
        {
            n--;
            var k = _random.Next(n + 1);
            (_shuffledQueue[k], _shuffledQueue[n]) = (_shuffledQueue[n], _shuffledQueue[k]);
        }
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
            else // Sequential
            {
                canGoNext = CurrentQueueIndex < _playbackQueue.Count - 1;
                canGoPrevious = CurrentQueueIndex > 0;
            }

            // Always allow going back to the start of the current track.
            if (CurrentTrack != null) canGoPrevious = true;
        }

        _audioPlayer.UpdateSmtcButtonStates(canGoNext, canGoPrevious);
    }

    private async void OnAudioPlayerPlaybackEnded()
    {
        await NextAsync();
    }

    private void OnAudioPlayerStateChanged()
    {
        PlaybackStateChanged?.Invoke();
        if (IsTransitioningTrack && _audioPlayer.IsPlaying) IsTransitioningTrack = false;
    }

    private void OnAudioPlayerVolumeChanged()
    {
        VolumeStateChanged?.Invoke();
    }

    private void OnAudioPlayerPositionChanged()
    {
        PositionChanged?.Invoke();
    }

    private async void OnAudioPlayerErrorOccurred(string errorMessage)
    {
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