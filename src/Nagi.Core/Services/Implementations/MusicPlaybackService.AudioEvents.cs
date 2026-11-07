using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Settings;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Reactions to audio player and settings events, scrobble eligibility, and ReplayGain.
/// </summary>
public partial class MusicPlaybackService
{
    private void OnAudioPlayerPlaybackEnded()
    {
        if (_isTransitioningTrack) return;
        // Capture and clear the session ID synchronously before firing the async task.
        // This prevents PlayQueueItemAsync (called inside NextAsync) from seeing a non-null
        // CurrentListenHistoryId and issuing a second FinalizeListenSessionAsync(Skipped)
        // on a session already being finalized here as Finished.
        var finishedSessionId = CurrentListenHistoryId;
        var duration = Duration;
        var request = _playbackRequestVersion;
        CurrentListenHistoryId = null;

        FireAndForgetSafe(async () =>
        {
            if (finishedSessionId.HasValue)
            {
                await _libraryWriter.FinalizeListenSessionAsync(finishedSessionId.Value, duration, PlaybackEndReason.Finished);
            }
            if (IsCurrentRequest(request)) await NextAsync();
        }, "PlaybackEnded transition", trackForDisposal: true);
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
        if (_isTransitioningTrack || _isEligibilityMarked || !CurrentListenHistoryId.HasValue) return;

        var duration = Duration;
        var progress = _audioPlayer.CurrentPosition;

        // Apply the scrobble threshold even when no external scrobbling account is configured.
        var isLongEnough = duration.TotalSeconds > 30;
        var hasPlayedEnough = progress.TotalSeconds >= duration.TotalSeconds / 2
                           || progress.TotalMinutes >= 4;

        if (!isLongEnough || !hasPlayedEnough) return;

        // Prevent re-entry within the same listening session.
        _isEligibilityMarked = true;
        var sessionId = CurrentListenHistoryId.Value;
        var song = CurrentTrack!;
        var listenStartedUtc = _currentListenStartedUtc ?? _timeProvider.GetUtcNow().UtcDateTime - progress;
        FireAndForgetSafe(
            async () =>
            {
                var marked = false;
                try
                {
                    marked = await _libraryWriter.MarkListenAsEligibleForScrobblingAsync(sessionId);
                    if (marked && !_isDisposed) ScrobbleEligibilityReached?.Invoke(song, sessionId, listenStartedUtc);
                }
                finally
                {
                    if (!marked && CurrentListenHistoryId == sessionId) _isEligibilityMarked = false;
                }
            },
            "Mark listen eligible for scrobbling",
            trackForDisposal: true);
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
        FireAndForgetSafe(StopAsync, "Error recovery");
        if (song is { IsRemote: true }) ServerSongFailed?.Invoke(song);
    }

    private void OnAudioPlayerMediaOpened()
    {
        TrackChanged?.Invoke();
        if (CurrentTrack is { } song)
            FireAndForgetSafe(() => _mediaControls.SetNowPlayingAsync(song), "Media controls now playing");
        UpdateMediaControlsNavigation();
        UpdateMediaControlsTimeline(CurrentPosition);
    }

    // The media controls send Play and Pause separately, so only toggle when the state differs.
    private void OnMediaControlsPlayPressed()
    {
        if (!IsPlaying) FireAndForgetSafe(PlayPauseAsync, "Media controls Play");
    }

    private void OnMediaControlsPausePressed()
    {
        if (IsPlaying) FireAndForgetSafe(PlayPauseAsync, "Media controls Pause");
    }

    private void OnMediaControlsNextPressed()
    {
        FireAndForgetSafe(
            NextAsync,
            "Media controls Next");
    }

    private void OnMediaControlsPreviousPressed()
    {
        FireAndForgetSafe(
            PreviousAsync,
            "Media controls Previous");
    }

    private void OnMediaControlsSeekRequested(TimeSpan position)
    {
        if (position < TimeSpan.Zero || position > Duration)
        {
            _logger.LogDebug("Ignoring media controls seek to {Position} outside the track.", position);
            return;
        }

        FireAndForgetSafe(
            () => SeekAsync(position),
            "Media controls Seek");
    }

    private async Task ApplyReplayGainIfEnabledAsync()
    {
        var version = ++_replayGainVersion;
        var request = _playbackRequestVersion;
        var song = CurrentTrack;
        if (song == null) return;
        var isEnabled = _settingsService.Get(CoreSettings.VolumeNormalization);
        if (!IsCurrentRequest(request) || version != _replayGainVersion) return;

        if (isEnabled && !ReferenceEquals(_trackGain?.Song, song))
        {
            var trackGain = new TrackGain(song, song.ReplayGainTrackGain, song.ReplayGainTrackPeak);
            // A ReplayGain scan can store the gain after the song was loaded.
            if (!trackGain.GainDb.HasValue && !_transientTracks.ContainsKey(song.Id))
            {
                var refreshed = await _libraryReader.GetSongByIdAsync(song.Id);
                if (!IsCurrentRequest(request) || version != _replayGainVersion) return;
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
            FireAndForgetSafe(ApplyReplayGainIfEnabledAsync, "ReplayGain settings change", trackForDisposal: true);
    }
}
