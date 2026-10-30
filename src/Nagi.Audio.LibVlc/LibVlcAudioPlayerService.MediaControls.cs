using LibVLCSharp;
using Nagi.Core.Models;

namespace Nagi.Audio.LibVlc;

/// <summary>
///     Keeps the operating system's media controls in step with LibVLC. Their buttons are handled by
///     MusicPlaybackService, which owns playback.
/// </summary>
public sealed partial class LibVlcAudioPlayerService
{
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
