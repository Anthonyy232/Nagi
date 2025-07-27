using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.WinUI.Navigation;
using Nagi.WinUI.Pages;
using Nagi.WinUI.Services.Abstractions;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Nagi.WinUI.ViewModels;

/// <summary>
/// Manages the state and interactions for the main media player UI.
/// This view model acts as a bridge between the UI and the background <see cref="IMusicPlaybackService"/>.
/// </summary>
public partial class PlayerViewModel : ObservableObject, IDisposable {
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

    private const string PlayTooltip = "Play";
    private const string PauseTooltip = "Pause";

    private const double VolumeLowThreshold = 33;
    private const double VolumeMediumThreshold = 66;

    private readonly IMusicPlaybackService _playbackService;
    private readonly INavigationService _navigationService;
    private readonly IDispatcherService _dispatcherService;

    private bool _isUpdatingFromService;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayerViewModel"/> class.
    /// </summary>
    /// <param name="playbackService">The core service for managing playback.</param>
    /// <param name="navigationService">The service for navigating between pages.</param>
    /// <param name="dispatcherService">The service for dispatching actions to the UI thread.</param>
    public PlayerViewModel(IMusicPlaybackService playbackService, INavigationService navigationService, IDispatcherService dispatcherService) {
        _playbackService = playbackService ?? throw new ArgumentNullException(nameof(playbackService));
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        _dispatcherService = dispatcherService ?? throw new ArgumentNullException(nameof(dispatcherService));

        Debug.WriteLine("[PlayerViewModel] INFO: Initializing and subscribing to playback service events.");
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
    [NotifyPropertyChangedFor(nameof(IsArtworkAvailable))]
    private string? _albumArtUri;

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

    [ObservableProperty]
    private bool _isGlobalOperationIndeterminate;

    [ObservableProperty]
    private bool _isQueueViewVisible;

    public bool IsArtworkAvailable => !string.IsNullOrWhiteSpace(AlbumArtUri);

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

    /// <summary>
    /// Cleans up resources by unsubscribing from service events to prevent memory leaks.
    /// </summary>
    public void Dispose() {
        Debug.WriteLine("[PlayerViewModel] INFO: Disposing and unsubscribing from playback service events.");
        UnsubscribeFromPlaybackServiceEvents();
        GC.SuppressFinalize(this);
    }

    [RelayCommand]
    private void ShowQueueView() => IsQueueViewVisible = true;

    [RelayCommand]
    private void ShowPlayerView() => IsQueueViewVisible = false;

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

    [RelayCommand(CanExecute = nameof(CanGoToArtist))]
    private void GoToArtist(Song? song) {
        var targetSong = song ?? CurrentPlayingTrack;
        if (targetSong?.ArtistId == null || targetSong.Artist == null) {
            Debug.WriteLine("[PlayerViewModel] WARN: Attempted to navigate to Artist, but target song or artist was null/invalid.");
            return;
        }

        var navParam = new ArtistViewNavigationParameter {
            ArtistId = targetSong.Artist.Id,
            ArtistName = targetSong.Artist.Name
        };
        Debug.WriteLine($"[PlayerViewModel] INFO: Navigating to Artist: Name='{navParam.ArtistName}', ID='{navParam.ArtistId}'");
        _navigationService.Navigate(typeof(ArtistViewPage), navParam);
    }

    private bool CanGoToArtist(Song? song) {
        var targetSong = song ?? CurrentPlayingTrack;
        return targetSong?.Artist != null && targetSong.Artist.Id != Guid.Empty;
    }

    partial void OnIsMutedChanged(bool value) => UpdateVolumeIconGlyph();

    partial void OnCurrentVolumeChanged(double value) {
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

    #region Playback Service Event Handling

    private void SubscribeToPlaybackServiceEvents() {
        _playbackService.PlaybackStateChanged += OnPlaybackService_PlaybackStateChanged;
        _playbackService.TrackChanged += OnPlaybackService_TrackChanged;
        _playbackService.VolumeStateChanged += OnPlaybackService_VolumeStateChanged;
        _playbackService.ShuffleModeChanged += OnPlaybackService_ShuffleModeChanged;
        _playbackService.RepeatModeChanged += OnPlaybackService_RepeatModeChanged;
        _playbackService.QueueChanged += OnPlaybackService_QueueChanged;
        _playbackService.PositionChanged += OnPlaybackService_PositionChanged;
        _playbackService.DurationChanged += OnPlaybackService_DurationChanged;
    }

    private void UnsubscribeFromPlaybackServiceEvents() {
        _playbackService.PlaybackStateChanged -= OnPlaybackService_PlaybackStateChanged;
        _playbackService.TrackChanged -= OnPlaybackService_TrackChanged;
        _playbackService.VolumeStateChanged -= OnPlaybackService_VolumeStateChanged;
        _playbackService.ShuffleModeChanged -= OnPlaybackService_ShuffleModeChanged;
        _playbackService.RepeatModeChanged -= OnPlaybackService_RepeatModeChanged;
        _playbackService.QueueChanged -= OnPlaybackService_QueueChanged;
        _playbackService.PositionChanged -= OnPlaybackService_PositionChanged;
        _playbackService.DurationChanged -= OnPlaybackService_DurationChanged;
    }

    private void InitializeStateFromService() {
        Debug.WriteLine("[PlayerViewModel] INFO: Synchronizing initial state from playback service.");
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
            var newTrack = _playbackService.CurrentTrack;
            UpdateTrackDetails(newTrack);

            if (newTrack == null) {
                Debug.WriteLine("[PlayerViewModel] INFO: Playback stopped. Resetting duration and position to 0.");
                TotalDuration = 0;
                CurrentPosition = 0;
            }
            else {
                CurrentPosition = 0;
            }

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
            if (!IsUserDraggingSlider) {
                CurrentPosition = _playbackService.CurrentPosition.TotalSeconds;
            }
        });
    }

    private void OnPlaybackService_DurationChanged() {
        RunOnUIThread(() => {
            Debug.WriteLine($"[PlayerViewModel] INFO: DurationChanged event received. Updating TotalDuration.");
            TotalDuration = Math.Max(0, _playbackService.Duration.TotalSeconds);
        });
    }

    #endregion

    private void UpdateTrackDetails(Song? song) {
        CurrentPlayingTrack = song;
        if (song != null) {
            SongTitle = song.Title;
            ArtistName = song.Artist?.Name ?? string.Empty;
            AlbumArtUri = song.AlbumArtUriFromTrack;
            Debug.WriteLine($"[PlayerViewModel] INFO: Track details updated to '{song.Title}'.");
        }
        else {
            SongTitle = "No track playing";
            ArtistName = string.Empty;
            AlbumArtUri = null;
            Debug.WriteLine("[PlayerViewModel] INFO: Playback stopped, clearing track details.");
        }
        GoToArtistCommand.NotifyCanExecuteChanged();
    }

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
                newDisplayQueue.AddRange(sourceQueueList.Skip(currentTrackIndex));
                newDisplayQueue.AddRange(sourceQueueList.Take(currentTrackIndex));
            }
            else {
                newDisplayQueue.AddRange(sourceQueueList);
            }
        }
        else {
            newDisplayQueue.AddRange(sourceQueue);
        }

        if (!CurrentQueue.SequenceEqual(newDisplayQueue)) {
            Debug.WriteLine("[PlayerViewModel] INFO: Updating queue display.");
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

    private void RunOnUIThread(Action action) {
        _dispatcherService.TryEnqueue(() => {
            using (new ServiceUpdateScope(this)) {
                action();
            }
        });
    }

    /// <summary>
    /// A helper struct to prevent property-changed event feedback loops.
    /// It sets a flag on the view model upon creation and unsets it upon disposal,
    /// allowing code within a 'using' block to be identified as originating from the background service.
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