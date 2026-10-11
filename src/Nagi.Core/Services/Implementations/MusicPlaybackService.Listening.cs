using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

public partial class MusicPlaybackService
{
    private readonly TimeProvider _timeProvider;

    public ShuffleMode CurrentShuffleMode { get; private set; }

    public async Task<bool> PlayMixAsync(IEnumerable<Guid> songIds, ShuffleMode mode, PlaybackContext? context = null, int avoidRecentDays = 1)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
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
                var candidates = (await _libraryService.GetMixCandidatesAsync(ids)).ToList();
                candidates.AddRange(transient);
                return PlaybackMixBuilder.Build(candidates, mode, now, avoidRecentDays: avoidRecentDays);
            });
            if (!IsCurrentRequest(request)) throw new OperationCanceledException();
            if (order.Count == 0) return;
            await PlayInternalAsync(order, 0, true, context ?? _currentContext, request, order, mode);
            if (!IsCurrentRequest(request)) throw new OperationCanceledException();
            played = true;
        });
        return played;
    }
}
