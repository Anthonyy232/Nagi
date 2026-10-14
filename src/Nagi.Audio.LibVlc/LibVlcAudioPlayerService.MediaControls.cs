using System;
using LibVLCSharp;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;

namespace Nagi.Audio.LibVlc;

/// <summary>
///     Keeps the operating system's media controls in step with LibVLC and handles their play and pause buttons.
/// </summary>
public sealed partial class LibVlcAudioPlayerService
{
    private async void OnMediaControlsPlayPressed()
    {
        if (_isDisposed) return;
        try { await PlayAsync(); }
        catch (Exception ex) { _logger.LogError(ex, "Error handling the media controls Play button."); }
    }

    private async void OnMediaControlsPausePressed()
    {
        if (_isDisposed) return;
        try { await PauseAsync(); }
        catch (Exception ex) { _logger.LogError(ex, "Error handling the media controls Pause button."); }
    }

    private void ReportPlaybackStatus()
    {
        if (_isDisposed || _mediaPlayer is null) return;
        _mediaControls.SetPlaybackStatus(ReadNative(() => _mediaPlayer?.State, null) switch
        {
            VLCState.Playing => SystemMediaPlaybackStatus.Playing,
            VLCState.Paused => SystemMediaPlaybackStatus.Paused,
            VLCState.Stopped or VLCState.Error => SystemMediaPlaybackStatus.Stopped,
            VLCState.Opening or VLCState.Stopping => SystemMediaPlaybackStatus.Changing,
            _ => SystemMediaPlaybackStatus.Closed
        });
    }

    private void ReportNowPlaying()
    {
        var song = _currentSong;
        if (_isDisposed || song is null) return;
        _ = _mediaControls.SetNowPlayingAsync(song);
    }
}
