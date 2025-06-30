using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Nagi.Models;
using Nagi.Services.Abstractions;

namespace Nagi.ViewModels;

/// <summary>
///     Manages state and logic for the main media player controls and queue display.
///     It synchronizes its state with the IMusicPlaybackService and provides commands for UI interaction.
/// </summary>
public partial class PlayerViewModel : ObservableObject, IDisposable
{
    // --- Constants ---
    private const string PlayIconGlyph = "\uE768";
    private const string PauseIconGlyph = "\uE769";
    private const string PlayTooltip = "Play";
    private const string PauseTooltip = "Pause";
    private const string RepeatOffIconGlyph = "\uE8EE";
    private const string RepeatAllIconGlyph = "\uE895";
    private const string RepeatOneIconGlyph = "\uE8ED";
    private const string ShuffleOffIconGlyph = "\uE8B1";
    private const string ShuffleOnIconGlyph = "\uE148";
    private const string MuteIconGlyph = "\uE74F";
    private const string VolumeLowIconGlyph = "\uE993";
    private const string VolumeMediumIconGlyph = "\uE994";
    private const string VolumeHighIconGlyph = "\uE767";
    private readonly DispatcherQueue _dispatcherQueue;

    // --- Services and State ---
    private readonly IMusicPlaybackService _playbackService;
    private bool _isUpdatingFromService;

    // --- Constructor and Dispose ---
    public PlayerViewModel(IMusicPlaybackService playbackService)
    {
        _playbackService = playbackService ?? throw new ArgumentNullException(nameof(playbackService));
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        SubscribeToPlaybackServiceEvents();
        InitializeStateFromService();
    }

    // --- Observable Properties ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseIconGlyph))]
    [NotifyPropertyChangedFor(nameof(PlayPauseButtonToolTip))]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty] public partial string SongTitle { get; set; } = "No track playing";

    [ObservableProperty] public partial string ArtistName { get; set; } = string.Empty;

    [ObservableProperty] public partial ImageSource? AlbumArtSource { get; set; }

    [ObservableProperty] public partial Song? CurrentPlayingTrack { get; set; }

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

    [ObservableProperty] public partial double CurrentVolume { get; set; } = 50;

    [ObservableProperty] public partial string VolumeIconGlyph { get; set; } = VolumeMediumIconGlyph;

    [ObservableProperty] public partial ObservableCollection<Song> CurrentQueue { get; set; } = new();

    [ObservableProperty] public partial double CurrentPosition { get; set; }

    [ObservableProperty] public partial string CurrentTimeText { get; set; } = "0:00";

    [ObservableProperty] public partial bool IsUserDraggingSlider { get; set; }

    [ObservableProperty] public partial double TotalDuration { get; set; }

    [ObservableProperty] public partial string TotalDurationText { get; set; } = "0:00";

    [ObservableProperty] public partial bool IsGlobalOperationInProgress { get; set; }

    [ObservableProperty] public partial string GlobalOperationStatusMessage { get; set; } = string.Empty;

    [ObservableProperty] public partial double GlobalOperationProgressValue { get; set; }

    // --- Computed Properties ---
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

    public void Dispose()
    {
        UnsubscribeFromPlaybackServiceEvents();
        GC.SuppressFinalize(this);
    }

    // --- Commands ---
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

    // --- Partial OnChanged Methods ---
    partial void OnIsMutedChanged(bool value)
    {
        UpdateVolumeIconGlyph();
    }

    partial void OnCurrentVolumeChanged(double value)
    {
        if (!_isUpdatingFromService)
        {
            var serviceVolume = Math.Clamp(value / 100.0, 0.0, 1.0);
            if (Math.Abs(_playbackService.Volume - serviceVolume) > 0.001)
                _ = _playbackService.SetVolumeAsync(serviceVolume);
        }

        UpdateVolumeIconGlyph();
    }

    partial void OnCurrentPositionChanged(double value)
    {
        CurrentTimeText = TimeSpan.FromSeconds(value).ToString(@"m\:ss");
        if (!_isUpdatingFromService && !IsUserDraggingSlider)
        {
            var newPosition = TimeSpan.FromSeconds(value);
            if (Math.Abs(_playbackService.CurrentPosition.TotalSeconds - newPosition.TotalSeconds) > 0.5)
                _ = _playbackService.SeekAsync(newPosition);
        }
    }

    partial void OnTotalDurationChanged(double value)
    {
        TotalDurationText = TimeSpan.FromSeconds(value).ToString(@"m\:ss");
    }

    // --- Event Subscription ---
    private void SubscribeToPlaybackServiceEvents()
    {
        _playbackService.PlaybackStateChanged += OnPlaybackService_PlaybackStateChanged;
        _playbackService.TrackChanged += OnPlaybackService_TrackChanged;
        _playbackService.VolumeStateChanged += OnPlaybackService_VolumeStateChanged;
        _playbackService.ShuffleModeChanged += OnPlaybackService_ShuffleModeChanged;
        _playbackService.RepeatModeChanged += OnPlaybackService_RepeatModeChanged;
        _playbackService.QueueChanged += OnPlaybackService_QueueChanged;
        _playbackService.PositionChanged += OnPlaybackService_PositionChanged;
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
    }

    // --- State Synchronization ---
    private void InitializeStateFromService()
    {
        Debug.WriteLine($"[{nameof(PlayerViewModel)}] Initializing state from service.");
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
            if (!_playbackService.IsTransitioningTrack) IsPlaying = _playbackService.IsPlaying;
        });
    }

    private void OnPlaybackService_TrackChanged()
    {
        RunOnUIThread(() =>
        {
            UpdateTrackDetails(_playbackService.CurrentTrack);
            TotalDuration = Math.Max(0, _playbackService.Duration.TotalSeconds);
            CurrentPosition = _playbackService.CurrentPosition.TotalSeconds;
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
        RunOnUIThread(() =>
        {
            if (!IsUserDraggingSlider) CurrentPosition = _playbackService.CurrentPosition.TotalSeconds;
        });
    }

    // --- Private Helper Methods ---
    private void UpdateTrackDetails(Song? song)
    {
        CurrentPlayingTrack = song;
        if (song != null)
        {
            SongTitle = song.Title;
            ArtistName = song.Artist?.Name ?? string.Empty;
            try
            {
                AlbumArtSource = !string.IsNullOrEmpty(song.AlbumArtUriFromTrack)
                    ? new BitmapImage(new Uri(song.AlbumArtUriFromTrack))
                    : null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[{nameof(PlayerViewModel)}] Error loading album art '{song.AlbumArtUriFromTrack}': {ex.Message}");
                AlbumArtSource = null;
            }
        }
        else
        {
            SongTitle = "No track playing";
            ArtistName = string.Empty;
            AlbumArtSource = null;
        }
    }

    /// <summary>
    ///     Efficiently updates the display queue by modifying the existing collection,
    ///     which allows the UI to perform incremental updates instead of a full reload.
    /// </summary>
    private void UpdateCurrentQueueDisplay()
    {
        var sourceQueue = _playbackService.IsShuffleEnabled
            ? _playbackService.ShuffledQueue
            : _playbackService.PlaybackQueue;

        var newDisplayQueue = new List<Song>();
        var currentTrack = _playbackService.CurrentTrack;

        if (currentTrack != null)
        {
            var sourceQueueList = sourceQueue.ToList();
            var currentTrackIndex = sourceQueueList.FindIndex(s => s.Id == currentTrack.Id);

            if (currentTrackIndex != -1)
            {
                newDisplayQueue.AddRange(sourceQueueList.Skip(currentTrackIndex));
                newDisplayQueue.AddRange(sourceQueueList.Take(currentTrackIndex));
            }
            else
            {
                newDisplayQueue.AddRange(sourceQueueList);
            }
        }
        else
        {
            newDisplayQueue.AddRange(sourceQueue);
        }

        // By modifying the collection in-place, we avoid replacing the instance,
        // which is significantly more performant for data-bound UI controls.
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
                <= 33 => VolumeLowIconGlyph,
                <= 66 => VolumeMediumIconGlyph,
                _ => VolumeHighIconGlyph
            };

        if (VolumeIconGlyph != newGlyph) VolumeIconGlyph = newGlyph;
    }

    /// <summary>
    ///     Safely executes an action on the UI thread and sets a flag to prevent re-entrancy from service events.
    /// </summary>
    private void RunOnUIThread(Action action)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            using (new ServiceUpdateScope(this))
            {
                action();
            }
        });
    }

    /// <summary>
    ///     A helper struct to create an exception-safe scope for service updates.
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
}