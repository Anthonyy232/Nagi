using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
///     Implements IAudioPlayer using Windows.Media.Playback.MediaPlayer for WinUI 3,
///     integrating with System Media Transport Controls (SMTC) via MediaPlayer.CommandManager.
/// </summary>
public class MediaPlayerAudioPlayerService : IAudioPlayer
{
    private readonly IDispatcherService _dispatcherService;
    private readonly MediaPlayer _mediaPlayer;
    private MediaPlaybackSession? _currentPlaybackSession;
    private Song? _currentSong;
    private SystemMediaTransportControls? _smtc;

    public MediaPlayerAudioPlayerService(IDispatcherService dispatcherService)
    {
        _dispatcherService = dispatcherService ?? throw new ArgumentNullException(nameof(dispatcherService));

        _mediaPlayer = new MediaPlayer();
        _mediaPlayer.MediaEnded += MediaPlayer_MediaEnded;
        _mediaPlayer.CurrentStateChanged += MediaPlayer_CurrentStateChanged;
        _mediaPlayer.VolumeChanged += MediaPlayer_VolumeChanged;
        _mediaPlayer.MediaFailed += MediaPlayer_MediaFailed;
        _mediaPlayer.MediaOpened += MediaPlayer_MediaOpened;

        // Set default volume and mute state.
        _mediaPlayer.Volume = 0.5;
        _mediaPlayer.IsMuted = false;
    }

    public bool IsPlaying => _mediaPlayer.CurrentState == MediaPlayerState.Playing;
    public TimeSpan CurrentPosition => _mediaPlayer.PlaybackSession?.Position ?? TimeSpan.Zero;
    public TimeSpan Duration => _mediaPlayer.PlaybackSession?.NaturalDuration ?? TimeSpan.Zero;
    public double Volume => _mediaPlayer.Volume;
    public bool IsMuted => _mediaPlayer.IsMuted;

    public event Action? PlaybackEnded;
    public event Action? PositionChanged;
    public event Action? StateChanged;
    public event Action? VolumeChanged;
    public event Action<string>? ErrorOccurred;
    public event Action? MediaOpened;
    public event Action? SmtcNextButtonPressed;
    public event Action? SmtcPreviousButtonPressed;

    /// <summary>
    ///     Initializes automatic SMTC integration provided by the MediaPlayer's CommandManager.
    /// </summary>
    public void InitializeSmtc()
    {
        _mediaPlayer.CommandManager.IsEnabled = true;
        _smtc = _mediaPlayer.SystemMediaTransportControls;

        // Subscribe to CommandManager events to forward SMTC button presses to the MusicPlaybackService.
        // The player automatically handles Play/Pause commands. We only need to forward commands
        // that require queue logic (Next/Previous).
        _mediaPlayer.CommandManager.NextReceived += CommandManager_NextReceived;
        _mediaPlayer.CommandManager.PreviousReceived += CommandManager_PreviousReceived;
    }

    public void UpdateSmtcButtonStates(bool canNext, bool canPrevious)
    {
        if (_mediaPlayer.CommandManager == null) return;

        // The enabling rule must be set to Always because the MediaPlayer itself
        // is only aware of a single media source and doesn't know about our custom queue.
        // The application logic determines if next/previous is possible.
        var newNextRule = canNext ? MediaCommandEnablingRule.Always : MediaCommandEnablingRule.Never;
        if (_mediaPlayer.CommandManager.NextBehavior.EnablingRule != newNextRule)
            _mediaPlayer.CommandManager.NextBehavior.EnablingRule = newNextRule;

        var newPreviousRule = canPrevious ? MediaCommandEnablingRule.Always : MediaCommandEnablingRule.Never;
        if (_mediaPlayer.CommandManager.PreviousBehavior.EnablingRule != newPreviousRule)
            _mediaPlayer.CommandManager.PreviousBehavior.EnablingRule = newPreviousRule;
    }

    public async Task LoadAsync(Song song)
    {
        if (song == null) return;

        _currentSong = song;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(song.FilePath);
            _mediaPlayer.Source = MediaSource.CreateFromStorageFile(file);
        }
        catch (Exception ex)
        {
            var errorMessage = $"Failed to load '{song.Title}': {ex.Message}";
            Debug.WriteLine($"[MediaPlayerAudioPlayerService] {errorMessage}");
            ErrorOccurred?.Invoke(errorMessage);
            _currentSong = null;
            _mediaPlayer.Source = null;
        }
    }

    public async Task PlayAsync()
    {
        // If the source is not set, but we have a current song, attempt to load it first.
        if (_mediaPlayer.Source == null && _currentSong != null)
        {
            await LoadAsync(_currentSong);
            // If loading failed, LoadAsync would have already raised an error event. Do not proceed.
            if (_mediaPlayer.Source == null) return;
        }

        if (_mediaPlayer.Source != null) _mediaPlayer.Play();
    }

    public Task PauseAsync()
    {
        if (_mediaPlayer.Source != null && _mediaPlayer.CanPause) _mediaPlayer.Pause();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // Setting the source to null effectively stops and unloads the media.
        _mediaPlayer.Source = null;
        _currentSong = null;

        // Clear the SMTC display when playback is stopped.
        if (_smtc != null)
        {
            _smtc.DisplayUpdater.ClearAll();
            _smtc.DisplayUpdater.Update();
        }

        return Task.CompletedTask;
    }

    public Task SeekAsync(TimeSpan position)
    {
        if (_mediaPlayer.PlaybackSession?.CanSeek == true) _mediaPlayer.PlaybackSession.Position = position;
        return Task.CompletedTask;
    }

    public Task SetVolumeAsync(double volume)
    {
        _mediaPlayer.Volume = Math.Clamp(volume, 0.0, 1.0);
        return Task.CompletedTask;
    }

    public Task SetMuteAsync(bool isMuted)
    {
        _mediaPlayer.IsMuted = isMuted;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_mediaPlayer != null)
        {
            _mediaPlayer.MediaEnded -= MediaPlayer_MediaEnded;
            _mediaPlayer.CurrentStateChanged -= MediaPlayer_CurrentStateChanged;
            _mediaPlayer.VolumeChanged -= MediaPlayer_VolumeChanged;
            _mediaPlayer.MediaFailed -= MediaPlayer_MediaFailed;
            _mediaPlayer.MediaOpened -= MediaPlayer_MediaOpened;

            if (_currentPlaybackSession != null)
                _currentPlaybackSession.PositionChanged -= PlaybackSession_PositionChanged;

            if (_mediaPlayer.CommandManager != null)
            {
                _mediaPlayer.CommandManager.NextReceived -= CommandManager_NextReceived;
                _mediaPlayer.CommandManager.PreviousReceived -= CommandManager_PreviousReceived;
            }

            _mediaPlayer.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private void MediaPlayer_MediaEnded(MediaPlayer sender, object args)
    {
        _dispatcherService.TryEnqueue(() => PlaybackEnded?.Invoke());
    }

    private void MediaPlayer_CurrentStateChanged(MediaPlayer sender, object args)
    {
        _dispatcherService.TryEnqueue(() => StateChanged?.Invoke());

        // Update the SMTC playback status to match the player's state.
        if (_smtc != null)
            _smtc.PlaybackStatus = sender.CurrentState switch
            {
                MediaPlayerState.Playing => MediaPlaybackStatus.Playing,
                MediaPlayerState.Paused => MediaPlaybackStatus.Paused,
                MediaPlayerState.Stopped => MediaPlaybackStatus.Stopped,
                MediaPlayerState.Opening or MediaPlayerState.Buffering => MediaPlaybackStatus.Changing,
                MediaPlayerState.Closed => MediaPlaybackStatus.Closed,
                _ => MediaPlaybackStatus.Closed
            };
    }

    private void MediaPlayer_VolumeChanged(MediaPlayer sender, object args)
    {
        _dispatcherService.TryEnqueue(() => VolumeChanged?.Invoke());
    }

    private void PlaybackSession_PositionChanged(MediaPlaybackSession sender, object args)
    {
        _dispatcherService.TryEnqueue(() => PositionChanged?.Invoke());
    }

    private void MediaPlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        var errorMessage = $"Media playback failed. Error: {args.Error}, Message: {args.ErrorMessage}";
        Debug.WriteLine($"[MediaPlayerAudioPlayerService] {errorMessage}");
        _dispatcherService.TryEnqueue(() => ErrorOccurred?.Invoke(errorMessage));
        _dispatcherService.TryEnqueue(async () => await StopAsync());
    }

    private async void MediaPlayer_MediaOpened(MediaPlayer sender, object args)
    {
        // Ensure PositionChanged events are subscribed to the correct playback session.
        if (_currentPlaybackSession != null) _currentPlaybackSession.PositionChanged -= PlaybackSession_PositionChanged;
        _currentPlaybackSession = sender.PlaybackSession;
        if (_currentPlaybackSession != null) _currentPlaybackSession.PositionChanged += PlaybackSession_PositionChanged;

        _dispatcherService.TryEnqueue(() => MediaOpened?.Invoke());
        await UpdateSmtcDisplayAsync();
    }

    private async Task UpdateSmtcDisplayAsync()
    {
        if (_smtc == null || _currentSong == null) return;

        var updater = _smtc.DisplayUpdater;
        updater.Type = MediaPlaybackType.Music;
        updater.MusicProperties.Title = _currentSong.Title ?? string.Empty;
        updater.MusicProperties.Artist = _currentSong.Artist?.Name ?? "Unknown Artist";
        updater.MusicProperties.AlbumArtist =
            _currentSong.Album?.Artist?.Name ?? _currentSong.Artist?.Name ?? "Unknown Album Artist";
        updater.MusicProperties.AlbumTitle = _currentSong.Album?.Title ?? "Unknown Album";

        if (!string.IsNullOrEmpty(_currentSong.AlbumArtUriFromTrack))
            try
            {
                var albumArtFile = await StorageFile.GetFileFromPathAsync(_currentSong.AlbumArtUriFromTrack);
                updater.Thumbnail = RandomAccessStreamReference.CreateFromFile(albumArtFile);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaPlayerAudioPlayerService] Error setting SMTC thumbnail: {ex.Message}");
                updater.Thumbnail = null;
            }
        else
            updater.Thumbnail = null;

        updater.Update();
    }

    private void CommandManager_NextReceived(MediaPlaybackCommandManager sender,
        MediaPlaybackCommandManagerNextReceivedEventArgs args)
    {
        var deferral = args.GetDeferral();
        _dispatcherService.TryEnqueue(() =>
        {
            SmtcNextButtonPressed?.Invoke();
            args.Handled = true;
            deferral.Complete();
        });
    }

    private void CommandManager_PreviousReceived(MediaPlaybackCommandManager sender,
        MediaPlaybackCommandManagerPreviousReceivedEventArgs args)
    {
        var deferral = args.GetDeferral();
        _dispatcherService.TryEnqueue(() =>
        {
            SmtcPreviousButtonPressed?.Invoke();
            args.Handled = true;
            deferral.Complete();
        });
    }
}