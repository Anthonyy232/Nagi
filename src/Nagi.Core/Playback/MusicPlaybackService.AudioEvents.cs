using Microsoft.Extensions.Logging;
using Nagi.Core.Library;
using Nagi.Core.Listening;
using Nagi.Core.Settings;

namespace Nagi.Core.Playback;

/// <summary>
///     Reactions to audio player and settings events, scrobble eligibility, and ReplayGain.
/// </summary>
public partial class MusicPlaybackService
{
    private void OnAudioPlayerPlaybackEnded()
    {
        if (IsRequestPending) return;
        var request = _requestCts.Token;
        var finishing = EndListenSession(PlaybackEndReason.Finished, Duration);
        RunInBackground(async () =>
        {
            await finishing;
            if (!request.IsCancellationRequested) await RunPlaybackRequest(AdvanceAfterTrackEndAsync);
        }, "Moving on after the track ended");
    }

    private void OnAudioPlayerStateChanged()
    {
        PlaybackStateChanged?.Invoke();
        UpdateMediaControlsStatus();
        UpdateMediaControlsTimeline(CurrentPosition);
    }

    // The one place volume and mute are saved, so changes made outside Nagi, such as in the Windows volume mixer, are
    // kept too.
    private void OnAudioPlayerVolumeChanged()
    {
        VolumeStateChanged?.Invoke();
        _settingsService.Set(CoreSettings.Volume, _audioPlayer.Volume);
        _settingsService.Set(CoreSettings.IsMuted, _audioPlayer.IsMuted);
    }

    private void OnAudioPlayerPositionChanged()
    {
        PositionChanged?.Invoke();
        MaybeMarkEligibleForScrobbling();
    }

    private void MaybeMarkEligibleForScrobbling()
    {
        if (IsRequestPending || _isEligibilityMarked || !CurrentListenHistoryId.HasValue) return;

        if (!ListenRules.IsEligible(Duration, _audioPlayer.CurrentPosition)) return;

        _isEligibilityMarked = true;
        var sessionId = CurrentListenHistoryId.Value;
        RunInBackground(
            async () =>
            {
                var marked = false;
                try
                {
                    marked = await _listening.MarkListenAsEligibleForScrobblingAsync(sessionId);
                    if (marked && !_isDisposed) ScrobbleEligibilityReached?.Invoke();
                }
                finally
                {
                    if (!marked && CurrentListenHistoryId == sessionId) _isEligibilityMarked = false;
                }
            },
            "Mark listen eligible for scrobbling");
    }

    private void OnAudioPlayerDurationChanged()
    {
        DurationChanged?.Invoke();
        UpdateMediaControlsTimeline(CurrentPosition);
    }

    private void OnAudioPlayerErrorOccurred()
    {
        _logger.LogWarning("Audio playback failed. Stopping playback.");
        // Read before stopping, which clears the current track.
        var song = CurrentTrack;
        RunInBackground(StopAsync, "Error recovery");
        if (song is { IsFromServer: true }) ServerSongFailed?.Invoke(song);
    }

    private void OnAudioPlayerMediaOpened()
    {
        if (CurrentTrack is { } song)
            RunInBackground(() => _mediaControls.SetNowPlayingAsync(song), "Media controls now playing");
        UpdateMediaControlsNavigation();
        UpdateMediaControlsTimeline(CurrentPosition);
    }

    // The media controls send Play and Pause separately, so only toggle when the state differs.
    private void OnMediaControlsPlayPressed()
    {
        if (!IsPlaying) RunInBackground(PlayPauseAsync, "Media controls Play");
    }

    private void OnMediaControlsPausePressed()
    {
        if (IsPlaying) RunInBackground(PlayPauseAsync, "Media controls Pause");
    }

    private void OnMediaControlsNextPressed()
    {
        RunInBackground(NextAsync, "Media controls Next");
    }

    private void OnMediaControlsPreviousPressed()
    {
        RunInBackground(PreviousAsync, "Media controls Previous");
    }

    private void OnMediaControlsSeekRequested(TimeSpan position)
    {
        if (position < TimeSpan.Zero || position > Duration)
        {
            _logger.LogDebug("Ignoring media controls seek to {Position} outside the track.", position);
            return;
        }

        RunInBackground(() => SeekAsync(position), "Media controls Seek");
    }

    private async Task ApplyReplayGainIfEnabledAsync()
    {
        // A later application, or a later playback request, supersedes this one.
        _replayGainCts.Cancel();
        _replayGainCts = new CancellationTokenSource();
        var superseded = _replayGainCts.Token;
        var request = _requestCts.Token;
        var song = CurrentTrack;
        if (song == null) return;
        var isEnabled = _settingsService.Get(CoreSettings.VolumeNormalization);

        if (isEnabled && !ReferenceEquals(_trackGain?.Song, song))
        {
            var trackGain = new TrackGain(song, song.ReplayGainTrackGain, song.ReplayGainTrackPeak);
            // A ReplayGain scan can store the gain after the song was loaded.
            if (!trackGain.GainDb.HasValue && !_transientTracks.ContainsKey(song.Id))
            {
                var refreshed = await _libraryReader.GetSongByIdAsync(song.Id);
                if (request.IsCancellationRequested || superseded.IsCancellationRequested) return;
                if (refreshed != null)
                    trackGain = trackGain with { GainDb = refreshed.ReplayGainTrackGain, Peak = refreshed.ReplayGainTrackPeak };
            }
            _trackGain = trackGain;
        }

        var gain = isEnabled ? _trackGain!.GainDb.GetValueOrDefault() : 0;
        if (isEnabled && _trackGain!.GainDb.HasValue && _trackGain.Peak is > 0)
            gain = Math.Min(gain, -20 * Math.Log10(_trackGain.Peak.Value));
        await _audioPlayer.SetReplayGainAsync(gain);
    }

    // Kept per loaded song instance, so the library is asked at most once each time a song is played.
    private sealed record TrackGain(Song Song, double? GainDb, double? Peak);

    private void OnSettingChanged(Setting setting)
    {
        if (setting == CoreSettings.VolumeNormalization)
            RunInBackground(ApplyReplayGainIfEnabledAsync, "ReplayGain settings change");
    }
}
