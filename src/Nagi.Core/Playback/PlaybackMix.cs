namespace Nagi.Core.Playback;

public enum ShuffleMode
{
    Random,
    MoreVariety,
    Rediscover,
    Unheard
}

/// <summary>
///     The ranges <see cref="IMusicPlaybackService.PlayMixAsync" /> accepts.
/// </summary>
public static class PlaybackMixLimits
{
    public const int MaxMinutes = 24 * 60;
    public const int MaxAvoidRecentDays = 365;
    public const int DefaultAvoidRecentDays = 1;
}

public sealed record MixCandidate(Guid Id, string Artist, long DurationTicks, int PlayCount, DateTime? LastPlayedDate);
