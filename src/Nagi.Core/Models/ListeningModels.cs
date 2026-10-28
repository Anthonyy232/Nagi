namespace Nagi.Core.Models;

public enum ShuffleMode
{
    Random,
    MoreVariety,
    Rediscover,
    Unheard
}

/// <summary>
///     The ranges <see cref="Services.Abstractions.IMusicPlaybackService.PlayMixAsync" /> accepts.
/// </summary>
public static class PlaybackMixLimits
{
    public const int MaxMinutes = 24 * 60;
    public const int MaxAvoidRecentDays = 365;
    public const int DefaultAvoidRecentDays = 1;
}

public enum PlaybackStopMode
{
    Off,
    AfterTrack,
    EndOfQueue,
    Timer
}

public sealed record MixCandidate(Guid Id, string Artist, long DurationTicks, int PlayCount, DateTime? LastPlayedDate);

public sealed record RecentListen(long Id, Guid SongId, string Title, string ArtistName, DateTime TimestampUtc);

public sealed record RediscoverySuggestion(Guid SongId, string Title, string ArtistName, DateTime LastPlayedDate);
