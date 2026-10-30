using Microsoft.Extensions.Logging;
using Nagi.Core.Models;

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
        UpdateMediaControlsTimeline(CurrentPosition);
    }

    private void OnAudioPlayerVolumeChanged()
    {
        FireAndForgetSafe(
            async () =>
            {
                await _settingsService.SaveVolumeAsync(_audioPlayer.Volume);
                VolumeStateChanged?.Invoke();
            },
            "Volume save");
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

    private void OnAudioPlayerErrorOccurred(string errorMessage)
    {
        _logger.LogWarning("Audio playback failed: {Message}", errorMessage);
        FireAndForgetSafe(StopAsync, "Error recovery");
    }

    private void OnAudioPlayerMediaOpened()
    {
        TrackChanged?.Invoke();
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

    private async Task ApplyReplayGainIfEnabledAsync(bool? isEnabledOverride = null)
    {
        var version = ++_replayGainVersion;
        var request = _playbackRequestVersion;
        var song = CurrentTrack;
        if (song == null) return;
        var isEnabled = isEnabledOverride ?? await _settingsService.GetVolumeNormalizationEnabledAsync();
        if (!IsCurrentRequest(request) || version != _replayGainVersion) return;

        if (isEnabled && !_transientTracks.ContainsKey(song.Id) &&
            !song.ReplayGainTrackGain.HasValue && !song.ReplayGainCheckPerformed)
        {
            var refreshed = await _libraryReader.GetSongByIdAsync(song.Id);
            if (!IsCurrentRequest(request) || version != _replayGainVersion) return;
            song.ReplayGainCheckPerformed = true;
            if (refreshed != null)
            {
                song.ReplayGainTrackGain = refreshed.ReplayGainTrackGain;
                song.ReplayGainTrackPeak = refreshed.ReplayGainTrackPeak;
            }
        }

        var gain = isEnabled ? song.ReplayGainTrackGain.GetValueOrDefault() : 0;
        if (isEnabled && song.ReplayGainTrackGain.HasValue && song.ReplayGainTrackPeak is > 0)
            gain = Math.Min(gain, -20 * Math.Log10(song.ReplayGainTrackPeak.Value));
        await _audioPlayer.SetReplayGainAsync(gain);
    }

    private void OnVolumeNormalizationEnabledChanged(bool isEnabled)
    {
        FireAndForgetSafe(
            () => ApplyReplayGainIfEnabledAsync(isEnabled),
            "ReplayGain settings change", trackForDisposal: true);
    }

    private void OnFadeOnPlayPauseEnabledChanged(bool isEnabled)
    {
        _audioPlayer.SetFadeOnPlayPauseEnabled(isEnabled);
    }

    private void OnFadeInDurationChanged(int durationMs)
    {
        _audioPlayer.SetFadeInDuration(durationMs);
    }

    private void OnFadeOutDurationChanged(int durationMs)
    {
        _audioPlayer.SetFadeOutDuration(durationMs);
    }
}
