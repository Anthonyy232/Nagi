namespace Nagi.Core.Models;

public enum ShuffleMode
{
    Random,
    MoreVariety,
    Rediscover,
    Unheard
}

public sealed record MixCandidate(Guid Id, string Artist, long DurationTicks, int PlayCount, DateTime? LastPlayedDate);
