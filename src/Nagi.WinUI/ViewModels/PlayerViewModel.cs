using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.WinUI.Models;
using Nagi.WinUI.Services.Abstractions;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Resources;
using System.Threading;

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     Manages the state and interactions for the main media player UI. This view model acts as a coordinator
///     between the UI and various services like <see cref="IMusicPlaybackService" /> and <see cref="IWindowService" />.
/// </summary>
public partial class PlayerViewModel : ObservableObject
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

    private const double VolumeLowThreshold = 33;
    private const double VolumeMediumThreshold = 66;
    private readonly IDispatcherService _dispatcherService;
    private readonly ILogger<PlayerViewModel> _logger;
    private readonly IMusicNavigationService _musicNavigationService;

    private readonly IMusicPlaybackService _playbackService;
    private readonly IUISettingsService _settingsService;
    private readonly IWindowService _windowService;
    private readonly ILibraryService _libraryService;

    private bool _isUpdatingFromService;
    private bool _isEfficiencyModeEnabled;
    private bool _isCleanedUp;

    private double _lastReportedPosition;
    private int _lastDisplayedSecond = -1;
    private const double PositionThrottleSeconds = 0.1;

    private CancellationTokenSource? _queueDisplayCts;

    private Action? _cancelGlobalOperation;

    public PlayerViewModel(IMusicPlaybackService playbackService,
        IMusicNavigationService musicNavigationService,
        IDispatcherService dispatcherService, IUISettingsService settingsService, IWindowService windowService,
        ILibraryService libraryService, ILogger<PlayerViewModel> logger)
    {
        _playbackService = playbackService ?? throw new ArgumentNullException(nameof(playbackService));
        _musicNavigationService = musicNavigationService ?? throw new ArgumentNullException(nameof(musicNavigationService));
        _dispatcherService = dispatcherService ?? throw new ArgumentNullException(nameof(dispatcherService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _windowService = windowService ?? throw new ArgumentNullException(nameof(windowService));
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        _logger = logger;

        SongTitle = Strings.Status_NoTrackPlaying;
        ArtistName = string.Empty;
        SidebarQueue = new PlaybackQueueCollection(_playbackService, _logger);
        CurrentTimeText = "0:00";
        TotalDurationText = "0:00";
        GlobalOperationStatusMessage = string.Empty;
        QueueDisplaySettings.PropertyChanged += OnQueueDisplaySettingChanged;

        SubscribeToPlaybackServiceEvents();
        _libraryService.LibraryContentChanged += OnQueueLibraryContentChanged;
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
    [NotifyCanExecuteChangedFor(nameof(GoToAlbumCommand))]
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
    [NotifyPropertyChangedFor(nameof(VolumeIconGlyph))]
    public partial bool IsMuted { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeIconGlyph))]
    public partial double CurrentVolume { get; set; }
    public ObservableRangeCollection<Song> CurrentQueue { get; } = new();
    [ObservableProperty] public partial double CurrentPosition { get; set; }
    [ObservableProperty] public partial string CurrentTimeText { get; set; }
    [ObservableProperty] public partial bool IsUserDraggingSlider { get; set; }
    [ObservableProperty] public partial double TotalDuration { get; set; }
    [ObservableProperty] public partial string TotalDurationText { get; set; }
    [ObservableProperty] public partial bool IsGlobalOperationInProgress { get; set; }
    [ObservableProperty] public partial string GlobalOperationStatusMessage { get; set; }
    [ObservableProperty] public partial double GlobalOperationProgressValue { get; set; }
    [ObservableProperty] public partial bool IsGlobalOperationIndeterminate { get; set; }
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelGlobalOperationCommand))]
    public partial bool IsGlobalOperationCancellable { get; set; }
    [ObservableProperty] public partial bool IsQueueViewVisible { get; set; }
    [ObservableProperty] public partial bool IsQueuePanelOpen { get; set; }
    [ObservableProperty] public partial QueueDisplaySettings QueueDisplaySettings { get; set; } = new();

    public string QueueTrackCountText => string.Format(Strings.QueuePanel_TrackCount, _playbackService.PlaybackQueue.Count);

    public PlaybackQueueCollection SidebarQueue { get; }

    partial void OnIsQueuePanelOpenChanged(bool value) => SidebarQueue.SetActive(value);

    [ObservableProperty] public partial bool IsLyricsPageActive { get; set; }

    [ObservableProperty] public partial bool IsVolumeControlVisible { get; set; }

    public ObservableRangeCollection<PlayerButtonSetting> MainTransportButtons { get; } = new();
    public ObservableRangeCollection<PlayerButtonSetting> SecondaryControlsButtons { get; } = new();

    public bool IsArtworkAvailable => !string.IsNullOrWhiteSpace(AlbumArtUri);
    public string PlayPauseIconGlyph => IsPlaying ? PauseIconGlyph : PlayIconGlyph;
    public string PlayPauseButtonToolTip => IsPlaying ? Strings.Tooltip_Pause : Strings.Tooltip_Play;
    public string ShuffleIconGlyph => IsShuffleEnabled ? ShuffleOnIconGlyph : ShuffleOffIconGlyph;
    public string ShuffleButtonToolTip => IsShuffleEnabled ? Strings.Tooltip_ShuffleOn : Strings.Tooltip_ShuffleOff;

    public string RepeatIconGlyph => CurrentRepeatMode switch
    {
        RepeatMode.RepeatAll => RepeatAllIconGlyph,
        RepeatMode.RepeatOne => RepeatOneIconGlyph,
        _ => RepeatOffIconGlyph
    };

    public void SetGlobalOperationCancellation(Action? cancel)
    {
        Interlocked.Exchange(ref _cancelGlobalOperation, cancel);
        IsGlobalOperationCancellable = cancel != null;
    }

    private bool CanCancelGlobalOperation() => IsGlobalOperationCancellable;

    [RelayCommand(CanExecute = nameof(CanCancelGlobalOperation))]
    private void CancelGlobalOperation()
    {
        var cancel = Interlocked.Exchange(ref _cancelGlobalOperation, null);
        IsGlobalOperationCancellable = false;
        cancel?.Invoke();
    }

    public string RepeatButtonToolTip => CurrentRepeatMode switch
    {
        RepeatMode.Off => Strings.Tooltip_RepeatOff,
        RepeatMode.RepeatAll => Strings.Tooltip_RepeatAll,
        RepeatMode.RepeatOne => Strings.Tooltip_RepeatOne,
        _ => Strings.Tooltip_Repeat
    };

    public string VolumeButtonToolTip => IsMuted ? Strings.Tooltip_Unmute : Strings.Tooltip_Mute;

    partial void OnIsPlayingChanged(bool value)
    {
        UpdateButtonState("PlayPause", PlayPauseIconGlyph, PlayPauseButtonToolTip);
    }

    partial void OnIsShuffleEnabledChanged(bool value)
    {
        UpdateButtonState("Shuffle", ShuffleIconGlyph, ShuffleButtonToolTip);
    }

    partial void OnCurrentRepeatModeChanged(RepeatMode value)
    {
        UpdateButtonState("Repeat", RepeatIconGlyph, RepeatButtonToolTip);
    }

    private void UpdateButtonState(string buttonId, string icon, string toolTip)
    {
        var button = MainTransportButtons.FirstOrDefault(b => b.Id == buttonId)
                     ?? SecondaryControlsButtons.FirstOrDefault(b => b.Id == buttonId);

        if (button != null)
        {
            button.DynamicIcon = icon;
            button.DynamicToolTip = toolTip;
        }
    }

    /// <summary>
    ///     Cleans up resources and unsubscribes from service events.
    ///     Called during application shutdown.
    /// </summary>
    public void Cleanup()
    {
        if (_isCleanedUp) return;
        _isCleanedUp = true;
        _logger.LogDebug("Cleaning up PlayerViewModel resources");
        UnsubscribeFromPlaybackServiceEvents();
        _libraryService.LibraryContentChanged -= OnQueueLibraryContentChanged;
        UnsubscribeFromSettingsServiceEvents();
        UnsubscribeFromWindowServiceEvents();
        QueueDisplaySettings.PropertyChanged -= OnQueueDisplaySettingChanged;
        SidebarQueue.Dispose();
        _queueDisplayCts?.Cancel();
        _queueDisplayCts?.Dispose();
        _queueDisplayCts = null;
    }


    /// <summary>
    ///     Loads player button settings and splits them into main and secondary controls
    ///     based on a special "Separator" item.
    /// </summary>
    private async Task LoadPlayerButtonSettingsAsync()
    {
        if (_isCleanedUp) return;
        var allButtons = await _settingsService.GetPlayerButtonSettingsAsync();

        var enabledButtons = allButtons.Where(s => s.IsEnabled).ToList();

        IsVolumeControlVisible = enabledButtons.Any(b => b.Id == "Volume");

        var separatorIndex = enabledButtons.FindIndex(b => b.Id == "Separator");

        foreach (var button in enabledButtons)
        {
            switch (button.Id)
            {
                case "PlayPause":
                    button.Command = PlayPauseCommand;
                    button.DynamicIcon = PlayPauseIconGlyph;
                    button.DynamicToolTip = PlayPauseButtonToolTip;
                    break;
                case "Shuffle":
                    button.Command = ToggleShuffleCommand;
                    button.DynamicIcon = ShuffleIconGlyph;
                    button.DynamicToolTip = ShuffleButtonToolTip;
                    break;
                case "Repeat":
                    button.Command = CycleRepeatCommand;
                    button.DynamicIcon = RepeatIconGlyph;
                    button.DynamicToolTip = RepeatButtonToolTip;
                    break;
                case "Volume":
                    button.Command = ToggleMuteCommand;
                    button.DynamicIcon = VolumeIconGlyph;
                    button.DynamicToolTip = VolumeButtonToolTip;
                    break;
                case "Previous":
                    button.Command = PreviousCommand;
                    button.DynamicIcon = "\uE892";
                    button.DynamicToolTip = Strings.Player_PreviousButton_ToolTip;
                    break;
                case "Next":
                    button.Command = NextCommand;
                    button.DynamicIcon = "\uE893";
                    button.DynamicToolTip = Strings.Player_NextButton_ToolTip;
                    break;
                case "Lyrics":
                    button.DynamicIcon = "\uE8D2";
                    button.DynamicToolTip = Strings.Player_LyricsButton_ToolTip;
                    break;
                case "Queue":
                    button.Command = ToggleQueuePanelCommand;
                    button.DynamicIcon = "\uE90B";
                    button.DynamicToolTip = Strings.Player_QueueButton_ToolTip;
                    break;
            }
        }

        UpdateCollectionIfChanged(MainTransportButtons,
            separatorIndex < 0 ? enabledButtons : enabledButtons.Take(separatorIndex));
        UpdateCollectionIfChanged(SecondaryControlsButtons,
            separatorIndex < 0 ? [] : enabledButtons.Skip(separatorIndex + 1));
    }

    private static void UpdateCollectionIfChanged(ObservableRangeCollection<PlayerButtonSetting> collection,
        IEnumerable<PlayerButtonSetting> newItems)
    {
        if (!collection.Select(button => button.Id).SequenceEqual(newItems.Select(button => button.Id)))
            collection.ReplaceRange(newItems);
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

    private bool CanClearQueue() => _playbackService.PlaybackQueue.Count > 0;

    [RelayCommand]
    private void ToggleQueuePanel() => IsQueuePanelOpen = !IsQueuePanelOpen;

    [RelayCommand(CanExecute = nameof(CanClearQueue))]
    private Task ClearQueueAsync() => _playbackService.ClearQueueAsync();

    [RelayCommand]
    private Task PlayPauseAsync() => _playbackService.PlayPauseAsync();

    [RelayCommand]
    private Task PreviousAsync() => _playbackService.PreviousAsync();

    [RelayCommand]
    private Task NextAsync() => _playbackService.NextAsync();

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

    [RelayCommand(CanExecute = nameof(CanNavigateToTrack))]
    public async Task GoToArtistAsync(object? parameter)
    {
        _windowService.ShowAndActivate();
        await _musicNavigationService.NavigateToArtistAsync(parameter ?? CurrentPlayingTrack);
    }

    [RelayCommand(CanExecute = nameof(CanNavigateToTrack))]
    public async Task GoToAlbumAsync(object? parameter)
    {
        _windowService.ShowAndActivate();
        await _musicNavigationService.NavigateToAlbumAsync(parameter ?? CurrentPlayingTrack);
    }

    private bool CanNavigateToTrack(object? parameter) => parameter != null || CurrentPlayingTrack != null;

    partial void OnIsMutedChanged(bool value) => UpdateButtonState("Volume", VolumeIconGlyph, VolumeButtonToolTip);

    partial void OnCurrentVolumeChanged(double value)
    {
        UpdateButtonState("Volume", VolumeIconGlyph, VolumeButtonToolTip);
        if (_isUpdatingFromService) return;
        var serviceVolume = Math.Clamp(value / 100.0, 0.0, 1.0);
        if (Math.Abs(_playbackService.Volume - serviceVolume) > 0.001)
            _ = _playbackService.SetVolumeAsync(serviceVolume);
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
        var (isVisible, isMiniPlayerActive, isMinimized, isPlaying) =
            (_windowService.IsVisible, _windowService.IsMiniPlayerActive,
             _windowService.IsMinimized, _playbackService.IsPlaying);

        var isInBackgroundState = !isVisible || isMiniPlayerActive || isMinimized;
        var shouldBeEfficient = !isPlaying && isInBackgroundState;

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
            ArtistName = song.ArtistName;
            AlbumArtUri = ImageUriHelper.GetUriWithCacheBuster(song.AlbumArtUriFromTrack);
        }
        else
        {
            SongTitle = Strings.Status_NoTrackPlaying;
            ArtistName = string.Empty;
            AlbumArtUri = null;
            TotalDuration = 0;
        }
    }

    private void UpdateCurrentQueueDisplay()
    {
        ClearQueueCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(QueueTrackCountText));

        // Cancel any previous in-flight fetch to prevent stale data from arriving out of order
        _queueDisplayCts?.Cancel();
        _queueDisplayCts?.Dispose();
        _queueDisplayCts = new CancellationTokenSource();
        var token = _queueDisplayCts.Token;

        _ = UpdateCurrentQueueDisplayAsync(token);
    }

    private async Task UpdateCurrentQueueDisplayAsync(CancellationToken token)
    {

        try
        {
            var isShuffleEnabled = _playbackService.IsShuffleEnabled;
            var repeatMode = _playbackService.CurrentRepeatMode;
            var sourceQueue = isShuffleEnabled
                ? _playbackService.ShuffledQueue
                : _playbackService.PlaybackQueue;

            if (sourceQueue.Count == 0)
            {
                CurrentQueue.Clear();
                return;
            }

            var currentTrackIndexInSource = isShuffleEnabled
                ? _playbackService.CurrentShuffledIndex
                : _playbackService.CurrentQueueIndex;

            // For large libraries, we only show a window of the queue to keep the UI snappy
            // and avoid allocating 500k objects in the ObservableCollection.
            const int displayWindowSize = 100;
            var start = currentTrackIndexInSource >= 0 && currentTrackIndexInSource < sourceQueue.Count
                ? currentTrackIndexInSource : 0;
            var upcoming = sourceQueue.Skip(start);
            if (repeatMode == RepeatMode.RepeatAll) upcoming = upcoming.Concat(sourceQueue.Take(start));
            var idsToShow = upcoming.Take(displayWindowSize).ToArray();

            if (token.IsCancellationRequested) return;

            var fetchedSongs = await _playbackService.GetQueueTracksAsync(idsToShow);

            if (token.IsCancellationRequested) return;

            var newDisplayQueue = idsToShow
                .Select(id => fetchedSongs.GetValueOrDefault(id))
                .OfType<Song>()
                .ToList();

            if (!CurrentQueue.SequenceEqual(newDisplayQueue))
            {
                CurrentQueue.ReplaceRange(newDisplayQueue);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when a newer queue update supersedes this one
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update queue display");
        }
    }

    public Task AddFilesToQueueAsync(IEnumerable<string> filePaths) =>
        _playbackService.AddTransientFilesToQueueAsync(filePaths);

    public string VolumeIconGlyph => IsMuted || CurrentVolume == 0
        ? MuteIconGlyph
        : CurrentVolume switch
        {
            <= VolumeLowThreshold => VolumeLowIconGlyph,
            <= VolumeMediumThreshold => VolumeMediumIconGlyph,
            _ => VolumeHighIconGlyph
        };

    private void RunOnUIThread(Action action)
    {
        void ApplyUpdate()
        {
            if (_isCleanedUp) return;
            var wasUpdating = _isUpdatingFromService;
            _isUpdatingFromService = true;
            try
            {
                action();
            }
            finally
            {
                _isUpdatingFromService = wasUpdating;
            }
        }

        if (_dispatcherService.HasThreadAccess) ApplyUpdate();
        else _dispatcherService.TryEnqueue(ApplyUpdate);
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
        RunOnUIThread(() =>
        {
            _lastReportedPosition = 0;
            _lastDisplayedSecond = -1;
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
        RunOnUIThread(() =>
        {
            CurrentRepeatMode = _playbackService.CurrentRepeatMode;
            UpdateCurrentQueueDisplay();
        });
    }

    private void OnPlaybackService_QueueChanged()
    {
        RunOnUIThread(() =>
        {
            UpdateCurrentQueueDisplay();
            SidebarQueue.Refresh();
        });
    }

    private void OnQueueLibraryContentChanged(object? sender, LibraryContentChangedEventArgs e)
    {
        if (e.ChangeType == LibraryChangeType.FolderAdded) return;
        RunOnUIThread(() =>
        {
            UpdateCurrentQueueDisplay();
            if (IsQueuePanelOpen) SidebarQueue.Refresh(reloadMetadata: true);
        });
    }

    private void OnPlaybackService_PositionChanged()
    {
        RunOnUIThread(() =>
        {
            if (IsUserDraggingSlider) return;
            var newPosition = _playbackService.CurrentPosition.TotalSeconds;
            if (Math.Abs(newPosition - _lastReportedPosition) < PositionThrottleSeconds) return;
            _lastReportedPosition = newPosition;
            CurrentPosition = newPosition;
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
            if (_isCleanedUp) return;
            QueueDisplaySettings = await _settingsService.GetQueueDisplaySettingsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load player button settings during initialization");
        }
    }

    private void SubscribeToSettingsServiceEvents()
    {
        _settingsService.PlayerButtonSettingsChanged += OnSettingsService_PlayerButtonSettingsChanged;
        _settingsService.QueueDisplaySettingsChanged += OnSettingsService_QueueDisplaySettingsChanged;
    }

    private void UnsubscribeFromSettingsServiceEvents()
    {
        _settingsService.PlayerButtonSettingsChanged -= OnSettingsService_PlayerButtonSettingsChanged;
        _settingsService.QueueDisplaySettingsChanged -= OnSettingsService_QueueDisplaySettingsChanged;
    }

    private async void OnSettingsService_PlayerButtonSettingsChanged()
    {
        try
        {
            await _dispatcherService.EnqueueAsync(LoadPlayerButtonSettingsAsync);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reload player button settings.");
        }
    }

    partial void OnQueueDisplaySettingsChanging(QueueDisplaySettings value) =>
        QueueDisplaySettings.PropertyChanged -= OnQueueDisplaySettingChanged;

    partial void OnQueueDisplaySettingsChanged(QueueDisplaySettings value)
    {
        value.PropertyChanged += OnQueueDisplaySettingChanged;
        SidebarQueue.SetShowTotalDuration(value.ShowTotalDuration);
    }

    private void OnSettingsService_QueueDisplaySettingsChanged(QueueDisplaySettings settings) =>
        RunOnUIThread(() => QueueDisplaySettings = settings);

    private async void OnQueueDisplaySettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QueueDisplaySettings.ShowTotalDuration))
            SidebarQueue.SetShowTotalDuration(QueueDisplaySettings.ShowTotalDuration);
        try
        {
            await _settingsService.SetQueueDisplaySettingsAsync(QueueDisplaySettings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save queue display settings.");
        }
    }

    #endregion
}
