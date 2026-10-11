namespace Nagi.Core.Models;

public enum ShuffleMode
{
    Random,
    MoreVariety,
    Rediscover,
    Unheard
}

public enum PlaybackStopMode
{
    Off,
    AfterTrack,
    EndOfQueue,
    Timer
}

public sealed record MixCandidate(Guid Id, string Artist, long DurationTicks, int PlayCount, DateTime? LastPlayedDate);
