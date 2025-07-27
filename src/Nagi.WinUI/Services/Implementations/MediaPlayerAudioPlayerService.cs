using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Services.Abstractions;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
/// Implements <see cref="IAudioPlayer"/> using <see cref="Windows.Media.Playback.MediaPlayer"/> for WinUI 3.
/// This service integrates automatically with the System Media Transport Controls (SMTC)
/// via the <see cref="MediaPlayer.CommandManager"/>.
/// </summary>
public class MediaPlayerAudioPlayerService : IAudioPlayer, IDisposable {
    private readonly IDispatcherService _dispatcherService;
    private readonly MediaPlayer _mediaPlayer;
    private MediaPlaybackSession? _currentPlaybackSession;
    private Song? _currentSong;
    private SystemMediaTransportControls? _smtc;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaPlayerAudioPlayerService"/> class.
    /// </summary>
    /// <param name="dispatcherService">The service for dispatching actions to the UI thread.</param>
    public MediaPlayerAudioPlayerService(IDispatcherService dispatcherService) {
        _dispatcherService = dispatcherService ?? throw new ArgumentNullException(nameof(dispatcherService));

        Debug.WriteLine("[MediaPlayerAudioPlayerService] INFO: Initializing MediaPlayer.");
        _mediaPlayer = new MediaPlayer();
        _mediaPlayer.MediaEnded += MediaPlayer_MediaEnded;
        _mediaPlayer.CurrentStateChanged += MediaPlayer_CurrentStateChanged;
        _mediaPlayer.VolumeChanged += MediaPlayer_VolumeChanged;
        _mediaPlayer.MediaFailed += MediaPlayer_MediaFailed;
        _mediaPlayer.MediaOpened += MediaPlayer_MediaOpened;

        _mediaPlayer.Volume = 0.5;
        _mediaPlayer.IsMuted = false;
    }

    /// <inheritdoc />
    public bool IsPlaying => _mediaPlayer.PlaybackSession?.PlaybackState == MediaPlaybackState.Playing;

    /// <inheritdoc />
    public TimeSpan CurrentPosition => _mediaPlayer.PlaybackSession?.Position ?? TimeSpan.Zero;

    /// <inheritdoc />
    public TimeSpan Duration => _mediaPlayer.PlaybackSession?.NaturalDuration ?? TimeSpan.Zero;

    /// <inheritdoc />
    public double Volume => _mediaPlayer.Volume;

    /// <inheritdoc />
    public bool IsMuted => _mediaPlayer.IsMuted;

    /// <inheritdoc />
    public event Action? PlaybackEnded;

    /// <inheritdoc />
    public event Action? PositionChanged;

    /// <inheritdoc />
    public event Action? StateChanged;

    /// <inheritdoc />
    public event Action? VolumeChanged;

    /// <inheritdoc />
    public event Action<string>? ErrorOccurred;

    /// <inheritdoc />
    public event Action? MediaOpened;

    /// <inheritdoc />
    public event Action? DurationChanged;

    /// <inheritdoc />
    public event Action? SmtcNextButtonPressed;

    /// <inheritdoc />
    public event Action? SmtcPreviousButtonPressed;

    /// <inheritdoc />
    public void InitializeSmtc() {
        Debug.WriteLine("[MediaPlayerAudioPlayerService] INFO: Initializing SMTC CommandManager.");
        _mediaPlayer.CommandManager.IsEnabled = true;
        _smtc = _mediaPlayer.SystemMediaTransportControls;

        _mediaPlayer.CommandManager.NextReceived += CommandManager_NextReceived;
        _mediaPlayer.CommandManager.PreviousReceived += CommandManager_PreviousReceived;
    }

    /// <inheritdoc />
    public void UpdateSmtcButtonStates(bool canNext, bool canPrevious) {
        if (_mediaPlayer.CommandManager == null) return;

        var newNextRule = canNext ? MediaCommandEnablingRule.Always : MediaCommandEnablingRule.Never;
        if (_mediaPlayer.CommandManager.NextBehavior.EnablingRule != newNextRule) {
            _mediaPlayer.CommandManager.NextBehavior.EnablingRule = newNextRule;
        }

        var newPreviousRule = canPrevious ? MediaCommandEnablingRule.Always : MediaCommandEnablingRule.Never;
        if (_mediaPlayer.CommandManager.PreviousBehavior.EnablingRule != newPreviousRule) {
            _mediaPlayer.CommandManager.PreviousBehavior.EnablingRule = newPreviousRule;
        }
    }

    /// <inheritdoc />
    public async Task LoadAsync(Song song) {
        if (song == null) return;

        _currentSong = song;
        try {
            Debug.WriteLine($"[MediaPlayerAudioPlayerService] INFO: Loading media from path: {song.FilePath}");
            var file = await StorageFile.GetFileFromPathAsync(song.FilePath);
            _mediaPlayer.Source = MediaSource.CreateFromStorageFile(file);
        }
        catch (Exception ex) {
            var errorMessage = $"Failed to load '{song.Title}': {ex.Message}";
            Debug.WriteLine($"[MediaPlayerAudioPlayerService] ERROR: {errorMessage}");
            _dispatcherService.TryEnqueue(() => ErrorOccurred?.Invoke(errorMessage));
            _currentSong = null;
            _mediaPlayer.Source = null;
        }
    }

    /// <inheritdoc />
    public async Task PlayAsync() {
        if (_mediaPlayer.Source == null && _currentSong != null) {
            Debug.WriteLine("[MediaPlayerAudioPlayerService] WARN: PlayAsync called with no source. Attempting to reload current song.");
            await LoadAsync(_currentSong);
            if (_mediaPlayer.Source == null) return;
        }

        if (_mediaPlayer.Source != null) {
            _mediaPlayer.Play();
        }
    }

    /// <inheritdoc />
    public Task PauseAsync() {
        if (_mediaPlayer.Source != null && _mediaPlayer.CanPause) {
            _mediaPlayer.Pause();
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync() {
        Debug.WriteLine("[MediaPlayerAudioPlayerService] INFO: Stop command received.");
        _mediaPlayer.Source = null;
        _currentSong = null;

        if (_smtc != null) {
            _smtc.DisplayUpdater.ClearAll();
            _smtc.DisplayUpdater.Update();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SeekAsync(TimeSpan position) {
        if (_mediaPlayer.PlaybackSession?.CanSeek == true) {
            _mediaPlayer.PlaybackSession.Position = position;
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SetVolumeAsync(double volume) {
        _mediaPlayer.Volume = Math.Clamp(volume, 0.0, 1.0);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SetMuteAsync(bool isMuted) {
        _mediaPlayer.IsMuted = isMuted;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() {
        Debug.WriteLine("[MediaPlayerAudioPlayerService] INFO: Disposing service.");
        _mediaPlayer.MediaEnded -= MediaPlayer_MediaEnded;
        _mediaPlayer.CurrentStateChanged -= MediaPlayer_CurrentStateChanged;
        _mediaPlayer.VolumeChanged -= MediaPlayer_VolumeChanged;
        _mediaPlayer.MediaFailed -= MediaPlayer_MediaFailed;
        _mediaPlayer.MediaOpened -= MediaPlayer_MediaOpened;

        if (_currentPlaybackSession != null) {
            _currentPlaybackSession.PositionChanged -= PlaybackSession_PositionChanged;
            _currentPlaybackSession.NaturalDurationChanged -= PlaybackSession_NaturalDurationChanged;
        }

        if (_mediaPlayer.CommandManager != null) {
            _mediaPlayer.CommandManager.NextReceived -= CommandManager_NextReceived;
            _mediaPlayer.CommandManager.PreviousReceived -= CommandManager_PreviousReceived;
        }

        _mediaPlayer.Dispose();

        GC.SuppressFinalize(this);
        Debug.WriteLine("[MediaPlayerAudioPlayerService] INFO: Service disposed.");
    }

    private void MediaPlayer_MediaEnded(MediaPlayer sender, object args) {
        Debug.WriteLine("[MediaPlayerAudioPlayerService] INFO: MediaEnded event fired.");
        _dispatcherService.TryEnqueue(() => PlaybackEnded?.Invoke());
    }

    private void MediaPlayer_CurrentStateChanged(MediaPlayer sender, object args) {
        Debug.WriteLine($"[MediaPlayerAudioPlayerService] INFO: CurrentStateChanged event fired. New state: {sender.PlaybackSession?.PlaybackState}");
        _dispatcherService.TryEnqueue(() => StateChanged?.Invoke());

        if (_smtc != null) {
            var newStatus = sender.PlaybackSession?.PlaybackState switch {
                MediaPlaybackState.Playing => MediaPlaybackStatus.Playing,
                MediaPlaybackState.Paused => MediaPlaybackStatus.Paused,
                MediaPlaybackState.None => MediaPlaybackStatus.Stopped,
                MediaPlaybackState.Opening or MediaPlaybackState.Buffering => MediaPlaybackStatus.Changing,
                _ => MediaPlaybackStatus.Closed
            };

            if (_smtc.PlaybackStatus != newStatus) {
                _smtc.PlaybackStatus = newStatus;
            }
        }
    }

    private void MediaPlayer_VolumeChanged(MediaPlayer sender, object args) {
        _dispatcherService.TryEnqueue(() => VolumeChanged?.Invoke());
    }

    private void PlaybackSession_PositionChanged(MediaPlaybackSession sender, object args) {
        _dispatcherService.TryEnqueue(() => PositionChanged?.Invoke());
    }

    private void PlaybackSession_NaturalDurationChanged(MediaPlaybackSession sender, object args) {
        Debug.WriteLine($"[MediaPlayerAudioPlayerService] INFO: NaturalDurationChanged event fired. New duration: {sender.NaturalDuration}.");
        _dispatcherService.TryEnqueue(() => DurationChanged?.Invoke());
    }

    private void MediaPlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) {
        var errorMessage = $"Media playback failed. Error: {args.Error}, Message: {args.ErrorMessage}";
        Debug.WriteLine($"[MediaPlayerAudioPlayerService] ERROR: {errorMessage}");
        _dispatcherService.TryEnqueue(() => ErrorOccurred?.Invoke(errorMessage));
        _dispatcherService.TryEnqueue(async () => await StopAsync());
    }

    private async void MediaPlayer_MediaOpened(MediaPlayer sender, object args) {
        Debug.WriteLine("[MediaPlayerAudioPlayerService] INFO: MediaOpened event fired.");

        if (_currentPlaybackSession != null) {
            _currentPlaybackSession.PositionChanged -= PlaybackSession_PositionChanged;
            _currentPlaybackSession.NaturalDurationChanged -= PlaybackSession_NaturalDurationChanged;
        }

        _currentPlaybackSession = sender.PlaybackSession;
        if (_currentPlaybackSession != null) {
            _currentPlaybackSession.PositionChanged += PlaybackSession_PositionChanged;
            _currentPlaybackSession.NaturalDurationChanged += PlaybackSession_NaturalDurationChanged;
        }

        _dispatcherService.TryEnqueue(() => MediaOpened?.Invoke());

        if (_currentPlaybackSession?.NaturalDuration > TimeSpan.Zero) {
            PlaybackSession_NaturalDurationChanged(_currentPlaybackSession, args);
        }

        await UpdateSmtcDisplayAsync();
    }



    private async Task UpdateSmtcDisplayAsync() {
        if (_smtc == null || _currentSong == null) return;

        Debug.WriteLine($"[MediaPlayerAudioPlayerService] INFO: Updating SMTC display for track '{_currentSong.Title}'.");
        var updater = _smtc.DisplayUpdater;
        updater.Type = MediaPlaybackType.Music;
        updater.MusicProperties.Title = _currentSong.Title ?? string.Empty;
        updater.MusicProperties.Artist = _currentSong.Artist?.Name ?? "Unknown Artist";
        updater.MusicProperties.AlbumArtist =
            _currentSong.Album?.Artist?.Name ?? _currentSong.Artist?.Name ?? "Unknown Album Artist";
        updater.MusicProperties.AlbumTitle = _currentSong.Album?.Title ?? "Unknown Album";

        if (!string.IsNullOrEmpty(_currentSong.AlbumArtUriFromTrack)) {
            try {
                var albumArtFile = await StorageFile.GetFileFromPathAsync(_currentSong.AlbumArtUriFromTrack);
                updater.Thumbnail = RandomAccessStreamReference.CreateFromFile(albumArtFile);
            }
            catch (Exception ex) {
                Debug.WriteLine($"[MediaPlayerAudioPlayerService] WARN: Error setting SMTC thumbnail: {ex.Message}");
                updater.Thumbnail = null;
            }
        }
        else {
            updater.Thumbnail = null;
        }

        updater.Update();
    }

    private void CommandManager_NextReceived(MediaPlaybackCommandManager sender,
        MediaPlaybackCommandManagerNextReceivedEventArgs args) {
        Debug.WriteLine("[MediaPlayerAudioPlayerService] INFO: SMTC Next command received.");
        var deferral = args.GetDeferral();
        _dispatcherService.TryEnqueue(() => {
            SmtcNextButtonPressed?.Invoke();
            args.Handled = true;
            deferral.Complete();
        });
    }

    private void CommandManager_PreviousReceived(MediaPlaybackCommandManager sender,
        MediaPlaybackCommandManagerPreviousReceivedEventArgs args) {
        Debug.WriteLine("[MediaPlayerAudioPlayerService] INFO: SMTC Previous command received.");
        var deferral = args.GetDeferral();
        _dispatcherService.TryEnqueue(() => {
            SmtcPreviousButtonPressed?.Invoke();
            args.Handled = true;
            deferral.Complete();
        });
    }
}