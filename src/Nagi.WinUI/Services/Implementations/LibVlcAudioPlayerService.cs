﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Storage;
using Windows.Storage.Streams;
using LibVLCSharp;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Services.Abstractions;
using WinMediaPlayback = Windows.Media.Playback;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
///     Implements <see cref="IAudioPlayer" /> using LibVLCSharp for robust audio playback
///     and manual integration with the System Media Transport Controls (SMTC).
/// </summary>
public sealed class LibVlcAudioPlayerService : IAudioPlayer, IDisposable {
    private readonly IDispatcherService _dispatcherService;
    private readonly ILogger<LibVlcAudioPlayerService> _logger;

    private readonly WinMediaPlayback.MediaPlayer _dummyMediaPlayer;
    private readonly Equalizer _equalizer;
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private Song? _currentSong;
    private SystemMediaTransportControls? _smtc;

    public LibVlcAudioPlayerService(IDispatcherService dispatcherService, ILogger<LibVlcAudioPlayerService> logger) {
        _dispatcherService = dispatcherService ?? throw new ArgumentNullException(nameof(dispatcherService));
        _logger = logger;

        _logger.LogInformation("Initializing LibVLC core.");

        var vlcOptions = new[]
        {
            "--no-video", "--no-spu", "--no-osd", "--no-stats", "--ignore-config",
            "--no-one-instance", "--no-lua", "--verbose=-1", "--audio-filter=equalizer"
        };

        // Log detailed configuration at Debug level
        _logger.LogDebug("Initializing LibVLC with options: {VlcOptions}", string.Join(" ", vlcOptions));
        _libVlc = new LibVLC(false, vlcOptions);
        _mediaPlayer = new MediaPlayer(_libVlc);
        _dummyMediaPlayer = new WinMediaPlayback.MediaPlayer { CommandManager = { IsEnabled = false } };

        _equalizer = new Equalizer();
        _mediaPlayer.SetEqualizer(_equalizer);

        // Register event handlers
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

    public event Action? PlaybackEnded, PositionChanged, StateChanged, VolumeChanged, MediaOpened, DurationChanged;
    public event Action<string>? ErrorOccurred;
    public event Action? SmtcNextButtonPressed, SmtcPreviousButtonPressed;

    public bool IsPlaying => _mediaPlayer.IsPlaying;
    public TimeSpan CurrentPosition => TimeSpan.FromMilliseconds(_mediaPlayer.Time);
    public TimeSpan Duration => TimeSpan.FromMilliseconds(_mediaPlayer.Length);
    public double Volume => _mediaPlayer.Volume / 100.0;
    public bool IsMuted => _mediaPlayer.Mute;

    public void InitializeSmtc() {
        try {
            _logger.LogInformation("Initializing System Media Transport Controls (SMTC).");
            _smtc = _dummyMediaPlayer.SystemMediaTransportControls;
            _smtc.IsPlayEnabled = true;
            _smtc.IsPauseEnabled = true;
            _smtc.IsNextEnabled = false;
            _smtc.IsPreviousEnabled = false;
            _smtc.ButtonPressed += OnSmtcButtonPressed;
            _smtc.IsEnabled = true;
            _smtc.PlaybackStatus = MediaPlaybackStatus.Stopped;
            _logger.LogInformation("SMTC initialized successfully.");
        }
        catch (Exception ex) {
            _logger.LogCritical(ex, "Failed to initialize SMTC. System media controls will be unavailable.");
        }
    }

    public void UpdateSmtcButtonStates(bool canNext, bool canPrevious) {
        if (_smtc is null) return;
        _smtc.IsNextEnabled = canNext;
        _smtc.IsPreviousEnabled = canPrevious;
    }

    public Task LoadAsync(Song song) {
        _currentSong = song;
        try {
            _logger.LogInformation("Loading media for song '{SongTitle}' from path: {FilePath}", song.Title, song.FilePath);
            var media = new Media(new Uri(song.FilePath));
            _mediaPlayer.Media = media;
            media.Dispose();
        }
        catch (Exception ex) {
            var errorMessage = $"Failed to load '{song.Title}'.";
            _logger.LogError(ex, "Failed to load song '{SongTitle}' from path: {FilePath}", song.Title, song.FilePath);
            _dispatcherService.TryEnqueue(() => ErrorOccurred?.Invoke(errorMessage));
            _currentSong = null;
        }
        return Task.CompletedTask;
    }

    public Task PlayAsync() {
        if (_mediaPlayer.Media is not null) {
            _mediaPlayer.Play();
        }
        else {
            _logger.LogWarning("Play command received, but no media is loaded.");
        }
        return Task.CompletedTask;
    }

    public Task PauseAsync() {
        if (_mediaPlayer.CanPause) {
            _mediaPlayer.Pause();
        }
        else {
            _logger.LogWarning("Pause command received, but player cannot be paused in its current state.");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync() {
        _logger.LogInformation("Stop command received.");
        _mediaPlayer.Stop();
        _currentSong = null;

        if (_smtc is not null) {
            _smtc.DisplayUpdater.ClearAll();
            _smtc.DisplayUpdater.Update();
            _smtc.PlaybackStatus = MediaPlaybackStatus.Stopped;
        }
        return Task.CompletedTask;
    }

    public Task SeekAsync(TimeSpan position) {
        if (_mediaPlayer.IsSeekable) {
            _mediaPlayer.SetTime((long)position.TotalMilliseconds, true);
        }
        else {
            _logger.LogWarning("Seek command received, but media is not seekable.");
        }
        return Task.CompletedTask;
    }

    public Task SetVolumeAsync(double volume) {
        var vlcVolume = (int)Math.Clamp(volume * 100, 0, 100);
        _mediaPlayer.SetVolume(vlcVolume);
        return Task.CompletedTask;
    }

    public Task SetMuteAsync(bool isMuted) {
        _mediaPlayer.Mute = isMuted;
        return Task.CompletedTask;
    }

    public IReadOnlyList<(uint Index, float Frequency)> GetEqualizerBands() {
        var bandCount = _equalizer.BandCount;
        var bands = new List<(uint, float)>();
        for (uint i = 0; i < bandCount; i++) bands.Add((i, _equalizer.BandFrequency(i)));
        return bands;
    }

    public bool ApplyEqualizerSettings(EqualizerSettings settings) {
        if (settings == null) {
            _logger.LogWarning("ApplyEqualizerSettings called with null settings. Aborting.");
            return false;
        }

        _equalizer.SetPreamp(settings.Preamp);
        var bandCount = _equalizer.BandCount;
        for (var i = 0; i < settings.BandGains.Count; i++) {
            if (i < bandCount) _equalizer.SetAmp(settings.BandGains[i], (uint)i);
        }

        var success = _mediaPlayer.SetEqualizer(_equalizer);
        _logger.LogDebug("Re-applied equalizer to MediaPlayer. Success: {Success}", success);
        return success;
    }

    public void Dispose() {
        _logger.LogInformation("Disposing LibVlcAudioPlayerService.");
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

        _equalizer.Dispose();
        _mediaPlayer.Stop();
        _mediaPlayer.Dispose();
        _libVlc.Dispose();
        _dummyMediaPlayer.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnMediaPlayerMediaChanged(object? sender, MediaPlayerMediaChangedEventArgs e) {
        if (e.Media is not null) {
            _dispatcherService.TryEnqueue(() => MediaOpened?.Invoke());
            _ = UpdateSmtcDisplayAsync();
        }
    }

    private void OnMediaPlayerLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e) => _dispatcherService.TryEnqueue(() => DurationChanged?.Invoke());

    private void OnMediaPlayerEncounteredError(object? sender, EventArgs e) {
        var lastVlcError = _libVlc.LastLibVLCError;
        var errorMessage = string.IsNullOrEmpty(lastVlcError) ? "LibVLC encountered an unspecified error." : $"LibVLC Error: {lastVlcError}";
        _logger.LogError("Playback error occurred. {ErrorMessage}", errorMessage);
        _dispatcherService.TryEnqueue(() => {
            ErrorOccurred?.Invoke(errorMessage);
            PlaybackEnded?.Invoke();
        });
    }

    private void OnMediaPlayerStateChanged(object? sender, EventArgs e) {
        _dispatcherService.TryEnqueue(() => {
            StateChanged?.Invoke();
            if (_mediaPlayer.State == VLCState.Stopped && _currentSong is not null) {
                _logger.LogInformation("Detected natural end of playback for song '{SongTitle}'.", _currentSong.Title);
                PlaybackEnded?.Invoke();
            }
        });
        UpdateSmtcPlaybackStatus();
    }

    private void OnMediaPlayerPositionChanged(object? sender, MediaPlayerPositionChangedEventArgs e) => _dispatcherService.TryEnqueue(() => PositionChanged?.Invoke());
    private void OnMediaPlayerVolumeChanged(object? sender, MediaPlayerVolumeChangedEventArgs e) => _dispatcherService.TryEnqueue(() => VolumeChanged?.Invoke());
    private void OnMediaPlayerMuteChanged(object? sender, EventArgs e) => _dispatcherService.TryEnqueue(() => VolumeChanged?.Invoke());

    private async void OnSmtcButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args) {
        _logger.LogDebug("SMTC button pressed: {Button}", args.Button);
        await _dispatcherService.EnqueueAsync(async () => {
            switch (args.Button) {
                case SystemMediaTransportControlsButton.Play: await PlayAsync(); break;
                case SystemMediaTransportControlsButton.Pause: await PauseAsync(); break;
                case SystemMediaTransportControlsButton.Next: SmtcNextButtonPressed?.Invoke(); break;
                case SystemMediaTransportControlsButton.Previous: SmtcPreviousButtonPressed?.Invoke(); break;
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
            _logger.LogDebug("Updating SMTC PlaybackStatus from {OldStatus} to {NewStatus}", _smtc.PlaybackStatus, newStatus);
            _smtc.PlaybackStatus = newStatus;
        }
    }

    private async Task UpdateSmtcDisplayAsync() {
        if (_smtc is null || _currentSong is null) return;

        _logger.LogInformation("Updating SMTC display for track '{SongTitle}'.", _currentSong.Title);
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
                _logger.LogWarning(ex, "Failed to load and set SMTC thumbnail from {AlbumArtPath}", _currentSong.AlbumArtUriFromTrack);
            }
        }
        updater.Update();
    }
}