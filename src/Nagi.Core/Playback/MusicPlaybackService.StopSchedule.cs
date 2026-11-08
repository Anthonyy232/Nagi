namespace Nagi.Core.Playback;

public partial class MusicPlaybackService
{
    private bool _finishCurrentTrack = true;
    private DateTimeOffset? _stopDeadline;
    private CancellationTokenSource? _stopTimerCts;

    public PlaybackStopMode StopMode { get; private set; }

    public TimeSpan? StopTimeRemaining => _stopDeadline is { } deadline
        ? TimeSpan.FromTicks(Math.Max(0, (deadline - _timeProvider.GetUtcNow()).Ticks))
        : null;

    public event Action? StopScheduleChanged;

    public void ScheduleStop(PlaybackStopMode mode, TimeSpan? delay = null, bool finishCurrentTrack = true)
    {
        if (_isDisposed) return;
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode == PlaybackStopMode.Timer && (delay is null || delay <= TimeSpan.Zero ||
                                               delay > TimeSpan.FromMinutes(PlaybackLimits.MaxScheduledMinutes)))
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
}
