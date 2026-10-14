using Nagi.Core.Models;

namespace Nagi.Core.Helpers;

internal static class PlaybackMixBuilder
{
    public static List<Guid> Build(IEnumerable<MixCandidate> candidates, ShuffleMode mode, DateTime now,
        TimeSpan? duration = null, int avoidRecentDays = 1, Random? random = null)
    {
        random ??= Random.Shared;
        var eligible = candidates.Where(c => mode switch
        {
            ShuffleMode.Unheard => c.PlayCount == 0,
            ShuffleMode.Rediscover => c.PlayCount > 0 && c.LastPlayedDate < now.AddDays(-Math.Max(30, avoidRecentDays)),
            ShuffleMode.MoreVariety => c.LastPlayedDate is null || c.LastPlayedDate < now.AddDays(-avoidRecentDays),
            _ => true
        }).DistinctBy(c => c.Id).ToArray();
        random.Shuffle(eligible);

        IEnumerable<MixCandidate> ordered = eligible;
        if (mode != ShuffleMode.Random)
            ordered = SpaceArtists(eligible);

        var result = new List<Guid>();
        var remaining = duration?.Ticks;
        foreach (var candidate in ordered)
        {
            if (remaining.HasValue)
            {
                if (candidate.DurationTicks <= 0 || candidate.DurationTicks > remaining.Value) continue;
                remaining -= candidate.DurationTicks;
            }
            result.Add(candidate.Id);
        }
        return result;
    }

    private static IEnumerable<MixCandidate> SpaceArtists(IEnumerable<MixCandidate> candidates)
    {
        var groups = new PriorityQueue<Queue<MixCandidate>, int>();
        foreach (var group in candidates.GroupBy(c => c.Artist, StringComparer.OrdinalIgnoreCase))
        {
            var queue = new Queue<MixCandidate>(group);
            groups.Enqueue(queue, -queue.Count);
        }

        Queue<MixCandidate>? previous = null;
        while (groups.TryDequeue(out var next, out _))
        {
            yield return next.Dequeue();
            if (previous is { Count: > 0 }) groups.Enqueue(previous, -previous.Count);
            previous = next;
        }
        while (previous is { Count: > 0 }) yield return previous.Dequeue();
    }
}
