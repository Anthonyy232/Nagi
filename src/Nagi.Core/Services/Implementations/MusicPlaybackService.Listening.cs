using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

public partial class MusicPlaybackService
{
    private readonly TimeProvider _timeProvider;
    private bool _finishCurrentTrack = true;

    public ShuffleMode CurrentShuffleMode { get; private set; }
    public PlaybackStopMode StopMode { get; private set; }
    public DateTimeOffset? StopDeadline { get; private set; }
    public event Action? StopScheduleChanged;

    public async Task<bool> PlayMixAsync(IEnumerable<Guid> songIds, ShuffleMode mode, TimeSpan? duration = null,
        PlaybackContext? context = null, int avoidRecentDays = 1)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (duration is { } limit && (limit <= TimeSpan.Zero || limit > TimeSpan.FromDays(1)))
            throw new ArgumentOutOfRangeException(nameof(duration));
        if (avoidRecentDays is < 0 or > 365) throw new ArgumentOutOfRangeException(nameof(avoidRecentDays));

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
            if (!IsCurrentRequest(request)) throw new OperationCanceledException();
            if (order.Count == 0) return;
            await PlayInternalAsync(order, 0, true, context ?? _currentContext, request, order, mode);
            if (!IsCurrentRequest(request)) throw new OperationCanceledException();
            if (duration.HasValue)
            {
                await SetRepeatModeAsync(RepeatMode.Off);
                if (!IsCurrentRequest(request)) throw new OperationCanceledException();
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
        if (mode == PlaybackStopMode.Timer && (delay is null || delay <= TimeSpan.Zero || delay > TimeSpan.FromDays(1)))
            throw new ArgumentOutOfRangeException(nameof(delay));
        StopMode = mode;
        StopDeadline = mode == PlaybackStopMode.Timer ? _timeProvider.GetUtcNow() + delay!.Value : null;
        _finishCurrentTrack = finishCurrentTrack;
        StopScheduleChanged?.Invoke();
    }

    public Task CheckStopScheduleAsync()
    {
        if (_isDisposed || StopMode != PlaybackStopMode.Timer || _timeProvider.GetUtcNow() < StopDeadline)
            return Task.CompletedTask;
        if (_finishCurrentTrack && CurrentTrack is not null && IsPlaying)
        {
            ScheduleStop(PlaybackStopMode.AfterTrack);
            return Task.CompletedTask;
        }
        return StopAsync();
    }

    private bool ShouldStopAfterTrack()
    {
        if (StopMode == PlaybackStopMode.AfterTrack) return true;
        var count = IsShuffleEnabled ? _queue.Shuffled.Count : _queue.Order.Count;
        var index = IsShuffleEnabled ? CurrentShuffledIndex : CurrentQueueIndex;
        return StopMode == PlaybackStopMode.EndOfQueue && index == count - 1;
    }
}
