using System;
using LibVLCSharp;
using Microsoft.Extensions.Logging;
using System.Threading;
using Nagi.Presentation.Resources;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
///     Handlers for LibVLC media player and log events.
/// </summary>
public sealed partial class LibVlcAudioPlayerService
{
    private void OnLibVlcLog(object? sender, LogEventArgs e)
    {
        if (_isDisposed) return;

        var message = string.IsNullOrWhiteSpace(e.FormattedLog) ? e.Message : e.FormattedLog;
        switch (e.Level)
        {
            case LibVLCSharp.LogLevel.Error:
                _logger.LogError("LibVLC [{Module}] {Message}", e.Module, message);
                break;
            case LibVLCSharp.LogLevel.Warning:
                _logger.LogWarning("LibVLC [{Module}] {Message}", e.Module, message);
                break;
            case LibVLCSharp.LogLevel.Notice:
                _logger.LogDebug("LibVLC [{Module}] {Message}", e.Module, message);
                break;
            default:
                _logger.LogTrace("LibVLC [{Module}] {Message}", e.Module, message);
                break;
        }
    }

    private void OnMediaPlayerMediaChanged(object? sender, MediaPlayerMediaChangedEventArgs e)
    {
        if (_isDisposed) return;
        if (e.Media is not null)
        {
            _dispatcherService.TryEnqueue(() =>
            {
                if (_isDisposed) return;
                MediaOpened?.Invoke();
            });
            _ = UpdateSmtcDisplayAsync();
        }
    }

    private void OnMediaPlayerLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
    {
        if (_isDisposed) return;
        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed) return;
            DurationChanged?.Invoke();
        });
    }

    private void OnMediaPlayerEncounteredError(object? sender, EventArgs e)
    {
        if (_isDisposed || _libVlc is null) return;

        _isExplicitStop = true;

        var lastVlcError = ReadNative(() => _libVlc?.LastLibVLCError, null);
        var errorMessage = string.IsNullOrEmpty(lastVlcError)
            ? Strings.Player_Error_LibVLC_Unspecified
            : string.Format(Strings.Player_Error_LibVLC_WithDetails, lastVlcError);
        _logger.LogError("Playback error occurred. {ErrorMessage}", errorMessage);
        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed) return;
            ErrorOccurred?.Invoke(errorMessage);
        });
    }

    private void OnMediaPlayerStateChanged(object? sender, EventArgs e)
    {
        if (_isDisposed || _mediaPlayer is null) return;

        // Capture volatile fields before enqueueing to prevent TOCTOU races.
        // Between the outer check and lambda execution, Dispose/Stop could null these fields.
        var currentSong = _currentSong;
        var isExplicitStop = _isExplicitStop;

        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed) return;
            StateChanged?.Invoke();
            var state = ReadNative(() => _mediaPlayer?.State, null);

            // Only fire PlaybackEnded for natural end of playback, not explicit stops
            if (state == VLCState.Stopped && currentSong is not null && !isExplicitStop)
            {
                _logger.LogDebug("Detected natural end of playback for song '{SongTitle}'.", currentSong.Title);
                PlaybackEnded?.Invoke();
            }

            // Reset flag when playback starts
            if (state == VLCState.Playing)
            {
                _isExplicitStop = false;
                _isPausing = false;
            }
        });
        UpdateSmtcPlaybackStatus();
    }

    private void OnMediaPlayerPositionChanged(object? sender, MediaPlayerPositionChangedEventArgs e)
    {
        if (_isDisposed) return;
        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed) return;
            PositionChanged?.Invoke();
        });
    }

    private void OnMediaPlayerVolumeChanged(object? sender, MediaPlayerVolumeChangedEventArgs e)
    {
        if (_isDisposed || _isFading) return;

        // LibVLC 4 uses the Windows audio-session volume. A change made in Windows Settings
        // therefore arrives here just like a change made through Nagi. Keep our user-volume
        // snapshot in sync so the next track does not restore a stale value over the user's
        // Windows mixer adjustment.
        var reportedVolume = ReadNative(() => _mediaPlayer?.Volume ?? -1, -1);
        if (reportedVolume >= 0)
            Interlocked.Exchange(ref _userVolume, Math.Clamp(reportedVolume / 100.0, 0.0, 1.0));

        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed || _isFading) return;
            VolumeChanged?.Invoke();
        });
    }

    private void OnMediaPlayerMuteChanged(object? sender, EventArgs e)
    {
        if (_isDisposed) return;
        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed) return;
            VolumeChanged?.Invoke();
        });
    }
}
