using LibVLCSharp;
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
public sealed class LibVlcAudioPlayerService : IAudioPlayer, IDisposable {
    private readonly IDispatcherService _dispatcherService;
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private SystemMediaTransportControls? _smtc;
    private Song? _currentSong;

    // A dummy MediaPlayer from the Windows.Media.Playback namespace is required in WinUI 3
    // to obtain a working SMTC instance that is correctly associated with the application's main window.
    private readonly WinMediaPlayback.MediaPlayer _dummyMediaPlayer;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibVlcAudioPlayerService"/> class.
    /// </summary>
    /// <param name="dispatcherService">The service for dispatching actions to the UI thread.</param>
    public LibVlcAudioPlayerService(IDispatcherService dispatcherService) {
        _dispatcherService = dispatcherService ?? throw new ArgumentNullException(nameof(dispatcherService));

        Debug.WriteLine("[LibVlcAudioPlayerService] Initializing LibVLC core.");
        var vlcOptions = new string[]
        {
            "--no-video",               // No video output
            "--no-spu",                 // Disable subtitle processing
            "--no-osd",                 // Disable OnOSD
            "--no-stats",               // Disable playback statistics
            "--ignore-config",          // Isolate app from user's global VLC config
            "--no-one-instance",        // Ensure the instance is self-contained
            "--no-lua",                 // Disable Lua scripting
            "--verbose=1"               // Error/debug logging level
        };
        _libVlc = new LibVLC(false, vlcOptions);
        _mediaPlayer = new MediaPlayer(_libVlc);
        _dummyMediaPlayer = new WinMediaPlayback.MediaPlayer();

        // Disable the command manager for the dummy player to prevent it from
        // interfering with our manual SMTC control.
        _dummyMediaPlayer.CommandManager.IsEnabled = false;

        // Map LibVLCSharp events to the IAudioPlayer interface events.
        _mediaPlayer.PositionChanged += OnMediaPlayerPositionChanged;
        _mediaPlayer.Playing += OnMediaPlayerStateChanged;
        _mediaPlayer.Paused += OnMediaPlayerStateChanged;
        _mediaPlayer.Stopped += OnMediaPlayerStateChanged;
        _mediaPlayer.EncounteredError += OnMediaPlayerEncounteredError;
        _mediaPlayer.MediaChanged += OnMediaPlayerMediaChanged;
        _mediaPlayer.LengthChanged += OnMediaPlayerLengthChanged;
        _mediaPlayer.VolumeChanged += OnMediaPlayerVolumeChanged;
        _mediaPlayer.Muted += OnMediaPlayerMuteChanged;
        _mediaPlayer.Unmuted += OnMediaPlayerMuteChanged;
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
    public event Action? DurationChanged;
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
            Debug.WriteLine("[LibVlcAudioPlayerService] Initializing System Media Transport Controls (SMTC).");
            _smtc = _dummyMediaPlayer.SystemMediaTransportControls;

            _smtc.IsPlayEnabled = true;
            _smtc.IsPauseEnabled = true;
            _smtc.IsNextEnabled = false;
            _smtc.IsPreviousEnabled = false;

            _smtc.ButtonPressed += OnSmtcButtonPressed;
            _smtc.IsEnabled = true;

            _smtc.PlaybackStatus = MediaPlaybackStatus.Stopped;
            Debug.WriteLine("[LibVlcAudioPlayerService] SMTC initialized successfully.");
        }
        catch (Exception ex) {
            Debug.WriteLine($"[LibVlcAudioPlayerService] CRITICAL: Failed to initialize SMTC. Error: {ex.Message}");
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
        _currentSong = song;
        try {
            Debug.WriteLine($"[LibVlcAudioPlayerService] Loading media from path: {song.FilePath}");
            var media = new Media(new Uri(song.FilePath));
            _mediaPlayer.Media = media;
            // LibVLCSharp documentation recommends disposing the Media object
            // after assigning it to the player.
            media.Dispose();
        }
        catch (Exception ex) {
            var errorMessage = $"Failed to load '{song.Title}': {ex.Message}";
            Debug.WriteLine($"[LibVlcAudioPlayerService] ERROR: {errorMessage}");
            _dispatcherService.TryEnqueue(() => ErrorOccurred?.Invoke(errorMessage));
            _currentSong = null;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task PlayAsync() {
        if (_mediaPlayer.Media is not null) {
            _mediaPlayer.Play();
        }
        else {
            Debug.WriteLine("[LibVlcAudioPlayerService] WARN: Play command received, but no media is loaded.");
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task PauseAsync() {
        if (_mediaPlayer.CanPause) {
            _mediaPlayer.Pause();
        }
        else {
            Debug.WriteLine("[LibVlcAudioPlayerService] WARN: Pause command received, but player cannot be paused.");
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync() {
        _mediaPlayer.Stop();
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
            _mediaPlayer.SetTime((long)position.TotalMilliseconds, true);
        }
        else {
            Debug.WriteLine("[LibVlcAudioPlayerService] WARN: Seek command received, but media is not seekable.");
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SetVolumeAsync(double volume) {
        int vlcVolume = (int)Math.Clamp(volume * 100, 0, 100);
        _mediaPlayer.SetVolume(vlcVolume);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SetMuteAsync(bool isMuted) {
        _mediaPlayer.Mute = isMuted;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() {
        Debug.WriteLine("[LibVlcAudioPlayerService] Disposing service.");
        _mediaPlayer.PositionChanged -= OnMediaPlayerPositionChanged;
        _mediaPlayer.Playing -= OnMediaPlayerStateChanged;
        _mediaPlayer.Paused -= OnMediaPlayerStateChanged;
        _mediaPlayer.Stopped -= OnMediaPlayerStateChanged;
        _mediaPlayer.EncounteredError -= OnMediaPlayerEncounteredError;
        _mediaPlayer.MediaChanged -= OnMediaPlayerMediaChanged;
        _mediaPlayer.LengthChanged -= OnMediaPlayerLengthChanged;
        _mediaPlayer.VolumeChanged -= OnMediaPlayerVolumeChanged;
        _mediaPlayer.Muted -= OnMediaPlayerMuteChanged;
        _mediaPlayer.Unmuted -= OnMediaPlayerMuteChanged;

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
        Debug.WriteLine($"[LibVlcAudioPlayerService] MediaChanged event fired. New media is {(e.Media is not null ? "loaded" : "null")}.");
        if (e.Media is not null) {
            _dispatcherService.TryEnqueue(() => MediaOpened?.Invoke());
            _ = UpdateSmtcDisplayAsync();
        }
    }

    private void OnMediaPlayerLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e) {
        Debug.WriteLine($"[LibVlcAudioPlayerService] LengthChanged event fired. New length: {e.Length}ms.");
        _dispatcherService.TryEnqueue(() => DurationChanged?.Invoke());
    }

    private void OnMediaPlayerEncounteredError(object? sender, EventArgs e) {
        string? lastVlcError = _libVlc.LastLibVLCError;
        string errorMessage = string.IsNullOrEmpty(lastVlcError)
            ? "LibVLC encountered an unspecified error."
            : $"LibVLC Error: {lastVlcError}";

        Debug.WriteLine($"[LibVlcAudioPlayerService] ERROR: {errorMessage}");
        _dispatcherService.TryEnqueue(() => {
            ErrorOccurred?.Invoke(errorMessage);
            PlaybackEnded?.Invoke();
        });
    }

    private void OnMediaPlayerStateChanged(object? sender, EventArgs e) {
        _dispatcherService.TryEnqueue(() => {
            StateChanged?.Invoke();
            if (_mediaPlayer.State == VLCState.Stopped && _currentSong is not null) {
                Debug.WriteLine("[LibVlcAudioPlayerService] Detected natural end of playback.");
                PlaybackEnded?.Invoke();
            }
        });
        UpdateSmtcPlaybackStatus();
    }

    private void OnMediaPlayerPositionChanged(object? sender, MediaPlayerPositionChangedEventArgs e) {
        _dispatcherService.TryEnqueue(() => PositionChanged?.Invoke());
    }

    private void OnMediaPlayerVolumeChanged(object? sender, MediaPlayerVolumeChangedEventArgs e) {
        _dispatcherService.TryEnqueue(() => VolumeChanged?.Invoke());
    }

    private void OnMediaPlayerMuteChanged(object? sender, EventArgs e) {
        _dispatcherService.TryEnqueue(() => VolumeChanged?.Invoke());
    }

    private async void OnSmtcButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args) {
        Debug.WriteLine($"[LibVlcAudioPlayerService] SMTC button pressed: {args.Button}.");
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

        var newStatus = _mediaPlayer.State switch {
            VLCState.Playing => MediaPlaybackStatus.Playing,
            VLCState.Paused => MediaPlaybackStatus.Paused,
            VLCState.Stopped or VLCState.Error => MediaPlaybackStatus.Stopped,
            VLCState.Opening or VLCState.Buffering or VLCState.Stopping => MediaPlaybackStatus.Changing,
            _ => MediaPlaybackStatus.Closed
        };

        if (_smtc.PlaybackStatus != newStatus) {
            Debug.WriteLine($"[LibVlcAudioPlayerService] Updating SMTC PlaybackStatus to {newStatus}.");
            _smtc.PlaybackStatus = newStatus;
        }
    }

    private async Task UpdateSmtcDisplayAsync() {
        if (_smtc is null || _currentSong is null) return;

        Debug.WriteLine($"[LibVlcAudioPlayerService] Updating SMTC display for track '{_currentSong.Title}'.");
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
                Debug.WriteLine($"[LibVlcAudioPlayerService] WARN: Failed to set SMTC thumbnail: {ex.Message}");
            }
        }

        updater.Update();
    }
}