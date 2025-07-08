using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Nagi.Models;
using Nagi.Services.Abstractions;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Nagi.ViewModels;

/// <summary>
/// Manages state and logic for the main media player controls and queue display.
/// </summary>
public partial class PlayerViewModel : ObservableObject, IDisposable {
    // Constants for UI Glyphs
    private const string PlayIconGlyph = "\uE768";
    private const string PauseIconGlyph = "\uE769";
    private const string RepeatOffIconGlyph = "\uE8EE";
    private const string RepeatAllIconGlyph = "\uE895";
    private const string RepeatOneIconGlyph = "\uE8ED";
    private const string ShuffleOffIconGlyph = "\uE8B1";
    private const string ShuffleOnIconGlyph = "\uE148";
    private const string MuteIconGlyph = "\uE74F";
    private const string VolumeLowIconGlyph = "\uE993";
    private const string VolumeMediumIconGlyph = "\uE994";
    private const string VolumeHighIconGlyph = "\uE767";

    // Constants for UI ToolTips
    private const string PlayTooltip = "Play";
    private const string PauseTooltip = "Pause";

    // Constants for Volume Thresholds
    private const double VolumeLowThreshold = 33;
    private const double VolumeMediumThreshold = 66;

    private readonly IMusicPlaybackService _playbackService;
    private readonly DispatcherQueue _dispatcherQueue;

    // This flag prevents re-entrant property updates when the ViewModel's state
    // is being updated from the playback service.
    private bool _isUpdatingFromService;

    public PlayerViewModel(IMusicPlaybackService playbackService) {
        _playbackService = playbackService ?? throw new ArgumentNullException(nameof(playbackService));
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        SubscribeToPlaybackServiceEvents();
        InitializeStateFromService();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseIconGlyph))]
    [NotifyPropertyChangedFor(nameof(PlayPauseButtonToolTip))]
    private bool _isPlaying;

    [ObservableProperty]
    private string _songTitle = "No track playing";

    [ObservableProperty]
    private string _artistName = string.Empty;

    [ObservableProperty]
    private ImageSource? _albumArtSource;

    [ObservableProperty]
    private Song? _currentPlayingTrack;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShuffleIconGlyph))]
    [NotifyPropertyChangedFor(nameof(ShuffleButtonToolTip))]
    private bool _isShuffleEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepeatIconGlyph))]
    [NotifyPropertyChangedFor(nameof(RepeatButtonToolTip))]
    private RepeatMode _currentRepeatMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeButtonToolTip))]
    private bool _isMuted;

    [ObservableProperty]
    private double _currentVolume = 50;

    [ObservableProperty]
    private string _volumeIconGlyph = VolumeMediumIconGlyph;

    [ObservableProperty]
    private ObservableCollection<Song> _currentQueue = new();

    [ObservableProperty]
    private double _currentPosition;

    [ObservableProperty]
    private string _currentTimeText = "0:00";

    [ObservableProperty]
    private bool _isUserDraggingSlider;

    [ObservableProperty]
    private double _totalDuration;

    [ObservableProperty]
    private string _totalDurationText = "0:00";

    [ObservableProperty]
    private bool _isGlobalOperationInProgress;

    [ObservableProperty]
    private string _globalOperationStatusMessage = string.Empty;

    [ObservableProperty]
    private double _globalOperationProgressValue;

    // Computed properties for UI bindings
    public string PlayPauseIconGlyph => IsPlaying ? PauseIconGlyph : PlayIconGlyph;
    public string PlayPauseButtonToolTip => IsPlaying ? PauseTooltip : PlayTooltip;
    public string ShuffleIconGlyph => IsShuffleEnabled ? ShuffleOnIconGlyph : ShuffleOffIconGlyph;
    public string ShuffleButtonToolTip => IsShuffleEnabled ? "Shuffle On" : "Shuffle Off";
    public string RepeatIconGlyph => CurrentRepeatMode switch {
        RepeatMode.RepeatAll => RepeatAllIconGlyph,
        RepeatMode.RepeatOne => RepeatOneIconGlyph,
        _ => RepeatOffIconGlyph
    };
    public string RepeatButtonToolTip => CurrentRepeatMode switch {
        RepeatMode.Off => "Repeat Off",
        RepeatMode.RepeatAll => "Repeat All",
        RepeatMode.RepeatOne => "Repeat One",
        _ => "Repeat"
    };
    public string VolumeButtonToolTip => IsMuted ? "Unmute" : "Mute";

    [ObservableProperty]
    private bool _isQueueViewVisible;

    [RelayCommand]
    private void ShowQueueView() => IsQueueViewVisible = true;

    [RelayCommand]
    private void ShowPlayerView() => IsQueueViewVisible = false;

    public void Dispose() {
        UnsubscribeFromPlaybackServiceEvents();
        GC.SuppressFinalize(this);
    }

    [RelayCommand]
    private Task PlayPauseAsync() => _playbackService.PlayPauseAsync();

    [RelayCommand]
    private Task PreviousAsync() => _playbackService.PreviousAsync();

    [RelayCommand]
    private Task NextAsync() => _playbackService.NextAsync();

    [RelayCommand]
    private Task ToggleShuffleAsync() => _playbackService.SetShuffleAsync(!_playbackService.IsShuffleEnabled);

    [RelayCommand]
    private Task CycleRepeatAsync() {
        var nextMode = _playbackService.CurrentRepeatMode switch {
            RepeatMode.Off => RepeatMode.RepeatAll,
            RepeatMode.RepeatAll => RepeatMode.RepeatOne,
            _ => RepeatMode.Off
        };
        return _playbackService.SetRepeatModeAsync(nextMode);
    }

    [RelayCommand]
    private Task ToggleMuteAsync() => _playbackService.ToggleMuteAsync();

    [RelayCommand]
    private Task SeekAsync(double position) => _playbackService.SeekAsync(TimeSpan.FromSeconds(position));

    partial void OnIsMutedChanged(bool value) => UpdateVolumeIconGlyph();

    partial void OnCurrentVolumeChanged(double value) {
        // Only update the service if the change originated from the UI, not from the service itself.
        if (!_isUpdatingFromService) {
            var serviceVolume = Math.Clamp(value / 100.0, 0.0, 1.0);
            if (Math.Abs(_playbackService.Volume - serviceVolume) > 0.001) {
                _ = _playbackService.SetVolumeAsync(serviceVolume);
            }
        }
        UpdateVolumeIconGlyph();
    }

    partial void OnCurrentPositionChanged(double value) {
        CurrentTimeText = TimeSpan.FromSeconds(value).ToString(@"m\:ss");
        // Only seek if the user is not dragging the slider and the change is significant.
        if (!_isUpdatingFromService && !IsUserDraggingSlider) {
            var newPosition = TimeSpan.FromSeconds(value);
            if (Math.Abs(_playbackService.CurrentPosition.TotalSeconds - newPosition.TotalSeconds) > 0.5) {
                _ = _playbackService.SeekAsync(newPosition);
            }
        }
    }

    partial void OnTotalDurationChanged(double value) {
        TotalDurationText = TimeSpan.FromSeconds(value).ToString(@"m\:ss");
    }

    private void SubscribeToPlaybackServiceEvents() {
        _playbackService.PlaybackStateChanged += OnPlaybackService_PlaybackStateChanged;
        _playbackService.TrackChanged += OnPlaybackService_TrackChanged;
        _playbackService.VolumeStateChanged += OnPlaybackService_VolumeStateChanged;
        _playbackService.ShuffleModeChanged += OnPlaybackService_ShuffleModeChanged;
        _playbackService.RepeatModeChanged += OnPlaybackService_RepeatModeChanged;
        _playbackService.QueueChanged += OnPlaybackService_QueueChanged;
        _playbackService.PositionChanged += OnPlaybackService_PositionChanged;
    }

    private void UnsubscribeFromPlaybackServiceEvents() {
        _playbackService.PlaybackStateChanged -= OnPlaybackService_PlaybackStateChanged;
        _playbackService.TrackChanged -= OnPlaybackService_TrackChanged;
        _playbackService.VolumeStateChanged -= OnPlaybackService_VolumeStateChanged;
        _playbackService.ShuffleModeChanged -= OnPlaybackService_ShuffleModeChanged;
        _playbackService.RepeatModeChanged -= OnPlaybackService_RepeatModeChanged;
        _playbackService.QueueChanged -= OnPlaybackService_QueueChanged;
        _playbackService.PositionChanged -= OnPlaybackService_PositionChanged;
    }

    // Populates the ViewModel with the current state from the playback service.
    private void InitializeStateFromService() {
        RunOnUIThread(() => {
            IsPlaying = _playbackService.IsPlaying;
            UpdateTrackDetails(_playbackService.CurrentTrack);
            IsMuted = _playbackService.IsMuted;
            CurrentVolume = Math.Clamp(_playbackService.Volume * 100.0, 0.0, 100.0);
            IsShuffleEnabled = _playbackService.IsShuffleEnabled;
            CurrentRepeatMode = _playbackService.CurrentRepeatMode;
            TotalDuration = Math.Max(0, _playbackService.Duration.TotalSeconds);
            CurrentPosition = _playbackService.CurrentPosition.TotalSeconds;
            UpdateCurrentQueueDisplay();
        });
    }

    private void OnPlaybackService_PlaybackStateChanged() {
        RunOnUIThread(() => {
            if (!_playbackService.IsTransitioningTrack) IsPlaying = _playbackService.IsPlaying;
        });
    }

    private void OnPlaybackService_TrackChanged() {
        RunOnUIThread(() => {
            UpdateTrackDetails(_playbackService.CurrentTrack);
            TotalDuration = Math.Max(0, _playbackService.Duration.TotalSeconds);
            CurrentPosition = _playbackService.CurrentPosition.TotalSeconds;
            UpdateCurrentQueueDisplay();
        });
    }

    private void OnPlaybackService_VolumeStateChanged() {
        RunOnUIThread(() => {
            IsMuted = _playbackService.IsMuted;
            CurrentVolume = Math.Clamp(_playbackService.Volume * 100.0, 0.0, 100.0);
        });
    }

    private void OnPlaybackService_ShuffleModeChanged() {
        RunOnUIThread(() => {
            IsShuffleEnabled = _playbackService.IsShuffleEnabled;
            UpdateCurrentQueueDisplay();
        });
    }

    private void OnPlaybackService_RepeatModeChanged() {
        RunOnUIThread(() => CurrentRepeatMode = _playbackService.CurrentRepeatMode);
    }

    private void OnPlaybackService_QueueChanged() {
        RunOnUIThread(UpdateCurrentQueueDisplay);
    }



    private void OnPlaybackService_PositionChanged() {
        RunOnUIThread(() => {
            if (!IsUserDraggingSlider) CurrentPosition = _playbackService.CurrentPosition.TotalSeconds;
        });
    }

    private void UpdateTrackDetails(Song? song) {
        CurrentPlayingTrack = song;
        if (song != null) {
            SongTitle = song.Title;
            ArtistName = song.Artist?.Name ?? string.Empty;
            try {
                AlbumArtSource = !string.IsNullOrEmpty(song.AlbumArtUriFromTrack)
                    ? new BitmapImage(new Uri(song.AlbumArtUriFromTrack))
                    : null;
            }
            catch (Exception ex) {
                Debug.WriteLine($"[{nameof(PlayerViewModel)}] Error loading album art '{song.AlbumArtUriFromTrack}': {ex.Message}");
                AlbumArtSource = null;
            }
        }
        else {
            SongTitle = "No track playing";
            ArtistName = string.Empty;
            AlbumArtSource = null;
        }
    }

    /// <summary>
    /// Updates the observable collection for the queue view.
    /// The queue is rotated so that the currently playing track is always at the top.
    /// </summary>
    private void UpdateCurrentQueueDisplay() {
        var sourceQueue = _playbackService.IsShuffleEnabled
            ? _playbackService.ShuffledQueue
            : _playbackService.PlaybackQueue;

        var newDisplayQueue = new List<Song>();
        var currentTrack = _playbackService.CurrentTrack;

        if (currentTrack != null) {
            var sourceQueueList = sourceQueue.ToList();
            var currentTrackIndex = sourceQueueList.FindIndex(s => s.Id == currentTrack.Id);

            if (currentTrackIndex != -1) {
                // Add tracks from the current one to the end.
                newDisplayQueue.AddRange(sourceQueueList.Skip(currentTrackIndex));
                // Wrap around and add tracks from the beginning.
                newDisplayQueue.AddRange(sourceQueueList.Take(currentTrackIndex));
            }
            else {
                newDisplayQueue.AddRange(sourceQueueList);
            }
        }
        else {
            newDisplayQueue.AddRange(sourceQueue);
        }

        // Only update the collection if the content has actually changed.
        if (!CurrentQueue.SequenceEqual(newDisplayQueue)) {
            CurrentQueue.Clear();
            foreach (var song in newDisplayQueue) CurrentQueue.Add(song);
        }
    }

    private void UpdateVolumeIconGlyph() {
        var newGlyph = IsMuted || CurrentVolume == 0
            ? MuteIconGlyph
            : CurrentVolume switch {
                <= VolumeLowThreshold => VolumeLowIconGlyph,
                <= VolumeMediumThreshold => VolumeMediumIconGlyph,
                _ => VolumeHighIconGlyph
            };

        if (VolumeIconGlyph != newGlyph) VolumeIconGlyph = newGlyph;
    }

    /// <summary>
    /// Safely executes an action on the UI thread, wrapping it in a scope
    /// that prevents property change feedback loops.
    /// </summary>
    private void RunOnUIThread(Action action) {
        _dispatcherQueue.TryEnqueue(() => {
            using (new ServiceUpdateScope(this)) {
                action();
            }
        });
    }

    /// <summary>
    /// A helper struct to temporarily set a flag that indicates the ViewModel
    /// is being updated from the backing service, not by user interaction.
    /// This prevents feedback loops where a UI update would trigger a command.
    /// </summary>
    private readonly struct ServiceUpdateScope : IDisposable {
        private readonly PlayerViewModel _viewModel;

        public ServiceUpdateScope(PlayerViewModel viewModel) {
            _viewModel = viewModel;
            _viewModel._isUpdatingFromService = true;
        }

        public void Dispose() {
            _viewModel._isUpdatingFromService = false;
        }
    }
}