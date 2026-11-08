using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Library;
using Nagi.Core.Listening;
using Nagi.Core.Platform;
using Nagi.Core.Playback;
using Nagi.Core.Settings;
using Nagi.Presentation.Common;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Settings;
using Nagi.Presentation.Shell;

namespace Nagi.Presentation.Player;

public partial class PlayerViewModel : ObservableObject
{
    private const double VolumeLowThreshold = 33;
    private const double VolumeMediumThreshold = 66;
    private readonly IDispatcherService _dispatcherService;
    private readonly ILogger<PlayerViewModel> _logger;
    private readonly IMusicNavigationService _musicNavigationService;

    private readonly IMusicPlaybackService _playbackService;
    private readonly ISettingsService _settingsService;
    private readonly IWindowService _windowService;
    private readonly ILibraryReader _libraryReader;
    private readonly IListeningService _listening;
    private readonly ILibraryNotifier _libraryNotifier;
    private readonly TimeProvider _timeProvider;

    private bool _isCleanedUp;

    private double _lastReportedPosition;
    private int _lastDisplayedSecond = -1;
    private const double PositionThrottleSeconds = 0.1;

    // The slider writes its value back through the two-way binding and can round it to its step, so a position this
    // close to the service's is an echo, not a seek.
    private const double SeekThresholdSeconds = 0.5;

    private CancellationTokenSource? _queueDisplayCts;
    private int _queuePreviewCount;

    // A song that opens normally starts within about a quarter of a second, so waiting longer keeps the indicator
    // from flashing on every change of song.
    private static readonly TimeSpan OpeningIndicatorDelay = TimeSpan.FromMilliseconds(500);
    private readonly Debouncer _openingIndicator;

    public PlayerViewModel(IMusicPlaybackService playbackService,
        IMusicNavigationService musicNavigationService,
        IDispatcherService dispatcherService, ISettingsService settingsService, IWindowService windowService,
        ILibraryReader libraryReader, IListeningService listening, ILibraryNotifier libraryNotifier, ILogger<PlayerViewModel> logger, IUIService uiService,
        TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _openingIndicator = new Debouncer(OpeningIndicatorDelay, _timeProvider);
        _playbackService = playbackService;
        _musicNavigationService = musicNavigationService;
        _dispatcherService = dispatcherService;
        _settingsService = settingsService;
        _windowService = windowService;
        _libraryReader = libraryReader;
        _listening = listening;
        _libraryNotifier = libraryNotifier;
        _logger = logger;
        _uiService = uiService;

        SongTitle = Strings.Status_NoTrackPlaying;
        ArtistName = string.Empty;
        SidebarQueue = new PlaybackQueueCollection(_playbackService, _logger);
        History = new RecentHistoryViewModel(_libraryReader, _listening, _dispatcherService, _logger);
        CurrentTimeText = "0:00";
        TotalDurationText = "0:00";

        SubscribeToPlaybackServiceEvents();
        InitializeListeningEvents();
        _libraryNotifier.Changed += OnQueueLibraryChanged;
        _settingsService.SettingChanged += OnSettingChanged;
        InitializeStateFromService();
        LoadPlayerButtonSettings();
        QueueDisplaySettings = _settingsService.Get(AppSettings.QueueDisplay);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseIcon))]
    [NotifyPropertyChangedFor(nameof(PlayPauseButtonToolTip))]
    public partial bool IsPlaying { get; set; }

    /// <summary>
    ///     True once the current song has spent a moment opening, such as while a slow music server answers.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseButtonToolTip))]
    public partial bool IsOpening { get; set; }

    [ObservableProperty] public partial string SongTitle { get; set; }

    [ObservableProperty] public partial string ArtistName { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsArtworkAvailable), nameof(IsTrayCoverArtVisible))]
    public partial string? AlbumArtUri { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoToArtistCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoToAlbumCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopAfterTrackCommand))]
    [NotifyPropertyChangedFor(nameof(CurrentPlayingSongId))]
    public partial Song? CurrentPlayingTrack { get; set; }

    public Guid? CurrentPlayingSongId => CurrentPlayingTrack?.Id;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShuffleButtonToolTip))]
    public partial bool IsShuffleEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepeatIcon))]
    [NotifyPropertyChangedFor(nameof(RepeatButtonToolTip))]
    public partial RepeatMode CurrentRepeatMode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeButtonToolTip))]
    [NotifyPropertyChangedFor(nameof(VolumeIcon))]
    public partial bool IsMuted { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeIcon))]
    public partial double CurrentVolume { get; set; }
    public ObservableRangeCollection<Song> CurrentQueue { get; } = new();
    [ObservableProperty] public partial double CurrentPosition { get; set; }
    [ObservableProperty] public partial string CurrentTimeText { get; set; }
    [ObservableProperty] public partial bool IsUserDraggingSlider { get; set; }
    [ObservableProperty] public partial double TotalDuration { get; set; }
    [ObservableProperty] public partial string TotalDurationText { get; set; }
    [ObservableProperty] public partial bool IsQueuePanelOpen { get; set; }
    [ObservableProperty] public partial QueueDisplaySettings QueueDisplaySettings { get; set; } = new();

    public PlaybackQueueCollection SidebarQueue { get; }

    partial void OnIsQueuePanelOpenChanged(bool value)
    {
        SidebarQueue.SetActive(value && !IsHistoryVisible);
        History.SetActive(value && IsHistoryVisible);
    }

    [ObservableProperty] public partial bool IsLyricsPageActive { get; set; }

    [ObservableProperty] public partial bool IsVolumeControlVisible { get; set; }

    public ObservableRangeCollection<PlayerButtonSetting> MainTransportButtons { get; } = new();
    public ObservableRangeCollection<PlayerButtonSetting> SecondaryControlsButtons { get; } = new();

    public bool IsArtworkAvailable => !string.IsNullOrWhiteSpace(AlbumArtUri);

    /// <summary>
    ///     Whether the tray flyout shows the track's cover art behind its controls.
    /// </summary>
    public bool IsTrayCoverArtVisible =>
        IsArtworkAvailable && _settingsService.Get(AppSettings.ShowCoverArtInTrayFlyout);

    public AppIcon PlayPauseIcon => IsPlaying ? AppIcon.Pause : AppIcon.Play;
    public string PlayPauseButtonToolTip => IsOpening ? Strings.Status_Loading
        : IsPlaying ? Strings.Tooltip_Pause
        : Strings.Generic_Play;
    public string ShuffleButtonToolTip => IsShuffleEnabled
        ? ShuffleModeText.GetName(_playbackService.CurrentShuffleMode)
        : Strings.Tooltip_ShuffleOff;

    public AppIcon RepeatIcon => CurrentRepeatMode switch
    {
        RepeatMode.RepeatAll => AppIcon.RepeatAll,
        RepeatMode.RepeatOne => AppIcon.RepeatOne,
        _ => AppIcon.RepeatOff
    };

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
        UpdateButtonState(PlayerButtonIds.PlayPause, PlayPauseIcon, PlayPauseButtonToolTip, isLoading: IsOpening);
    }

    partial void OnIsOpeningChanged(bool value)
    {
        UpdateButtonState(PlayerButtonIds.PlayPause, PlayPauseIcon, PlayPauseButtonToolTip, isLoading: value);
    }

    partial void OnIsShuffleEnabledChanged(bool value)
    {
        UpdateButtonState(PlayerButtonIds.Shuffle, AppIcon.Shuffle, ShuffleButtonToolTip, IsShuffleEnabled);
    }

    partial void OnCurrentRepeatModeChanged(RepeatMode value)
    {
        UpdateButtonState(PlayerButtonIds.Repeat, RepeatIcon, RepeatButtonToolTip);
    }

    private void UpdateButtonState(string buttonId, AppIcon icon, string toolTip, bool isIconActive = true,
        bool isLoading = false)
    {
        var button = MainTransportButtons.FirstOrDefault(b => b.Id == buttonId)
                     ?? SecondaryControlsButtons.FirstOrDefault(b => b.Id == buttonId);

        if (button != null)
        {
            button.IsIconActive = isIconActive;
            button.DynamicIcon = icon;
            button.DynamicToolTip = toolTip;
            button.IsLoading = isLoading;
        }
    }

    public void Cleanup()
    {
        if (_isCleanedUp) return;
        _isCleanedUp = true;
        _logger.LogDebug("Cleaning up PlayerViewModel resources");
        UnsubscribeFromPlaybackServiceEvents();
        CleanupListening();
        _libraryNotifier.Changed -= OnQueueLibraryChanged;
        _settingsService.SettingChanged -= OnSettingChanged;

        QueueDisplaySettings.PropertyChanged -= OnQueueDisplaySettingChanged;
        SidebarQueue.Dispose();
        _queueDisplayCts?.Cancel();
        _queueDisplayCts?.Dispose();
        _queueDisplayCts = null;
        _openingIndicator.Dispose();
    }

    private void LoadPlayerButtonSettings()
    {
        if (_isCleanedUp) return;
        var allButtons = _settingsService.Get(AppSettings.PlayerButtons);

        var enabledButtons = allButtons.Where(s => s.IsEnabled).ToList();

        IsVolumeControlVisible = enabledButtons.Any(b => b.Id == PlayerButtonIds.Volume);

        var separatorIndex = enabledButtons.FindIndex(b => b.IsSeparator);

        foreach (var button in enabledButtons)
        {
            switch (button.Id)
            {
                case PlayerButtonIds.PlayPause:
                    button.Command = PlayPauseCommand;
                    button.DynamicIcon = PlayPauseIcon;
                    button.DynamicToolTip = PlayPauseButtonToolTip;
                    button.IsLoading = IsOpening;
                    break;
                case PlayerButtonIds.Shuffle:
                    button.Command = ToggleShuffleCommand;
                    button.IsIconActive = IsShuffleEnabled;
                    button.DynamicIcon = AppIcon.Shuffle;
                    button.DynamicToolTip = ShuffleButtonToolTip;
                    break;
                case PlayerButtonIds.Repeat:
                    button.Command = CycleRepeatCommand;
                    button.DynamicIcon = RepeatIcon;
                    button.DynamicToolTip = RepeatButtonToolTip;
                    break;
                case PlayerButtonIds.Volume:
                    button.Command = ToggleMuteCommand;
                    button.DynamicIcon = VolumeIcon;
                    button.DynamicToolTip = VolumeButtonToolTip;
                    break;
                case PlayerButtonIds.Previous:
                    button.Command = PreviousCommand;
                    button.DynamicIcon = button.Icon;
                    button.DynamicToolTip = Strings.Player_PreviousButton_ToolTip;
                    break;
                case PlayerButtonIds.Next:
                    button.Command = NextCommand;
                    button.DynamicIcon = button.Icon;
                    button.DynamicToolTip = Strings.Player_NextButton_ToolTip;
                    break;
                case PlayerButtonIds.Lyrics:
                    button.DynamicIcon = button.Icon;
                    button.DynamicToolTip = Strings.Player_LyricsButton_ToolTip;
                    break;
                case PlayerButtonIds.Queue:
                    button.Command = ToggleQueuePanelCommand;
                    button.DynamicIcon = button.Icon;
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

    /// <summary>
    ///     Keeps <see cref="CurrentQueue" /> filled with the upcoming songs until the returned handle is disposed.
    ///     Each view showing the queue holds its own handle, so one view closing its queue doesn't empty another's.
    /// </summary>
    public IDisposable PreviewQueue()
    {
        if (_queuePreviewCount++ == 0) UpdateCurrentQueueDisplay();
        return new QueuePreview(this);
    }

    private sealed class QueuePreview(PlayerViewModel owner) : IDisposable
    {
        private bool _isDisposed;

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            if (--owner._queuePreviewCount == 0) owner.UpdateCurrentQueueDisplay();
        }
    }

    /// <returns>Whether the navigation pane was open last time, or <c>null</c> when its state isn't remembered.</returns>
    public bool? GetRememberedPaneOpen() =>
        _settingsService.Get(AppSettings.RememberPaneState) ? _settingsService.Get(AppSettings.LastPaneOpen) : null;

    /// <returns>The navigation items the user has turned on, in their chosen order.</returns>
    public IReadOnlyList<NavigationItemSetting> GetVisibleNavigationItems() =>
        _settingsService.Get(AppSettings.NavigationItems).Where(item => item.IsEnabled).ToList();

    public void RememberPaneOpen(bool isOpen)
    {
        if (_settingsService.Get(AppSettings.RememberPaneState))
            _settingsService.Set(AppSettings.LastPaneOpen, isOpen);
    }

    private bool CanClearQueue() => _playbackService.PlayOrder.Count > 0;

    [RelayCommand]
    private void ToggleQueuePanel() => IsQueuePanelOpen = !IsQueuePanelOpen;

    [RelayCommand(CanExecute = nameof(CanClearQueue))]
    private Task ClearQueueAsync() => RunActionAsync(_playbackService.ClearQueueAsync);

    [RelayCommand]
    private Task PlayPauseAsync() => RunActionAsync(_playbackService.PlayPauseAsync);

    [RelayCommand]
    private Task PreviousAsync() => RunActionAsync(_playbackService.PreviousAsync);

    [RelayCommand]
    private Task NextAsync() => RunActionAsync(_playbackService.NextAsync);

    private Task RunActionAsync(Func<Task> action) => UserAction.RunAsync(action, _logger, _uiService, () => _isCleanedUp);

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

    public double PlaybackRate => _playbackService.PlaybackRate;

    [RelayCommand]
    private Task SetPlaybackRateAsync(double rate)
    {
        return _playbackService.SetPlaybackRateAsync(rate);
    }

    // Without a request it opens the playing song's artist.
    [RelayCommand(CanExecute = nameof(CanGoToArtist))]
    private async Task GoToArtistAsync(ArtistNavigationRequest? request)
    {
        _windowService.ShowAndActivate();
        await _musicNavigationService.OpenArtistAsync(request ?? new ArtistNavigationRequest { Song = CurrentPlayingTrack });
    }

    [RelayCommand(CanExecute = nameof(HasPlayingTrack))]
    private async Task GoToAlbumAsync()
    {
        if (CurrentPlayingTrack is not { } song) return;
        _windowService.ShowAndActivate();
        await _musicNavigationService.OpenAlbumAsync(song);
    }

    private bool CanGoToArtist(ArtistNavigationRequest? request) => request != null || HasPlayingTrack();

    private bool HasPlayingTrack() => CurrentPlayingTrack != null;

    partial void OnIsMutedChanged(bool value) => UpdateButtonState(PlayerButtonIds.Volume, VolumeIcon, VolumeButtonToolTip);

    /// <summary>Moves the volume one step, as the mouse wheel does over a volume control.</summary>
    public void StepVolume(bool up) => CurrentVolume = Math.Clamp(CurrentVolume + (up ? 5 : -5), 0, 100);

    partial void OnCurrentVolumeChanged(double value)
    {
        UpdateButtonState(PlayerButtonIds.Volume, VolumeIcon, VolumeButtonToolTip);
        var serviceVolume = Math.Clamp(value / 100.0, 0.0, 1.0);
        if (Math.Abs(_playbackService.Volume - serviceVolume) > 0.001)
            _ = _playbackService.SetVolumeAsync(serviceVolume);
    }

    partial void OnCurrentPositionChanged(double value)
    {
        var currentSecond = (int)value;
        if (currentSecond != _lastDisplayedSecond)
        {
            _lastDisplayedSecond = currentSecond;
            CurrentTimeText = DurationFormatter.Format(TimeSpan.FromSeconds(value));
        }

        if (!IsUserDraggingSlider) SeekIfMoved();
    }

    partial void OnIsUserDraggingSliderChanged(bool value)
    {
        if (!value) SeekIfMoved();
    }

    private void SeekIfMoved()
    {
        if (Math.Abs(_playbackService.CurrentPosition.TotalSeconds - CurrentPosition) > SeekThresholdSeconds)
            _ = _playbackService.SeekAsync(TimeSpan.FromSeconds(CurrentPosition));
    }

    partial void OnTotalDurationChanged(double value)
    {
        TotalDurationText = DurationFormatter.Format(TimeSpan.FromSeconds(value));
    }

    private void UpdateTrackDetails(Song? song)
    {
        CurrentPlayingTrack = song;
        IsCurrentTrackFavorite = song?.IsLoved ?? false;
        ToggleFavoriteCommand.NotifyCanExecuteChanged();
        if (song != null)
        {
            SongTitle = song.Title;
            ArtistName = song.ArtistName;
            AlbumArtUri = song.AlbumArtUriFromTrack;
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
        StopAtQueueEndCommand.NotifyCanExecuteChanged();

        _queueDisplayCts?.Cancel();
        _queueDisplayCts?.Dispose();
        _queueDisplayCts = null;
        if (_queuePreviewCount == 0) return;

        _queueDisplayCts = new CancellationTokenSource();
        var token = _queueDisplayCts.Token;

        _ = UpdateCurrentQueueDisplayAsync(token);
    }

    private async Task UpdateCurrentQueueDisplayAsync(CancellationToken token)
    {
        try
        {
            const int displayWindowSize = 100;
            var idsToShow = _playbackService.UpcomingQueue.Take(displayWindowSize).ToArray();
            if (idsToShow.Length == 0)
            {
                CurrentQueue.Clear();
                return;
            }

            var fetchedSongs = await _playbackService.GetQueueSongsAsync(idsToShow, token);

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
            // A newer queue update superseded this one.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update queue display");
        }
    }

    /// <summary>Queues dropped files and folders in file name order.</summary>
    public async Task AddFilesToQueueAsync(IEnumerable<string> paths)
    {
        try { await _playbackService.AddTransientFilesToQueueAsync(paths.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to add dropped files to the playback queue."); }
    }

    public AppIcon VolumeIcon => IsMuted || CurrentVolume == 0
        ? AppIcon.Mute
        : CurrentVolume switch
        {
            <= VolumeLowThreshold => AppIcon.VolumeLow,
            <= VolumeMediumThreshold => AppIcon.VolumeMedium,
            _ => AppIcon.VolumeHigh
        };

    private void RunOnUIThread(Action action)
    {
        void ApplyUpdate()
        {
            if (!_isCleanedUp) action();
        }

        if (_dispatcherService.HasThreadAccess) ApplyUpdate();
        else _dispatcherService.TryEnqueue(ApplyUpdate);
    }

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
        });
    }

    private void OnPlaybackService_PlaybackStateChanged()
    {
        RunOnUIThread(() =>
        {
            IsPlaying = _playbackService.IsPlaying;
            UpdateOpening();
        });
    }

    private void UpdateOpening()
    {
        if (!_playbackService.IsOpening)
        {
            _openingIndicator.Cancel();
            IsOpening = false;
        }
        else if (!IsOpening)
        {
            _openingIndicator.Trigger(_ =>
            {
                RunOnUIThread(() => IsOpening = _playbackService.IsOpening);
                return Task.CompletedTask;
            });
        }
    }

    internal Task PendingOpeningIndicator => _openingIndicator.PendingInvocation;

    private void OnPlaybackService_TrackChanged()
    {
        RunOnUIThread(() =>
        {
            _lastDisplayedSecond = -1;
            UpdateTrackDetails(_playbackService.CurrentTrack);
            // A song loaded paused reports no duration of its own, so take the library's until it plays.
            TotalDuration = Math.Max(0, _playbackService.Duration.TotalSeconds);
            _lastReportedPosition = _playbackService.CurrentPosition.TotalSeconds;
            CurrentPosition = _lastReportedPosition;
            UpdateCurrentQueueDisplay();
            SidebarQueue.Refresh();
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
            OnPropertyChanged(nameof(ShuffleButtonToolTip));
            UpdateButtonState(PlayerButtonIds.Shuffle, AppIcon.Shuffle, ShuffleButtonToolTip, IsShuffleEnabled);
            UpdateCurrentQueueDisplay();
            SidebarQueue.Refresh();
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

    private void OnQueueLibraryChanged(object? sender, LibraryChange change)
    {
        if (change.Kind == LibraryChangeKind.RootAdded) return;
        RunOnUIThread(() =>
        {
            UpdateCurrentQueueDisplay();
            SidebarQueue.Refresh(reloadMetadata: true);
            if (IsQueuePanelOpen && IsHistoryVisible) _ = History.SearchAsync();
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

    private void OnSettingChanged(Setting setting)
    {
        if (setting == AppSettings.PlayerButtons) RunOnUIThread(LoadPlayerButtonSettings);
        else if (setting == AppSettings.QueueDisplay)
            RunOnUIThread(() => QueueDisplaySettings = _settingsService.Get(AppSettings.QueueDisplay));
        else if (setting == AppSettings.ShowCoverArtInTrayFlyout)
            RunOnUIThread(() => OnPropertyChanged(nameof(IsTrayCoverArtVisible)));
    }

    partial void OnQueueDisplaySettingsChanging(QueueDisplaySettings value) =>
        QueueDisplaySettings.PropertyChanged -= OnQueueDisplaySettingChanged;

    partial void OnQueueDisplaySettingsChanged(QueueDisplaySettings value)
    {
        value.PropertyChanged += OnQueueDisplaySettingChanged;
        SidebarQueue.SetShowTotalDuration(value.ShowTotalDuration);
    }

    private void OnQueueDisplaySettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QueueDisplaySettings.ShowTotalDuration))
            SidebarQueue.SetShowTotalDuration(QueueDisplaySettings.ShowTotalDuration);
        _settingsService.Set(AppSettings.QueueDisplay, QueueDisplaySettings);
    }
}
