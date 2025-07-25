using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.WinUI.Navigation;
using Nagi.WinUI.Pages;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.ViewModels;

/// <summary>
/// Manages the state and interactions for the main media player UI.
/// Acts as a bridge between the UI and the background <see cref="IMusicPlaybackService"/>.
/// </summary>
public partial class PlayerViewModel : ObservableObject, IDisposable {
    // Glyphs for UI icons
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

    // Prevents feedback loops when properties are updated by the service.
    private bool _isUpdatingFromService;

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
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    public partial string SongTitle { get; set; } = "No track playing";

    [ObservableProperty]
    public partial string ArtistName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsArtworkAvailable))]
    public partial string? AlbumArtUri { get; set; }

    [ObservableProperty]
    public partial Song? CurrentPlayingTrack { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShuffleIconGlyph))]
    [NotifyPropertyChangedFor(nameof(ShuffleButtonToolTip))]
    public partial bool IsShuffleEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepeatIconGlyph))]
    [NotifyPropertyChangedFor(nameof(RepeatButtonToolTip))]
    public partial RepeatMode CurrentRepeatMode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeButtonToolTip))]
    public partial bool IsMuted { get; set; }

    [ObservableProperty]
    public partial double CurrentVolume { get; set; } = 50;

    [ObservableProperty]
    public partial string VolumeIconGlyph { get; set; } = VolumeMediumIconGlyph;

    [ObservableProperty]
    public partial ObservableCollection<Song> CurrentQueue { get; set; } = new();

    [ObservableProperty]
    public partial double CurrentPosition { get; set; }

    [ObservableProperty]
    public partial string CurrentTimeText { get; set; } = "0:00";

    [ObservableProperty]
    public partial bool IsUserDraggingSlider { get; set; }

    [ObservableProperty]
    public partial double TotalDuration { get; set; }

    [ObservableProperty]
    public partial string TotalDurationText { get; set; } = "0:00";

    [ObservableProperty]
    public partial bool IsGlobalOperationInProgress { get; set; }

    [ObservableProperty]
    public partial string GlobalOperationStatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double GlobalOperationProgressValue { get; set; }

    [ObservableProperty]
    public partial bool IsGlobalOperationIndeterminate { get; set; }

    [ObservableProperty]
    public partial bool IsQueueViewVisible { get; set; }

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

    // Commands that simply proxy calls to the playback service.
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
        // This check prevents an infinite loop: UI changes volume -> VM updates service -> service event fires -> VM updates UI.
        if (!_isUpdatingFromService) {
            var serviceVolume = Math.Clamp(value / 100.0, 0.0, 1.0);
            // Only update the service if the change is significant, to avoid flooding with small adjustments.
            if (Math.Abs(_playbackService.Volume - serviceVolume) > 0.001) {
                _ = _playbackService.SetVolumeAsync(serviceVolume);
            }
        }
        UpdateVolumeIconGlyph();
    }

    partial void OnCurrentPositionChanged(double value) {
        CurrentTimeText = TimeSpan.FromSeconds(value).ToString(@"m\:ss");
        // Avoid seeking while the user is dragging the slider or when the update comes from the service itself.
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
    // These methods bridge the background playback service to the UI thread, ensuring thread-safe updates.

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
            // This check prevents a brief visual flicker of the pause icon between tracks.
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
            // Only update the slider position if the user isn't currently dragging it.
            if (!IsUserDraggingSlider) CurrentPosition = _playbackService.CurrentPosition.TotalSeconds;
        });
    }

    #endregion

    private void UpdateTrackDetails(Song? song) {
        CurrentPlayingTrack = song;
        if (song != null) {
            SongTitle = song.Title;
            ArtistName = song.Artist?.Name ?? string.Empty;
            AlbumArtUri = song.AlbumArtUriFromTrack;
            Debug.WriteLine($"[PlayerViewModel] INFO: Track changed to '{song.Title}'.");
        }
        else {
            SongTitle = "No track playing";
            ArtistName = string.Empty;
            AlbumArtUri = null;
            Debug.WriteLine("[PlayerViewModel] INFO: Playback stopped, clearing track details.");
        }
        GoToArtistCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Updates the displayed queue, reordering it to show the current track at the top.
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

            // This logic reorders the list so the current track is first, followed by the rest of the queue.
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

        // This check is a performance optimization to avoid clearing and re-adding all items if the queue hasn't changed.
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
    /// A helper to ensure state updates from the service are run on the UI thread
    /// and are wrapped in a scope that prevents update cycles.
    /// </summary>
    private void RunOnUIThread(Action action) {
        _dispatcherService.TryEnqueue(() => {
            using (new ServiceUpdateScope(this)) {
                action();
            }
        });
    }

    /// <summary>
    /// A disposable struct to temporarily flag that property changes are originating
    /// from the background service, not from user interaction.
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