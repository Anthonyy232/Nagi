using Nagi.Core.Settings;

namespace Nagi.Core.Playback;

public partial class MusicPlaybackService
{
    public ShuffleMode CurrentShuffleMode { get; private set; }

    /// <summary>
    ///     Sets the shuffle mode once the queue's own shuffle has been set, saving the shuffle setting and raising
    ///     <see cref="ShuffleModeChanged" /> when either changed.
    /// </summary>
    private void SetShuffleMode(ShuffleMode mode, bool wasShuffled)
    {
        if (mode == CurrentShuffleMode && wasShuffled == IsShuffleEnabled) return;
        CurrentShuffleMode = mode;
        if (wasShuffled != IsShuffleEnabled) _settingsService.Set(CoreSettings.IsShuffleEnabled, IsShuffleEnabled);
        ShuffleModeChanged?.Invoke();
    }

    public async Task<bool> PlayMixAsync(IEnumerable<Guid> songIds, ShuffleMode mode, TimeSpan? duration = null,
        PlaybackContext? context = null, int avoidRecentDays = PlaybackLimits.DefaultAvoidRecentDays)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (duration is { } limit && (limit <= TimeSpan.Zero || limit > TimeSpan.FromMinutes(PlaybackLimits.MaxScheduledMinutes)))
            throw new ArgumentOutOfRangeException(nameof(duration));
        if (avoidRecentDays is < 0 or > PlaybackLimits.MaxAvoidRecentDays) throw new ArgumentOutOfRangeException(nameof(avoidRecentDays));

        var played = false;
        await RunPlaybackRequest(async request =>
        {
            var ids = songIds.Distinct().ToArray();
            var transient = ids.Where(_transientTracks.ContainsKey).Select(id => _transientTracks[id])
                .Select(s => new MixCandidate(s.Id, s.PrimaryArtistName, s.DurationTicks, s.PlayCount, s.LastPlayedDate)).ToList();
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var order = await Task.Run(async () =>
            {
                var candidates = (await _listening.GetMixCandidatesAsync(ids)).ToList();
                candidates.AddRange(transient);
                return PlaybackMixBuilder.Build(candidates, mode, now, duration, avoidRecentDays);
            });
            request.ThrowIfCancellationRequested();
            if (order.Count == 0) return;
            await PlayInternalAsync(order, 0, true, context ?? _currentContext, request, order, mode);
            request.ThrowIfCancellationRequested();
            if (duration.HasValue) ScheduleStop(PlaybackStopMode.EndOfQueue);
            played = true;
        });
        return played;
    }
}
