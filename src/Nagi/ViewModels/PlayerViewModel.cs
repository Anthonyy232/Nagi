using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Nagi.Models;
using Nagi.Services.Abstractions;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Nagi.ViewModels;

/// <summary>
/// Manages the state and logic for the music player UI, including playback controls,
/// track information, and the song queue. It interfaces with the IMusicPlaybackService
/// to control playback and reflects its state to the UI.
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

    // Constants for Image Effects
    private const float AlbumArtBlurAmount = 10.0f;
    private const float AlbumArtBlurScaleFactor = 1.05f;

    private readonly IMusicPlaybackService _playbackService;
    private readonly DispatcherQueue _dispatcherQueue;
    private bool _isUpdatingFromService;

    // Manages cancellation for the asynchronous blur generation task.
    private CancellationTokenSource? _blurGenerationCts;

    public PlayerViewModel(IMusicPlaybackService playbackService) {
        _playbackService = playbackService ?? throw new ArgumentNullException(nameof(playbackService));
        // Ensure you get the DispatcherQueue from the UI thread context.
        // For ViewModels, if created on the UI thread, this is fine.
        // If created on a background thread, you'd need to pass it in or ensure it's accessed correctly.
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
    private ImageSource? _albumArtBlurredSource;

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
        _blurGenerationCts?.Cancel();
        _blurGenerationCts?.Dispose();
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
        if (!_isUpdatingFromService) {
            var serviceVolume = Math.Clamp(value / 100.0, 0.0, 1.0);
            // Check for a meaningful change to avoid a feedback loop with the service.
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
            // Only seek if the position has changed by a noticeable amount.
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
        CurrentPlayingTrack = song; // Update the current playing track object immediately

        // Always update text details immediately
        SongTitle = song?.Title ?? "No track playing";
        ArtistName = song?.Artist?.Name ?? string.Empty;

        // Cancel any pending blur generation for the previous track.
        // This is important to ensure the new blur generation starts clean.
        _blurGenerationCts?.Cancel();
        _blurGenerationCts?.Dispose();
        _blurGenerationCts = new CancellationTokenSource();
        var token = _blurGenerationCts.Token;

        if (song != null && !string.IsNullOrEmpty(song.AlbumArtUriFromTrack)) {
            try {
                var uri = new Uri(song.AlbumArtUriFromTrack);

                // Update the non-blurred album art immediately.
                // BitmapImage handles its own asynchronous loading.
                AlbumArtSource = new BitmapImage(uri);

                // Start generating the blurred image.
                // Pass the song ID to ensure we only apply the blur if it's still the current track.
                _ = GenerateBlurredImageAsync(uri, song.Id, token);
            }
            catch (Exception ex) {
                Debug.WriteLine($"[{nameof(PlayerViewModel)}] Error processing album art URI '{song.AlbumArtUriFromTrack}': {ex.Message}");
                // If there's an error, clear both to avoid showing incorrect art.
                AlbumArtSource = null;
                AlbumArtBlurredSource = null;
            }
        }
        else {
            // If no song or no art URI, clear existing art.
            AlbumArtSource = null;
            AlbumArtBlurredSource = null;
        }
    }

    /// <summary>
    /// Generates a blurred version of the album art asynchronously. This method is designed
    /// to be cancellable and artifact-free.
    /// </summary>
    /// <param name="imageUri">The URI of the source image.</param>
    /// <param name="songId">The ID of the song this image belongs to. Used to prevent applying old blur results.</param>
    /// <param name="token">Cancellation token to stop processing if a new track starts.</param>
    private async Task GenerateBlurredImageAsync(Uri imageUri, Guid songId, CancellationToken token) {
        try {
            var blurredBitmap = await Task.Run(async () => {
                token.ThrowIfCancellationRequested();

                IRandomAccessStream sourceStream;
                if (imageUri.IsFile) {
                    var file = await StorageFile.GetFileFromPathAsync(imageUri.LocalPath);
                    sourceStream = await file.OpenAsync(FileAccessMode.Read);
                }
                else {
                    // For network or appx URIs
                    sourceStream = await RandomAccessStreamReference.CreateFromUri(imageUri).OpenReadAsync();
                }

                using (sourceStream) {
                    var device = CanvasDevice.GetSharedDevice();

                    // Load the image into a Win2D CanvasBitmap.
                    using var canvasBitmap = await CanvasBitmap.LoadAsync(device, sourceStream);
                    token.ThrowIfCancellationRequested();

                    // Create a render target with the exact same dimensions as the source.
                    using var renderTarget = new CanvasRenderTarget(canvasBitmap, canvasBitmap.Size);

                    // Perform the blur using a multi-step process to avoid edge artifacts.
                    using (var ds = renderTarget.CreateDrawingSession()) {
                        // First, clear the background. This prevents the blur's soft edges from
                        // blending with a transparent background, which can cause dark borders.
                        ds.Clear(Colors.Black);

                        // Second, scale the source image up slightly to create a "bleed" margin.
                        // This gives the blur algorithm real pixel data to sample at the edges.
                        var scaleEffect = new Transform2DEffect {
                            Source = canvasBitmap,
                            TransformMatrix = Matrix3x2.CreateScale(
                                AlbumArtBlurScaleFactor,
                                AlbumArtBlurScaleFactor,
                                canvasBitmap.Size.ToVector2() / 2)
                        };

                        // Third, apply the blur to the scaled-up image.
                        var blurEffect = new GaussianBlurEffect {
                            Source = scaleEffect,
                            BlurAmount = AlbumArtBlurAmount,
                            Optimization = EffectOptimization.Quality,
                            BorderMode = EffectBorderMode.Hard
                        };

                        // Finally, draw the result. This implicitly crops the oversized blurred
                        // image to the original dimensions of the render target, trimming the bleed margin.
                        ds.DrawImage(blurEffect);
                    }
                    token.ThrowIfCancellationRequested();

                    // Create a SoftwareBitmap from the result for UI display.
                    var pixelBytes = renderTarget.GetPixelBytes();
                    return SoftwareBitmap.CreateCopyFromBuffer(
                        pixelBytes.AsBuffer(),
                        BitmapPixelFormat.Bgra8,
                        (int)renderTarget.SizeInPixels.Width,
                        (int)renderTarget.SizeInPixels.Height,
                        BitmapAlphaMode.Premultiplied);
                }
            }, token);

            if (blurredBitmap == null || token.IsCancellationRequested) return;

            // Update the UI on the dispatcher thread.
            await _dispatcherQueue.EnqueueAsync(async () => {
                if (token.IsCancellationRequested) return;

                // IMPORTANT: Before applying the blur, check if the song it was generated for
                // is still the currently playing track. This prevents race conditions where
                // an older blur finishes after a new track has already started playing.
                if (CurrentPlayingTrack?.Id != songId) {
                    Debug.WriteLine($"[{nameof(PlayerViewModel)}] Skipping blur update for old track ID: {songId}. Current: {CurrentPlayingTrack?.Id}");
                    return;
                }

                var source = new SoftwareBitmapSource();
                await source.SetBitmapAsync(blurredBitmap);
                AlbumArtBlurredSource = source;
            }, DispatcherQueuePriority.Normal);
        }
        catch (OperationCanceledException) {
            Debug.WriteLine($"[{nameof(PlayerViewModel)}] Blur generation for '{imageUri}' was cancelled. (This is normal)");
        }
        catch (Exception ex) {
            Debug.WriteLine($"[{nameof(PlayerViewModel)}] Failed to generate blurred image for URI '{imageUri}': {ex}");
            // On error, ensure the blurred source is cleared
            _dispatcherQueue.TryEnqueue(() => AlbumArtBlurredSource = null);
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