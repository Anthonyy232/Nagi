namespace Nagi.Core.Playback;

public enum ShuffleMode
{
    Random,
    MoreVariety,
    Rediscover,
    Unheard
}

/// <summary>
///     The ranges <see cref="IMusicPlaybackService.PlayMixAsync" /> and <see cref="IMusicPlaybackService.ScheduleStop" />
///     accept.
/// </summary>
public static class PlaybackLimits
{
    /// <summary>The longest a timed mix or a sleep timer can run.</summary>
    public const int MaxScheduledMinutes = 24 * 60;
    public const int MaxAvoidRecentDays = 365;
    public const int DefaultAvoidRecentDays = 1;
}

public sealed record MixCandidate(Guid Id, string Artist, long DurationTicks, int PlayCount, DateTime? LastPlayedDate);
