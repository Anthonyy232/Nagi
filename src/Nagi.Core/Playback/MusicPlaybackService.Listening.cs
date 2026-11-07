namespace Nagi.Core.Playback;

public partial class MusicPlaybackService
{
    private readonly TimeProvider _timeProvider;
    private bool _finishCurrentTrack = true;
    private DateTimeOffset? _stopDeadline;
    private CancellationTokenSource? _stopTimerCts;

    public ShuffleMode CurrentShuffleMode { get; private set; }
    public PlaybackStopMode StopMode { get; private set; }

    public TimeSpan? StopTimeRemaining => _stopDeadline is { } deadline
        ? TimeSpan.FromTicks(Math.Max(0, (deadline - _timeProvider.GetUtcNow()).Ticks))
        : null;

    public event Action? StopScheduleChanged;

    public async Task<bool> PlayMixAsync(IEnumerable<Guid> songIds, ShuffleMode mode, TimeSpan? duration = null,
        PlaybackContext? context = null, int avoidRecentDays = PlaybackMixLimits.DefaultAvoidRecentDays)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (duration is { } limit && (limit <= TimeSpan.Zero || limit > TimeSpan.FromMinutes(PlaybackMixLimits.MaxMinutes)))
            throw new ArgumentOutOfRangeException(nameof(duration));
        if (avoidRecentDays is < 0 or > PlaybackMixLimits.MaxAvoidRecentDays) throw new ArgumentOutOfRangeException(nameof(avoidRecentDays));

        var played = false;
        await RunPlaybackRequest(async request =>
        {
            var ids = songIds.Distinct().ToArray();
            var transient = ids.Where(_transientTracks.ContainsKey).Select(id => _transientTracks[id])
                .Select(s => new MixCandidate(s.Id, s.PrimaryArtistName, s.DurationTicks, s.PlayCount, s.LastPlayedDate)).ToList();
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var order = await Task.Run(async () =>
            {
                var candidates = (await _libraryReader.GetMixCandidatesAsync(ids)).ToList();
                candidates.AddRange(transient);
                return PlaybackMixBuilder.Build(candidates, mode, now, duration, avoidRecentDays);
            });
            request.ThrowIfCancellationRequested();
            if (order.Count == 0) return;
            await PlayInternalAsync(order, 0, true, context ?? _currentContext, request, order, mode);
            request.ThrowIfCancellationRequested();
            if (duration.HasValue)
            {
                await SetRepeatModeAsync(RepeatMode.Off);
                request.ThrowIfCancellationRequested();
                ScheduleStop(PlaybackStopMode.EndOfQueue);
            }
            played = true;
        });
        return played;
    }

    public void ScheduleStop(PlaybackStopMode mode, TimeSpan? delay = null, bool finishCurrentTrack = true)
    {
        if (_isDisposed) return;
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode == PlaybackStopMode.Timer && (delay is null || delay <= TimeSpan.Zero ||
                                               delay > TimeSpan.FromMinutes(PlaybackMixLimits.MaxMinutes)))
            throw new ArgumentOutOfRangeException(nameof(delay));
        _stopTimerCts?.Cancel();
        _stopTimerCts = null;
        StopMode = mode;
        _stopDeadline = null;
        _finishCurrentTrack = finishCurrentTrack;
        if (mode == PlaybackStopMode.Timer)
        {
            _stopDeadline = _timeProvider.GetUtcNow() + delay!.Value;
            _stopTimerCts = new CancellationTokenSource();
            _ = StopWhenTimerRunsOutAsync(delay.Value, _stopTimerCts.Token);
        }
        StopScheduleChanged?.Invoke();
    }

    // Scheduled from the UI context, like every call into the service, so it carries on there when the delay ends.
    private async Task StopWhenTimerRunsOutAsync(TimeSpan delay, CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(delay, _timeProvider, cancellation);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_finishCurrentTrack && CurrentTrack is not null && IsPlaying)
            ScheduleStop(PlaybackStopMode.AfterTrack);
        else
            _ = RunInBackground(StopAsync, "Sleep timer stop");
    }

    private bool ShouldStopAfterTrack()
    {
        if (StopMode == PlaybackStopMode.AfterTrack) return true;
        return StopMode == PlaybackStopMode.EndOfQueue && _queue.CurrentPosition == _queue.Count - 1;
    }
}
