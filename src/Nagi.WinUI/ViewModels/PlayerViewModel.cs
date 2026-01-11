using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.WinUI.Models;
using Nagi.WinUI.Navigation;
using Nagi.WinUI.Pages;
using Nagi.WinUI.Services.Abstractions;
using Nagi.WinUI.Helpers;

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     Manages the state and interactions for the main media player UI. This view model acts as a coordinator
///     between the UI and various services like <see cref="IMusicPlaybackService" /> and <see cref="IWindowService" />.
/// </summary>
public partial class PlayerViewModel : ObservableObject, IDisposable
{
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
    private readonly IDispatcherService _dispatcherService;
    private readonly ILogger<PlayerViewModel> _logger;
    private readonly INavigationService _navigationService;

    private readonly IMusicPlaybackService _playbackService;
    private readonly IUISettingsService _settingsService;
    private readonly IWindowService _windowService;

    private bool _isUpdatingFromService;
    private bool _isDisposed;
    private bool _isEfficiencyModeEnabled;

    // Position update throttling
    private double _lastReportedPosition;
    private int _lastDisplayedSecond = -1;
    private const double PositionThrottleSeconds = 0.1; // 100ms

    public PlayerViewModel(IMusicPlaybackService playbackService, INavigationService navigationService,
        IDispatcherService dispatcherService, IUISettingsService settingsService, IWindowService windowService,
        ILogger<PlayerViewModel> logger)
    {
        _playbackService = playbackService ?? throw new ArgumentNullException(nameof(playbackService));
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        _dispatcherService = dispatcherService ?? throw new ArgumentNullException(nameof(dispatcherService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _windowService = windowService ?? throw new ArgumentNullException(nameof(windowService));
        _logger = logger;

        // Initialize properties with default values
        SongTitle = "No track playing";
        ArtistName = string.Empty;
        VolumeIconGlyph = VolumeMediumIconGlyph;
        CurrentQueue = new ObservableCollection<Song>();
        CurrentTimeText = "0:00";
        TotalDurationText = "0:00";
        GlobalOperationStatusMessage = string.Empty;

        SubscribeToPlaybackServiceEvents();
        SubscribeToSettingsServiceEvents();
        SubscribeToWindowServiceEvents();
        InitializeStateFromService();
        _ = InitializeSettingsAsync();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseIconGlyph))]
    [NotifyPropertyChangedFor(nameof(PlayPauseButtonToolTip))]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty] public partial string SongTitle { get; set; }

    [ObservableProperty] public partial string ArtistName { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsArtworkAvailable))]
    public partial string? AlbumArtUri { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoToArtistCommand))]
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

    [ObservableProperty] public partial double CurrentVolume { get; set; }
    [ObservableProperty] public partial string VolumeIconGlyph { get; set; }
    [ObservableProperty] public partial ObservableCollection<Song> CurrentQueue { get; set; }
    [ObservableProperty] public partial double CurrentPosition { get; set; }
    [ObservableProperty] public partial string CurrentTimeText { get; set; }
    [ObservableProperty] public partial bool IsUserDraggingSlider { get; set; }
    [ObservableProperty] public partial double TotalDuration { get; set; }
    [ObservableProperty] public partial string TotalDurationText { get; set; }
    [ObservableProperty] public partial bool IsGlobalOperationInProgress { get; set; }
    [ObservableProperty] public partial string GlobalOperationStatusMessage { get; set; }
    [ObservableProperty] public partial double GlobalOperationProgressValue { get; set; }
    [ObservableProperty] public partial bool IsGlobalOperationIndeterminate { get; set; }
    [ObservableProperty] public partial bool IsQueueViewVisible { get; set; }

    [ObservableProperty] public partial bool IsVolumeControlVisible { get; set; }

    public ObservableCollection<PlayerButtonSetting> MainTransportButtons { get; } = new();
    public ObservableCollection<PlayerButtonSetting> SecondaryControlsButtons { get; } = new();

    public bool IsArtworkAvailable => !string.IsNullOrWhiteSpace(AlbumArtUri);
    public string PlayPauseIconGlyph => IsPlaying ? PauseIconGlyph : PlayIconGlyph;
    public string PlayPauseButtonToolTip => IsPlaying ? PauseTooltip : PlayTooltip;
    public string ShuffleIconGlyph => IsShuffleEnabled ? ShuffleOnIconGlyph : ShuffleOffIconGlyph;
    public string ShuffleButtonToolTip => IsShuffleEnabled ? "Shuffle On" : "Shuffle Off";

    public string RepeatIconGlyph => CurrentRepeatMode switch
    {
        RepeatMode.RepeatAll => RepeatAllIconGlyph,
        RepeatMode.RepeatOne => RepeatOneIconGlyph,
        _ => RepeatOffIconGlyph
    };

    public string RepeatButtonToolTip => CurrentRepeatMode switch
    {
        RepeatMode.Off => "Repeat Off",
        RepeatMode.RepeatAll => "Repeat All",
        RepeatMode.RepeatOne => "Repeat One",
        _ => "Repeat"
    };

    public string VolumeButtonToolTip => IsMuted ? "Unmute" : "Mute";

    /// <summary>
    ///     Cleans up resources and unsubscribes from service events.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _logger.LogDebug("Disposing and unsubscribing from service events");
        UnsubscribeFromPlaybackServiceEvents();
        UnsubscribeFromSettingsServiceEvents();
        UnsubscribeFromWindowServiceEvents();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Loads player button settings and splits them into main and secondary controls
    ///     based on a special "Separator" item.
    /// </summary>
    private async Task LoadPlayerButtonSettingsAsync()
    {
        var allButtons = await _settingsService.GetPlayerButtonSettingsAsync();

        var enabledButtons = allButtons.Where(s => s.IsEnabled).ToList();

        IsVolumeControlVisible = enabledButtons.Any(b => b.Id == "Volume");

        var separatorIndex = enabledButtons.FindIndex(b => b.Id == "Separator");

        var mainButtons = new List<PlayerButtonSetting>();
        var secondaryButtons = new List<PlayerButtonSetting>();

        if (separatorIndex != -1)
        {
            // Split the list based on the separator's position.
            mainButtons.AddRange(enabledButtons.Take(separatorIndex));
            secondaryButtons.AddRange(enabledButtons.Skip(separatorIndex + 1));
        }
        else
        {
            // Fallback: If no separator is found, place all buttons in the main transport area.
            mainButtons.AddRange(enabledButtons.Where(b => b.Id != "Separator"));
        }

        UpdateCollectionIfChanged(MainTransportButtons, mainButtons);
        UpdateCollectionIfChanged(SecondaryControlsButtons, secondaryButtons);
    }

    /// <summary>
    ///     Efficiently updates an ObservableCollection by comparing its current items
    ///     with a new list, preventing unnecessary UI refreshes if they are identical.
    /// </summary>
    private static void UpdateCollectionIfChanged(ObservableCollection<PlayerButtonSetting> collection,
        List<PlayerButtonSetting> newItems)
    {
        // Fast path: check if counts differ
        if (collection.Count != newItems.Count)
        {
            collection.Clear();
            foreach (var item in newItems) collection.Add(item);
            return;
        }
        
        // Check if all IDs match by index (avoids LINQ allocations)
        for (var i = 0; i < collection.Count; i++)
        {
            if (collection[i].Id != newItems[i].Id)
            {
                collection.Clear();
                foreach (var item in newItems) collection.Add(item);
                return;
            }
        }
    }

    [RelayCommand]
    private void ShowQueueView()
    {
        IsQueueViewVisible = true;
    }

    [RelayCommand]
    private void ShowPlayerView()
    {
        IsQueueViewVisible = false;
    }

    [RelayCommand]
    private Task PlayPauseAsync()
    {
        return _playbackService.PlayPauseAsync();
    }

    [RelayCommand]
    private Task PreviousAsync()
    {
        return _playbackService.PreviousAsync();
    }

    [RelayCommand]
    private Task NextAsync()
    {
        return _playbackService.NextAsync();
    }

    [RelayCommand]
    private Task ToggleShuffleAsync()
    {
        return _playbackService.SetShuffleAsync(!_playbackService.IsShuffleEnabled);
    }

    [RelayCommand]
    private Task CycleRepeatAsync()
    {
        var nextMode = _playbackService.CurrentRepeatMode switch
        {
            RepeatMode.Off => RepeatMode.RepeatAll,
            RepeatMode.RepeatAll => RepeatMode.RepeatOne,
            _ => RepeatMode.Off
        };
        return _playbackService.SetRepeatModeAsync(nextMode);
    }

    [RelayCommand]
    private Task ToggleMuteAsync()
    {
        return _playbackService.ToggleMuteAsync();
    }

    [RelayCommand]
    private Task SeekAsync(double position)
    {
        return _playbackService.SeekAsync(TimeSpan.FromSeconds(position));
    }

    [RelayCommand(CanExecute = nameof(CanGoToArtist))]
    private void GoToArtist(Song? song)
    {
        var targetSong = song ?? CurrentPlayingTrack;
        if (targetSong?.ArtistId == null || targetSong.Artist == null)
        {
            _logger.LogWarning("Cannot navigate to Artist page: artist information is missing for song {SongId}",
                targetSong?.Id);
            return;
        }

        var navParam = new ArtistViewNavigationParameter
            { ArtistId = targetSong.Artist.Id, ArtistName = targetSong.Artist.Name };
        _navigationService.Navigate(typeof(ArtistViewPage), navParam);
    }

    private bool CanGoToArtist(Song? song)
    {
        var targetSong = song ?? CurrentPlayingTrack;
        return targetSong?.Artist != null && targetSong.Artist.Id != Guid.Empty;
    }

    [RelayCommand]
    private void GoToLyricsPage()
    {
        _navigationService.Navigate(typeof(LyricsPage));
    }

    partial void OnIsMutedChanged(bool value)
    {
        UpdateVolumeIconGlyph();
    }

    partial void OnCurrentVolumeChanged(double value)
    {
        if (_isUpdatingFromService) return;
        var serviceVolume = Math.Clamp(value / 100.0, 0.0, 1.0);
        if (Math.Abs(_playbackService.Volume - serviceVolume) > 0.001)
            _ = _playbackService.SetVolumeAsync(serviceVolume);
        UpdateVolumeIconGlyph();
    }

    partial void OnCurrentPositionChanged(double value)
    {
        // Only update time text when the displayed second changes
        var currentSecond = (int)value;
        if (currentSecond != _lastDisplayedSecond)
        {
            _lastDisplayedSecond = currentSecond;
            var timeSpan = TimeSpan.FromSeconds(value);
            CurrentTimeText = timeSpan.TotalHours >= 1 
                ? timeSpan.ToString(@"h\:mm\:ss") 
                : timeSpan.ToString(@"m\:ss");
        }

        if (_isUpdatingFromService || IsUserDraggingSlider) return;

        var newPosition = TimeSpan.FromSeconds(value);
        if (Math.Abs(_playbackService.CurrentPosition.TotalSeconds - newPosition.TotalSeconds) > 0.5)
            _ = _playbackService.SeekAsync(newPosition);
    }

    partial void OnIsUserDraggingSliderChanged(bool value)
    {
        // When the user finishes dragging, perform the final seek.
        if (!value && !_isUpdatingFromService)
        {
            var newPosition = TimeSpan.FromSeconds(CurrentPosition);
            // Seek only if the position is meaningfully different from the player's known position.
            if (Math.Abs(_playbackService.CurrentPosition.TotalSeconds - newPosition.TotalSeconds) > 0.1)
            {
                _ = _playbackService.SeekAsync(newPosition);
            }
        }
    }

    partial void OnTotalDurationChanged(double value)
    {
        var timeSpan = TimeSpan.FromSeconds(value);
        TotalDurationText = timeSpan.TotalHours >= 1 
            ? timeSpan.ToString(@"h\:mm\:ss") 
            : timeSpan.ToString(@"m\:ss");
    }

    private void UpdateEfficiencyMode()
    {
        // Snapshot state to ensure consistent evaluation (thread safety).
        var (isVisible, isMiniPlayerActive, isMinimized, isPlaying) =
            (_windowService.IsVisible, _windowService.IsMiniPlayerActive,
             _windowService.IsMinimized, _playbackService.IsPlaying);

        var isInBackgroundState = !isVisible || isMiniPlayerActive || isMinimized;
        var shouldBeEfficient = !isPlaying && isInBackgroundState;

        // Only call the API if the mode has actually changed.
        if (_isEfficiencyModeEnabled != shouldBeEfficient)
        {
            _isEfficiencyModeEnabled = shouldBeEfficient;
            _windowService.SetEfficiencyMode(shouldBeEfficient);
        }
    }

    private void UpdateTrackDetails(Song? song)
    {
        CurrentPlayingTrack = song;
        if (song != null)
        {
            SongTitle = song.Title;
            ArtistName = song.Artist?.Name ?? string.Empty;
            AlbumArtUri = ImageUriHelper.GetUriWithCacheBuster(song.AlbumArtUriFromTrack);
        }
        else
        {
            SongTitle = "No track playing";
            ArtistName = string.Empty;
            AlbumArtUri = null;
            TotalDuration = 0;
        }
    }

    private void UpdateCurrentQueueDisplay()
    {
        var sourceQueue = _playbackService.IsShuffleEnabled
            ? _playbackService.ShuffledQueue
            : _playbackService.PlaybackQueue;

        var newDisplayQueue = new List<Song>();
        var currentTrack = _playbackService.CurrentTrack;

        if (currentTrack != null)
        {
            // Find current track index directly without ToList() allocation
            var currentTrackIndex = -1;
            for (var i = 0; i < sourceQueue.Count; i++)
            {
                if (sourceQueue[i].Id == currentTrack.Id)
                {
                    currentTrackIndex = i;
                    break;
                }
            }

            if (currentTrackIndex != -1)
            {
                // Add songs from current position to end
                for (var i = currentTrackIndex; i < sourceQueue.Count; i++)
                    newDisplayQueue.Add(sourceQueue[i]);
                    
                // If repeating, add songs from beginning to current position
                if (_playbackService.CurrentRepeatMode == RepeatMode.RepeatAll)
                {
                    for (var i = 0; i < currentTrackIndex; i++)
                        newDisplayQueue.Add(sourceQueue[i]);
                }
            }
            else
            {
                foreach (var song in sourceQueue)
                    newDisplayQueue.Add(song);
            }
        }

        if (!CurrentQueue.SequenceEqual(newDisplayQueue))
        {
            CurrentQueue.Clear();
            foreach (var song in newDisplayQueue) CurrentQueue.Add(song);
        }
    }

    private void UpdateVolumeIconGlyph()
    {
        var newGlyph = IsMuted || CurrentVolume == 0
            ? MuteIconGlyph
            : CurrentVolume switch
            {
                <= VolumeLowThreshold => VolumeLowIconGlyph,
                <= VolumeMediumThreshold => VolumeMediumIconGlyph,
                _ => VolumeHighIconGlyph
            };

        if (VolumeIconGlyph != newGlyph) VolumeIconGlyph = newGlyph;
    }

    private void RunOnUIThread(Action action)
    {
        if (_isDisposed) return;

        // Avoid redundant dispatching if already on UI thread
        if (_dispatcherService.HasThreadAccess)
        {
            using (new ServiceUpdateScope(this))
            {
                action();
            }
            return;
        }

        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed) return;
            using (new ServiceUpdateScope(this))
            {
                action();
            }
        });
    }

    /// <summary>
    ///     A disposable struct that sets a flag to prevent property change feedback loops
    ///     when updating the ViewModel from a service.
    /// </summary>
    private readonly struct ServiceUpdateScope : IDisposable
    {
        private readonly PlayerViewModel _viewModel;

        public ServiceUpdateScope(PlayerViewModel viewModel)
        {
            _viewModel = viewModel;
            _viewModel._isUpdatingFromService = true;
        }

        public void Dispose()
        {
            _viewModel._isUpdatingFromService = false;
        }
    }

    #region Service Event Handling

    private void SubscribeToPlaybackServiceEvents()
    {
        _playbackService.PlaybackStateChanged += OnPlaybackService_PlaybackStateChanged;
        _playbackService.TrackChanged += OnPlaybackService_TrackChanged;
        _playbackService.VolumeStateChanged += OnPlaybackService_VolumeStateChanged;
        _playbackService.ShuffleModeChanged += OnPlaybackService_ShuffleModeChanged;
        _playbackService.RepeatModeChanged += OnPlaybackService_RepeatModeChanged;
        _playbackService.QueueChanged += OnPlaybackService_QueueChanged;
        _playbackService.PositionChanged += OnPlaybackService_PositionChanged;
        _playbackService.DurationChanged += OnPlaybackService_DurationChanged;
    }

    private void UnsubscribeFromPlaybackServiceEvents()
    {
        _playbackService.PlaybackStateChanged -= OnPlaybackService_PlaybackStateChanged;
        _playbackService.TrackChanged -= OnPlaybackService_TrackChanged;
        _playbackService.VolumeStateChanged -= OnPlaybackService_VolumeStateChanged;
        _playbackService.ShuffleModeChanged -= OnPlaybackService_ShuffleModeChanged;
        _playbackService.RepeatModeChanged -= OnPlaybackService_RepeatModeChanged;
        _playbackService.QueueChanged -= OnPlaybackService_QueueChanged;
        _playbackService.PositionChanged -= OnPlaybackService_PositionChanged;
        _playbackService.DurationChanged -= OnPlaybackService_DurationChanged;
    }

    private void InitializeStateFromService()
    {
        RunOnUIThread(() =>
        {
            IsPlaying = _playbackService.IsPlaying;
            UpdateTrackDetails(_playbackService.CurrentTrack);
            IsMuted = _playbackService.IsMuted;
            CurrentVolume = Math.Clamp(_playbackService.Volume * 100.0, 0.0, 100.0);
            IsShuffleEnabled = _playbackService.IsShuffleEnabled;
            CurrentRepeatMode = _playbackService.CurrentRepeatMode;
            TotalDuration = Math.Max(0, _playbackService.Duration.TotalSeconds);
            CurrentPosition = _playbackService.CurrentPosition.TotalSeconds;
            UpdateCurrentQueueDisplay();
            UpdateEfficiencyMode();
        });
    }

    private void OnPlaybackService_PlaybackStateChanged()
    {
        RunOnUIThread(() =>
        {
            IsPlaying = _playbackService.IsPlaying;
            UpdateEfficiencyMode();
        });
    }

    private void OnPlaybackService_TrackChanged()
    {
        // Reset throttle state so new track gets immediate updates
        _lastReportedPosition = 0;
        _lastDisplayedSecond = -1;

        RunOnUIThread(() =>
        {
            UpdateTrackDetails(_playbackService.CurrentTrack);
            CurrentPosition = 0;
            UpdateCurrentQueueDisplay();
        });
    }

    private void OnPlaybackService_VolumeStateChanged()
    {
        RunOnUIThread(() =>
        {
            IsMuted = _playbackService.IsMuted;
            CurrentVolume = Math.Clamp(_playbackService.Volume * 100.0, 0.0, 100.0);
        });
    }

    private void OnPlaybackService_ShuffleModeChanged()
    {
        RunOnUIThread(() =>
        {
            IsShuffleEnabled = _playbackService.IsShuffleEnabled;
            UpdateCurrentQueueDisplay();
        });
    }

    private void OnPlaybackService_RepeatModeChanged()
    {
        RunOnUIThread(() => CurrentRepeatMode = _playbackService.CurrentRepeatMode);
    }

    private void OnPlaybackService_QueueChanged()
    {
        RunOnUIThread(UpdateCurrentQueueDisplay);
    }

    private void OnPlaybackService_PositionChanged()
    {
        var newPosition = _playbackService.CurrentPosition.TotalSeconds;

        // Throttle: Skip if change is less than 100ms
        if (Math.Abs(newPosition - _lastReportedPosition) < PositionThrottleSeconds)
            return;

        _lastReportedPosition = newPosition;

        RunOnUIThread(() =>
        {
            if (!IsUserDraggingSlider) CurrentPosition = newPosition;
        });
    }

    private void OnPlaybackService_DurationChanged()
    {
        RunOnUIThread(() => TotalDuration = Math.Max(0, _playbackService.Duration.TotalSeconds));
    }

    private void SubscribeToWindowServiceEvents()
    {
        _windowService.UIStateChanged += OnWindowService_UIStateChanged;
    }

    private void UnsubscribeFromWindowServiceEvents()
    {
        _windowService.UIStateChanged -= OnWindowService_UIStateChanged;
    }

    private void OnWindowService_UIStateChanged()
    {
        RunOnUIThread(UpdateEfficiencyMode);
    }

    private async Task InitializeSettingsAsync()
    {
        try
        {
            await LoadPlayerButtonSettingsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load player button settings during initialization");
        }
    }

    private void SubscribeToSettingsServiceEvents()
    {
        _settingsService.PlayerButtonSettingsChanged += OnSettingsService_PlayerButtonSettingsChanged;
    }

    private void UnsubscribeFromSettingsServiceEvents()
    {
        _settingsService.PlayerButtonSettingsChanged -= OnSettingsService_PlayerButtonSettingsChanged;
    }

    private void OnSettingsService_PlayerButtonSettingsChanged()
    {
        _ = _dispatcherService.EnqueueAsync(LoadPlayerButtonSettingsAsync);
    }

    #endregion
}