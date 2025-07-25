using CommunityToolkit.WinUI;
using LibVLCSharp.Shared;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Services.Abstractions;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Storage;
using Windows.Storage.Streams;
using WinMediaPlayback = Windows.Media.Playback;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
/// Implements <see cref="IAudioPlayer"/> using LibVLCSharp for robust audio playback
/// and manual integration with the System Media Transport Controls (SMTC).
/// </summary>
public class LibVlcAudioPlayerService : IAudioPlayer, IDisposable {
    private readonly IDispatcherService _dispatcherService;
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private SystemMediaTransportControls? _smtc;
    private Song? _currentSong;

    // A dummy MediaPlayer is required in WinUI 3 to obtain a working SMTC instance
    // that is correctly associated with the application's main window.
    private readonly WinMediaPlayback.MediaPlayer _dummyMediaPlayer;

    public LibVlcAudioPlayerService(IDispatcherService dispatcherService) {
        _dispatcherService = dispatcherService ?? throw new ArgumentNullException(nameof(dispatcherService));
        _libVlc = new LibVLC();
        _mediaPlayer = new MediaPlayer(_libVlc);
        _dummyMediaPlayer = new WinMediaPlayback.MediaPlayer();

        // Disable the command manager for the dummy player to prevent it from
        // interfering with our manual SMTC control.
        _dummyMediaPlayer.CommandManager.IsEnabled = false;

        // Map LibVLCSharp events to the IAudioPlayer interface events.
        _mediaPlayer.EndReached += OnMediaPlayerEndReached;
        _mediaPlayer.PositionChanged += OnMediaPlayerPositionChanged;
        _mediaPlayer.Playing += OnMediaPlayerStateChanged;
        _mediaPlayer.Paused += OnMediaPlayerStateChanged;
        _mediaPlayer.Stopped += OnMediaPlayerStateChanged;
        _mediaPlayer.EncounteredError += OnMediaPlayerEncounteredError;
        _mediaPlayer.MediaChanged += OnMediaPlayerMediaChanged;
    }

    #region IAudioPlayer Events

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
    public event Action? SmtcNextButtonPressed;
    /// <inheritdoc />
    public event Action? SmtcPreviousButtonPressed;

    #endregion

    #region IAudioPlayer Properties

    /// <inheritdoc />
    public bool IsPlaying => _mediaPlayer.IsPlaying;
    /// <inheritdoc />
    public TimeSpan CurrentPosition => TimeSpan.FromMilliseconds(_mediaPlayer.Time);
    /// <inheritdoc />
    public TimeSpan Duration => TimeSpan.FromMilliseconds(_mediaPlayer.Length);
    /// <inheritdoc />
    public double Volume => _mediaPlayer.Volume / 100.0;
    /// <inheritdoc />
    public bool IsMuted => _mediaPlayer.Mute;

    #endregion

    #region IAudioPlayer Methods

    /// <inheritdoc />
    public void InitializeSmtc() {
        try {
            // This method must be called on the UI thread when the window is active.
            // We get the SMTC from the dummy player, which is the reliable method in WinUI 3.
            _smtc = _dummyMediaPlayer.SystemMediaTransportControls;

            _smtc.IsPlayEnabled = true;
            _smtc.IsPauseEnabled = true;
            _smtc.IsNextEnabled = false;
            _smtc.IsPreviousEnabled = false;

            _smtc.ButtonPressed += OnSmtcButtonPressed;
            _smtc.IsEnabled = true;

            _smtc.PlaybackStatus = MediaPlaybackStatus.Stopped;
        }
        catch (Exception ex) {
            Debug.WriteLine($"[CRITICAL] LibVlcAudioPlayerService: Failed to initialize SMTC. Error: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public void UpdateSmtcButtonStates(bool canNext, bool canPrevious) {
        if (_smtc is null) return;
        _smtc.IsNextEnabled = canNext;
        _smtc.IsPreviousEnabled = canPrevious;
    }

    /// <inheritdoc />
    public Task LoadAsync(Song song) {
        if (song is null) return Task.CompletedTask;

        _currentSong = song;
        try {
            var media = new Media(_libVlc, new Uri(song.FilePath));
            _mediaPlayer.Media = media;
        }
        catch (Exception ex) {
            var errorMessage = $"Failed to load '{song.Title}': {ex.Message}";
            Debug.WriteLine($"[LibVlcAudioPlayerService] {errorMessage}");
            _dispatcherService.TryEnqueue(() => ErrorOccurred?.Invoke(errorMessage));
            _currentSong = null;
            _mediaPlayer.Media = null;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task PlayAsync() {
        if (_mediaPlayer.Media is not null) {
            _mediaPlayer.Play();
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task PauseAsync() {
        if (_mediaPlayer.CanPause) {
            _mediaPlayer.Pause();
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync() {
        _mediaPlayer.Stop();
        _mediaPlayer.Media = null;
        _currentSong = null;

        if (_smtc is not null) {
            _smtc.DisplayUpdater.ClearAll();
            _smtc.DisplayUpdater.Update();
            _smtc.PlaybackStatus = MediaPlaybackStatus.Stopped;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SeekAsync(TimeSpan position) {
        if (_mediaPlayer.IsSeekable) {
            _mediaPlayer.Time = (long)position.TotalMilliseconds;
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SetVolumeAsync(double volume) {
        _mediaPlayer.Volume = (int)Math.Clamp(volume * 100, 0, 100);
        // Manually invoke VolumeChanged since LibVLCSharp does not provide a dedicated event.
        _dispatcherService.TryEnqueue(() => VolumeChanged?.Invoke());
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SetMuteAsync(bool isMuted) {
        _mediaPlayer.Mute = isMuted;
        // Manually invoke VolumeChanged since LibVLCSharp does not provide a dedicated event.
        _dispatcherService.TryEnqueue(() => VolumeChanged?.Invoke());
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() {
        _mediaPlayer.EndReached -= OnMediaPlayerEndReached;
        _mediaPlayer.PositionChanged -= OnMediaPlayerPositionChanged;
        _mediaPlayer.Playing -= OnMediaPlayerStateChanged;
        _mediaPlayer.Paused -= OnMediaPlayerStateChanged;
        _mediaPlayer.Stopped -= OnMediaPlayerStateChanged;
        _mediaPlayer.EncounteredError -= OnMediaPlayerEncounteredError;
        _mediaPlayer.MediaChanged -= OnMediaPlayerMediaChanged;

        if (_smtc is not null) {
            _smtc.ButtonPressed -= OnSmtcButtonPressed;
            _smtc.IsEnabled = false;
        }

        _mediaPlayer.Stop();
        _mediaPlayer.Dispose();
        _libVlc.Dispose();
        _dummyMediaPlayer.Dispose();

        GC.SuppressFinalize(this);
    }

    #endregion

    private void OnMediaPlayerMediaChanged(object? sender, MediaPlayerMediaChangedEventArgs e) {
        // This event confirms the media is loaded and ready for playback commands.
        _dispatcherService.TryEnqueue(() => MediaOpened?.Invoke());
        _ = UpdateSmtcDisplayAsync();
    }

    private void OnMediaPlayerEncounteredError(object? sender, EventArgs e) {
        var errorMessage = "LibVLC encountered an unspecified error.";
        var lastVlcError = _libVlc.LastLibVLCError;
        if (!string.IsNullOrEmpty(lastVlcError)) {
            errorMessage = $"LibVLC Error: {lastVlcError}";
        }

        Debug.WriteLine($"[LibVlcAudioPlayerService] {errorMessage}");
        _dispatcherService.TryEnqueue(() => ErrorOccurred?.Invoke(errorMessage));
    }

    private void OnMediaPlayerStateChanged(object? sender, EventArgs e) {
        _dispatcherService.TryEnqueue(() => StateChanged?.Invoke());
        UpdateSmtcPlaybackStatus();
    }

    private void OnMediaPlayerPositionChanged(object? sender, MediaPlayerPositionChangedEventArgs e) {
        _dispatcherService.TryEnqueue(() => PositionChanged?.Invoke());
    }

    private void OnMediaPlayerEndReached(object? sender, EventArgs e) {
        _dispatcherService.TryEnqueue(() => PlaybackEnded?.Invoke());
    }

    private async void OnSmtcButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args) {
        // Marshal to the dispatcher queue to ensure thread-safe interaction with the player.
        await _dispatcherService.EnqueueAsync(async () => {
            switch (args.Button) {
                case SystemMediaTransportControlsButton.Play:
                    await PlayAsync();
                    break;
                case SystemMediaTransportControlsButton.Pause:
                    await PauseAsync();
                    break;
                case SystemMediaTransportControlsButton.Next:
                    SmtcNextButtonPressed?.Invoke();
                    break;
                case SystemMediaTransportControlsButton.Previous:
                    SmtcPreviousButtonPressed?.Invoke();
                    break;
            }
        });
    }

    private void UpdateSmtcPlaybackStatus() {
        if (_smtc is null) return;

        _smtc.PlaybackStatus = _mediaPlayer.State switch {
            VLCState.Playing => MediaPlaybackStatus.Playing,
            VLCState.Paused => MediaPlaybackStatus.Paused,
            VLCState.Stopped or VLCState.Ended or VLCState.Error => MediaPlaybackStatus.Stopped,
            VLCState.Opening or VLCState.Buffering => MediaPlaybackStatus.Changing,
            _ => MediaPlaybackStatus.Closed
        };
    }

    private async Task UpdateSmtcDisplayAsync() {
        if (_smtc is null || _currentSong is null) return;

        var updater = _smtc.DisplayUpdater;
        updater.Type = MediaPlaybackType.Music;
        updater.MusicProperties.Title = _currentSong.Title ?? string.Empty;
        updater.MusicProperties.Artist = _currentSong.Artist?.Name ?? "Unknown Artist";
        updater.MusicProperties.AlbumArtist = _currentSong.Album?.Artist?.Name ?? _currentSong.Artist?.Name ?? "Unknown Album Artist";
        updater.MusicProperties.AlbumTitle = _currentSong.Album?.Title ?? "Unknown Album";
        updater.Thumbnail = null;

        if (!string.IsNullOrEmpty(_currentSong.AlbumArtUriFromTrack)) {
            try {
                var albumArtFile = await StorageFile.GetFileFromPathAsync(_currentSong.AlbumArtUriFromTrack);
                updater.Thumbnail = RandomAccessStreamReference.CreateFromFile(albumArtFile);
            }
            catch (Exception ex) {
                Debug.WriteLine($"[LibVlcAudioPlayerService] Failed to set SMTC thumbnail: {ex.Message}");
            }
        }

        updater.Update();
    }
}